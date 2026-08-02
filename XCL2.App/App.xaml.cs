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

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(DataDir, "runtime")); // java
        Directory.CreateDirectory(Path.Combine(DataDir, "scripts")); // 导出的启动脚本

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
            MessageBox.Show("发生未处理的异常：\n" + GetFullExceptionMessage(args.Exception), "XCL2 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 等价于原来 Program.cs 里的 app.Run(new Views.MainWindow())：显式创建并显示
        // 主窗口。App.xaml 没有设置 StartupUri，所以 WPF 不会自动创建任何窗口，这一步
        // 是必须的，否则应用会启动后立刻因为"没有任何窗口、也没有设置
        // ShutdownMode=OnExplicitShutdown"而退出。
        new Views.MainWindow().Show();
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
