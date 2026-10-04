using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

internal static class ExternalGameDiscovery
{
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    private static readonly Dictionary<int, GameProcessInfo> Games = new();
    private static readonly object Gate = new();

    /// <summary>只读取游戏窗口和进程信息，不修改游戏设置、安装输入钩子或启用触屏。</summary>
    public static IReadOnlyList<GameProcessInfo> Discover(IReadOnlySet<int>? ownedPids = null)
    {
        lock (Gate)
        {
            foreach (var pair in Games.Where(p => p.Value.HasExited).ToArray())
            {
                Games.Remove(pair.Key);
                pair.Value.Process.Dispose();
            }
            EnumWindows((hwnd, _) =>
            {
                Process? process = null;
                try
                {
                    // 游戏原生窗口，与启动器的 Java Swing / AWT 窗口区分。
                    if (TouchGameWindow.Detect(hwnd) == TouchGameBackend.Unknown) return true;
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == 0 || pid == Environment.ProcessId || ownedPids?.Contains((int)pid) == true) return true;
                    if (Games.TryGetValue((int)pid, out var existing))
                    {
                        if (IsWindowVisible(hwnd) || existing.TouchWindowHandle == IntPtr.Zero)
                            existing.TouchWindowHandle = hwnd;
                        return true;
                    }
                    process = Process.GetProcessById((int)pid);
                    var title = new StringBuilder(1024);
                    GetWindowText(hwnd, title, title.Capacity);
                    var name = process.ProcessName;
                    if (!name.Equals("java", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("javaw", StringComparison.OrdinalIgnoreCase) &&
                        !title.ToString().Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) return true;
                    process.EnableRaisingEvents = true;
                    var info = new GameProcessInfo(process,
                        title.Length > 0 ? title.ToString() : "Minecraft（外部启动）", "其他启动器", "")
                    { TouchWindowHandle = hwnd, IsExternal = true };
                    Games.Add((int)pid, info);
                    process = null; // 缓存持有进程，悬浮层关闭面板后仍可跟踪退出。
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
                {
                    // 枚举期间退出或无权访问的窗口跳过，不中断其他实例。
                }
                finally { process?.Dispose(); }
                return true;
            }, IntPtr.Zero);
            return Games.Values.Where(p => !p.HasExited).ToArray();
        }
    }
}
