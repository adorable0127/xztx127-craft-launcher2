using System.Runtime.InteropServices;
using System.Text;

namespace XCL2.App.Services;

internal enum TouchGameBackend { Unknown, Glfw, Sdl, Lwjgl2 }

/// <summary>按 PID 和原生窗口类识别后端，不依赖版本号、窗口标题或 Java 的启动窗口。</summary>
internal static class TouchGameWindow
{
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    public static TouchGameBackend Detect(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        if (GetClassName(hwnd, name, name.Capacity) == 0) return TouchGameBackend.Unknown;
        var value = name.ToString();
        if (value.StartsWith("GLFW", StringComparison.OrdinalIgnoreCase)) return TouchGameBackend.Glfw;
        if (value.StartsWith("SDL", StringComparison.OrdinalIgnoreCase)) return TouchGameBackend.Sdl;
        if (value.StartsWith("LWJGL", StringComparison.OrdinalIgnoreCase)) return TouchGameBackend.Lwjgl2;
        return TouchGameBackend.Unknown;
    }

    public static bool BelongsTo(IntPtr hwnd, int pid)
    {
        GetWindowThreadProcessId(hwnd, out var owner);
        return hwnd != IntPtr.Zero && pid > 0 && owner == (uint)pid;
    }

    public static IntPtr Find(int pid, IntPtr preferred = default)
    {
        if (pid <= 0) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero;
        long bestScore = -1;
        var foreground = GetForegroundWindow();
        EnumWindows((hwnd, _) =>
        {
            if (!BelongsTo(hwnd, pid) || !IsWindowVisible(hwnd) || Detect(hwnd) == TouchGameBackend.Unknown)
                return true;
            if (!TouchInputInjector.TryGetClientAreaOnScreen(hwnd, out var l, out var t, out var r, out var b)
                || r <= l || b <= t) return true;
            long score = (long)(r - l) * (b - t);
            if (hwnd == preferred) score += 1L << 50;
            if (hwnd == foreground) score += 1L << 55;
            if (score > bestScore) { best = hwnd; bestScore = score; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
