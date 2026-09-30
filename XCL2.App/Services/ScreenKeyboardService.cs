using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace XCL2.App.Services;

internal static class ScreenKeyboardService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);

    public static void Show()
    {
        // OSK 适用于没有文本框焦点的 OpenGL/GLFW 游戏；不会像 TabTip 一样仅启动后台进程。
        var existing = FindWindow("OSKMainClass", null);
        if (existing != IntPtr.Zero)
        {
            if (!IsWindowVisible(existing) || IsIconic(existing)) ShowWindow(existing, 4); // SW_SHOWNOACTIVATE
            return;
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var directory = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        Process.Start(new ProcessStartInfo(Path.Combine(windows, directory, "osk.exe")) { UseShellExecute = true });
    }
}
