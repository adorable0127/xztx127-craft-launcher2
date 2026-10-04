using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>保守地把 Windows 枚举到的任何鼠标类设备（包括触控板）视作键鼠环境。</summary>
internal static class TouchMousePresence
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr Device; public uint Type; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    private static long _nextRefresh;
    public static bool MouseConnected { get; private set; } = true;

    // UI 定时/触摸入口采样，低级钩子只读取缓存，避免在系统输入回调内枚举设备。
    public static bool Refresh()
    {
        var now = Environment.TickCount64;
        if (now < _nextRefresh) return MouseConnected;
        _nextRefresh = now + 200;
        MouseConnected = true; // 枚举失败时不接管鼠标。
        uint count = 0;
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count > 4096) return true;
        if (count == 0) { MouseConnected = false; return false; }
        var list = new RAWINPUTDEVICELIST[(int)count];
        var read = GetRawInputDeviceList(list, ref count, size);
        if (read == uint.MaxValue) return true;
        MouseConnected = list.Take((int)read).Any(d => d.Type == 0); // RIM_TYPEMOUSE
        return MouseConnected;
    }
}
