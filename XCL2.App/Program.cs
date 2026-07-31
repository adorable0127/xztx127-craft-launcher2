using System.Runtime.InteropServices;
using System.Windows;

namespace XCL2.App;

/// <summary>
/// 自定义程序入口，取代 WPF SDK 默认自动生成的隐藏 Main()。
///
/// 需求："如果用户电脑上没有安装 .NET，就在启动的前面加上：你需要安装 .NET8 运行时
/// 才可以继续使用本程序。"
///
/// 这个项目是框架依赖发布(不是自包含部署)，意味着 .exe 本身要跑起来就需要机器上已经装了
/// 兼容的 .NET8 运行时——如果连一个 .NET Core/5+ 运行时都没装，操作系统层面的 apphost
/// 会在本方法执行前就弹出系统自带的对话框（这是 apphost 的原生行为，托管代码此时还没有
/// 机会执行，属于框架依赖部署模式的固有边界，无法从代码层面绕过；自包含部署可以规避，
/// 但会让安装包从几 MB 涨到大几十 MB，且绝大多数用户机器本来就有 .NET 运行时，不值得
/// 为这一种边缘情况牺牲所有用户的下载体积）。
///
/// 但还有一类更常见、能被托管代码捕获到的情况：机器上装了某个 .NET 运行时，但不是
/// 兼容 .NET8 桌面应用的版本（比如只装了 .NET 6/7，或者只装了不含 WindowsDesktop
/// 工作负载的 "仅运行时"版本）——这类情况下 apphost 能够找到"一个"运行时并把本方法
/// 启动起来，但 WPF 子系统实际初始化时会失败。这里在 new App() 真正加载 App.xaml
/// (进而触发 WPF 资源解析/窗口创建)之前，主动检测一次当前进程实际加载的运行时描述，
/// 检测到不是 .NET 8.x 就先用中文 MessageBox 提示，而不是让用户看到一堆看不懂的
/// TypeInitializationException 技术性异常堆栈。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!IsRunningOnNet8Desktop())
        {
            // 用系统默认的 MessageBox(不依赖 WPF 资源/样式，这一步必须能在 WPF 完全没初始化
            // 的情况下也能弹出)，中文提示 + 附带下载链接，比原生的英文技术异常对用户友好得多。
            MessageBox.Show(
                "你需要安装 .NET8 运行时才可以继续使用本程序。\n\n" +
                "检测到当前机器上的 .NET 运行时版本不是 8.x（或者装的是不含桌面支持的精简版），" +
                "无法正常启动 XCL2。\n\n" +
                "请前往微软官方页面下载安装「.NET Desktop Runtime 8.0」（Windows x64，" +
                "选择 \".NET Desktop Runtime\" 而不是 \"ASP.NET Core Runtime\"）后重新打开本程序：\n" +
                "https://dotnet.microsoft.com/download/dotnet/8.0",
                "缺少 .NET8 运行时", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }

        var app = new App();
        app.InitializeComponent();

        // 修复"切换页面后才会变黑/侧边栏和底部账户区一直是浅色"：WPF 的 Style 只在第一次
        // 真正被套用到控件上时才会 Seal（连带把它引用的画刷值一起冻结定型）。之前的做法是
        // 先 new MainWindow()（构造函数里 InitializeComponent() 一执行，侧边栏 SideNavButton
        // 等样式立刻用当前资源字典里的默认浅色 Seal 掉），再在构造函数内部调用
        // ThemeService.ApplyForCurrentState 试图"事后"把已经 Seal 的样式解封重上——这个
        // 事后补救对大多数元素有效，但对这个时间点上还没真正走完一次布局/渲染的部分控件
        // （典型就是嵌套在 ControlTemplate 里的子元素，比如 SideNavButton 内层的 Border）
        // 不完全可靠，导致侧边栏和底部账户信息区第一屏还是浅色，只有切一次页面后才补上
        // （因为切页面时对应的 Page/UserControl 是延迟构造的，那时候资源字典已经是深色了，
        // Seal 用的就是正确的颜色，不需要"补救"这一步）。
        //
        // 现在把"读配置 + 应用主题"整体提到 MainWindow 构造之前：这样 MainWindow.xaml 及其
        // 侧边栏在 InitializeComponent() 第一次执行、第一次 Seal 样式的那一刻，资源字典里
        // 已经是正确的皮肤颜色了，不再需要任何"事后刷新"，从第一帧画面开始就是对的。
        // 这里单独 new 一个 ConfigService 只是为了在窗口存在之前读一次持久化的皮肤/访客模式
        // 配置，跟 MainWindow 自己持有的 ConfigService 实例互不冲突（各自独立 Load 一次，
        // 读的是同一份 config.json，开销可忽略）。
        var earlyConfig = new Services.ConfigService();
        earlyConfig.Load();
        Services.ThemeService.ApplyForCurrentState(earlyConfig.Config.GuestModeEnabled, earlyConfig.Config.UiSkin, earlyConfig.Config.IsDarkMode);

        var exitCode = app.Run(new Views.MainWindow());
        return exitCode;
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
