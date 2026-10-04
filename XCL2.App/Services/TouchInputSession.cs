using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>后端分流只按实际窗口类；SDL 保留原上传版本的鼠标注入方式。</summary>
internal class TouchInputSession
{
    protected readonly IntPtr Window;
    public TouchGameBackend Backend { get; }
    public bool IsForeground => GetForegroundWindow() == Window;
    protected TouchInputSession(IntPtr window, TouchGameBackend backend) { Window = window; Backend = backend; }

    public static TouchInputSession Create(IntPtr window)
    {
        var backend = TouchGameWindow.Detect(window);
        return backend is TouchGameBackend.Glfw or TouchGameBackend.Lwjgl2
            ? new GlfwTouchInputSession(window, backend)
            : new TouchInputSession(window, backend);
    }

    /// <summary>版本分流已选择旧版时，禁止再按窗口类退回原始路径。</summary>
    public static TouchInputSession CreateCompatibility(IntPtr window)
        => new GlfwTouchInputSession(window, TouchGameWindow.Detect(window));

    public virtual void MoveLook(int dx, int dy) => TouchInputInjector.MouseMoveRelative(dx, dy);
    public bool UsesLegacyCompatibility => Backend is TouchGameBackend.Glfw or TouchGameBackend.Lwjgl2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO
    {
        public int Size, Flags;
        public IntPtr Cursor;
        public POINT Position;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RECT rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// 只观察游戏抓取，不调用 ClipCursor/ShowCursor/SetCapture。
    /// 菜单初始状态由调用方设为 false；触摸抑制或 API 失败时不给出“已进入游戏”的结论。
    /// 这是窗口级抓取信号，不是读取 Minecraft 内部世界对象。
    /// </summary>
    public bool? ReadGameplayCapture()
    {
        if (!IsForeground) return false;
        var info = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info)) return null;
        var hasClip = GetClipCursor(out var clip);
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        var desktop = hasClip && clip.Left == left && clip.Top == top &&
            clip.Right == left + GetSystemMetrics(78) && clip.Bottom == top + GetSystemMetrics(79);
        var confined = hasClip && TouchInputInjector.TryGetClientAreaOnScreen(Window, out var l, out var t, out var r, out var b)
            && clip.Right > clip.Left && clip.Bottom > clip.Top
            && clip.Left >= l && clip.Top >= t && clip.Right <= r && clip.Bottom <= b;
        if (confined && !desktop) return true;
        // Windows 的 SUPPRESSED 仅表示手指操作时隐藏箭头，不能视为游戏锁定。
        if ((info.Flags & 2) != 0) return info.Cursor != IntPtr.Zero ? false : null;
        if ((info.Flags & 1) != 0) return false;
        // GLFW disabled / SDL relative 模式正常隐藏光标；无 ClipCursor 的旧版也可使用。
        return true; // 未被系统触摸抑制、也未 SHOWING：游戏明确隐藏，兼容非空旧光标句柄。
    }
}

/// <summary>
/// GLFW/旧 LWJGL 专用相对鼠标路径。本次明确启用触屏后才会创建此路径；
/// 落到悬浮层的传统移动消息另由 GlfwMouseMessageRelay 转交目标窗口。
/// </summary>
internal sealed class GlfwTouchInputSession : TouchInputSession
{
    public GlfwTouchInputSession(IntPtr window, TouchGameBackend backend) : base(window, backend) { }
    public override void MoveLook(int dx, int dy)
    {
        if (IsForeground) TouchInputInjector.MouseMoveRelative(dx, dy, noCoalesce: true);
    }
}
