using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class FeatureSearchBox : UserControl
{
    private MainWindow? _owner;
    private IReadOnlyList<FeatureSearchEntry> _catalog = Array.Empty<FeatureSearchEntry>();
    private IReadOnlyList<FeatureSearchEntry> _results = Array.Empty<FeatureSearchEntry>();
    private string _originPage = "home";
    private bool _suppress;
    private bool _navigating;
    private CancellationTokenSource? _navigation;
    private int _navigationVersion;

    public FeatureSearchBox()
    {
        InitializeComponent();
        Unloaded += (_, _) => { SearchPopup.IsOpen = false; CancelNavigation(); };
    }

    public void Attach(MainWindow owner)
    {
        _owner = owner;
        _suppress = true;
        ScopeCombo.SelectedIndex = owner.ConfigService.Config.FeatureSearchAllPages ? 1 : 0;
        _suppress = false;
    }

    internal void SetEntryVisible(bool visible)
    {
        SearchEntry.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) Close();
    }

    public void Open(string? query = null, string? pageKey = null)
    {
        if (_owner == null) return;
        CancelNavigation();
        _originPage = pageKey ?? _owner.CurrentSearchPageKey;
        string? loadError = null;
        try { _catalog = FeatureSearchCatalog.Load(); }
        catch (Exception ex) { loadError = "无法加载功能目录：" + ex.Message; _catalog = Array.Empty<FeatureSearchEntry>(); }
        _suppress = true;
        if (pageKey != null) ScopeCombo.SelectedIndex = 0;
        else ScopeCombo.SelectedIndex = _owner.ConfigService.Config.FeatureSearchAllPages ? 1 : 0;
        if (query != null) QueryBox.Text = NormalizeQuery(query);
        _suppress = false;
        FloatingSearchCard.Width = Math.Max(320, Math.Min(520, _owner.ActualWidth - 80));
        FloatingSearchCard.MaxHeight = Math.Max(160, Math.Min(430, _owner.ActualHeight - 110));
        ResultList.MaxHeight = Math.Max(72, Math.Min(275, FloatingSearchCard.MaxHeight - 120));
        // 设置只隐藏搜索入口。快捷键仍能在可见的主窗口上打开搜索弹层。
        SearchPopup.PlacementTarget = SearchEntry.IsVisible ? SearchEntry : _owner;
        SearchPopup.Placement = SearchEntry.IsVisible ? PlacementMode.Bottom : PlacementMode.Center;
        SearchPopup.HorizontalOffset = SearchEntry.IsVisible && !_floating ? SearchEntry.ActualWidth - FloatingSearchCard.Width : 0;
        SearchPopup.VerticalOffset = SearchEntry.IsVisible ? 6 : 0;
        RebuildResults();
        if (loadError != null)
        {
            SearchStatus.Text = loadError;
            SearchStatus.Visibility = Visibility.Visible;
        }
        SearchPopup.IsOpen = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!SearchPopup.IsOpen) return;
            QueryBox.Focus(); QueryBox.SelectAll();
        }));
    }

    public void Close() { SearchPopup.IsOpen = false; CancelNavigation(); }
    /// <summary>窗口最大化/还原/缩放后调用：关掉位置已过期的弹层并重新布局入口。</summary>
    internal void RefreshLayout()
    {
        SearchPopup.IsOpen = false;
        EmptyClickHint.Visibility = Visibility.Collapsed;
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
    }

    private bool _floating;
    private int _emptyClicks;
    private DateTime _emptyClickWindowStart = DateTime.MinValue;

    /// <summary>悬浮窗样式下搜索入口半透明（鼠标移上去恢复不透明）；原样式完全不透明。</summary>
    internal void ApplyStyle(bool floating)
    {
        _floating = floating;
        SearchEntry.MouseEnter -= SearchEntry_MouseEnterLeave;
        SearchEntry.MouseLeave -= SearchEntry_MouseEnterLeave;
        if (floating)
        {
            SearchEntry.MouseEnter += SearchEntry_MouseEnterLeave;
            SearchEntry.MouseLeave += SearchEntry_MouseEnterLeave;
        }
        SearchEntry_MouseEnterLeave(null, null!);
    }

    private void SearchEntry_MouseEnterLeave(object? sender, MouseEventArgs e)
        => SearchEntry.Opacity = _floating && !SearchEntry.IsMouseOver && !SearchPopup.IsOpen ? 0.7 : 1.0;

    private void SearchEntry_Click(object sender, RoutedEventArgs e)
    {
        // 反复点搜索框却一直没输入内容：15 秒内累计 3 次，就弹出内嵌提示。
        if (string.IsNullOrWhiteSpace(QueryBox.Text))
        {
            var now = DateTime.UtcNow;
            if ((now - _emptyClickWindowStart).TotalSeconds > 15) { _emptyClickWindowStart = now; _emptyClicks = 0; }
            if (++_emptyClicks >= 3)
            {
                _emptyClicks = 0;
                SearchPopup.IsOpen = false;
                EmptyClickHint.Visibility = Visibility.Visible;
                return;
            }
        }
        else _emptyClicks = 0;
        EmptyClickHint.Visibility = Visibility.Collapsed;
        _owner?.OpenFeatureSearch();
    }

    private void HintDismiss_Click(object sender, RoutedEventArgs e) => EmptyClickHint.Visibility = Visibility.Collapsed;

    private void HintSettings_Click(object sender, RoutedEventArgs e)
    {
        EmptyClickHint.Visibility = Visibility.Collapsed;
        _owner?.NavigateToSettings();
    }
    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _owner == null) return;
        string normalized = NormalizeQuery(QueryBox.Text);
        if (normalized != QueryBox.Text)
        {
            int caret = QueryBox.CaretIndex;
            _suppress = true;
            QueryBox.Text = normalized;
            QueryBox.CaretIndex = Math.Min(caret, normalized.Length);
            _suppress = false;
        }
        CancelNavigation();
        RebuildResults();
    }
    private static string NormalizeQuery(string query) => query.Replace("\r\n", " ")
        .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private void ScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _owner == null) return;
        CancelNavigation();
        _originPage = _owner.CurrentSearchPageKey;
        _owner.ConfigService.Config.FeatureSearchAllPages = ScopeCombo.SelectedIndex == 1;
        try { _owner.ConfigService.Save(); }
        catch (Exception ex) { LauncherLogService.AppendLine("[功能搜索] 保存范围失败：" + ex.Message); }
        RebuildResults();
    }
    private void RebuildResults()
    {
        var oldId = (ResultList.SelectedItem as FeatureSearchEntry)?.Id;
        _results = FeatureSearchMatcher.Match(_catalog, QueryBox.Text, ScopeCombo.SelectedIndex == 1 ? null : _originPage);
        ResultList.ItemsSource = _results;
        int existing = _results.ToList().FindIndex(item => item.Id == oldId);
        ResultList.SelectedIndex = _results.Count == 0 ? -1 : Math.Max(0, existing);
        SearchEntryText.Text = string.IsNullOrWhiteSpace(QueryBox.Text) ? "搜索功能" : "搜索：" + QueryBox.Text;
        SearchEntry.ToolTip = "搜索功能（Ctrl+K / Ctrl+F）";
        SearchStatus.Text = ScopeCombo.SelectedIndex == 1 ? "搜索全部页面；定位后高亮对应功能。" : "搜索当前打开的主页面及主窗口常用功能。";
        SearchStatus.Visibility = Visibility.Collapsed;
        ResultList.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoResultsText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Text = string.IsNullOrWhiteSpace(QueryBox.Text)
            ? "输入功能名称，例如：启动游戏、下载游戏、内存、UUID。"
            : "没有匹配的功能，试试更短的词或“搜索全部”。";
        UpdateResultCount();
    }
    private void UpdateResultCount()
    {
        if (ResultCount == null) return;
        int position = ResultList.SelectedIndex + 1;
        ResultCount.Text = $"{position} / {_results.Count} 项";
    }
    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateResultCount();
    private void MoveSelection(int direction)
    {
        if (_results.Count == 0) return;
        int current = ResultList.SelectedIndex;
        ResultList.SelectedIndex = current < 0 ? (direction > 0 ? 0 : _results.Count - 1)
            : (current + direction + _results.Count) % _results.Count;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }
    private async void FloatingSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); RestoreFocus(); return; }
        if (ScopeCombo.IsKeyboardFocusWithin) return; // 保留范围选择器的原生方向键操作。
        if (e.Key is Key.Up or Key.Down) { e.Handled = true; MoveSelection(e.Key == Key.Up ? -1 : 1); }
        else if (e.Key == Key.Enter && ResultList.SelectedItem is FeatureSearchEntry)
        { e.Handled = true; await LocateSelectedAsync(); }
    }
    private async void ResultList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ResultList, source) is not ListBoxItem item
            || item.Content is not FeatureSearchEntry entry) return;
        ResultList.SelectedItem = entry;
        e.Handled = true;
        await LocateSelectedAsync();
    }
    private async Task LocateSelectedAsync()
    {
        if (_owner == null || ResultList.SelectedItem is not FeatureSearchEntry entry) return;
        CancelNavigation();
        var navigation = new CancellationTokenSource();
        _navigation = navigation;
        int version = ++_navigationVersion;
        _navigating = true;
        SearchPopup.IsOpen = false;
        try
        {
            string status = await _owner.NavigateToSearchResultAsync(entry, navigation.Token);
            if (version != _navigationVersion) return;
            SearchStatus.Text = status;
            SearchEntry.ToolTip = status + "；点击继续搜索。";
            // 非破坏性提示同时照顾目标隐藏、导航被取消和定位成功的情况。
            ToastService.ShowInfo(status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _navigationVersion)
            { SearchStatus.Text = "无法定位：" + ex.Message; ToastService.ShowInfo(SearchStatus.Text); }
        }
        finally
        {
            if (ReferenceEquals(_navigation, navigation)) { _navigation = null; _navigating = false; }
            navigation.Dispose();
        }
    }
    private void CancelNavigation()
    {
        ++_navigationVersion;
        _navigation?.Cancel();
        _navigating = false;
    }
    private void SearchPopup_Closed(object sender, EventArgs e)
    {
        if (!_navigating) CancelNavigation();
    }
    private void RestoreFocus()
    {
        if (SearchEntry.IsVisible) SearchEntry.Focus();
        else _owner?.Focus();
    }
    private void Close_Click(object sender, RoutedEventArgs e) { Close(); RestoreFocus(); }
}
