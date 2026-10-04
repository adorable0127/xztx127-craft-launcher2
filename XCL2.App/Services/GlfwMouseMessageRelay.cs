using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// GLFW 的传统鼠标路径读取 WM_MOUSEMOVE。SendInput 命中置顶悬浮层时，
/// 这些消息不再自然到达游戏，必须转交实际收到的客户区坐标（包括 GLFW 自己回中的移动）。
/// 不再额外合成一份位移，不用于 SDL，也不伪造 WM_INPUT。
/// </summary>
internal static class GlfwMouseMessageRelay
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    public static bool Forward(IntPtr overlay, IntPtr game, int message, IntPtr wParam, IntPtr lParam)
    {
        if (GetForegroundWindow() != game) return false;
        if (message == 0x0020) // WM_SETCURSOR：让游戏决定箭头/隐藏，WPF 不覆盖成自己的箭头。
        {
            // 仅在已确认的游戏内兼容路径调用；不是自行隐藏或抓取鼠标。
            SendMessageTimeout(game, 0x0020, game, lParam, 0x0003, 4, out _);
            return true;
        }
        var injected = unchecked((ulong)GetMessageExtraInfo().ToInt64()) == TouchInputInjector.InjectedSignature;
        // 移动含物理鼠标、我们注入的位移和游戏自己的回中；按钮只转发我们的事件，
        // 否则点击悬浮层按键会同时触发游戏攻击。
        var move = message == 0x0200;
        var button = message is 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208;
        var wheel = message is 0x020A or 0x020E;
        if (!move && !(injected && (button || wheel))) return false;
        var translated = lParam;
        if (!wheel)
        {
            var packed = lParam.ToInt64();
            var point = new POINT { X = unchecked((short)(packed & 0xffff)), Y = unchecked((short)((packed >> 16) & 0xffff)) };
            if (!ClientToScreen(overlay, ref point) || !ScreenToClient(game, ref point)) return false;
            translated = new IntPtr(unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16))));
        }
        // 异步投递，不在触摸 UI 线程等待游戏渲染线程，也不把同一事件交回 WPF 二次生成拖动。
        PostMessage(game, (uint)message, wParam, translated);
        return true;
    }
}
