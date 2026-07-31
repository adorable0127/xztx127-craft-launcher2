using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 整页的 Mod / 资源包 / 数据包 / 光影包 / 地图 详情页，取代之前"列表原地手风琴展开"的交互，
/// 改成仿视频/CurseForge 那种"点进一个条目后整页跳转，顶部有返回箭头"的样式。
///
/// 两种使用场景（由构造函数的 mode 参数区分，两者共用同一套版本分组展示 UI）：
/// 1. 下载中心（Mode = DirectDownload）：点击"下载"按钮直接下载安装到当前选中的 .minecraft 文件夹，
///    行为等价于原来 DownloadCenterPage 里 DownloadModInlineAsync/DownloadResourceInlineAsync。
/// 2. 一键开服向导（Mode = AddToWizardList）：点击按钮文案变成"加入清单"，点击后不下载，
///    而是把选中的具体版本封装成 WizardSelectionEntry 回调给宿主向导，由向导维护"已选清单"，
///    最后统一下载步骤时按清单里锁定的版本下载。
///
/// 不管哪种模式，都需要把"这个条目对应的原始搜索结果（UnifiedModItem/UnifiedResourceItem）+
/// 具体点的版本文件（InlineVersionEntry）"提供给宿主页面处理下载/加入清单的实际逻辑——
/// 本控件不直接依赖 DownloadService/ModrinthService 等下载实现，靠 onDownload/onAddToList
/// 回调委托给宿主，保持这个详情页本身是"纯展示 + 交互"组件，两处宿主各自的下载目录解析
/// （下载中心按当前 folder + 资源包隔离设置；一键开服向导按向导里选的版本）完全不同，
/// 硬塞进本控件反而会让它认识不该认识的宿主细节。
/// </summary>
public partial class ModDetailPage : UserControl
{
    public enum DetailMode { DirectDownload, AddToWizardList }

    /// <summary>版本条目按钮的文案依赖属性："下载"（下载中心模式）或"加入清单"（一键开服模式），
    /// 供 XAML 里 DataTemplate 内的按钮通过 RelativeSource AncestorType=UserControl 绑定，
    /// 因为按钮所在的 DataContext 是 InlineVersionEntry，没法直接拿到宿主 UserControl 的 CLR 字段。</summary>
    public static readonly DependencyProperty EntryActionLabelProperty = DependencyProperty.Register(
        nameof(EntryActionLabel), typeof(string), typeof(ModDetailPage), new PropertyMetadata("下载"));

    public string EntryActionLabel
    {
        get => (string)GetValue(EntryActionLabelProperty);
        set => SetValue(EntryActionLabelProperty, value);
    }

    private readonly DetailMode _mode;
    private readonly Action _onBack;
    private readonly Func<InlineVersionEntry, Task>? _onDownload;
    private readonly Action<InlineVersionEntry, string?>? _onAddToList;

    /// <summary>原始搜索结果条目：UnifiedModItem 或 UnifiedResourceItem，供收藏/展示复用，
    /// 以及宿主向导在 onAddToList 回调里构造 WizardSelectionEntry 时取标题/图标等展示字段。</summary>
    private readonly object _sourceItem;
    public object SourceItem => _sourceItem;
    private readonly bool _isDataPack;
    private readonly Action<bool>? _onFavoriteToggle;
    private bool _isFavorite;

    /// <summary>数据包场景下拿到的存档名列表 + 当前选中项，跟随向导/下载中心一次性传入
    /// （由宿主页面在打开详情页前先扫描好 FolderService.ScanSaves，本控件不自己扫描文件系统，
    /// 避免重复实现"找当前选中的 .minecraft 文件夹"这段逻辑）。</summary>
    public string? SelectedSaveName => SaveNameCombo.SelectedItem as string;

    public ModDetailPage(
        DetailMode mode,
        string title,
        string description,
        string? iconUrl,
        string author,
        long downloads,
        string sourceLabel,
        string? sourceUrl,
        object sourceItem,
        bool isFavorite,
        Action<bool>? onFavoriteToggle,
        Action onBack,
        Func<InlineVersionEntry, Task>? onDownload = null,
        Action<InlineVersionEntry, string?>? onAddToList = null,
        bool isDataPack = false,
        IEnumerable<string>? saveNames = null)
    {
        InitializeComponent();
        _mode = mode;
        EntryActionLabel = mode == DetailMode.AddToWizardList ? "加入清单" : "下载";
        _sourceItem = sourceItem;
        _isDataPack = isDataPack;
        _onBack = onBack;
        _onDownload = onDownload;
        _onAddToList = onAddToList;
        _onFavoriteToggle = onFavoriteToggle;
        _isFavorite = isFavorite;

        TitleText.Text = title;
        NameText.Text = title;
        DescriptionText.Text = string.IsNullOrWhiteSpace(description) ? "（暂无简介）" : description;
        MetaText.Text = $"作者: {author}    下载量: {downloads}    来源: {sourceLabel}";
        IconImage.Source = string.IsNullOrEmpty(iconUrl) ? null : new System.Windows.Media.Imaging.BitmapImage(new Uri(iconUrl));
        _sourceUrl = sourceUrl;
        OpenSourceButton.Visibility = string.IsNullOrEmpty(sourceUrl) ? Visibility.Collapsed : Visibility.Visible;
        UpdateFavoriteButtonText();

        if (isDataPack)
        {
            SaveNamePanel.Visibility = Visibility.Visible;
            SaveNameCombo.ItemsSource = saveNames?.ToList() ?? new List<string>();
            if (SaveNameCombo.Items.Count > 0) SaveNameCombo.SelectedIndex = 0;
        }
    }

