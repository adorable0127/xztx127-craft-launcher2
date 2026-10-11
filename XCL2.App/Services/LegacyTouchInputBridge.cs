using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>Old LWJGL/GLFW bridge: menu coordinates and relative camera motion are separate transports.</summary>
internal sealed class LegacyTouchInputBridge(IntPtr game)
{
    private double _x, _y;
    private int _failures;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public void Reset() { _x = _y = 0; }
    public void MoveLook(double x, double y)
    {
        if (GetForegroundWindow() != game || !double.IsFinite(x) || !double.IsFinite(y)) { Reset(); return; }
        _x += Math.Clamp(x, -250, 250); _y += Math.Clamp(y, -250, 250);
        int dx = (int)Math.Truncate(_x), dy = (int)Math.Truncate(_y); _x -= dx; _y -= dy;
        if (dx == 0 && dy == 0) return;
        if (!TouchInputInjector.TryMouseMoveRelative(dx, dy))
        {
            Reset();
            if (++_failures == 1) LauncherLogService.AppendLine("[旧版触屏] Windows 拒绝输入注入。请让启动器与游戏使用相同权限级别，并确认游戏在前台。");
        }
        else _failures = 0;
    }
    public bool MoveMenu(int x, int y, bool down) => GetForegroundWindow() == game && TouchInputInjector.MenuPointerMove(game, x, y, down);
    public bool MenuButton(int x, int y, bool down) => (!down || GetForegroundWindow() == game) && TouchInputInjector.MenuLeftButton(game, x, y, down);
}
