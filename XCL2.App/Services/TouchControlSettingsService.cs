using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>独立保存，避免启动器设置页的旧编辑副本覆盖游戏内刚保存的按键。</summary>
public static class TouchControlSettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2", "touch-controls.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public static event Action? Changed;

    public static List<TouchControlDefinition> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var controls = JsonSerializer.Deserialize<List<TouchControlDefinition>>(File.ReadAllText(FilePath), JsonOptions);
                if (controls != null && Validate(controls) == null) return controls;
                LauncherLogService.AppendLine("[触屏设置] 按键配置无效，本次使用默认布局，原文件未覆盖。");
            }
        }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail($"[读取触屏按键配置]\n{ex}"); }
        return CreateDefaults();
    }

    public static void Save(IEnumerable<TouchControlDefinition> controls)
    {
        var list = controls.Select(c => c.Copy()).ToList();
        var error = Validate(list);
        if (error != null) throw new ArgumentException(error);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(list, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Changed?.Invoke();
    }

    public static string? Validate(IReadOnlyList<TouchControlDefinition> controls)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in controls)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.Id) || !ids.Add(c.Id)) return "按键 ID 不能为空或重复。";
            if (string.IsNullOrWhiteSpace(c.Label)) return "请填写按键名称。";
            if (!Enum.IsDefined(c.Mode) || !Enum.IsDefined(c.Visibility) || !Enum.IsDefined(c.Anchor) || !Enum.IsDefined(c.MenuAction))
                return $"{c.Label}：按键模式或锚点无效。";
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || !double.IsFinite(c.Width) || !double.IsFinite(c.Height) ||
                Math.Abs(c.X) > 100000 || Math.Abs(c.Y) > 100000 || c.Width < 20 || c.Height < 20 || c.Width > 2000 || c.Height > 2000)
                return $"{c.Label}：坐标必须是有限数字，宽高应在 20–2000 之间。";
            if (!TouchInputInjector.IsValidBinding(c.Binding, c.Mode == TouchControlMode.Command))
                return $"{c.Label}：无法识别按键 {c.Binding}。可用字母、数字、F1–F24、CTRL+字母、MOUSE_L/R/M、WHEEL_UP/DOWN。";
            if (c.Mode != TouchControlMode.Tap && (c.Binding.Equals("WHEEL_UP", StringComparison.OrdinalIgnoreCase) ||
                c.Binding.Equals("WHEEL_DOWN", StringComparison.OrdinalIgnoreCase))) return $"{c.Label}：滚轮请使用 Tap 模式。";
            if (c.MenuAction != TouchMenuAction.None && (c.Mode != TouchControlMode.Tap ||
                c.Binding.Split('+', StringSplitOptions.TrimEntries).Any(k => !TouchInputInjector.TryMapKey(k).HasValue)))
                return $"{c.Label}：菜单动作请使用点按模式及键盘绑定。";
        }
        return null;
    }

    public static List<TouchControlDefinition> CreateDefaults()
    {
        var list = new List<TouchControlDefinition>();
        void Add(string id, string label, string binding, TouchControlMode mode, TouchControlAnchor anchor,
            double x, double y, double w = 62, double h = 62,
            TouchControlVisibility visibility = TouchControlVisibility.Game, TouchMenuAction menu = TouchMenuAction.None)
            => list.Add(new() { Id = id, Label = label, Binding = binding, Mode = mode, Anchor = anchor,
                X = x, Y = y, Width = w, Height = h, Visibility = visibility, MenuAction = menu });
        var top = new[] { ("escape", "ESC", "ESC"), ("debug", "F3", "F3"), ("delete", "DEL", "DEL"),
            ("perspective", "F5", "F5"), ("fullscreen", "F11", "F11"), ("chat", "聊天", "T"),
            ("players", "列表", "TAB"), ("hud", "F1", "F1") };
        for (var i = 0; i < top.Length; i++)
            Add(top[i].Item1, top[i].Item2, top[i].Item3, i == 6 ? TouchControlMode.Hold : TouchControlMode.Tap,
                TouchControlAnchor.TopLeft, 13 + (i % 7) * 58, 11 + (i / 7) * 44, 52, 38, TouchControlVisibility.Always,
                i == 0 ? TouchMenuAction.Toggle : i == 5 ? TouchMenuAction.Open : TouchMenuAction.None);
        var commands = new[] { ("hide", "隐藏", "HIDE"), ("focus", "聚焦", "GRAB"), ("sensitivity-up", "灵敏+", "SENS+"),
            ("sensitivity-down", "灵敏−", "SENS-"), ("keyboard", "键盘", "KEYBOARD"), ("settings", "设置", "SETTINGS") };
        for (var i = 0; i < commands.Length; i++)
            Add(commands[i].Item1, commands[i].Item2, commands[i].Item3, TouchControlMode.Command,
                TouchControlAnchor.TopRight, 13 + i * 58, 11, 52, 38, TouchControlVisibility.Always);
        Add("forward", "W\n前", "W", TouchControlMode.Hold, TouchControlAnchor.BottomLeft, 98, 172);
        Add("left", "A\n左", "A", TouchControlMode.Hold, TouchControlAnchor.BottomLeft, 28, 102);
        Add("sprint", "疾跑", "CTRL", TouchControlMode.Toggle, TouchControlAnchor.BottomLeft, 98, 102);
        Add("right", "D\n右", "D", TouchControlMode.Hold, TouchControlAnchor.BottomLeft, 168, 102);
        Add("backward", "S\n后", "S", TouchControlMode.Hold, TouchControlAnchor.BottomLeft, 98, 32);
        Add("drop", "丢弃 Q", "Q", TouchControlMode.Tap, TouchControlAnchor.BottomRight, 186, 172);
        Add("inventory", "背包 E", "E", TouchControlMode.Tap, TouchControlAnchor.BottomRight, 116, 172,
            visibility: TouchControlVisibility.Game, menu: TouchMenuAction.Toggle);
        Add("offhand", "换手 F", "F", TouchControlMode.Tap, TouchControlAnchor.BottomRight, 46, 172);
        Add("attack", "攻击\n挖掘", "MOUSE_L", TouchControlMode.Hold, TouchControlAnchor.BottomRight, 186, 102);
        Add("use", "使用\n放置", "MOUSE_R", TouchControlMode.Hold, TouchControlAnchor.BottomRight, 116, 102);
        Add("jump", "跳跃\n上浮", "SPACE", TouchControlMode.Hold, TouchControlAnchor.BottomRight, 28, 58, 80, 80);
        Add("sneak", "潜行 / 下潜", "SHIFT", TouchControlMode.Toggle, TouchControlAnchor.BottomRight, 120, 32, 128);
        for (var i = 1; i <= 9; i++)
            Add("hotbar-" + i, i.ToString(), i.ToString(), TouchControlMode.Tap, TouchControlAnchor.BottomCenter,
                (i - 5) * 58, 13, 52, 38);
        return list;
    }
}
