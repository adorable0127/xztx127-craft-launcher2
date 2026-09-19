using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// 触屏模式的底层输入注入器：把悬浮层上的按钮/拖动，翻译成 Windows 系统级的键盘、鼠标事件，
/// 送进当前前台窗口（也就是 Minecraft 的游戏窗口）。
///
/// 为什么用 SendInput 而不是 PostMessage/SendMessage：
/// Minecraft Java 版（1.13+ 用 LWJGL3/GLFW，老版本用 LWJGL2）都不是靠标准 WM_KEYDOWN 消息
/// 做游戏内输入的——GLFW 走的是原始输入(Raw Input)/底层键盘状态，鼠标视角更是完全依赖
/// "光标被锁定后系统上报的相对位移"。用 PostMessage 往窗口句柄发消息，在聊天框里也许能打出字，
/// 但人物根本不会移动、视角也不会转，是最常见的踩坑点。SendInput 注入的是系统输入队列层面的
/// 事件，对游戏来说跟真实键盘鼠标完全没有区别，所以三种输入（按键/点击/视角）都能正常工作。
///
/// 代价是：SendInput 只会送给"当前前台窗口"。所以悬浮层窗口必须永远不能抢焦点
/// （WS_EX_NOACTIVATE，见 TouchOverlayWindow），否则你一点按钮，焦点跑到悬浮层上，
/// 按键就全被悬浮层自己吃掉了，游戏一动不动。
///
/// 另外键盘事件统一用扫描码(KEYEVENTF_SCANCODE)而不是只给虚拟键码：GLFW 是按扫描码识别
/// 物理按键位置的，只给 VK 在部分非美式键盘布局/部分驱动环境下会识别失败。这里两者都填，
/// 由系统合成完整的 lParam，兼容性最好。
/// </summary>
internal static class TouchInputInjector
{
    // ===================== Win32 结构体与常量 =====================

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x0001;

    /// <summary>
    /// 打在我们自己注入的每一个事件的 dwExtraInfo 上的专属签名（"XCL2" 的 ASCII）。
    ///
    /// 作用是让 <see cref="TouchMousePromotionFilter"/> 那个低级鼠标钩子能一眼分出
    /// "这是悬浮层发的"还是"这是 Windows 由手指合成的"：前者放行，后者吞掉。
    /// 没有这个标记，钩子会把我们自己注入的点击也一起吃掉，整个悬浮层直接失灵。
    /// </summary>
    public const ulong InjectedSignature = 0x58434C32; // 'X''C''L''2'

    private static IntPtr Signature => (IntPtr)unchecked((long)InjectedSignature);

    // ===================== 键盘 =====================

    /// <summary>需要带"扩展键"标志位的按键：方向键、Delete/Insert、Home/End/PgUp/PgDn、右 Ctrl/Alt、小键盘回车。
    /// 少了这个标志位，系统会把 Delete 认成小键盘的 '.'、把上下左右认成小键盘数字，游戏里就是按了没反应。</summary>
    private static readonly HashSet<ushort> ExtendedKeys = new()
    {
        0x2E, // VK_DELETE
        0x2D, // VK_INSERT
        0x24, // VK_HOME
        0x23, // VK_END
        0x21, // VK_PRIOR (PageUp)
        0x22, // VK_NEXT  (PageDown)
        0x25, 0x26, 0x27, 0x28, // 左上右下
        0xA3, // VK_RCONTROL
        0xA5, // VK_RMENU
    };

    public static void KeyDown(ushort vk) => SendKey(vk, false);

    public static void KeyUp(ushort vk) => SendKey(vk, true);

    /// <summary>按一下就松开（用于 ESC/F3/E/T 这类"点一下触发"的功能键）。</summary>
    public static void KeyTap(ushort vk)
    {
        SendKey(vk, false);
        SendKey(vk, true);
    }

    private static void SendKey(ushort vk, bool up)
    {
        var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        uint flags = KEYEVENTF_SCANCODE;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        // 极少数虚拟键映射不出扫描码（返回 0）时退回纯虚拟键码方式，总比什么都发不出去强。
        if (scan == 0) flags &= ~KEYEVENTF_SCANCODE;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Signature }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ===================== 鼠标 =====================

    public enum MouseButton { Left, Right, Middle }

    public static void MouseDown(MouseButton button) => SendMouseButton(button, false);

    public static void MouseUp(MouseButton button) => SendMouseButton(button, true);

    public static void MouseClick(MouseButton button)
    {
        SendMouseButton(button, false);
        SendMouseButton(button, true);
    }

