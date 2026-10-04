using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>
/// "插件管理"弹窗：列出 PluginManager 扫描到的所有插件（含加载失败的），支持
/// 启用/禁用、打开某个插件自己的配置面板，以及一组面向插件开发者的开发命令（重新扫描/重新加载、导入、打开插件目录、
/// 生成插件项目模板，以及打开 DLL/API/EXE 协议文档）。
///
/// 列表项没有用 XAML 数据模板 + 绑定，是有意简化：插件列表是"打开弹窗时扫一次、
/// 操作完立刻重新渲染整份列表"这种轻量交互，不需要为几条到几十条数据搭一整套
/// INotifyPropertyChanged/ObservableCollection，直接在代码里 Children.Clear()
/// 再重新 Add 一遍即可，跟这个弹窗的量级更匹配。
/// </summary>
public partial class PluginManagerDialog : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly PluginManager _manager;

    private readonly Dictionary<string, bool> _pendingEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _rendering;

    public PluginManagerDialog(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        _manager = _owner.Plugins;
        Loaded += (_, _) => { _manager.Changed -= Render; _manager.Changed += Render; Render(); };
        Unloaded += (_, _) => _manager.Changed -= Render;
        Render();
    }

    private void Render()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(Render)); return; }
        _rendering = true;
        var installed = _manager.Plugins.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selected.IntersectWith(installed);
        foreach (var stale in _pendingEnabled.Keys.Where(k => !installed.Contains(k)).ToArray()) _pendingEnabled.Remove(stale);
        PluginListPanel.Children.Clear();

        if (_manager.Plugins.Count == 0)
        {
            PluginListPanel.Children.Add(new TextBlock
            {
                Text = "还没有发现插件。拖入 DLL / EXE / .xclplugin，或点击“导入插件”选择文件。",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 8)
            });
        }

        foreach (var info in _manager.Plugins)
            PluginListPanel.Children.Add(BuildRow(info));

        bool IsEffectivelyEnabled(LoadedPluginInfo p) =>
            _pendingEnabled.TryGetValue(p.FilePath, out var pending) ? pending : p.Enabled;

        SelectAllBox.IsEnabled = installed.Count > 0;
        SelectAllBox.IsChecked = _selected.Count == 0 ? false : _selected.Count == installed.Count ? true : null;
        SelectionText.Text = $"已选择 {_selected.Count} / {installed.Count}";
        DeleteSelectedButton.IsEnabled = EnableSelectedButton.IsEnabled = DisableSelectedButton.IsEnabled = _selected.Count > 0;
        _rendering = false;
        int ok = _manager.Plugins.Count(p => p.LoadError == null);
        int enabled = _manager.Plugins.Count(p => p.LoadError == null && IsEffectivelyEnabled(p));
        int failed = _manager.Plugins.Count(p => p.LoadError != null);
        SummaryText.Text = $"共发现 {_manager.Plugins.Count} 个插件：{enabled} 个已启用，{ok - enabled} 个已禁用" +
                            (failed > 0 ? $"，{failed} 个加载失败。" : "。") +
                            $" 插件目录：{_manager.InstalledDirectory}";
    }

    private Border BuildRow(LoadedPluginInfo info)
    {
        var root = new StackPanel();
        var titleLine = new WrapPanel();
        var selectBox = new CheckBox
        {
            Content = "选择", IsChecked = _selected.Contains(info.FilePath),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 4),
            ToolTip = "选择此插件进行批量操作"
        };
        selectBox.Click += (_, _) => { if (selectBox.IsChecked == true) _selected.Add(info.FilePath); else _selected.Remove(info.FilePath); Render(); };
        titleLine.Children.Add(selectBox);

        var enabledBox = new CheckBox
        {
            IsChecked = _pendingEnabled.TryGetValue(info.FilePath, out var pending) ? pending : info.Enabled,
            Content = "启用",
            IsEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "启用 / 禁用（点“保存”后立即生效）"
        };
        enabledBox.Checked += (_, _) => { _pendingEnabled[info.FilePath] = true; Render(); };
        enabledBox.Unchecked += (_, _) => { _pendingEnabled[info.FilePath] = false; Render(); };
        titleLine.Children.Add(enabledBox);

        string name = info.Metadata?.Name ?? info.Plugin?.DisplayName ?? Path.GetFileName(info.FilePath);
        string version = (info.Metadata?.Version ?? info.Plugin?.Version) is { Length: > 0 } v ? $" v{v}" : "";
        string author = (info.Metadata?.Author ?? info.Plugin?.Author) is { Length: > 0 } a ? $"  ·  {a}" : "";
        titleLine.Children.Add(new TextBlock
        {
            Text = $"{name}{version}{author}",
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var configButton = new Button
        {
            Content = "配置",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = info.LoadError == null && (info.Plugin != null || info.IsExecutable) && info.Context?.Permissions.Has(PluginPermissionNames.UiBasic) == true,
            VerticalAlignment = VerticalAlignment.Center
        };
        configButton.Click += (_, _) => OpenSettings(info);
        if (info.Metadata is { IsLegacy: false } metadata)
        {
            var authorize = new Button { Content = Loc.T("Str_Plugin_Permissions", "权限…"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
            authorize.Click += async (_, _) =>
            {
                var dialog = new PluginPermissionsDialog(metadata, _manager.GetGrantedPermissions(info.FilePath));
                if (await OverlayDialogService.ShowModalAsync(dialog) != true) return;
                try
                {
                    _manager.SetPermissions(info.FilePath, dialog.Granted);
                    if (info.Context == null && info.RunningProcess == null) _manager.ScanAndLoad();
                }
                catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, Loc.T("Str_Plugin_Authorize", "插件授权")); }
                Render();
            };
            titleLine.Children.Add(authorize);
        }
        titleLine.Children.Add(configButton);
        var delete = new Button { Content = "删除", Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "立即停止并删除插件安装文件，保留配置和数据" };
        delete.Click += (_, _) => DeletePlugins(new[] { info.FilePath });
        titleLine.Children.Add(delete);

        root.Children.Add(titleLine);
        root.Children.Add(new TextBlock { Text = Path.GetRelativePath(_manager.InstalledDirectory, info.FilePath),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });

        if (info.IsExecutable || (info.StartedOnce && info.Context == null))
        {
            root.Children.Add(new TextBlock
            {
                Text = info.IsExecutable
                    ? (info.RunningProcess is { HasExited: false } ? "EXE 插件 · 正在运行" : "EXE 插件 · 当前未运行")
                    : "DLL 插件 · 本次未运行（仅导入时启动）",
                FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            });
        }

        if (info.Metadata == null || info.Metadata.IsLegacy)
            root.Children.Add(new TextBlock { Text = "此插件未声明权限，按旧版兼容模式加载；新增业务 API 不开放。", TextWrapping = TextWrapping.Wrap,
                FontSize = 11, Foreground = (Brush)FindResource("TextSecondaryBrush"), Margin = new Thickness(0, 4, 0, 0) });

        if ((info.Metadata?.Description ?? info.Plugin?.Description) is { Length: > 0 } desc)
        {
            root.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        if (info.LoadError != null)
        {
            root.Children.Add(new TextBlock
            {
                Text = $"加载失败：{info.LoadError}",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("DangerBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        if (info.Context?.BusinessDisabled == true)
            root.Children.Add(new TextBlock { Text = "插件回调超时且未返回，业务能力已暂停。可重新加载此插件。", TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5, Foreground = (Brush)FindResource("DangerBrush"), Margin = new Thickness(0, 4, 0, 0) });

        return new Border
        {
            Background = (Brush)FindResource("SideBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = root
        };
    }

    /// <summary>打开某个插件的配置面板：优先用插件自己提供的 CreateSettingsPanel；
    /// 插件没实现（返回 null）或者当前被禁用（没有 Context）时，退化成展示一份
    /// 该插件已保存配置项的只读 JSON 预览，至少让用户知道现在存了什么。</summary>
    private void OpenSettings(LoadedPluginInfo info)
    {
        if (info.Plugin == null && !info.IsExecutable) return;

        UserControl? panel = null;
        if (info.Context != null)
        {
            try { info.Context.Permissions.Demand(PluginPermissionNames.UiBasic); panel = info.Plugin?.CreateSettingsPanel(info.Context); }
            catch (Exception ex)
            {
                panel = new TextBlock { Text = $"插件生成配置面板时抛出异常：{ex.Message}", TextWrapping = TextWrapping.Wrap }
                    .WrapAsUserControl();
            }
        }

        panel ??= BuildFallbackConfigPreview(info);

        if (info.Context != null)
        {
            using var combined = new PluginSettingsContent(panel, info.Context.Ui);
            new PluginSettingsHostDialog($"{info.Metadata?.Name ?? info.Plugin?.DisplayName ?? Path.GetFileName(info.FilePath)} · 配置", combined).ShowDialog();
        }
        else new PluginSettingsHostDialog($"{info.Metadata?.Name ?? info.Plugin?.DisplayName ?? Path.GetFileName(info.FilePath)} · 配置", panel).ShowDialog();
    }

    private UserControl BuildFallbackConfigPreview(LoadedPluginInfo info)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        if (info.Context == null)
        {
            text.Text = "该插件当前已禁用，没有可展示的运行时配置；启用并重新加载后再打开配置。";
        }
        else
        {
            var snapshot = info.Context.Config.Snapshot();
            text.Text = snapshot.Count == 0
                ? "该插件没有提供图形配置界面，目前也还没有保存过任何配置项。"
                : "该插件没有提供图形配置界面，以下是当前已保存的原始配置（只读）：\n\n" +
                  JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        }
        return text.WrapAsUserControl();
    }

    // ===== 开发命令 =====

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        try { _manager.ScanAndLoad(); }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "重新加载插件"); }
        Render();
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_manager.InstalledDirectory);
        Process.Start(new ProcessStartInfo(_manager.InstalledDirectory) { UseShellExecute = true });
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择第三方插件",
            Filter = "插件 (*.dll;*.exe;*.xclplugin)|*.dll;*.exe;*.xclplugin",
            Multiselect = true
        };
        if (picker.ShowDialog() != true) return;
        _owner.ImportPluginFiles(picker.FileNames);
        Render();
    }

    private async void GenerateTemplate_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new PluginTemplatePromptDialog();
        var result = await OverlayDialogService.ShowModalAsync(prompt);
        if (result == true && prompt.GeneratedDirectory != null)
        {
            Process.Start(new ProcessStartInfo(prompt.GeneratedDirectory) { UseShellExecute = true });
        }
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OverlayDialogService.ShowNonModal(new HelpDocumentDialog(EmbeddedDocumentationService.PluginGuideFileName));
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning($"打开插件开发指南失败：{ex.Message}", "插件开发指南");
        }
    }

    private void OpenApiReference_Click(object sender, RoutedEventArgs e)
    {
        try { OverlayDialogService.ShowNonModal(new HelpDocumentDialog(Services.EmbeddedDocumentationService.ApiFileName)); }
        catch (Exception ex) { MessageBoxDialog.ShowWarning($"打开 API 接口说明失败：{ex.Message}", "启动器 API 接入说明"); }
    }

    private void OpenExeProtocol_Click(object sender, RoutedEventArgs e)
    {
        try { OverlayDialogService.ShowNonModal(new HelpDocumentDialog(Services.EmbeddedDocumentationService.PluginExeProtocolFileName)); }
        catch (Exception ex) { MessageBoxDialog.ShowWarning($"打开 EXE 插件协议失败：{ex.Message}", "EXE 插件协议"); }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        if (_selected.Count == _manager.Plugins.Count) _selected.Clear();
        else foreach (var plugin in _manager.Plugins) _selected.Add(plugin.FilePath);
        Render();
    }
    private void EnableSelected_Click(object sender, RoutedEventArgs e) => ChangeSelected(true);
    private void DisableSelected_Click(object sender, RoutedEventArgs e) => ChangeSelected(false);
    private void ChangeSelected(bool enabled)
    {
        foreach (var path in _selected) _pendingEnabled[path] = enabled;
        Render();
    }
    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => DeletePlugins(_selected.ToArray());
    private void DeletePlugins(string[] paths)
    {
        if (paths.Length == 0) return;
        var names = string.Join("\n", paths.Select(p => Path.GetRelativePath(_manager.InstalledDirectory, p)));
        if (!MessageBoxDialog.ShowConfirm($"立即停止并删除以下 {paths.Length} 个插件的安装文件（包括独立目录内的依赖）？\n\n{names}\n\n配置和数据会保留。此操作立即执行，不受底部“取消”影响。", "删除插件")) return;
        try
        {
            var results = _manager.Uninstall(paths);
            foreach (var result in results.Where(r => r.Success)) { _selected.Remove(result.FilePath); _pendingEnabled.Remove(result.FilePath); }
            var errors = results.Where(r => r.Error != null).Select(r => $"{Path.GetFileName(r.FilePath)}：{r.Error}").ToArray();
            if (errors.Length > 0) MessageBoxDialog.ShowWarning(string.Join("\n", errors), "插件删除结果");
            if (results.Any(r => r.Success)) Services.ToastService.ShowSuccess($"已删除 {results.Count(r => r.Success)} 个插件。");
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "删除插件"); }
        Render();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var group in _pendingEnabled.GroupBy(p => p.Value))
                _manager.SetEnabled(group.Select(p => p.Key), group.Key);
            _pendingEnabled.Clear();
            _manager.ScanAndLoad();
            CloseWith(null);
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "保存插件设置"); Render(); }
    }

    /// <summary>丢弃所有未保存的勾选改动，直接关闭弹窗。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingEnabled.Clear();
        CloseWith(null);
    }
}

/// <summary>把一个裸的 TextBlock 包一层 UserControl——PluginSettingsHostDialog /
/// IPlugin.CreateSettingsPanel 的约定返回类型是 UserControl，这里避免每处兜底展示
/// 都重复写这几行包装代码。</summary>
internal static class UiExtensions
{
    public static UserControl WrapAsUserControl(this UIElement element)
        => new() { Content = element, Margin = new Thickness(4) };
}
