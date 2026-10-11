using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 协调 DWM 背景材质、圆角与整窗 alpha。逐像素透明浮层不参与全局处理。
/// 系统背景材质只在支持的 Windows 11 版本生效；普通窗口透明度由 ThemeService 原生 alpha 管理。
/// </summary>
public static class Win11EffectsService
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    // NONE 才是明确关闭；AUTO 会让 DWM 根据窗口自行决定是否加背景，不能用作“关闭”。
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMSBT_TABBEDWINDOW = 4;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_FRAME = 0x0400;

    public enum BackdropMaterial
    {
        Mica = DWMSBT_MAINWINDOW,
        MicaAlt = DWMSBT_TABBEDWINDOW,
        Acrylic = DWMSBT_TRANSIENTWINDOW,
    }

    public static bool CurrentEnabled { get; private set; }
    public static BackdropMaterial CurrentMainWindowMaterial { get; private set; } = BackdropMaterial.Mica;
    public static bool CurrentWinUi3Enabled { get; private set; }

    private sealed class WindowEffectState
    {
        public bool HasApplied;
        public int Corner;
        public int Backdrop;
        public bool BackdropApplied;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, WindowEffectState> WindowStates = new();

    public static void SetWinUi3Enabled(bool enabled)
    {
        if (CurrentWinUi3Enabled == enabled) return;
        CurrentWinUi3Enabled = enabled;
        ApplyToOpenWindows();
    }

    public static void SetEnabled(bool enabled, BackdropMaterial mainWindowMaterial = BackdropMaterial.Mica)
    {
        CurrentEnabled = enabled;
        CurrentMainWindowMaterial = Enum.IsDefined(mainWindowMaterial) ? mainWindowMaterial : BackdropMaterial.Mica;
        ApplyToOpenWindows();
    }

    private static void ApplyToOpenWindows()
    {
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows) Apply(window, CurrentEnabled);
    }

    internal static void InvalidateWindowState(Window window)
    {
        if (WindowStates.TryGetValue(window, out var state)) state.HasApplied = false;
    }

    /// <summary>
    /// 整窗透明使用 layered alpha；它与系统 backdrop 的合成路径不同。透明生效期间暂停
    /// 背景材质、保留用户选择，关闭透明或调到 100% 后自动恢复。不通过改变尺寸“强制刷新”。
    /// </summary>
    public static void Apply(Window window, bool enabled)
    {
        if (WindowTreatmentPolicy.IsExempt(window)) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var state = WindowStates.GetValue(window, _ => new WindowEffectState());
        var transparent = ThemeService.CurrentGlobalTransparencyEnabled && ThemeService.CurrentGlobalOpacityPercent < 100;
        var corner = enabled || CurrentWinUi3Enabled ? DWMWCP_ROUND : DWMWCP_DEFAULT;
        var backdrop = enabled && !transparent
            ? (window is MainWindow ? (int)CurrentMainWindowMaterial : DWMSBT_TRANSIENTWINDOW)
            : DWMSBT_NONE;
        var nativeChanged = !state.HasApplied || state.Corner != corner || state.Backdrop != backdrop;
        if (nativeChanged)
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            var result = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            var requestedBackdrop = backdrop != DWMSBT_NONE && result == 0;
            var margins = requestedBackdrop
                ? new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 }
                : new MARGINS();
            var frameResult = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            state.BackdropApplied = requestedBackdrop && frameResult == 0;
            if (requestedBackdrop && !state.BackdropApplied)
            {
                // 扩展 frame 失败时也撤销 backdrop，保留 WPF 正常底色。
                var none = DWMSBT_NONE;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            }
            state.Corner = corner;
            state.Backdrop = backdrop;
            state.HasApplied = true;
        }

        if (window is MainWindow mainWindow)
        {
            var target = HwndSource.FromHwnd(hwnd)?.CompositionTarget;
            if (state.BackdropApplied)
            {
                // 让 DWM 材质露出来；上层侧栏、卡片继续使用主题画刷。
                if (window.Background != Brushes.Transparent) window.Background = Brushes.Transparent;
                if (target != null && target.BackgroundColor != Colors.Transparent)
                    target.BackgroundColor = Colors.Transparent;
            }
            else
            {
                // 整窗 alpha 有一个可靠的不透明 WPF 底板，避免把透明面板混到未定义的黑底上。
                var sideBrush = window.TryFindResource("SideBrush") as SolidColorBrush;
                if (!ReferenceEquals(window.Background, sideBrush))
                    // 同一材质状态下保留已有动态引用，避免每次 WM_SIZE/激活都重建表达式。
                if (nativeChanged || window.Background == Brushes.Transparent)
                    window.SetResourceReference(Control.BackgroundProperty, "SideBrush");
                if (target != null)
                {
                    var color = sideBrush?.Color ?? Colors.Black;
                    color.A = 255;
                    if (target.BackgroundColor != color) target.BackgroundColor = color;
                }
            }
            if (nativeChanged) mainWindow.RefreshCustomBackgroundVisualEffect();
        }

        if (nativeChanged)
        {
            window.InvalidateVisual();
            // 正常重绘即可；+1/-1 尺寸操作会打断最小化/恢复动画并额外触发两次布局。
            _ = RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
        }
    }
}
