using System.Runtime.InteropServices;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>只读窗口响应状态；系统状态仅供展示，强制结束始终要求用户明确勾选。</summary>
internal static class GameWindowStatus
{
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);

    public static string ResponseLabel(GameProcessInfo info)
    {
        try
        {
            if (info.HasExited) return "已退出";
            var hwnd = TouchGameWindow.Find(info.Pid, info.TouchWindowHandle);
            if (hwnd == IntPtr.Zero) return "等待游戏窗口";
            return IsHungAppWindow(hwnd) ? "Windows 标记为未响应" : "窗口正在响应";
        }
        catch { return "响应状态未知"; }
    }
}
