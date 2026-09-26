using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace XCL2.App.Views;

public partial class ExperimentalPreviewWindow : Window
{
    private HomePage? _previewHome;
    private readonly MainWindow _owner;

    public ExperimentalPreviewWindow(MainWindow owner)
    {
        _owner = owner;
        Owner = owner;
        InitializeComponent();
        _previewHome = new HomePage(owner);
        // 修复"按钮显示状态"+"套娃假死"问题：这份内层 HomePage 实际上已经在被预览窗口
        // 打开了，把它自己的「新界面预览」按钮同步成已选中并禁用，见该方法上的详细注释。
        _previewHome.ConfigureAsEmbeddedPreview();
        PreviewContent.Content = _previewHome;
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 标题栏"下载"按钮：就是打开下载列表（下载中心页面），跟侧边栏「下载」导航按钮
    /// 是同一个入口——直接复用 MainWindow.NavigateToDownloadCenter()，不重新实现一份。
    /// </summary>
    private void Download_Click(object sender, RoutedEventArgs e) => _owner.NavigateToDownloadCenter();

    /// <summary>
    /// 之前直接把 PreviewContent.Content 置空会崩：HomePage 内部的搜索结果 Popup
    /// 如果还开着，会在自己所在的可视化树被摘掉的同一时刻尝试重新定位，WPF 在这种时序下
    /// 会抛异常；同时 HomePage 订阅的 LocalizationService.LanguageChanged 是静态事件，
    /// 不主动关闭/清空就会一直挂着一个指向已关闭窗口内容的引用。这里在真正关闭前先强制
    /// 关掉预览里所有还开着的 Popup，再把 Content 置空，两步都套了保护，任何一步出错都
    /// 不应该让"关闭"这个操作本身失败。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try { CloseAllOpenPopups(_previewHome); } catch { }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { PreviewContent.Content = null; } catch { }
        _previewHome = null;
        base.OnClosed(e);
    }

    private static void CloseAllOpenPopups(DependencyObject? root)
    {
        if (root == null) return;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup { IsOpen: true } popup) popup.IsOpen = false;
            CloseAllOpenPopups(child);
        }
    }
}
