using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>
/// "插件管理"弹窗：列出 PluginManager 扫描到的所有插件（含加载失败的），支持
/// 启用/禁用、打开某个插件自己的配置面板，以及三个面向插件开发者的"开发命令"
/// （重新扫描/重新加载、打开插件目录、生成插件项目模板）。
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

    /// <summary>启用/禁用勾选框的改动先暂存在这里，不立刻写盘；只有点“保存”时才
    /// 一次性调用 _manager.SetEnabled 落盘，点“取消”则整批丢弃，界面状态也不回滚
    /// 到磁盘上的旧值（因为压根没写过），符合“取消=不生效”的直觉。</summary>
    private readonly Dictionary<string, bool> _pendingEnabled = new();

    public PluginManagerDialog(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        _manager = _owner.Plugins;
        Render();
    }

    private void Render()
    {
        PluginListPanel.Children.Clear();

        if (_manager.Plugins.Count == 0)
        {
            PluginListPanel.Children.Add(new TextBlock
            {
                Text = "还没有发现插件。拖入 DLL / EXE，或点击“导入插件”选择文件。",
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
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };

        var enabledBox = new CheckBox
        {
            IsChecked = _pendingEnabled.TryGetValue(info.FilePath, out var pending) ? pending : info.Enabled,
            IsEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "启用 / 禁用（点“保存”才会落盘，之后还需要“重新扫描 / 重新加载插件”才会生效）"
        };
        enabledBox.Checked += (_, _) => _pendingEnabled[info.FilePath] = true;
        enabledBox.Unchecked += (_, _) => _pendingEnabled[info.FilePath] = false;
        titleLine.Children.Add(enabledBox);

        string name = info.Plugin?.DisplayName ?? Path.GetFileName(info.FilePath);
        string version = info.Plugin?.Version is { Length: > 0 } v ? $" v{v}" : "";
        string author = info.Plugin?.Author is { Length: > 0 } a ? $"  ·  {a}" : "";
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
            IsEnabled = info.LoadError == null && info.Plugin != null && info.Context != null,
            VerticalAlignment = VerticalAlignment.Center
        };
        configButton.Click += (_, _) => OpenSettings(info);
        titleLine.Children.Add(configButton);

        root.Children.Add(titleLine);

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

        if (info.Plugin?.Description is { Length: > 0 } desc)
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
        if (info.Plugin == null) return;

        UserControl? panel = null;
        if (info.Context != null)
        {
            try { panel = info.Plugin.CreateSettingsPanel(info.Context); }
            catch (Exception ex)
            {
                panel = new TextBlock { Text = $"插件生成配置面板时抛出异常：{ex.Message}", TextWrapping = TextWrapping.Wrap }
                    .WrapAsUserControl();
            }
        }

        panel ??= BuildFallbackConfigPreview(info);

        new PluginSettingsHostDialog($"{info.Plugin.DisplayName} · 配置", panel).ShowDialog();
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
        _manager.ScanAndLoad();
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
            Filter = "插件 (*.dll;*.exe)|*.dll;*.exe",
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
            using var resource = typeof(PluginManagerDialog).Assembly.GetManifestResourceStream("XCL2.App.PLUGIN_GUIDE.md")
                ?? throw new FileNotFoundException("未找到内置插件开发指南。");
            var docsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2", "Docs");
            Directory.CreateDirectory(docsDir);
            var guidePath = Path.Combine(docsDir, "PLUGIN_GUIDE.md");
            using (var output = File.Create(guidePath)) resource.CopyTo(output);
            try { Process.Start(new ProcessStartInfo(guidePath) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception)
            {
                var fallback = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                fallback.ArgumentList.Add(guidePath);
                Process.Start(fallback);
            }
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

    /// <summary>把暂存的启用/禁用改动一次性落盘，再关闭弹窗。</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (filePath, enabled) in _pendingEnabled)
            _manager.SetEnabled(filePath, enabled);
        _pendingEnabled.Clear();
        CloseWith(null);
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