    private static void SendMouseButton(MouseButton button, bool up)
    {
        uint flags = button switch
        {
            MouseButton.Left => up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN,
            MouseButton.Right => up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN,
            _ => up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN,
        };
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = Signature } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>相对位移：这是"手指在视角区拖动 = 转视角"的实现基础。
    /// 游戏锁定光标后只关心相对位移量，不关心光标绝对坐标，所以这里绝不能用绝对坐标模式
    /// （MOUSEEVENTF_ABSOLUTE），否则视角会疯狂乱飞。</summary>
    public static void MouseMoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE, dwExtraInfo = Signature } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>滚轮：用于切换快捷栏物品。notches 为正=向上滚（上一格）。</summary>
    public static void MouseWheel(int notches)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT { mouseData = unchecked((uint)(notches * 120)), dwFlags = MOUSEEVENTF_WHEEL, dwExtraInfo = Signature }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 把系统光标直接放到屏幕上的某个绝对位置（设备像素坐标）。
    ///
    /// 只在"游戏没有抓住光标"的时候用——也就是背包/菜单/聊天这类界面里，光标是可见的、
    /// 有真实绝对位置的，手指点哪就该点哪。游戏中（光标被抓走、隐藏）绝对不能用这个：
    /// 那种状态下游戏把光标位置的变化当成视角增量，强行挪光标 = 视角瞬间甩飞，
    /// 正是我们要消灭的那个毛病。判断方式见 <see cref="IsCursorVisible"/>。
    /// </summary>
    public static void MoveCursorTo(int screenX, int screenY)
    {
        try { SetCursorPos(screenX, screenY); } catch { /* 尽力而为 */ }
    }

    /// <summary>
    /// 当前系统光标是否可见。用来区分玩家此刻在"游戏画面里"还是在"某个界面里"：
    /// Minecraft 进入游戏画面时会把光标隐藏并锁定（GLFW 的 disabled cursor 模式），
    /// 打开背包/菜单/聊天时又把它放出来。这是不用读游戏内存、不依赖版本就能拿到的
    /// 最可靠信号，比"猜测按了哪个键"靠谱得多。
    ///
    /// 拿不到状态时按"不可见（游戏中）"处理：走相对位移那条路，最坏情况是界面里点不准，
    /// 而不是视角乱飞——两害相权取其轻。
    /// </summary>
    public static bool IsCursorVisible()
    {
        try
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info)) return false;
            return (info.flags & CURSOR_SHOWING) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 取游戏窗口"客户区"（不含标题栏/边框）在屏幕上的矩形。
    ///
    /// 悬浮层之前贴的是 GetWindowRect（整个窗口，含标题栏），如果游戏是有边框的窗口模式，
    /// 标题栏右上角原生的"最小化/最大化/关闭"三个按钮就被悬浮层这块透明层盖在下面，
    /// 手指点上去只会被 ViewPad 当成视角拖动区吃掉，那三个按钮永远点不到。
    /// 换成客户区之后悬浮层只贴游戏实际画面这一块，标题栏留在悬浮层外面，
    /// 原生按钮自然重新可以点。
    /// </summary>
    public static bool TryGetClientAreaOnScreen(IntPtr hwnd, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        try
        {
            if (!GetClientRect(hwnd, out var rc)) return false;
            var tl = new POINT { x = rc.Left, y = rc.Top };
            var br = new POINT { x = rc.Right, y = rc.Bottom };
            if (!ClientToScreen(hwnd, ref tl)) return false;
            if (!ClientToScreen(hwnd, ref br)) return false;
            left = tl.x; top = tl.y; right = br.x; bottom = br.y;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把系统光标摆到游戏客户区正中间。
    ///
    /// 只在"光标刚刚从可见变成不可见"（也就是游戏刚锁光标进世界那一瞬间）调用一次：
    /// 如果那一刻光标恰好停在手指上一次点击的地方（比如菜单右上角），游戏锁光标时
    /// 内部记录的"上一帧位置"和"这一帧我们想让它在的中心"隔着老远，
    /// 游戏按"位移=视角增量"一算，等于凭空多出一大截位移，表现就是刚进世界视角猛地转一圈。
    /// 进锁定状态前先把光标钉在正中间，就不会有这个虚假的第一帧跳变了。
    /// </summary>
    public static void CenterCursor(IntPtr hwnd)
    {
        if (!TryGetClientAreaOnScreen(hwnd, out var left, out var top, out var right, out var bottom)) return;
        var cx = (left + right) / 2;
        var cy = (top + bottom) / 2;
        try { SetCursorPos(cx, cy); } catch { /* 尽力而为 */ }
    }

    // ===================== 按键名 -> 虚拟键码 =====================

    /// <summary>
    /// 悬浮层按钮上写的按键名（XAML 里 Tag="K:W" 这种）映射到虚拟键码。
    /// 只收录 Minecraft 实际会用到的那些键，不做完整键盘表——保持可读、避免维护负担。
    /// </summary>
    private static readonly Dictionary<string, ushort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["W"] = 0x57, ["A"] = 0x41, ["S"] = 0x53, ["D"] = 0x44,
        ["Q"] = 0x51, ["E"] = 0x45, ["F"] = 0x46, ["T"] = 0x54, ["R"] = 0x52,
        ["SPACE"] = 0x20, ["SHIFT"] = 0xA0 /* 左 Shift */, ["CTRL"] = 0xA2 /* 左 Ctrl */,
        ["ALT"] = 0xA4, ["TAB"] = 0x09, ["ESC"] = 0x1B, ["ENTER"] = 0x0D,
        ["DEL"] = 0x2E, ["BACK"] = 0x08, ["SLASH"] = 0xBF,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
        ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
        ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34, ["5"] = 0x35,
        ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39, ["0"] = 0x30,
        ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
    };

    public static ushort? TryMapKey(string name)
        => KeyMap.TryGetValue(name.Trim(), out var vk) ? vk : null;

    /// <summary>兜底：悬浮层被隐藏/关闭时，把所有可能还处于"按住"状态的键统一松开，
    /// 避免出现"人物一直往前走停不下来"这种最难受的残留状态。</summary>
    public static void ReleaseAll(IEnumerable<ushort> heldKeys, IEnumerable<MouseButton> heldButtons)
    {
        foreach (var vk in heldKeys.Distinct()) { try { KeyUp(vk); } catch { /* 尽力而为 */ } }
        foreach (var b in heldButtons.Distinct()) { try { MouseUp(b); } catch { /* 尽力而为 */ } }
    }
}
