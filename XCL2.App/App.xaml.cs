using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App;

public partial class App : Application
{
    /// <summary>
    /// XCL2 的私有数据目录：启动器运行目录下的 "xcl2" 文件夹。
    /// 存放配置文件(config.json)、账户缓存(accounts.json)、日志、下载的 Java 等。
    /// </summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "xcl2");

    /// <summary>
    /// 需求："如果用户电脑上没有安装 .NET，就在启动的前面加上：你需要安装 .NET8 运行时
    /// 才可以继续使用本程序。"
    ///
    /// 之前的做法是新增一个 Program.cs 手写 [STAThread] static Main，并通过 csproj 的
    /// $(StartupObject) 把入口指过去。这个方向撞上了 WPF SDK 两阶段编译的一个已知兼容性坑：
    /// 项目里只要有 XAML 引用了"本地类型"（我们这里是 models:ScreenHeightFractionConverter
    /// 这种自定义转换器），SDK 就会在正式编译前先现拼一个临时项目（形如
    /// XCL2.App_xxxxxxxx_wpftmp）跑一遍 markup 编译，这个临时项目对 PresentationFramework
    /// 引用链路的处理跟正式项目不完全一致——一旦 StartupObject 指向一个不含 WPF 隐式 Main
    /// 的普通类，会连带打乱临时项目对 Window/TextBlock/StackPanel 这些最基础 WPF 类型的
    /// 解析，导致临时项目自己先编译失败（CS0117/CS0246），正式的第二遍编译也就无法进行；
    /// 就算给 StartupObject 加条件排除临时项目，临时项目本身又会因为缺少明确的 Main 重新
    /// 触发 CS0017，两头不讨好。
    ///
    /// 所以彻底换一个不涉及自定义入口点、不触碰 App.xaml 生成操作的做法：完全不写自定义
    /// Main，App.xaml 保持 SDK 默认的 ApplicationDefinition，继续用 WPF SDK 自动生成的
    /// 隐藏 Main() 作为唯一入口——这是 SDK 最常规、兼容性最好的路径，不会触发上面的坑。
    /// 运行时检测逻辑改为在 OnStartup 一开始就执行：OnStartup 本来就在 WPF 创建任何窗口
    /// 之前触发，跟原来"new App() 之后、Run(new MainWindow()) 之前"检测的时间点等价。
    /// 检测不通过时用 Shutdown() 而不是 return——OnStartup 是 void 方法，要显式调用
    /// Shutdown() 才能阻止 WPF 继续往下走去创建窗口。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsRunningOnNet8Desktop())
        {
            // 这里**必须**用系统原生 MessageBox，不能换成内嵌的 MessageBoxDialog：
            // 内嵌弹窗要挂在 MainWindow 的 OverlayContentHost 上（见 OverlayDialogService.Register），
            // 而这段代码跑在 OnStartup 里、MainWindow 还没被创建，没有宿主可挂。
            // 这是全项目仅剩的两处原生 MessageBox 之一，另一处是下面的全局未处理异常兜底，
            // 同理：异常可能发生在主窗口已经崩掉/还没建好的时刻，不能依赖它。
            MessageBox.Show(
                "你需要安装 .NET8 运行时才可以继续使用本程序。\n\n" +
                "检测到当前机器上的 .NET 运行时版本不是 8.x（或者装的是不含桌面支持的精简版），" +
                "无法正常启动 XCL2。\n\n" +
                "请前往微软官方页面下载安装「.NET Desktop Runtime 8.0」（Windows x64，" +
                "选择 \".NET Desktop Runtime\" 而不是 \"ASP.NET Core Runtime\"）后重新打开本程序：\n" +
                "https://dotnet.microsoft.com/download/dotnet/8.0",
                "缺少 .NET8 运行时", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // 修复"切换页面后才会变黑/侧边栏和底部账户区一直是浅色"：必须在 MainWindow 构造
        // 之前完成"读配置 + 应用主题/语言"，让 MainWindow.xaml 第一次 InitializeComponent()
        // 时资源字典里就已经是正确的皮肤颜色和语言，不需要任何事后刷新。这里单独 new 一个
        // ConfigService 只是为了在窗口存在之前读一次持久化配置，跟 MainWindow 自己持有的
        // ConfigService 实例互不冲突。
        var earlyConfig = new ConfigService();
        earlyConfig.Load();
        ThemeService.ApplyForCurrentState(earlyConfig.Config.GuestModeEnabled, earlyConfig.Config.UiSkin, earlyConfig.Config.IsDarkMode);
        LocalizationService.ApplyForCurrentState(earlyConfig.Config.LauncherLanguage);

        // 深色/浅色窗口图标自动切换：原来靠 App.xaml 里一条隐式 Window Style 的
        // Setter 统一设图标，那条已经移除（Setter 的样式值会跟运行时赋的本地值打架，
        // 详见 AppIconService 类头注释）。改成在这里注册一个全局类处理器：
        // 任何 Window 一旦 Loaded 就自动套用当前主题对应的图标——
        // 不需要给 17 个窗口挨个写代码，以后新增窗口也自动生效。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w) AppIconService.ApplyTo(w);
            }));

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(DataDir, "runtime")); // java
        Directory.CreateDirectory(Path.Combine(DataDir, "scripts")); // 导出的启动脚本

        // 需求："启动器每次关闭时会自动在 xcl2/logs/日期-时间-分钟-今日第几次启动启动器.log 生成日志"。
        // 文件名要在启动时就定下来（见 LauncherLogService 类注释），所以在这里、目录已创建好之后
        // 尽早调用；真正的落盘动作留到关闭时，见下面的 Exit 事件和 MainWindow.Closed。
        LauncherLogService.BeginSession(earlyConfig);

        // 兜底：正常情况下 MainWindow.Closed 会调用一次 EndSessionAndFlush（见 MainWindow 构造函数），
        // 但如果窗口没能正常触发 Closed 就整个应用退出了（比如上面 Shutdown() 分支、或者其它
        // 异常路径导致的提前退出），这里的 Application.Exit 兜底确保日志文件仍然会被写出去。
        // EndSessionAndFlush 内部做了幂等处理，不会因为被调用两次而出问题。
        Exit += (_, _) => LauncherLogService.EndSessionAndFlush();

        // 深色标题栏（修复"顶部白条"，见 WindowChromeService 类注释）：项目里有 20 多个
        // Window（MainWindow + 各种弹窗），逐个在各自构造函数里调用
        // WindowChromeService.HookTitleBarTheme 既繁琐又容易漏改新增窗口。改用 WPF 的
        // EventManager.RegisterClassHandler 在 Window 类型这一级注册一次 SourceInitialized
        // 的处理器，就能让"当前已存在的 + 以后新增的"所有 Window 子类都自动生效，不需要
        // 逐个窗口文件里重复接线——新建一个窗口类不需要为了标题栏深色这件事额外写任何代码。
        // （MainWindow 自己另外单独调用了一次 HookTitleBarTheme，属于重复调用，无副作用：
        // ApplyTitleBarTheme 只是幂等地设置同一个 DWM attribute，调用两次效果和调用一次一样。）
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w) WindowChromeService.ApplyTitleBarTheme(w, ThemeService.CurrentIsDarkMode);
            }));

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "logs", "crash.log"),
                    $"[{DateTime.Now}] {args.Exception}\n\n");
            }
            catch { /* ignore */ }
            LauncherLogService.AppendLine($"[未处理异常] {args.Exception.GetType().Name}: {args.Exception.Message}");
            // 同上：全局未处理异常的最后兜底，此时主窗口可能已经不可用，
            // 只能用系统原生 MessageBox，不能走内嵌 Overlay。
            MessageBox.Show("发生未处理的异常：\n" + GetFullExceptionMessage(args.Exception), "XCL2 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 等价于原来 Program.cs 里的 app.Run(new Views.MainWindow())：显式创建并显示
        // 主窗口。App.xaml 没有设置 StartupUri，所以 WPF 不会自动创建任何窗口，这一步
        // 是必须的，否则应用会启动后立刻因为"没有任何窗口、也没有设置
        // ShutdownMode=OnExplicitShutdown"而退出。
        var mainWindow = new Views.MainWindow();
        mainWindow.Show();

        // 修复"首次打开白屏，要手动拖动/全屏窗口才会渲染出内容"：这是 WPF 一个常见坑——
        // Show() 只是把窗口标记为可见（Win32 层面发出 WM_SHOWWINDOW），真正的首帧
        // 布局(Measure/Arrange)+渲染要等消息循环空闲下来才会被排上；如果 Show() 之后
        // 紧接着还有一堆 Loaded 事件、后台任务启动、绑定刷新等工作抢占了 UI 线程，
        // 第一次 Layout/Render 就会被无限推迟，表现就是"看起来是白屏，直到用户做了一次
        // 窗口大小变化——不管是拖动改变尺寸还是切换全屏——才会连带强制触发一次
        // Measure/Arrange，内容才画出来"。
        //
        // 之前只用一次 Dispatcher.Invoke(..., DispatcherPriority.Render) 占位，实测
        // 不够可靠：那一行本身也是在 OnStartup（Send 优先级、比 Render 更高）这个尚未
        // 返回的调用栈里发起的重入调用，只能强制处理"当时已经排队"的 Render 级工作项，
        // 而 MainWindow 的首次 Layout 请求往往是 Show() 内部通过 Loaded/布局失效
        // 才异步排上队的，可能比这次 Dispatcher.Invoke 本身还晚入队，从而被跳过——
        // 这也是为什么有的机器上这个坑修复了、有的机器上（时序更慢/窗口更复杂）依然
        // 会白屏。改成直接调用 mainWindow.UpdateLayout()：这是 WPF 提供的同步 API，
        // 明确语义就是"立刻强制走一次 Measure→Arrange"，不依赖任何消息队列时序/
        // 优先级排队是否"恰好"发生在这次调用之前，从根上避免"该发生的布局请求还没
        // 排上就被跳过"这个不确定性。UpdateLayout 只处理布局，不含 Win32
        // 合成/呈现那一步，所以后面仍然保留一次 Render 优先级的 Dispatcher.Invoke，
        // 让已经算好的布局结果真正被合成绘制出来——两步合起来才是"强制完整走一遍
        // 首帧"的完整流程，缺一不可。
        mainWindow.UpdateLayout();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// WPF 里"设置属性 XXX 时引发了异常"这类是外层包装异常（一般是
    /// System.Windows.Markup.XamlParseException 或 TargetInvocationException），
    /// 它自己的 Message 只会说"设置属性 XXX 时引发了异常"，完全不提到底是什么原因——
    /// 真正有诊断价值的信息在 InnerException（有时候还要再往下一层）里。之前只显示
    /// args.Exception.Message，用户看到的永远是这句没有信息量的外层包装文案，没法
    /// 定位真实问题。这里沿着 InnerException 链把每一层的类型名+消息都拼出来。
    /// </summary>
    private static string GetFullExceptionMessage(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        var current = ex;
        var depth = 0;
        while (current != null)
        {
            if (depth > 0) sb.Append("\n\n  → 内部原因：");
            sb.Append($"[{current.GetType().Name}] {current.Message}");
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 检测当前进程实际运行在哪个 .NET 版本上。用 RuntimeInformation.FrameworkDescription
    /// (形如 ".NET 8.0.7")而不是 Environment.Version(那个反映的是 CLR 内部版本号，
    /// 跟"用户装的是 .NET 几"这个概念在 .NET 5+ 之后已经对不上，容易读出误导性的结果)。
    /// 只要求主版本号是 8，不强求具体的补丁版本——.NET 的补丁版本升级向后兼容，不需要
    /// 卡死在某个具体的 8.0.x。
    /// </summary>
    private static bool IsRunningOnNet8Desktop()
    {
        try
        {
            var desc = RuntimeInformation.FrameworkDescription; // 例如 ".NET 8.0.7"
            var match = System.Text.RegularExpressions.Regex.Match(desc, @"\.NET\s+(\d+)\.");
            if (!match.Success) return true; // 解析不出版本号时不误伤用户，放行交给后续初始化去暴露真正的问题
            return int.Parse(match.Groups[1].Value) >= 8;
        }
        catch
        {
            // 检测本身出错不应该拦住启动——宁可放过一个真正有问题的环境，
            // 也不要因为检测逻辑自身的 bug 挡住所有正常用户。
            return true;
        }
    }
}
