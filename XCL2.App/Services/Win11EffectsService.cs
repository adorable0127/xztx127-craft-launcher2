using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// "Windows 11 新光效设计"开关背后的实现。默认关闭（见 AppConfig.EnableWin11VisualEffects），
/// 用户在设置页手动开启后才会生效，且只在真正的 Windows 11（22H2/build 22621 起）上有实际
/// 视觉效果——DWM 相关属性编号在更老的系统上会调用失败，这里跟 WindowChromeService 处理
/// DWMWA_USE_IMMERSIVE_DARK_MODE 一样，静默忽略失败的返回值，不弹错误、不影响正常使用，
/// 老系统上开关虽然打得开，但界面观感跟关闭时几乎没有区别。
///
/// 做了两件事：
/// 1. 云母(Mica)/亚克力(Acrylic)背景材质——主窗口用 Mica（跟资源管理器、设置应用同款，
///    贴合桌面壁纸做柔和渐变），其余弹窗用 Acrylic（更适合"临时浮层"的磨砂质感，
///    跟主窗口区分开，视觉上能看出主次）。
/// 2. 窗口圆角——Win11 系统级窗口圆角，开启后跟系统原生窗口（资源管理器等）保持一致的
///    视觉语言；关闭时退回 Win10 时代的直角窗口（DWMWCP_DEFAULT，交给系统按当前系统版本
///    自行决定，Win10 上本来就是直角，不会有任何变化）。
///
/// 跟 ThemeService.CurrentIsDarkMode 一样，用一个静态字段记录"当前应该开启还是关闭"，
/// 而不是每次都从配置文件现读——App.xaml.cs 里给所有 Window 统一注册的 Loaded 类处理器
/// 需要一个不依赖具体窗口实例、随时能查询的"当前状态"，设置页保存设置时调用 SetEnabled
/// 更新这个状态，新开的窗口、已经打开的窗口都会分别通过类处理器/ApplyToAllOpenWindows
/// 跟着同步。
/// </summary>
public static class Win11EffectsService
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    // DWMWA_WINDOW_CORNER_PREFERENCE，Windows 11 (build 22000) 起支持。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_ROUND = 2;

    // DWMWA_SYSTEMBACKDROP_TYPE，Windows 11 22H2 (build 22621) 起支持。
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_AUTO = 0;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica：贴合桌面壁纸的柔和渐变，主窗口默认用这个
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic：真正的"毛玻璃"磨砂模糊，透感更强、颗粒更细
    private const int DWMSBT_TABBEDWINDOW = 4;    // Mica Alt：云母的加深/加强版，层次感比标准 Mica 更明显

    /// <summary>背景材质可选项，对应设置页新增的"背景材质"下拉框。数值等于对应的
    /// DWMSBT_* 常量，方便直接强转传给 DwmSetWindowAttribute，不需要额外做一次映射。</summary>
    public enum BackdropMaterial
    {
        Mica = DWMSBT_MAINWINDOW,
        MicaAlt = DWMSBT_TABBEDWINDOW,
        Acrylic = DWMSBT_TRANSIENTWINDOW,
    }

    /// <summary>当前是否应该给（新开/已打开的）窗口套用 Win11 视觉效果，由设置页保存时通过
    /// SetEnabled 更新。默认 false，跟 AppConfig.EnableWin11VisualEffects 的默认值一致。</summary>
    public static bool CurrentEnabled { get; private set; }

    /// <summary>主窗口应该用哪种背景材质，由设置页的"背景材质"下拉框决定，默认 Mica
    /// （跟以前的固定行为保持一致，老配置文件里没有这一项时走这个默认值）。
    /// 弹窗类临时浮层固定用 Acrylic，不受这个选项影响——磨砂质感本来就更适合浮层，
    /// 而且这样主窗口和弹窗永远能一眼区分开，不会因为都选了同一种材质而混在一起。</summary>
    public static BackdropMaterial CurrentMainWindowMaterial { get; private set; } = BackdropMaterial.Mica;

    /// <summary>
    /// 更新"当前是否启用"的状态并立即应用到所有已打开的窗口。App.xaml.cs 启动时用早期读取
    /// 的配置调一次（此时通常还没有任何窗口打开，只是把状态记下来，供后面陆续创建的窗口的
    /// Loaded 类处理器读取）；设置页 Save_Click 里用户改动开关后再调一次，让已经打开的窗口
    /// 立即看到效果，不需要重启。
    /// </summary>
    public static void SetEnabled(bool enabled, BackdropMaterial mainWindowMaterial = BackdropMaterial.Mica)
    {
        CurrentEnabled = enabled;
        CurrentMainWindowMaterial = mainWindowMaterial;
        foreach (Window window in Application.Current.Windows)
        {
            Apply(window, enabled);
        }
    }

    /// <summary>
    /// 真正修复"开启云母/亚克力立刻白屏，重启启动器也没用"：问题根本不在 WPF 这边的
    /// Measure/Arrange/Render 时序（之前这里用 UpdateLayout + Dispatcher.Invoke(Render) 修过一版，
    /// 实测没用，因为那一套只能强制 WPF 自己的逻辑布局/呈现流程，而 DwmExtendFrameIntoClientArea
    /// 生效之后，DWM 会给这块窗口重新分配一块合成用的重定向表面(redirection surface)——这块新表面
    /// 初始内容是空的（合成器意义上的"白"/透明底），必须等窗口产生一次真正的 WM_SIZE 消息，
    /// DWM 才会重新把 WPF 呈现出来的内容跟云母/亚克力材质合成进这块表面；不产生 WM_SIZE 的话，
    /// 不管 WPF 内部再怎么强制刷新，画面永远停在 DWM 分配新表面那一刻的空白状态——这就是"重启
    /// 启动器也没用"的原因：每次启动只要一开启特效就会立刻踩中同一个坑，跟重不重启无关。
    /// 用 SetWindowPos 在原地把窗口尺寸改一次再改回去（SWP_FRAMECHANGED 强制连非客户区一起
    /// 重算），人为触发一次真正的 WM_SIZE，逼 DWM 把新表面跟最新画面重新合成一遍，白屏就消失了。
    /// 只需要在"已经 Show 过、已有实际尺寸"的窗口上做；还没显示过的窗口走正常首帧流程即可，
    /// 不会有这个问题（还没被 DwmExtendFrameIntoClientArea 分配过旧表面，没有"陈旧空白表面"要刷）。
    /// </summary>
    private static void ForceDwmResurface(IntPtr hwnd)
    {
        // 修复"窗口一直抽搐抖动"：这里跟 ThemeService.ApplyNativeGlobalOpacity 里的
        // ForceLayeredResurface 是同一类"原地缩放1像素逼 DWM 重合成"手法，都会对同一个 hwnd
        // 调 SetWindowPos。当用户同时开启了 Win11 视觉效果 和 整窗全局透明（正是默认演示配置
        // 里的组合）时，两边几乎同时各自触发一轮，SetWindowPos 交错调用 + 各自引发的
        // WM_STYLECHANGED/WM_DWMCOMPOSITIONCHANGED 又互相唤醒对方，肉眼看就是窗口反复抖动。
        // 用 ThemeService 暴露的忙碌闸门保证同一时刻只有一边在改尺寸，另一边直接跳过——
        // 跳过是安全的，因为很快会有下一次机会（比如面板刷新时）重新尝试。
        if (!ThemeService.TryEnterResurface(hwnd)) return;
        try
        {
            if (!GetWindowRect(hwnd, out var rect)) return;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            const uint flags = SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED;
            // 高度 +1 再 -1：任何一次真实尺寸变化都会触发 WM_SIZE，幅度只要非零即可，
            // 1 像素的抖动用户完全看不出来，但足够让 DWM 重新生成/合成重定向表面。
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height + 1, flags);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, flags);
        }
        finally { ThemeService.ExitResurface(hwnd); }
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// 给单个窗口套用（或撤销）背景材质 + 圆角。主窗口(MainWindow)用 CurrentMainWindowMaterial
    /// 里选的材质（Mica / Mica Alt / 亚克力毛玻璃三选一），其它窗口（各种设置/详情弹窗）
    /// 固定用 Acrylic，跟主窗口区分开，视觉上能看出主次。
    /// 句柄还没创建（窗口还没 Show 过）时直接跳过，交给 App.xaml.cs 里统一注册的
    /// Loaded 类处理器在窗口真正显示时再调一次，用法完全比照 WindowChromeService.ApplyTitleBarTheme。
    ///
    /// 之前只调了 DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)，界面上完全看不出效果——
    /// 这个属性只是告诉 DWM"这扇窗口想要什么材质"，但 WPF 窗口默认把整个客户区都当成
    /// 自己负责绘制的不透明区域（Window.Background 用的是不透明的 SideBrush），DWM 的合成
    /// 材质只会显示在"客户区里没有被应用程序绘制内容覆盖"的地方——也就是必须先用
    /// DwmExtendFrameIntoClientArea 把"玻璃"区域从窗口边框（默认只有几像素）扩展到整个
    /// 客户区（传全 -1 的 MARGINS，即 Win11 官方文档里 Mica/Acrylic 例子的标准写法），
    /// DWM 才会真的把背景材质合成到这块区域里，WPF 画的内容再叠加在材质上面。
    /// 没有这一步，不管背景材质选哪个、参数传得再对，用户能看到的都只是普通不透明窗口。
    /// </summary>
    public static void Apply(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var corner = enabled ? DWMWCP_ROUND : DWMWCP_DEFAULT;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var isMainWindow = window is MainWindow;
        var backdrop = enabled
            ? (isMainWindow ? (int)CurrentMainWindowMaterial : DWMSBT_TRANSIENTWINDOW)
            : DWMSBT_AUTO;
        var backdropResult = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        // Win10 / Win11 早期版本不支持 DWMWA_SYSTEMBACKDROP_TYPE。只有 DWM 明确返回成功时
        // 才能把 WPF 顶层背景改成透明；否则会把本来正常的 SideBrush 清掉，旧系统上反而变成
        // 黑底/异常透明。关闭特效时无论属性是否受支持都恢复正常 WPF 背景。
        var backdropApplied = enabled && backdropResult == 0;

        // 把"玻璃"区域扩展到整个客户区（关闭时传全 0 margins 收回，交还给窗口自己绘制）。
        // 注意：这一步不需要 AllowsTransparency="True"——DwmExtendFrameIntoClientArea 走的是
        // DWM 合成层，跟 WPF 自己的逐像素透明窗口（AllowsTransparency）是两套独立机制，
        // 两者同时开启反而会互相冲突/性能变差，所以项目里所有窗口都保持 AllowsTransparency="False"。
        var margins = backdropApplied
            ? new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 }
            : new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 0 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // 仅设置 DWMWA_SYSTEMBACKDROP_TYPE / 扩展 DWM frame 还不够：WPF 的顶层 Hwnd 默认会用
        // Window.Background 把客户区整张重定向表面先涂成不透明色，DWM 的 Mica/Acrylic 实际
        // 已经在下面生成了，却被这层 WPF 底色完全盖住，因此“下拉框选了 Acrylic 但背景没同步”。
        // MainWindow 使用自绘标题栏，最底层 Grid 本身没有 Background，安全做法是：开启 DWM
        // 材质时让顶层 Window 背景与 HwndTarget 背景透明，让材质成为真正底板；侧栏/卡片仍由
        // SideBrush/PanelBrush 绘制，并由“窗口透明度”控制其 alpha。关闭时恢复 SideBrush。
        if (window is MainWindow)
        {
            var source = HwndSource.FromHwnd(hwnd);
            if (backdropApplied)
            {
                window.Background = Brushes.Transparent;
                if (source?.CompositionTarget != null)
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            else
            {
                window.SetResourceReference(Control.BackgroundProperty, "SideBrush");
                if (source?.CompositionTarget != null)
                {
                    var targetColor = window.TryFindResource("SideBrush") is SolidColorBrush side
                        ? side.Color : Colors.Black;
                    // HwndTarget 的兜底底色保持不透明；面板/自定义底图自己的 alpha 仍由 WPF
                    // 内容树处理，避免关闭 DWM 材质后意外把桌面透进来。
                    targetColor.A = 255;
                    source.CompositionTarget.BackgroundColor = targetColor;
                }
            }
        }

        // 用户自定义底图是 WPF 内容树内的一层，效果强度主要由"面板透明度"滑块决定（见
        // MainWindow.RefreshCustomBackgroundVisualEffect 注释），跟 DWM 材质无关、不依赖
        // Windows 11；这里切换材质后仍然调用一次，只是为了让"亚克力"材质带来的那一点点
        // 额外强度加成同步生效，不是让效果"从无到有"的必要条件。
        if (window is MainWindow mainWindow)
            mainWindow.RefreshCustomBackgroundVisualEffect();

        // 开启特效、且窗口已经真正显示过（有实际尺寸）时，补一次强制重合成，
        // 见 ForceDwmResurface 注释——这是修复"立刻白屏"的关键一步，缺了这步单靠上面
        // 两个 DWM 调用不会自动出现画面。窗口第一次 Show 的过程中也会经过这里
        // （App.xaml.cs 里 Loaded 类处理器），所以启动时开着特效同样会走到这一步，
        // 不需要用户手动拖动/切换窗口大小才能看到内容。
        if (backdropApplied)
        {
            window.Dispatcher.InvokeAsync(() => ForceDwmResurface(hwnd),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
