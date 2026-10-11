using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XCL2.App.Services.Worlds;

namespace XCL2.WinUI.Preview;

/// <summary>与 WPF 和插件共用存档字段定义；只有勾选的高级字段才会写入。</summary>
internal sealed class WorldAdvancedEditor
{
    private readonly List<(WorldEditField Field, CheckBox Apply, Control Input)> _inputs = new();
    private readonly IReadOnlyDictionary<string, string> _originalRules;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public UIElement View { get; }

    public WorldAdvancedEditor(WorldSettings settings)
    {
        _originalRules = new Dictionary<string, string>(settings.GameRules, StringComparer.Ordinal);
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(new TextBlock { Text = "勾选需要修改的字段。未勾选时保留原值；种子仅影响新生成的区块。", TextWrapping = TextWrapping.Wrap });
        var world = new StackPanel { Spacing = 8 };
        var player = new StackPanel { Spacing = 8 };
        using var current = JsonDocument.Parse(JsonSerializer.Serialize(settings, JsonOptions));
        foreach (var field in WorldArchiveService.EditableFields.Skip(5))
        {
            bool isPlayer = field.Key.StartsWith("player.", StringComparison.Ordinal);
            var apply = new CheckBox { Content = "修改 " + field.Label, IsEnabled = !isPlayer || settings.Player != null };
            JsonElement value = current.RootElement;
            foreach (string key in field.Key.Split('.'))
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var child)) { value = default; break; }
                value = child;
            }
            Control input;
            if (field.Type == "bool") input = new CheckBox { Content = "开启", IsChecked = value.ValueKind == JsonValueKind.True };
            else
            {
                string text = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : value.ToString();
                if (field.Type == "dictionary") text = string.Join("\n", settings.GameRules.Select(p => p.Key + "=" + p.Value));
                input = new TextBox { Text = text, AcceptsReturn = field.Type == "dictionary", MinHeight = field.Type == "dictionary" ? 120 : 0, MaxHeight = field.Type == "dictionary" ? 220 : double.PositiveInfinity };
            }
            input.IsEnabled = false;
            apply.Checked += (_, _) => input.IsEnabled = true;
            apply.Unchecked += (_, _) => input.IsEnabled = false;
            var group = isPlayer ? player : world;
            group.Children.Add(apply); group.Children.Add(input);
            if (field.Note != null) group.Children.Add(new TextBlock { Text = field.Note, FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            _inputs.Add((field, apply, input));
        }
        if (settings.Player == null) player.Children.Insert(0, new TextBlock { Text = "此存档没有内嵌的单人玩家数据；不会修改多人 playerdata。", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new Expander { Header = "时间 / 天气 / 种子 / 出生点 / 边界 / 规则", Content = world, HorizontalAlignment = HorizontalAlignment.Stretch });
        root.Children.Add(new Expander { Header = "单人玩家属性", Content = player, HorizontalAlignment = HorizontalAlignment.Stretch });
        View = root;
    }

    public WorldEdit Apply(WorldEdit basics)
    {
        var result = new Dictionary<string, object?> { ["name"] = basics.Name, ["gameMode"] = basics.GameMode, ["difficulty"] = basics.Difficulty, ["allowCommands"] = basics.AllowCommands, ["hardcore"] = basics.Hardcore };
        var player = new Dictionary<string, object?>();
        foreach (var (field, apply, input) in _inputs.Where(item => item.Apply.IsChecked == true))
        {
            object value;
            if (input is CheckBox check) value = check.IsChecked == true;
            else
            {
                string text = ((TextBox)input).Text.Trim();
                try
                {
                    value = field.Type switch
                    {
                        "int" => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        "long" => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        "float" => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                        "double" => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                        "dictionary" => ParseRules(text),
                        _ => text
                    };
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                { throw new ArgumentException(field.Label + "：" + ex.Message); }
            }
            if (field.Key.StartsWith("player.", StringComparison.Ordinal)) player[field.Key[7..]] = value;
            else result[field.Key] = value;
        }
        if (player.Count > 0) result["player"] = player;
        var edit = JsonSerializer.Deserialize<WorldEdit>(JsonSerializer.Serialize(result), JsonOptions)!;
        WorldArchiveService.Validate(edit);
        return edit;
    }

    private IReadOnlyDictionary<string, string> ParseRules(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0) throw new FormatException("每行输入 规则名称=值。");
            if (!result.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim())) throw new FormatException("规则名称重复。");
        }
        return result.Where(pair => !_originalRules.TryGetValue(pair.Key, out var original) || original != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
