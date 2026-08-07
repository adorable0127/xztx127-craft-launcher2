using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 下载中心：仿 PCL 风格，左侧竖排分类（游戏版本 / Mod / 材质包 / 数据包 / 光影包 / 地图 / 我的收藏），
/// 右侧根据选中分类展示对应的下载面板。
/// - 「游戏版本」：复用现有 DownloadService，覆盖正式版/快照/远古版/愚人节版。
/// - 「Mod」：综合搜索 Modrinth + CurseForge（用户可切换来源），额外接入 MC百科做中文名搜索辅助
///   （MC百科没有下载能力，只提供百科页面链接，见 McModService 类注释）。
/// - 「材质包/数据包/光影包」：接入 Modrinth API 搜索与下载。
/// - 「地图」：接入 CurseForge API（Modrinth 没有对应分类）；需要用户在「设置」页配置好
///   CurseForge API Key 才能用，没配置时展示引导提示而不是报错。
/// - 「我的收藏」：展示收藏的游戏版本，收藏/下载复用「游戏版本」面板同一套逻辑。
/// </summary>
public partial class DownloadCenterPage : UserControl
{
    /// <summary>下载前询问保存目录：默认建议目录优先用"这次会话里上一次用户手动选过的目录"
    /// (_lastChosenDownloadDir)，会话里还没选过时才回退用调用方传入的 defaultDir（当前实例
    /// 对应的隔离目录）。用户也可以点"选择其他目录"调起系统资源管理器（Windows 原生文件夹
    /// 选择框，跟游戏版本/Mod站点的"另存为"体验一致，是社区启动器的标配功能）另存到任意位置。
    /// 无论用户最终是直接确认默认建议、还是重新选了别的目录，选定结果都会更新
    /// _lastChosenDownloadDir，供同一会话内下一次下载继续沿用。
    /// 返回 null 表示用户取消了本次下载（这种情况不更新会话记忆，维持上一次的选择）。</summary>
    private string? PromptSaveDirectory(string defaultDir, string itemName)
    {
        var suggestedDir = _lastChosenDownloadDir ?? defaultDir;

        var choice = MessageBoxDialog.ShowYesNoCancel(
            $"「{itemName}」将下载到：\n{suggestedDir}\n\n点击「是」使用该目录，点击「否」选择其他目录，点击「取消」放弃本次下载。",
            "选择保存位置");

        if (choice == XclMessageResult.Cancel) return null;
        if (choice == XclMessageResult.Yes)
        {
            _lastChosenDownloadDir = suggestedDir;
            return suggestedDir;
        }

        // 用户选"否"：调起 Windows 原生资源管理器文件夹选择框，起始位置用当前建议目录，
        // 而不是每次都回到默认目录——同一会话内连续下载多个东西到同一个自定义目录时，
        // 不用每次都重新点好几层文件夹。
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载保存目录",
            InitialDirectory = Directory.Exists(suggestedDir) ? suggestedDir : null
        };
        if (dialog.ShowDialog() != true) return null;

