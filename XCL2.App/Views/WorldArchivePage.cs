using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Views;

public sealed class WorldArchivePage : UserControl
{
    private readonly ListBox _worlds = new() { DisplayMemberPath = "Settings.Name", MinWidth = 180 };
    private readonly TextBox _name = new();
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "生存", "创造", "冒险", "旁观" } };
    private readonly ComboBox _difficulty = new() { ItemsSource = new[] { "和平", "简单", "普通", "困难" } };
    private readonly CheckBox _cheats = new() { Content = "允许作弊" }, _hardcore = new() { Content = "极限模式" };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap }, _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _nbt = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Image _projection = new() { Stretch = Stretch.Uniform, MinHeight = 220 };
    private readonly TextBlock _materials = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<string, TextBox> _extraFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _extraFlags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _initialFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _initialFlags = new(StringComparer.Ordinal);
    private readonly TextBox _rules = new() { AcceptsReturn = true, MinHeight = 140, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
    private readonly StackPanel _playerFields = new();
    private WorldArchive? _selected;
    public WorldArchivePage()
    {
        var page = new Grid { Margin = new Thickness(24) };
        page.RowDefinitions.Add(new() { Height = GridLength.Auto }); page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "存档", FontSize = 28, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock { Text = "管理世界 · 生成副本 · 查看投影与结构", Opacity = .7, Margin = new Thickness(0, 4, 0, 12) });
        var toolbar = new WrapPanel();
        Add(toolbar, "刷新", async () => await RefreshAsync());
        Add(toolbar, "打开存档目录", async () => { var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择含 level.dat 的 Java 版存档目录" }; if (picker.ShowDialog() == true) await RunAsync(() => Task.Run(() => Select(WorldArchiveService.Read(picker.FolderName)))); });
        Add(toolbar, "查看投影 / 结构文件", OpenStructureAsync); header.Children.Add(toolbar); page.Children.Add(header);
        var body = new Grid { Margin = new Thickness(0, 12, 0, 12) }; body.ColumnDefinitions.Add(new() { Width = new GridLength(220) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); Grid.SetRow(body, 1); page.Children.Add(body);
        body.Children.Add(_worlds); _worlds.SelectionChanged += (_, _) => { if (_worlds.SelectedItem is WorldArchive world) { try { Select(world); } catch (Exception ex) { _status.Text = ex.Message; } } };
        var tabs = new TabControl { Margin = new Thickness(16, 0, 0, 0) }; Grid.SetColumn(tabs, 1); body.Children.Add(tabs);
        var settings = new StackPanel { Margin = new Thickness(16) };
        settings.Children.Add(_info); Label(settings, "存档名称", _name); Label(settings, "游戏模式", _mode); Label(settings, "难度", _difficulty);
        settings.Children.Add(_cheats); settings.Children.Add(_hardcore);
        var advanced = new StackPanel();
        foreach (var field in WorldArchiveService.EditableFields.Where(f => f.Key is not "name" and not "gameMode" and not "difficulty" and not "allowCommands" and not "hardcore" and not "gameRules"))
        {
            var panel = field.Key.StartsWith("player.", StringComparison.Ordinal) ? _playerFields : advanced;
            if (field.Type == "bool")
            {
                var check = new CheckBox { Content = field.Label, IsThreeState = true, Margin = new Thickness(0, 8, 0, 4) };
                _extraFlags[field.Key] = check; panel.Children.Add(check);
            }
            else
            {
                var box = new TextBox { ToolTip = field.Note ?? $"可选；范围 {field.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "不限"}–{field.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "不限"}" };
                _extraFields[field.Key] = box; Label(panel, field.Label, box);
                if (field.Note != null) panel.Children.Add(new TextBlock { Text = field.Note, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            }
        }
        settings.Children.Add(new Expander { Header = "时间、天气、种子、出生点与世界边界", Content = advanced, Margin = new Thickness(0, 16, 0, 8) });
        var gameRules = new StackPanel();
        gameRules.Children.Add(new TextBlock { Text = "每行填写 规则名称=true/false 或非负整数，名称区分大小写。可加入当前版本支持的新规则；未列出或删除的行保留原值。", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 4, 0, 8) });
        gameRules.Children.Add(_rules);
        settings.Children.Add(new Expander { Header = "游戏规则（例如 keepInventory、doDaylightCycle、randomTickSpeed）", Content = gameRules, Margin = new Thickness(0, 8, 0, 8) });
        var playerPanel = new StackPanel(); playerPanel.Children.Add(new TextBlock { Text = "编辑 level.dat 中的单人玩家生命、饥饿、经验、坐标和模式。不修改多人 playerdata；移动坐标前请确认目标安全。", TextWrapping = TextWrapping.Wrap, Opacity = .7 }); playerPanel.Children.Add(_playerFields);
        settings.Children.Add(new Expander { Header = "单人玩家数据", Content = playerPanel, Margin = new Thickness(0, 8, 0, 8) });
        settings.Children.Add(new TextBlock { Text = "留空或三态勾选的中间状态表示保留原值。保存前请退出游戏。修改 level.dat 会自动备份；副本保留已有区块。修改种子只影响之后生成的区块，可能产生地形接缝。", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 12, 0, 12) });
        Add(_actions, "保存设置", SaveAsync); Add(_actions, "修改并生成副本", CopyAsync); settings.Children.Add(_actions);
        tabs.Items.Add(new TabItem { Header = "世界设置", Content = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var preview = new StackPanel { Margin = new Thickness(12) }; preview.Children.Add(new TextBlock { Text = "俯视投影 · 颜色区分方块类型（非游戏材质）", Margin = new Thickness(0, 0, 0, 8) }); preview.Children.Add(_projection); preview.Children.Add(_materials);
        tabs.Items.Add(new TabItem { Header = "投影 / 材料", Content = new ScrollViewer { Content = preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "NBT 结构", Content = _nbt });
        Grid.SetRow(_status, 2); page.Children.Add(_status); Content = page;
        Loaded += async (_, _) => await RefreshAsync();
    }
    private static void Label(Panel panel, string label, UIElement control)
    { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4) }); panel.Children.Add(control); }
    private static void Add(Panel panel, string label, Func<Task> action)
    { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 7, 12, 7) }; button.Click += async (_, _) => await action(); panel.Children.Add(button); }
    private void Select(WorldArchive world)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Select(world)); return; }
        _selected = world; _name.Text = world.Settings.Name; _mode.SelectedIndex = world.Settings.GameMode;
        _difficulty.SelectedIndex = world.Settings.Difficulty; _cheats.IsChecked = world.Settings.AllowCommands; _hardcore.IsChecked = world.Settings.Hardcore;
        var snapshot = JsonSerializer.SerializeToNode(world.Settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        JsonNode? Field(string key) { var parts = key.Split('.'); return parts.Length == 1 ? snapshot[parts[0]] : snapshot[parts[0]]?[parts[1]]; }
        _initialFields.Clear(); _initialFlags.Clear();
        foreach (var pair in _extraFields) { pair.Value.Text = Field(pair.Key)?.ToString() ?? ""; _initialFields[pair.Key] = pair.Value.Text; }
        foreach (var pair in _extraFlags) { pair.Value.IsChecked = Field(pair.Key)?.GetValue<bool>(); _initialFlags[pair.Key] = pair.Value.IsChecked; }
        _rules.Text = string.Join("\n", world.Settings.GameRules.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        _playerFields.IsEnabled = world.Settings.Player != null;
        _info.Text = $"{world.Settings.Version} · DataVersion {world.Settings.DataVersion}\n种子：{world.Settings.Seed?.ToString() ?? "未知"}\n{world.Path}";
        _nbt.Text = NbtDocument.Load(Path.Combine(world.Path, "level.dat")).Describe();
    }
    private WorldEdit Edit()
    {
        var obj = new JsonObject { ["name"] = _name.Text.Trim(), ["gameMode"] = _mode.SelectedIndex, ["difficulty"] = _difficulty.SelectedIndex, ["allowCommands"] = _cheats.IsChecked == true, ["hardcore"] = _hardcore.IsChecked == true };
        void Put(string key, JsonNode? value)
        {
            var parts = key.Split('.');
            if (parts.Length == 1) obj[key] = value;
            else { if (obj[parts[0]] == null) obj[parts[0]] = new JsonObject(); obj[parts[0]]![parts[1]] = value; }
        }
        foreach (var field in WorldArchiveService.EditableFields)
        {
            if (_extraFields.TryGetValue(field.Key, out var box) && !string.IsNullOrWhiteSpace(box.Text) && box.Text.Trim() != _initialFields.GetValueOrDefault(field.Key))
            {
                var text = box.Text.Trim(); JsonNode value;
                try
                {
                    value = field.Type switch
                    {
                        "long" => JsonValue.Create(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture))!,
                        "int" => JsonValue.Create(int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture))!,
                        "float" => JsonValue.Create(float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))!,
                        _ => JsonValue.Create(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))!
                    };
                }
                catch (Exception ex) when (ex is FormatException or OverflowException) { throw new ArgumentException(field.Label + "：请输入有效数字。", ex); }
                Put(field.Key, value);
            }
            if (_extraFlags.TryGetValue(field.Key, out var flag) && flag.IsChecked.HasValue && flag.IsChecked != _initialFlags.GetValueOrDefault(field.Key)) Put(field.Key, JsonValue.Create(flag.IsChecked.Value));
        }
        var rules = new JsonObject(); var ruleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in _rules.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = line.Trim(); if (entry.Length == 0) continue;
            var index = entry.IndexOf('='); if (index <= 0) throw new ArgumentException("游戏规则应为 名称=值，每行一条。");
            var key = entry[..index].Trim(); var value = entry[(index + 1)..].Trim();
            if (!ruleNames.Add(key)) throw new ArgumentException("游戏规则重复：" + key);
            if (_selected?.Settings.GameRules.GetValueOrDefault(key) != value) rules[key] = value;
        }
        if (rules.Count > 0) obj["gameRules"] = rules;
        var edit = obj.Deserialize<WorldEdit>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        WorldArchiveService.Validate(edit); return edit;
    }
    private async Task RefreshAsync()
    {
        var roots = WorldCatalog.GameRoots(); var warnings = new List<string>();
        await RunAsync(async () => { _worlds.ItemsSource = await Task.Run(() => WorldArchiveService.Discover(roots, warnings.Add)); _status.Text = warnings.Count == 0 ? "请选择存档；也可打开其他目录。" : string.Join("\n", warnings.Take(5)); });
    }
    private async Task SaveAsync()
    {
        if (_selected == null) { _status.Text = "请先选择存档。"; return; }
        var world = _selected; WorldEdit edit;
        try { edit = Edit(); } catch (Exception ex) { _status.Text = ex.Message; return; }
        if (!MessageBoxDialog.ShowConfirm($"将修改 {world.Path}\\level.dat，自动保留修改前备份。\n{WorldArchiveService.DescribeEdit(edit)}\n请确认游戏已经退出。", "保存存档设置")) return;
        await RunAsync(async () => { var backup = await Task.Run(() => WorldArchiveService.Update(world.Path, edit)); Select(WorldArchiveService.Read(world.Path)); _status.Text = "设置已保存。备份：" + backup; });
    }
    private async Task CopyAsync()
    {
        if (_selected == null) { _status.Text = "请先选择作为模板的存档。"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "输入新存档目录名称（不覆盖已有目录）", FileName = Path.GetFileName(_selected.Path) + "-copy", InitialDirectory = Path.GetDirectoryName(_selected.Path), CheckPathExists = true, AddExtension = false };
        if (dialog.ShowDialog() != true) return;
        var source = _selected.Path; WorldEdit edit;
        try { edit = Edit(); } catch (Exception ex) { _status.Text = ex.Message; return; }
        if (!MessageBoxDialog.ShowConfirm($"从 {source}\n生成到 {dialog.FileName}\n保留已有区块，应用以下设置：\n{WorldArchiveService.DescribeEdit(edit)}\n请退出游戏后继续。", "生成存档副本")) return;
        await RunAsync(async () => { await Task.Run(() => WorldArchiveService.GenerateCopy(source, dialog.FileName, edit)); Select(WorldArchiveService.Read(dialog.FileName)); _status.Text = "已生成：" + dialog.FileName; });
    }
    private async Task OpenStructureAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "投影 / 结构|*.litematic;*.nbt;*.schematic;*.schem", Title = "选择投影或结构文件" };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(async () =>
        {
            var data = await Task.Run(() => StructureInspector.Read(dialog.FileName)); _nbt.Text = data.NbtText;
            _projection.Source = RenderProjection(data);
            _materials.Text = string.Join("\n", data.Materials.OrderByDescending(p => p.Value).Take(200).Select(p => $"{p.Key}  × {p.Value:N0}"));
            _status.Text = $"{data.Format} · {data.Width} × {data.Height} × {data.Depth} · {data.Blocks:N0} 个非空气方块。请切换到投影 / 材料或 NBT 结构标签查看。";
        });
    }
    private static ImageSource RenderProjection(StructurePreview preview)
    {
        const int limit = 720; double scale = Math.Min(1, limit / (double)Math.Max(preview.Width, preview.Depth));
        int width = Math.Max(1, (int)Math.Ceiling(preview.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(preview.Depth * scale));
        var pixels = new byte[width * height * 4]; var levels = Enumerable.Repeat(int.MinValue, width * height).ToArray();
        foreach (var cell in preview.TopView)
        {
            int x = Math.Min(width - 1, (int)(cell.X * scale)), z = Math.Min(height - 1, (int)(cell.Z * scale)); int index = z * width + x;
            if (cell.Y < levels[index]) continue; levels[index] = cell.Y; var color = StructureInspector.Color(cell.Block);
            pixels[index * 4] = color.B; pixels[index * 4 + 1] = color.G; pixels[index * 4 + 2] = color.R; pixels[index * 4 + 3] = 255;
        }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze(); return bitmap;
    }
    private async Task RunAsync(Func<Task> action)
    {
        _actions.IsEnabled = false; _status.Text = "正在处理…";
        try { await action(); } catch (Exception ex) { _status.Text = ex.Message; }
        finally { _actions.IsEnabled = true; }
    }
}