    private readonly string? _sourceUrl;

    /// <summary>保存最近一次拉到的扁平版本列表，供"显示预览版"按钮切换时本地重新分组用，
    /// 不需要重新发网络请求——预览版本来就在这批数据里，只是 ModVersionGrouping.Group
    /// 默认把它们过滤掉了。</summary>
    private List<InlineVersionEntry> _flatEntries = new();
    private bool _includePreview;

    /// <summary>展开状态：调用方在拿到分组数据后调用这个方法一次性填充展示。
    /// 跟旧版 ToggleModExpandAsync/ToggleResourceExpandAsync 里"填充 Groups"是同一批数据，
    /// 只是现在不需要"展开/收起"这一层，页面本身就是详情页，进来就直接显示。</summary>
    public void ShowGroups(IEnumerable<VersionGroup> groups)
    {
        var list = groups.ToList();
        GroupsList.ItemsSource = list;
        NoResultText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Collapsed;
    }

    /// <summary>跟 ShowGroups 配套：额外传入这批版本对应的扁平列表，好让"显示预览版"
    /// 按钮之后能本地重新分组。调用方（DownloadCenterPage）在 LoadModVersionsAsync/
    /// LoadResourceVersionsAsync 里 item.Versions 填充完成后一并传进来。不强制要求调用——
    /// 不传时 _flatEntries 保持为空，"显示预览版"点了也只是空列表，不会报错。</summary>
    public void SetFlatEntries(IEnumerable<InlineVersionEntry> entries)
    {
        _flatEntries = entries.ToList();
        _includePreview = false;
        TogglePreviewButton.Content = "显示预览版";
    }

    private void TogglePreview_Click(object sender, RoutedEventArgs e)
    {
        _includePreview = !_includePreview;
        TogglePreviewButton.Content = _includePreview ? "隐藏预览版" : "显示预览版";
        var groups = ModVersionGrouping.Group(_flatEntries, _includePreview);
        if (groups.Count > 0) groups[0].IsExpanded = true;
        ShowGroups(groups);
    }

    public void ShowLoading()
    {
        LoadingText.Visibility = Visibility.Visible;
        NoResultText.Visibility = Visibility.Collapsed;
        GroupsList.ItemsSource = null;
    }

    private void UpdateFavoriteButtonText()
    {
        FavoriteButton.Content = _isFavorite ? "★ 已收藏" : "☆ 收藏";
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _onBack();

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_sourceUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_sourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开浏览器：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(NameText.Text); } catch { /* 剪贴板偶发被占用，静默忽略，不影响其它功能 */ }
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        _isFavorite = !_isFavorite;
        UpdateFavoriteButtonText();
        _onFavoriteToggle?.Invoke(_isFavorite);
    }

    private void SaveNameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>分组标题条点击：翻转 VersionGroup.IsExpanded，跟原来 DownloadCenterPage 里
    /// VersionGroupHeader_Click 逻辑完全一致，只是搬到了详情页自己的 code-behind。</summary>
    private void VersionGroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not VersionGroup group) return;
        group.IsExpanded = !group.IsExpanded;
    }

    /// <summary>版本条目按钮点击：下载中心模式直接调用 onDownload 下载；
    /// 一键开服模式调用 onAddToList，把选中版本交给向导加入清单，不在这里下载任何东西。</summary>
    private async void EntryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not InlineVersionEntry entry) return;

        if (_isDataPack && string.IsNullOrEmpty(SelectedSaveName))
        {
            MessageBox.Show("请先选择要安装到哪个存档（数据包必须放进具体存档才会生效）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_mode == DetailMode.DirectDownload)
        {
            if (_onDownload != null)
            {
                btn.IsEnabled = false;
                try { await _onDownload(entry); }
                finally { btn.IsEnabled = true; }
            }
        }
        else
        {
            _onAddToList?.Invoke(entry, SelectedSaveName);
        }
    }
}
