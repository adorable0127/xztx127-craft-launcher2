using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal sealed class PluginApiSettingsDialog : OverlayDialogControl
{
    internal PluginApiSettingsDialog()
    {
        Width = 650; MaxHeight = 670;
        var panel = new StackPanel { Margin = new Thickness(22) };
        var config = ConfigService.Active!.Config;
        var title = new DockPanel();
        var close = new Button { Content = "×", ToolTip = "关闭", Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => CloseWith(false); DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        title.Children.Add(new TextBlock { Text = "扩展 API 2.1", FontSize = 23, FontWeight = FontWeights.Bold });
        panel.Children.Add(title);
        var preview = new CheckBox { Content = "允许原生预览扩展 API", IsChecked = config.EnablePreviewPluginApi, Margin = new Thickness(0, 14, 0, 8) };
        var worlds = new CheckBox { Content = "允许存档 API", IsChecked = config.EnableWorldPluginApi, Margin = new Thickness(0, 14, 0, 8) };
        panel.Children.Add(preview);
        panel.Children.Add(new TextBlock { Text = "插件可以注册和更新预览卡片、标题、强调色与布局密度。需要 ui.preview 授权。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(worlds);
        panel.Children.Add(new TextBlock { Text = "读取及编辑存档、游戏规则和玩家数据。每次写入均显示具体改动，确认后自动备份；关闭开关后新调用立即被拒绝。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "回调最长等待时间（秒）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        var timeout = new TextBox { Text = config.PluginCallbackTimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture), Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(7) };
        panel.Children.Add(timeout);
        panel.Children.Add(new TextBlock { Text = "允许 0.1～3600 秒（最长 1 小时），默认 3 秒。适合较长安装或生成任务；取消任务与停止插件仍会中止等待。网络请求和 EXE 握手保持独立超时。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "新增启动器控制能力", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        panel.Children.Add(new TextBlock { Text = "页面导航、账户 / 实例选择、启动 / 取消、窗口标题和尺寸、置顶与侧栏、主题与透明度、内存与下载源，均通过明确权限授权。插件管理 → 选择插件 → 授权，可单独撤销。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 14) });
        var docs = new Button { Content = "打开 API 开发中心 · 6 个专题与样例", Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        docs.Click += (_, _) =>
        {
            CloseWith(false);
            if (Application.Current.MainWindow is MainWindow owner) owner.NavigateToApiDocumentation();
        };
        panel.Children.Add(docs);
        panel.Children.Add(new TextBlock { Text = "授权只限制宿主 API。DLL 插件和源码编译拥有当前用户的系统权限，请使用可信来源。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 10) });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        error.Foreground = System.Windows.Media.Brushes.IndianRed; panel.Children.Add(error);
        var save = new Button { Content = "保存", Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            if (!double.TryParse(timeout.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0.1 || seconds > 3600)
            { error.Text = "请输入 0.1～3600 秒的有效等待时间。"; return; }
            config.EnablePreviewPluginApi = preview.IsChecked == true;
            config.EnableWorldPluginApi = worlds.IsChecked == true;
            config.PluginCallbackTimeoutSeconds = seconds;
            ConfigService.Active.Save(); CloseWith(true);
        };
        panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
