using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow : Window
{
    public ConfigService ConfigService { get; } = new();

    /// <summary>全局游戏进程注册表，供主页进程控制按钮组、日志面板、崩溃/注入分析共用。</summary>
    public GameProcessManager ProcessManager { get; } = new();

    /// <summary>已创建的服务器实例列表（服务端管理模块），持久化于 xcl2/servers.json。</summary>
    public ServerInstanceService ServerInstanceService { get; } = new();

    /// <summary>正在运行的服务器进程注册表，供服务端管理页的列表/控制台面板共用。</summary>
    public ServerProcessManager ServerProcessManager { get; } = new();

    private readonly DispatcherTimer _pruneTimer;

    /// <summary>访客模式服务：生成本次会话的临时账户 + 应用退出前清理本次会话产生的日志/临时下载。</summary>
    private readonly GuestModeService _guestModeService = new();

    /// <summary>
    /// "启动游戏"按钮的防手滑冷却：记录上一次点击被接受处理的时间。
    /// 测试时发现连续手滑点击「启动游戏」会在很短时间内触发多次 Launch_Click，
    /// 每次都要走账户校验/Java 检测/下载/进程启动一整套逻辑，多个 MessageBox 弹窗、
    /// 甚至多个游戏进程同时起来，体验很糟。这里用时间戳做一个简单的冷却锁：
    /// 冷却期内的点击直接忽略，不进入下面任何逻辑，也不弹任何提示（静默吞掉），
    /// 避免手滑连点时反而又弹出一堆"操作太快"之类的提示框，制造更多噪音。
    /// </summary>
    private DateTime _lastLaunchClickAtUtc = DateTime.MinValue;

    /// <summary>启动按钮的冷却时长。1 秒足够挡住手滑连点，又不会让正常用户感觉卡顿。</summary>
    private static readonly TimeSpan LaunchClickCooldown = TimeSpan.FromSeconds(1);

    public MainWindow()
    {
        InitializeComponent();
        ConfigService.Load();
        ServerInstanceService.Load();

        // 修复"检测不到以前创建的服务器"：ServerInstanceService.Load() 现在会在主文件损坏时
        // 尝试从 .bak 备份恢复，但恢复与否用户都应该知情——之前这里完全没有任何提示，
        // 配置读取失败和"真的没有服务器"在界面上是无法区分的两种状态。
        if (ServerInstanceService.LastLoadError != null)
        {
            var recovered = ServerInstanceService.Instances.Count > 0;
            MessageBox.Show(
                (recovered
                    ? "服务器列表配置文件（servers.json）读取失败，已自动从备份文件恢复。\n"
                    : "服务器列表配置文件（servers.json）读取失败，且没有可用的备份，服务器列表已重置为空。\n" +
                      "原有服务器的文件本身没有丢失，可以在「服务端管理」页重新创建实例并指向原目录。\n") +
                $"\n错误详情：{ServerInstanceService.LastLoadError.Message}",
                "服务器列表读取异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 访客模式：如果配置里这个开关已经打开(比如上次关闭启动器前就是开启状态)，
        // 一进程序就立即生成本次会话的临时账户，让 GetSelectedAccount 从第一次调用起
        // 就返回这个临时账户，而不是等用户手动去设置页勾一下才生效。
        RefreshGuestModeState();

        RefreshSidebar();
        ShowHome();

        // 定时清理已退出的进程记录，保持"进程管理"列表/按钮的可用性状态是最新的
        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pruneTimer.Tick += (_, _) => ProcessManager.PruneExited();
        _pruneTimer.Start();

        // 应用关闭时，如果访客模式是开启状态，清理本次会话产生的日志/临时下载文件，
        // 不留下这次使用的痕迹。放在 Closed 而不是 Closing，避免清理耗时(理论上很快，
        // 但保险起见)阻塞窗口关闭动画/响应。
        Closed += (_, _) =>
        {
            if (ConfigService.Config.GuestModeEnabled)
            {
                try { _guestModeService.CleanupSessionArtifacts(); }
                catch { /* 清理失败不应该阻止应用退出 */ }
            }
        };

        // 首次启动自动弹出新手引导：用 Loaded 事件延迟到主窗口真正显示之后再弹，
        // 避免向导窗口在主窗口还没渲染完成时就抢先出现、体验突兀。
        if (!ConfigService.Config.FirstRunWizardCompleted)
        {
            Loaded += (_, _) =>
            {
                var wizard = new FirstRunWizardWindow(this) { Owner = this };
                wizard.ShowDialog();
            };
        }
    }

    /// <summary>
    /// 根据 cfg.GuestModeEnabled 的当前值，同步 ConfigService.GuestAccount：
    /// 开启时如果还没有本次会话的临时账户，就生成一个；关闭时清空(GetSelectedAccount 会
    /// 自动回退到真实保存的账户列表)。构造函数里调用一次处理"启动时就是开启状态"，
    /// SettingsPage 保存设置时状态变化了也会调用一次，两处共享这一份逻辑不重复实现。
    /// 调用后会自动刷新侧边栏显示，让账户变化立即反映在界面上。
    /// </summary>
    public void RefreshGuestModeState()
    {
        if (ConfigService.Config.GuestModeEnabled)
        {
            ConfigService.GuestAccount ??= _guestModeService.CreateGuestAccount();
        }
        else
        {
            ConfigService.GuestAccount = null;
        }

        // 访客模式开关一变化就立即重算配色：开启时不管用户平时选的是哪套"持久皮肤"
        // (cfg.UiSkin)，都强制切黑色；关闭时恢复回 cfg.UiSkin。构造函数里第一次调用
        // RefreshGuestModeState 时也会走到这里，保证"启动时访客模式就是开启状态"这种
        // 情况下界面从一开始显示就是黑的，不会先白一下再跳黑。
        ThemeService.ApplyForCurrentState(ConfigService.Config.GuestModeEnabled, ConfigService.Config.UiSkin);

        RefreshSidebar();
    }

    public void RefreshSidebar()
    {
        try
        {
            var acc = ConfigService.GetSelectedAccount();
            CurrentAccountText.Text = acc == null ? "未选择账户" : $"当前账户: {acc.DisplayLabel}";
            CurrentVersionText.Text = string.IsNullOrEmpty(ConfigService.Config.SelectedVersionId)
                ? "未选择版本"
                : $"当前版本: {ConfigService.Config.SelectedVersionId}";
        }
        catch
        {
            // 配置异常不应阻塞主页显示，回退为默认文案
            CurrentAccountText.Text = "未选择账户";
            CurrentVersionText.Text = "未选择版本";
        }
    }

    /// <summary>
    /// 统一的右侧内容区切换入口：所有导航（左侧栏点击、其他页面/窗口调用的 NavigateToXxx）
    /// 都应该通过这里赋值，而不是直接写 MainContent.Content = ...，这样淡入过渡动画
    /// 才能对所有切页场景统一生效。cfg.EnablePageAnimations 关闭时直接退回原来的
    /// "瞬间替换"，不跑任何动画、不产生任何额外开销。
    /// </summary>
    private void SetMainContent(object page)
    {
        if (!ConfigService.Config.EnablePageAnimations)
        {
            // 关闭动画时先清掉可能残留的动画/位移状态，避免"先开着动画切了一次页，
            // 再去设置页关掉动画"这种场景下，MainContent 卡在半透明或偏移的位置上不动。
            MainContent.BeginAnimation(OpacityProperty, null);
            MainContentTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            MainContent.Opacity = 1;
            MainContentTransform.Y = 0;
            MainContent.Content = page;
            return;
        }

        // 新内容先设置好位移起点（往下 14px、透明），赋值 Content 后立即对
        // Opacity + TranslateTransform.Y 同时做一个缓出动画，制造"从下方淡入归位"的
        // 过渡感，而不是生硬地瞬间替换。
        // 先用 BeginAnimation(prop, null) 停掉上一次可能还没播完的动画再重设起始值，
        // 否则连续快速切页时，新动画会从"上一次动画播放到一半的当前值"起步而不是干净的
        // 起始状态，肉眼看起来就像"动画没生效、内容直接跳出来"。
        MainContent.BeginAnimation(OpacityProperty, null);
        MainContentTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);

        MainContentTransform.Y = 14;
        MainContent.Opacity = 0;
        MainContent.Content = page;

        var duration = TimeSpan.FromMilliseconds(260);
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = easeOut };
        var slideUp = new DoubleAnimation(14, 0, duration) { EasingFunction = easeOut };

        MainContent.BeginAnimation(OpacityProperty, fadeIn);
        MainContentTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideUp);
    }

    private void ShowHome()
    {
        var page = new HomePage(this);
        SetMainContent(page);
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void NavVersions_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new VersionSelectPage(this));
    }

    private void NavDownload_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new DownloadCenterPage(this));
    }

    /// <summary>供其他页面/窗口调用的公开导航方法，跳转到下载中心。</summary>
    public void NavigateToDownloadCenter() => SetMainContent(new DownloadCenterPage(this));

    private void NavMultiplayer_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new MultiplayerPage(this));
    }

    /// <summary>供其他页面调用的公开导航方法，跳转到「联机」页（陶瓦联机/红石联机入口）。</summary>
    public void NavigateToMultiplayer() => SetMainContent(new MultiplayerPage(this));

    /// <summary>
    /// 从「联机」页跳转到下载中心并直接按给定关键词搜索 Mod——用于"红石联机"的
    /// 一键搜索安装入口，复用下载中心现成的 Mod 分类 + Modrinth 综合搜索逻辑，
    /// 不需要在联机页里重新实现一遍下载/安装流程。
    /// </summary>
    public void NavigateToDownloadCenterWithModSearch(string keyword)
    {
        var page = new DownloadCenterPage(this);
        SetMainContent(page);
        page.SelectModCategoryAndSearch(keyword);
    }

    private void NavModManager_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new ModManagerPage(this));
    }

    /// <summary>供其他页面/窗口调用的公开导航方法，跳转到本地 Mod 管理页。</summary>
    public void NavigateToModManager() => SetMainContent(new ModManagerPage(this));

    private void NavServerManager_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new ServerManagerPage(this));
    }

    /// <summary>供其他页面/窗口调用的公开导航方法，跳转到服务端管理页。</summary>
    public void NavigateToServerManager() => SetMainContent(new ServerManagerPage(this));

    private void NavAccounts_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new LoginPage(this));
    }

    /// <summary>供其他窗口（如首次启动向导）调用的公开导航方法，跳转到账户管理页。</summary>
    public void NavigateToAccounts() => SetMainContent(new LoginPage(this));

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new SettingsPage(this));
    }

    /// <summary>供其他页面/窗口调用的公开导航方法，跳转到设置页。</summary>
    public void NavigateToSettings() => SetMainContent(new SettingsPage(this));

    private void NavLogs_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new LogsPage(this));
    }

    /// <summary>
    /// 启动游戏的入口，原来只被主窗口左下角的"启动游戏"按钮调用，现在首页磁贴的
    /// "启动游戏"按钮也会调用这个方法（见 HomePage.xaml.cs），改成 public 供跨页面复用，
    /// 两处共享同一套防手滑冷却状态（_lastLaunchClickAtUtc/LaunchGameBtn），不会出现
    /// "首页点了启动、左下角按钮的冷却状态却没跟着更新"这种不一致。
    /// </summary>
    public async void Launch_Click(object sender, RoutedEventArgs e)
    {
        // 防手滑冷却：必须放在方法最开头、任何 await 之前的同步代码里判断，
        // 否则连续点击会在第一次点击的 await 还没跑完时就又进来一次，冷却形同虚设。
        // DateTime.UtcNow 的读取+比较+写回虽然不是原子操作，但 WPF 的事件处理器
        // 本身就是在 UI 线程单线程排队执行的，同一时刻不可能有两个 Launch_Click
        // 真正并发运行，不需要额外加锁。
        var now = DateTime.UtcNow;
        if (now - _lastLaunchClickAtUtc < LaunchClickCooldown || !LaunchGameBtn.IsEnabled)
        {
            // 冷却期内 / 按钮当前就是禁用状态（说明上一次点击触发的流程还没走完，
            // 比如正在等 Java 下载、正在等账户校验的网络请求）时都直接忽略，
            // 不弹提示、不做任何事——手滑连点时不应该再多弹出"点太快了"之类的
            // 提示框，那只会制造更多需要用户点掉的窗口，适得其反。
            return;
        }
        _lastLaunchClickAtUtc = now;

        // 立刻把按钮置灰：这是给用户最直观的反馈——"已经收到你的点击了，正在处理，
        // 不需要再点"。比单纯静默吞掉后续点击更清楚，用户能看到按钮变灰就知道发生了什么，
        // 不会怀疑是不是自己没点到。finally 里保证无论方法从哪个分支退出都会恢复。
        LaunchGameBtn.IsEnabled = false;
        try
        {
            await LaunchInternalAsync(sender, e);
        }
        finally
        {
            // 至少等满冷却时长再真正把按钮恢复可点：如果账户校验/Java 检测很快就失败
            // 返回（比如根本没联网，几十毫秒就抛异常），不加这一步的话按钮会几乎瞬间
            // 重新可点，用户还没反应过来上一次点击发生了什么，又忙不迭点了第二下，
            // 体验上跟没做冷却差不多。用剩余冷却时间兜底，保证"点一下之后最少 1 秒
            // 按钮都摸不到"，真正达到防手滑的效果。
            var elapsed = DateTime.UtcNow - _lastLaunchClickAtUtc;
            var remaining = LaunchClickCooldown - elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining);
            LaunchGameBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 启动游戏的实际逻辑，从 Launch_Click 拆出来，专门用于被防手滑冷却的
    /// try/finally 包裹，避免把冷却相关代码和原有的一大段启动流程混在一起、
    /// 显得臃肿难读。
    /// </summary>
    private async Task LaunchInternalAsync(object sender, RoutedEventArgs e)
    {
        var cfg = ConfigService.Config;
        var account = ConfigService.GetSelectedAccount();
        var folder = cfg.Folders.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath);

        // 需求修复："一键开始游戏"（以及左下角"启动游戏"）之前完全静默调用
        // GetSelectedAccount()，只会自动选中"上次选中/第一个"账户，用户没有机会在这个时间点
        // 选别的账户，只能先跳去"账户管理"页手动切换、再跳回来点启动，多绕一层。
        // 现在改为：有多个账户时（且不是访客模式——访客模式下账户始终是本次会话的临时账户，
        // 不应该被这个选择框打断），弹出账户选择框让用户当场选。只有一个账户/没有账户时
        // 保持原来的行为不变，不会为"没有可选"这种情况也多此一举地弹一次框。
        if (!cfg.GuestModeEnabled && ConfigService.Accounts.Count > 1)
        {
            var picker = new AccountPickerWindow(ConfigService.Accounts, cfg.LastSelectedAccountId) { Owner = this };
            if (picker.ShowDialog() != true)
            {
                // Round16 反馈：之前这里直接静默 return，用户点"取消"后界面毫无反应，
                // 体验上跟"点了没反应/软件卡死"没区别。这里跟同一方法里其它分支
                // （没账户/没选文件夹）一样用 MessageBox 给一句明确提示。
                MessageBox.Show("已取消启动。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            account = picker.SelectedAccount;
            if (account != null)
            {
                if (picker.RememberChoice)
                {
                    // "记住这次选择"：跟账户管理页的切换账户是同一份逻辑(SelectAccount)，
                    // 之后不勾选记住的启动也会默认选中这一个，直到用户下次又手动切换。
                    ConfigService.SelectAccount(account.Id);
                }
                RefreshSidebar();
            }
        }

        if (account == null)
        {
            MessageBox.Show("请先在“账户管理”中登录或创建一个离线账户。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            NavAccounts_Click(sender, e);
            return;
        }
        if (folder == null || string.IsNullOrEmpty(cfg.SelectedVersionId))
        {
            MessageBox.Show("请先在“版本选择”中选择 .minecraft 文件夹和游戏版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            NavVersions_Click(sender, e);
            return;
        }

        try
        {
            // 微软账户：若 access token 即将过期，先静默刷新
            if (account.Type == AccountType.Microsoft &&
                (account.AccessTokenExpiresAtUtc == null || account.AccessTokenExpiresAtUtc < DateTime.UtcNow.AddMinutes(5)))
            {
                if (!string.IsNullOrEmpty(account.MsRefreshToken))
                {
                    var msAuth = new MicrosoftAuthService();
                    var refreshed = await msAuth.RefreshAsync(account.MsRefreshToken);
                    if (refreshed != null)
                    {
                        refreshed.Id = account.Id;
                        ConfigService.AddOrUpdateAccount(refreshed);
                        account = refreshed;
                    }
                }
            }

            var javaService = new JavaService();

            // 版本隔离设置要提前算出来，因为下面判断"这个版本需要 Java 几"时要扫描正确的
            // mods 目录(隔离开启时是 versions/<id>/mods，关闭时是 .minecraft 根目录下的 mods)，
            // 用错目录会导致扫描不到已安装的 mod，从而漏判 Java 版本要求。
            var isolateVersion = cfg.VersionIsolationOverrides.TryGetValue(cfg.SelectedVersionId, out var isolateOverride)
                ? isolateOverride
                : cfg.IsolateVersionsByDefault;

            // 自动匹配 Java，按优先级从高到低：
            //   1) 用户为这个具体版本单独指定的 Java 版本(VersionJavaOverrides)——最高优先级，
            //      用于兜底极端情况(比如某个 mod 没有按标准字段声明 Java 要求导致自动探测漏判)，
            //      用户可以针对单个版本手动指定，不需要牵动全局高级模式设置。
            //   2) 自动探测：version json 的 javaVersion.majorVersion + mods 目录下所有
            //      fabric.mod.json 里 depends.java 声明的最低版本，取两者较大值。
            //      之前的实现只看 version json，装了要求更高 Java 版本的 mod(如本例的
            //      Fabric API/Voice Chat 要求 25+)时完全探测不到，导致下载/选用了版本本体
            //      要求的 21，一进游戏 Fabric Loader 直接报 "Incompatible mods found"。
            //   3) 高级模式下用户设置的全局默认版本(cfg.PreferredJavaMajorVersion)。
            var requiredJavaMajor = LauncherService.GetRequiredJavaMajorVersion(
                folder.Path, cfg.SelectedVersionId, isolateVersion);

            int? preferMajor;
            if (cfg.VersionJavaOverrides.TryGetValue(cfg.SelectedVersionId, out var versionOverride) && versionOverride > 0)
                preferMajor = versionOverride;
            else if (requiredJavaMajor is > 0)
                preferMajor = requiredJavaMajor;
            else
                preferMajor = cfg.AdvancedMode ? cfg.PreferredJavaMajorVersion : null;

            // Java 列表优先级最高：如果用户为这个版本明确选了列表里的某一条(VersionJavaIdOverrides)，
            // 或者虽然没为这个版本单独选、但设了全局默认 Java(SelectedJavaId)，直接用它的路径，
            // 不再走下面的"按主版本号搜索"逻辑——这是用户明确的选择，不需要再猜。
            // 记录的文件如果已经被移动/删除(ResolveJavaPath 返回 null)，则安全回退到旧的搜索逻辑。
            var javaIdOverride = cfg.VersionJavaIdOverrides.TryGetValue(cfg.SelectedVersionId, out var vjid) ? vjid : cfg.SelectedJavaId;
            var javaPath = ConfigService.ResolveJavaPath(javaIdOverride)
                ?? javaService.FindJava(cfg.JavaPath, preferMajor);
            var justDownloaded = false;

            // 启动前 Java 版本匹配检查：javaIdOverride 这条路径(用户在设置里指定了某个具体 Java，
            // 或为这个版本单独指定了 Java 列表里的某一项)是"用户明确的选择"，之前会直接拿去用，
            // 完全不检查它跟 preferMajor(这个版本实际需要的 Java 主版本号)是否匹配——
            // 于是"选了 Java 8 当全局默认，去启动一个要求 Java 21 的版本"这种情况下，
            // 用户会一直被闷头拿着错误的 Java 启动，直到游戏报 UnsupportedClassVersionError 崩溃，
            // 且完全不知道原因。现在改为：这种情况下先弹窗告知"建议改用匹配的 Java"，
            // 用户可以选择"仍然使用当前这个"（尊重用户可能的特殊需求，比如临时测试），
            // 或者"改用推荐的 Java"（自动切到列表里已登记的匹配项，没有就走下载流程）。
            if (javaIdOverride != null && javaPath != null && preferMajor is > 0)
            {
                // TryGetJavaMajorVersionSync 内部会起进程等待退出(最多阻塞 5 秒)，用 Task.Run
                // 丢到线程池执行，避免这几秒内卡住 UI 线程(LaunchInternalAsync 本身是 async 方法)。
                var actualMajor = await Task.Run(() => JavaService.TryGetJavaMajorVersionSync(javaPath));

                if (actualMajor is > 0 && actualMajor != preferMajor)
                {
                    var matchedInList = cfg.InstalledJavas.FirstOrDefault(j => j.MajorVersion == preferMajor);
                    var suggestion = matchedInList != null
                        ? $"列表里已经有登记的 Java {preferMajor}（{matchedInList.Name}），可以直接切换使用。"
                        : $"列表里还没有登记 Java {preferMajor}，选择切换的话会自动下载一个便携版。";

                    bool shouldSwitch;
                    if (cfg.EnforceJavaVersionMatch)
                    {
                        // 强制模式：不给"仍然使用"的选项，弹窗只是告知，点确定就直接切换。
                        MessageBox.Show(
                            $"你为这个版本手动指定的 Java 是 {actualMajor}，但这个版本自动匹配的应该是 Java {preferMajor}" +
                            $"（如果不手动指定，启动器本来会自动帮你选到这个版本）。\n\n" +
                            $"已开启「强制使用匹配 Java」，将自动切换到 Java {preferMajor} 后再启动。\n{suggestion}",
                            "Java 版本不匹配，已自动切换", MessageBoxButton.OK, MessageBoxImage.Warning);
                        shouldSwitch = true;
                    }
                    else
                    {
                        var switchResult = MessageBox.Show(
                            $"你为这个版本手动指定的 Java 是 {actualMajor}，但这个版本自动匹配的应该是 Java {preferMajor}" +
                            $"（如果不手动指定，启动器本来会自动帮你选到这个版本）。用不匹配的版本启动很可能会崩溃" +
                            $"（常见报错如 UnsupportedClassVersionError）。\n\n{suggestion}\n\n" +
                            $"点「是」改用匹配的 Java {preferMajor}；点「否」仍然使用当前这个 Java {actualMajor}（不建议，除非你清楚自己在做什么）。\n\n" +
                            $"提示：可以在「设置」页开启「强制使用匹配 Java」，开启后遇到这种情况会直接自动切换，不再询问。",
                            "Java 版本可能不匹配", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        shouldSwitch = switchResult == MessageBoxResult.Yes;
                    }

                    if (shouldSwitch)
                    {
                        // 改用匹配版本：优先用列表里已登记的匹配项，没有就清空覆盖走回下面的
                        // 自动探测/下载逻辑（preferMajor 已经算好了，FindJava/下载都会用它）。
                        javaPath = matchedInList != null ? ConfigService.ResolveJavaPath(matchedInList.Id) : null;
                        javaPath ??= javaService.FindJava(null, preferMajor);
                    }
                    // 非强制模式选"否"：保留原 javaPath 不变，尊重用户的明确选择。
                }
            }
            if (javaPath == null)
            {
                var versionHint = preferMajor is > 0
                    ? $"这个版本需要 Java {preferMajor}，但未找到匹配的 Java（可能没安装，或已安装的版本不对）。"
                    : "未检测到可用的 Java 环境。";
                var result = MessageBox.Show($"{versionHint}\n是否自动下载对应的便携版 Java？",
                    "需要 Java", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;

                var progressWin = new ProgressWindow("正在下载 Java 运行时...");
                progressWin.Owner = this;
                progressWin.Show();
                try
                {
                    // 下载时同样优先用上面算出的 preferMajor(单版本覆盖 > 自动探测)；
                    // 只有连自动探测都没有结果、且不是高级模式时，才退回旧的
                    // "下载一个通用推荐版本(21)"逻辑。
                    if (preferMajor is > 0)
                    {
                        var arch = cfg.AdvancedMode ? cfg.PreferredJavaArch
                            : (Environment.Is64BitOperatingSystem ? "x64" : "x86");
                        var installMode = cfg.AdvancedMode && cfg.PreferredJavaInstallMode == "System"
                            ? JavaInstallMode.System : JavaInstallMode.Portable;
                        javaPath = await javaService.DownloadJavaAsync(
                            new JavaDownloadRequest(preferMajor.Value, arch, installMode),
                            progressWin.Progress);
                    }
                    else
                    {
                        javaPath = await javaService.DownloadRecommendedJavaAsync(progressWin.Progress);
                    }
                    justDownloaded = true;
                }
                finally { progressWin.Close(); }
            }

            // 只有"手动指定路径为空、这次是靠自动探测/下载补上的"才写回配置里的"便携版已下载"记录；
            // 绝不覆盖用户在设置里手动填写的 JavaPath——那是明确的手动覆盖，写死一条路径反而会让
            // 以后启动其他要求不同 Java 版本的版本时，永远被这一条手动路径卡住，起不到自动匹配的作用。
            if (justDownloaded && string.IsNullOrEmpty(cfg.JavaPath))
            {
                cfg.JavaPath = javaPath;
                ConfigService.Save();
            }

            // 自定义皮肤需要"万能皮肤补丁"(authlib-injector)才能在离线模式下生效。
            // 之前 SkinJvmArgs 完全没有被赋值过，账户选了自定义皮肤也不会真正生效，
            // 且 jar 不存在时 BuildSkinJvmArgs 会静默返回空列表、不会有任何报错提示。
            // 挂在启动前而不是"下载/安装某个版本"时：这样即使用户很早之前就下载好了
            // 版本、后来才改选自定义皮肤，也能在真正启动的这一刻补齐 jar，不会漏掉。
            List<string>? skinJvmArgs = null;
            if (account.Type == AccountType.Offline && account.SkinType == OfflineSkinType.Custom)
            {
                var skinService = new SkinService();
                if (!File.Exists(skinService.AuthlibInjectorPath))
                {
                    var skinProgressWin = new ProgressWindow("正在下载万能皮肤补丁...");
                    skinProgressWin.Owner = this;
                    skinProgressWin.Show();
                    try
                    {
                        await skinService.EnsureAuthlibInjectorAsync(skinProgressWin.Progress);
                    }
                    catch (Exception skinEx)
                    {
                        MessageBox.Show(
                            $"下载万能皮肤补丁失败，本次将不会显示自定义皮肤：\n{skinEx.Message}",
                            "皮肤补丁下载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    finally { skinProgressWin.Close(); }
                }
                skinJvmArgs = skinService.BuildSkinJvmArgs(account, cfg.SkinApiRoot);
            }

            var launcher = new LauncherService();
            var options = new LauncherService.LaunchOptions
            {
                MinecraftDir = folder.Path,
                VersionId = cfg.SelectedVersionId,
                JavaPath = javaPath,
                Account = account,
                MinMemoryMb = cfg.MinMemoryMb,
                MaxMemoryMb = cfg.MaxMemoryMb,
                WindowWidth = cfg.WindowWidth,
                WindowHeight = cfg.WindowHeight,
                ShowConsoleWindow = cfg.EnableGameConsoleWindow,
                IsolateVersion = isolateVersion,
                GameLanguage = cfg.GameLanguage,
                SkinJvmArgs = skinJvmArgs,
                // 自定义 JVM 参数仅在高手模式下生效：普通模式下即使配置里残留了历史值，
                // 也不应该被悄悄应用，避免用户切回普通模式后出现"不知道为什么还生效"的困惑。
                CustomJvmArgs = cfg.AdvancedMode ? cfg.CustomJvmArgs : null,
                PreLaunchCommand = cfg.PreLaunchCommand
            };

            // 导出启动脚本只是附加功能，不应该在失败时阻止真正的游戏启动
            // （之前 GBK 编码问题就是在这一步抛异常，导致下面的 Launch 根本没执行到）。
            // MissingLibrariesException 在这里也可能抛出，但那是"下面 Launch() 也一定会
            // 遇到的同一个问题"，不属于导出脚本独有的失败，放过它冒泡到下面统一处理。
            try { launcher.ExportLaunchScript(options); }
            catch (MissingLibrariesException) { /* 留给下面 Launch() 统一处理 */ }
            catch (Exception exportEx)
            {
                File.AppendAllText(Path.Combine(App.DataDir, "logs", "crash.log"),
                    $"[{DateTime.Now}] 导出启动脚本失败(不影响启动): {exportEx}\n\n");
            }

            GameProcessInfo processInfo;
            try
            {
                processInfo = launcher.Launch(options);
            }
            catch (MissingLibrariesException mle)
            {
                // 远古版本(1.8 及更早)最容易触发这个分支：lwjgl-platform/jinput-platform/
                // twitch-platform 等 natives 库在早期安装时经常因为老版本 classifier 规则
                // 没跟上而遗漏。之前只会弹"请重新安装/补全依赖库"的死路提示，用户还得自己
                // 想办法去哪里"重新安装"——现在提供"自动补全"，复用
                // DownloadService.DownloadLibrariesOnlyAsync 补齐缺失库后原地重试启动。
                var repair = MessageBox.Show(
                    mle.Message + "\n\n是否现在自动下载补全这些缺失的库？",
                    "缺少依赖库", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (repair != MessageBoxResult.Yes) return;

                var repairWin = new ProgressWindow("正在补全缺失的依赖库...");
                repairWin.Owner = this;
                repairWin.Show();
                try
                {
                    var versionDir = Path.Combine(folder.Path, "versions", mle.VersionId);
                    var versionJsonPath = File.Exists(Path.Combine(versionDir, $"{mle.VersionId}.json"))
                        ? Path.Combine(versionDir, $"{mle.VersionId}.json")
                        : Directory.GetFiles(versionDir, "*.json").FirstOrDefault();
                    if (versionJsonPath == null)
                        throw new InvalidOperationException("找不到该版本的 version json，无法确定需要补全哪些库。");

                    var detail = System.Text.Json.JsonSerializer.Deserialize<VersionDetail>(
                        File.ReadAllText(versionJsonPath)) ?? new VersionDetail();
                    detail.Id = mle.VersionId;

                    using var repairDownloader = DownloadService.CreateFromConfig(cfg);
                    // 有 inheritsFrom 时(Fabric/Forge 等)，缺库也可能来自父版本(原版)的库列表，
                    // 两份都要补，跟 BuildArguments 里 AddLibs 对父子两份 json 都扫描的逻辑一致。
                    if (!string.IsNullOrEmpty(detail.InheritsFrom))
                    {
                        var parentDir = Path.Combine(folder.Path, "versions", detail.InheritsFrom);
                        var parentJsonPath = File.Exists(Path.Combine(parentDir, $"{detail.InheritsFrom}.json"))
                            ? Path.Combine(parentDir, $"{detail.InheritsFrom}.json")
                            : (Directory.Exists(parentDir) ? Directory.GetFiles(parentDir, "*.json").FirstOrDefault() : null);
                        if (parentJsonPath != null)
                        {
                            var parentDetail = System.Text.Json.JsonSerializer.Deserialize<VersionDetail>(
                                File.ReadAllText(parentJsonPath)) ?? new VersionDetail();
                            parentDetail.Id = detail.InheritsFrom;
                            await repairDownloader.DownloadLibrariesOnlyAsync(folder.Path, parentDetail, repairWin.Progress);
                        }
                    }
                    await repairDownloader.DownloadLibrariesOnlyAsync(folder.Path, detail, repairWin.Progress);
                }
                catch (Exception repairEx)
                {
                    repairWin.Close();
                    ErrorPresenter.ShowFriendlyError(
                        "自动补全依赖库失败，请检查网络连接后重试，或前往「版本选择」页重新安装该版本。",
                        $"[补全依赖库失败] {repairEx}", "补全失败");
                    return;
                }
                repairWin.Close();

                // 补库之后原地重试一次启动，不需要用户再点一次"启动游戏"按钮。
                processInfo = launcher.Launch(options);
            }
            ProcessManager.Register(processInfo);
            RefreshSidebar();

            // 独立 CMD 日志窗口：可选功能，弹出后实时镜像游戏控制台输出，方便命令行党直接查看。
            if (options.ShowConsoleWindow)
            {
                try { new GameConsoleWindowService().Attach(processInfo); }
                catch { /* 弹窗失败不影响游戏本身启动 */ }
            }

            // 注入检测：游戏启动几秒后（等待游戏进程把自身/mod 的原生库加载完毕），扫描一次模块列表。
            if (cfg.EnableInjectionScan)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    if (processInfo.HasExited) return;
                    try
                    {
                        var scan = new InjectionScanService().Scan(processInfo.Process);
                        if (scan.HasSuspiciousModule)
                        {
                            var names = string.Join("\n", scan.Modules
                                .Where(m => m.Risk == ModuleRisk.Suspicious)
                                .Select(m => $"· {m.FileName}  [{m.MatchedRule}]\n  路径: {m.FullPath}"));
                            Dispatcher.Invoke(() => MessageBox.Show(
                                "注入检测发现可疑模块，游戏进程中可能存在外挂或密码窃取风险：\n\n" + names +
                                "\n\n建议：立即考虑关闭游戏并修改微软账户密码，同时检查这些文件的来源。",
                                "⚠ 注入检测警告", MessageBoxButton.OK, MessageBoxImage.Warning));
                        }
                    }
                    catch { /* 扫描失败不影响正常游戏 */ }
                });
            }

            // "进程启动成功" 不等于 "游戏真的跑起来了"：Java 可能在几百毫秒内因为参数错误/
            // 版本文件损坏等原因直接崩溃退出，之前这里不等待就直接弹"启动成功"，会让用户
            // 误以为游戏在运行，实际上窗口根本不会出现。这里等待最多 3 秒，观察进程是否
            // 提前退出，提前退出就直接把已经捕获到的输出显示出来，而不是撒谎说启动成功。
            var exitedEarly = await Task.Run(async () =>
            {
                for (var i = 0; i < 30; i++)
                {
                    if (processInfo.HasExited) return true;
                    await Task.Delay(100);
                }
                return false;
            });

            if (exitedEarly)
            {
                string output;
                lock (processInfo.OutputBuffer) output = processInfo.OutputBuffer.ToString();
                var tail = output.Length > 3000 ? output[^3000..] : output;
                var exitCode = -1;
                try { exitCode = processInfo.Process.ExitCode; } catch { /* 忽略 */ }

                MessageBox.Show(
                    $"游戏进程刚启动就退出了（退出码 {exitCode}），大概率没有正常运行起来。\n\n" +
                    "最近的控制台输出：\n" + (string.IsNullOrWhiteSpace(tail) ? "(没有捕获到任何输出，可能是 Java 本身启动失败)" : tail) +
                    "\n\n可以到「日志」页的「游戏日志」/「崩溃报告分析」标签查看更多细节。",
                    "启动异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show($"游戏已启动：{account.DisplayLabel} - {cfg.SelectedVersionId}", "启动成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("启动失败，请检查 Java、游戏文件是否完整，或查看「日志」页面获取更多信息。", $"[启动失败] {ex}", "启动失败");
        }
    }

    /// <summary>一键关闭游戏：结束所有正在运行的游戏进程。</summary>
    private void CloseAllGames_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessManager.Running.Count == 0)
        {
            MessageBox.Show("当前没有正在运行的游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show($"确定要关闭全部 {ProcessManager.Running.Count} 个正在运行的游戏吗？",
            "一键关闭游戏", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            ProcessManager.CloseAll();
            RefreshSidebar();
        }
    }

    /// <summary>关闭所选的游戏：打开进程列表窗口，用户选中一个后关闭。</summary>
    private void CloseSelectedGame_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessManager.Running.Count == 0)
        {
            MessageBox.Show("当前没有正在运行的游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new ProcessManagerWindow(ProcessManager, ProcessManagerWindow.Mode.SelectToClose) { Owner = this };
        win.ShowDialog();
        RefreshSidebar();
    }

    /// <summary>
    /// 关闭未响应的游戏：不会自动判断"卡死"，而是打开进程列表让用户勾选确认无响应的游戏，
    /// 勾选后才允许强制结束，避免误杀正在正常游玩的进程。
    /// </summary>
    private void CloseUnresponsiveGame_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessManager.Running.Count == 0)
        {
            MessageBox.Show("当前没有正在运行的游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new ProcessManagerWindow(ProcessManager, ProcessManagerWindow.Mode.MarkUnresponsive) { Owner = this };
        win.ShowDialog();
        RefreshSidebar();
    }
}
