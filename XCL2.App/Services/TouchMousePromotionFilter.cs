using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// 触屏模式下的"手指鼠标"拦截器：把 Windows 自动由触摸合成出来的那一套鼠标事件
/// （touch-to-mouse promotion）在到达任何窗口之前就吃掉，只放行悬浮层自己用 SendInput
/// 注入的事件。
///
/// ===== 为什么必须有这个东西（视角疯狂乱转的真正成因）=====
/// Minecraft Java 版进游戏后会"抓住"光标：GLFW/LWJGL 把光标隐藏并锁在窗口里，每一帧
/// 读一次光标位置、算出相对上一帧的位移当作视角增量，然后把光标拉回窗口中心。这套机制
/// 是为真实鼠标设计的——鼠标本来就没有绝对位置，被拉回中心对它毫无影响。
///
/// 但触摸屏不是这样：你的手指有绝对坐标，而且 Windows 为了兼容老程序，会在你碰屏幕时
/// 额外合成一份"鼠标移动到手指位置"的事件。游戏根本分不出这是手指还是鼠标，于是：
///   手指落在屏幕左下角 → 系统把光标瞬移到左下角 → 游戏算出"一帧内移动了半个屏幕"
///   → 视角猛地甩过去 → 游戏又把光标拉回中心 → 下一次触摸事件又瞬移回手指位置……
/// 一来一回，视角就在那儿疯狂旋转。而且这跟我们注入的相对位移是叠加的，越拖越乱。
/// 光调灵敏度、光改注入方式都治不了根，因为多出来的那份位移压根不是我们发的。
///
/// ===== 解决办法 =====
/// 装一个 WH_MOUSE_LL 低级鼠标钩子，按 dwExtraInfo 的签名判断事件来源：
/// Windows 给"由触摸/触控笔合成的鼠标事件"打的标记是 0xFF515700（微软公开文档里
/// 判断 MOUSEEVENTF_FROMTOUCH 的标准做法），低位 bit 0x80 置位表示触控笔、清零表示手指。
/// 命中这个签名就直接返回 1 把事件吞掉，它既不会移动系统光标、也不会送进游戏；
/// 我们自己 SendInput 注入的事件带的是 <see cref="TouchInputInjector.InjectedSignature"/>，
/// 跟这个签名完全不同，一律放行。
///
/// 悬浮层上的虚拟按键不受影响：它们是靠 WPF 的 TouchDown/TouchUp（WM_TOUCH/WM_POINTER
/// 这条真正的触摸通道）工作的，跟被吞掉的"合成鼠标"是两套独立输入，见
/// TouchOverlayWindow.HookAllKeys 里同时挂 Touch* 和 Mouse* 两组处理器。
///
/// ===== 作用范围必须卡死 =====
/// 这是个全局钩子，开着的时候整台机器的触摸点击都会失效，所以只在"游戏窗口在前台
/// + 悬浮层可见"这个窗口期内启用（见 TouchOverlayWindow.FollowGameWindow），
/// 用户点"隐藏"让悬浮层穿透、切出游戏、或者悬浮层关闭时立刻卸载。任何一条路径漏掉
/// 卸载，用户的平板就会变成"触摸点哪都没反应"——所以 Teardown 里是无条件卸载，
/// 不依赖任何状态判断。
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

    private const int WH_MOUSE_LL = 14;

    /// <summary>Windows 给"触摸/触控笔合成的鼠标事件"打在 dwExtraInfo 高位上的固定签名。</summary>
    private const ulong TouchSignature = 0xFF515700;
    private const ulong TouchSignatureMask = 0xFFFFFF00;

    private static IntPtr _hook;
    // 委托必须用字段强引用住：只把局部变量传给 SetWindowsHookEx 的话，GC 随时可能回收它，
    // 之后系统再回调就是野指针，表现为进程随机崩溃（而且往往在几分钟后才崩，极难排查）。
    private static LowLevelMouseProc? _proc;

    public static bool IsEnabled => _hook != IntPtr.Zero;

    /// <summary>装上钩子（幂等）。必须在有消息循环的线程上调用——即 WPF 的 UI 线程。</summary>
    public static void Enable()
    {
        if (_hook != IntPtr.Zero) return;
        try
        {
            _proc = HookCallback;
            // 低级钩子不需要真实的模块句柄，传 IntPtr.Zero + 线程 0（全局）即可。
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                _proc = null;
                LauncherLogService.AppendLine("[触屏模式] 触摸合成鼠标拦截器安装失败，视角可能会乱转。");
            }
        }
        catch (Exception ex)
        {
            _proc = null;
            _hook = IntPtr.Zero;
            ErrorPresenter.LogTechnicalDetail($"[触摸鼠标拦截器安装失败，不影响游戏运行]\n{ex}");
        }
    }

    /// <summary>卸载钩子（幂等）。任何"悬浮层不再接管触摸"的时机都必须调用，
    /// 否则整机触摸点击会一直失效。</summary>
    public static void Disable()
    {
        if (_hook == IntPtr.Zero) { _proc = null; return; }
        try { UnhookWindowsHookEx(_hook); } catch { /* 尽力而为 */ }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var extra = (ulong)data.dwExtraInfo;

                // 我们自己注入的事件带专属签名，永远放行——少了这一条，悬浮层按钮
                // 注入的点击会被自己的钩子吞掉，等于整层失灵。
                if (extra == TouchInputInjector.InjectedSignature)
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                // 触摸/触控笔合成出来的鼠标事件：吞掉，不让它进入系统光标和游戏。
                if ((extra & TouchSignatureMask) == TouchSignature)
                    return (IntPtr)1;
            }
            catch
            {
                // 钩子回调里绝不能抛异常：这是在系统输入线程上下文里跑的，抛出去的后果
                // 是整个输入链路被系统摘掉。出任何意外都按"放行"处理。
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
