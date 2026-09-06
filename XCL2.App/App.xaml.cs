using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using XCL2.App.Services;

namespace XCL2.App;

public partial class App : Application
{
    /// <summary>
    /// XCL2 的便携数据目录：启动器运行目录下的 "xcl2" 文件夹。
    /// 主要存放日志、下载的 Java、缓存和便携资源；2.2.9 起全局 config/账户主副本改存 AppData，
    /// 启动器目录下 json/config.json 只保留可人工恢复的配置镜像。
    /// </summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "xcl2");

    private const string WriteXorExecuteEnvVar = "DOTNET_EnableWriteXorExecute";
    private const string Win7RelaunchMarkerEnvVar = "XCL2_WIN7_WXORX_RELAUNCHED";

    /// <summary>
    /// 本次启动解析出来的命令行参数（-r / -gui / --d 等），供 MainWindow 首帧渲染完成后消费。
    /// -help 分支不会走到这里——命中 -help 直接在 OnStartup 里打印帮助并退出，不创建主窗口。
    /// </summary>
    public static CommandLineService.ParsedArgs StartupArgs { get; private set; } = new();

    /// <summary>
    /// 需求排查："主界面还没进、连启动提示窗都还没看到，没有任何报错提示，进程直接消失"。
    ///
    /// 这类"更早期"的崩溃，问题出在 OnStartup 方法体本身还没开始执行的阶段：WPF 生成的
    /// 隐藏 Main() 大致是：
    ///     App app = new App();       // 触发这个类的静态构造函数 + 实例构造函数
    ///     app.InitializeComponent(); // 解析并应用 App.xaml 里的 1500+ 行资源字典/样式
    ///     app.Run();                 // 这之后才会调用 OnStartup(e)
    /// 而 App.xaml 本身体积很大（颜色/样式/矢量图标/控件模板），任何一处 XAML 写法有问题
    /// 都会在 InitializeComponent() 这一步直接抛 XamlParseException——这个时间点比
    /// OnStartup 里原来注册的 AppDomain.CurrentDomain.UnhandledException（见下面
    /// RunStartupSequence 调用之前）还要早，原来的写法完全捕获不到，表现正是"用户看到的是
    /// 进程一闪而过或者压根没有任何窗口/弹窗，跟没双击到图标一模一样"。
    /// 同理，OnStartup 最开头 new StartupSplashWindow() / splash.Show() 这几行，本身也是在
    /// "还没走到 RunStartupSequence 的 try/catch"之前执行的，一旦这几行本身出问题（比如
    /// 某些精简版 Windows/远程桌面环境下 AllowsTransparency 相关的窗口创建异常），
    /// 同样会绕开所有现有的异常处理兜底。
    ///
    /// 解决方式：把"注册全局未处理异常兜底"这件事挪到本类的静态构造函数里——静态构造函数
    /// 保证在 new App() 这一行、也就是 InitializeComponent() 之前就已经跑完，是不触碰
    /// StartupObject/自定义 Main（会踩 WPF SDK 两阶段编译的坑，见 IsRunningOnNet8Desktop
    /// 方法注释）的前提下，能够拿到的最早的一个钩子。这里不依赖 DataDir/ConfigService 等
    /// 任何要等到 RunStartupSequence 里才初始化好的状态，落盘路径直接用最基础的
    /// AppContext.BaseDirectory 拼出来，且额外多写一份到 exe 同级目录（不需要先
    /// Directory.CreateDirectory 任何子目录），双重兜底，尽量确保"哪怕 xcl2 目录本身创建
    /// 失败"也还有地方能看到崩溃信息；同时弹出原生 MessageBox，保证用户不会再看到
    /// "什么反馈都没有，只能干猜"的局面。
    /// </summary>
    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            WriteEarliestCrashLog(ex, isTerminating: args.IsTerminating);
            ShowEarliestCrashMessageBox(ex);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteEarliestCrashLog(args.Exception, isTerminating: false, contextTag: "未观察的后台 Task 异常");
            args.SetObserved();
        };
    }

    /// <summary>
    /// 落盘尽量不依赖任何还没初始化好的状态：DataDir 只是一个基于 AppContext.BaseDirectory
    /// 的字符串拼接，本身不会抛异常，但下面创建目录、写文件这些 IO 操作可能因为各种环境问题
    /// 失败，所以整个方法体自己也要能"打不着一枪就换下一枪"，不能因为落盘失败又抛出新异常，
    /// 那样会在异常处理器里再触发一次异常，导致进程更早、更诡异地终止。
    /// </summary>
    private static void WriteEarliestCrashLog(Exception? ex, bool isTerminating, string? contextTag = null)
    {
        var tag = contextTag ?? $"AppDomain 未处理异常，IsTerminating={isTerminating}";
        var text = $"[{DateTime.Now}] [{tag}] {ex?.ToString() ?? "(无异常对象，可能是非 CLR 异常/原生崩溃)"}\n\n";

        // 主日志：跟 RunStartupSequence 里后续正常运行阶段用的是同一个文件，方便统一排查。
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
            File.AppendAllText(Path.Combine(DataDir, "logs", "crash.log"), text);
        }
        catch { /* 落盘失败也不能再抛异常，继续尝试下面的兜底路径 */ }

        // 兜底路径：万一连 xcl2 目录都创建不了（比如安装目录只读/权限问题），
        // 直接写在 exe 同级目录下，不需要额外创建任何子目录，成功率更高。
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "xcl2_crash.log"), text);
        }
        catch { /* 两份都失败的话确实没辙了，至少上面的 MessageBox 还能让用户看到内容 */ }
    }

    /// <summary>
    /// 显式弹出原生 MessageBox 而不是依赖任何内嵌 Overlay：这个阶段可能连 MainWindow 都
    /// 还不存在，也可能是在非 UI 线程上触发的，只有系统原生 MessageBox 能保证在这些情况下
    /// 都还能弹出来。MessageBox.Show 本身理论上也可能因为极端情况（比如没有桌面/无人值守
    /// 服务器环境）失败，所以同样包一层 try/catch，绝不能让"兜底逻辑自己出错"盖过真正的
    /// 原始异常信息（原始信息已经在上面 WriteEarliestCrashLog 里落盘了）。
    /// </summary>
    private static void ShowEarliestCrashMessageBox(Exception? ex)
    {
        try
        {
            var detail = ex == null ? "（未捕获到具体异常对象）" : GetFullExceptionMessage(ex);
            MessageBox.Show(
                "XCL2 在启动过程中发生了未处理的异常，程序即将退出：\n\n" + detail +
                "\n\n详细堆栈已尝试写入 xcl2\\logs\\crash.log（或程序所在目录下的 xcl2_crash.log），" +
                "可以把这个文件发给开发者排查。",
                "XCL2 启动崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* 见方法注释：这里失败也不能再抛 */ }
    }

    /// <summary>
    /// Win7 专属修复：.NET 7 开始 Runtime 默认打开 WriteXorExecute 安全特性，Win7 上跟这个
    /// 特性不兼容，会导致启动变慢、内存占用暴涨到 2G 左右（网上很少提到，GitHub 上
    /// dotnet/runtime 有相关 issue 讨论）。关掉它只需要把 DOTNET_EnableWriteXorExecute
    /// 环境变量设成 0，但这个开关必须在 CLR 启动前就通过环境变量生效——进程已经跑起来之后
    /// 再 Environment.SetEnvironmentVariable 已经来不及了，Runtime 早就按默认值初始化完了。
    /// 所以唯一可行的办法：检测到是 Win7 且这个环境变量还没设置时，带着设置好的环境变量
    /// 重新拉起自己一份新进程，当前这个旧进程立刻退出，交给新进程接管。
    ///
    /// 只在 Win7 上这么做——Win10/11 不受这个问题影响，关掉 WriteXorExecute 反而是倒退
    /// （JIT 出来的代码没有 W^X 隔离，安全性打折扣，某些场景下执行效率也会变差），所以
    /// Win10/11 上完全不碰这个环境变量、不重新拉起进程，行为和没加这段代码之前一模一样。
    /// </summary>
    private static bool TryRelaunchForWin7WriteXorExecuteFix()
    {
        // 只处理 Win7（内核版本 6.1）。Win8/8.1 是 6.2/6.3，官方支持更完整，且基岩版这类
        // 功能本来就已经按 Win10+ 判定，不需要跟着一起处理；Win10/11 内核版本号都是 10.x，
        // 直接跳过，不受影响。
        var ver = Environment.OSVersion.Version;
        var isWin7 = ver.Major == 6 && ver.Minor == 1;
        if (!isWin7) return false;

        // 已经是重新拉起之后的那个新进程了（标记环境变量已经带上了），不要再拉一次，
        // 避免自己无限重启自己。
        if (Environment.GetEnvironmentVariable(Win7RelaunchMarkerEnvVar) == "1") return false;

        // 用户/运维已经在系统层面手动设置过这个环境变量（比如自己按教程配置好了），
        // 就不要越俎代庖再包一层子进程，尊重现有设置，直接用当前进程即可。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(WriteXorExecuteEnvVar))) return false;

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                psi.ArgumentList.Add(arg);

            psi.EnvironmentVariables[WriteXorExecuteEnvVar] = "0";
            psi.EnvironmentVariables[Win7RelaunchMarkerEnvVar] = "1";

            Process.Start(psi);
            return true;
        }
        catch
        {
            // 重新拉起失败（比如权限问题）：退回原来的行为，正常在当前进程里启动，
            // 只是 Win7 上会比较慢——总比直接打不开、或者代码抛异常崩溃强。
            return false;
        }
    }

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
        // 基岩版 MSIXVC 解压隔离子进程模式：必须放在整个 OnStartup 最开头、比 Win7 修复
        // 检测还要早——这个分支根本不是要启动 UI，是自身 exe 被
        // BedrockDecode.BedrockExtractWorkerProcess.RunAsync 当作命令行工具重新拉起的
        // 一次性子进程，只需要跑完解压逻辑、把结果打到 stdout、然后整个进程退出。
        // 绝对不能往下走到 base.OnStartup/创建任何窗口——那样会平白多出一个看不见的
        // WPF 应用实例、White 掉子进程本该有的"跑完就退出"语义。
        // 用 Environment.Exit 而不是 Shutdown()：这个阶段 WPF 消息循环还没跑起来，
        // Shutdown() 依赖的机制此时不生效。
        if (e.Args.Length > 0 && e.Args[0] == BedrockDecode.BedrockExtractWorkerProcess.WorkerArgMarker)
        {
            int exitCode;
            try
            {
                exitCode = BedrockDecode.BedrockExtractWorkerProcess.RunAsWorkerAsync(e.Args).GetAwaiter().GetResult();
            }
            catch
            {
                // 兜底：worker 逻辑内部已经把能捕获的异常都转成 ERR 行处理了，这里再包一层
                // 纯粹是防止极端情况下 GetAwaiter().GetResult() 本身抛出未预期异常时，
                // 子进程还能带着非 0 退出码正常结束，而不是变成另一种诡异的卡死状态。
                exitCode = 1;
            }
            Environment.Exit(exitCode);
            return;
        }

        // 命令行参数：-help 需要在整个启动流程最前面处理——用户是想快速看一眼用法就退出，
        // 不需要（也不应该）先跑一遍 Win7 修复检测、创建启动提示窗、加载主界面这些跟
        // "查看帮助"完全无关的重活。命中 -help 直接用 MessageBox 展示帮助文本（WPF 应用
        // 默认没有附加控制台，写 Console.WriteLine 在双击运行时用户根本看不到；如果是从
        // 命令行/脚本调用，MessageBox 同样能保证用户一定能看到内容），然后立即退出，
        // 不再往下创建任何窗口。
        //
        // 其余参数（-r/-gui/--d）只是解析出来存到 StartupArgs，真正的动作（选账户、切实例、
        // 跳页面、开下载中心）留到 MainWindow 首帧渲染完成之后再执行——命令行启动这一刻，
        // 账户列表/实例列表/ConfigService 都还没加载好，此时就去匹配账户名/实例名没有意义。
        var cliArgs = CommandLineService.Parse(e.Args);

        // 访客模式必须经过一次真正的进程重启：-l 只是“请求进入访客”，当前进程不直接
        // 切模式，而是拉起带隐藏 --guest-session 标记的新进程。--wait-pid 让接力进程先等
        // 旧进程退出，避免旧实例的单实例管道还没释放就误弹“已经运行”。
        if (cliArgs.WaitForPid > 0 && cliArgs.WaitForPid != Environment.ProcessId)
        {
            try
            {
                using var oldProcess = Process.GetProcessById(cliArgs.WaitForPid);
                oldProcess.WaitForExit(15000);
            }
            catch { /* 父进程已经退出/不存在，直接继续 */ }
        }

        if (cliArgs.GuestModeRelaunchRequested)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                psi.ArgumentList.Add("--guest-session");
                psi.ArgumentList.Add("--wait-pid");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi);
            }
            Shutdown();
            return;
        }

        if (cliArgs.ShowHelp)
        {
            MessageBox.Show(CommandLineService.HelpText, "XCL2 命令行帮助", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        StartupArgs = cliArgs;

        // Win7 专属修复：必须在 base.OnStartup / 任何托管代码正式跑起来之前做，
        // 见 TryRelaunchForWin7WriteXorExecuteFix 上面的注释。
        if (TryRelaunchForWin7WriteXorExecuteFix())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 修复"启动时偶发 NullReferenceException @ ThemeService.Apply / Application.Current.Resources"：
        // ShutdownMode 默认是 OnLastWindowClose。而这里、以及下面 SingleInstanceService.HandleStartup
        // 冲突分支里，都会在 MainWindow 创建之前先弹出并关闭一个"过渡窗口"（InstanceConflictDialog）。
        // 如果这个对话框关闭时恰好是当前唯一打开的窗口，WPF 会把它当成"最后一个窗口关闭"，
        // 自动触发一次隐式的应用退出流程——即使代码逻辑上马上还要继续创建 splash/MainWindow。
        // 应用一旦进入这个隐式退出流程，稍后 RunStartupSequence 里 ThemeService.Apply() 访问
        // Application.Current.Resources 就会踩到空引用（Resources 在退出过程中被回收）。
        // 这里显式接管 ShutdownMode，把它推迟到 MainWindow 真正 Show() 出来之后再恢复默认值
        // （对应位置见下面 RunStartupSequence 里 mainWindow.Show() 之后），保证 InstanceConflictDialog/
        // splash 这些过渡窗口无论以什么顺序关闭，都不会被误判成"最后一个窗口"从而提前触发退出。
        // 不影响 MainWindow.Closed 之后依赖 OnLastWindowClose 的托盘图标/便签置顶逻辑，
        // 因为等到那时 ShutdownMode 早已恢复成默认值。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 全局未处理异常兜底（AppDomain.CurrentDomain.UnhandledException /
        // TaskScheduler.UnobservedTaskException）已经挪到本类的静态构造函数里注册，
        // 时机比这里早得多（覆盖到 App.xaml InitializeComponent 阶段，见 static App()
        // 的类头注释），这里不再重复注册，避免同一次异常被写两遍 crash.log。

        // 需求排查："根本没打开过 XCL2 时，打开 XCL2 还是会提示已有实例在运行"。
        //
        // 之前的调用顺序是：先在这里创建/Show 出启动提示窗（StartupSplashWindow），
        // 再走进 RunStartupSequence 内部才做多开探测（SingleInstanceService.HandleStartup）。
        // 这个顺序本身会带来一类不容易复现、但确实存在的假阳性：
        //   1) 启动提示窗一旦 Show() 出来，WPF 就已经有了一个真实存在的 Window——如果这个
        //      窗口的创建/首帧渲染过程比较慢（低速磁盘、杀毒软件扫描、远程桌面等环境），
        //      而用户在这个空档期又手快地再双击了一次图标（或者上一次双击图标产生的进程
        //      因为系统调度延迟，事实上还没跑到 HandleStartup 那一步），两个进程几乎同时
        //      "都还没来得及注册管道服务端"，谁也探测不到谁，最终各自都当成了第一个实例，
        //      表现为"看起来同时开出了两个启动器"（对应用户截图 1：任务栏出现两个 XCL2）。
        //   2) 反过来，一旦有一个实例先一步把管道服务端注册好、但用户此时主观上并没有
        //      "主动打开过" XCL2（比如它是上一次以"仅关闭启动器"方式退出、其实还在后台
        //      常驻的旧实例，见 MainWindow_Closing 里的说明），新进程这边不管有没有先弹
        //      启动提示窗，探测到的都是这个真实存在、但用户看不见的旧实例——这种情况下
        //      "提示窗要不要先弹出来"本身并不是根因，但把探测尽量提到最前面，能让用户在
        //      看到任何界面反馈之前就先看到多开选择框，观感上更直接对应"点开图标 = 立刻
        //      问我"，而不是"先看到一个'正在启动'的提示窗，紧接着又跳出一个多开提示"，
        //      两个弹窗前后脚出现容易让人误以为是探测逻辑本身有问题。
        //
        // 解决办法：把探测挪到全部代码最前面——比创建 StartupSplashWindow 还要早，
        // 确保"探测、以及探测到冲突时弹出的 InstanceConflictDialog"是本进程创建的
        // 第一个、也是当前唯一的窗口，不会跟启动提示窗的创建时机产生竞争。探测通过
        // （没有冲突，或者用户选择让本实例继续）之后，才创建启动提示窗，走原来的
        // RunStartupSequence 流程；RunStartupSequence 内部不再重复做这一步。
        if (!SingleInstanceService.HandleStartup(() =>
        {
            var dialog = new Views.InstanceConflictDialog();
            dialog.ShowDialog();
            return dialog.Result;
        }))
        {
            Shutdown();
            return;
        }

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

        // 需求排查："exe 完全没有任何反馈——不弹窗口、不弹错误提示，只是生成了 xcl2 目录/
        // config.json 就没有下文了"。这类问题的关键线索是：DispatcherUnhandledException
        // 处理器要到 RunStartupSequence 内部才注册，而在它注册之前，earlyConfig.Load()／
        // ThemeService.ApplyForCurrentState／LocalizationService.ApplyForCurrentState／
        // LauncherLogService.BeginSession 这几行已经先跑了——如果异常恰好发生在这些
        // "处理器还没来得及注册"的语句里，就完全不会被 DispatcherUnhandledException
        // 捕获到，表现正是"config.json 已经生成（说明 earlyConfig.Load()/EnsureDefaultFolder
        // 跑过了），但异常来自它之后、DispatcherUnhandledException 注册之前的某一行"。
        // 用一个显式 try/catch 包住从这里到窗口创建为止的全过程，任何异常都立刻落盘 +
        // 弹出原生 MessageBox，不依赖任何还没来得及注册的事件处理器，也不用等到
        // "进程静默退出"这种用户完全看不出发生了什么的结局。
        // splash 声明在 try 外面，是因为 catch 块里还要用它来关闭提示窗；但**创建**它
        // 的动作本身挪进 try 里面——这几行以前是在这层 try/catch 覆盖范围之外裸跑的
        // （见 static App() 类头注释里"更早期崩溃"的第二种情况），一旦 StartupSplashWindow
        // 的 XAML/构造过程本身出问题，之前会完全没有任何提示地直接崩溃退出。现在统一被
        // 下面这个 catch 兜住，用户至少能看到弹窗 + crash.log。
        Views.StartupSplashWindow? splash = null;
        try
        {
            // 需求："首页十分卡，特别是切换页面时，可以在启动时稍微弄一个进度条，提示正在
            // 渲染主界面，1-2s 内必须进入主界面"。真正让 MainWindow 首帧渲染变得更快涉及
            // 面比较广（各页面构造函数的懒加载改造，见 MainWindow.NavigateLazy 的注释），
            // 这里先解决"用户完全看不到任何反馈"这个更紧迫的体感问题：在 OnStartup 一开始、
            // 任何耗时初始化之前，先弹出一个几乎零依赖、瞬间就能显示出来的轻量提示窗，
            // 明确告诉用户"正在渲染主界面"，而不是让用户对着没有任何窗口出现的桌面干等。
            // 等 MainWindow 真正的首帧画出来之后立刻关掉这个提示窗、切换到 MainWindow。
            splash = new Views.StartupSplashWindow();
            splash.Show();
            // 跟下面 mainWindow.UpdateLayout() 同样的道理：Show() 不保证立刻真正画到屏幕上，
            // 这里强制走一遍首帧，确保用户能第一时间看到这个提示窗，而不是被接下来的
            // ConfigService.Load()/ThemeService 初始化这些同步工作抢占，导致提示窗跟主窗口
            // 一起延后才出现，失去了"提前反馈"的意义。
            splash.UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            RunStartupSequence(splash);
        }
        catch (Exception ex)
        {
            WriteEarliestCrashLog(ex, isTerminating: false, contextTag: "启动阶段异常，发生在主窗口创建/显示完成之前");

            // 启动失败也要把提示窗关掉，不然用户会看到一个卡死的"正在启动…"窗口
            // 停留在桌面上，跟后面弹出的错误提示框叠在一起，体验很奇怪。
            try { splash?.Close(); } catch { /* 已经关闭或窗口本身出问题时忽略 */ }

            ShowEarliestCrashMessageBox(ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// OnStartup 的主体逻辑抽成独立方法，纯粹是为了让上面那层 try/catch 能完整覆盖
    /// "从读配置到窗口创建完成"的全过程，而不需要把 try/catch 的缩进套进整个方法体里
    /// 让本来就很长的 OnStartup 变得更难读。行为跟原来完全一致，只是外层多包了一层。
    /// </summary>
    /// <param name="splash">OnStartup 一开始弹出的轻量提示窗，MainWindow 首帧渲染完成后
    /// 由本方法负责关闭并让位。</param>
    private void RunStartupSequence(Views.StartupSplashWindow splash)
    {
        // 修复"切换页面后才会变黑/侧边栏和底部账户区一直是浅色"：必须在 MainWindow 构造
        // 之前完成"读配置 + 应用主题/语言"，让 MainWindow.xaml 第一次 InitializeComponent()
        // 时资源字典里就已经是正确的皮肤颜色和语言，不需要任何事后刷新。这里单独 new 一个
        // ConfigService 只是为了在窗口存在之前读一次持久化配置，跟 MainWindow 自己持有的
        // ConfigService 实例互不冲突。
        splash.SetStatus("正在读取启动器配置…");
        var earlyConfig = new ConfigService();
        earlyConfig.Load();
        splash.SetStatus("正在应用主题与语言…");
        ThemeService.ApplyForCurrentState(earlyConfig.Config.GuestModeEnabled, earlyConfig.Config.UiSkin, earlyConfig.Config.IsDarkMode, earlyConfig.Config.CustomAccentColor);
        LocalizationService.ApplyForCurrentState(earlyConfig.Config.LauncherLanguage);

        // 多开检测（SingleInstanceService.HandleStartup）已经挪到 OnStartup 最前面、
        // 早于本方法、也早于启动提示窗创建之前执行，见 OnStartup 里对应的注释。这里不再
        // 重复检测——冲突时弹出的 InstanceConflictDialog 会用当时（尚未应用主题前）的
        // 默认配色，这是为了让"多开检测"这件事本身尽可能不依赖任何还没初始化好的状态、
        // 尽早给用户反馈，代价是这个弹窗暂时用不上用户自定义的皮肤颜色，可接受。

        // 窗口透明度 + Win11 新视觉效果：均默认关闭，这里只是把启动时读到的配置状态记下来
        // （ThemeService/Win11EffectsService 各自的静态字段），真正应用到具体窗口的时机分两处：
        // 这一刻还没有任何窗口打开的话什么也不会发生；已经打开的窗口（几乎不会出现在这么早的
        // 阶段）会被立即应用；后续新建的每一个窗口，靠下面 Win11EffectsService 对应的
        // Loaded 类处理器自动套用，不需要逐个窗口接线，见 Win11EffectsService 类注释。
        ThemeService.ApplyWindowTransparency(earlyConfig.Config.EnableWindowTransparency, earlyConfig.Config.WindowOpacityPercent);
        ThemeService.ApplyGlobalWindowTransparency(earlyConfig.Config.EnableGlobalWindowTransparency, earlyConfig.Config.GlobalWindowOpacityPercent);
        ThemeService.ApplyTextOpacity(earlyConfig.Config.TextOpacityPercent);
        ThemeService.SetPopupAppearanceConfig(earlyConfig.Config.PopupUseCustomAppearance, earlyConfig.Config.PopupOpacityPercent, earlyConfig.Config.PopupFrostPercent, earlyConfig.Config.PopupTextOpacityPercent);
        ThemeService.SetDrawerAppearanceConfig(earlyConfig.Config.DrawerUseCustomAppearance, earlyConfig.Config.DrawerOpacityPercent, earlyConfig.Config.DrawerFrostPercent, earlyConfig.Config.DrawerTextOpacityPercent);
        ScrollWheelBehavior.SetSensitivityPercent(earlyConfig.Config.MouseWheelSensitivityPercent);
        var earlyMaterial = Enum.TryParse<Win11EffectsService.BackdropMaterial>(earlyConfig.Config.Win11BackdropMaterial, out var earlyM)
            ? earlyM : Win11EffectsService.BackdropMaterial.Mica;
        Win11EffectsService.SetEnabled(earlyConfig.Config.EnableWin11VisualEffects, earlyMaterial);
        // WinUI 3 新设计：圆角（Win11EffectsService）+ 字体（ThemeService）两部分，见各自方法注释——
        // 之前这个开关只保存在配置文件里，完全没有代码读取/应用过，勾选了也没有任何视觉变化。
        Win11EffectsService.SetWinUi3Enabled(earlyConfig.Config.EnableWinUi3Design);
        ThemeService.ApplyFontFamily(earlyConfig.Config.AppFontFamily, earlyConfig.Config.EnableWinUi3Design);

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

        splash.SetStatus("正在初始化数据目录与日志…");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(DataDir, "runtime")); // java
        Directory.CreateDirectory(Path.Combine(DataDir, "scripts")); // 导出的启动脚本

        // 需求："启动器每次关闭时会自动在 xcl2/logs/日期-时间-分钟-今日第几次启动启动器.log 生成日志"。
        // 文件名要在启动时就定下来（见 LauncherLogService 类注释），所以在这里、目录已创建好之后
        // 尽早调用；真正的落盘动作留到关闭时，见下面的 Exit 事件和 MainWindow.Closed。
        LauncherLogService.BeginSession(earlyConfig);
        RegisterUiInteractionLogging();

        // 高性能模式的按钮"约束感"微交互：跟切页动画（MainWindow.SetMainContent 里
        // highPerf 分支）同一套设计语言——按下瞬间轻微缩小、抬起时 BackEase 回弹冲过头再
        // 弹回，模拟 macOS/游戏 UI 那种"有重量感"的点击反馈。用全局类处理器挂在
        // ButtonBase 上，不用逐个按钮样式改 Trigger（那样没法在运行时按设置开关热切换），
        // 每次按下/抬起时临时读一次 ConfigService.Active?.Config.EnableHighPerformanceMode，
        // 开关状态在设置页保存后立即对新的点击生效，不需要重启或重建按钮。
        // 关闭时（默认）这两个处理器直接短路返回，不创建任何 Transform/动画，零额外开销。
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((sender, _) =>
            {
                if (sender is ButtonBase b && ConfigService.Active?.Config.EnableHighPerformanceMode == true)
                    AnimateButtonPress(b, pressed: true);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((sender, _) =>
            {
                if (sender is ButtonBase b && ConfigService.Active?.Config.EnableHighPerformanceMode == true)
                    AnimateButtonPress(b, pressed: false);
            }), handledEventsToo: true);

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

        // Win11 新视觉效果（云母/亚克力背景 + 圆角）：跟上面标题栏深色模式同一套思路，
        // 用类处理器让"当前已存在的 + 以后新增的"所有 Window 子类都在 Loaded 时自动套用，
        // 不需要逐个窗口文件接线。默认关闭，Win11EffectsService.CurrentEnabled 读的是
        // 用户在设置页的最新选择（见该类注释），不是这里闭包捕获的一次性配置快照。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w) Win11EffectsService.Apply(w, Win11EffectsService.CurrentEnabled);
            }));

        // 全局窗口透明（整窗 Window.Opacity）：跟上面两个类处理器同一套思路，让"当前已存在的
        // + 以后新增的"所有 Window 子类在 Loaded 时都自动套用最新的全局透明度状态。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w) ThemeService.ApplyGlobalOpacityToWindow(w);
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
        splash.SetStatus("正在构建主界面…");
        var mainWindow = new Views.MainWindow();

        // 不再在 OnStartup 这条同步 UI 调用栈里强制 mainWindow.UpdateLayout() +
        // Dispatcher.Invoke(Render)。那种“为了保证首帧而强制同步渲染”的做法会让整个主窗口
        // 的 Measure/Arrange/Render 一次性堵住 UI 线程，复杂配置/低速磁盘机器上 Windows 会
        // 直接把窗口判成“未响应”。正确做法是先挂 ContentRendered，再 Show，然后尽快让
        // OnStartup 返回消息循环；首帧真正画出来时事件自然触发，再关闭启动提示窗。
        EventHandler? closeSplashAfterFirstFrame = null;
        closeSplashAfterFirstFrame = (_, _) =>
        {
            mainWindow.FirstFrameRendered -= closeSplashAfterFirstFrame;
            try { splash.Close(); } catch { /* splash 已关闭时忽略 */ }
        };
        mainWindow.FirstFrameRendered += closeSplashAfterFirstFrame;
        splash.SetStatus("正在渲染主界面…");
        mainWindow.Show();

        // 接线 SingleInstanceService 的两个钩子：
        // - TryActivateMainWindow：新实例静默探测"能不能把我拉到前台"时调用，具体判断
        //   "窗口是否已被 Close() 掉、只是缩在托盘/便签模式后台"的逻辑都在 MainWindow 内部
        //   （它最清楚自己当前的可见性状态），这里只是把入口接上。
        // - PerformFullExit：收到别的实例发来的"确认要关闭我"指令时调用，负责结束所有
        //   游戏/服务器子进程后再退出，同样把实现放在 MainWindow（它持有 ProcessManager/
        //   ServerProcessManager 引用）。
        // 两个委托都指向同一个 mainWindow 实例，注册一次即可长期有效——本进程存活期间
        // 只会有这一个 MainWindow。
        SingleInstanceService.TryActivateMainWindow = mainWindow.TryActivateFromAnotherInstance;
        SingleInstanceService.PerformFullExit = mainWindow.PerformFullExit;

        // MainWindow 已经真正 Show() 出来，"过渡窗口可能被误判成最后一个窗口"的风险窗口期
        // 已经结束，这里把 ShutdownMode 恢复成 WPF 默认的 OnLastWindowClose——对应上面
        // OnStartup 里 ShutdownMode = ShutdownMode.OnExplicitShutdown 那处注释。恢复之后，
        // MainWindow.xaml.cs 里原有的"关闭 MainWindow 后如果还有其它窗口/便签就亮出托盘图标"
        // 那套逻辑（依赖 OnLastWindowClose 触发时机）行为跟修复前完全一致，不受影响。
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // 自动更新检查：整个过程在后台线程进行（内部延迟几秒，不跟首帧渲染抢时间），
        // 有新版本才会弹提示，见 UpdateCheckService 类注释。
        UpdateCheckService.CheckForUpdateInBackground();
    }

    /// <summary>
    /// 修复"在部分界面里选择东西（下拉框/列表切换选中项），整个界面突然变卡"：这三个类处理器
    /// 之前是同步执行的——DescribeElement/DescribeSelectedValue 里既要顺着可视化树找宿主窗口，
    /// 又可能碰到匿名类型选项(DescribeAnonymousType)触发反射 GetProperties()+逐个 GetValue()，
    /// 这些都是纯字符串拼接/反射开销，本身不重，但它们跟触发它们的用户操作（点击/选择变化/
    /// 输入框聚焦）挤在同一次 UI 线程消息处理里同步执行，会直接顶在"选中项要重新布局/重新
    /// 应用样式"这些真正的渲染工作前面排队；像 ModManager/DownloadCenter 这类列表控件选中项
    /// 变化时本来就有一波布局+样式刷新，本已经不轻，再叠加同步的反射拼字符串，两者加在一起
    /// 才会表现成"选一下东西全局都跟着卡一下"。这里的日志本来就只是"锦上添花"的交互留痕
    /// （见 LauncherLogService 类注释），不需要跟真正的渲染工作抢首选，所以统一改成用
    /// Dispatcher.InvokeAsync 扔到 Background 优先级（比正常输入/布局/渲染都低，只在
    /// UI 线程真正闲下来之后才执行），不再阻塞当前这一次事件处理，视觉上的卡顿就消失了。
    /// </summary>
    /// <summary>高性能模式按钮按下/抬起的"约束感"缩放反馈。给按钮临时挂一个
    /// ScaleTransform(RenderTransformOrigin=0.5,0.5) 当宿主——大多数按钮本来没有自己的
    /// RenderTransform，直接复用；如果已经有别的 Transform（少数自定义按钮），跳过不处理，
    /// 避免互相覆盖冲突，这类按钮本来就没多少，缺个动效不影响使用。
    /// 按下：短促 EaseOut 缩到 0.94；抬起：BackEase(Amplitude 0.6) 从 0.94 冲回 1 再轻微
    /// 回弹，这个"冲过头再弹回"的手感就是 macOS/游戏 UI 点击反馈的核心。</summary>
    private static void AnimateButtonPress(ButtonBase button, bool pressed)
    {
        if (button.RenderTransform is not System.Windows.Media.ScaleTransform scale)
        {
            if (button.RenderTransform != null && button.RenderTransform != System.Windows.Media.Transform.Identity)
                return;
            scale = new System.Windows.Media.ScaleTransform(1, 1);
            button.RenderTransform = scale;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);

        DoubleAnimation anim;
        if (pressed)
        {
            anim = new DoubleAnimation(scale.ScaleX, 0.94, TimeSpan.FromMilliseconds(80))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        }
        else
        {
            anim = new DoubleAnimation(scale.ScaleX, 1.0, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        }
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, anim);
    }

    private static void RegisterUiInteractionLogging()
    {
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ButtonBase button)
                    LogInteractionAsync(button, () => "[交互] 点击 " + DescribeElement(button));
            }), handledEventsToo: true);

        EventManager.RegisterClassHandler(typeof(Selector), Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((sender, _) =>
            {
                if (sender is Selector selector)
                    LogInteractionAsync(selector, () => "[交互] 选择变化 " + DescribeElement(selector) +
                                                  DescribeSelectedValue(selector));
            }), handledEventsToo: true);

        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is TextBox textBox)
                    LogInteractionAsync(textBox, () => "[交互] 聚焦输入框 " + DescribeElement(textBox));
            }), handledEventsToo: true);
    }

    /// <summary>把"拼描述文本 + 写日志缓冲"这部分工作挪到 Background 优先级异步执行，见
    /// RegisterUiInteractionLogging 类注释。描述文本要在回调里（而不是调用方）才求值，
    /// 否则 DescribeElement 等反射/遍历工作还是会在触发事件的那一刻同步跑一遍，白做了这层异步。</summary>
    private static void LogInteractionAsync(System.Windows.Threading.DispatcherObject element, Func<string> describe)
    {
        element.Dispatcher.InvokeAsync(() =>
        {
            try { LauncherLogService.AppendLine(describe()); }
            catch { /* 交互留痕失败不应该影响正常使用 */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string DescribeElement(FrameworkElement element)
    {
        var window = Window.GetWindow(element)?.GetType().Name ?? "UnknownWindow";
        var type = element.GetType().Name;
        var name = string.IsNullOrWhiteSpace(element.Name) ? "" : $"#{element.Name}";
        var text = TryGetElementText(element);
        return string.IsNullOrWhiteSpace(text)
            ? $"{window}.{type}{name}"
            : $"{window}.{type}{name} \"{text}\"";
    }

    private static string TryGetElementText(object element)
    {
        return element switch
        {
            ButtonBase { Content: string s } => s,
            ButtonBase { Content: TextBlock tb } => tb.Text,
            HeaderedContentControl { Header: string s } => s,
            TextBox tb when !string.IsNullOrWhiteSpace(tb.Name) => tb.Name,
            _ => ""
        };
    }

    private static string DescribeSelectedValue(Selector selector)
    {
        return selector.SelectedItem switch
        {
            null => "",
            string s => $" -> \"{s}\"",
            ComboBoxItem { Content: string s } => $" -> \"{s}\"",
            FrameworkElement fe when !string.IsNullOrWhiteSpace(fe.Name) => $" -> {fe.GetType().Name}#{fe.Name}",
            // 匿名类型（例如 ComboBox 里装 new { Label, Version } 的选项）没有有意义的 ToString，
            // 默认反射显示它的真实属性名，比 GetType().Name 那种 <>f__AnonymousType 有用得多。
            _ => $" -> {DescribeAnonymousType(selector.SelectedItem)}"
        };
    }

    /// <summary>反射列出匿名类型的属性，形如 { Label = xxx, Version = ... }；普通类型则显示类型名。</summary>
    private static string DescribeAnonymousType(object item)
    {
        var t = item.GetType();
        if (!IsAnonymousType(t))
            return t.Name;

        var props = t.GetProperties();
        if (props.Length == 0) return t.Name;

        var parts = props.Select(p =>
        {
            try
            {
                var v = p.GetValue(item);
                return $"{p.Name} = {DescribeValue(v)}";
            }
            catch { return $"{p.Name} = <获取失败>"; }
        });
        return $"{{ {string.Join(", ", parts)} }}";
    }

    private static string DescribeValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            Selector sel => DescribeSelectedValue(sel),
            _ => value.ToString() ?? ""
        };
    }

    private static bool IsAnonymousType(Type t)
    {
        // 编译器生成的匿名类型：C# 是 <>f__AnonymousType...，VB 是 VB$AnonymousType...
        return t.IsGenericType
            && (t.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                || t.Name.StartsWith("VB$AnonymousType", StringComparison.Ordinal));
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