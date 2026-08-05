using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>
    /// 需求："点击'启动游戏'到等待游戏窗口出现的间隙，把按钮改成'取消启动'；
    /// 点击'取消启动'不弹出账户选择框，直接中止本次启动流程"。
    /// 这个 CancellationTokenSource 在 Launch_Click 进入"处理中"状态时创建，
    /// LaunchInternalAsync 内部所有可以安全中止的等待点（下载/安装、等待游戏窗口出现的轮询）
    /// 都会传入它的 Token；用户点"取消启动"时调用它的 Cancel()，流程在下一个检查点
    /// 自行退出，不需要真的杀掉刚拉起的游戏进程（进程可能已经起来了，取消只是让启动器
    /// 不再继续等待/不再弹出后续确认框，不影响已经拉起的进程本身）。
    /// </summary>
    private CancellationTokenSource? _launchCts;

    /// <summary>
    /// "启动游戏"→"取消启动"→"启动游戏" 之间来回切换的最短间隔。
    /// 需求明确要求保留 1~2 秒，避免用户在两个状态之间快速连点造成
    /// "启动-取消-启动-取消"的死循环（比如手滑连点，或者误以为没点中而反复点）。
    /// 取区间中点 1.5 秒：比 LaunchClickCooldown 的 1 秒稍宽松一点，因为这里挡的是
    /// "点了取消/点了启动"这种状态切换动作本身，而不是同一个按钮的连续误触。
    /// </summary>
    private static readonly TimeSpan LaunchStateSwitchGuard = TimeSpan.FromSeconds(1.5);

    /// <summary>上一次"启动游戏"⇄"取消启动"两个状态之间切换的时间，配合 LaunchStateSwitchGuard 使用。</summary>
    private DateTime _lastLaunchStateSwitchAtUtc = DateTime.MinValue;

    /// <summary>当前是否处于"取消启动"可点击状态（即已经在启动流程中，按钮显示为取消）。</summary>
    private bool _isCancelLaunchState;

    /// <summary>记录窗口最近一次处于"非最小化"状态时的 WindowState(Normal 或 Maximized)。
    /// 修复"启动/下载成功弹窗出现时启动器窗口会自动最小化"：如果窗口在弹窗前已经被系统
    /// (或前台焦点被刚拉起的游戏进程抢走)意外最小化，简单粗暴地把 WindowState 设成
    /// Normal 会导致原本是最大化的窗口意外变回还原态；这里记住恢复前的真实状态，
    /// 保证"最小化前是最大化"的窗口在恢复时也还是最大化，而不是每次都被强制还原成小窗。</summary>
    private WindowState _lastNonMinimizedWindowState = WindowState.Normal;

    /// <summary>修复"启动成功/下载成功等提示弹窗出现时启动器主窗口会自动最小化"：耗时操作
    /// (下载、安装、启动游戏等)执行期间，用户可能切到了其它窗口，或者刚拉起的游戏进程抢走了
    /// 前台焦点，导致主窗口在弹提示的这一刻已经不是前台/甚至被系统最小化。各个页面
    /// (DownloadCenterPage/ModManagerPage 等)在弹出"成功"提示前调用这个方法，统一把主窗口
    /// 从 Minimized 恢复到恢复前的真实状态(Normal 或 Maximized，见 _lastNonMinimizedWindowState
    /// 的注释)并带到前台，不需要每个调用点各自处理窗口状态。</summary>
    public void EnsureVisibleForDialog()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _lastNonMinimizedWindowState;
        Activate();
    }

    public MainWindow()
    {
        InitializeComponent();
        ApplyFeatureVisibility();

        // 标题栏跟随深浅色模式（修复"顶部白条"）现在由 App.xaml.cs 里注册的
        // EventManager.RegisterClassHandler 对所有 Window 统一处理，MainWindow 不需要
        // 再单独接线，见 WindowChromeService 类注释。

        // F11 全屏切换：只在主窗口生效，Key.System 分支处理是因为 F11 在部分系统/输入法
        // 状态下会被识别为"系统键"（Alt 组合键那一类路由），PreviewKeyDown 阶段
        // e.Key == Key.System 时真正的键值在 e.SystemKey 里，两个都要判断到才不会漏掉。
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.F11) return;
            WindowChromeService.ToggleFullScreen(this);
            e.Handled = true;
        };

        // Esc 关闭当前最顶层的进程内弹窗（Overlay），跟 Windows 系统对话框的通行习惯
        // 一致。放在这里而不是每个弹窗 UserControl 自己接线，是因为 Esc 需要在整个
        // MainWindow 范围内都能生效（弹窗内部任意控件获得焦点时都要能按 Esc 关闭），
        // 而不是只在弹窗自己的可视化树内监听——同一个原因，F11 全屏判断也放在这一层。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (!OverlayDialogService.HasActiveOverlay) return;
            OverlayDialogService.RequestDismissTopByEscape();
            e.Handled = true;
        };

        // F12：临时显示被"功能隐藏"设置隐藏起来的功能项，方便用户手滑隐藏了什么之后
        // 还能找回入口去设置页取消勾选。这是"按下就切换一次状态"，不是"按住才显示"——
        // 再按一次 F12 变回正常隐藏状态。只在这里改内存里的标记 + 立即重新应用一次
        // 导航栏可见性，不碰 HiddenFeatureKeys 配置本身，松开也不会自动还原。
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.F12) return;
            FeatureVisibilityService.TemporaryRevealActive = !FeatureVisibilityService.TemporaryRevealActive;
            ApplyFeatureVisibility();
            e.Handled = true;
        };

        // 注册为 Overlay 弹窗宿主：进程内只有一个 MainWindow 实例，全部 24 个原独立
        // Window 弹窗迁移后都通过 OverlayDialogService 挂载到这里的 OverlayRoot。
        // 见 OverlayDialogService.cs 顶部的整体设计注释。
        OverlayDialogService.Register(this);
        // Toast 通知层宿主（右下角自动消失的轻提示，见 ToastService 类头注释）。
        ToastService.Register(this);

        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastNonMinimizedWindowState = WindowState;
        };

        // 「实验性功能」导航按钮只在启动器界面语言是简体中文时显示（见
        // LocalizationService.ExperimentalFeaturesLanguageGate 注释）。构造时立即按当前语言
        // 同步一次，并订阅 LanguageChanged，保证用户在运行时切换语言后这个按钮立即跟着
        // 显示/隐藏，不需要重启或切页面才生效。
        RefreshExperimentalNavVisibility();
        LocalizationService.LanguageChanged += RefreshExperimentalNavVisibility;
        Closed += (_, _) => LocalizationService.LanguageChanged -= RefreshExperimentalNavVisibility;

        // 侧边栏"收起/展开"初始状态：默认展开（跟原来的固定 180 宽行为一致），
        // 不做持久化——每次启动都是展开态，避免"上次不小心点收起了，下次开机
        // 一脸懵不知道导航栏去哪了"这种体验问题。
        ApplySidebarCollapsedState(collapsed: false);

        ConfigService.Load();
        ServerInstanceService.Load();

        // 修复"检测不到以前创建的服务器"：ServerInstanceService.Load() 现在会在主文件损坏时
        // 尝试从 .bak 备份恢复，但恢复与否用户都应该知情——之前这里完全没有任何提示，
        // 配置读取失败和"真的没有服务器"在界面上是无法区分的两种状态。
        if (ServerInstanceService.LastLoadError != null)
        {
            var recovered = ServerInstanceService.Instances.Count > 0;
            MessageBoxDialog.ShowWarning(
                (recovered
                    ? "服务器列表配置文件（servers.json）读取失败，已自动从备份文件恢复。\n"
                    : "服务器列表配置文件（servers.json）读取失败，且没有可用的备份，服务器列表已重置为空。\n" +
                      "原有服务器的文件本身没有丢失，可以在「服务端管理」页重新创建实例并指向原目录。\n") +
                $"\n错误详情：{ServerInstanceService.LastLoadError.Message}",
                "服务器列表读取异常");
        }

        // 访客模式：如果配置里这个开关已经打开(比如上次关闭启动器前就是开启状态)，
        // 一进程序就立即生成本次会话的临时账户，让 GetSelectedAccount 从第一次调用起
        // 就返回这个临时账户，而不是等用户手动去设置页勾一下才生效。
        RefreshGuestModeState();

        RefreshSidebar();
        ShowHome();

        // 启动时自动扫描本机的 .minecraft 文件夹（AppData + 每个磁盘 1/2/3 级目录），
        // 找到新的就自动加入"版本选择"页的文件夹列表，不需要用户手动一个个"添加文件夹"。
        // 放到窗口真正显示出来之后用后台线程跑：扫描要枚举磁盘目录，慢盘/大量文件的机器上
        // 可能要几秒甚至更久，不能放在构造函数里同步跑（会让主窗口卡在黑屏/白屏好几秒才
        // 显示出来）。扫描结果通过 Dispatcher 切回 UI 线程再保存配置 + 弹提示，避免跨线程
        // 直接改 ConfigService.Config 或者操作 UI 控件。
        //
        // 修复"首次打开白屏，要手动拖动/全屏窗口才会渲染出内容"：这里以及下面 Java 扫描/
        // 新手向导三处，原来都是挂在 Loaded 事件上。Loaded 只代表"可视化树已经连接完毕"，
        // WPF 并不保证这时候已经真正走完一次 Measure/Arrange/Render 把内容画到屏幕上——
        // 如果 Loaded 的回调本身还在做事(哪怕只是启动一个 Task.Run、注册几个事件)，
        // 就会继续占着 UI 线程，把"排队中但还没真正执行"的首帧渲染往后推，表现就是
        // "看起来是白屏，直到用户做了一次窗口大小变化才强制触发一次布局/渲染"。
        // 改成挂在 ContentRendered 上：这是 WPF 专门用来表示"第一帧（以及此后每一次
        // 内容变化触发的重新渲染）已经真正合成绘制完毕"的事件，在它触发之前，
        // WPF 内部会保证先完整走完一遍 Layout+Render，不会被同一批 Loaded 回调抢占——
        // 从根上避免"渲染工作还没来得及执行就被其它逻辑挤到后面"这个不确定性，
        // 不需要再靠 Dispatcher.Invoke(..., Render) 这种猜时序的占位技巧。
        if (ConfigService.Config.FirstRunWizardCompleted)
        {
            ContentRendered += (_, _) => _ = ScanMinecraftFoldersInBackgroundAsync();
        }

        // 需求："在启动的时候，像检测mc的目录一样检测 Javaw.exe，无需用户手动打开设置，
        // 就可以生成 java 列表。"——跟上面 MC 文件夹扫描完全同一套模式：主窗口真正显示
        // 出来之后台线程跑，静默登记，不打断/不阻塞主窗口显示。之前只有打开「设置」页时
        // 才会触发 AutoDetectJavaOnLoadAsync（见 SettingsPage.xaml.cs），用户不点进设置页
        // 永远不会自动发现新装的 Java，这里补上真正"程序启动时"这一级的自动探测。
        ContentRendered += (_, _) => _ = ScanJavaInBackgroundAsync();

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

        // 需求："启动器每次关闭时会自动生成会话日志"。写在访客模式清理**之前**：
        // 访客模式清理会删掉本次会话新产生的日志文件（见 GuestModeService.CleanupNewLogFiles
        // 注释——"不留下这次使用的痕迹"是访客模式的既定设计），如果反过来先清理再落盘，
        // 访客模式下这个文件会残留下来，跟"访客模式不留痕迹"的承诺矛盾。非访客模式下
        // 顺序无所谓，这里统一放前面简化逻辑。
        Closed += (_, _) => LauncherLogService.EndSessionAndFlush();

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

        // 首次启动自动弹出新手引导：挂在 ContentRendered 而不是 Loaded 上（原因见上面
        // MC 文件夹/Java 扫描两处的注释——Loaded 不保证首帧已经真正画出来，向导这种
        // 立刻弹出的模态 Overlay 如果在 Loaded 里弹，很容易跟"主窗口自己的首帧渲染"抢
        // UI 线程，表现就是主窗口白屏、向导也显示不全）。ContentRendered 在窗口每次
        // 内容重新渲染完成后都会触发，这里用 EventHandler 局部变量 + 立即反订阅实现
        // "只在首帧渲染完成后弹一次"，避免后续任何触发 ContentRendered 的操作
        // （比如窗口尺寸变化、主题切换引起的视觉刷新）意外把向导再弹一次。
        //
        // 新手引导走完/关闭之后才补跑一次 .minecraft 文件夹自动扫描（原因见上面
        // ScanMinecraftFoldersInBackgroundAsync 调用点的注释：避免扫描结果提示框
        // 在向导进行到一半时插队压栈，把向导流程打断/挡住）。这里不区分用户是正常走完
        // 向导还是中途关掉——不管哪种，向导这个 Overlay 已经让出了 OverlayContentHost，
        // 此时再弹提示不会有任何抢占问题。
        //
        // 修复"点弹窗周围的空白处会把新手引导关掉"：wizard.ShowDialog() 走的是
        // OverlayDialogControl 里给 30 多个弹窗共用的兼容层（见 IOverlayDialog.cs），
        // 内部固定调用 OverlayDialogService.ShowModal(this)，也就是用
        // dismissOnBackgroundClick 的默认值 true——这个默认值对"确认框/选择框"这些
        // 一次性小弹窗是合理的（点旁边空白 = 取消），但新手引导是"必须显式选择/走完
        // 才算数"的多步骤流程，被误触的空白区域一点就整个关掉、状态直接按当前进度标记
        // 成\"已完成\"，不应该走这条默认放行的路径。这里不改 OverlayDialogControl 基类
        // 的默认值（改了会连带影响其余 20 多个弹窗的点击外部关闭行为），而是绕开
        // ShowDialog() 这层封装，直接调用 OverlayDialogService.ShowModal 并显式传
        // dismissOnBackgroundClick: false，只让新手引导这一个弹窗变成"点空白处不生效，
        // 必须点「跳过引导」或走完流程"。
        if (!ConfigService.Config.FirstRunWizardCompleted)
        {
            EventHandler? showWizardOnce = null;
            showWizardOnce = (_, _) =>
            {
                ContentRendered -= showWizardOnce;
                var wizard = new FirstRunWizardWindow(this);
                OverlayDialogService.ShowModal(wizard, dismissOnBackgroundClick: false);
                _ = ScanMinecraftFoldersInBackgroundAsync();
            };
            ContentRendered += showWizardOnce;
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
        // MemoryWarningWindow 已迁移成内嵌 Overlay，UserControl 没有 Window.Closed，
        // 用等价的 IOverlayDialog.RequestClose 复位这个"同一时刻只留一个"的哨兵字段。
        _activeMemoryWarningWindow.RequestClose += (_, _) => _activeMemoryWarningWindow = null;
        _activeMemoryWarningWindow.Show();
    }

    public void RefreshSidebar()
    {
        try
        {
            var acc = ConfigService.GetSelectedAccount();
            CurrentAccountText.Text = acc == null ? Loc.T("Str_Launch_NoAccount", "未选择账户") : $"当前账户: {acc.DisplayLabel}";
            CurrentVersionText.Text = string.IsNullOrEmpty(ConfigService.Config.SelectedVersionId)
                ? "未选择版本"
                : $"当前版本: {ConfigService.Config.SelectedVersionId}";
        }
        catch
        {
            // 配置异常不应阻塞主页显示，回退为默认文案
            CurrentAccountText.Text = Loc.T("Str_Launch_NoAccount", "未选择账户");
            CurrentVersionText.Text = Loc.T("Str_Launch_NoVersion", "未选择版本");
        }

        // 收起态下横向空间极窄，"当前账户: xxx"这种完整文案必然放不下，需要再跑一遍
        // 收起态专用的缩写逻辑。放在 try/catch 外面：上面已经把 CurrentAccountText/
        // CurrentVersionText.Text 兜底成了确定的字符串，这里只是在此基础上做展示层的截断，
        // 不会再抛异常。
        RefreshAccountVersionSidebarText();
    }

    /// <summary>
    /// 收起态下（图2示例："pl..." / "1.0"）把账户名/版本号从完整文案缩写成极简形式：
    /// - 账户：只取账户名前 2 个字符 + "..."（如"Player"→"pl..."，跟需求截图给的示例一致，
    ///   用小写是因为图2示例本身就是小写"pl..."）；
    /// - 版本：只保留版本号数字部分（如"1.0"），去掉"当前版本: "这个前缀。
    /// 展开态则完全不动，用回 RefreshSidebar() 里已经设置好的完整文案
    /// （这里不重新赋值 CurrentAccountText.Text 本身，而是在收起时套一层显示层缩写、
    /// 展开时换回来，避免破坏 RefreshSidebar 里已经算好的完整文案，导致下次直接调用
    /// RefreshSidebar 时还要重新拼一遍"完整"文案）。
    /// </summary>
    private void RefreshAccountVersionSidebarText()
    {
        if (!_sidebarCollapsed)
        {
            // 展开态：RefreshSidebar 已经把完整文案写进去了，这里不用做任何事。
            return;
        }

        try
        {
            var acc = ConfigService.GetSelectedAccount();
            if (acc == null)
            {
                CurrentAccountText.Text = "-";
            }
            else
            {
                var name = acc.DisplayLabel ?? "";
                CurrentAccountText.Text = name.Length <= 2 ? name : name[..2].ToLowerInvariant() + "...";
            }

            // 修复截图2里"26.2 服务器"竖着一个字一行的问题：
            // 这两个 TextBlock 在 XAML 里写了 TextWrapping="Wrap"，收起态列宽只有 ~46px，
            // 任何超过两三个字的文案都会被逐字换行，把底部区域撑得很高、
            // 还把启动按钮往下挤。收起态必须同时做两件事：截断 + 关掉换行。
            var versionId = ConfigService.Config.SelectedVersionId;
            if (string.IsNullOrEmpty(versionId))
            {
                CurrentVersionText.Text = "-";
            }
            else
            {
                // 只保留能认出来的版本号部分（"26.2服务器" → "26.2"），认不出来就取前 4 个字符。
                var shortVer = VersionInfoResolver.ExtractAnyVersion(versionId)
                               ?? (versionId.Length <= 4 ? versionId : versionId[..4] + "…");
                CurrentVersionText.Text = shortVer;
            }
        }
        catch
        {
            CurrentAccountText.Text = "-";
            CurrentVersionText.Text = "-";
        }

        // 收起态下这两行也应该居中显示（跟图标居中的导航按钮保持一致的视觉重心），
        // 而不是继续贴在左边。
        CurrentAccountText.TextAlignment = TextAlignment.Center;
        CurrentVersionText.TextAlignment = TextAlignment.Center;

        // 关掉自动换行 + 打开省略号截断，双保险：即使上面的缩写逻辑漏了某种情况，
        // 也只会显示成"26.2…"，绝不会再出现逐字竖排。
        CurrentAccountText.TextWrapping = TextWrapping.NoWrap;
        CurrentVersionText.TextWrapping = TextWrapping.NoWrap;
        CurrentAccountText.TextTrimming = TextTrimming.CharacterEllipsis;
        CurrentVersionText.TextTrimming = TextTrimming.CharacterEllipsis;
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

            // 修复"关掉检测到 Minecraft 文件夹的提示框，整页按钮全部点不动"：
            // 根因是这里以前调用的 MessageBoxDialog.ShowInfo → OverlayDialogService.ShowModal
            // 走的是"手动 PushFrame 局部消息泵、同步阻塞等结果"这条路径，而这个调用点本身
            // 又是在 await Task.Run(...) 之后的异步延续里执行的——也就是"在一个已经通过
            // SynchronizationContext 延续机制排队等 UI 线程执行的回调内部，再手动 PushFrame
            // 一次、并且用同一个 TaskScheduler.FromCurrentSynchronizationContext() 来在
            // 弹窗关闭时把消息泵跳出来"。两层调度互相嵌套，在某些时序下（尤其是启动阶段
            // UI 线程本身还比较繁忙、消息队列里排了不少待处理项）会导致"弹窗按钮点击触发
            // 的 CloseTop → OverlayHideRoot 广播"迟迟排不到、或者跟外层 PushFrame 的退出
            // 条件产生竞争，表现为 Overlay 遮罩没能正常收起、卡在原地吃掉后续所有点击。
            // 改成 ShowInfoAsync + await：不再需要手动起第二个消息泵，弹窗关闭只是让
            // 这里的 await 自然恢复，没有嵌套 PushFrame，从根上避免这类竞争。
            await MessageBoxDialog.ShowInfoAsync(
                $"启动时自动发现了 {newlyAdded.Count} 个新的 .minecraft 文件夹，已加入「版本选择」页的文件夹列表：\n\n{names}",
                "自动发现新文件夹");
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(App.DataDir, "logs", "crash.log"),
                $"[{DateTime.Now}] [自动扫描.minecraft文件夹失败] {ex}\n\n"); }
            catch { /* 连日志都写不进去就彻底放弃，不影响启动器正常使用 */ }
        }
    }

    /// <summary>
    /// 启动时静默自动探测 Java：跟 ScanMinecraftFoldersInBackgroundAsync 同一套模式——
    /// Loaded 之后台线程跑，找到的新 Java 直接登记进"Java 列表"（cfg.InstalledJavas），
    /// 不需要用户手动打开「设置」页去点"刷新（自动探测）"按钮才能发现新装的 Java。
    ///
    /// 合并两路来源：
    /// 1. JavaService.QuickDetectJavaAsync——已知产品固定路径（JAVA_HOME/注册表/PATH/
    ///    .minecraft/runtime/.hmcl/java 等），秒回。
    /// 2. JavaService.ScanCommonJavaLocationsAsync——AppData、Program Files、JAVA_HOME
    ///    上级目录下的有限深度扫描（4 级找疑似 JDK 文件夹，命中后 6 级内找 javaw.exe），
    ///    覆盖前者没有硬编码到的自定义/小众发行版安装路径。
    /// 两路结果按 javaw 路径去重后一起登记，找到就静默加入列表刷新状态栏提示（如果当前正显示
    /// 「设置」页），找不到/扫描失败都完全静默，不弹窗打扰用户——这只是启动时的锦上添花，
    /// 用户仍然可以在「设置」页手动点"刷新（自动探测）"或"全盘扫描"兜底。
    /// </summary>
    private async Task ScanJavaInBackgroundAsync()
    {
        try
        {
            var javaService = new JavaService();
            var quick = await javaService.QuickDetectJavaAsync();
            var common = await javaService.ScanCommonJavaLocationsAsync();

            var merged = quick.Concat(common)
                .GroupBy(c => c.JavawPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (merged.Count == 0) return;

            var cfg = ConfigService.Config;
            var existingPaths = new HashSet<string>(
                cfg.InstalledJavas.Select(j => j.JavawPath), StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var candidate in merged)
            {
                if (existingPaths.Contains(candidate.JavawPath)) continue;

                int? major = candidate.Version != null
                    ? JavaService.ParseJavaMajorVersion($"\"{candidate.Version}\"")
                    : null;
                ConfigService.RegisterJava(candidate.JavawPath, major, "Detected");
                existingPaths.Add(candidate.JavawPath);
                added++;
            }

            if (added == 0) return;

            ConfigService.Save();

            // 如果当前正好显示着「设置」页，顺手刷新一下它的 Java 列表框，让用户立刻看到
            // 新登记的条目，而不用切出去再切回来才发现列表更新了。不是「设置」页时什么都不做——
            // 静默登记本身已经完成，下次用户打开「设置」页自然会看到最新列表。
            if (MainContent.Content is SettingsPage settingsPage)
                settingsPage.RefreshJavaListPublic();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(App.DataDir, "logs", "crash.log"),
                $"[{DateTime.Now}] [自动扫描Java失败] {ex}\n\n"); }
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
        Views.ProgressDialog.CloseAll();

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

    /// <summary>供其他页面/弹窗调用的公开导航方法，跳转到日志页（比如崩溃提示弹窗的"查看日志"按钮）。</summary>
    public void NavigateToLogs() => SetMainContent(new LogsPage(this));

    private void NavExperimental_Click(object sender, RoutedEventArgs e)
    {
        OpenExperimentalFeatures();
    }

    private void NavToolbox_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new ToolboxPage(this));
    }

    /// <summary>供其他页面/窗口调用的公开导航方法，跳转到「百宝箱」页。</summary>
    public void NavigateToToolbox() => SetMainContent(new ToolboxPage(this));

    private void NavAboutHelp_Click(object sender, RoutedEventArgs e)
    {
        SetMainContent(new AboutHelpPage(this));
    }

    /// <summary>供其他页面/窗口调用的公开导航方法，跳转到「鸣谢与帮助」页。</summary>
    public void NavigateToAboutHelp() => SetMainContent(new AboutHelpPage(this));

    /// <summary>侧边栏当前是否处于"收起"状态。</summary>
    private bool _sidebarCollapsed;

    private const double SidebarExpandedWidth = 180;
    private const double SidebarCollapsedWidth = 56;

    private void SidebarCollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplySidebarCollapsedState(!_sidebarCollapsed);
    }

    /// <summary>
    /// 应用"功能隐藏"设置：目前只覆盖主导航栏的下载/设置/工具三个入口（跟设置页
    /// FeatureVisibilityService.Groups 里"主页面"这一组对应）——子页面/特定功能那些
    /// 更细粒度的隐藏项，各自的宿主页面（SettingsPage/ToolboxPage 等）在自己
    /// 加载时各自读取判断，不需要 MainWindow 统一处理。每次进入/离开设置页保存、
    /// 或按 F12 切换临时显示时都要重新调用一次这个方法，确保导航栏立即反映最新状态。
    /// </summary>
    public void ApplyFeatureVisibility()
    {
        var cfg = ConfigService.Config;
        NavDownloadButton.Visibility = FeatureVisibilityService.IsVisible(cfg, FeatureVisibilityService.NavDownload) ? Visibility.Visible : Visibility.Collapsed;
        NavSettingsButton.Visibility = FeatureVisibilityService.IsVisible(cfg, FeatureVisibilityService.NavSettings) ? Visibility.Visible : Visibility.Collapsed;
        NavToolboxButton.Visibility = FeatureVisibilityService.IsVisible(cfg, FeatureVisibilityService.NavToolbox) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 侧边栏收起/展开的统一应用逻辑：宽度、Logo、每个导航按钮的文字部分、
    /// 底部进程控制按钮组、启动游戏按钮，全部按同一个 collapsed 状态联动切换，
    /// 避免"点了收起，某几个地方没跟着变"这种不一致。
    ///
    /// 收起态设计（对应需求描述）：
    /// - 三横杠(☰)按钮变成一个点(●)；
    /// - 导航按钮只剩图标（文字 TextBlock 部分 Collapsed，图标 TextBlock 单独放在同一个
    ///   StackPanel 里，收起时直接把文字那块 TextBlock 隐藏，图标 TextBlock 不受影响）；
    /// - 进程控制三按钮只保留"关闭所选"，文案缩成"关"字（用 CloseSelectedBtnCollapsed 这个
    ///   独立按钮，跟展开态的 UniformGrid 三件套做 Visibility 二选一）；
    /// - "启动游戏"长条按钮换成一个圆形图标按钮；
    /// - 账户/版本信息缩短显示（比如账户名太长时省略号截断，版本号只留主版本号）。
    /// </summary>
    private void ApplySidebarCollapsedState(bool collapsed)
    {
        _sidebarCollapsed = collapsed;

        SidebarColumn.Width = new GridLength(collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);

        // ===== 修复「收起后图标只剩 1 像素」=====
        // 旧版只改了列宽，没有同步收掉内边距，于是留给图标的净宽被一路吃到只剩 10px：
        //     56(列宽) − 24(SidebarPanel.Margin=12 左右) − 20(SideNavButton.Padding=10,10)
        //     − 2(模板 BorderThickness="2,0,0,0") = 10px
        // 而图标至少需要 20px（NavIconBox 固定尺寸）。收起态必须把 Margin/Padding 一起收掉，
        // 否则不管图标换成 emoji 还是矢量，都一样会被裁掉。
        //     56 − 8(Margin 左右各 4) − 0(Padding 归零) − 2(左描边) = 46px ≥ 20px，宽裕。
        SidebarPanel.Margin = collapsed ? new Thickness(4, 12, 4, 12) : new Thickness(12);

        // 折叠切换按钮的图标：三横杠 ↔ 一个圆点。现在是 Path 不是 TextBlock，改的是 Data。
        SidebarToggleIcon.Data = (Geometry)FindResource(collapsed ? "IconDot" : "IconMenu");
        LogoText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        // 导航按钮：文字 TextBlock 隐藏/显示（{DynamicResource Str_Nav_Xxx} 绑定保持不变，
        // 收起态只是不显示，不是清空文案，展开时立即恢复，不需要手动缓存/还原字符串），
        // 按钮内容居中对齐（收起时）或靠左对齐（展开时），这样收起态下只剩 emoji 图标会
        // 自然居中，不会贴在按钮左边显得很怪。
        foreach (var (button, icon, label) in new (Button, FrameworkElement, TextBlock)[]
                 {
                     (NavHomeButton, NavHomeIcon, NavHomeLabel),
                     (NavVersionsButton, NavVersionsIcon, NavVersionsLabel),
                     (NavDownloadButton, NavDownloadIcon, NavDownloadLabel),
                     (NavMultiplayerButton, NavMultiplayerIcon, NavMultiplayerLabel),
                     (NavModManagerButton, NavModManagerIcon, NavModManagerLabel),
                     (NavServerManagerButton, NavServerManagerIcon, NavServerManagerLabel),
                     (NavToolboxButton, NavToolboxIcon, NavToolboxLabel),
                     (NavAccountsButton, NavAccountsIcon, NavAccountsLabel),
                     (NavSettingsButton, NavSettingsIcon, NavSettingsLabel),
                     (NavLogsButton, NavLogsIcon, NavLogsLabel),
                     (NavExperimentalButton, NavExperimentalIcon, NavExperimentalLabel),
                 })
        {
            label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            button.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;

            // 图标本身是固定 20×20 的 NavIconBox，Margin 只影响它和文字之间的间距，
            // 不再影响图标自身尺寸（这正是换成矢量+固定盒子之后最大的好处：
            // 无论外面怎么调间距，图标都不会被压扁）。
            icon.Margin = new Thickness(0);

            // 关键：收起态把按钮左右内边距归零，把宽度全部让给图标；
            // 上下保留 10px 保证点击热区够大。展开态恢复原来的 10,10。
            button.Padding = collapsed ? new Thickness(0, 10, 0, 10) : new Thickness(10, 10, 10, 10);

            // 按钮之间的垂直间距：收起态只剩一排图标，加大到 10px 免得又挤又密。
            button.Margin = collapsed ? new Thickness(0, 10, 0, 0) : new Thickness(0, 4, 0, 0);
        }

        // 进程控制按钮组：收起态只留"关闭所选"缩写成"关"。
        ProcessControlExpanded.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CloseSelectedBtnCollapsed.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;

        // 启动游戏按钮：展开态长条 / 收起态圆形图标二选一。
        LaunchGameBtn.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        LaunchGameBtnCollapsed.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;

        // 账户/版本信息：收起态下横向空间只剩图标那么宽，长文字必然放不下，
        // 索性直接换成极简缩写（如图2示例：账户名只显示前几个字符+省略号，版本号只留数字），
        // 而不是让 WPF 自动换行把这一小块区域撑得很高、把下面的按钮全部往下挤。
        if (!collapsed)
        {
            // 展开态：换回左对齐 + 完整文案（RefreshSidebar 里已经算好的），
            // 不依赖 RefreshAccountVersionSidebarText 的 early-return（那里只在 _sidebarCollapsed
            // 为 true 时才会真正改写文案，为 false 时直接跳过——所以这里展开时要主动重新调用
            // 一次 RefreshSidebar 让完整文案生效，同时把对齐方式改回左边）。
            CurrentAccountText.TextAlignment = TextAlignment.Left;
            CurrentVersionText.TextAlignment = TextAlignment.Left;
            // 展开态恢复换行（收起时被关掉了，见 RefreshAccountVersionSidebarText），
            // 否则长账户名展开后也不换行、被直接截断。
            CurrentAccountText.TextWrapping = TextWrapping.Wrap;
            CurrentVersionText.TextWrapping = TextWrapping.Wrap;
            CurrentAccountText.TextTrimming = TextTrimming.None;
            CurrentVersionText.TextTrimming = TextTrimming.None;
            RefreshSidebar();
        }
        else
        {
            RefreshAccountVersionSidebarText();
        }
    }



    /// <summary>
    /// 根据当前启动器界面语言控制侧边栏"实验性功能"按钮的显隐（见
    /// LocalizationService.ExperimentalFeaturesLanguageGate 注释：这批功能还没有多语言界面，
    /// 只在简体中文下展示）。构造函数调用一次做初始同步，LanguageChanged 事件触发时
    /// 再调一次，保证运行时切换语言立即生效，不需要重启/切页面。
    /// </summary>
    private void RefreshExperimentalNavVisibility()
    {
        NavExperimentalButton.Visibility =
            LocalizationService.CurrentLanguageCode == LocalizationService.ExperimentalFeaturesLanguageGate
                ? Visibility.Visible
                : Visibility.Collapsed;
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
            // 修复编译错误 CS0117："ExperimentalGateWindow 未包含 Owner 的定义"。
            // ExperimentalGateWindow 已迁移成 Overlay 弹窗（继承 OverlayDialogControl，
            // 一个 UserControl），本来就没有 Owner 这个属性——居中和层叠关系现在统一由
            // MainWindow 的 OverlayCard 负责，不再需要每个弹窗各自指定 owner。
            // 这是上一轮批量清理 Owner 初始化器时的漏网：清理用的正则要求
            // "new Xxx(...) { Owner = ... }" 带括号的构造调用，而这里是
            // "new Xxx { Owner = ... }" 省略括号的对象初始化器写法，没被正则命中。
            var gate = new ExperimentalGateWindow();
            gate.ShowDialog();

            if (!gate.Confirmed) return; // 用户取消/关闭窗口：不解锁，不打开实验性功能面板

            cfg.ExperimentalFeaturesUnlocked = true;
            ConfigService.Save();
        }

        var window = new ExperimentalFeaturesWindow(this);
        window.ShowDialog();

        // 修复"关闭实验性功能窗口时启动器主窗口会自动最小化"：ExperimentalFeaturesWindow
        // 内部还会再弹出 MultiLoaderInstallWindow 等子窗口，多层 ShowDialog() 关闭之后，
        // Windows 有时不会把前台焦点正确交还给 Owner（尤其是子窗口本身也丢了焦点、或者
        // 用户在等待期间切到了其它程序），观感上就是"关掉这个窗口，主窗口自己缩没了"。
        // 复用 EnsureVisibleForDialog()：如果这期间被最小化了就恢复到之前的真实状态
        // (Normal/Maximized) 并 Activate() 抢回前台，不最小化则什么都不做。
        EnsureVisibleForDialog();
    }

    /// <summary>
    /// 启动游戏的入口，原来只被主窗口左下角的"启动游戏"按钮调用，现在首页磁贴的
    /// "启动游戏"按钮也会调用这个方法（见 HomePage.xaml.cs），改成 public 供跨页面复用，
    /// 两处共享同一套防手滑冷却状态（_lastLaunchClickAtUtc/LaunchGameBtn），不会出现
    /// "首页点了启动、左下角按钮的冷却状态却没跟着更新"这种不一致。
    /// </summary>
    public void Launch_Click(object sender, RoutedEventArgs e) => Launch_Click(sender, e, skipAccountConfirm: false);

    /// <summary>
    /// 修复"傻瓜式启动/一键开始游戏完成后，还会再弹一次「选择要用来启动游戏的账户」"：
    /// QuickStartWizardWindow 步骤 1 已经让用户显式选过/登录过账户（不确认好账户不能进下一步），
    /// 走到最后一步调用这里启动游戏时，账户早就是用户当场确认过的，没有必要在同一次操作里
    /// 再弹一遍一模一样的选择框——对用户来说这不是"多一次确认机会"，而是"同一件事问了两遍"，
    /// 显得启动器没记住自己刚刚做过的选择。skipAccountConfirm=true 时跳过下面的账户确认弹窗，
    /// 直接使用 ConfigService.GetSelectedAccount()（此时必定是向导里选定的那个账户）。
    /// 普通入口（左下角"启动游戏"按钮/首页磁贴）不知道用户是否刚确认过账户，继续走原来的
    /// public 无参重载，默认不跳过。
    /// </summary>
    public async void Launch_Click(object sender, RoutedEventArgs e, bool skipAccountConfirm)
    {
        // 需求："从点击'启动游戏'到等待游戏窗口出现的间隙，按钮变成'取消启动'；
        // 点击'取消启动'时不再弹出选择账户的界面（也不弹任何其它确认框），直接中止本次启动"。
        // 这里复用同一个 Click 处理器：当前处于"取消启动"状态时，这次点击是"取消"动作，
        // 跟下面"启动"分支完全分开处理，不走冷却判断（冷却是为了防止连续点"启动"，
        // 取消操作本身应该能立刻响应）。
        if (_isCancelLaunchState)
        {
            HandleCancelLaunchClick();
            return;
        }

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
        // 状态切换保护：即使冷却已过，也要求距离上一次"启动⇄取消"状态切换至少
        // LaunchStateSwitchGuard（1.5 秒）才允许再次切换。冷却挡的是"同一个状态下的
        // 连续误触"，这里挡的是"启动→取消→启动→取消"这种在两个状态之间来回跳的死循环——
        // 二者场景不同，缺一都可能被绕过（比如冷却时间一到，用户立刻点取消再点启动，
        // 没有这层保护冷却形同虚设）。
        if (now - _lastLaunchStateSwitchAtUtc < LaunchStateSwitchGuard)
            return;
        _lastLaunchClickAtUtc = now;
        _lastLaunchStateSwitchAtUtc = now;

        // 立刻把按钮置灰：这是给用户最直观的反馈——"已经收到你的点击了，正在处理，
        // 不需要再点"。比单纯静默吞掉后续点击更清楚，用户能看到按钮变灰就知道发生了什么，
        // 不会怀疑是不是自己没点到。finally 里保证无论方法从哪个分支退出都会恢复。
        // 收起态侧边栏用的是另一个按钮 LaunchGameBtnCollapsed（同一个 Click 处理器），
        // 之前这里只置灰了展开态的 LaunchGameBtn，收起态按钮视觉上一直是可点状态——
        // 冷却判断本身读的是 LaunchGameBtn.IsEnabled，点了不会真的触发第二次启动，
        // 但按钮看起来"没有被禁用"，用户会怀疑点击没生效，跟需求里"窗口出现之前不可以
        // 重复点击启动游戏"的意图不符（应该是视觉上也能看出正在处理，而不只是点了没反应）。
        // 两个按钮的 IsEnabled 现在统一置灰/恢复。
        LaunchGameBtn.IsEnabled = false;
        LaunchGameBtnCollapsed.IsEnabled = false;

        // 进入"取消启动"状态：按钮文字/ToolTip 切到 Str_Launch_Cancel，但先保持置灰
        // LaunchStateSwitchGuard 的时长，避免刚点完"启动"手指还没抬起就又按到了
        // "取消"（两者在屏幕上是同一个按钮位置，切换太快等于给了一个"连点死循环"的窗口）。
        // 这段置灰单独用 Task.Delay 而不是复用下面 finally 里的冷却等待，是因为
        // 这里保护的是"进入取消态"这一下，跟"启动流程整体结束后恢复成启动态"是两件事，
        // 分开表达更清楚，也避免后面改动其中一处误伤另一处。
        _isCancelLaunchState = true;
        SetLaunchButtonContent(cancel: true);
        _launchCts = new CancellationTokenSource();
        var launchCts = _launchCts;
        _ = Task.Delay(LaunchStateSwitchGuard).ContinueWith(_ =>
        {
            if (launchCts == _launchCts && _isCancelLaunchState)
            {
                LaunchGameBtn.IsEnabled = true;
                LaunchGameBtnCollapsed.IsEnabled = true;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());

        try
        {
            await LaunchInternalAsync(sender, e, skipAccountConfirm, launchCts.Token);
        }
        finally
        {
            // 流程结束（成功/失败/被取消）：切回"启动游戏"状态。跟进入取消态时一样，
            // 状态切换本身也要受 LaunchStateSwitchGuard 保护——如果流程几乎瞬间结束
            // （比如账户校验直接失败 return），"取消启动"文字一闪而过又变回"启动游戏"，
            // 用户这时候如果正好又点了一下，很容易触发"上一次启动的收尾"和"这一次新的
            // 启动"前后脚发生的时序问题。用剩余的保护时长兜底，保证"取消启动"这个状态
            // 至少完整展示 LaunchStateSwitchGuard 那么久，再恢复成可点的"启动游戏"。
            var elapsed = DateTime.UtcNow - _lastLaunchStateSwitchAtUtc;
            var remaining = LaunchStateSwitchGuard - elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining);

            _isCancelLaunchState = false;
            _launchCts = null;
            SetLaunchButtonContent(cancel: false);
            _lastLaunchStateSwitchAtUtc = DateTime.UtcNow;

            LaunchGameBtn.IsEnabled = true;
            LaunchGameBtnCollapsed.IsEnabled = true;
        }
    }

    /// <summary>
    /// 处理"取消启动"按钮的点击：只中止 CancellationTokenSource，让 LaunchInternalAsync
    /// 内部的等待点自行感知到取消并退出，不弹任何确认框（需求明确要求"点击取消启动
    /// 不弹出选择账户的界面"——取消本身就是一次明确、无需二次确认的操作）。
    /// 如果游戏进程这时候已经真的拉起来了，取消只是让启动器不再继续等待"窗口出现"，
    /// 不会去杀掉已经在运行的游戏进程——那是"关闭游戏"按钮的职责，不是这里的。
    /// </summary>
    private void HandleCancelLaunchClick()
    {
        // 同样受状态切换保护：防止用户在"取消"这个按钮刚出现的一瞬间因为手滑/连点
        // 又立刻点了一次（这里本身不做任何事，但避免重复调用 Cancel() 造成不必要的
        // ObjectDisposedException 之类的边界问题）。
        var now = DateTime.UtcNow;
        if (now - _lastLaunchStateSwitchAtUtc < LaunchStateSwitchGuard || !LaunchGameBtn.IsEnabled)
            return;

        _launchCts?.Cancel();
    }

    /// <summary>统一设置展开态/收起态两个启动按钮的文字和 ToolTip，在"启动游戏"和"取消启动"之间切换。</summary>
    private void SetLaunchButtonContent(bool cancel)
    {
        var resourceKey = cancel ? "Str_Launch_Cancel" : "Str_Launch_Button";
        var text = Loc.T(resourceKey, cancel ? "取消启动" : "启动游戏");
        LaunchGameBtn.Content = text;
        LaunchGameBtnCollapsed.ToolTip = text;
    }

    /// <summary>
    /// 启动游戏的实际逻辑，从 Launch_Click 拆出来，专门用于被防手滑冷却的
    /// try/finally 包裹，避免把冷却相关代码和原有的一大段启动流程混在一起、
    /// 显得臃肿难读。
    /// </summary>
    private async Task LaunchInternalAsync(object sender, RoutedEventArgs e, bool skipAccountConfirm = false, CancellationToken cancelToken = default)
    {
        var cfg = ConfigService.Config;
        var account = ConfigService.GetSelectedAccount();
        var folder = cfg.Folders.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath);

        // 需求修复："一键开始游戏"（以及左下角"启动游戏"）之前完全静默调用
        // GetSelectedAccount()，只会自动选中"上次选中/第一个"账户，用户没有机会在这个时间点
        // 选别的账户，只能先跳去"账户管理"页手动切换、再跳回来点启动，多绕一层。
        // 现在改为：有多个账户时（且不是访客模式——访客模式下账户始终是本次会话的临时账户，
        // 不应该被这个选择框打断），弹出账户选择框让用户当场选。
        //
        // 触发条件在原来"账户数量 > 1"的基础上再加一种情况：账户数量 == 1 但这个账户从来没有
        // 被显式选中过（LastSelectedAccountId 为空，即新建/登录账户后不再自动选中——见
        // LoginPage/FirstRunWizardWindow/QuickStartWizardWindow/AccountPickerDialog 的改动），
        // 这种情况下也应该让用户在启动这一刻明确确认一下"就用这个账户"，而不是端起来直接静默
        // 用 FirstOrDefault() 兜底的那个账户启动——那样等于替用户做了选择，且用户完全无感知。
        // 只有真正"没有任何账户"或"唯一账户已经被显式选过"这两种情况才不弹框，跟其它任何
        // 导航/切换页面的场景一样，这个选择框现在只在真正点击"启动游戏"这个动作时才会出现。
        var needsAccountConfirm = !skipAccountConfirm &&
            (ConfigService.Accounts.Count > 1
            || (ConfigService.Accounts.Count == 1 && string.IsNullOrEmpty(cfg.LastSelectedAccountId)));
        if (!cfg.GuestModeEnabled && needsAccountConfirm)
        {
            var picker = new AccountPickerDialog(this, ConfigService.Accounts, cfg.LastSelectedAccountId);
            if (OverlayDialogService.ShowModal(picker) != true)
            {
                // Round16 反馈：之前这里直接静默 return，用户点"取消"后界面毫无反应，
                // 体验上跟"点了没反应/软件卡死"没区别。这里跟同一方法里其它分支
                // （没账户/没选文件夹）一样用 MessageBox 给一句明确提示。
                MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Launch_Cancelled", "已取消启动。"), Loc.T("Str_Status_Tip", "提示"));
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

        // 需求："点击'取消启动'不弹出选择账户的界面"。账户确认框本身已经在上面处理完了
        // （要么用户确认了账户，要么在弹框里点了取消已经 return），这里检查的是：用户在
        // 账户确认框弹出**之前**（比如账户框还没来得及显示、或者根本不需要账户确认——
        // 单账户/访客模式）就已经点了"取消启动"。此时不应该再走下面 Java 检测/下载等
        // 任何后续流程，也不应该再弹任何提示框——静默退出，跟用户主动点取消的直觉一致。
        if (cancelToken.IsCancellationRequested)
            return;

        if (account == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Sign_In_Or_Create_An_Offline_Account_On_", "请先在“账户管理”中登录或创建一个离线账户。"), Loc.T("Str_Status_Tip", "提示"));
            NavAccounts_Click(sender, e);
            return;
        }
        if (folder == null || string.IsNullOrEmpty(cfg.SelectedVersionId))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Choose_A_Minecraft_Folder_And_A_Game_Ver", "请先在“版本选择”中选择 .minecraft 文件夹和游戏版本。"));
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
                    MessageBoxDialog.ShowWarning(
                        $"账户「{account.Username}」的登录状态已过期，且自动刷新失败，请重新登录微软账户后再启动游戏。\n" +
                        "（如果直接用过期状态启动，Minecraft 会静默进入离线试玩(Demo)模式而不会报错，" +
                        "为避免这种情况这里主动拦截。）",
                        Loc.T("Str_Cs_Sign_In_Required", "需要重新登录"));
                    NavAccounts_Click(sender, e);
                    return;
                }
                // else: token 还没到硬过期时间（只是进入 5 分钟提前刷新窗口)，刷新虽失败但
                // 旧 token 短期内应该仍然有效，容许继续启动，避免因为一次偶发的网络抖动
                // 就完全无法进游戏。
            }

            // 账户刷新（可能有一次网络请求）之后、Java 检测/下载安装这段可能耗时较久的流程
            // 开始之前，再检查一次取消状态：用户可能就是在等 token 刷新的这几百毫秒到几秒里
            // 点的"取消启动"。这里静默 return，不弹任何提示——跟需求"点取消不弹账户选择框"
            // 是同一个原则的延伸：取消操作本身不需要任何确认/告知。
            if (cancelToken.IsCancellationRequested)
                return;

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
                ?? javaService.FindJava(cfg.JavaPath, preferMajor, ConfigService);
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
                        MessageBoxDialog.ShowWarning(
                            $"你为这个版本手动指定的 Java 是 {actualMajor}，但这个版本自动匹配的应该是 Java {preferMajor}" +
                            $"（如果不手动指定，启动器本来会自动帮你选到这个版本）。\n\n" +
                            $"已开启「强制使用匹配 Java」，将自动切换到 Java {preferMajor} 后再启动。\n{suggestion}",
                            "Java 版本不匹配，已自动切换");
                        shouldSwitch = true;
                    }
                    else
                    {
                        var switchResult = MessageBoxDialog.ShowConfirm(
                            $"你为这个版本手动指定的 Java 是 {actualMajor}，但这个版本自动匹配的应该是 Java {preferMajor}" +
                            $"（如果不手动指定，启动器本来会自动帮你选到这个版本）。用不匹配的版本启动很可能会崩溃" +
                            $"（常见报错如 UnsupportedClassVersionError）。\n\n{suggestion}\n\n" +
                            $"点「是」改用匹配的 Java {preferMajor}；点「否」仍然使用当前这个 Java {actualMajor}（不建议，除非你清楚自己在做什么）。\n\n" +
                            $"提示：可以在「设置」页开启「强制使用匹配 Java」，开启后遇到这种情况会直接自动切换，不再询问。",
                            "Java 版本可能不匹配");
                        shouldSwitch = switchResult;
                    }

                    if (shouldSwitch)
                    {
                        // 改用匹配版本：优先用列表里已登记的匹配项，没有就清空覆盖走回下面的
                        // 自动探测/下载逻辑（preferMajor 已经算好了，FindJava/下载都会用它）。
                        javaPath = matchedInList != null ? ConfigService.ResolveJavaPath(matchedInList.Id) : null;
                        javaPath ??= javaService.FindJava(null, preferMajor, ConfigService);
                    }
                    // 非强制模式选"否"：保留原 javaPath 不变，尊重用户的明确选择。
                }
            }
            if (javaPath == null)
            {
                var versionHint = preferMajor is > 0
                    ? $"这个版本需要 Java {preferMajor}，但未找到匹配的 Java（可能没安装，或已安装的版本不对）。"
                    : "未检测到可用的 Java 环境。";
                var result = MessageBoxDialog.ShowConfirm($"{versionHint}\n是否自动下载对应的便携版 Java？",
                    "需要 Java");
                if (!result) return;

                var progressWin = new ProgressDialog("正在下载 Java 运行时...");
                // ProgressDialog 迁移成 Overlay 之后已经不是独立 Window，没有 Owner 属性了——
                // 它现在挂在 MainWindow 自己的 Overlay 层里，天然"属于"当前主窗口，不需要
                // 也不能再显式赋 Owner（迁移前遗留的这行赋值如果留着会导致编译失败）。
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
                    var skinProgressWin = new ProgressDialog("正在下载万能皮肤补丁...");
                    // 同上：ProgressDialog 现在是 Overlay 弹窗，没有 Owner 属性了。
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
                        MessageBoxDialog.ShowWarning(hint + skinEx.Message, Loc.T("Str_Cs_Failed_To_Download_The_Skin_Patch", "皮肤补丁下载失败"));
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

            // 「百宝箱」-「内存优化」：开关开启时，启动前用 MemoryOptimizerService 按当前
            // 系统实际可用内存重新计算一遍 -Xms/-Xmx，覆盖设置页里用户手动填写的固定值。
            // 计算失败（非 Windows/API 异常等）时静默回退到用户原有配置，不阻断启动流程。
            var effectiveMinMemoryMb = cfg.MinMemoryMb;
            var effectiveMaxMemoryMb = cfg.MaxMemoryMb;
            if (cfg.EnableMemoryOptimization)
            {
                var recommendation = MemoryOptimizerService.Calculate(cfg.MemoryOptimizationReserveMb);
                if (recommendation != null)
                {
                    effectiveMinMemoryMb = recommendation.RecommendedMinMemoryMb;
                    effectiveMaxMemoryMb = recommendation.RecommendedMaxMemoryMb;
                }
            }

            var launcher = new LauncherService();
            var options = new LauncherService.LaunchOptions
            {
                MinecraftDir = folder.Path,
                VersionId = cfg.SelectedVersionId,
                JavaPath = javaPath,
                Account = account,
                MinMemoryMb = effectiveMinMemoryMb,
                MaxMemoryMb = effectiveMaxMemoryMb,
                WindowWidth = cfg.WindowWidth,
                WindowHeight = cfg.WindowHeight,
                ShowConsoleWindow = cfg.EnableGameConsoleWindow,
                IsolateVersion = isolateVersion,
                GameLanguage = cfg.GameLanguage,
                VersionTypeLabel = cfg.GameVersionTypeLabel,
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

            LauncherLogService.AppendLine($"[启动游戏] 账户={account.DisplayLabel} 版本={cfg.SelectedVersionId}");

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
                var repair = MessageBoxDialog.ShowConfirm(
                    mle.Message + "\n\n是否现在自动下载补全这些缺失的库？",
                    Loc.T("Str_Cs_Missing_Library", "缺少依赖库"));
                if (!repair) return;

                var repairWin = new ProgressDialog("正在补全缺失的依赖库...");
                // 同上：ProgressDialog 现在是 Overlay 弹窗，没有 Owner 属性了。
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

            // 需求："游戏崩溃的时候，可以选择查看日志和导出完整日志"——这里覆盖的是"游戏已经
            // 正常运行了一段时间之后才意外退出"的场景（跟下面 exitedEarly 覆盖的"刚启动就
            // 退出"是两种不同的时机，两处都要接）。判定标准：
            //   1. 不是用户自己点"关闭游戏"/"关闭未响应的游戏"（UserRequestedClose）；
            //   2. 退出码不是 0（Minecraft 正常从游戏内菜单退出时退出码是 0）。
            // 只有同时满足才弹崩溃提示，避免用户正常退出游戏时被无意义地打扰。
            processInfo.Process.Exited += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (processInfo.UserRequestedClose) return;
                    var exitCode = -1;
                    try { exitCode = processInfo.Process.ExitCode; } catch { /* 忽略 */ }
                    if (exitCode == 0) return;

                    LauncherLogService.AppendLine($"[游戏崩溃] {processInfo.AccountLabel} - {processInfo.VersionId}，退出码 {exitCode}");
                    EnsureVisibleForDialog();
                    CrashReportDialog.Show(this,
                        $"游戏「{processInfo.VersionId}」意外退出了（退出码 {exitCode}），可能是崩溃了。",
                        processInfo);
                });
            };

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
                            Dispatcher.Invoke(() => MessageBoxDialog.ShowWarning(
                                "注入检测发现可疑模块，游戏进程中可能存在外挂或密码窃取风险：\n\n" + names +
                                "\n\n建议：立即考虑关闭游戏并修改微软账户密码，同时检查这些文件的来源。",
                                Loc.T("Str_Cs_Injection_Scan_Warning", "⚠ 注入检测警告")));
                        }
                    }
                    catch { /* 扫描失败不影响正常游戏 */ }
                });
            }

            // 需求："不要在刚开始启动的时候就说游戏启动成功，要在游戏窗口出现之后才说成功"。
            // 之前这里只是等固定 3 秒观察进程有没有提前退出，进程没退出就直接判定"成功"——
            // 但"进程还活着"不等于"游戏窗口已经出现"：Forge/NeoForge 这类加载器安装器阶段、
            // 或者 JVM 正在下载/校验资源文件的这几秒到几十秒里，进程确实还活着，用户却看不到
            // 任何窗口，此时弹"启动成功"是在撒谎。改成轮询 Process.MainWindowHandle：句柄非零
            // 就是 Win32 意义上"这个进程已经创建了一个可见主窗口"，用它作为"游戏窗口出现"的
            // 判定依据，比固定等 3 秒更贴近真实状态。
            //
            // 轮询上限放宽到 2 分钟（跟旧的 3 秒相比宽松很多）：Forge/NeoForge 首次启动常见的
            // "下载/合并 mod 依赖" FML 处理阶段，在网络一般的环境下花十几秒到一两分钟都不算
            // 罕见，不能照抄原型时的 3 秒观察窗口，否则大量正常启动会被误判成"提前退出"而报错。
            // 轮询期间同步更新 LaunchStatusText，让用户知道当前处于"等待窗口出现"而不是卡死。
            ShowLaunchStatus("正在启动游戏进程…");
            var exitedEarly = false;
            var windowAppeared = false;
            var waitCancelled = false;
            await Task.Run(async () =>
            {
                for (var i = 0; i < 1200; i++) // 1200 * 100ms = 2 分钟
                {
                    // 需求：点"取消启动"要能中断"等待游戏窗口出现"这个轮询。这里只是让
                    // 启动器停止继续等待/不再把这次启动算作"成功"，不会去杀掉进程——
                    // 游戏进程如果已经拉起来了，用户之后仍然能在"关闭所选的游戏"里看到它、
                    // 手动结束；取消动作本身只表达"我不想再等/不想继续这次启动确认流程了"。
                    if (cancelToken.IsCancellationRequested) { waitCancelled = true; return; }
                    if (processInfo.HasExited) { exitedEarly = true; return; }
                    try
                    {
                        processInfo.Process.Refresh();
                        if (processInfo.Process.MainWindowHandle != IntPtr.Zero)
                        {
                            windowAppeared = true;
                            return;
                        }
                    }
                    catch { /* 进程可能正好在这一瞬间退出，下一轮循环会被上面的 HasExited 捕获到 */ }

                    if (i == 5) // 前 0.5 秒过后再切文案，避免"秒切"看起来像没变化
                        Dispatcher.Invoke(() => ShowLaunchStatus("等待游戏窗口出现…"));
                    await Task.Delay(100);
                }
            });
            // 轮询 2 分钟仍未见到窗口、进程也没退出：不判定为失败（有些环境窗口创建确实很慢，
            // 强行判失败反而会打断真正还在正常加载的游戏），但也不该继续用"等待"文案卡住不动，
            // 交还给下面 windowAppeared==false 且 exitedEarly==false 的分支处理。

            if (waitCancelled)
            {
                // 用户主动取消：跟需求一致，不弹任何提示框（不是"失败"，是用户自己叫停的），
                // 用 Toast 轻量告知一下就好，游戏进程本身留给用户在进程列表里自行处理。
                ToastService.ShowSuccess(Loc.T("Str_Cs_Launch_Cancelled", "已取消启动。"));
                return;
            }

            // 修复"启动成功/启动异常弹窗出现时启动器窗口会自动最小化"：上面等待最多 3 秒观察
            // 游戏进程是否提前退出的这段时间里，刚拉起的 Java/游戏窗口很容易抢到前台焦点，
            // 之前这里的系统原生 MessageBox.Show 没有显式传 Owner，弹窗触发时启动器主窗口
            // 已经不是前台窗口，Windows 有时会把这个已经失去前台焦点、又刚好被系统认为
            // "不活跃"的窗口直接最小化。现在改用进程内 Overlay 弹窗（MessageBoxDialog）后，
            // 弹窗本身就是挂在 MainWindow 可视化树里的一部分，天然跟随主窗口，不存在"独立
            // Win32 窗口跟主窗口分离"这个问题了；但主窗口自己被最小化的情况依然可能发生
            // （见上面的原因），所以这里仍然需要在弹窗前调用 EnsureVisibleForDialog()
            // 主动把主窗口从 Minimized 恢复。
            EnsureVisibleForDialog();

            if (exitedEarly)
            {
                string output;
                lock (processInfo.OutputBuffer) output = processInfo.OutputBuffer.ToString();
                var tail = output.Length > 3000 ? output[^3000..] : output;
                var exitCode = -1;
                try { exitCode = processInfo.Process.ExitCode; } catch { /* 忽略 */ }

                LauncherLogService.AppendLine($"[游戏崩溃] {account.DisplayLabel} - {cfg.SelectedVersionId}，退出码 {exitCode}");

                // 需求："游戏崩溃的时候，可以选择查看日志和导出完整日志"。这里用专门的
                // CrashReportDialog 替代原来只读的 MessageBoxDialog.ShowWarning——多了
                // "查看日志"（跳转日志页）和"导出完整日志"（合并启动器日志+崩溃前输出+
                // 游戏日志文件另存为一份文本）两个动作，其余提示文案基本保持不变。
                CrashReportDialog.Show(this,
                    $"游戏进程刚启动就退出了（退出码 {exitCode}），大概率没有正常运行起来。\n\n" +
                    Loc.T("Str_Cs_Recent_Console_Output_N", "最近的控制台输出：\n") + (string.IsNullOrWhiteSpace(tail) ? "(没有捕获到任何输出，可能是 Java 本身启动失败)" : tail),
                    processInfo);
            }
            else
            {
                // 「百宝箱」-「查看启动计数」：只在真正判定为启动成功（窗口出现，或至少没有
                // 提前退出）时计数，累计值持久化进 config.json，跟版本/账户切换无关。
                cfg.GameLaunchSuccessCount++;
                ConfigService.Save();

                // 需求："在游戏窗口出现之后才会说启动成功"。windowAppeared==true 是真正观察到
                // Win32 主窗口句柄出现的情况，文案照常；windowAppeared==false 但进程也没退出，
                // 属于"轮询 2 分钟仍未见到窗口、但进程还活着"的边界情况（极少数环境下窗口创建
                // confirm 得比 2 分钟还慢，或者是没有可见窗口的服务端/无头场景），不应该武断地
                // 说"启动成功"，改用更谨慎的措辞，明确告诉用户"进程仍在运行、窗口还没等到"。
                if (windowAppeared)
                {
                    // 需求："让所有弹窗提示均在窗口内内嵌，不弹出新窗口，就像 PCL 一样"。
                    // "游戏启动成功"是纯告知性提示，用户不需要做任何决定——做成必须点"确定"的
                    // 模态框，等于在游戏起来之后还要求用户回来点一下，是纯多余的一步。
                    // 改成右下角 Toast，几秒后自己消失，不阻塞任何操作（PCL 就是这个体验）。
                    // 判断标准见 ToastService 类头注释：需要决定的才用模态，只是告知的一律 Toast。
                    ToastService.ShowSuccess($"游戏已启动：{account.DisplayLabel} - {cfg.SelectedVersionId}");
                }
                else
                {
                    ToastService.ShowSuccess($"游戏进程仍在运行（尚未检测到窗口）：{account.DisplayLabel} - {cfg.SelectedVersionId}");
                }
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Launch_Failed_Check_That_Java_And_The_Ga", "启动失败，请检查 Java、游戏文件是否完整，或查看「日志」页面获取更多信息。"), $"[启动失败] {ex}", "启动失败");
        }
        finally
        {
            // 不管走成功/崩溃/异常哪条分支，等待过程结束后都要把状态提示收起来，
            // 不能让"等待游戏窗口出现…"这行字永久挂在侧边栏上。
            HideLaunchStatus();
        }
    }

    /// <summary>在侧边栏"启动游戏"按钮上方显示一行启动状态提示文字。</summary>
    private void ShowLaunchStatus(string text)
    {
        LaunchStatusText.Text = text;
        LaunchStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>收起侧边栏的启动状态提示。</summary>
    private void HideLaunchStatus()
    {
        LaunchStatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>一键关闭游戏：结束所有正在运行的游戏进程。</summary>
    private void CloseAllGames_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessManager.Running.Count == 0)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_No_Game_Is_Currently_Running", "当前没有正在运行的游戏。"));
            return;
        }
        var confirm = MessageBoxDialog.ShowConfirm($"确定要关闭全部 {ProcessManager.Running.Count} 个正在运行的游戏吗？",
            "一键关闭游戏");
        if (confirm)
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
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_No_Game_Is_Currently_Running", "当前没有正在运行的游戏。"));
            return;
        }
        var win = new ProcessManagerWindow(ProcessManager, ProcessManagerWindow.Mode.SelectToClose);
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
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_No_Game_Is_Currently_Running", "当前没有正在运行的游戏。"));
            return;
        }
        var win = new ProcessManagerWindow(ProcessManager, ProcessManagerWindow.Mode.MarkUnresponsive);
        win.ShowDialog();
        RefreshSidebar();
    }

    // ===================================================================
    // ===== Overlay 弹窗宿主实现（配合 OverlayDialogService 使用） =====
    // 这几个方法是 internal，只给同程序集内的 OverlayDialogService 调用，不对外暴露——
    // "怎么把一个弹窗塞进/摘出可视化树、怎么播放进出场动画"是纯粹的宿主实现细节，
    // OverlayDialogService 只需要知道"渲染这个内容"和"收起整个遮罩层"这两个操作，
    // 不需要关心 MainWindow.xaml 里具体是哪几个命名元素在起作用。
    // ===================================================================

    /// <summary>把某个弹窗内容渲染到 Overlay 层并显示，播放进场动画。
    /// animateIn=false 用于"弹窗里弹出的子弹窗关闭后，恢复显示上一层"的场景——那种情况
    /// 更接近"揭开覆盖层露出原来就在那的内容"，用更快、更轻的过渡即可，不需要完整的
    /// 进场动画，避免用户以为又打开了一个全新的弹窗。</summary>
    internal void OverlayRenderEntry(object content, bool animateIn)
    {
        OverlayRoot.Visibility = Visibility.Visible;
        OverlayContentHost.Content = content;

        // 修复"关掉某个提示框/弹窗后，整页所有按钮都点不动"系列问题的根因：
        // OverlayDismissEntry 淡出时是用 BeginAnimation 在 OverlayScrim/OverlayCard/
        // OverlayCardScale 这几个属性上挂了动画时钟；WPF 里"动画时钟"的优先级高于
        // "直接赋值的本地值"——只要那个时钟还挂在属性上没有被显式清除，即使外面
        // 简单地写 OverlayScrim.Opacity = 1，实际生效的值仍然由那个（可能还没走完，
        // 或者已经走完但没有被清除）的动画时钟决定，赋值会被静默忽略。
        // 如果上一次 OverlayDismissEntry 的淡出动画/兜底延时判定跟这一次
        // "恢复显示上一层"(animateIn=false，比如从一个子弹窗如 MessageBoxDialog
        // 返回到它上面的 ExperimentalFeaturesWindow) 前后脚发生，遗留的旧时钟就可能
        // 让 OverlayScrim 视觉上停留在透明（Opacity 实际还是 0）却仍然
        // Visibility=Visible 且占据命中测试，表现就是"看不见任何弹窗了，但整个界面
        // 点哪里都没反应"——不是没收起遮罩，而是遮罩收起了"看起来"，命中测试没收起。
        // 这里在每次重新渲染 Overlay 内容之前，先显式清空这几个属性上可能残留的动画
        // 时钟（BeginAnimation(prop, null)），保证接下来的赋值/新动画一定是从干净状态
        // 开始生效，不会被过期的旧时钟顶掉。
        OverlayScrim.BeginAnimation(OpacityProperty, null);
        OverlayCard.BeginAnimation(OpacityProperty, null);
        OverlayCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        OverlayCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        // 每次都重新测量一次内容尺寸：不同弹窗大小不同，OverlayCard 靠
        // HorizontalAlignment/VerticalAlignment=Center 自动居中，不需要手动算坐标。
        if (!animateIn)
        {
            OverlayScrim.Opacity = 1;
            OverlayCardScale.ScaleX = 1;
            OverlayCardScale.ScaleY = 1;
            OverlayCard.Opacity = 1;
            return;
        }

        // 进场动画：遮罩淡入 + 卡片从 96% 缩放到 100% 同时淡入，跟主流弹窗库（比如网页端
        // Modal）的观感一致，比"瞬间出现"更柔和，也能让用户明确注意到"有新内容出现了"。
        OverlayScrim.Opacity = 0;
        OverlayCard.Opacity = 0;
        OverlayCardScale.ScaleX = 0.96;
        OverlayCardScale.ScaleY = 0.96;

        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        OverlayScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        OverlayCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        OverlayCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        OverlayCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    /// <summary>播放退场动画后调用 onComplete（由 OverlayDialogService 传入，负责把结果
    /// 回传给等待方、以及恢复上一层弹窗或彻底收起 Overlay）。</summary>
    internal void OverlayDismissEntry(object content, Action onComplete)
    {
        var duration = TimeSpan.FromMilliseconds(110);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        // 修复："实验性功能"关掉之后整页按钮全部点不动"：根因是这里以前只靠
        // cardFade.Completed 事件来触发 onComplete()（进而触发 OverlayHideRoot()
        // 把 OverlayRoot 收起来）。但 OverlayRenderEntry 在恢复上一层弹窗时
        // （CloseTop 里 _stack.Count > 0 的分支）会立刻对同一个 OverlayCard.Opacity
        // 再调一次 BeginAnimation——WPF 里对同一个依赖属性重新 BeginAnimation 会
        // 直接替换掉前一个动画时钟，被替换掉的动画的 Completed 事件不保证触发。
        // 一旦这次 Completed 没触发，onComplete()/OverlayHideRoot() 就永远不会被
        // 调用，OverlayRoot 卡在 Visibility=Visible（即便看起来透明），继续占据
        // 全屏命中测试，导致底下所有按钮的点击全部被这个隐形遮罩吃掉。
        // 用一个一次性标记 + Dispatcher 兜底延时代替"只信任动画事件"，保证无论
        // Completed 是否触发，onComplete 都会且只会被调用一次。
        var completedOnce = false;
        void RunOnce()
        {
            if (completedOnce) return;
            completedOnce = true;
            if (ReferenceEquals(OverlayContentHost.Content, content)) OverlayContentHost.Content = null;
            onComplete();
        }

        var cardFade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        cardFade.Completed += (_, _) => RunOnce();

        OverlayCard.BeginAnimation(OpacityProperty, cardFade);
        OverlayScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = ease });
        OverlayCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, duration) { EasingFunction = ease });
        OverlayCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, duration) { EasingFunction = ease });

        // 兜底：动画本该在 duration 之后完成，多给 150ms 余量；如果 Completed 因为
        // 上面说的替换问题没能触发，这里保证 onComplete 依然会被调用一次，
        // OverlayRoot 不会永久卡住。
        // 注意：Dispatcher.InvokeAsync 没有直接接受延时的重载（那是 DispatcherTimer 的
        // 职责，上一版写成 InvokeAsync(..., TimeSpan) 导致编译错误 CS1503），
        // 这里改用一次性 DispatcherTimer 来做延时兜底。
        var fallbackTimer = new DispatcherTimer { Interval = duration + TimeSpan.FromMilliseconds(150) };
        fallbackTimer.Tick += (_, _) =>
        {
            fallbackTimer.Stop();
            RunOnce();
        };
        fallbackTimer.Start();
    }

    /// <summary>整个弹窗栈都关闭完了，彻底收起 OverlayRoot（Visibility=Collapsed，
    /// 恢复"不占用命中测试"的状态，见 MainWindow.xaml 对 OverlayRoot 的注释）。</summary>
    internal void OverlayHideRoot()
    {
        // 加一层判断：只有当前确实没有内容挂在 OverlayContentHost 上时才收起 OverlayRoot。
        // 上面 OverlayDismissEntry 的兜底延时回调有极小概率跟"栈里又压入了新弹窗"的时序
        // 撞在一起，这里避免误把刚显示出来的新弹窗所在的 OverlayRoot 又给 Collapsed 掉。
        if (OverlayContentHost.Content != null) return;
        OverlayRoot.Visibility = Visibility.Collapsed;
    }

    /// <summary>点击遮罩（弹窗卡片以外的半透明黑色区域）。只有事件源就是 OverlayScrim
    /// 本身时才处理——弹窗卡片内部控件的点击事件会先被卡片内部消费，理论上不会冒泡到
    /// 这里，这个判断是双重保险，避免未来某个弹窗内容意外把事件冒泡上来时被误判成
    /// "点了背景"而错误关闭。</summary>
    private void OverlayScrim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, OverlayScrim)) return;
        OverlayDialogService.RequestDismissTopByBackgroundClick();
    }

    // ==================== 拖拽安装 ====================
    // 需求："加入拖动即可导入整合包功能"、"拖入 mod 资源包等会自动安装到所选的这个游戏实例中"。
    //
    // 挂在 Window 级别（MainWindow.xaml 里 AllowDrop="True" + 三个事件），而不是挂在某个页面上：
    // 用户不会记得"要先切到 Mod 管理页才能拖"，在任何页面拖进来都应该接住。
    // 具体装到哪由 ResolveDropTargetInstanceDir() 统一决定，跟启动游戏用的是同一个实例目录，
    // 保证"装进去的东西游戏一定读得到"。

    private readonly DragDropInstallService _dragDropService = new();

    /// <summary>拖拽的目标实例目录：当前选中的版本 + 当前的版本隔离设置。
    /// 跟 LauncherService 启动时算出来的游戏目录口径完全一致——隔离开启时是
    /// versions/&lt;id&gt;，关闭时是 .minecraft 根目录。口径不一致的话会出现
    /// "提示装好了但游戏里看不到"这种最难排查的问题。</summary>
    private string? ResolveDropTargetInstanceDir()
    {
        try
        {
            var cfg = ConfigService.Config;
            var versionId = cfg.SelectedVersionId;
            if (string.IsNullOrEmpty(versionId)) return null;

            // 跟 LaunchInternalAsync 里取当前文件夹的写法保持一致，别自造第二套口径。
            var folder = cfg.Folders.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath)
                         ?? cfg.Folders.FirstOrDefault();
            if (folder == null) return null;

            var isolate = cfg.VersionIsolationOverrides.TryGetValue(versionId, out var ov)
                ? ov
                : cfg.IsolateVersionsByDefault;

            return isolate ? Path.Combine(folder.Path, "versions", versionId) : folder.Path;
        }
        catch
        {
            return null;
        }
    }

    private void MainWindow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        // 悬停时就把"会发生什么"说清楚，而不是等松手才知道装到哪去了。
        var target = ResolveDropTargetInstanceDir();
        if (target == null)
        {
            DragHintTitle.Text = Loc.T("Str_Cs_No_Game_Instance_Selected", "还没有选择游戏实例");
            DragHintDetail.Text = "请先在「版本选择」里选一个版本，再把文件拖进来。";
        }
        else
        {
            var kinds = paths.Select(_dragDropService.Classify).ToList();
            DragHintTitle.Text = Loc.T("Str_Drop_Title", "松手即可安装");
            DragHintDetail.Text = $"{DescribeKinds(kinds)}\n将安装到：{Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar))}";
        }

        DragHintLayer.Visibility = Visibility.Visible;
    }

    private static string DescribeKinds(List<DragDropInstallService.DropKind> kinds)
    {
        var names = new List<string>();
        void Add(DragDropInstallService.DropKind k, string label)
        {
            var n = kinds.Count(x => x == k);
            if (n > 0) names.Add($"{n} 个{label}");
        }
        Add(DragDropInstallService.DropKind.Mod, "Mod");
        Add(DragDropInstallService.DropKind.ResourcePack, "材质包");
        Add(DragDropInstallService.DropKind.ShaderPack, "光影包");
        Add(DragDropInstallService.DropKind.DataPack, "数据包");
        Add(DragDropInstallService.DropKind.World, "存档");
        Add(DragDropInstallService.DropKind.Modpack, "整合包");
        Add(DragDropInstallService.DropKind.BedrockContent, "基岩版内容");
        Add(DragDropInstallService.DropKind.Unknown, "无法识别的文件");
        return names.Count == 0 ? "没有可安装的内容" : string.Join("、", names);
    }

    /// <summary>
    /// 拖进来的整合包。
    ///
    /// 需求原文："拖入 modrinth 和 XCL 的整合包时从 0 下载一个版本实例去安装
    /// （允许用户自定义新的实例名称），而不是只在当前文件夹里面覆盖安装"。
    ///
    /// 所以默认走 ModpackInstallService.InstallToNewInstanceAsync：
    ///   读清单拿到 MC 版本 + 加载器 → 用用户起的名字新建实例目录 →
    ///   下载原版本体 → 装加载器 → 最后才解整合包内容。
    ///
    /// 这跟旧行为有本质区别：旧的只是把 mods/config 解压覆盖进某个已有实例，
    /// 整合包要 Fabric 1.20.1 而你当前实例是原版 1.21 的话，装完必崩且看不出原因。
    ///
    /// 想装进已有实例仍然可以——设置里把「拖入整合包时新建实例」关掉，
    /// 就会退回原来的 ModpackTargetVersionDialog 让你选目标目录。
    /// </summary>
    private async Task ImportDroppedModpackAsync(string modpackPath)
    {
        var cfg = ConfigService.Config;
        var folder = cfg.Folders.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath)
                     ?? cfg.Folders.FirstOrDefault();
        if (folder == null)
        {
            ToastService.ShowWarning(Loc.T("Str_Cs_No_Minecraft_Folder_Is_Configured_So_The", "还没有配置 .minecraft 文件夹，无法导入整合包。"));
            return;
        }

        var installer = new ModpackInstallService(cfg);
        var req = installer.ReadRequirements(modpackPath);

        // ---------- 走"从零新建实例"这条路 ----------
        if (cfg.ModpackDropCreatesNewInstance)
        {
            var suggested = ModpackInstallService.MakeUniqueInstanceName(
                folder.Path,
                string.IsNullOrWhiteSpace(req.Name) ? Path.GetFileNameWithoutExtension(modpackPath) : req.Name!);

            var nameDialog = new NewInstanceNameDialog(suggested, req.McVersion, req.Loader, req.LoaderVersion);
            if (OverlayDialogService.ShowModal(nameDialog) != true) return;

            var instanceName = nameDialog.InstanceName;

            // Forge/NeoForge 的安装器必须用本地 Java 跑；Fabric/Quilt 不需要。
            // 这里提前解析一次，解析不到就传 null，由 InstallToNewInstanceAsync 在
            // 真正需要时抛一句人话出来（而不是跑到一半才失败）。
            string? javaExe = null;
            try { javaExe = new JavaService().FindJava(cfg.JavaPath, configService: ConfigService); }
            catch { }

            var progressDialog = new ProgressDialog($"正在从零安装整合包「{instanceName}」...");
            progressDialog.Show();
            try
            {
                var result = await installer.InstallToNewInstanceAsync(
                    modpackPath, folder.Path, instanceName, javaExe, progressDialog.Progress);

                // 装完直接把新实例设为当前选中，用户点启动就能玩，不用自己再去版本列表找一遍。
                cfg.SelectedVersionId = result.InstanceId;
                try { ConfigService.Save(); } catch { }
                RefreshSidebar();

                if (result.FailedFiles.Count > 0)
                {
                    // 有 mod 没下下来必须明说：静默失败会让用户拿到一个缺 mod 的实例，
                    // 进游戏才崩，最难排查。这属于"必须让用户读完"，用模态框而不是 Toast。
                    MessageBoxDialog.ShowInfo(
                        $"整合包已装成新实例「{result.InstanceId}」（{result.McVersion} {result.Loader}），" +
                        $"但有 {result.FailedFiles.Count} 个文件没能下载成功，需要手动补齐：\n\n" +
                        string.Join("\n", result.FailedFiles.Take(10)),
                        Loc.T("Str_Cs_Some_Modpack_Files_Failed_To_Download", "整合包部分文件未下载成功"));
                }
                else
                {
                    ToastService.ShowSuccess(
                        $"已装成新实例「{result.InstanceId}」（{result.McVersion}{(string.IsNullOrEmpty(result.Loader) ? "" : " " + result.Loader)}），已切换为当前版本");
                }
            }
            catch (Exception ex)
            {
                ErrorPresenter.ShowFriendlyError(
                    ex is InvalidOperationException ? ex.Message : "从零安装整合包失败，可能是网络问题或磁盘空间不足。",
                    ex.ToString(), Loc.T("Str_Cs_Modpack_Installation_Failed", "安装整合包失败"));
            }
            finally
            {
                progressDialog.Close();
            }
            return;
        }

        // ---------- 退回旧行为：装进用户选的已有/新建目录（不下载本体和加载器）----------
        var folderService = new FolderService();
        List<GameVersion> existing;
        try { existing = folderService.ScanVersions(folder.Path); }
        catch { existing = new List<GameVersion>(); }

        var suggestedLegacy = Path.GetFileNameWithoutExtension(modpackPath);
        var dialog = new ModpackTargetVersionDialog(suggestedLegacy, existing);
        if (OverlayDialogService.ShowModal(dialog) != true) return;

        var targetDir = Path.Combine(folder.Path, "versions", dialog.TargetVersionId);

        var pd = new ProgressDialog($"正在导入整合包 {Path.GetFileName(modpackPath)} ...");
        pd.Show();
        try
        {
            var service = new ModpackService();
            if (ModpackService.IsMrpack(modpackPath))
            {
                var r = await service.ImportMrpackAsync(modpackPath, targetDir,
                    new Progress<string>(msg => pd.Progress.Report(new ProgressInfo(msg, 0, 1, ""))));

                if (r.FailedFiles.Count > 0)
                {
                    MessageBoxDialog.ShowInfo(
                        $"整合包已导入到「{dialog.TargetVersionId}」，但有 {r.FailedFiles.Count} 个文件下载失败，" +
                        $"需要手动补齐：\n\n{string.Join("\n", r.FailedFiles.Take(10))}",
                        "整合包部分文件未下载成功");
                }
                else
                {
                    ToastService.ShowSuccess($"整合包已导入到「{dialog.TargetVersionId}」");
                }
            }
            else
            {
                await Task.Run(() => service.Import(modpackPath, targetDir));
                ToastService.ShowSuccess($"整合包已导入到「{dialog.TargetVersionId}」");
            }

            RefreshSidebar();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Importing_The_Modpack_Failed_The_File_Ma", "导入整合包失败，可能是文件损坏或磁盘空间不足。"),
                ex.ToString(), Loc.T("Str_Cs_Modpack_Import_Failed", "导入整合包失败"));
        }
        finally
        {
            pd.Close();
        }
    }

    private void MainWindow_DragLeave(object sender, DragEventArgs e)
    {
        DragHintLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 当前显示的是哪个页面。拖拽的默认行为跟页面走（需求原文：
    /// "在服务器管理页面拖入 jar 文件，默认会给服务器安装；在主页版本选择其他界面拖动进入，
    /// 默认给当前选中实例安装模组"）。
    /// </summary>
    private bool IsOnServerManagerPage => MainContent.Content is ServerManagerPage;

    /// <summary>
    /// 按"当前页面 + 设置项"决定每个拖入文件的去向，必要时弹内嵌选择框问用户。
    /// 返回 null 表示用户取消了整个操作。
    /// </summary>
    private async Task<Dictionary<string, DragDropInstallService.DropKind>?> ResolveDropKindsAsync(string[] paths)
    {
        var cfg = ConfigService.Config;
        var overrides = new Dictionary<string, DragDropInstallService.DropKind>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            // ---------- .jar：按页面决定装给客户端还是服务端 ----------
            if (ext == ".jar")
            {
                var target = IsOnServerManagerPage ? cfg.ServerPageJarDropTarget : cfg.DefaultJarDropTarget;

                if (target == DropJarTarget.Ask)
                {
                    var dlg = new DropJarTargetDialog(path, IsOnServerManagerPage);
                    if (OverlayDialogService.ShowModal(dlg) != true) return null;
                    target = dlg.SelectedTarget;
                    if (dlg.Remember)
                    {
                        if (IsOnServerManagerPage) cfg.ServerPageJarDropTarget = target;
                        else cfg.DefaultJarDropTarget = target;
                        try { ConfigService.Save(); } catch { }
                    }
                }

                overrides[path] = target == DropJarTarget.Server
                    ? DragDropInstallService.DropKind.ServerJar
                    : DragDropInstallService.DropKind.Mod;
                continue;
            }

            // ---------- .zip 且内容认不出来：按设置决定问不问 ----------
            if (_dragDropService.IsAmbiguousZip(path))
            {
                var def = cfg.ZipDropDefault;
                if (def == DropZipDefault.Ask)
                {
                    var preselect = DragDropInstallService.DropKind.Modpack;
                    var dlg = new DropTypeChoiceDialog(path, preselect);
                    if (OverlayDialogService.ShowModal(dlg) != true) return null;

                    overrides[path] = dlg.SelectedKind;
                    if (dlg.Remember)
                    {
                        cfg.ZipDropDefault = dlg.SelectedKind switch
                        {
                            DragDropInstallService.DropKind.ResourcePack => DropZipDefault.ResourcePack,
                            DragDropInstallService.DropKind.Modpack => DropZipDefault.Modpack,
                            _ => DropZipDefault.Ask,
                        };
                        try { ConfigService.Save(); } catch { }
                    }
                }
                else
                {
                    overrides[path] = def == DropZipDefault.ResourcePack
                        ? DragDropInstallService.DropKind.ResourcePack
                        : DragDropInstallService.DropKind.Modpack;
                }
            }
        }

        await Task.CompletedTask;
        return overrides;
    }

    private async void MainWindow_Drop(object sender, DragEventArgs e)
    {
        DragHintLayer.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (paths.Length == 0) return;

        // 先把"每个文件该怎么处理"定下来（可能会弹选择框），再动手装。
        var overrides = await ResolveDropKindsAsync(paths);
        if (overrides == null) return;  // 用户取消

        // 服务端 jar 单独走服务端安装路径，不需要客户端实例目录。
        var serverJars = overrides.Where(kv => kv.Value == DragDropInstallService.DropKind.ServerJar)
                                  .Select(kv => kv.Key).ToList();
        if (serverJars.Count > 0)
        {
            InstallJarsToSelectedServer(serverJars);
            foreach (var j in serverJars) overrides.Remove(j);
            if (overrides.Count == 0 && paths.Length == serverJars.Count) return;
        }

        var target = ResolveDropTargetInstanceDir();
        if (target == null)
        {
            ToastService.ShowWarning(Loc.T("Str_Drop_NoInstance", "请先在「版本选择」里选一个游戏版本，再拖入文件。"));
            return;
        }

        var remaining = paths.Where(p => !serverJars.Contains(p)).ToArray();
        if (remaining.Length == 0) return;

        DragDropInstallService.DropResult result;
        try
        {
            result = await Task.Run(() => _dragDropService.InstallMany(remaining, target, null, overrides));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Drag_And_Drop_Install_Failed_The_Target_", "拖拽安装失败，可能是目标目录没有写入权限。"),
                ex.ToString(), Loc.T("Str_Cs_Drag_And_Drop_Install_Failed", "拖拽安装失败"));
            return;
        }

        // 整合包：按设置决定"从零建新实例"还是"装进已有实例"。
        foreach (var modpack in result.Modpacks)
            await ImportDroppedModpackAsync(modpack);

        // 基岩版内容（.mcworld/.mcpack/.mcaddon/.mctemplate）直接就地导入，
        // 不再只是提示用户自己去别处操作。基岩版没装时 ImportMany 会抛出带说明的异常。
        if (result.BedrockItems.Count > 0)
            await ImportDroppedBedrockAsync(result.BedrockItems);

        if (result.Installed.Count > 0)
        {
            ToastService.ShowSuccess(result.Installed.Count == 1
                ? $"已安装：{result.Installed[0]}"
                : $"已安装 {result.Installed.Count} 个文件到当前实例");
        }

        if (result.Skipped.Count > 0)
            ToastService.ShowWarning($"有 {result.Skipped.Count} 个文件没有安装：{string.Join("；", result.Skipped.Take(3))}");

        if (!result.AnythingHappened && result.Skipped.Count == 0)
            ToastService.ShowInfo(Loc.T("Str_Cs_Nothing_Installable_Was_Found_In_What_Yo", "拖进来的文件里没有可安装的内容。"));
    }

    /// <summary>
    /// 拖进来的基岩版内容：世界 / 资源包 / 行为包 / 附加包 / 世界模板。
    /// 全部是解压到 com.mojang 下对应子目录的纯本地操作，不涉及任何 Store 许可证。
    /// 见 BedrockContentService 类头注释里对"基岩版能做什么、不能做什么"的说明。
    /// </summary>
    private async Task ImportDroppedBedrockAsync(List<string> paths)
    {
        var service = new BedrockContentService();

        if (!BedrockContentService.IsBedrockDataPresent)
        {
            // 这属于"必须让用户读完并且要去做一件事"的情况，用模态框而不是 Toast。
            MessageBoxDialog.ShowInfo(
                "这台电脑上还没有安装 Minecraft for Windows（基岩版），无法导入基岩版内容。\n\n" +
                "请先从 Microsoft Store 安装基岩版，并**至少启动一次**（首次启动才会创建数据目录），再来导入。",
                Loc.T("Str_Cs_Bedrock_Edition_Isn_T_Installed", "还没有安装基岩版"));
            return;
        }

        var pd = new ProgressDialog("正在导入基岩版内容 ...");
        pd.Show();
        try
        {
            var r = await Task.Run(() => service.ImportMany(paths,
                new Progress<string>(msg => pd.Progress.Report(new ProgressInfo(msg, 0, 1, "")))));

            if (r.Installed.Count > 0)
            {
                ToastService.ShowSuccess(r.Installed.Count == 1
                    ? $"已导入基岩版内容：{r.Installed[0]}"
                    : $"已导入 {r.Installed.Count} 个基岩版内容，重启基岩版后生效");
            }
            if (r.Failed.Count > 0)
                ToastService.ShowWarning($"有 {r.Failed.Count} 个没导入成功：{string.Join("；", r.Failed.Take(3))}");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : Loc.T("Str_Cs_Couldn_T_Import_The_Bedrock_Content", "导入基岩版内容失败。"),
                ex.ToString(), Loc.T("Str_Cs_Bedrock_Import_Failed", "导入基岩版内容失败"));
        }
        finally
        {
            pd.Close();
        }
    }

    /// <summary>
    /// 把 jar 装进当前选中的服务器实例的 mods/ 目录。
    /// 在「服务端管理」页拖 jar 时走这条路（需求："在服务器管理页面拖入 jar 文件，
    /// 默认会给服务器安装"）。没有选中服务器时提示用户先选一个，而不是默默装到客户端去。
    /// </summary>
    private void InstallJarsToSelectedServer(List<string> jarPaths)
    {
        // ServerInstance 上标了 IsDefault 的优先，没有就取第一个。
        // （服务端实例没有像客户端 SelectedVersionId 那样的"当前选中"配置项，
        //  IsDefault 是这个模型里已有的、语义最接近的字段。）
        var instance = ServerInstanceService.Instances.FirstOrDefault(i => i.IsDefault)
                       ?? ServerInstanceService.Instances.FirstOrDefault();

        if (instance == null || string.IsNullOrEmpty(instance.Directory))
        {
            ToastService.ShowWarning(Loc.T("Str_Cs_No_Server_Exists_Yet_So_Server_Mods_Can_", "还没有创建服务器实例，无法安装服务端 Mod。请先到「服务端管理」新建一个。"));
            return;
        }

        var modsDir = Path.Combine(instance.Directory, "mods");
        var ok = 0;
        var failed = new List<string>();
        try
        {
            Directory.CreateDirectory(modsDir);
            foreach (var jar in jarPaths)
            {
                try
                {
                    var dest = Path.Combine(modsDir, Path.GetFileName(jar));
                    var baseName = Path.GetFileNameWithoutExtension(dest);
                    var i = 2;
                    while (File.Exists(dest))
                    {
                        dest = Path.Combine(modsDir, $"{baseName} ({i}).jar");
                        i++;
                    }
                    File.Copy(jar, dest);
                    ok++;
                }
                catch (Exception ex) { failed.Add($"{Path.GetFileName(jar)}（{ex.Message}）"); }
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Install_The_Server_Mod_The_Serv", "安装服务端 Mod 失败，可能是服务器目录没有写入权限。"),
                ex.ToString(), Loc.T("Str_Cs_Server_Mod_Installation_Failed", "安装服务端 Mod 失败"));
            return;
        }

        if (ok > 0) ToastService.ShowSuccess($"已给服务器「{instance.DisplayName}」安装 {ok} 个 Mod");
        if (failed.Count > 0) ToastService.ShowWarning($"有 {failed.Count} 个没装上：{string.Join("；", failed.Take(3))}");
    }

}
