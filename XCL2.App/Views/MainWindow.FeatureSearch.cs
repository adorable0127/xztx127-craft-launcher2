using System.Windows;
using System.Windows.Input;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private void InitializeFeatureSearch()
    {
        LauncherFeatureSearch.Attach(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.K or Key.F)) return;
            // Ctrl+F 在编辑框内仍交给文本/网页控件；Ctrl+K 始终是功能搜索快捷键。
            if (e.Key == Key.F && (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)) return;
            if (OverlayRoot.Visibility == Visibility.Visible) return;
            e.Handled = true;
            OpenFeatureSearch();
        };
        Deactivated += (_, _) => LauncherFeatureSearch.Close();
        // 最大化/还原/缩放后：重新应用搜索框样式并刷新当前页（首页会重新匹配当前高度）。
        StateChanged += (_, _) => RefreshLayoutAfterResize();
        SizeChanged += (_, _) => RefreshLayoutAfterResize();
    }

    public void OpenFeatureSearch(string? query = null, string? pageKey = null)
    {
        if (OverlayRoot.Visibility == Visibility.Visible || _isWindowClosed) return;
        LauncherFeatureSearch.Open(query, pageKey);
    }

    internal void ApplyFeatureSearchVisibility(bool? visible = null)
    {
        bool show = visible ?? ConfigService.Config.ShowFeatureSearch;
        if (!show) LauncherFeatureSearch.Close();
        // Keep the popup host alive so Ctrl+K can still search when its entry is hidden.
        LauncherFeatureSearch.SetEntryVisible(show);
        var floating = ConfigService.Config.FeatureSearchStyle != "Top";
        LauncherFeatureSearch.ApplyStyle(floating);
        // 悬浮窗：盖在内容区左上角（不占行）；原样式：独占顶部一行、靠右。
        System.Windows.Controls.Grid.SetRowSpan(LauncherFeatureSearch, floating ? 2 : 1);
        // 跨两行时默认 Stretch 会让搜索入口在整个内容区垂直居中，必须固定贴顶。
        LauncherFeatureSearch.VerticalAlignment = VerticalAlignment.Top;
        LauncherFeatureSearch.HorizontalAlignment = floating ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        // 悬浮窗紧贴标题栏（上方快捷栏）下沿，所有页面都固定显示在顶部（内容区有 16 的外边距，这里抵消掉大部分，-7 即离标题栏约 9px）。
        LauncherFeatureSearch.Margin = floating ? new Thickness(0, -7, 0, 0)
            : show ? new Thickness(0, 0, 0, 10) : new Thickness(0);
    }

    private bool _resizeRefreshQueued;
    private void RefreshLayoutAfterResize()
    {
        if (_resizeRefreshQueued || _isWindowClosed) return;
        _resizeRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _resizeRefreshQueued = false;
            if (_isWindowClosed) return;
            ApplyFeatureSearchVisibility();
            LauncherFeatureSearch.RefreshLayout();
            (MainContent?.Content as HomePage)?.RefreshForSizeChange();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
