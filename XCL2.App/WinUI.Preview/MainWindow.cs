using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using XCL2.App.Services.Worlds;
using XCL2.Preview;

namespace XCL2.WinUI.Preview;

public sealed class MainWindow : Window
{
    private readonly PreviewClient _client;
    private readonly Grid _root = new() { RequestedTheme = ElementTheme.Dark };
    private readonly StackPanel _body = new() { Spacing = 20 };
    private readonly StackPanel _pluginCards = new() { Spacing = 12 };
    private readonly TextBlock _status = Text("正在连接启动器…", 12);
    private readonly TextBlock _brand = Text("XCL2", 24, true);
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly Grid _instances = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索你的实例…", MinWidth = 180, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _loaderFilter = new() { MinWidth = 130 };
    private readonly ComboBox _sort = new() { MinWidth = 130 };
    private readonly TextBlock _resultCount = Text("", 12);
    private string _query = "";
    private string _filter = "全部加载器";
    private bool _sortDescending;
    private readonly StackPanel _pinned = new() { Spacing = 5 };
    private readonly ColumnDefinition _sidebarColumn = new() { Width = new GridLength(232) };

    private PreviewSnapshot? _snapshot;
    private string _page = "home";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _refreshing;
    private SolidColorBrush _accent = Brush("#1BD96A");
    private string _uiSignature = "";
    public MainWindow(string? pipe)
    {
        _client = new PreviewClient(pipe); Title = "XCL2";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 790));
        _root.Background = Brush("#16181C");
        _root.ColumnDefinitions.Add(_sidebarColumn); _root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid { Background = Brush("#202328"), Padding = new Thickness(18, 20, 18, 16) };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition()); sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var wordmark = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8, 0, 0, 30) };
        var mark = Text("◉", 27, true); mark.Foreground = _accent; wordmark.Children.Add(mark); wordmark.Children.Add(_brand); sidebar.Children.Add(wordmark);
        var navigation = new StackPanel { Spacing = 5 };
        foreach (var (label, key, icon) in new[] { ("首页", "home", Symbol.Home), ("发现内容", "downloads", Symbol.Find), ("实例库", "instances", Symbol.Library), ("我的存档", "worlds", Symbol.Save), ("AI 助手", "ai", Symbol.Message), ("扩展插件", "plugins", Symbol.AllApps) })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            content.Children.Add(new SymbolIcon(icon) { Width = 20 }); content.Children.Add(Text(label, 15, true));
            var nav = Button("", () => ShowPageAsync(key)); nav.Content = content; nav.HorizontalAlignment = HorizontalAlignment.Stretch; nav.HorizontalContentAlignment = HorizontalAlignment.Left;
            nav.BorderThickness = new Thickness(0); _navigation[key] = nav; navigation.Children.Add(nav);
        }
        Grid.SetRow(navigation, 1); sidebar.Children.Add(navigation);
        var library = new StackPanel { Spacing = 10, Margin = new Thickness(8, 26, 0, 0) }; var labelInstalled = Text("已安装实例", 11, true); labelInstalled.Opacity = .5;
        library.Children.Add(labelInstalled); library.Children.Add(_pinned); Grid.SetRow(library, 2); sidebar.Children.Add(library);
        var footer = new StackPanel { Spacing = 8 }; footer.Children.Add(Button("⚙  设置", () => ShowPageAsync("settings")));
        var note = Text("XCL2 · 界面预览", 11); note.Opacity = .45; footer.Children.Add(note); Grid.SetRow(footer, 3); sidebar.Children.Add(footer); _root.Children.Add(sidebar);
        var main = new Grid(); main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition()); main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new Grid { Padding = new Thickness(30, 20, 30, 20), ColumnSpacing = 16 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.Children.Add(_search);
        var create = Button("＋ 创建实例", () => HostPageAsync("downloads"), true); Grid.SetColumn(create, 1); toolbar.Children.Add(create); main.Children.Add(toolbar);
        var scroll = new ScrollViewer { Content = _body, Padding = new Thickness(30, 8, 30, 26), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); main.Children.Add(scroll);
        _status.Margin = new Thickness(30, 8, 30, 12); _status.Opacity = .6; Grid.SetRow(_status, 2); main.Children.Add(_status);
        Grid.SetColumn(main, 1); _root.Children.Add(main); Content = _root;
        _search.TextChanged += async (_, _) => { _query = _search.Text.Trim(); if (_page != "instances") await ShowPageAsync("instances"); else RenderInstances(); };
        _instances.SizeChanged += (_, _) => LayoutInstanceCards();
        _root.SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 850;
            _sidebarColumn.Width = new GridLength(compact ? 78 : 232);
            sidebar.Padding = new Thickness(compact ? 10 : 18, 20, compact ? 10 : 18, 16);
            if (footer.Children[0] is Button settingsButton) settingsButton.Content = compact ? "⚙" : "⚙  设置";
            _brand.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            library.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            note.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var item in _navigation)
                if (item.Value.Content is StackPanel row && row.Children.Count > 1)
                    row.Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var item in _navigation) ToolTipService.SetToolTip(item.Value, item.Key switch { "home" => "首页", "instances" => "实例库", "worlds" => "存档", "ai" => "AI 助手", "plugins" => "插件", _ => "发现内容" });
            create.Content = e.NewSize.Width < 640 ? "＋" : "＋ 创建实例";
        };
        _root.Loaded += async (_, _) => { await RefreshAsync(); await ShowPageAsync("home"); _timer.Start(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _timer.Stop(); _client.Dispose(); };
    }
    private static SolidColorBrush Brush(string hex) => new(ColorHelper.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static Border Card(UIElement content) => new() { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(12), Background = Brush("#24282F"), BorderBrush = Brush("#343A43"), BorderThickness = new Thickness(1) };
    private Button Button(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 10, 16, 10), CornerRadius = new CornerRadius(8) };
        if (primary) { button.Background = _accent; button.Foreground = Brush("#092514"); }
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { _status.Text = ex.Message; } finally { button.IsEnabled = true; } }; return button;
    }
    private void Heading(string title, string subtitle)
    { var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(Text(title, 30, true)); var sub = Text(subtitle); sub.Opacity = .65; panel.Children.Add(sub); _body.Children.Add(panel); }
    private async Task RefreshAsync()
    {
        if (_refreshing) return; _refreshing = true;
        try
        {
            var previousInstances = System.Text.Json.JsonSerializer.Serialize(_snapshot?.Instances);
            _snapshot = await _client.SendAsync<PreviewSnapshot>(new("state"));
            _brand.Text = _snapshot.Appearance.Title; _accent = Brush(_snapshot.Appearance.Accent); _body.Spacing = _snapshot.Appearance.Compact ? 12 : 20;
            if (_status.Text == "正在连接启动器…") _status.Text = $"已连接 · {_snapshot.Instances.Count} 个实例 · 插件 API 2.3";
            string signature = System.Text.Json.JsonSerializer.Serialize(new { _snapshot.Cards, _snapshot.Appearance });
            if (signature != _uiSignature) { _uiSignature = signature; RenderPluginCards(); }
            if (previousInstances != System.Text.Json.JsonSerializer.Serialize(_snapshot.Instances))
            {
                _pinned.Children.Clear();
                foreach (var instance in _snapshot.Instances.Where(i => i.Installed).OrderByDescending(i => i.Selected).Take(5))
                {
                    var item = Button("▣  " + instance.Name, async () => { _search.Text = instance.Name; await ShowPageAsync("instances"); });
                    item.HorizontalAlignment = HorizontalAlignment.Stretch; item.HorizontalContentAlignment = HorizontalAlignment.Left;
                    _pinned.Children.Add(item);
                }
                if (_page == "instances") RenderInstances();
            }
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _refreshing = false; }
    }
    private async Task ShowPageAsync(string page)
    {
        if (_loaderFilter.Parent is Panel oldFilters) oldFilters.Children.Clear();
        _page = page; _body.Children.Clear();
        foreach (var item in _navigation) { item.Value.Background = Brush(item.Key == page ? "#323B38" : "#202328"); item.Value.Foreground = item.Key == page ? _accent : Brush("#CFD4DE"); }
        switch (page)
        {
            case "home":
                Heading("开始你的下一个世界", "你的实例、存档与创作，集中在这里。");
                var hero = new StackPanel { Spacing = 16 }; hero.Children.Add(Text("READY TO PLAY", 11)); hero.Children.Add(Text(_snapshot?.Instances.FirstOrDefault(i => i.Selected)?.Name ?? "欢迎回来", 25, true)); hero.Children.Add(Text("选择实例，继续冒险。用存档工具管理世界和建筑投影。"));
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; actions.Children.Add(Button("打开实例库 →", () => ShowPageAsync("instances"), true)); actions.Children.Add(Button("管理存档", () => ShowPageAsync("worlds"))); hero.Children.Add(actions); _body.Children.Add(Card(hero));
                var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }; links.Children.Add(Button("↓  下载 Mod 与整合包", () => HostPageAsync("downloads"))); links.Children.Add(Button("✦  打开 AI 助手", () => HostPageAsync("ai"))); _body.Children.Add(links);
                _body.Children.Add(Text("插件工作台", 19, true)); _body.Children.Add(_pluginCards); RenderPluginCards(); break;
            case "instances":
                Heading("你的实例库", "每一个世界，都从这里开始。");
                var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                _loaderFilter.SelectionChanged -= FilterChanged; _sort.SelectionChanged -= SortChanged;
                _loaderFilter.Items.Clear(); _loaderFilter.Items.Add("全部加载器");
                foreach (var loader in (_snapshot?.Instances ?? Array.Empty<PreviewInstance>()).Select(i => i.Loader).Distinct().OrderBy(x => x)) _loaderFilter.Items.Add(loader);
                _loaderFilter.SelectedItem = _loaderFilter.Items.Contains(_filter) ? _filter : "全部加载器";
                _loaderFilter.SelectionChanged -= FilterChanged; _loaderFilter.SelectionChanged += FilterChanged;
                _sort.Items.Clear(); _sort.Items.Add("名称 A → Z"); _sort.Items.Add("名称 Z → A"); _sort.SelectedIndex = _sortDescending ? 1 : 0;
                _sort.SelectionChanged -= SortChanged; _sort.SelectionChanged += SortChanged;
                filters.Children.Add(_loaderFilter); filters.Children.Add(_sort); filters.Children.Add(Button("刷新", async () => { await RefreshAsync(); RenderInstances(); }));
                _body.Children.Add(filters); _body.Children.Add(_resultCount); _body.Children.Add(_instances); RenderInstances(); break;
            case "worlds": await ShowWorldsAsync(); break;
            case "settings": ShowSettings(); break;
            default:
                Heading(page == "ai" ? "AI 助手" : page == "plugins" ? "插件管理" : "下载中心", "通过启动器打开完整工具。");
                _body.Children.Add(Card(Text("当前预览已原生实现实例库、存档、投影查看与 API 设置。此功能会打开启动器中的对应页面。")));
                _body.Children.Add(Button("打开 " + (page == "ai" ? "AI 助手" : page == "plugins" ? "插件管理" : "下载中心"), () => HostPageAsync(page), true)); break;
        }
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { _filter = _loaderFilter.SelectedItem as string ?? "全部加载器"; RenderInstances(); }
    private void SortChanged(object sender, SelectionChangedEventArgs e) { _sortDescending = _sort.SelectedIndex == 1; RenderInstances(); }
    private void RenderInstances()
    {
        _instances.Children.Clear();
        IEnumerable<PreviewInstance> items = _snapshot?.Instances ?? Array.Empty<PreviewInstance>();
        var matching = items.Where(i => (_query.Length == 0 || (i.Name + " " + i.Version + " " + i.Loader).Contains(_query, StringComparison.OrdinalIgnoreCase)) && (_filter == "全部加载器" || i.Loader == _filter));
        matching = _sortDescending ? matching.OrderByDescending(i => i.Name) : matching.OrderBy(i => i.Name);
        var visible = matching.ToArray(); _resultCount.Text = $"{visible.Length} 个实例";
        foreach (var instance in visible)
        {
            var panel = new StackPanel { Spacing = 10 };
            var tile = new Grid { Height = 108, CornerRadius = new CornerRadius(8), Background = Brush(instance.Selected ? "#214B37" : "#313843") };
            var glyph = Text("▧", 56); glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center; glyph.Foreground = instance.Selected ? _accent : Brush("#9BAABB"); tile.Children.Add(glyph); panel.Children.Add(tile);
            var name = Text(instance.Name, 17, true); name.MaxLines = 1; name.TextTrimming = TextTrimming.CharacterEllipsis; panel.Children.Add(name);
            var meta = Text($"{instance.Version}  ·  {instance.Loader}", 12); meta.Opacity = .65; panel.Children.Add(meta);
            var launch = Button(instance.Installed ? "▶  游玩" : "继续安装", async () =>
            {
                if (!instance.Installed) { await HostPageAsync("downloads"); return; }
                _status.Text = await _client.SendAsync<string>(new("launch", Root: instance.Root, Version: instance.Name));
            }, instance.Installed);
            launch.HorizontalAlignment = HorizontalAlignment.Stretch; panel.Children.Add(launch); _instances.Children.Add(Card(panel));
        }
        if (visible.Length == 0) _instances.Children.Add(Card(Text(_query.Length == 0 ? "还没有实例，点击右上角创建实例。" : "没有匹配的实例，试试其他关键词。")));
        LayoutInstanceCards();
    }
    private void LayoutInstanceCards()
    {
        int columns = Math.Clamp((int)(Math.Max(248, _instances.ActualWidth) / 248), 1, 6);
        int rows = (int)Math.Ceiling((double)_instances.Children.Count / columns);
        if (_instances.ColumnDefinitions.Count != columns || _instances.RowDefinitions.Count != rows)
        {
            _instances.ColumnDefinitions.Clear(); _instances.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) _instances.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < rows; i++) _instances.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (int i = 0; i < _instances.Children.Count; i++)
        {
            if (_instances.Children[i] is FrameworkElement card)
            {
                Grid.SetRow(card, i / columns);
                Grid.SetColumn(card, i % columns);
            }
        }
    }
    private void RenderPluginCards()
    {
        _pluginCards.Children.Clear();
        if (_snapshot?.Cards.Count is null or 0) { _pluginCards.Children.Add(Card(Text("授权插件后，它们可以在这里添加卡片、更新内容和调整预览外观。"))); return; }
        foreach (var card in _snapshot.Cards)
        {
            var panel = new StackPanel { Spacing = 8 }; var title = Text(card.Title, 18, true); title.Foreground = Brush(card.Accent); panel.Children.Add(title); panel.Children.Add(Text(card.Description));
            var source = Text("来自插件 · " + card.Plugin, 11); source.Opacity = .5; panel.Children.Add(source); panel.Children.Add(Button("打开 →", () => ShowPageAsync(card.Destination))); _pluginCards.Children.Add(Card(panel));
        }
    }
    private async Task HostPageAsync(string page) => _status.Text = await _client.SendAsync<string>(new("navigate", page));
    private void ShowSettings()
    {
        Heading("设置与扩展 API", "这些权限开关由你控制。插件还需要独立的声明与授权。");
        var panel = new StackPanel { Spacing = 16 };
        var preview = new ToggleSwitch { Header = "允许插件修改 WinUI 3 预览", IsOn = _snapshot?.PreviewApiEnabled == true };
        var worlds = new ToggleSwitch { Header = "允许存档 API", IsOn = _snapshot?.WorldsApiEnabled == true };
        panel.Children.Add(preview); panel.Children.Add(Text("ui.preview：注册 / 更新卡片、标题、强调色和布局密度，仅作用于新预览。", 12)); panel.Children.Add(worlds);
        panel.Children.Add(Text("worlds.read / worlds.write：读取已配置游戏目录的存档，修改前仍需你确认并自动备份。", 12));
        panel.Children.Add(Button("保存 API 设置", async () => { _status.Text = await _client.SendAsync<string>(new("api-settings", PreviewApiEnabled: preview.IsOn, WorldsApiEnabled: worlds.IsOn)); await RefreshAsync(); }, true));
        panel.Children.Add(Button("打开 API 页面 / 接口目录 / 示例", () => HostPageAsync("api")));
        panel.Children.Add(Button("AI 接口 / 模型 / 流式设置", () => HostPageAsync("ai"))); _body.Children.Add(Card(panel));
    }
    private async Task ShowWorldsAsync()
    {
        Heading("存档", "查看当前设置 · 修改并生成副本 · 预览投影和结构文件");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(Button("打开其他存档", async () => { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializePicker(picker); var folder = await picker.PickSingleFolderAsync(); if (folder != null) await EditWorldAsync(await Task.Run(() => WorldArchiveService.Read(folder.Path))); }, true));
        actions.Children.Add(Button("查看投影 / 结构", OpenStructureAsync)); _body.Children.Add(actions);
        var roots = (_snapshot?.Instances ?? Array.Empty<PreviewInstance>()).SelectMany(i => new[] { i.Root, Path.Combine(i.Root, "versions", i.Name) }).Concat(_snapshot?.GameRoots ?? Array.Empty<string>()).Distinct().ToArray();
        var warnings = new List<string>(); var worlds = await Task.Run(() => WorldArchiveService.Discover(roots, warnings.Add));
        if (_page != "worlds") return;
        foreach (var world in worlds)
        {
            var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(Text(world.Settings.Name, 20, true)); panel.Children.Add(Text($"{world.Settings.Version} · 种子 {world.Settings.Seed} · {world.ModifiedUtc.ToLocalTime():g}"));
            panel.Children.Add(Button("查看和编辑", () => EditWorldAsync(world))); _body.Children.Add(Card(panel));
        }
        if (worlds.Count == 0) _body.Children.Add(Card(Text("当前目录未发现 Java 版存档。也可以打开其他存档目录。")));
        if (warnings.Count > 0) _body.Children.Add(Text(string.Join("\n", warnings.Take(4)), 12));
    }
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    private async Task EditWorldAsync(WorldArchive world)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Text($"{world.Settings.Version} · 种子 {world.Settings.Seed}\n{world.Path}", 12));
        var name = new TextBox { Header = "存档名称", Text = world.Settings.Name, MaxLength = 128 };
        var mode = new ComboBox { Header = "游戏模式", ItemsSource = new[] { "生存", "创造", "冒险", "旁观" }, SelectedIndex = world.Settings.GameMode, HorizontalAlignment = HorizontalAlignment.Stretch };
        var difficulty = new ComboBox { Header = "难度", ItemsSource = new[] { "和平", "简单", "普通", "困难" }, SelectedIndex = world.Settings.Difficulty, HorizontalAlignment = HorizontalAlignment.Stretch };
        var cheats = new CheckBox { Content = "允许作弊", IsChecked = world.Settings.AllowCommands }; var hardcore = new CheckBox { Content = "极限模式", IsChecked = world.Settings.Hardcore };
        foreach (var control in new UIElement[] { name, mode, difficulty, cheats, hardcore, Text("请先退出游戏。保存前备份 level.dat；生成副本保留地形；高级设置仅写入勾选的字段。", 12) }) content.Children.Add(control);
        var advanced = new WorldAdvancedEditor(world.Settings); content.Children.Add(advanced.View);
        var error = Text("", 12); content.Children.Add(error);
        WorldEdit? edit = null;
        var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "存档设置", Content = new ScrollViewer { Content = content, MaxHeight = 520 }, PrimaryButtonText = "备份并保存", SecondaryButtonText = "生成副本", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, RequestedTheme = ElementTheme.Dark };
        void ValidateEdit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            try { edit = advanced.Apply(new WorldEdit(name.Text.Trim(), mode.SelectedIndex, difficulty.SelectedIndex, cheats.IsChecked == true, hardcore.IsChecked == true)); error.Text = ""; }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
        }
        dialog.PrimaryButtonClick += ValidateEdit; dialog.SecondaryButtonClick += ValidateEdit;
        var result = await dialog.ShowAsync(); if (result == ContentDialogResult.None || edit == null) return;
        if (result == ContentDialogResult.Primary) _status.Text = "保存完成，备份：" + await Task.Run(() => WorldArchiveService.Update(world.Path, edit));
        else
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializePicker(picker); var parent = await picker.PickSingleFolderAsync(); if (parent == null) return;
            var destination = Path.Combine(parent.Path, Path.GetFileName(world.Path) + "-copy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var confirm = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "生成副本", Content = "生成到：\n" + destination + "\n已有文件不会被覆盖。", PrimaryButtonText = "生成", CloseButtonText = "取消", RequestedTheme = ElementTheme.Dark };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary) _status.Text = "已生成：" + await Task.Run(() => WorldArchiveService.GenerateCopy(world.Path, destination, edit));
        }
        await ShowPageAsync("worlds");
    }
    private async Task OpenStructureAsync()
    {
        var picker = new FileOpenPicker(); foreach (var extension in new[] { ".litematic", ".nbt", ".schematic", ".schem" }) picker.FileTypeFilter.Add(extension); InitializePicker(picker);
        var file = await picker.PickSingleFileAsync(); if (file == null) return;
        _status.Text = "正在读取结构…"; var data = await Task.Run(() => StructureInspector.Read(file.Path));
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(Text($"{data.Format} · {data.Width} × {data.Height} × {data.Depth}\n{data.Blocks:N0} 个非空气方块 · 俯视图（颜色区分类型，非游戏材质）"));
        double scale = Math.Min(1, 640.0 / Math.Max(data.Width, data.Depth)); int width = Math.Max(1, (int)Math.Ceiling(data.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(data.Depth * scale));
        var pixels = new byte[width * height * 4]; var levels = Enumerable.Repeat(int.MinValue, width * height).ToArray();
        foreach (var cell in data.TopView)
        {
            int index = Math.Min(height - 1, (int)(cell.Z * scale)) * width + Math.Min(width - 1, (int)(cell.X * scale));
            if (cell.Y < levels[index]) continue; levels[index] = cell.Y; var color = StructureInspector.Color(cell.Block);
            pixels[index * 4] = color.B; pixels[index * 4 + 1] = color.G; pixels[index * 4 + 2] = color.R; pixels[index * 4 + 3] = 255;
        }
        var bitmap = new WriteableBitmap(width, height); using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(pixels); bitmap.Invalidate();
        panel.Children.Add(new Image { Source = bitmap, Height = 280, Stretch = Stretch.Uniform });
        panel.Children.Add(new Expander { Header = "材料清单", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Text(string.Join("\n", data.Materials.OrderByDescending(p => p.Value).Take(200).Select(p => $"{p.Key} × {p.Value:N0}")), 12) });
        panel.Children.Add(new Expander { Header = "NBT 结构", HorizontalAlignment = HorizontalAlignment.Stretch, Content = new TextBox { Text = data.NbtText, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 220, TextWrapping = TextWrapping.NoWrap } });
        var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, Title = Path.GetFileName(file.Path), Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, CloseButtonText = "关闭", RequestedTheme = ElementTheme.Dark };
        await dialog.ShowAsync();
    }
}
