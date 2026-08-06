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

    /// <summary>是否是"模组管理"场景（下载中心的 Mod 分类，见 DownloadCenterPage.OpenModDetailAsync）。
    /// "显示/隐藏预览版"按钮只在这个场景下出现——资源包/数据包/光影包/地图详情页复用同一个
    /// ModDetailPage，但按需求只给模组管理加这个按钮，其余场景保持之前"去掉这些按钮"的状态不变。</summary>
    private readonly bool _isMod;

    /// <summary>"显示/隐藏预览版"按钮当前状态：true=预览版(beta/alpha)也显示在分组列表里，
    /// false=只显示正式版。默认 false（跟历史上"预览版默认隐藏"的设计一致），只有 _isMod 时
    /// 才会被按钮切换和 RebuildModGroups 用到，非 Mod 场景下这个字段不产生任何效果。</summary>
    /// <summary>
    /// 「显示预览版」的全局默认值，由「下载中心 - 社区资源」筛选栏里的勾选框写入
    /// （见 DownloadCenterPage.ResourceShowPreview_Changed）。
    ///
    /// 用静态属性而不是在这里直接读 ConfigService：ModDetailPage 是个纯 UserControl，
    /// 没有持有 MainWindow/ConfigService 的引用（ConfigService.Config 是实例属性不是静态的），
    /// 为了读一个开关去给它接一条 owner 引用不划算。宿主页面本来就知道配置，
    /// 由宿主推给它是最省事也最不容易出错的做法。
    /// </summary>
    public static bool DefaultShowPreview { get; set; }

    /// <summary>本页当前的"显示/隐藏预览版"状态，初始值取上面的全局默认。
    /// 这样用户在列表页勾了"显示预览版资源"之后，进任何资源详情页都已经是展开状态，
    /// 不用每进一个资源再点一次按钮。详情页里再点按钮只改本页，不写回全局。</summary>
    private bool _showPreview = DefaultShowPreview;

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
        IEnumerable<string>? saveNames = null,
        bool isMod = false)
    {
        InitializeComponent();
        _mode = mode;
        EntryActionLabel = mode == DetailMode.AddToWizardList ? "加入清单" : "下载";
        _sourceItem = sourceItem;
        _isDataPack = isDataPack;
        _isMod = isMod;
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

    /// <summary>保存最近一次拉到的扁平版本列表。Mod 场景（_isMod=true）下这份数据是
    /// "显示/隐藏预览版"按钮的数据来源——RebuildModGroups 按 _showPreview 状态从这里重新分组；
    /// 非 Mod 场景（资源包/数据包/光影包/地图）目前只是保留供以后可能的场景复用，不参与展示，
    /// 这些场景下按钮一直保持之前"去掉"的状态，见 ModVersionGrouping.Group 的注释。</summary>
    private List<InlineVersionEntry> _flatEntries = new();

    /// <summary>最近一次 ShowGroups 传入的完整分组列表（未按版本筛选），供 Tab 切换时本地
    /// 重新筛选用，不需要重新分组/重新请求网络。</summary>
    private List<VersionGroup> _allGroups = new();

    /// <summary>展开状态：调用方在拿到分组数据后调用这个方法一次性填充展示。
    /// 跟旧版 ToggleModExpandAsync/ToggleResourceExpandAsync 里"填充 Groups"是同一批数据，
    /// 只是现在不需要"展开/收起"这一层，页面本身就是详情页，进来就直接显示。</summary>
    public void ShowGroups(IEnumerable<VersionGroup> groups)
    {
        _allGroups = groups.ToList();
        // Mod 场景下不直接用调用方传入的分组（那份是按 ModVersionGrouping 默认 includePreview:true
        // 算出来的，跟"预览版默认隐藏"的按钮初始状态对不上），改成用 SetFlatEntries 存下来的扁平列表
        // 按当前 _showPreview 状态本地重新分组，这样按钮切换时不用再向 Modrinth/CurseForge 重新请求。
        if (_isMod) RebuildModGroups();
        BuildVersionFilterTabs();
        ApplyVersionFilter(_selectedVersionTag);
        LoadingText.Visibility = Visibility.Collapsed;
        UpdatePreviewToggleVisibility();
    }

    /// <summary>Mod 场景专用：按 _showPreview 状态从 _flatEntries 重新分组，覆盖 _allGroups。
    /// _flatEntries 为空（还没调用过 SetFlatEntries，或这个 mod 本来就没有任何版本）时不做任何事，
    /// 保留 ShowGroups 一开始赋的值，避免误把"还没数据"当成"筛选后没数据"。</summary>
    private void RebuildModGroups()
    {
        if (_flatEntries.Count == 0) return;

        var filtered = ModVersionGrouping.Group(_flatEntries, _showPreview);

        // 兜底：某个 mod 的版本类型标注本身不规范，导致"只显示正式版"把全部版本都过滤掉时，
        // 直接显示会变成详情页"没有找到匹配的版本"，比预览版本身更容易误导用户以为这个 mod
        // 根本没有能装的版本——这正是 ModVersionGrouping.Group 类注释里提到的"全军覆没"场景。
        // 与其让用户看着空列表去猜"是不是要点一下显示预览版"，不如这种情况下直接退回显示全部。
        if (filtered.Count == 0) filtered = ModVersionGrouping.Group(_flatEntries, includePreview: true);

        if (filtered.Count > 0) filtered[0].IsExpanded = true;
        _allGroups = filtered;
    }

    /// <summary>按钮文案在"显示预览版"/"隐藏预览版"之间切换，只在 _isMod 且这批版本里确实存在
    /// 预览版时才显示按钮——没有预览版的 mod 显示这个按钮也没有意义，还会让人以为点了会有效果。</summary>
    private void UpdatePreviewToggleVisibility()
    {
        var hasPreview = _isMod && _flatEntries.Any(e => e.IsPreview);
        PreviewToggleButton.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewToggleButton.Content = _showPreview ? "隐藏预览版" : "显示预览版";
    }

    private void TogglePreview_Click(object sender, RoutedEventArgs e)
    {
        _showPreview = !_showPreview;
        RebuildModGroups();
        BuildVersionFilterTabs();
        ApplyVersionFilter(_selectedVersionTag);
        UpdatePreviewToggleVisibility();
    }

    private string? _selectedVersionTag;

    /// <summary>从分组标题（形如 "NeoForge 1.21.11"、"Fabric 26.2"）里提取出跟截图一致的
    /// 版本筛选 Tab 文案：取标题里的游戏版本号部分，保留前两段（"1.21.11" → "1.21"，
    /// "26.2" 已经只有两段就原样保留），这样同一个大版本下的补丁号(1.21.10/1.21.11...)
    /// 会归到同一个 Tab 下，跟截图里 Tab 数量精简、不会每个补丁版本单独占一个 Tab 一致。</summary>
    private static string ExtractVersionTag(VersionGroup group)
    {
        var lastSpace = group.GroupTitle.LastIndexOf(' ');
        var gameVersion = lastSpace >= 0 ? group.GroupTitle[(lastSpace + 1)..] : group.GroupTitle;
        var parts = gameVersion.Split('.');
        return parts.Length <= 2 ? gameVersion : $"{parts[0]}.{parts[1]}";
    }

    /// <summary>按 _allGroups 里实际出现过的版本动态生成筛选 Tab（"全部" + 各版本，按
    /// 首次出现顺序排列，与 ModVersionGrouping 输出的组顺序一致，即新版本在前）。
    /// 只有一个版本（或没有分组）时整条筛选栏隐藏——只有一个选项的筛选没有意义，
    /// 还会白占一行空间。</summary>
    private void BuildVersionFilterTabs()
    {
        var tags = _allGroups.Select(ExtractVersionTag).Distinct().ToList();

        VersionFilterPanel.Children.Clear();

        if (tags.Count <= 1)
        {
            VersionFilterPanel.Visibility = Visibility.Collapsed;
            _selectedVersionTag = null;
            return;
        }

        VersionFilterPanel.Visibility = Visibility.Visible;

        // 之前选中的版本如果在新数据里已经不存在了（比如切换到另一个 mod 详情页），
        // 回退到"全部"，避免筛选栏看起来选中了一个但实际没有对应内容。
        if (_selectedVersionTag != null && !tags.Contains(_selectedVersionTag))
            _selectedVersionTag = null;

        var allTab = new RadioButton
        {
            Content = "全部",
            GroupName = "ModDetailVersionFilter",
            Style = (Style)FindResource("GameVersionTabRadioButton"),
            IsChecked = _selectedVersionTag == null,
        };
        allTab.Checked += VersionFilterTab_Checked;
        VersionFilterPanel.Children.Add(allTab);

        foreach (var tag in tags)
        {
            var tab = new RadioButton
            {
                Content = tag,
                Tag = tag,
                GroupName = "ModDetailVersionFilter",
                Style = (Style)FindResource("GameVersionTabRadioButton"),
                IsChecked = tag == _selectedVersionTag,
            };
            tab.Checked += VersionFilterTab_Checked;
            VersionFilterPanel.Children.Add(tab);
        }
    }

    private void VersionFilterTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        _selectedVersionTag = rb.Tag as string; // "全部" 没设 Tag，取出来是 null
        ApplyVersionFilter(_selectedVersionTag);
    }

    /// <summary>按选中的版本 Tag 筛选 _allGroups 后展示；tag 为 null 时展示全部分组
    /// （对应"全部" Tab）。</summary>
    private void ApplyVersionFilter(string? tag)
    {
        var filtered = tag == null ? _allGroups : _allGroups.Where(g => ExtractVersionTag(g) == tag).ToList();
        GroupsList.ItemsSource = filtered;
        NoResultText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>跟 ShowGroups 配套：额外传入这批版本对应的扁平列表。调用方（DownloadCenterPage）
    /// 在 LoadModVersionsAsync/LoadResourceVersionsAsync 里 item.Versions 填充完成后一并传进来。
    /// 不强制要求调用——不传时 _flatEntries 保持为空。</summary>
    public void SetFlatEntries(IEnumerable<InlineVersionEntry> entries)
    {
        _flatEntries = entries.ToList();
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
            MessageBoxDialog.ShowError(Loc.T("Str_Cs_Couldn_T_Open_Your_Browser_N", "无法打开浏览器：\n") + ex.Message, Loc.T("Str_Cs_Error", "错误"));
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
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Choose_Which_World_To_Install_Into_First", "请先选择要安装到哪个存档（数据包必须放进具体存档才会生效）。"),
                "提示");
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
