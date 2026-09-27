using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

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

    // ===== 自绘标题栏最大化时"盖住任务栏" 的修复 =====
    //
    // 用 shell:WindowChrome 本该会自动处理这件事（它接管 WM_GETMINMAXINFO，最大化时
    // 按当前显示器工作区计算尺寸），但 WindowChrome 内部这段计算在 Per-Monitor-V2
    // DPI 感知（.NET Core/.NET 5+ 项目默认就是这个模式，见 csproj/app.manifest）下有个
    // 长期存在、微软没修的已知 bug：某些 DPI 缩放比例/多屏组合下算出来的
    // ptMaxSize/ptMaxPosition 是"物理屏幕"而不是"工作区"，结果就是最大化后窗口边缘
    // 盖住任务栏。不是我们的 XAML 配置有问题，是 WindowChrome 这个类本身这段逻辑不可靠。
    //
    // 解决方式是不依赖 WindowChrome 自己算，改成我们自己在 WM_GETMINMAXINFO 消息里
    // 用 Win32 API（MonitorFromWindow + GetMonitorInfo）现查当前窗口所在显示器的工作区
    // （rcWork，天然已经排除任务栏），直接把 MINMAXINFO 结构体里的
    // ptMaxPosition/ptMaxSize 改写成这个工作区的坐标——这是 WPF 自定义标题栏窗口
    // 遇到这个 bug 时的标准/通用解法（MahApps.Metro、MaterialDesignInXAML 等主流
    // WPF 无边框窗口库内部都是同一套做法），比等 WindowChrome 自己修更可靠。
    //
    // 调用时机：Window.SourceInitialized 时挂钩子（这时 HWND 才真正创建出来，
    // 之前挂钩子拿不到 Handle）。只对 MainWindow 调用一次即可，弹窗仍是系统标题栏，
    // 用不到这个修复。

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    /// <summary>给自绘标题栏窗口（目前只有 MainWindow）接上"最大化尊重任务栏"的修复钩子。
    /// 在窗口构造函数里、SourceInitialized 事件触发时调用一次即可，此后每次用户最大化
    /// 窗口（点按钮/双击标题栏/拖到屏幕顶部）都会自动生效，不需要每次手动调。</summary>
    public static void EnableWorkAreaAwareMaximize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return; // 必须在 SourceInitialized 之后调用，这里兜底一下避免空句柄崩溃

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook((IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // 我们自己把 WM_GETMINMAXINFO 标记为 handled 后，WPF / WindowChrome 后续默认处理
            // 就不会再有机会把 Window.MinWidth / MinHeight 写进 ptMinTrackSize。此前 MainWindow
            // XAML 虽然声明了最小尺寸，实际上仍能被原生拖拽缩到更小，标题栏右侧按钮就会被
            // 挤出/裁掉，看起来像“离奇消失”。这里显式把 WPF 的 DIP 最小尺寸换算成当前显示器
            // 的物理像素并写回原生结构，确保 854x480 在 100%/125%/150% 等 DPI 下都真正生效。
            var dpi = VisualTreeHelper.GetDpi(window);
            if (!double.IsNaN(window.MinWidth) && !double.IsInfinity(window.MinWidth) && window.MinWidth > 0)
                mmi.ptMinTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX));
            if (!double.IsNaN(window.MinHeight) && !double.IsInfinity(window.MinHeight) && window.MinHeight > 0)
                mmi.ptMinTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY));

            var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    // ptMaxPosition/ptMaxSize 要用"相对显示器左上角"的偏移，而不是屏幕
                    // 绝对坐标——多屏且非主屏在左/上方向有负坐标时，直接抄绝对坐标会导致
                    // 窗口最大化后跑到别的显示器上，这里统一减去 rcMonitor 的偏移量。
                    mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                    mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                }
            }

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        });
    }

    // ===== Win11 贴靠布局（Snap Layout）悬停菜单 =====
    //
    // 只对自绘标题栏的最大化按钮生效（目前只有 MainWindow 那一个）。原生系统标题栏的
    // 最大化按钮天然有这个悬停菜单，全靠自绘之后就没了；要拿回来，唯一官方支持的办法是
    // 在 WM_NCHITTEST 里把鼠标扫到这个按钮矩形范围内时如实上报 HTMAXBUTTON——Windows 11
    // 外壳自己会在检测到"鼠标悬停在 HTMAXBUTTON 上"时弹出贴靠布局九宫格，这部分逻辑在
    // 系统里，这边不用也不能自己画。声明了 HTMAXBUTTON 之后，鼠标在这块矩形上的按下/
    // 抬起就变成非客户区消息（WM_NCLBUTTONDOWN/UP），不会再走 Button 的 Click 事件，
    // 所以点击切换最大化/还原改成在这里手动调用传进来的 toggleMaximizeRestore；悬停高亮
    // 同理也要手动摸模板里的 "bd" Border 来切换背景色（IsMouseOver 触发器不会再自己生效，
    // 因为这块区域对 WPF 来说已经不算"鼠标进入了这个控件"）。
    //
    // 懒加载：只在 Windows 11（Build ≥ 22000，贴靠布局悬停菜单最早随 Win11 一起出现）
    // 才挂这个钩子，Windows 10 及更早版本一次都不会调用下面的 Win32 互操作，跟原来
    // 完全没有这个功能时开销一样。

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int HTMAXBUTTON = 9;

    /// <summary>给自绘标题栏的最大化按钮接上 Win11 贴靠布局悬停菜单支持。只在
    /// Windows 11 上生效（懒加载：非 Win11 直接原样返回，不挂任何钩子）； SourceInitialized
    /// 时 HWND 才真正创建，必须放在这个事件里而不是构造函数本体直接调用。
    /// <paramref name="maximizeButton"/> 只用于计算按钮在窗口里的矩形范围（悬停命中测试），
    /// <paramref name="toggleMaximizeRestore"/> 是点击后真正切换最大化/还原状态的回调——
    /// 一旦声明了 HTMAXBUTTON，这块区域的点击就不会再触发 Button 的 Click 事件了，
    /// 必须由这里接管并手动调用。</summary>
    public static void EnableSnapLayoutForMaximizeButton(Window window, FrameworkElement maximizeButton, Action toggleMaximizeRestore)
    {
        if (Environment.OSVersion.Version.Build < 22000) return; // 懒加载：Win10 及更早版本直接跳过

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(hwnd);
        if (source == null) return;

        Border? hoverBorder = null; // 模板里名为 "bd" 的部件，首次用到时才去找，找不到就放弃高亮但不影响点击/贴靠
        Brush? normalBrush = null;
        var hovering = false;

        void SetHover(bool over)
        {
            if (hovering == over) return;
            hovering = over;
            if (maximizeButton is not Button btn) return;
            if (hoverBorder == null)
            {
                btn.ApplyTemplate();
                hoverBorder = btn.Template?.FindName("bd", btn) as Border;
                if (hoverBorder == null) return;
                normalBrush = hoverBorder.Background;
            }
            hoverBorder.Background = over ? (Brush)btn.FindResource("GlowSoftBrush") : normalBrush;
        }

        source.AddHook((IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                {
                    // lParam 是屏幕物理像素坐标；先用 ScreenToClient 换算成客户区物理像素
                    // （这一步是精确的 DPI 无关操作），再按当前窗口 DPI 缩放成 WPF 的设备
                    // 无关单位，最后跟按钮在窗口里的矩形（TransformToAncestor 得到）比较。
                    var pt = new POINT { X = unchecked((short)(long)lParam), Y = unchecked((short)((long)lParam >> 16)) };
                    if (!ScreenToClient(hWnd, ref pt)) break;
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var clientPoint = new Point(pt.X / dpi.DpiScaleX, pt.Y / dpi.DpiScaleY);

                    var over = false;
                    if (maximizeButton.IsVisible && maximizeButton.Visibility == Visibility.Visible)
                    {
                        var rect = maximizeButton.TransformToAncestor(window)
                            .TransformBounds(new Rect(0, 0, maximizeButton.ActualWidth, maximizeButton.ActualHeight));
                        over = rect.Contains(clientPoint);
                    }

                    if (over)
                    {
                        SetHover(true);
                        handled = true;
                        return new IntPtr(HTMAXBUTTON);
                    }
                    SetHover(false);
                    break;
                }
                case WM_NCMOUSELEAVE:
                    SetHover(false);
                    break;
                case WM_NCLBUTTONDOWN:
                case WM_NCLBUTTONDBLCLK:
                    if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; return IntPtr.Zero; }
                    break;
                case WM_NCLBUTTONUP:
                    if (wParam.ToInt32() == HTMAXBUTTON)
                    {
                        handled = true;
                        toggleMaximizeRestore();
                        return IntPtr.Zero;
                    }
                    break;
            }
            return IntPtr.Zero;
        });
    }
}