        _lastChosenDownloadDir = dialog.FolderName;
        return dialog.FolderName;
    }


    private readonly MainWindow _owner;
    private readonly ObservableCollection<VersionListItem> _online = new();
    private VersionManifestRoot? _manifestCache;

    private readonly ModrinthService _modrinth = new();
    private readonly FolderService _folderService = new();
    private readonly ModpackService _modpackService = new();
    /// <summary>材质包/数据包/光影包结果列表：原来只装 Modrinth 的 ModrinthSearchHit，
    /// 现在改成统一的 UnifiedResourceItem，才能同时展示 Modrinth + CurseForge 两个来源的结果
    /// （对应 CurseForgeService.SearchResourcesAsync 补上的搜索能力）。</summary>
    private readonly ObservableCollection<UnifiedResourceItem> _resources = new();
    private ModrinthResourceType _currentResourceType = ModrinthResourceType.ResourcePack;
    private ModSource _currentResourceSource = ModSource.Combined;

    private readonly ObservableCollection<VersionListItem> _favorites = new();
    /// <summary>"我的收藏"面板里的三个社区资源分组：Mod / (材质包+数据包+光影包合并展示) / 地图。
    /// 分开成三个集合而不是复用 _mods/_resources/_maps，是因为那三个字段是"搜索结果"，
    /// 会被下一次搜索整体清空重填；收藏内容需要独立维护，不应该被搜索行为影响。</summary>
    private readonly ObservableCollection<UnifiedModItem> _favoriteMods = new();
    private readonly ObservableCollection<UnifiedResourceItem> _favoriteResources = new();
    private readonly ObservableCollection<FavoriteItem> _favoriteMaps = new();

    private readonly CurseForgeKeyService _curseForgeKeyService = new();
    private CurseForgeService? _curseForge;
    private readonly ObservableCollection<CurseForgeMod> _maps = new();

    private ModSearchService? _modSearch;
    private readonly ObservableCollection<UnifiedModItem> _mods = new();
    private readonly ObservableCollection<McModSearchHit> _mcModHits = new();
    private ModSource _currentModSource = ModSource.Combined;

    /// <summary>
    /// 是否已经跑完构造函数里的 InitializeComponent()。
    ///
    /// 崩溃根因：XAML 里左侧分类栏的 CatVersion 写了 IsChecked="True"，这会在
    /// InitializeComponent() 解析 XAML 树的过程中同步触发 Checked 事件——但此时
    /// InitializeComponent() 还没解析到下面"右侧内容区"里的 VersionPanel 等节点，
    /// 对应的自动生成字段还是 null，Category_Checked 里一读就是 NullReferenceException。
    /// 之前"游戏版本"恰好是唯一默认选中项时没触发过这个问题，这次新增"我的收藏""地图"
    /// 分类后，生成的控件连接顺序变化，把这个一直存在的时序隐患暴露了出来。
    ///
    /// 这里不依赖"调整 XAML 节点顺序"这种脆弱的偶然规避，而是显式跳过初始化阶段的事件：
    /// 构造函数在 InitializeComponent() 之后手动把 _initialized 置 true 并调用一次
    /// Category_Checked，补上被跳过的那次面板显隐，行为与之前完全一致。
    /// </summary>
    private bool _initialized;

    /// <summary>
    /// 需求："如果在同一个会话中（没有切换页面，也没有关闭启动器、退出页面等），
    /// 上一次选择的文件夹默认就是下一个下 mod 下载的文件夹"。
    ///
    /// 这是纯粹的"这一次运行期间"的会话状态，不落盘、不写进 AppConfig——重新打开启动器后
    /// 应该回到"默认跟随当前选中实例"这个基准行为，而不是被上次关闭前的选择永久覆盖掉
    /// （用户可能下一次开着的是完全不同的一个实例/整合包，继续沿用旧会话选的目录反而更容易出错）。
    /// 只要这个 DownloadCenterPage 实例本身没有被销毁重建（也就是用户没有整个关掉/重开
    /// 启动器、且一直待在同一次运行里），这个字段就会一直保留最近一次用户在
    /// PromptSaveDirectory 里主动选择的目录，下次再下载 Mod/资源/整合包/地图时用它做默认建议。
    ///
    /// 为 null 表示这次会话里用户还没手动选过（也可能用户每次都直接确认了默认目录，
    /// 没有点"选择其他目录"），这种情况下继续走各自原有的"默认跟随当前实例"逻辑。
    /// </summary>
    private string? _lastChosenDownloadDir;

    /// <summary>
    /// 记录每个分类是否已经自动加载过一次数据。切分类回来时（比如 游戏版本→Mod→游戏版本）
    /// 已经拉取过的数据没必要重新请求一次网络——用户没改任何筛选条件，列表内容不会变，
    /// 只是徒增等待和流量；只有"第一次进入该分类"才需要自动拉取。
    /// 手动点击各面板的"手动刷新/搜索"按钮，或改动筛选条件，会绕开这个标记强制重新请求。
    /// </summary>
    private readonly HashSet<string> _autoLoadedCategories = new();

    /// <summary>
    /// 修复"在版本框输入版本后，进入模组下载页面不会自动匹配到该版本"的 bug：
    /// Mod/材质包(资源)/地图 三个分类各自有一个独立的游戏版本输入框，互相之间完全不同步。
    /// 用共享字段记录"用户最后一次手动填的版本号"，切换分类时同步到目标分类的版本框
    /// （仅当目标框为空时才填，不覆盖用户已有输入）。
    /// </summary>
    private string? _lastTypedGameVersion;

    private void SyncGameVersionBox(TextBox box)
    {
        if (string.IsNullOrWhiteSpace(box.Text) && !string.IsNullOrWhiteSpace(_lastTypedGameVersion))
            box.Text = _lastTypedGameVersion;
    }

    /// <summary>
    /// 筛选条件(搜索框文字/游戏版本号/加载器下拉框)变化时的统一防抖计时器：
    /// 用户连续打字时只在停顿 500ms 后真正发起一次网络请求，避免每敲一个字都请求一次接口。
    /// 同一时刻只会有一个筛选面板处于焦点，所以一个计时器 + 一个"待执行动作"字段就够用，
    /// 不需要给每个面板各自维护一份。
    /// </summary>
    private readonly DispatcherTimer _debounceTimer;
    private Action? _pendingDebouncedAction;

    /// <summary>
    /// 竞态保护：自动触发的搜索现在可能被连续、快速地发起（打字防抖后又切换分类等），
    /// 网络请求的返回顺序不保证和发出顺序一致——例如"a" "ap" "app" 三次搜索里，"a" 的请求
    /// 因为网络抖动比 "app" 后返回，如果不加保护，界面最终会错误地停留在 "a" 的搜索结果上。
    /// 每次真正发起请求前给对应序号自增并记录"这次发起时的序号"，写回结果前比对序号是否还是
    /// 最新的一次，不是最新的就丢弃这次结果，不更新界面。三个搜索面板（Mod/资源/地图）各自独立计数。
    /// </summary>
    private int _modSearchSeq;
    private int _resourceSearchSeq;
    private int _mapSearchSeq;

    /// <summary>
    /// 四个面板（Mod / 资源包 / 地图 / 游戏版本）各自独立的当前页码，从 0 开始，页大小固定 20。
    /// Mod/资源包/地图三个走真 API 分页（offset = pageIndex * PageSize，直接传给
    /// ModSearchService/CurseForgeService）；游戏版本面板数据本身是本地缓存的整份 manifest，
    /// 分页是纯本地 Skip/Take，不发网络请求，见 ApplyVersionFilter。
    /// </summary>
    private const int PageSize = 20;
    private int _modPageIndex;
    private int _resourcePageIndex;
    private int _mapPageIndex;
    private int _versionPageIndex;

    /// <summary>整页详情组件显示/收起：显示时不隐藏底下的 DockPanel 各面板——它们的筛选条件/
    /// 分页页码天然保留，回退直接露出原样，不需要额外保存/恢复状态。</summary>
    private void ShowDetail(ModDetailPage page)
    {
        DetailHost.Content = page;
        DetailHost.Visibility = Visibility.Visible;
    }

    private void HideDetail()
    {
        DetailHost.Visibility = Visibility.Collapsed;
        DetailHost.Content = null;
    }

    public DownloadCenterPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        OnlineListBox.ItemsSource = _online;
        ResourceListBox.ItemsSource = _resources;
        FavoritesListBox.ItemsSource = _favorites;
        FavoriteModsListBox.ItemsSource = _favoriteMods;
        FavoriteResourcesListBox.ItemsSource = _favoriteResources;
        FavoriteMapsListBox.ItemsSource = _favoriteMaps;
        MapListBox.ItemsSource = _maps;
        ModListBox.ItemsSource = _mods;
        McModListBox.ItemsSource = _mcModHits;

        // 「显示预览版资源」勾选框：从配置恢复上次的选择，并同步给资源详情页做默认值。
        InitPreviewToggleFromConfig();

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            var action = _pendingDebouncedAction;
            _pendingDebouncedAction = null;
            action?.Invoke();
        };

        _initialized = true;
        Category_Checked(CatVersion, new RoutedEventArgs()); // 补上初始化阶段被跳过的那次面板显隐
    }

    /// <summary>
    /// 重置防抖计时器并把 action 设为"停顿后要执行的动作"，用于筛选条件变化时的自动重新请求。
    ///
    /// 崩溃根因：XAML 里筛选相关控件(游戏版本分类下拉框等)的默认选中项/默认值，会在
    /// InitializeComponent() 解析阶段同步触发 SelectionChanged/TextChanged，从而调用到这里——
    /// 但此时构造函数还没走到 "_debounceTimer = new DispatcherTimer(...)" 那一行，字段还是
    /// null，Stop() 直接空引用。同 Category_Checked 的时序问题，这里用同一个 _initialized
    /// 标记短路跳过，构造函数末尾会补跑一次真正需要的动作。
    /// </summary>
    private void Debounce(Action action)
    {
        if (!_initialized || _debounceTimer == null) return;
        _pendingDebouncedAction = action;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>
    /// 打开超级"一键开始游戏"向导（Round 12 补完，见 QuickStartWizardWindow）。放在下载中心顶部
    /// 而不是首页磁贴，理由见 HANDOFF-ROUND11.md：首页 3x2 磁贴布局已经排满，不适合再塞第 7 个。
    /// </summary>
    private void QuickStartWizard_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new QuickStartWizardWindow(_owner);
        wizard.ShowDialog();
    }

    /// <summary>本地 Mod 管理入口：首页磁贴改版后（Mod 管理磁贴换成了"一键开始游戏"），
    /// ModManagerPage 需要一个可达的入口，放在下载中心顶部，紧邻"一键开始游戏"。
    /// 直接复用 MainWindow.NavigateToModManager，不重复实现导航逻辑。</summary>
    private void OpenModManager_Click(object sender, RoutedEventArgs e) => _owner.NavigateToModManager();

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // InitializeComponent() 过程中触发的事件：控件树还没解析完，直接跳过
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag as string ?? "";

        VersionPanel.Visibility = Visibility.Collapsed;
        ModPanel.Visibility = Visibility.Collapsed;
        ResourcePanel.Visibility = Visibility.Collapsed;
        MapPanel.Visibility = Visibility.Collapsed;
        MapKeyMissingPanel.Visibility = Visibility.Collapsed;
        FavoritesPanel.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "version":
                VersionPanel.Visibility = Visibility.Visible;
                if (_autoLoadedCategories.Add("version")) _ = FetchOnlineAsync();
                break;
            case "mod":
                ModPanel.Visibility = Visibility.Visible;
                {
                    var hadNoText = string.IsNullOrWhiteSpace(ModGameVersionBox.Text);
                    SyncGameVersionBox(ModGameVersionBox);
                    var justFilled = hadNoText && !string.IsNullOrWhiteSpace(ModGameVersionBox.Text);
                    if (_autoLoadedCategories.Add("mod") || justFilled) _ = RunModSearchAsync();
                }
                break;
            case "resourcepack":
                SwitchResourceCategory(ModrinthResourceType.ResourcePack, "材质包下载");
                break;
            case "datapack":
                SwitchResourceCategory(ModrinthResourceType.DataPack, "数据包下载");
                break;
            case "shader":
                SwitchResourceCategory(ModrinthResourceType.Shader, "光影包下载");
                break;
            case "modpack":
                SwitchResourceCategory(ModrinthResourceType.Modpack, "整合包下载");
                break;
            case "map":
                if (_curseForgeKeyService.HasKey())
                {
                    MapPanel.Visibility = Visibility.Visible;
                    var hadNoText = string.IsNullOrWhiteSpace(MapGameVersionBox.Text);
                    SyncGameVersionBox(MapGameVersionBox);
                    var justFilled = hadNoText && !string.IsNullOrWhiteSpace(MapGameVersionBox.Text);
                    if (_autoLoadedCategories.Add("map") || justFilled) _ = RunMapSearchAsync();
                }
                else
                {
                    MapKeyMissingPanel.Visibility = Visibility.Visible;
                }
                break;
            case "favorites":
                FavoritesPanel.Visibility = Visibility.Visible;
                RefreshFavorites();
                break;
        }
    }

    private void GoToSettingsForKey_Click(object sender, RoutedEventArgs e) => _owner.NavigateToSettings();

    /// <summary>
    /// 供其他页面跳转时调用：切到「Mod」分类，把搜索框填成给定关键词，并立即触发一次搜索。
    /// 目前用于「联机」页"一键搜索安装红石联机模组"的入口——不重新实现下载逻辑，
    /// 直接复用这里现成的 Modrinth 综合搜索 + 卡片内联展开下载。
    /// </summary>
    public void SelectModCategoryAndSearch(string keyword)
    {
        CatMod.IsChecked = true; // 触发 Category_Checked，切换到 Mod 面板
        _autoLoadedCategories.Add("mod"); // 标记已加载，避免 Category_Checked 里再触发一次空关键词搜索
        ModSearchBox.Text = keyword;
        _modPageIndex = 0;
        _ = RunModSearchAsync(showHints: true);
    }

    public void SelectResourceCategoryAndSearch(ModrinthResourceType type, string keyword)
    {
        var radio = type switch
        {
            ModrinthResourceType.DataPack => CatDataPack,
            ModrinthResourceType.Shader => CatShader,
            _ => CatResourcePack
        };
        // 关键词要先填好，再触发 RadioButton 选中——IsChecked=true 会同步触发
        // Category_Checked -> SwitchResourceCategory 并可能立即发起一次搜索，
        // 顺序反了会先拿旧关键词/空关键词搜一次、白白多打一次请求。
        ResourceSearchBox.Text = keyword;
        _resourcePageIndex = 0;
        radio.IsChecked = true;
        // 保险起见再显式跑一次，覆盖"类型没变、SwitchResourceCategory 内部判断命中
        // _lastLoadedResourceType 未变而跳过搜索"这种边界情况（比如收藏面板里连续两次
        // 点同一类型的不同资源"去下载"）。
        _ = RunResourceSearchAsync(showEmptyHint: true);
    }

    /// <summary>供"我的收藏"面板里"地图"卡片"去下载"按钮调用，逻辑跟
    /// SelectResourceCategoryAndSearch/SelectModCategoryAndSearch 对称。</summary>
    public void SelectMapCategoryAndSearch(string keyword)
    {
        MapSearchBox.Text = keyword;
        _mapPageIndex = 0;
        CatMap.IsChecked = true; // 触发 Category_Checked，没有 Key 时会自动展示"未配置 Key"提示面板
        _autoLoadedCategories.Add("map");
        _ = RunMapSearchAsync(showHints: true);
    }

    /// <summary>
    /// 切换材质包/数据包/光影包分类的实际执行逻辑。
    ///
    /// 修复"社区资源分类之间来回切换时刷新不及时"的 bug：这三个分类共用同一个 ResourcePanel/
    /// ResourceListBox/_resources 集合，只靠 _currentResourceType 字段区分"当前展示的是哪一种"。
    /// 之前用 _autoLoadedCategories（每个分类 tag 各自一条记录）判断"是否已经自动加载过"，
    /// 但这三个分类实际上共享同一份列表数据——例如先点"材质包"（记录 resourcepack 已加载，
    /// 列表被填充成材质包结果），再点"数据包"（记录 datapack 已加载，列表被替换成数据包结果），
    /// 这时如果用户点回"材质包"，_autoLoadedCategories 里 "resourcepack" 已经记录过加载，
    /// Add 返回 false，不会重新拉取，界面就会残留数据包分类的旧结果（或者切换瞬间残留的旧数据），
    /// 也就是"来回切换时刷新不及时"。
    ///
    /// 现在改成：只要"要切换到的类型"跟"上一次实际发起过请求并成功展示的类型"不一致，就强制
    /// 重新拉取，不再依赖按分类 tag 的一次性标记。同一个类型重复点击（比如从材质包切到数据包
    /// 又切回材质包又再切回材质包）才会命中"不需要重新请求"的优化路径。
    /// </summary>
    private ModrinthResourceType? _lastLoadedResourceType;

    private void SwitchResourceCategory(ModrinthResourceType type, string title)
    {
        _currentResourceType = type;
        ResourcePanelTitle.Text = title;
        ResourcePanel.Visibility = Visibility.Visible;

        // 同步面板内"类型"下拉框的选中项，让左侧导航栏切换分类时，面板内的下拉框显示保持一致，
        // 不会出现"标题写着数据包下载，类型下拉框却还停在材质包"这种不一致。用 _syncingResourceType
        // 短路 ResourceTypeCombo_SelectionChanged 里会再次触发的 SwitchResourceCategory，避免死循环。
        _syncingResourceType = true;
        foreach (ComboBoxItem candidate in ResourceTypeCombo.Items)
        {
            if (candidate.Tag is string tag && Enum.TryParse<ModrinthResourceType>(tag, out var candidateType) && candidateType == type)
            {
                ResourceTypeCombo.SelectedItem = candidate;
                break;
            }
        }
        _syncingResourceType = false;

        var hadNoText = string.IsNullOrWhiteSpace(ResourceGameVersionBox.Text);
        SyncGameVersionBox(ResourceGameVersionBox);
        var justFilled = hadNoText && !string.IsNullOrWhiteSpace(ResourceGameVersionBox.Text);

        if (_lastLoadedResourceType != type || justFilled)
        {
            _lastLoadedResourceType = type;
            _resourcePageIndex = 0;
            _ = RunResourceSearchAsync();
        }
    }

    private bool _syncingResourceType;

    /// <summary>面板内"类型"下拉框直接切换材质包/数据包/光影包，不需要用户回到左侧导航栏。
    /// 顺带把左侧对应的 RadioButton 也勾上，保持两处入口的选中状态一致。</summary>
    private void ResourceTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingResourceType) return;
        if (ResourceTypeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!Enum.TryParse<ModrinthResourceType>(tag, out var type)) return;

        switch (type)
        {
            case ModrinthResourceType.ResourcePack: CatResourcePack.IsChecked = true; break;
            case ModrinthResourceType.DataPack: CatDataPack.IsChecked = true; break;
            case ModrinthResourceType.Shader: CatShader.IsChecked = true; break;
            case ModrinthResourceType.Modpack: CatModpack.IsChecked = true; break;
        }
    }

    /// <summary>"重置条件"：清空名称/游戏版本输入框，重新按当前类型搜索一次（浏览热门资源）。
    /// 不重置"类型"和"来源"——这两个是这次要看哪一类资源的主要选择，重置筛选条件不应该
    /// 连带把用户特意选的类型也弹回默认值。</summary>
    private void ResourceFilterReset_Click(object sender, RoutedEventArgs e)
    {
        ResourceSearchBox.Text = "";
        ResourceGameVersionBox.Text = "";
        _resourcePageIndex = 0;
        _debounceTimer.Stop();
        _ = RunResourceSearchAsync(showEmptyHint: true);
    }

    /// <summary>
    /// 刷新"我的收藏"列表。收藏只存了版本 ID，实际条目要从 _manifestCache（游戏版本清单缓存）里查出来——
    /// 所以如果用户还没在"游戏版本"分类点过"刷新版本列表"，这里没有清单可查，只能提示先去刷新一次，
    /// 而不是在收藏页里重新发一次网络请求（收藏页本身不需要联网，除非缓存是空的）。
    /// </summary>
    /// <summary>
    /// 刷新"我的收藏"里的四个分组：游戏版本 / Mod / 材质包+数据包+光影包 / 地图。
    /// 版本收藏跟之前一样要靠 _manifestCache 反查条目详情；社区资源收藏在收藏时已经存了
    /// 一份展示快照（标题/作者/图标/下载量），不需要联网就能直接展示，只有真正点"下载安装"
    /// 才会按 SourceId 重新向 Modrinth/CurseForge 查询最新版本列表。
    /// </summary>
    private void RefreshFavorites()
    {
        _favorites.Clear();
        _favoriteMods.Clear();
        _favoriteResources.Clear();
        _favoriteMaps.Clear();

        var items = _owner.ConfigService.Config.FavoriteItems;
        var versionItems = items.Where(f => f.Type == FavoriteItemType.Version).ToList();

        if (_manifestCache != null)
        {
            foreach (var f in versionItems)
            {
                var entry = _manifestCache.Versions.FirstOrDefault(v => v.Id == f.SourceId);
                if (entry != null) _favorites.Add(new VersionListItem(entry, true));
            }
        }

        foreach (var f in items.Where(f => f.Type == FavoriteItemType.Mod))
            _favoriteMods.Add(FavoriteItemToModDisplay(f));

        foreach (var f in items.Where(f => f.Type is FavoriteItemType.ResourcePack or FavoriteItemType.DataPack or FavoriteItemType.Shader))
            _favoriteResources.Add(FavoriteItemToResourceDisplay(f));

        foreach (var f in items.Where(f => f.Type == FavoriteItemType.Map))
            _favoriteMaps.Add(f);

        var showEmpty = items.Count == 0;
        var showManifestHint = versionItems.Count > 0 && _manifestCache == null;

        FavoritesEmptyHint.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        FavoritesEmptyHint.Text = showManifestHint
            ? "已收藏版本，但还没拉取过版本清单，请先去「游戏版本」分类点一次「刷新版本列表」。"
            : "还没有收藏任何内容，去「游戏版本」/「Mod」/「材质包」等分类点击 ☆ 收藏 试试。";
        if (showManifestHint) FavoritesEmptyHint.Visibility = Visibility.Visible;

        FavoritesListBox.Visibility = _favorites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FavoriteModsListBox.Visibility = _favoriteMods.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FavoriteResourcesListBox.Visibility = _favoriteResources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FavoriteMapsListBox.Visibility = _favoriteMaps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        FavoriteModsHeader.Visibility = _favoriteMods.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FavoriteResourcesHeader.Visibility = _favoriteResources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FavoriteMapsHeader.Visibility = _favoriteMaps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>把一条 FavoriteItem(Mod) 还原成 UnifiedModItem 展示用（用收藏时存的快照字段，
    /// 不重新联网查询）。RawItem 留空——"我的收藏"面板的 Mod 卡片不支持直接展开下载版本，
    /// 只做展示+取消收藏，要下载的话引导用户去「Mod」分类重新搜索一次（真实文件列表可能已更新）。</summary>
    private static UnifiedModItem FavoriteItemToModDisplay(FavoriteItem f) => new()
    {
        Source = f.Source,
        SourceId = f.SourceId,
        Title = f.Title,
        Description = f.Description,
        Author = f.Author,
        IconUrl = f.IconUrl,
        Downloads = f.Downloads,
        IsFavorite = true
    };

    private static UnifiedResourceItem FavoriteItemToResourceDisplay(FavoriteItem f) => new()
    {
        Source = f.Source,
        SourceId = f.SourceId,
        Title = f.Title,
        Description = f.Description,
        Author = f.Author,
        IconUrl = f.IconUrl,
        Downloads = f.Downloads,
        FavoriteType = f.Type,
        IsFavorite = true
    };

    /// <summary>
    /// XAML 里 SourceCombo 的顺序是 索引0=官方源、索引1=BMCLAPI镜像源（默认选中官方源，见 AppConfig.Source 注释）。
    /// 切换来源后立即自动重新拉取一次版本列表，不需要用户再手动点按钮。
    /// </summary>
    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (_owner?.ConfigService?.Config == null) return; // 控件初始化早于字段赋值时的防御
        _owner.ConfigService.Config.Source = SourceCombo.SelectedIndex == 1 ? DownloadSource.BMCLAPI : DownloadSource.Official;
        _owner.ConfigService.Save();
        _ = FetchOnlineAsync();
    }

    private async void FetchOnline_Click(object sender, RoutedEventArgs e) => await FetchOnlineAsync();

    /// <summary>
    /// 拉取（或重新拉取）版本清单。由三处触发："游戏版本"分类第一次打开时自动调用一次、
    /// 切换来源下拉框时自动调用、用户点"手动刷新"按钮时调用——三处共用同一份逻辑。
    /// </summary>
    private async Task FetchOnlineAsync()
    {
        try
        {
            var svc = new DownloadService(_owner.ConfigService.Config.Source);
            _manifestCache = await svc.GetVersionManifestAsync();
            ApplyVersionFilter();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Version_List_This_Is_", "获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
    }

    /// <summary>
    /// 搜索框/分类下拉框内容变化：走防抖，停顿 500ms 后才真正重新过滤，
    /// 避免用户每敲一个字符就重新渲染一次列表。过滤是纯本地操作（基于已缓存的 _manifestCache），
    /// 不发网络请求，防抖主要是为了避免频繁重建 ListBox 内容造成的界面抖动。
    /// </summary>
    private void VersionFilter_Changed(object sender, RoutedEventArgs e)
    {
        _versionPageIndex = 0; // 筛选条件变了，之前翻到的页码对新结果集没有意义，回到第一页
        Debounce(ApplyVersionFilter);
    }

    private void VersionPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_versionPageIndex <= 0) return;
        _versionPageIndex--;
        ApplyVersionFilter();
    }

    private void VersionNextPage_Click(object sender, RoutedEventArgs e)
    {
        _versionPageIndex++;
        ApplyVersionFilter();
    }

    /// <summary>根据分类下拉框(正式版/快照/远古版/愚人节版)和搜索框内容过滤已缓存的版本清单，
    /// 再按 _versionPageIndex 做纯本地分页（数据本身已经整份缓存在 _manifestCache，不需要、
    /// 也不能对 manifest 做真 API 分页——manifest 接口本身不支持分页参数）。</summary>
    private void ApplyVersionFilter()
    {
        if (_manifestCache == null) return;

        var selectedTag = (VersionTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Release";
        if (!Enum.TryParse<VersionCategory>(selectedTag, out var category)) category = VersionCategory.Release;

        var keyword = VersionSearchBox.Text?.Trim() ?? "";

        var filtered = _manifestCache.Versions
            .Where(v => v.GetCategory() == category)
            .Where(v => keyword.Length == 0 || v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var totalPages = filtered.Count == 0 ? 0 : (int)Math.Ceiling(filtered.Count / (double)PageSize);
        if (_versionPageIndex < 0) _versionPageIndex = 0;
        if (totalPages > 0 && _versionPageIndex > totalPages - 1) _versionPageIndex = totalPages - 1;

        var page = filtered.Skip(_versionPageIndex * PageSize).Take(PageSize);

        _online.Clear();
        var favoriteIds = _owner.ConfigService.Config.FavoriteItems
            .Where(f => f.Type == FavoriteItemType.Version).Select(f => f.SourceId).ToHashSet();
        foreach (var v in page) _online.Add(new VersionListItem(v, favoriteIds.Contains(v.Id)));

        VersionPageSummaryText.Text = totalPages > 0 ? $"第 {_versionPageIndex + 1} 页 / 共 {totalPages} 页" : "第 1 页";
        VersionPrevPageButton.IsEnabled = _versionPageIndex > 0;
        VersionNextPageButton.IsEnabled = _versionPageIndex < totalPages - 1;
    }

    private void OnlineListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>
    /// 点击某个版本的"下载安装"按钮：先弹出 LoaderChoiceDialog 让用户一步选择
    /// "原版/Fabric/Forge/NeoForge"，不再依赖点击前先切换页面顶部的加载器筛选行——
    /// 见 LoaderChoiceDialog 类注释，这是本轮解决"下载生态割裂"的核心改动。
    /// 用户取消选择窗口则整个安装动作中止，不做任何事。
    /// </summary>
    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VersionListItem item) return;
        var entry = item.Entry;

        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();

        if (folder == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Add_A_Minecraft_Folder_On_The_Versions_P", "请先去「版本选择」页添加一个 .minecraft 文件夹。"));
            return;
        }

        var choiceDialog = new LoaderChoiceDialog(entry.Id);
        if (OverlayDialogService.ShowModal(choiceDialog) != true) return; // 用户点了"取消"

        // 选了 Fabric/Forge/NeoForge：跳转到 InstallClientLoaderWindow，预选好加载器类型 + 这一行
        // 的 MC 版本号，复用现成的三级联动安装逻辑，不在这里重新实现一遍加载器安装。
        if (choiceDialog.SelectedLoader != ServerCoreType.Vanilla)
        {
            var loaderWindow = new InstallClientLoaderWindow(_owner, choiceDialog.SelectedLoader, entry.Id);
            if (loaderWindow.ShowDialog() == true && loaderWindow.InstalledVersionId != null)
            {
                _owner.EnsureVisibleForDialog();
                // 需求："在安装完一个版本后，默认选择到那个版本"。之前这里只弹一句"可以在
                // 「版本选择」页选中它"，等于把"选中"这个本该顺手做掉的动作丢给用户自己再跑一趟
                // 页面手动点——这个安装动作本身发生在"当前选中的文件夹"下，装完之后没有理由
                // 不直接把它设为选中版本，跟"新建了什么就默认用它"是同一种直觉。
                _owner.ConfigService.Config.SelectedVersionId = loaderWindow.InstalledVersionId;
                _owner.ConfigService.Save();
                _owner.RefreshSidebar();
                MessageBoxDialog.ShowSuccess($"版本「{loaderWindow.InstalledVersionId}」安装完成，已自动选中。");
            }
            return;
        }

        var progressWin = new ProgressDialog($"正在安装 {entry.Id} ...");
        progressWin.Show();
        try
        {
            // 用 CreateFromConfig 而不是直接 new：这里是真正会下载大批 libraries/assets 文件的
            // 场景，应该按设置页里的"多线程下载/限速/智能限速"配置来，而不是永远单线程不限速。
            using var svc = DownloadService.CreateFromConfig(_owner.ConfigService.Config);
            await svc.InstallVersionAsync(folder.Path, entry, progressWin.Progress);
            _owner.EnsureVisibleForDialog();
            // 同上：原版直装完成后同样默认选中这个刚装好的版本，不需要用户再跑去「版本选择」页手动点一次。
            // 只有安装的目标文件夹正好是当前选中文件夹时才自动切选中版本——如果用户是往一个
            // 未被选中的文件夹里装版本（folder 变量取的是"当前选中文件夹"回退到"第一个文件夹"，
            // 两者可能不是同一个），贸然把 SelectedVersionId 改掉会打乱用户当前正在用的另一个文件夹
            // 下的版本选择，超出"装完自动选中"这个需求本身的意图。
            if (folder.Path == _owner.ConfigService.Config.SelectedFolderPath)
            {
                _owner.ConfigService.Config.SelectedVersionId = entry.Id;
                _owner.ConfigService.Save();
                _owner.RefreshSidebar();
            }
            MessageBoxDialog.ShowSuccess($"{entry.Id} 安装完成" + (folder.Path == _owner.ConfigService.Config.SelectedFolderPath ? "，已自动选中。" : "！"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Installation_Failed_This_Could_Be_A_Netw", "安装失败，可能是网络连接问题、下载源暂时不可用，或安装文件已损坏，请检查网络后重试。"), $"[安装失败] {ex}", "安装失败");
        }
        finally
        {
            progressWin.Close();
        }
    }

    /// <summary>右键菜单"在中文 Minecraft Wiki 中查看"（下载中心的"游戏版本"列表）：
    /// 复用 VersionSelectPage.OpenMinecraftWiki 的跳转逻辑，两处右键菜单行为保持一致。</summary>
    private void ViewVersionOnWiki_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: VersionListItem item }) return;
        VersionSelectPage.OpenMinecraftWiki(item.Id);
    }

    /// <summary>收藏/取消收藏一个游戏版本，写入 AppConfig.FavoriteItems 并持久化。</summary>
    private void FavoriteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VersionListItem item) return;
        var cfg = _owner.ConfigService.Config;

        var existing = cfg.FavoriteItems.FirstOrDefault(f => f.MatchesKey(FavoriteItemType.Version, item.Entry.Id, ModSource.Combined));
        if (existing != null)
        {
            cfg.FavoriteItems.Remove(existing);
            item.IsFavorite = false;
        }
        else
        {
            cfg.FavoriteItems.Add(new FavoriteItem { Type = FavoriteItemType.Version, Source = ModSource.Combined, SourceId = item.Entry.Id });
            item.IsFavorite = true;
        }
        _owner.ConfigService.Save();

        // 版本列表(OnlineListBox)和"我的收藏"面板可能同时展示同一个版本对应的两个不同
        // VersionListItem 实例(各自 ObservableCollection 独立包装)，切换其中一处的收藏状态后，
        // 另一处的按钮文案/状态需要跟着同步，不能只改点击的这一个实例。
        SyncFavoriteVersionState(item.Entry.Id, item.IsFavorite);

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }

    /// <summary>把某个版本 ID 的收藏状态同步到 _online 和 _favorites 里所有匹配的 VersionListItem 实例上。</summary>
    private void SyncFavoriteVersionState(string versionId, bool isFavorite)
    {
        foreach (var v in _online.Where(v => v.Id == versionId)) v.IsFavorite = isFavorite;
        foreach (var v in _favorites.Where(v => v.Id == versionId)) v.IsFavorite = isFavorite;
    }

    /// <summary>收藏/取消收藏一个 Mod（Modrinth/CurseForge 综合搜索结果里的卡片）。
    /// 收藏时把展示字段(标题/作者/图标/下载量)拷贝一份快照存进 FavoriteItem，
    /// 见 FavoriteItem 类注释——这样"我的收藏"面板不需要联网就能展示。</summary>
    private void FavoriteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not UnifiedModItem item) return;
        ToggleCommunityFavorite(FavoriteItemType.Mod, item.Source, item.SourceId,
            item.Title, item.Description, item.Author, item.IconUrl, item.Downloads, out var nowFavorite);

        // 同步所有集合里同一个 (Source, SourceId) 对应的卡片实例。
        foreach (var m in _mods.Where(m => m.Source == item.Source && m.SourceId == item.SourceId)) m.IsFavorite = nowFavorite;
        foreach (var m in _favoriteMods.Where(m => m.Source == item.Source && m.SourceId == item.SourceId)) m.IsFavorite = nowFavorite;

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }

    /// <summary>收藏/取消收藏一条材质包/数据包/光影包资源。</summary>
    private void FavoriteResource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not UnifiedResourceItem item) return;
        ToggleCommunityFavorite(item.FavoriteType, item.Source, item.SourceId,
            item.Title, item.Description, item.Author, item.IconUrl, item.Downloads, out var nowFavorite);

        foreach (var r in _resources.Where(r => r.Source == item.Source && r.SourceId == item.SourceId)) r.IsFavorite = nowFavorite;
        foreach (var r in _favoriteResources.Where(r => r.Source == item.Source && r.SourceId == item.SourceId)) r.IsFavorite = nowFavorite;

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }

    /// <summary>收藏/取消收藏一张地图（CurseForge 搜索结果）。</summary>
    private void FavoriteMap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CurseForgeMod item) return;
        ToggleCommunityFavorite(FavoriteItemType.Map, ModSource.CurseForge, item.Id.ToString(),
            item.Name, item.Summary, item.AuthorsDisplay, item.Logo?.ThumbnailUrl, item.DownloadCount, out var nowFavorite);

        item.IsFavorite = nowFavorite;
        foreach (var m in _maps.Where(m => m.Id == item.Id)) m.IsFavorite = nowFavorite;

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }

    /// <summary>"我的收藏"面板里点某条社区资源的"取消收藏"：直接按 FavoriteItem 记录移除，
    /// 不需要反查 _mods/_resources/_maps（那些是搜索结果集合，收藏面板打开时未必有对应搜索结果）。</summary>
    private void UnfavoriteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        FavoriteItemType type; ModSource source; string sourceId;
        switch (btn.Tag)
        {
            case UnifiedModItem m: type = FavoriteItemType.Mod; source = m.Source; sourceId = m.SourceId; break;
            case UnifiedResourceItem r: type = r.FavoriteType; source = r.Source; sourceId = r.SourceId; break;
            case FavoriteItem f: type = f.Type; source = f.Source; sourceId = f.SourceId; break;
            default: return;
        }

        var cfg = _owner.ConfigService.Config;
        var existing = cfg.FavoriteItems.FirstOrDefault(x => x.MatchesKey(type, sourceId, source));
        if (existing != null)
        {
            cfg.FavoriteItems.Remove(existing);
            _owner.ConfigService.Save();
        }

        // 同步搜索结果集合里对应卡片的收藏按钮状态（如果用户之前搜索过同一条资源）。
        foreach (var m in _mods.Where(m => m.Source == source && m.SourceId == sourceId && type == FavoriteItemType.Mod)) m.IsFavorite = false;
        foreach (var r in _resources.Where(r => r.Source == source && r.SourceId == sourceId && r.FavoriteType == type)) r.IsFavorite = false;
        foreach (var mp in _maps.Where(mp => type == FavoriteItemType.Map && mp.Id.ToString() == sourceId)) mp.IsFavorite = false;

        RefreshFavorites();
    }

    /// <summary>收藏/取消收藏一条社区资源的公共逻辑：存在则移除(取消收藏)，不存在则新增并拷贝展示快照。</summary>
    private void ToggleCommunityFavorite(FavoriteItemType type, ModSource source, string sourceId,
        string title, string description, string author, string? iconUrl, long downloads, out bool nowFavorite)
    {
        var cfg = _owner.ConfigService.Config;
        var existing = cfg.FavoriteItems.FirstOrDefault(f => f.MatchesKey(type, sourceId, source));
        if (existing != null)
        {
            cfg.FavoriteItems.Remove(existing);
            nowFavorite = false;
        }
        else
        {
            cfg.FavoriteItems.Add(new FavoriteItem
            {
                Type = type,
                Source = source,
                SourceId = sourceId,
                Title = title,
                Description = description,
                Author = author,
                IconUrl = iconUrl,
                Downloads = downloads
            });
            nowFavorite = true;
        }
        _owner.ConfigService.Save();
    }

    private async void ResourceSearch_Click(object sender, RoutedEventArgs e) => await RunResourceSearchAsync(showEmptyHint: true);

    /// <summary>
    /// 版本快捷选择条点击：把按钮 Tag（版本号，""代表"全部"/清空）填进"版本"输入框。
    /// 不需要在这里自己再调一次搜索——TextBox.Text 赋值会触发 TextChanged，走原有的
    /// ResourceFilter_Changed → Debounce → RunResourceSearchAsync 那条路径，
    /// 跟用户手打版本号触发搜索是完全同一套逻辑，这里只是"代打字"。
    /// </summary>
    private void ResourceVersionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        ResourceGameVersionBox.Text = btn.Tag as string ?? "";
    }

    /// <summary>
    /// 需求："加入社区资源下载筛选预览版功能"。
    ///
    /// 勾选框改变时做三件事：
    /// 1) 写进配置并持久化（下次打开启动器还记得）；
    /// 2) 推给 ModDetailPage.DefaultShowPreview，让之后打开的任何资源详情页
    ///    直接就是对应的展开状态，不用进去再点一次；
    /// 3) 立刻重新搜一次，让当前列表马上反映新的筛选条件。
    ///
    /// 注意这个开关跟详情页里那个"显示/隐藏预览版"按钮控制的**不是同一层东西**：
    /// 这个是全局默认，详情页那个是当前这个资源的临时覆盖。两者故意分开，
    /// 这样用户可以"整体只看正式版，但某个特定 mod 我想看看它的 beta"。
    /// </summary>
    private void ResourceShowPreview_Changed(object sender, RoutedEventArgs e)
    {
        var show = ResourceShowPreviewCheck.IsChecked == true;

        _owner.ConfigService.Config.ShowPreviewVersions = show;
        try { _owner.ConfigService.Save(); } catch { /* 存不下不影响本次会话生效 */ }

        ModDetailPage.DefaultShowPreview = show;

        _resourcePageIndex = 0;
        _ = RunResourceSearchAsync();
    }

    /// <summary>把配置里的「显示预览版资源」同步到勾选框和 ModDetailPage 的默认值。
    /// 页面构造时调用一次，保证刷新/重开页面后勾选状态跟配置一致。</summary>
    private void InitPreviewToggleFromConfig()
    {
        var show = _owner.ConfigService.Config.ShowPreviewVersions;
        ResourceShowPreviewCheck.IsChecked = show;
        ModDetailPage.DefaultShowPreview = show;
    }

    /// <summary>
    /// 搜索框/游戏版本号输入变化：走防抖，不打断用户打字，也不在每次搜索为空时弹提示框打扰（
    /// 静默完成即可，只有用户主动点"手动刷新"按钮时才在无结果时弹提示，见 showEmptyHint 参数）。
    /// </summary>
    private void ResourceFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ResourceGameVersionBox.Text))
            _lastTypedGameVersion = ResourceGameVersionBox.Text.Trim();
        _resourcePageIndex = 0;
        Debounce(() => _ = RunResourceSearchAsync());
    }

    private void ResourcePrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_resourcePageIndex <= 0) return;
        _resourcePageIndex--;
        _ = RunResourceSearchAsync();
    }

    private void ResourceNextPage_Click(object sender, RoutedEventArgs e)
    {
        _resourcePageIndex++;
        _ = RunResourceSearchAsync();
    }

    /// <summary>
    /// 材质包/数据包/光影包三个分类共用同一个搜索方法（靠 _currentResourceType 区分接口参数）。
    /// 现在改用 ModSearchService.SearchResourcesAsync 综合查询 Modrinth + CurseForge（原来只查
    /// Modrinth，CurseForge 完全搜不到材质包/光影包/数据包，见 CurseForgeService 新增的
    /// SearchResourcesAsync）。由四处触发：对应分类第一次打开时自动调用一次（留空关键词=浏览热门）、
    /// 切换分类时自动调用、搜索框/游戏版本号变化后防抖自动调用、用户点"手动刷新"按钮时调用。
    /// </summary>
    private async Task RunResourceSearchAsync(bool showEmptyHint = false)
    {
        var seq = ++_resourceSearchSeq;
        try
        {
            var keyword = ResourceSearchBox.Text?.Trim() ?? "";
            var gameVersion = ResourceGameVersionBox.Text?.Trim();
            var outcome = await GetModSearch().SearchResourcesAsync(_currentResourceSource, _currentResourceType,
                keyword, string.IsNullOrEmpty(gameVersion) ? null : gameVersion, _resourcePageIndex, PageSize);

            if (seq != _resourceSearchSeq) return; // 期间已有更新的搜索发出，这次结果已过时，丢弃

            _resources.Clear();
            var showIcons = _owner.ConfigService.Config.ShowModIcons;
            var favoriteType = _currentResourceType switch
            {
                ModrinthResourceType.DataPack => FavoriteItemType.DataPack,
                ModrinthResourceType.Shader => FavoriteItemType.Shader,
                _ => FavoriteItemType.ResourcePack
            };
            var favorites = _owner.ConfigService.Config.FavoriteItems;
            foreach (var item in outcome.Items)
            {
                item.ShowIcon = showIcons;
                item.FavoriteType = favoriteType;
                item.IsFavorite = favorites.Any(f => f.MatchesKey(favoriteType, item.SourceId, item.Source));
                _resources.Add(item);
            }

            UpdatePageSummary(ResourcePageSummaryText, _resourcePageIndex, outcome.ModrinthTotal, outcome.CurseForgeTotal);
            ResourcePrevPageButton.IsEnabled = _resourcePageIndex > 0;
            ResourceNextPageButton.IsEnabled = HasMorePages(_resourcePageIndex, outcome.ModrinthTotal, outcome.CurseForgeTotal);

            if (showEmptyHint)
            {
                if (outcome.Items.Count == 0 && outcome.Warnings.Count == 0)
                    MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Nothing_Matched_Try_A_Different_Keyword", "没有找到匹配的资源，换个关键词试试。"));
                else if (outcome.Warnings.Count > 0)
                    MessageBoxDialog.ShowWarning(string.Join("\n", outcome.Warnings), Loc.T("Str_Cs_Some_Sources_Failed_To_Return_Results", "部分来源搜索失败"));
            }
        }
        catch (Exception ex)
        {
            if (seq != _resourceSearchSeq) return;
            if (showEmptyHint)
                ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Search_Failed_Most_Likely_A_Network_Prob", "搜索失败，可能是网络连接问题，请检查网络后重试。"), $"[搜索失败] {ex}", "搜索失败");
            // 自动触发的搜索静默失败即可：切换分类/防抖引发的请求本来就是"锦上添花"，
            // 网络抖动没必要每次都弹窗打扰用户，用户仍可点"手动刷新"重试并看到明确错误。
        }
    }

    /// <summary>资源面板的来源切换（综合/仅 Modrinth/仅 CurseForge），跟 Mod 面板的
    /// ModSourceCombo_SelectionChanged 是同一个思路。</summary>
    private void ResourceSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _currentResourceSource = ((ResourceSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "modrinth" => ModSource.Modrinth,
            "curseforge" => ModSource.CurseForge,
            _ => ModSource.Combined
        };
        _resourcePageIndex = 0;
        _ = RunResourceSearchAsync();
    }

    private void ResourceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>整行点击：切换卡片内联展开（仿 PCL），取代原来弹出 ModrinthVersionPickerWindow/
    /// CurseForgeResourcePickerWindow 独立窗口的做法。第一次展开某个条目时才异步拉取版本列表，
    /// 之后收起再展开直接复用已经拉到的结果，不重复打网络请求；点在"下载"按钮上会被 Button
    /// 吃掉事件不冒泡到这里，跟原来的判断逻辑一致（见 ModListItem_Click 的注释）。</summary>
    private async void ResourceListItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not UnifiedResourceItem item) return;
        await OpenResourceDetailAsync(item);
    }

    /// <summary>
    /// 计算材质包/光影包实际应该下载到的目录：按"资源包下载作用域"设置（全局默认值 +
    /// 单版本覆盖，见 AppConfig.IsolateResourcePacksByDefault /
    /// VersionResourcePackIsolationOverrides）决定是装进 &lt;.minecraft&gt;/resourcepacks|shaderpacks
    /// （全局共用，多个版本共享同一份，不用重复下载）还是
    /// &lt;.minecraft&gt;/versions/&lt;当前版本&gt;/resourcepacks|shaderpacks（跟随版本隔离）。
    ///
    /// 只对材质包/光影包生效——数据包的目录逻辑完全不同（必须挂在具体存档下才生效，见
    /// ModrinthService.DownloadResourceAsync 类注释），不走这个设置；Mod 更是必须严格跟随版本
    /// 隔离，本方法不处理 Mod。没有选中任何已安装版本时（比如用户还没装过任何版本就先跑来下材质包），
    /// 回退到全局根目录，不强制要求先选版本。
    /// </summary>
    private string GetEffectiveResourceDir(string folderPath)
    {
        var cfg = _owner.ConfigService.Config;
        var versionId = cfg.SelectedVersionId;
        if (string.IsNullOrEmpty(versionId)) return folderPath;

        var isolate = cfg.VersionResourcePackIsolationOverrides.TryGetValue(versionId, out var overrideValue)
            ? overrideValue
            : cfg.IsolateResourcePacksByDefault;

        return isolate ? Path.Combine(folderPath, "versions", versionId) : folderPath;
    }

    /// <summary>
    /// 计算 Mod 实际应该下载到的目录：跟 GetEffectiveResourceDir 不同，Mod 没有"全局共用"这个选项——
    /// 一个 mod jar 是否兼容，直接跟它所在的那个具体版本（MC 版本+加载器+加载器版本）绑定，
    /// 装错版本轻则不生效重则直接把游戏炸了，绝对不能像材质包那样多个版本共用一份。
    ///
    /// 根因（之前"下载 Mod 默认存到 .minecraft/mods 而不是当前选中版本的 mods"）：
    /// 旧代码在 DownloadModInlineAsync 里直接把 folder.Path（.minecraft 根目录）传给
    /// PromptSaveDirectory 当默认目录，ModrinthService.DownloadResourceAsync / 
    /// CurseForgeService.DownloadModAsync 内部又会在传入目录后面拼接一层 "mods"，
    /// 两者一叠加，结果就是不论当前选中哪个版本，Mod 永远被存进 
    /// "<正在使用的 .minecraft 文件夹>/mods"这个全局共用目录——这既不是"隔离"，
    /// 也完全没有实际读这个 mod 的加载器认（Fabric/Forge 只认自己 gameDir 下的 mods，
    /// 不会去 .minecraft 根目录找），下载看起来"成功"了，实际游戏里完全看不到这个 mod。
    ///
    /// 修复：永远返回"当前选中版本自己的目录"（有 SelectedVersionId 时），
    /// 跟 LauncherService.BuildArguments 里 gameDir 的算法保持一致（都是
    /// .minecraft/versions/&lt;版本号&gt;），下载的目标目录和游戏实际读取 mods 的目录
    /// 永远是同一个，不会出现"下载成功但游戏里看不到"这种错觉。
    /// 没有选中任何已安装版本时（比如用户还没装过任何版本），没有地方可以隔离，
    /// 回退到 .minecraft 根目录，跟以前的行为一致，只是加一个提示，防止用户对着一个
    /// "还没绑定到任何具体版本"的下载摸不着头脑。
    /// </summary>
    private string GetEffectiveModDir(string folderPath, out bool hasSelectedVersion)
    {
        var versionId = _owner.ConfigService.Config.SelectedVersionId;
        hasSelectedVersion = !string.IsNullOrEmpty(versionId);
        return hasSelectedVersion ? Path.Combine(folderPath, "versions", versionId!) : folderPath;
    }

    /// <summary>
    /// 点击资源条目：整页跳转到 ModDetailPage（取代原来的原地手风琴展开），逻辑跟
    /// OpenModDetailAsync 对称。数据包场景需要先扫描存档列表并检查非空，检查通不过就不打开详情页
    /// （跟原来 ToggleResourceExpandAsync 里"展开前先校验"的顺序保持一致，不让用户先看到
    /// 详情页再被弹窗打断）。
    /// </summary>
    private async Task OpenResourceDetailAsync(UnifiedResourceItem item)
    {
        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();

        if (folder == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Add_A_Minecraft_Folder_On_The_Versions_P", "请先去「版本选择」页添加一个 .minecraft 文件夹。"));
            return;
        }

        item.IsDataPack = _currentResourceType == ModrinthResourceType.DataPack;
        if (item.IsDataPack && item.SaveNames.Count == 0)
        {
            foreach (var name in _folderService.ScanSaves(folder.Path)) item.SaveNames.Add(name);
            if (item.SaveNames.Count == 0)
            {
                MessageBoxDialog.ShowInfo("当前文件夹下还没有任何存档，数据包必须安装到具体存档里，请先创建一个存档再来下载。");
                return;
            }
            item.SelectedSaveName = item.SaveNames[0];
        }

        var sourceUrl = item.RawItem switch
        {
            ModrinthSearchHit h => $"https://modrinth.com/{ModrinthProjectTypeSlug(_currentResourceType)}/{h.Slug}",
            CurseForgeMod m => m.Links?.WebsiteUrl,
            _ => null
        };

        var detail = new ModDetailPage(
            ModDetailPage.DetailMode.DirectDownload,
            item.Title, item.Description, item.IconUrl, item.Author, item.Downloads,
            item.SourceLabel, sourceUrl, item, item.IsFavorite,
            onFavoriteToggle: nowFavorite => FavoriteResource_Toggle(item, nowFavorite),
            onBack: HideDetail,
            onDownload: entry => DownloadResourceInlineAsync(item, entry),
            isDataPack: item.IsDataPack,
            saveNames: item.SaveNames);

        ShowDetail(detail);

        if (item.VersionsLoaded)
        {
            detail.SetFlatEntries(item.Versions);
            detail.ShowGroups(item.Groups);
            return;
        }

        detail.ShowLoading();
        await LoadResourceVersionsAsync(item, ResourceGameVersionBox.Text?.Trim());
        detail.SetFlatEntries(item.Versions);
        detail.ShowGroups(item.Groups);
    }

    /// <summary>Modrinth 项目页 URL 里的类型段（用于拼"转到来源"按钮的链接），跟 project_type
    /// 在 Modrinth 网站路由里的写法一致：资源包/光影包走 resourcepack/shader，数据包本质上是
    /// project_type=mod 打了 datapack 分类标签，页面路由仍然是 /mod/{slug}。</summary>
    private static string ModrinthProjectTypeSlug(ModrinthResourceType type) => type switch
    {
        ModrinthResourceType.ResourcePack => "resourcepack",
        ModrinthResourceType.Shader => "shader",
        ModrinthResourceType.DataPack => "mod",
        _ => "mod"
    };

    /// <summary>拉取一个资源条目的版本列表并分组，从原来 ToggleResourceExpandAsync 里抽出来的
    /// 共享逻辑，供整页详情复用。</summary>
    private async Task LoadResourceVersionsAsync(UnifiedResourceItem item, string? gameVersion)
    {
        item.IsLoadingVersions = true;
        try
        {
            item.Versions.Clear();
            if (item.Source == ModSource.Modrinth)
            {
                var versions = await _modrinth.GetVersionsAsync(item.SourceId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                foreach (var v in versions) item.Versions.Add(new InlineVersionEntry(v));
            }
            else if (item.Source == ModSource.CurseForge)
            {
                var modId = int.Parse(item.SourceId);
                var files = await GetCurseForge().GetFilesAsync(modId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                foreach (var f in files) item.Versions.Add(new InlineVersionEntry(f));
            }
            item.HasNoResults = item.Versions.Count == 0;

            item.Groups.Clear();
            foreach (var g in ModVersionGrouping.Group(item.Versions)) item.Groups.Add(g);
            if (item.Groups.Count > 0) item.Groups[0].IsExpanded = true;

            item.VersionsLoaded = true;
        }
        catch (CurseForgeKeyMissingException ex)
        {
            MessageBoxDialog.ShowInfo(ex.Message, Loc.T("Str_Cs_No_Api_Key_Configured", "未配置 Key"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Version_List_This_Is_", "获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            item.IsLoadingVersions = false;
        }
    }

    /// <summary>ModDetailPage 收藏按钮回调专用，逻辑跟 FavoriteMod_Toggle 对称。</summary>
    private void FavoriteResource_Toggle(UnifiedResourceItem item, bool nowFavorite)
    {
        var cfg = _owner.ConfigService.Config;
        var existing = cfg.FavoriteItems.FirstOrDefault(f => f.MatchesKey(item.FavoriteType, item.SourceId, item.Source));
        if (nowFavorite && existing == null)
        {
            cfg.FavoriteItems.Add(new FavoriteItem
            {
                Type = item.FavoriteType,
                Source = item.Source,
                SourceId = item.SourceId,
                Title = item.Title,
                Description = item.Description,
                Author = item.Author,
                IconUrl = item.IconUrl,
                Downloads = item.Downloads
            });
        }
        else if (!nowFavorite && existing != null)
        {
            cfg.FavoriteItems.Remove(existing);
        }
        _owner.ConfigService.Save();

        item.IsFavorite = nowFavorite;
        foreach (var r in _resources.Where(r => r.Source == item.Source && r.SourceId == item.SourceId)) r.IsFavorite = nowFavorite;
        foreach (var r in _favoriteResources.Where(r => r.Source == item.Source && r.SourceId == item.SourceId)) r.IsFavorite = nowFavorite;

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }

    /// <summary>展开面板里点"下载"：按钮的 DataContext 是 InlineVersionEntry，
    /// 需要顺着可视化树找到外层承载的 UnifiedModItem/UnifiedResourceItem 才知道下载到哪个目录/
    /// 用哪个资源类型——这里靠 FrameworkElement.DataContext 沿着 Parent 链往上找第一个非
    /// InlineVersionEntry 的 DataContext，等价于原来弹窗构造函数里已经绑定好的 _type/_kind 字段，
    /// 只是从"构造函数参数"变成"运行时沿可视化树查找"，因为现在同一个模板要同时服务
    /// Mod 面板(DownloadCenterPage)、资源面板(DownloadCenterPage)、服务端资源面板
    /// (ServerManagerPage)三处不同的宿主，各自的目录解析逻辑不一样，不能写死在共享模板里。</summary>
    private async void InlineVersionDownload_Click(object sender, RoutedEventArgs e)
    {
        // 这个处理器现在挂在 ListBox 上，通过 Button.Click 路由事件冒泡触发（因为按钮所在的
        // DataTemplate 定义在 App.xaml 全局资源里，直接在按钮上写 Click= 会被解析成 App 类的方法，
        // 找不到就编译报错。所以改成在按钮的宿主 ListBox 上订阅冒泡的 Button.Click，
        // 真正被点击的按钮要从 e.OriginalSource 沿可视化树网上找。
        if (FindAncestorButton(e.OriginalSource as DependencyObject) is not Button btn) return;
        if (btn.Tag is not InlineVersionEntry entry) return;
        var host = FindHostItem(btn);
        if (host is UnifiedModItem modItem) { await DownloadModInlineAsync(modItem, entry); return; }
        if (host is UnifiedResourceItem resItem) { await DownloadResourceInlineAsync(resItem, entry); return; }
    }

    /// <summary>从事件的原始来源（可能是 Button 内部的 TextBlock/ContentPresenter 等子元素）
    /// 沿可视化树向上找到最近的 Button 本身。</summary>
    private static Button? FindAncestorButton(DependencyObject? start)
    {
        var current = start;
        while (current != null)
        {
            if (current is Button btn) return btn;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>沿可视化树向上找到第一个 DataContext 是 UnifiedModItem 或 UnifiedResourceItem 的祖先元素，
    /// 即这个"下载"按钮所属的外层卡片条目。</summary>
    private static object? FindHostItem(DependencyObject start)
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(start);
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.DataContext is (UnifiedModItem or UnifiedResourceItem))
                return fe.DataContext;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async Task DownloadResourceInlineAsync(UnifiedResourceItem item, InlineVersionEntry entry)
    {
        // 整合包走完全独立的安装流程：不是"下载单文件丢进固定目录"，而是"弹窗选/建目标版本
        // 目录，再解析 .mrpack 清单逐个下载 mod、展开 overrides"——跟下面材质包/光影包/数据包
        // 共用的通用下载逻辑（GetEffectiveResourceDir + PromptSaveDirectory + DownloadResourceAsync）
        // 完全不是一回事，必须在最前面单独分支拦下，不能落到下面通用逻辑里
        // （ModrinthService.DownloadResourceAsync 对 type==Modpack 会直接抛异常，就是为了防止误用）。
        if (_currentResourceType == ModrinthResourceType.Modpack)
        {
            await DownloadModpackInlineAsync(item, entry);
            return;
        }

        if (item.IsDataPack && string.IsNullOrEmpty(item.SelectedSaveName))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Choose_Which_World_To_Install_Into_First", "请先选择要安装到哪个存档（数据包必须放进具体存档才会生效）。"));
            return;
        }

        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();
        if (folder == null) return; // 展开阶段已经校验过，这里理论上不会触发

        var effectiveDir = item.IsDataPack
            ? folder.Path // 数据包必须挂在具体存档下，走 folder.Path + saveName 拼接，不受资源包作用域设置影响
            : GetEffectiveResourceDir(folder.Path);

        // 需求："加入下载时选下载目标目录功能"。数据包必须放进具体存档目录才会生效，
        // 改目录会导致游戏读不到，因此只对材质包/光影包开放"选择其他目录"，
        // 数据包始终固定用 effectiveDir（存档路径），不弹询问。
        if (!item.IsDataPack)
        {
            var chosen = PromptSaveDirectory(effectiveDir, entry.Name);
            if (chosen == null) return; // 用户取消
            effectiveDir = chosen;
        }

        var progressWin = new ProgressDialog($"正在下载 {entry.Name} ...");
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading", "下载中"), 0, 1, msg)));
            string path;
            if (entry.Source == ModSource.Modrinth)
            {
                // 用户已选定最终目录（数据包不弹选目录、必须留在存档 datapacks 下，保持拼接）；
                // 材质包/光影包选了别的目录就直接落进那个目录，不再套一层分类子文件夹。
                path = await _modrinth.DownloadResourceAsync(effectiveDir, _currentResourceType,
                    (ModrinthVersion)entry.RawVersion, progress, item.IsDataPack ? item.SelectedSaveName : null,
                    appendCategorySubdir: !item.IsDataPack);
            }
            else
            {
                var kind = _currentResourceType switch
                {
                    ModrinthResourceType.ResourcePack => CurseForgeResourceKind.ResourcePack,
                    ModrinthResourceType.Shader => CurseForgeResourceKind.Shader,
                    ModrinthResourceType.DataPack => CurseForgeResourceKind.DataPack,
                    _ => throw new ArgumentOutOfRangeException()
                };
                path = await GetCurseForge().DownloadResourceAsync(effectiveDir, kind,
                    (CurseForgeFile)entry.RawVersion, progress, item.IsDataPack ? item.SelectedSaveName : null,
                    appendCategorySubdir: !item.IsDataPack);
            }
            _owner.EnsureVisibleForDialog();
            MessageBoxDialog.ShowSuccess($"下载完成：\n{path}");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Download_Failed_This_Is_Usually_A_Networ", "下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[下载失败] {ex}", "下载失败");
        }
        finally
        {
            progressWin.Close();
        }
    }

    /// <summary>
    /// 整合包专用下载/安装流程，取代通用的 DownloadResourceInlineAsync 单文件下载路径。
    ///
    /// 需求 B："整合包下载逻辑：弹出来让你选/建一个新版本目录再装（更接近'整合包=一整套独立配置'
    /// 的直觉，避免跟你当前版本里已有的 mod 混在一起）"。
    ///
    /// 步骤：
    /// 1. 弹 ModpackTargetVersionDialog 让用户选"新建独立版本目录（默认推荐）"或"装进已有版本目录"。
    /// 2. 把 .mrpack 文件本身下载到一个临时文件（不能直接扔进目标目录——.mrpack 只是清单，
    ///    ImportMrpackAsync 需要先读它，再决定实际要往目标目录写哪些文件）。
    /// 3. 调用 ModpackService.ImportMrpackAsync 解析清单、下载所有 mod、展开 overrides 到目标版本目录。
    /// 4. 新建模式下，把这个新版本目录追加注册进当前 GameFolder 的可选版本列表里（对应
    ///    FolderService.ScanVersions 之后能在"版本选择"页看到这个新版本，不需要用户手动刷新）。
    /// </summary>
    private async Task DownloadModpackInlineAsync(UnifiedResourceItem item, InlineVersionEntry entry)
    {
        if (entry.Source != ModSource.Modrinth)
        {
            // 目前只接 Modrinth 一侧来源（.mrpack），跟 DownloadCenterPage.xaml 里"整合包分类"
            // 注释的说明一致；理论上不会走到这里，因为整合包分类的搜索结果本来就只来自 Modrinth，
            // 这里只是兜底防御，避免以后不小心把 CurseForge 结果也混进这个分类时静默出错。
            MessageBoxDialog.ShowInfo("整合包目前只支持 Modrinth 来源，这个结果暂时无法安装。");
            return;
        }

        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();
        if (folder == null) return; // 展开阶段已经校验过，这里理论上不会触发

        var existingVersions = _folderService.ScanVersions(folder.Path);

        var targetDialog = new ModpackTargetVersionDialog(item.Title, existingVersions);
        if (OverlayDialogService.ShowModal(targetDialog) != true) return; // 用户取消

        // 装进已有版本目录时，跟 ModManagerPage.ImportModpack_Click 一致，二次确认一下——
        // 整合包内容会合并覆盖同名文件，用户需要明确知道这一点，而不是选完目录就悄悄覆盖。
        if (!targetDialog.IsNewVersion)
        {
            var confirm = MessageBoxDialog.ShowConfirm(
                $"整合包「{item.Title}」会合并安装到版本目录「{targetDialog.TargetVersionId}」，\n" +
                "同名的 mod/config 文件会被覆盖，可能和已有 Mod 冲突。确定要继续吗？", "确认安装");
            if (!confirm) return;
        }

        var targetVersionDir = Path.Combine(folder.Path, "versions", targetDialog.TargetVersionId);

        var progressWin = new ProgressDialog($"正在安装整合包 {item.Title} ...");
        progressWin.Show();
        var tempMrpackPath = Path.Combine(Path.GetTempPath(), $"xcl2_dl_{Guid.NewGuid():N}.mrpack");
        try
        {
            var version = (ModrinthVersion)entry.RawVersion;
            var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
            if (file == null) throw new InvalidOperationException("这个整合包版本没有可下载的文件。");

            progressWin.Progress.Report(new ProgressInfo("下载整合包清单", 0, 1, file.Filename));
            using (var http = new HttpClient())
            using (var resp = await http.GetAsync(file.Url))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempMrpackPath);
                await resp.Content.CopyToAsync(fs);
            }

            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("安装中", 0, 1, msg)));
            var result = await _modpackService.ImportMrpackAsync(tempMrpackPath, targetVersionDir, progress);

            // 新建模式下，把新版本目录追加进这个 GameFolder 的可选版本列表，这样"版本选择"页
            // 下次打开就能直接看到，不需要用户手动去点"刷新"才发现整合包装到哪了。
            if (targetDialog.IsNewVersion)
            {
                var cfg = _owner.ConfigService.Config;
                cfg.SelectedFolderPath = folder.Path;
                _owner.ConfigService.Save();
            }

            _owner.EnsureVisibleForDialog();
            if (result.FailedFiles.Count > 0)
            {
                MessageBoxDialog.ShowWarning(
                    $"整合包「{result.Name}」已安装到版本目录「{targetDialog.TargetVersionId}」，\n" +
                    $"但有 {result.FailedFiles.Count} 个文件下载失败（可能是对应的 Mod 已在源站下架），\n" +
                    "可以之后在「Mod 管理」页手动补装：\n" + string.Join("\n", result.FailedFiles.Take(10)) +
                    (result.FailedFiles.Count > 10 ? $"\n... 等共 {result.FailedFiles.Count} 个" : ""), "部分安装失败");
            }
            else
            {
                MessageBoxDialog.ShowSuccess($"整合包「{result.Name}」已安装到版本目录：\n{targetVersionDir}");
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("安装整合包失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。",
                $"[安装整合包失败] {ex}", "安装失败");
        }
        finally
        {
            progressWin.Close();
            try { if (File.Exists(tempMrpackPath)) File.Delete(tempMrpackPath); } catch { /* 临时文件清理失败无需打扰用户 */ }
        }
    }

    /// <summary>
    /// 每次搜索前都重新创建一次 CurseForgeService：key 可能是用户刚在设置页改的，
    /// 用一个新实例保证一定用最新 key，不需要处理"旧实例缓存了旧 key"的问题。
    /// </summary>
    /// <summary>
    /// 计算并展示"第 N 页 / 共 M 页"文案。综合模式下总页数取两个来源里页数较多的那个
    /// （见 ModSearchService.SearchAsync 类注释的"并列分页"语义），单一来源时就是那个来源自己的页数。
    /// modrinthTotal/curseForgeTotal 为 0 且当前页也没有更多结果时，只显示"第 N 页"，不硬凑一个"共 0 页"。
    /// </summary>
    private static void UpdatePageSummary(TextBlock text, int pageIndex, int modrinthTotal, int curseForgeTotal)
    {
        var totalPages = Math.Max(
            modrinthTotal > 0 ? (int)Math.Ceiling(modrinthTotal / (double)PageSize) : 0,
            curseForgeTotal > 0 ? (int)Math.Ceiling(curseForgeTotal / (double)PageSize) : 0);
        text.Text = totalPages > 0 ? $"第 {pageIndex + 1} 页 / 共 {totalPages} 页" : $"第 {pageIndex + 1} 页";
    }

    /// <summary>是否还有下一页：只要任意一个来源的总条数大于"下一页的起始 offset"就有。</summary>
    private static bool HasMorePages(int pageIndex, int modrinthTotal, int curseForgeTotal)
    {
        var nextOffset = (pageIndex + 1) * PageSize;
        return nextOffset < modrinthTotal || nextOffset < curseForgeTotal;
    }

    private CurseForgeService GetCurseForge() => _curseForge ??= new CurseForgeService(_curseForgeKeyService);

    /// <summary>同理，ModSearchService 内部持有 CurseForgeService，每次搜索前重建以拿到最新 key。</summary>
    private ModSearchService GetModSearch() => _modSearch ??= new ModSearchService(_modrinth, GetCurseForge());

    private void ModSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _currentModSource = ((ModSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "modrinth" => ModSource.Modrinth,
            "curseforge" => ModSource.CurseForge,
            _ => ModSource.Combined
        };
        _modPageIndex = 0; // 换来源后结果集完全不同，页码回到第一页
        _ = RunModSearchAsync(); // 切换来源后用同一个关键词立即重新搜索一次，不需要用户再点搜索按钮
    }

    /// <summary>版本快捷选择条点击，同 ResourceVersionChip_Click，见那边注释。</summary>
    private void ModVersionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        ModGameVersionBox.Text = btn.Tag as string ?? "";
    }

    /// <summary>搜索框/游戏版本号/加载器下拉框变化：走防抖，停顿后自动重新搜索。筛选条件变了，
    /// 之前翻到的页码对新结果集没有意义，回到第一页。</summary>
    private void ModFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ModGameVersionBox.Text))
            _lastTypedGameVersion = ModGameVersionBox.Text.Trim();
        _modPageIndex = 0;
        Debounce(() => _ = RunModSearchAsync());
    }

    private void ModFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _modPageIndex = 0;
        Debounce(() => _ = RunModSearchAsync());
    }

    private async void ModSearch_Click(object sender, RoutedEventArgs e) => await RunModSearchAsync(showHints: true);

    /// <summary>"重置条件"：清空名称/游戏版本输入框，加载器下拉恢复到"不限加载器"，
    /// 然后立即按重置后的条件重新搜索一次（回到"浏览热门 Mod"的状态）。
    /// 改控件值会顺带触发 ModFilter_Changed/ModFilter_SelectionChanged，把防抖计时器又启动一次；
    /// 这里先停掉计时器再立即搜索，避免 500ms 后计时器到点又重复搜一次。</summary>
    private void ModFilterReset_Click(object sender, RoutedEventArgs e)
    {
        ModSearchBox.Text = "";
        ModGameVersionBox.Text = "";
        ModLoaderCombo.SelectedIndex = 0;
        _modPageIndex = 0;
        _debounceTimer.Stop();
        _ = RunModSearchAsync(showHints: true);
    }

    private void ModPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_modPageIndex <= 0) return;
        _modPageIndex--;
        _ = RunModSearchAsync();
    }

    private void ModNextPage_Click(object sender, RoutedEventArgs e)
    {
        _modPageIndex++;
        _ = RunModSearchAsync();
    }

    /// <summary>
    /// Mod 综合搜索（Modrinth + CurseForge，支持中英文关键词）。由五处触发：
    /// "Mod"分类第一次打开时自动调用一次（留空关键词=浏览热门）、切换来源/加载器/防抖后的关键词变化、
    /// 用户点"手动刷新"按钮时调用。showHints 控制是否在无结果/失败时弹提示——自动触发的场景
    /// 静默完成即可，只有用户主动点按钮时才弹窗，避免打字过程中或切分类时被提示框打断。
    /// </summary>
    private async Task RunModSearchAsync(bool showHints = false)
    {
        var seq = ++_modSearchSeq;
        var keyword = ModSearchBox.Text?.Trim() ?? "";
        var gameVersion = ModGameVersionBox.Text?.Trim();
        var loaderTag = (ModLoaderCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        try
        {
            var outcome = await GetModSearch().SearchAsync(_currentModSource, keyword,
                string.IsNullOrEmpty(gameVersion) ? null : gameVersion,
                string.IsNullOrEmpty(loaderTag) ? null : loaderTag,
                _modPageIndex, PageSize);

            if (seq != _modSearchSeq) return; // 期间已有更新的搜索发出，这次结果已过时，丢弃

            _mods.Clear();
            var showIcons = _owner.ConfigService.Config.ShowModIcons;
            var favorites = _owner.ConfigService.Config.FavoriteItems;
            foreach (var item in outcome.Items)
            {
                item.ShowIcon = showIcons;
                item.IsFavorite = favorites.Any(f => f.MatchesKey(FavoriteItemType.Mod, item.SourceId, item.Source));
                _mods.Add(item);
            }

            UpdatePageSummary(ModPageSummaryText, _modPageIndex, outcome.ModrinthTotal, outcome.CurseForgeTotal);
            ModPrevPageButton.IsEnabled = _modPageIndex > 0;
            ModNextPageButton.IsEnabled = HasMorePages(_modPageIndex, outcome.ModrinthTotal, outcome.CurseForgeTotal);

            // 命中中文名词典时提示用户实际搜索用的英文名，避免看着结果全是英文标题却不知道为什么。
            var hasChinese = keyword.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
            if (outcome.TranslatedFrom != null)
            {
                ModTranslationHintText.Text = $"“{outcome.TranslatedFrom}” 已自动按对应的英文名搜索。";
                ModTranslationHintText.Visibility = Visibility.Visible;
            }
            else if (hasChinese && outcome.Items.Count == 0)
            {
                // 中文关键词、内置词典没命中、Modrinth/CurseForge 英文接口也是零结果：这是"中文搜不到"
                // 抱怨的根本原因——这两个接口本身基本不理解中文。内置词典只覆盖了高知名度 Mod，
                // 覆盖不到的生僻/小众 Mod 无法在客户端凭空翻译，这里给出明确的下一步操作指引，
                // 而不是让用户对着空列表自己猜为什么，也不是无限扩充一个永远补不完的静态词典。
                ModTranslationHintText.Text = "没有直接搜到结果：Modrinth/CurseForge 的搜索接口本身不识别中文关键词，" +
                    "内置词典也没有收录这个名字。可以点右侧「MC百科 参考」列表里的条目，" +
                    "打开百科页面看它的英文原名，再回来用英文名重新搜索。";
                ModTranslationHintText.Visibility = Visibility.Visible;
            }
            else
            {
                ModTranslationHintText.Visibility = Visibility.Collapsed;
            }

            if (showHints)
            {
                if (outcome.Items.Count == 0 && outcome.Warnings.Count == 0)
                    MessageBoxDialog.ShowInfo("没有找到匹配的 Mod，换个关键词试试。");
                else if (outcome.Warnings.Count > 0)
                    MessageBoxDialog.ShowWarning(string.Join("\n", outcome.Warnings), Loc.T("Str_Cs_Some_Sources_Failed_To_Return_Results", "部分来源搜索失败"));
            }
        }
        catch (Exception ex)
        {
            if (seq != _modSearchSeq) return;
            if (showHints)
                ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Search_Failed_Most_Likely_A_Network_Prob", "搜索失败，可能是网络连接问题，请检查网络后重试。"), $"[搜索失败] {ex}", "搜索失败");
        }

        if (seq != _modSearchSeq) return; // 主搜索已过时，配套的MC百科辅助搜索也不必再跑

        // MC百科中文名搜索辅助：与上面的下载结果并行展示，互不影响成败
        try
        {
            var mcHits = await GetModSearch().SearchMcModAsync(keyword);
            if (seq != _modSearchSeq) return;
            _mcModHits.Clear();
            foreach (var hit in mcHits) _mcModHits.Add(hit);
        }
        catch
        {
            // MC百科没有官方 API，解析失败是预期可能发生的情况，静默跳过不打扰用户，
            // 反正它只是辅助来源，主下载列表(Modrinth/CurseForge)不受影响。
            if (seq == _modSearchSeq) _mcModHits.Clear();
        }
    }

    private void McModOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not McModSearchHit hit) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(hit.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError(Loc.T("Str_Cs_Couldn_T_Open_Your_Browser_N", "无法打开浏览器：\n") + ex.Message);
        }
    }

    private void ModListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>
    /// 整行点击：切换卡片内联展开（仿 PCL），取代原来弹出 ModrinthVersionPickerWindow/
    /// CurseForgeModPickerWindow 独立窗口的做法。
    ///
    /// 之所以能跟 Button 的 Click 共存不冲突：WPF 里 Button 在按下时会把鼠标事件标记为
    /// Handled=true（内置行为），Handled 的路由事件不会再向上冒泡触发外层 Grid 的
    /// MouseLeftButtonUp，所以点在展开面板里"下载"按钮上时只会触发 InlineVersionDownload_Click，
    /// 不会重复触发这里；点在按钮以外的任何位置（图标/标题/描述/作者/下载量）才会触发这里。
    /// </summary>
    private async void ModListItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not UnifiedModItem item) return;
        await OpenModDetailAsync(item);
    }

    /// <summary>
    /// 点击 Mod 条目：整页跳转到 ModDetailPage（取代原来的原地手风琴展开）。
    /// 详情页构造好之后立即塞进 DetailHost 并显示"加载中"，再异步拉取版本列表——
    /// 跟原来 ToggleModExpandAsync 的"先展开、后台拉取"体验一致，只是载体从"卡片下方
    /// 内联面板"换成了"整页详情"。
    /// </summary>
    private async Task OpenModDetailAsync(UnifiedModItem item)
    {
        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();

        if (folder == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Add_A_Minecraft_Folder_On_The_Versions_P", "请先去「版本选择」页添加一个 .minecraft 文件夹。"));
            return;
        }

        var sourceUrl = item.RawItem switch
        {
            ModrinthSearchHit h => $"https://modrinth.com/mod/{h.Slug}",
            CurseForgeMod m => m.Links?.WebsiteUrl,
            _ => null
        };

        var detail = new ModDetailPage(
            ModDetailPage.DetailMode.DirectDownload,
            item.Title, item.Description, item.IconUrl, item.Author, item.Downloads,
            item.SourceLabel, sourceUrl, item, item.IsFavorite,
            onFavoriteToggle: nowFavorite => FavoriteMod_Toggle(item, nowFavorite),
            onBack: HideDetail,
            onDownload: entry => DownloadModInlineAsync(item, entry),
            // 需求：只给「模组管理」（下载中心 Mod 分类）加"显示/隐藏预览版"按钮，
            // 资源包/数据包/光影包/地图详情页（下面 OpenResourceDetailAsync/OpenMapDetailAsync
            // 两处调用 ModDetailPage）不传这个参数，保持默认 false，不出现这个按钮。
            isMod: true);

        ShowDetail(detail);

        if (item.VersionsLoaded)
        {
            detail.SetFlatEntries(item.Versions);
            detail.ShowGroups(item.Groups);
            return;
        }

        detail.ShowLoading();
        await LoadModVersionsAsync(item, ModGameVersionBox.Text?.Trim());
        detail.SetFlatEntries(item.Versions);
        detail.ShowGroups(item.Groups);
    }

    /// <summary>拉取一个 Mod 条目的版本列表并按"加载器+游戏版本"分组，填进 item.Groups——
    /// 从原来 ToggleModExpandAsync 里抽出来的共享逻辑，供整页详情复用，不需要重复实现
    /// "按 Source 拉版本 + 分组"这段。失败时用 MessageBox/ErrorPresenter 提示，但不再需要
    /// 收起手风琴（详情页场景下没有"收起"的概念，出错留在详情页上即可，用户点返回箭头即可离开）。</summary>
    private async Task LoadModVersionsAsync(UnifiedModItem item, string? gameVersion)
    {
        item.IsLoadingVersions = true;
        try
        {
            item.Versions.Clear();
            if (item.Source == ModSource.Modrinth)
            {
                var versions = await _modrinth.GetVersionsAsync(item.SourceId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                foreach (var v in versions) item.Versions.Add(new InlineVersionEntry(v));
            }
            else if (item.Source == ModSource.CurseForge)
            {
                var modId = int.Parse(item.SourceId);
                var files = await GetCurseForge().GetFilesAsync(modId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                foreach (var f in files) item.Versions.Add(new InlineVersionEntry(f));
            }
            item.HasNoResults = item.Versions.Count == 0;

            // 按"加载器 + 游戏版本"重新分组展示（截图2/3那种"NeoForge 26.2"/"Fabric 26.2"可折叠分组样式）。
            // 第一个分组默认展开，方便用户不用再点一次就能直接看到最新版本可下的文件，
            // 其余分组保持折叠减少初次展开时的视觉噪音。
            item.Groups.Clear();
            foreach (var g in ModVersionGrouping.Group(item.Versions)) item.Groups.Add(g);
            if (item.Groups.Count > 0) item.Groups[0].IsExpanded = true;

            item.VersionsLoaded = true;
        }
        catch (CurseForgeKeyMissingException ex)
        {
            MessageBoxDialog.ShowInfo(ex.Message, Loc.T("Str_Cs_No_Api_Key_Configured", "未配置 Key"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Version_List_This_Is_", "获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            item.IsLoadingVersions = false;
        }
    }

    /// <summary>ModDetailPage 收藏按钮回调专用：按目标收藏状态直接设置（不是"翻转"），
    /// 因为详情页自己已经翻转过一次本地按钮文案，这里只需要把结果同步进 FavoriteItems +
    /// 各集合里对应卡片实例，逻辑复用 ToggleCommunityFavorite。</summary>
    private void FavoriteMod_Toggle(UnifiedModItem item, bool nowFavorite)
    {
        var cfg = _owner.ConfigService.Config;
        var existing = cfg.FavoriteItems.FirstOrDefault(f => f.MatchesKey(FavoriteItemType.Mod, item.SourceId, item.Source));
        if (nowFavorite && existing == null)
        {
            cfg.FavoriteItems.Add(new FavoriteItem
            {
                Type = FavoriteItemType.Mod,
                Source = item.Source,
                SourceId = item.SourceId,
                Title = item.Title,
                Description = item.Description,
                Author = item.Author,
                IconUrl = item.IconUrl,
                Downloads = item.Downloads
            });
        }
        else if (!nowFavorite && existing != null)
        {
            cfg.FavoriteItems.Remove(existing);
        }
        _owner.ConfigService.Save();

        item.IsFavorite = nowFavorite;
        foreach (var m in _mods.Where(m => m.Source == item.Source && m.SourceId == item.SourceId)) m.IsFavorite = nowFavorite;
        foreach (var m in _favoriteMods.Where(m => m.Source == item.Source && m.SourceId == item.SourceId)) m.IsFavorite = nowFavorite;

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }

    /// <summary>
    /// 分组标题条点击：翻转对应 VersionGroup.IsExpanded（仿截图2/3里点"Fabric 26.2"这一行
    /// 展开/收起该分组下的前置资源+版本列表）。跟 ModListItem_Click 是同一个"Tag 绑定数据项 +
    /// MouseLeftButtonUp"套路，只是这次翻转的是分组自己的展开状态，不需要重新发网络请求——
    /// 分组数据在 ToggleModExpandAsync 展开外层卡片时已经一次性全部生成好了，点分组标题条
    /// 只是切换本地已有数据的显示/隐藏，不存在"加载中"状态。
    /// </summary>
    private void VersionGroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not VersionGroup group) return;
        group.IsExpanded = !group.IsExpanded;
    }

    private async Task DownloadModInlineAsync(UnifiedModItem item, InlineVersionEntry entry)
    {
        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();
        if (folder == null) return; // 展开阶段已经校验过

        // 需求："加入下载时选下载目标目录功能（调用windows资源管理器选择）所有社区资源标配"。
        // 默认是当前选中版本自己的目录（GetEffectiveModDir，跟随版本隔离，保证游戏能识别到；
        // 修复了之前默认存到 .minecraft 根目录 mods 文件夹、游戏读不到的问题），
        // 用户也可以选择另存到别处；同一会话内上一次选过的目录会被记住，作为下一次的默认建议
        // （见 PromptSaveDirectory / _lastChosenDownloadDir 注释）。
        var effectiveModDir = GetEffectiveModDir(folder.Path, out var hasSelectedVersion);
        if (!hasSelectedVersion)
        {
            MessageBoxDialog.ShowInfo(
                "当前没有选中任何已安装版本，这个 Mod 将下载到 .minecraft 根目录下的 mods 文件夹，" +
                "游戏不一定能读到——建议先在「版本选择」页选中要装这个 Mod 的具体版本再下载。",
                Loc.T("Str_Status_Tip", "提示"));
        }
        var targetDir = PromptSaveDirectory(effectiveModDir, entry.Name);
        if (targetDir == null) return; // 用户取消

        var progressWin = new ProgressDialog($"正在下载 {entry.Name} ...");
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading", "下载中"), 0, 1, msg)));
            string path;
            if (entry.Source == ModSource.Modrinth)
                path = await _modrinth.DownloadResourceAsync(targetDir, ModrinthResourceType.Mod, (ModrinthVersion)entry.RawVersion, progress,
                    appendCategorySubdir: false);
            else
                path = await GetCurseForge().DownloadModAsync(targetDir, (CurseForgeFile)entry.RawVersion, progress,
                    appendCategorySubdir: false);
            _owner.EnsureVisibleForDialog();
            MessageBoxDialog.ShowSuccess($"Mod 已安装到：\n{path}");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Download_Failed_This_Is_Usually_A_Networ", "下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[下载失败] {ex}", "下载失败");
        }
        finally
        {
            progressWin.Close();
        }
    }

    private async void MapSearch_Click(object sender, RoutedEventArgs e)
    {
        _mapPageIndex = 0;
        await RunMapSearchAsync(showHints: true);
    }

    /// <summary>版本快捷选择条点击，同 ResourceVersionChip_Click，见那边注释。</summary>
    private void MapVersionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        MapGameVersionBox.Text = btn.Tag as string ?? "";
    }

    /// <summary>搜索框/游戏版本号变化：走防抖，停顿后自动重新搜索。</summary>
    private void MapFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(MapGameVersionBox.Text))
            _lastTypedGameVersion = MapGameVersionBox.Text.Trim();
        _mapPageIndex = 0;
        Debounce(() => _ = RunMapSearchAsync());
    }

    private void MapPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_mapPageIndex <= 0) return;
        _mapPageIndex--;
        _ = RunMapSearchAsync();
    }

    private void MapNextPage_Click(object sender, RoutedEventArgs e)
    {
        _mapPageIndex++;
        _ = RunMapSearchAsync();
    }

    /// <summary>
    /// 地图搜索（CurseForge）。由三处触发："地图"分类第一次打开时自动调用一次、
    /// 搜索框/游戏版本号变化后防抖自动调用、用户点"手动刷新"按钮时调用。
    /// </summary>
    private async Task RunMapSearchAsync(bool showHints = false)
    {
        var seq = ++_mapSearchSeq;
        try
        {
            var keyword = MapSearchBox.Text?.Trim() ?? "";
            var gameVersion = MapGameVersionBox.Text?.Trim();
            var result = await GetCurseForge().SearchMapsAsync(keyword, string.IsNullOrEmpty(gameVersion) ? null : gameVersion,
                _mapPageIndex, PageSize);

            if (seq != _mapSearchSeq) return; // 期间已有更新的搜索发出，这次结果已过时，丢弃

            _maps.Clear();
            var favorites = _owner.ConfigService.Config.FavoriteItems;
            foreach (var mod in result.Data)
            {
                mod.IsFavorite = favorites.Any(f => f.MatchesKey(FavoriteItemType.Map, mod.Id.ToString(), ModSource.CurseForge));
                _maps.Add(mod);
            }

            var mapTotal = result.Pagination?.TotalCount ?? result.Data.Count;
            UpdatePageSummary(MapPageSummaryText, _mapPageIndex, mapTotal, 0);
            MapPrevPageButton.IsEnabled = _mapPageIndex > 0;
            MapNextPageButton.IsEnabled = HasMorePages(_mapPageIndex, mapTotal, 0);

            if (showHints && result.Data.Count == 0)
                MessageBoxDialog.ShowInfo("没有找到匹配的地图，换个关键词试试。");
        }
        catch (CurseForgeKeyMissingException ex)
        {
            if (seq != _mapSearchSeq) return;
            if (showHints)
            {
                MessageBoxDialog.ShowInfo(ex.Message, Loc.T("Str_Cs_No_Api_Key_Configured", "未配置 Key"));
                Category_Checked(CatMap, new RoutedEventArgs()); // 重新走一遍分类切换逻辑，切到"未配置key"提示面板
            }
        }
        catch (Exception ex)
        {
            if (seq != _mapSearchSeq) return;
            if (showHints)
                ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Search_Failed_Most_Likely_A_Network_Prob", "搜索失败，可能是网络连接问题，请检查网络后重试。"), $"[搜索失败] {ex}", "搜索失败");
        }
    }

    /// <summary>整行点击进详情页，同 ModListItem_Click 的思路。地图原来是弹
    /// CurseForgeMapPickerDialog（原 CurseForgeMapPickerWindow 独立窗口迁移而来）弹窗，
    /// 现在改成走 ModDetailPage 整页详情，CurseForgeMapPickerDialog.xaml(.cs) 文件本身保留不删，只是不再从这个入口调用。</summary>
    private async void MapListItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not CurseForgeMod mod) return;
        await OpenMapDetailAsync(mod);
    }

    private async void ViewMapVersions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CurseForgeMod mod) return;
        await OpenMapDetailAsync(mod);
    }

    /// <summary>点击地图条目：整页跳转到 ModDetailPage。地图只有 CurseForge 一个来源，
    /// 不需要像 Mod/资源那样按 Source 区分拉取哪一边的接口，直接调用 GetFilesAsync。</summary>
    private async Task OpenMapDetailAsync(CurseForgeMod mod)
    {
        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();

        if (folder == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Add_A_Minecraft_Folder_On_The_Versions_P", "请先去「版本选择」页添加一个 .minecraft 文件夹。"));
            return;
        }

        var detail = new ModDetailPage(
            ModDetailPage.DetailMode.DirectDownload,
            mod.Name, mod.Summary, mod.Logo?.ThumbnailUrl, mod.AuthorsDisplay, mod.DownloadCount,
            "CurseForge", mod.Links?.WebsiteUrl, mod, mod.IsFavorite,
            onFavoriteToggle: nowFavorite => FavoriteMap_Toggle(mod, nowFavorite),
            onBack: HideDetail,
            onDownload: entry => DownloadMapInlineAsync(folder.Path, mod, entry));

        ShowDetail(detail);
        detail.ShowLoading();

        try
        {
            var gameVersion = MapGameVersionBox.Text?.Trim();
            var files = await GetCurseForge().GetFilesAsync(mod.Id, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
            var entries = files.Select(f => new InlineVersionEntry(f)).ToList();
            var groups = ModVersionGrouping.Group(entries).ToList();
            if (groups.Count > 0) groups[0].IsExpanded = true;
            detail.SetFlatEntries(entries);
            detail.ShowGroups(groups);
        }
        catch (CurseForgeKeyMissingException ex)
        {
            detail.ShowGroups(Enumerable.Empty<VersionGroup>());
            MessageBoxDialog.ShowInfo(ex.Message, Loc.T("Str_Cs_No_Api_Key_Configured", "未配置 Key"));
        }
        catch (Exception ex)
        {
            detail.ShowGroups(Enumerable.Empty<VersionGroup>());
            ErrorPresenter.ShowFriendlyError("获取文件列表失败，可能是网络连接问题，请检查网络后重试。", $"[获取文件列表失败] {ex}", "获取文件列表失败");
        }
    }

    /// <summary>地图详情页里点"下载"：直接下载并解压到当前 .minecraft 文件夹的 saves 目录下，
    /// 复用 CurseForgeService.DownloadMapAsync——跟原来 CurseForgeMapPickerDialog.Download_Click
    /// 调用的是同一个方法，只是调用点从弹窗换成了详情页回调。</summary>
    private async Task DownloadMapInlineAsync(string folderPath, CurseForgeMod mod, InlineVersionEntry entry)
    {
        // 需求："加入下载时选下载目标目录功能"。地图默认解压到当前 .minecraft 的 saves 目录下
        // （这样下载完能直接在游戏里打开），用户也可以选择另存到别的目录（比如只是想要地图
        // 文件本身，不打算马上放进这个实例里玩）。
        var chosenDir = PromptSaveDirectory(folderPath, entry.Name);
        if (chosenDir == null) return; // 用户取消
        folderPath = chosenDir;

        var progressWin = new ProgressDialog($"正在下载 {entry.Name} ...");
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading", "下载中"), 0, 1, msg)));
            // 用户选定的目录就是最终落点：地图压缩包直接解压进这个目录（zip 内世界文件夹就地展开），
            // 不再在所选目录下套一层 saves/（见 CurseForgeService.DownloadMapAsync 的 appendCategorySubdir 参数）。
            var path = await GetCurseForge().DownloadMapAsync(folderPath, (CurseForgeFile)entry.RawVersion, progress,
                appendCategorySubdir: false);
            _owner.EnsureVisibleForDialog();
            MessageBoxDialog.ShowSuccess($"地图已下载到：\n{path}");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Download_Failed_This_Is_Usually_A_Networ", "下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[下载失败] {ex}", "下载失败");
        }
        finally
        {
            progressWin.Close();
        }
    }

    /// <summary>ModDetailPage 收藏按钮回调专用（地图），逻辑跟 FavoriteMod_Toggle 对称。</summary>
    private void FavoriteMap_Toggle(CurseForgeMod mod, bool nowFavorite)
    {
        var cfg = _owner.ConfigService.Config;
        var existing = cfg.FavoriteItems.FirstOrDefault(f => f.MatchesKey(FavoriteItemType.Map, mod.Id.ToString(), ModSource.CurseForge));
        if (nowFavorite && existing == null)
        {
            cfg.FavoriteItems.Add(new FavoriteItem
            {
                Type = FavoriteItemType.Map,
                Source = ModSource.CurseForge,
                SourceId = mod.Id.ToString(),
                Title = mod.Name,
                Description = mod.Summary,
                Author = mod.AuthorsDisplay,
                IconUrl = mod.Logo?.ThumbnailUrl,
                Downloads = mod.DownloadCount
            });
        }
        else if (!nowFavorite && existing != null)
        {
            cfg.FavoriteItems.Remove(existing);
        }
        _owner.ConfigService.Save();

        mod.IsFavorite = nowFavorite;
        foreach (var m in _maps.Where(m => m.Id == mod.Id)) m.IsFavorite = nowFavorite;

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }
}

/// <summary>下载中心版本列表的显示包装：把 VersionManifestEntry 和"是否已收藏"状态放在一起，
/// 便于 XAML 直接绑定收藏按钮的文案，不需要在虚拟化 ListBox 里做视觉树查找。</summary>
public class VersionListItem : System.ComponentModel.INotifyPropertyChanged
{
    public VersionManifestEntry Entry { get; }
    public string Id => Entry.Id;
    public string ReleaseTime => Entry.ReleaseTime;

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FavoriteLabel)));
        }
    }

    public string FavoriteLabel => IsFavorite ? "★ 已收藏" : "☆ 收藏";

    public VersionListItem(VersionManifestEntry entry, bool isFavorite)
    {
        Entry = entry;
        _isFavorite = isFavorite;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
