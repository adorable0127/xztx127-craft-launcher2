using System;
using System.IO;
using System.Threading.Tasks;
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

    /// <summary>「自动循环」深浅色模式的定时检查器：每分钟醒一次，比较当前系统时间落在
    /// 哪个区间（浅色/深色），需要切换时才动配置+重新应用配色，不需要切换时什么都不做——
    /// 这样即使用户在两次检查之间手动点了「模式设置」按钮临时覆盖，也不会被这个每分钟的
    /// 检查在同一个时间段内反复纠正回去（见 AppConfig.AutoThemeLastAppliedSlotStartHour
    /// 的"手动优先"注释）。</summary>
    private readonly DispatcherTimer _autoThemeCycleTimer;

    /// <summary>访客模式服务：生成本次会话的临时账户 + 应用退出前清理本次会话产生的日志/临时下载。</summary>
    private readonly GuestModeService _guestModeService = new();

    /// <summary>
    /// 系统内存监视：全程后台运行（不局限于"有游戏在跑"才监控），因为下载/安装模组、
    /// 解压大文件等操作同样可能把系统内存吃满；一旦检测到可用内存过低就弹出警告窗口，
    /// 提醒用户在系统卡死/蓝屏之前主动关闭游戏进程，而不是等真的撑爆了才发现。
    /// </summary>
    private readonly MemoryWatchdogService _memoryWatchdog = new();

    /// <summary>避免同一时刻已经有一个内存警告窗口在显示时又弹出第二个。</summary>
    private MemoryWarningWindow? _activeMemoryWarningWindow;

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

        // 启动时自动扫描本机的 .minecraft 文件夹（AppData + 每个磁盘 1/2/3 级目录），
        // 找到新的就自动加入"版本选择"页的文件夹列表，不需要用户手动一个个"添加文件夹"。
        // 放到 Loaded 之后用后台线程跑：扫描要枚举磁盘目录，慢盘/大量文件的机器上可能要
        // 几秒甚至更久，不能放在构造函数里同步跑（会让主窗口卡在黑屏/白屏好几秒才显示出来）。
        // 扫描结果通过 Dispatcher 切回 UI 线程再保存配置 + 弹提示，避免跨线程直接改
        // ConfigService.Config 或者操作 UI 控件。
        Loaded += (_, _) => _ = ScanMinecraftFoldersInBackgroundAsync();

        // 定时清理已退出的进程记录，保持"进程管理"列表/按钮的可用性状态是最新的
        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pruneTimer.Tick += (_, _) => ProcessManager.PruneExited();
        _pruneTimer.Start();

        // 「自动循环」深浅色模式：之前是每分钟检查一次。反馈里出现过"到了设定的切换时间，
        // 界面没有自动变成深色，非要重启启动器一次才生效"的情况——ReevaluateAutoThemeCycle
        // 本身的判断逻辑没问题（按小时比较+去重的 slot 标识），但1分钟的间隔在一些机器上
        // （比如系统进入过短暂休眠/UI 线程短暂阻塞导致某次 Tick 被跳过、或者用户就是没那么
        // 巧等到下一次整分钟 Tick）会让人感觉"过了好一会儿还没切换"，容易被误判成"完全不生效"，
        // 直到重启走一遍构造函数里那次立即校验（见下面 ReevaluateAutoThemeCycle() 调用）才
        // 骤然发现变了，看起来就像"必须重启才生效"。
        // 改成每 1 秒检查一次：DispatcherTimer 本身开销很小（ReevaluateAutoThemeCycle 内部
        // 大部分时间是"当前 slot 没变，直接 return"的快速路径，真正切换配色的分支一小时最多
        // 触发一次），一秒级的粒度足够消除上述"感觉卡住不切换"的体验问题，用户不需要再重启。
        _autoThemeCycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoThemeCycleTimer.Tick += (_, _) => ReevaluateAutoThemeCycle();
        _autoThemeCycleTimer.Start();
        ReevaluateAutoThemeCycle();

        // 内存溢出预警：每 5 秒检查一次系统可用物理内存，跌破阈值（默认低于 10% 或
        // 低于 1GB，两者任一满足）就弹出警告窗口，让用户能在系统真正卡死/蓝屏之前
        // 主动关闭游戏。事件回调可能不在 UI 线程上触发，用 Dispatcher 切回来再弹窗。
        _memoryWatchdog.LowMemoryDetected += args =>
        {
            Dispatcher.Invoke(() => ShowMemoryWarning(args));
        };
        _memoryWatchdog.Start();

        Closed += (_, _) => _memoryWatchdog.Dispose();

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

        // 访客模式开关变化本身不再影响配色：配色完全以用户当前的色系(cfg.UiSkin)+
        // 明暗(cfg.IsDarkMode)选择为准，访客模式只负责临时账户的创建/清空，两者完全解耦。
        // 这里仍然调一次 ApplyForCurrentState 是为了保证"设置项保存后一秒内必须刷新界面"
        // 这个约定——访客模式开关本身也是一种设置项变化，调用方（SettingsPage/HomePage）
        // 保存完 GuestModeEnabled 后立刻调这个方法，顺带把当前配色重新应用一次、
        // 触发全窗口刷新，不需要用户切页/重启才能看到访客模式开关本身的即时反馈。
        ThemeService.ApplyForCurrentState(ConfigService.Config.GuestModeEnabled, ConfigService.Config.UiSkin, ConfigService.Config.IsDarkMode);

        RefreshSidebar();
    }

    /// <summary>
    /// 弹出内存不足警告窗口。同一时刻只保留一个警告窗口实例（避免多次触发时叠出一堆
    /// 弹窗把屏幕糊住），如果当前没有正在运行的游戏进程，说明内存紧张的来源不是本启动器
    /// 拉起的游戏（可能是下载/解压占用，或者纯粹是用户其它程序占用的），此时弹一个"没有
    /// 可关闭游戏进程"的提示意义不大，直接跳过，避免无谓打扰。
    /// </summary>
    private void ShowMemoryWarning(MemoryWatchdogService.LowMemoryEventArgs args)
    {
        if (_activeMemoryWarningWindow != null) return;
        if (ProcessManager.Running.Count == 0) return;

        _activeMemoryWarningWindow = new MemoryWarningWindow(ProcessManager, _memoryWatchdog, args);
        _activeMemoryWarningWindow.Closed += (_, _) => _activeMemoryWarningWindow = null;
        _activeMemoryWarningWindow.Show();
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
    /// 启动时自动扫描 AppData + 各磁盘 1/2/3 级目录下的 .minecraft 文件夹，新发现的自动
    /// 加进"版本选择"页的文件夹列表。扫描本身（MinecraftFolderScanService.ScanAndRegister）
    /// 全是同步的文件系统 IO，用 Task.Run 丢到线程池执行，避免枚举磁盘目录时卡住 UI 线程；
    /// 扫描完成后用 Dispatcher 切回 UI 线程再保存配置、刷新侧边栏、弹提示——
    /// ConfigService.Config 不是线程安全类型，所有实际修改都必须在 UI 线程上做。
    /// 扫描/保存过程中出的任何异常都只记日志、不打断启动流程，也不弹错误框打扰用户
    /// （这本来就是一个"顺手帮你找找看"的辅助功能，找不到、扫失败都不应该造成困扰）。
    /// </summary>
    private async Task ScanMinecraftFoldersInBackgroundAsync()
    {
        try
        {
            var newlyAdded = await Task.Run(() => MinecraftFolderScanService.ScanAndRegister(ConfigService.Config));
            if (newlyAdded.Count == 0) return;

            ConfigService.Save();
            RefreshSidebar();

            var names = string.Join("\n", newlyAdded.Select(f => $"• {f.Name}  ({f.Path})"));
            MessageBox.Show(
                $"启动时自动发现了 {newlyAdded.Count} 个新的 .minecraft 文件夹，已加入「版本选择」页的文件夹列表：\n\n{names}",
                "自动发现新文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(App.DataDir, "logs", "crash.log"),
                $"[{DateTime.Now}] [自动扫描.minecraft文件夹失败] {ex}\n\n"); }
            catch { /* 连日志都写不进去就彻底放弃，不影响启动器正常使用 */ }
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
        // 修复"切换大界面时下载窗口还在"：旧页面（比如下载中心）弹出的 ProgressWindow
        // 是独立顶层窗口，不会因为 MainContent.Content 被替换而自动关闭。这里在真正
        // 切换内容之前统一关掉所有当前存活的进度弹窗，视为"打断操作"。
        Views.ProgressWindow.CloseAll();

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

    /// <summary>
    /// 「自动循环」核心逻辑：如果 cfg.AutoThemeCycleEnabled 关闭，什么都不做——完全交给
    /// 用户手动控制。开启时，按当前系统时间判断现在应该是哪个模式(浅色区间 = 从
    /// AutoThemeLightStartHour 到 AutoThemeDarkStartHour 之前；深色区间 = 从
    /// AutoThemeDarkStartHour 到次日 AutoThemeLightStartHour 之前，正确处理"深色区间跨过
    /// 午夜"的情况)，只有这个判断结果对应的"时间段标识"跟上次已经应用过的不一样时才真正
    /// 切换配置+重新应用配色——这保证了"手动优先"：用户在同一个时间段内手动点了「模式设置」
    /// 临时覆盖后，这里不会每分钟都把它纠正回去，只有真正跨入下一个新的时间段才会重新接管。
    ///
    /// 由三处触发：(1) MainWindow 构造函数里启动时立即校验一次；(2) _autoThemeCycleTimer
    /// 每分钟 Tick 一次；(3) 用户在首页刚打开「自动循环」开关，或在设置页刚改了两个切换
    /// 时间点之后，立即调用一次，保证"设置项保存后一秒内必须看到界面刷新"。
    /// </summary>
    public void ReevaluateAutoThemeCycle()
    {
        var cfg = ConfigService.Config;
        if (!cfg.AutoThemeCycleEnabled) return;

        var lightStart = Math.Clamp(cfg.AutoThemeLightStartHour, 0, 23);
        var darkStart = Math.Clamp(cfg.AutoThemeDarkStartHour, 0, 23);
        var nowHour = DateTime.Now.Hour;

        // 判断当前小时落在"浅色区间"还是"深色区间"，同时记录这个区间的标识（用区间自己的
        // 起始小时当标识即可，浅色区间标识 = lightStart，深色区间标识 = darkStart）。
        // 区间可能跨越午夜（比如深色 19 点开始、浅色 8 点开始，19~23 和 0~7 都属于深色区间），
        // 所以不能简单判断 nowHour >= darkStart，要分 lightStart < darkStart（同一天内浅->深）
        // 和 lightStart >= darkStart（异常配置，两者相等或反过来）两种情况处理。
        bool isLightNow;
        if (lightStart == darkStart)
        {
            // 两个时间点设成一样：没有意义的配置，兜底为"始终浅色"，不让用户看到自动循环
            // 在这种边界情况下抛异常或者死循环判断。
            isLightNow = true;
        }
        else if (lightStart < darkStart)
        {
            // 正常情况：比如浅色 8 点、深色 19 点，[8,19) 是浅色，其余(含跨午夜)是深色。
            isLightNow = nowHour >= lightStart && nowHour < darkStart;
        }
        else
        {
            // 反过来的配置：比如浅色 22 点、深色 6 点，[22,24)+[0,6) 是浅色，[6,22) 是深色。
            isLightNow = nowHour >= lightStart || nowHour < darkStart;
        }

        var targetSlotId = isLightNow ? lightStart : -(darkStart + 1); // 用负数区分深色区间标识，避免跟浅色标识撞在同一个数值上（0 点开始的浅色 vs 0 点开始的深色）
        if (cfg.AutoThemeLastAppliedSlotStartHour == targetSlotId) return; // 同一个时间段内已经应用过，遵守"手动优先"，不重复纠正

        cfg.AutoThemeLastAppliedSlotStartHour = targetSlotId;
        cfg.IsDarkMode = !isLightNow;
        ConfigService.Save();

        ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, cfg.UiSkin, cfg.IsDarkMode);

        // 首页「模式设置」按钮显示的是缓存在 HomePage 里的旧勾选状态，配色已经变了但按钮
        // 文案还没跟上，这里如果当前正显示首页就顺手刷新一下，避免出现"背景已经变深，
        // 按钮却还写着浅色模式"的不一致。
        if (MainContent?.Content is HomePage homePage)
        {
            homePage.RefreshThemeToggles();
        }
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void NavVersions_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new VersionSelectPage(this));
    }

    /// <summary>
    /// 修复"一锅乱炖"（多加载器合装）装完之后启动器找不到新版本的问题：
    /// VersionSelectPage 只在自己构造函数里扫描一次 versions/ 目录，装完之后没人告诉它
    /// "该重新扫一遍了"。这里如果当前主内容区正好显示的就是版本选择页，就直接重新 new
    /// 一个换上去（VersionSelectPage 的构造函数本身就会做一次干净的目录扫描），
    /// 新装好的版本文件夹自然就出现在列表里了；如果用户当前在别的页面，
    /// 则什么都不用做——下次导航到版本选择页时反正会重新 new 一个实例、重新扫描。
    /// </summary>
    public void RefreshVersionsPageIfActive()
    {
        if (MainContent.Content is VersionSelectPage)
        {
            SetMainContent(new VersionSelectPage(this));
        }
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

    private void NavExperimental_Click(object sender, RoutedEventArgs e)
    {
        OpenExperimentalFeatures();
    }

    /// <summary>
    /// "实验性功能"统一入口：第一次打开（cfg.ExperimentalFeaturesUnlocked 还是 false）先弹
    /// ExperimentalGateWindow 强制等待 10 秒确认，确认过一次之后这个标记会持久化保存，
    /// 后续再打开直接展示面板，不需要重复罚站。
    /// 侧边栏"实验性功能"按钮和「设置」页里原来的入口都调这一个方法，避免同一段
    /// "先查/写 ExperimentalFeaturesUnlocked，再决定要不要弹网关窗口"的逻辑在两个地方各写一遍、
    /// 以后改一处忘了改另一处。
    /// </summary>
    public void OpenExperimentalFeatures()
    {
        var cfg = ConfigService.Config;

        if (!cfg.ExperimentalFeaturesUnlocked)
        {
            var gate = new ExperimentalGateWindow { Owner = this };
            gate.ShowDialog();

            if (!gate.Confirmed) return; // 用户取消/关闭窗口：不解锁，不打开实验性功能面板

            cfg.ExperimentalFeaturesUnlocked = true;
            ConfigService.Save();
        }

        var window = new ExperimentalFeaturesWindow(this) { Owner = this };
        window.ShowDialog();
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
            // 微软账户：若 access token 即将过期，先静默刷新。
            // 根因修复（"账户管理显示已登录微软账户，进游戏却变成 Demo 试玩"）：
            // 之前无论刷新成功与否，只要没抛异常就会往下走去启动游戏——刷新失败
            // （RefreshAsync 返回 null，比如 refresh token 已过期/被吊销/网络问题）
            // 或者压根没有 MsRefreshToken 时，会原样带着已经过期的旧 access token
            // 拼进启动参数。Minecraft 收到无效/过期的 accessToken 不会报错，而是
            // 静默降级成离线试玩(Demo)模式——这正是现象的根源。
            // 现在改成：刷新失败/无 refresh token 可用时，只要 access token 确实已过期，
            // 就直接终止启动流程并提示用户重新登录，不再拿失效凭证去启动游戏。
            if (account.Type == AccountType.Microsoft &&
                (account.AccessTokenExpiresAtUtc == null || account.AccessTokenExpiresAtUtc < DateTime.UtcNow.AddMinutes(5)))
            {
                Account? refreshed = null;
                if (!string.IsNullOrEmpty(account.MsRefreshToken))
                {
                    var msAuth = new MicrosoftAuthService();
                    refreshed = await msAuth.RefreshAsync(account.MsRefreshToken);
                }

                if (refreshed != null)
                {
                    refreshed.Id = account.Id;
                    ConfigService.AddOrUpdateAccount(refreshed);
                    account = refreshed;
                }
                else if (account.AccessTokenExpiresAtUtc == null || account.AccessTokenExpiresAtUtc < DateTime.UtcNow)
                {
                    // access token 已经确实过期、且刷新拿不到新的——不能再往下启动，
                    // 否则就是本节注释描述的"静默变 Demo"现象。
                    MessageBox.Show(
                        $"账户「{account.Username}」的登录状态已过期，且自动刷新失败，请重新登录微软账户后再启动游戏。\n" +
                        "（如果直接用过期状态启动，Minecraft 会静默进入离线试玩(Demo)模式而不会报错，" +
                        "为避免这种情况这里主动拦截。）",
                        "需要重新登录", MessageBoxButton.OK, MessageBoxImage.Warning);
                    NavAccounts_Click(sender, e);
                    return;
                }
                // else: token 还没到硬过期时间（只是进入 5 分钟提前刷新窗口)，刷新虽失败但
                // 旧 token 短期内应该仍然有效，容许继续启动，避免因为一次偶发的网络抖动
                // 就完全无法进游戏。
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

            // 自定义皮肤/认证服务器(AuthServer)账户都需要"万能皮肤补丁"(authlib-injector)
            // 才能在客户端里正确显示皮肤、通过对应服务器的会话校验。
            //
            // 修复：这里原来只判断"离线账户 + 自定义皮肤"，完全没覆盖 AuthServer 账户——
            // AuthServer 账户的登录/取 token/启动传参这条主链路本身是完整可用的，
            // 唯独这里"首次启动自动下载 jar"的条件写漏了 AuthServer 分支。
            // 后果就是：如果用户电脑上从来没下载过 authlib-injector.jar，第一次用皮肤站
            // 账户启动时，这个 if 直接不成立 -> EnsureAuthlibInjectorAsync 根本不会被调用
            // -> jar 依然不存在 -> BuildSkinJvmArgs 内部的 File.Exists 检查失败，
            // 静默返回空列表，玩家会发现皮肤没生效、也没有任何报错提示，一头雾水。
            // 现在两种情况统一判断"这个账户是否需要皮肤补丁"，需要就统一走同一套
            // "jar 不存在则先下载"的流程，跟离线自定义皮肤完全一致的体验。
            //
            // 挂在启动前而不是"下载/安装某个版本"时：这样即使用户很早之前就下载好了
            // 版本、后来才改选自定义皮肤/切换成皮肤站账户，也能在真正启动的这一刻补齐 jar，不会漏掉。
            List<string>? skinJvmArgs = null;
            var needsAuthlibInjector =
                (account.Type == AccountType.Offline && account.SkinType == OfflineSkinType.Custom) ||
                (account.Type == AccountType.AuthServer && !string.IsNullOrWhiteSpace(account.AuthServerApiRoot));

            if (needsAuthlibInjector)
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
                        var hint = account.Type == AccountType.AuthServer
                            ? "下载万能皮肤补丁失败，本次将无法通过认证服务器的皮肤/会话校验：\n"
                            : "下载万能皮肤补丁失败，本次将不会显示自定义皮肤：\n";
                        MessageBox.Show(
                            hint + skinEx.Message,
                            "皮肤补丁下载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    finally { skinProgressWin.Close(); }
                }
                skinJvmArgs = skinService.BuildSkinJvmArgs(account, cfg.SkinApiRoot);
            }

            // "开启后进入某某某服务器"：按当前选中版本 id 查一次是否配置了自动进服务器地址，
            // 没配置/配置为空白都传 null，LauncherService 内部会原样跳过 quickPlayMultiplayer
            // 这个参数，行为等同于这个功能上线前——纯增量开关，不影响没设置过的实例。
            string? autoJoinServer = null;
            if (cfg.VersionAutoJoinServer.TryGetValue(cfg.SelectedVersionId, out var configuredServer)
                && !string.IsNullOrWhiteSpace(configuredServer))
            {
                autoJoinServer = configuredServer.Trim();
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
                PreLaunchCommand = cfg.PreLaunchCommand,
                AutoJoinServerAddress = autoJoinServer
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
