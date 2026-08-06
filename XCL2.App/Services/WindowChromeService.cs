using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XCL2.App.Services;

/// <summary>
/// 窗口"外壳"相关的两个独立功能，放在一起是因为都要用到 Win32 互操作、都是"跟单个
/// Window 实例打交道"而不是纯资源字典层面的东西，跟 ThemeService（管画刷颜色）、
/// LocalizationService（管文案）职责上是平行关系：
///
/// 1. 标题栏跟随深浅色模式（修复"顶部白条"）——
///    这个项目里所有 Window 都是标准 WPF 窗口，没有自定义 WindowChrome/去掉系统边框
///    （这是有意的：完全自绘标题栏意味着要给每一个窗口重新实现拖动/双击最大化/贴边
///    半屏/Aero Snap 等一整套系统级窗口行为，工作量和维护成本都远超"白条"这一个问题
///    本身）。真正的根因是：Windows 10 1809+/Windows 11 上，系统标题栏默认按"浅色"
///    绘制，跟应用深色模式下的内容区完全不搭——这不是画出来的一条"条子"，是系统原生
///    标题栏本身的颜色，之前项目里没有调用任何 DWM API 去声明"这个窗口是深色内容"，
///    所以系统一直按默认浅色绘制它。
///    解决方式是调用 DwmSetWindowAttribute 的 DWMWA_USE_IMMERSIVE_DARK_MODE 属性
///    （Windows 10 20H1/Build 19041 起支持，此前的旧 attribute 编号 19 在更早的
///    Insider 预览版用过，这里两个编号都尝试设置一遍，兼容极少数老旧 Windows 10
///    版本），让系统按暗色主题绘制这个窗口的原生标题栏（背景色+文字颜色都会变），
///    从而跟下面深色的内容区融为一体，而不是维护自己单独绘制标题栏。
///    应用时机分两处：① App.xaml.cs 的 OnStartup 里用 EventManager.RegisterClassHandler
///    在 Window 类型这一级统一注册，项目里所有 Window 子类（MainWindow + 20 多个弹窗）
///    第一次显示时都会自动应用一次，不需要逐个窗口文件接线；② ThemeService 每次切换
///    深浅色模式时，同一批"遍历所有已打开窗口"的刷新逻辑里一并调用 ApplyTitleBarTheme，
///    保证已经打开的窗口标题栏能实时跟着切换，不需要关闭重开。
///
/// 2. 主窗口 F11 全屏切换——
///    只对 MainWindow 生效（其余都是模态弹窗，全屏没有意义）。做法是把 WindowStyle
///    切到 None、WindowState 切到 Maximized，同时记下切之前的 WindowStyle/
///    WindowState/尺寸位置，再按 F11 时原样恢复——不用 System.Windows.Forms 的
///    Screen 类（那是 WinForms 程序集，WPF 项目引入它只为拿屏幕尺寸没必要），
///    Maximized 状态下 WPF 自己就会把窗口撑满当前显示器工作区，去掉 WindowStyle
///    (捕获前先设 None）之后就不会露出系统标题栏/边框，视觉上就是完整全屏。
/// </summary>
public static class WindowChromeService
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // 早期 Windows 10 Insider build 用的编号

    /// <summary>
    /// 每次 ThemeService.Apply 切换深浅色模式时，同一批"遍历所有已打开窗口"的刷新逻辑里
    /// 一并调用这个方法，保证标题栏跟着实时切换，不需要重新打开窗口。
    ///
    /// 窗口首次创建时的标题栏应用不需要单独接线：App.xaml.cs 的 OnStartup 里用
    /// EventManager.RegisterClassHandler 在 Window 类型这一级统一注册了 SourceInitialized
    /// 处理器，项目里所有 Window 子类（MainWindow + 20 多个弹窗）都会自动应用一次，
    /// 新增窗口类也不需要额外接入这个逻辑。
    /// </summary>
    public static void ApplyTitleBarTheme(Window window, bool isDark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return; // 句柄还没创建（窗口还没 Show 过），交给 App.xaml.cs 的 SourceInitialized 处理器再调一次

        var useDark = isDark ? 1 : 0;
        // 两个 attribute 编号都尝试设置：新系统认新编号(20)、旧编号会返回失败但无副作用；
        // 极少数老 Windows 10 版本只认旧编号(19)。返回值不为 0 代表调用失败（比如运行在
        // Windows 7/8，系统压根没有这个 DWM attribute），静默忽略即可——非深色标题栏
        // 场景下窗口退化回系统默认外观，不影响功能，不弹错误打扰用户。
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
    }

    // ===== F11 全屏（仅 MainWindow 使用） =====

    private static WindowStyle _savedStyle;
    private static WindowState _savedState;
    private static ResizeMode _savedResizeMode;
    private static bool _isFullScreen;

    /// <summary>当前是否处于 F11 全屏状态，供 MainWindow 判断要不要在其它地方
    /// （比如 Esc 键、双击标题栏等）额外处理，目前只有 ToggleFullScreen 自己读写。</summary>
    public static bool IsFullScreen => _isFullScreen;

    /// <summary>
    /// 切换 MainWindow 的全屏状态。进全屏前保存 WindowStyle/WindowState/ResizeMode
    /// 三项（不只是 WindowState，因为全屏还需要去掉系统边框，光 Maximized 本身
    /// 窗口边框和标题栏依然存在），退出时原样恢复，不影响用户之前手动调整过的
    /// 正常窗口大小/位置（Normal 状态下的 Width/Height/Left/Top 全程没有被这里
    /// 动过，WindowState 在 Normal ⇄ Maximized 之间的记忆是 WPF 自带的行为）。
    /// </summary>
    public static void ToggleFullScreen(Window window)
    {
        if (!_isFullScreen)
        {
            _savedStyle = window.WindowStyle;
            _savedState = window.WindowState;
            _savedResizeMode = window.ResizeMode;

            // 顺序很重要：先去掉 ResizeMode/WindowStyle 系统边框，再设 Maximized，
            // 避免中间态短暂露出"半全屏带边框"的闪烁。
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStyle = WindowStyle.None;
            window.WindowState = WindowState.Maximized;
            _isFullScreen = true;
        }
        else
        {
            window.WindowStyle = _savedStyle;
            window.ResizeMode = _savedResizeMode;
            window.WindowState = _savedState;
            _isFullScreen = false;
        }
    }
}
