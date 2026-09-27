using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>
/// 只拦截命中当前游戏悬浮层的触摸/笔合成鼠标事件，防止一根手指同时产生触摸和鼠标输入。
/// 菜单定位和视角移动由悬浮层发送带签名的输入；标题栏、其他窗口和隐藏后的游戏区域放行。
/// </summary>
internal static class TouchMousePromotionFilter
{
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    private const int WH_MOUSE_LL = 14;
    private const ulong TouchSignature = 0xFF515700;
    private const ulong TouchSignatureMask = 0xFFFFFF00;

    private sealed record Target(IntPtr GameHwnd, IntPtr OverlayHwnd);

    private static readonly object Sync = new();
    // 委托终身存活；回调只读取一个不可变快照，不等待 UI 锁。
    private static readonly LowLevelMouseProc Proc = HookCallback;
    private static IntPtr _hook;
    private static Target? _target;
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;
    private static bool _stopping;
    private static long _nextInstallAttemptAt;

    public static bool IsEnabled => Volatile.Read(ref _hook) != IntPtr.Zero && Volatile.Read(ref _target) != null;

    /// <summary>使用独立的消息线程安装钩子，避免启动器的布局、定时刷新拖住系统鼠标输入。</summary>
    public static void Enable(IntPtr gameHwnd, IntPtr overlayHwnd)
    {
        if (gameHwnd == IntPtr.Zero || overlayHwnd == IntPtr.Zero) return;
        try
        {
            lock (Sync)
            {
                if (_stopping) return;
                var target = _target;
                if (target == null || target.GameHwnd != gameHwnd || target.OverlayHwnd != overlayHwnd)
                    Volatile.Write(ref _target, new Target(gameHwnd, overlayHwnd));
                if (_thread != null || Environment.TickCount64 < _nextInstallAttemptAt) return;
                _nextInstallAttemptAt = Environment.TickCount64 + 2000;

                var thread = new Thread(RunHook)
                {
                    IsBackground = true,
                    Name = "XCL2 Touch Mouse Filter"
                };
                thread.SetApartmentState(ApartmentState.STA);
                _thread = thread;
                try { thread.Start(); }
                catch { _thread = null; throw; }
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"[触摸鼠标拦截器安装失败，不影响游戏运行]\n{ex}");
        }
    }

    private static void RunHook()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            lock (Sync)
            {
                _dispatcher = dispatcher;
                if (_stopping) return;
            }

            Volatile.Write(ref _hook, SetWindowsHookEx(WH_MOUSE_LL, Proc, IntPtr.Zero, 0));
            if (Volatile.Read(ref _hook) == IntPtr.Zero)
            {
                LauncherLogService.AppendLine("[触屏模式] 触摸合成鼠标拦截器安装失败，视角可能会乱转。");
                return;
            }
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"[触摸鼠标拦截器异常，不影响游戏运行]\n{ex}");
        }
        finally
        {
            var hook = Volatile.Read(ref _hook);
            if (hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(hook); } catch { /* 线程结束时系统也会清理钩子 */ }
            }
            lock (Sync)
            {
                Volatile.Write(ref _hook, IntPtr.Zero);
                _dispatcher = null;
                _thread = null;
            }
        }
    }

    /// <summary>暂停指定悬浮层的拦截，不反复装卸钩子；不传句柄时结束专用线程。</summary>
    public static void Disable(IntPtr overlayHwnd = default)
    {
        Dispatcher? dispatcher = null;
        lock (Sync)
        {
            if (overlayHwnd != IntPtr.Zero && overlayHwnd != _target?.OverlayHwnd) return;
            Volatile.Write(ref _target, null);
            if (overlayHwnd == IntPtr.Zero)
            {
                _stopping = true;
                dispatcher = _dispatcher;
            }
        }
        try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send); }
        catch { /* 应用退出时消息线程可能已经结束 */ }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var extra = (ulong)data.dwExtraInfo;
                var target = Volatile.Read(ref _target);
                if (extra != TouchInputInjector.InjectedSignature &&
                    (extra & TouchSignatureMask) == TouchSignature &&
                    target != null && GetForegroundWindow() == target.GameHwnd &&
                    IsWindowVisible(target.OverlayHwnd) && WindowFromPoint(data.pt) == target.OverlayHwnd)
                {
                    return (IntPtr)1;
                }
            }
            catch { /* 输入钩子发生异常时放行 */ }
        }
        return CallNextHookEx(Volatile.Read(ref _hook), nCode, wParam, lParam);
    }
}
