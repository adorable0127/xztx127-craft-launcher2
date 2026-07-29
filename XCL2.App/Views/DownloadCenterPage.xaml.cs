using System.Collections.ObjectModel;
using System.IO;
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
    private readonly MainWindow _owner;
    private readonly ObservableCollection<VersionListItem> _online = new();
    private VersionManifestRoot? _manifestCache;

    private readonly ModrinthService _modrinth = new();
    private readonly FolderService _folderService = new();
    /// <summary>材质包/数据包/光影包结果列表：原来只装 Modrinth 的 ModrinthSearchHit，
    /// 现在改成统一的 UnifiedResourceItem，才能同时展示 Modrinth + CurseForge 两个来源的结果
    /// （对应 CurseForgeService.SearchResourcesAsync 补上的搜索能力）。</summary>
    private readonly ObservableCollection<UnifiedResourceItem> _resources = new();
    private ModrinthResourceType _currentResourceType = ModrinthResourceType.ResourcePack;
    private ModSource _currentResourceSource = ModSource.Combined;

    private readonly ObservableCollection<VersionListItem> _favorites = new();

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
    /// 记录每个分类是否已经自动加载过一次数据。切分类回来时（比如 游戏版本→Mod→游戏版本）
    /// 已经拉取过的数据没必要重新请求一次网络——用户没改任何筛选条件，列表内容不会变，
    /// 只是徒增等待和流量；只有"第一次进入该分类"才需要自动拉取。
    /// 手动点击各面板的"手动刷新/搜索"按钮，或改动筛选条件，会绕开这个标记强制重新请求。
    /// </summary>
    private readonly HashSet<string> _autoLoadedCategories = new();

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

    public DownloadCenterPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        OnlineListBox.ItemsSource = _online;
        ResourceListBox.ItemsSource = _resources;
        FavoritesListBox.ItemsSource = _favorites;
        MapListBox.ItemsSource = _maps;
        ModListBox.ItemsSource = _mods;
        McModListBox.ItemsSource = _mcModHits;

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
        var wizard = new QuickStartWizardWindow(_owner) { Owner = Window.GetWindow(this) };
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
                if (_autoLoadedCategories.Add("mod")) _ = RunModSearchAsync();
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
            case "map":
                if (_curseForgeKeyService.HasKey())
                {
                    MapPanel.Visibility = Visibility.Visible;
                    if (_autoLoadedCategories.Add("map")) _ = RunMapSearchAsync();
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

        if (_lastLoadedResourceType != type)
        {
            _lastLoadedResourceType = type;
            _ = RunResourceSearchAsync();
        }
    }

    /// <summary>
    /// 刷新"我的收藏"列表。收藏只存了版本 ID，实际条目要从 _manifestCache（游戏版本清单缓存）里查出来——
    /// 所以如果用户还没在"游戏版本"分类点过"刷新版本列表"，这里没有清单可查，只能提示先去刷新一次，
    /// 而不是在收藏页里重新发一次网络请求（收藏页本身不需要联网，除非缓存是空的）。
    /// </summary>
    private void RefreshFavorites()
    {
        _favorites.Clear();
        var favoriteIds = _owner.ConfigService.Config.FavoriteVersionIds;

        if (_manifestCache != null)
        {
            foreach (var id in favoriteIds)
            {
                var entry = _manifestCache.Versions.FirstOrDefault(v => v.Id == id);
                if (entry != null) _favorites.Add(new VersionListItem(entry, true));
            }
        }

        var showEmpty = favoriteIds.Count == 0;
        var showManifestHint = favoriteIds.Count > 0 && _manifestCache == null;

        FavoritesEmptyHint.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        FavoritesEmptyHint.Text = showManifestHint
            ? "已收藏，但还没拉取过版本清单，请先去「游戏版本」分类点一次「刷新版本列表」。"
            : "还没有收藏任何版本，去「游戏版本」分类点击 ☆ 收藏 试试。";
        if (showManifestHint) FavoritesEmptyHint.Visibility = Visibility.Visible;

        FavoritesListBox.Visibility = _favorites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

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
            ErrorPresenter.ShowFriendlyError("获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
    }

    /// <summary>
    /// 搜索框/分类下拉框内容变化：走防抖，停顿 500ms 后才真正重新过滤，
    /// 避免用户每敲一个字符就重新渲染一次列表。过滤是纯本地操作（基于已缓存的 _manifestCache），
    /// 不发网络请求，防抖主要是为了避免频繁重建 ListBox 内容造成的界面抖动。
    /// </summary>
    private void VersionFilter_Changed(object sender, RoutedEventArgs e) => Debounce(ApplyVersionFilter);

    /// <summary>根据分类下拉框(正式版/快照/远古版/愚人节版)和搜索框内容过滤已缓存的版本清单。</summary>
    private void ApplyVersionFilter()
    {
        if (_manifestCache == null) return;

        var selectedTag = (VersionTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Release";
        if (!Enum.TryParse<VersionCategory>(selectedTag, out var category)) category = VersionCategory.Release;

        var keyword = VersionSearchBox.Text?.Trim() ?? "";

        var filtered = _manifestCache.Versions
            .Where(v => v.GetCategory() == category)
            .Where(v => keyword.Length == 0 || v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(200); // 远古版本/快照数量较多，限制条数避免一次性渲染卡顿

        _online.Clear();
        var favorites = _owner.ConfigService.Config.FavoriteVersionIds;
        foreach (var v in filtered) _online.Add(new VersionListItem(v, favorites.Contains(v.Id)));
    }

    private void OnlineListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>
    /// 点击某个版本的"下载安装"按钮：先弹出 LoaderChoiceWindow 让用户一步选择
    /// "原版/Fabric/Forge/NeoForge"，不再依赖点击前先切换页面顶部的加载器筛选行——
    /// 见 LoaderChoiceWindow 类注释，这是本轮解决"下载生态割裂"的核心改动。
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
            MessageBox.Show("请先去「版本选择」页添加一个 .minecraft 文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choiceWindow = new LoaderChoiceWindow(entry.Id) { Owner = Window.GetWindow(this) };
        if (choiceWindow.ShowDialog() != true) return; // 用户点了"取消"

        // 选了 Fabric/Forge/NeoForge：跳转到 InstallClientLoaderWindow，预选好加载器类型 + 这一行
        // 的 MC 版本号，复用现成的三级联动安装逻辑，不在这里重新实现一遍加载器安装。
        if (choiceWindow.SelectedLoader != ServerCoreType.Vanilla)
        {
            var loaderWindow = new InstallClientLoaderWindow(_owner, choiceWindow.SelectedLoader, entry.Id) { Owner = Window.GetWindow(this) };
            if (loaderWindow.ShowDialog() == true && loaderWindow.InstalledVersionId != null)
            {
                MessageBox.Show($"版本「{loaderWindow.InstalledVersionId}」安装完成！可以在「版本选择」页选中它。",
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        var progressWin = new ProgressWindow($"正在安装 {entry.Id} ...") { Owner = Window.GetWindow(this) };
        progressWin.Show();
        try
        {
            // 用 CreateFromConfig 而不是直接 new：这里是真正会下载大批 libraries/assets 文件的
            // 场景，应该按设置页里的"多线程下载/限速/智能限速"配置来，而不是永远单线程不限速。
            using var svc = DownloadService.CreateFromConfig(_owner.ConfigService.Config);
            await svc.InstallVersionAsync(folder.Path, entry, progressWin.Progress);
            MessageBox.Show($"{entry.Id} 安装完成！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("安装失败，可能是网络连接问题、下载源暂时不可用，或安装文件已损坏，请检查网络后重试。", $"[安装失败] {ex}", "安装失败");
        }
        finally
        {
            progressWin.Close();
        }
    }

    /// <summary>收藏/取消收藏一个版本，写入 AppConfig.FavoriteVersionIds 并持久化。</summary>
    private void FavoriteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VersionListItem item) return;
        var cfg = _owner.ConfigService.Config;

        if (cfg.FavoriteVersionIds.Contains(item.Entry.Id))
        {
            cfg.FavoriteVersionIds.Remove(item.Entry.Id);
            item.IsFavorite = false;
        }
        else
        {
            cfg.FavoriteVersionIds.Add(item.Entry.Id);
            item.IsFavorite = true;
        }
        _owner.ConfigService.Save();

        if (FavoritesPanel.Visibility == Visibility.Visible) RefreshFavorites();
    }

    private async void ResourceSearch_Click(object sender, RoutedEventArgs e) => await RunResourceSearchAsync(showEmptyHint: true);

    /// <summary>
    /// 搜索框/游戏版本号输入变化：走防抖，不打断用户打字，也不在每次搜索为空时弹提示框打扰（
    /// 静默完成即可，只有用户主动点"手动刷新"按钮时才在无结果时弹提示，见 showEmptyHint 参数）。
    /// </summary>
    private void ResourceFilter_Changed(object sender, RoutedEventArgs e) => Debounce(() => _ = RunResourceSearchAsync());

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
                keyword, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);

            if (seq != _resourceSearchSeq) return; // 期间已有更新的搜索发出，这次结果已过时，丢弃

            _resources.Clear();
            var showIcons = _owner.ConfigService.Config.ShowModIcons;
            foreach (var item in outcome.Items)
            {
                item.ShowIcon = showIcons;
                _resources.Add(item);
            }

            if (showEmptyHint)
            {
                if (outcome.Items.Count == 0 && outcome.Warnings.Count == 0)
                    MessageBox.Show("没有找到匹配的资源，换个关键词试试。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                else if (outcome.Warnings.Count > 0)
                    MessageBox.Show(string.Join("\n", outcome.Warnings), "部分来源搜索失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            if (seq != _resourceSearchSeq) return;
            if (showEmptyHint)
                ErrorPresenter.ShowFriendlyError("搜索失败，可能是网络连接问题，请检查网络后重试。", $"[搜索失败] {ex}", "搜索失败");
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
        _ = RunResourceSearchAsync();
    }

    private void ResourceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>整行点击进"查看版本"，同 ModListItem_Click 的思路（Button 会 Handle 掉点击不冒泡，
    /// 所以点在"查看版本"按钮上只会触发 ViewResourceVersions_Click，不会重复触发这里）。</summary>
    private void ResourceListItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not UnifiedResourceItem item) return;
        ViewResourceVersions_Click(new Button { Tag = item }, new RoutedEventArgs());
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

    private async void ViewResourceVersions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not UnifiedResourceItem item) return;

        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();

        if (folder == null)
        {
            MessageBox.Show("请先去「版本选择」页添加一个 .minecraft 文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var gameVersion = ResourceGameVersionBox.Text?.Trim();

            if (item.Source == ModSource.Modrinth)
            {
                var versions = await _modrinth.GetVersionsAsync(item.SourceId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                if (versions.Count == 0)
                {
                    MessageBox.Show("这个资源暂无可下载的版本（可能是筛选的游戏版本不匹配）。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var saveNames = _currentResourceType == ModrinthResourceType.DataPack
                    ? _folderService.ScanSaves(folder.Path)
                    : new List<string>();

                if (_currentResourceType == ModrinthResourceType.DataPack && saveNames.Count == 0)
                {
                    MessageBox.Show("当前文件夹下还没有任何存档，数据包必须安装到具体存档里，请先创建一个存档再来下载。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var effectiveDir = _currentResourceType == ModrinthResourceType.DataPack
                    ? folder.Path // 数据包必须挂在具体存档下，走 folder.Path + saveName 拼接，不受资源包作用域设置影响
                    : GetEffectiveResourceDir(folder.Path);
                var picker = new ModrinthVersionPickerWindow(_modrinth, effectiveDir, _currentResourceType,
                    item.Title, versions, saveNames) { Owner = Window.GetWindow(this) };
                picker.ShowDialog();
            }
            else if (item.Source == ModSource.CurseForge)
            {
                var modId = int.Parse(item.SourceId);
                var files = await GetCurseForge().GetFilesAsync(modId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                if (files.Count == 0)
                {
                    MessageBox.Show("这个资源暂无可下载的文件（可能是筛选的游戏版本不匹配）。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var saveNames = _currentResourceType == ModrinthResourceType.DataPack
                    ? _folderService.ScanSaves(folder.Path)
                    : new List<string>();

                if (_currentResourceType == ModrinthResourceType.DataPack && saveNames.Count == 0)
                {
                    MessageBox.Show("当前文件夹下还没有任何存档，数据包必须安装到具体存档里，请先创建一个存档再来下载。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var kind = _currentResourceType switch
                {
                    ModrinthResourceType.ResourcePack => CurseForgeResourceKind.ResourcePack,
                    ModrinthResourceType.Shader => CurseForgeResourceKind.Shader,
                    ModrinthResourceType.DataPack => CurseForgeResourceKind.DataPack,
                    _ => throw new ArgumentOutOfRangeException()
                };

                var effectiveDir = _currentResourceType == ModrinthResourceType.DataPack
                    ? folder.Path
                    : GetEffectiveResourceDir(folder.Path);
                var picker = new CurseForgeResourcePickerWindow(GetCurseForge(), effectiveDir, kind,
                    item.Title, files, saveNames) { Owner = Window.GetWindow(this) };
                picker.ShowDialog();
            }
        }
        catch (CurseForgeKeyMissingException ex)
        {
            MessageBox.Show(ex.Message, "未配置 Key", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
    }

    /// <summary>
    /// 每次搜索前都重新创建一次 CurseForgeService：key 可能是用户刚在设置页改的，
    /// 用一个新实例保证一定用最新 key，不需要处理"旧实例缓存了旧 key"的问题。
    /// </summary>
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
        _ = RunModSearchAsync(); // 切换来源后用同一个关键词立即重新搜索一次，不需要用户再点搜索按钮
    }

    /// <summary>搜索框/游戏版本号/加载器下拉框变化：走防抖，停顿后自动重新搜索。</summary>
    private void ModFilter_Changed(object sender, RoutedEventArgs e) => Debounce(() => _ = RunModSearchAsync());

    private void ModFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        Debounce(() => _ = RunModSearchAsync());
    }

    private async void ModSearch_Click(object sender, RoutedEventArgs e) => await RunModSearchAsync(showHints: true);

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
                string.IsNullOrEmpty(loaderTag) ? null : loaderTag);

            if (seq != _modSearchSeq) return; // 期间已有更新的搜索发出，这次结果已过时，丢弃

            _mods.Clear();
            var showIcons = _owner.ConfigService.Config.ShowModIcons;
            foreach (var item in outcome.Items)
            {
                item.ShowIcon = showIcons;
                _mods.Add(item);
            }

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
                    MessageBox.Show("没有找到匹配的 Mod，换个关键词试试。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                else if (outcome.Warnings.Count > 0)
                    MessageBox.Show(string.Join("\n", outcome.Warnings), "部分来源搜索失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            if (seq != _modSearchSeq) return;
            if (showHints)
                ErrorPresenter.ShowFriendlyError("搜索失败，可能是网络连接问题，请检查网络后重试。", $"[搜索失败] {ex}", "搜索失败");
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
            MessageBox.Show("无法打开浏览器：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ModListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>
    /// 整行点击进入"查看版本"（下载详情）界面。之前只有右侧一个小按钮能点，条目本身点击没反应，
    /// 用户体验上不直观（很容易点在标题/描述文字上误以为没反应）。
    ///
    /// 之所以能跟 Button 的 Click 共存不冲突：WPF 里 Button 在按下时会把鼠标事件标记为
    /// Handled=true（内置行为），Handled 的路由事件不会再向上冒泡触发外层 Grid 的
    /// MouseLeftButtonUp，所以点在"查看版本"按钮上时只会触发 ViewModVersions_Click，
    /// 不会重复触发这里；点在按钮以外的任何位置（图标/标题/描述/作者/下载量）才会触发这里。
    /// </summary>
    private void ModListItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not UnifiedModItem item) return;
        ViewModVersions_Click(new Button { Tag = item }, new RoutedEventArgs());
    }

    private async void ViewModVersions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not UnifiedModItem item) return;

        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();

        if (folder == null)
        {
            MessageBox.Show("请先去「版本选择」页添加一个 .minecraft 文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var gameVersion = ModGameVersionBox.Text?.Trim();

        try
        {
            if (item.Source == ModSource.Modrinth)
            {
                var versions = await _modrinth.GetVersionsAsync(item.SourceId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                if (versions.Count == 0)
                {
                    MessageBox.Show("这个 Mod 暂无可下载的版本（可能是筛选的游戏版本不匹配）。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var picker = new ModrinthVersionPickerWindow(_modrinth, folder.Path, ModrinthResourceType.Mod,
                    item.Title, versions, new List<string>()) { Owner = Window.GetWindow(this) };
                picker.ShowDialog();
            }
            else if (item.Source == ModSource.CurseForge)
            {
                var modId = int.Parse(item.SourceId);
                var files = await GetCurseForge().GetFilesAsync(modId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                if (files.Count == 0)
                {
                    MessageBox.Show("这个 Mod 暂无可下载的文件（可能是筛选的游戏版本不匹配）。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var picker = new CurseForgeModPickerWindow(GetCurseForge(), folder.Path, item.Title, files)
                    { Owner = Window.GetWindow(this) };
                picker.ShowDialog();
            }
        }
        catch (CurseForgeKeyMissingException ex)
        {
            MessageBox.Show(ex.Message, "未配置 Key", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
    }

    private async void MapSearch_Click(object sender, RoutedEventArgs e) => await RunMapSearchAsync(showHints: true);

    /// <summary>搜索框/游戏版本号变化：走防抖，停顿后自动重新搜索。</summary>
    private void MapFilter_Changed(object sender, RoutedEventArgs e) => Debounce(() => _ = RunMapSearchAsync());

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
            var result = await GetCurseForge().SearchMapsAsync(keyword, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);

            if (seq != _mapSearchSeq) return; // 期间已有更新的搜索发出，这次结果已过时，丢弃

            _maps.Clear();
            foreach (var mod in result.Data) _maps.Add(mod);

            if (showHints && result.Data.Count == 0)
                MessageBox.Show("没有找到匹配的地图，换个关键词试试。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (CurseForgeKeyMissingException ex)
        {
            if (seq != _mapSearchSeq) return;
            if (showHints)
            {
                MessageBox.Show(ex.Message, "未配置 Key", MessageBoxButton.OK, MessageBoxImage.Information);
                Category_Checked(CatMap, new RoutedEventArgs()); // 重新走一遍分类切换逻辑，切到"未配置key"提示面板
            }
        }
        catch (Exception ex)
        {
            if (seq != _mapSearchSeq) return;
            if (showHints)
                ErrorPresenter.ShowFriendlyError("搜索失败，可能是网络连接问题，请检查网络后重试。", $"[搜索失败] {ex}", "搜索失败");
        }
    }

    /// <summary>整行点击进"查看版本"，同 ModListItem_Click 的思路。</summary>
    private void MapListItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not CurseForgeMod mod) return;
        ViewMapVersions_Click(new Button { Tag = mod }, new RoutedEventArgs());
    }

    private async void ViewMapVersions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not CurseForgeMod mod) return;

        var folder = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _owner.ConfigService.Config.Folders.FirstOrDefault();

        if (folder == null)
        {
            MessageBox.Show("请先去「版本选择」页添加一个 .minecraft 文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var gameVersion = MapGameVersionBox.Text?.Trim();
            var files = await GetCurseForge().GetFilesAsync(mod.Id, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);

            if (files.Count == 0)
            {
                MessageBox.Show("这个地图暂无可下载的文件（可能是筛选的游戏版本不匹配）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new CurseForgeMapPickerWindow(GetCurseForge(), folder.Path, mod.Name, files)
                { Owner = Window.GetWindow(this) };
            picker.ShowDialog();
        }
        catch (CurseForgeKeyMissingException ex)
        {
            MessageBox.Show(ex.Message, "未配置 Key", MessageBoxButton.OK, MessageBoxImage.Information);
            Category_Checked(CatMap, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取文件列表失败，可能是网络连接问题，请检查网络后重试。", $"[获取文件列表失败] {ex}", "获取文件列表失败");
        }
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
