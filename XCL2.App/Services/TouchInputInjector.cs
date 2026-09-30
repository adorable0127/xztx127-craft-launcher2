using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// 触屏模式的底层输入注入器：把悬浮层上的按钮/拖动，翻译成 Windows 系统级的键盘、鼠标事件，
/// 送进当前前台窗口（也就是 Minecraft 的游戏窗口）。
///
/// 键盘和视角位移使用 SendInput，保留扫描码、按键状态和相对输入。
/// 鼠标消息按命中窗口投递，并不保证送到前台窗口：悬浮层命中自己注入的鼠标消息时，
/// 必须在原生消息入口把它转交游戏，不能再次走 WPF 的按钮/拖动处理。
/// 悬浮层仍须保持 WS_EX_NOACTIVATE，确保键盘输入和 Raw Input 的前台目标是游戏。
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
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, in INPUT pInputs, int cbSize);

    // 每次只发送一个 INPUT，直接传结构体，避免连续滑动时为每一帧分配临时数组。
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int nVirtKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetClipCursor(out RECT rect);

    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x0001;
    private const int CURSOR_SUPPRESSED = 0x0002;

    /// <summary>
    /// 打在我们自己注入的每一个事件的 dwExtraInfo 上的专属签名（"XCL2" 的 ASCII）。
    ///
    /// 作用是让 <see cref="TouchMousePromotionFilter"/> 那个低级鼠标钩子能一眼分出
    /// "这是悬浮层发的"还是"这是 Windows 由手指合成的"：前者放行，后者吞掉。
    /// 没有这个标记，钩子会把我们自己注入的点击也一起吃掉，整个悬浮层直接失灵。
    /// </summary>
    public const ulong InjectedSignature = 0x58434C32; // 'X''C''L''2'

    private static IntPtr Signature => (IntPtr)unchecked((long)InjectedSignature);

    public static bool IsInjectedMouseMessage => GetMessageExtraInfo() == Signature;

    public static bool IsTouchPromotedMouseMessage
        => (unchecked((ulong)GetMessageExtraInfo().ToInt64()) & 0xFFFFFF00UL) == 0xFF515700UL;

    public static bool IsMouseMessage(int message) => message >= 0x0200 && message <= 0x020E;

    /// <summary>鼠标消息中的坐标转成屏幕像素；滚轮消息本来就使用屏幕坐标。</summary>
    public static bool TryGetMouseMessagePosition(IntPtr source, int message, IntPtr lParam, out int x, out int y)
    {
        var value = lParam.ToInt64();
        var point = new POINT { x = unchecked((short)(value & 0xFFFF)), y = unchecked((short)((value >> 16) & 0xFFFF)) };
        var ok = message is 0x020A or 0x020E || ClientToScreen(source, ref point);
        x = point.x;
        y = point.y;
        return ok;
    }

    /// <summary>把被悬浮层截获的原生鼠标消息交给游戏，避免模拟点击再次触发悬浮层。
    /// 不再注入一次相对位移：游戏已收到 SendInput 的 Raw Input，重复注入会加倍转动。</summary>
    public static void ForwardMouseMessage(IntPtr source, IntPtr game, int message, IntPtr wParam, IntPtr lParam)
    {
        if (game == IntPtr.Zero || !TryGetMouseMessagePosition(source, message, lParam, out var x, out var y)) return;
        var point = new POINT { x = x, y = y };
        if (message is not (0x020A or 0x020E) && !ScreenToClient(game, ref point)) return;
        var packed = unchecked((int)((uint)(ushort)point.x | ((uint)(ushort)point.y << 16)));
        PostMessage(game, (uint)message, wParam, new IntPtr(packed));
    }

    /// <summary>菜单采用屏幕绝对位置：同时更新系统指针和 GLFW 的窗口消息坐标。
    /// 只投递 WM_MOUSEMOVE 会使读取 GetCursorPos 的后端仍看到旧坐标。
    /// 松开游戏遗留的局部约束后定位；游戏视角路径绝不调用此方法。</summary>
    public static bool MenuPointerMove(IntPtr game, int screenX, int screenY, bool leftDown)
    {
        if (game == IntPtr.Zero || GetForegroundWindow() != game) return false;
        ClipCursor(IntPtr.Zero);
        if (!SetCursorPos(screenX, screenY)) return false;
        return PostMenuMouseMessage(game, 0x0200, screenX, screenY, leftDown);
    }

    /// <summary>调用方先投递目标位置，再投递按下；松开仍发给原游戏，切走窗口也不会松错目标。</summary>
    public static bool MenuLeftButton(IntPtr game, int screenX, int screenY, bool down)
        => PostMenuMouseMessage(game, down ? 0x0201u : 0x0202u, screenX, screenY, down);

    private static bool PostMenuMouseMessage(IntPtr game, uint message, int screenX, int screenY, bool leftDown)
    {
        if (game == IntPtr.Zero) return false;
        var point = new POINT { x = screenX, y = screenY };
        if (!ScreenToClient(game, ref point)) return false;
        var flags = leftDown ? 0x0001 : 0;
        if ((GetKeyState(0x10) & 0x8000) != 0) flags |= 0x0004; // MK_SHIFT
        if ((GetKeyState(0x11) & 0x8000) != 0) flags |= 0x0008; // MK_CONTROL
        var packed = unchecked((int)((uint)(ushort)point.x | ((uint)(ushort)point.y << 16)));
        return PostMessage(game, message, new IntPtr(flags), new IntPtr(packed));
    }

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
        SendInput(1, in input, InputSize);
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
        SendInput(1, in input, InputSize);
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
        SendInput(1, in input, InputSize);
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
        SendInput(1, in input, InputSize);
    }

    /// <summary>
    /// 把系统光标直接放到屏幕上的某个绝对位置（设备像素坐标）。
    ///
    /// 只在"游戏没有抓住光标"的时候用——也就是背包/菜单/聊天这类界面里，光标是可见的、
    /// 有真实绝对位置的，手指点哪就该点哪。游戏中（光标被抓走、隐藏）绝对不能用这个：
    /// 那种状态下游戏把光标位置的变化当成视角增量，强行挪光标 = 视角瞬间甩飞，
    /// 正是我们要消灭的那个毛病。判断方式见 <see cref="TryGetCursorVisibility"/>。
    /// </summary>
    public static void MoveCursorTo(int screenX, int screenY)
    {
        // 使用整个虚拟桌面的物理像素坐标，兼容副屏、负坐标和系统缩放。
        // 与相对移动一样打上签名，防止菜单定位被当成外接鼠标操作再次处理。
        var left = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var top = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        var width = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        var height = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (width <= 0 || height <= 0) return;
        var x = Math.Clamp((long)screenX - left, 0, width - 1);
        var y = Math.Clamp((long)screenY - top, 0, height - 1);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)(((2 * x + 1) * 65536) / (2L * width)),
                    dy = (int)(((2 * y + 1) * 65536) / (2L * height)),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = Signature
                }
            }
        };
        SendInput(1, in input, InputSize);
    }

    /// <summary>
    /// 光标是否可用于菜单定位。触摸抑制状态没有可靠的可见性含义，作为未知样本处理，
    /// 不能把 Windows 残留的箭头句柄当成游戏打开了菜单。
    /// 读取失败返回 false，由调用方保留上次确认的模式。
    /// </summary>
    public static bool TryGetCursorVisibility(out bool visible)
    {
        visible = false;
        try
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info)) return false;
            if ((info.flags & CURSOR_SUPPRESSED) != 0) return false;
            visible = info.hCursor != IntPtr.Zero && (info.flags & CURSOR_SHOWING) != 0;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 只观察游戏已经建立的鼠标约束，不主动抓取/隐藏/移动鼠标。
    /// 加载画面隐藏光标不等于进入世界：还必须确认光标被约束在游戏客户区内。
    /// true=菜单，false=游戏视角，null=状态不明确，调用方保留已确认的模式。
    /// </summary>
    public static bool? ReadGameMenuMode(IntPtr gameHwnd, bool allowFullscreenCapture = false)
    {
        if (!TryGetClientAreaOnScreen(gameHwnd, out var left, out var top, out var right, out var bottom) ||
            right <= left || bottom <= top || !GetClipCursor(out var clip)) return null;
        var confined = clip.Right > clip.Left && clip.Bottom > clip.Top &&
            clip.Left >= left && clip.Top >= top && clip.Right <= right && clip.Bottom <= bottom;
        var desktopLeft = GetSystemMetrics(76);
        var desktopTop = GetSystemMetrics(77);
        // 全屏客户区可能与整个桌面相同；默认的桌面矩形不是游戏抓取的证据。
        var wholeDesktop = clip.Left == desktopLeft && clip.Top == desktopTop &&
            clip.Right == desktopLeft + GetSystemMetrics(78) &&
            clip.Bottom == desktopTop + GetSystemMetrics(79);
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info)) return null;
        if ((info.flags & CURSOR_SUPPRESSED) != 0)
        {
            // 抑制本身不能判为菜单；游戏约束 + 显式空光标仍是独立的抓取证据。
            if (confined && info.hCursor == IntPtr.Zero && (!wholeDesktop || allowFullscreenCapture)) return false;
            // 菜单释放约束后，触屏仍可能让箭头处于抑制状态。此时结合释放约束的证据，
            // 交由上层持续采样确认，不能等到用户接入物理鼠标才恢复菜单操作。
            if (info.hCursor != IntPtr.Zero && (!confined || wholeDesktop)) return true;
            return null;
        }
        var visible = info.hCursor != IntPtr.Zero && (info.flags & CURSOR_SHOWING) != 0;
        // 可见光标是菜单证据，即使游戏遗留了较小的 ClipCursor 矩形也不能拒绝菜单模式。
        if (visible) return true;
        if (!visible && confined && (!wholeDesktop || (allowFullscreenCapture && info.hCursor == IntPtr.Zero))) return false;
        return null;
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

    // ===================== 按键名 -> 虚拟键码 =====================

    /// <summary>
    /// 悬浮层按钮上写的按键名（XAML 里 Tag="K:W" 这种）映射到虚拟键码。
    /// 支持自定义字母、功能键、常用特殊键及 VK_XX 虚拟键码。
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
    {
        name = (name ?? "").Trim().ToUpperInvariant();
        if (KeyMap.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1 && (name[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return name[0];
        if (name.StartsWith("F") && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return (ushort)(0x6F + f);
        if (name.StartsWith("VK_") && ushort.TryParse(name[3..], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out vk) && vk is > 0 and < 256) return vk;
        return name switch
        {
            "ESCAPE" => 0x1B, "RETURN" => 0x0D, "DELETE" => 0x2E, "BACKSPACE" => 0x08,
            "INSERT" => 0x2D, "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "CAPSLOCK" => 0x14, "RCTRL" => 0xA3, "RALT" => 0xA5, "RSHIFT" => 0xA1,
            "MINUS" => 0xBD, "PLUS" => 0xBB, "COMMA" => 0xBC, "PERIOD" => 0xBE,
            "SEMICOLON" => 0xBA, "QUOTE" => 0xDE, "BACKSLASH" => 0xDC,
            "LBRACKET" => 0xDB, "RBRACKET" => 0xDD, "GRAVE" => 0xC0,
            _ => null
        };
    }

    public static bool MatchesKeyBinding(string binding, ushort vk)
    {
        var keys = binding.Split('+', StringSplitOptions.TrimEntries).Select(TryMapKey).ToArray();
        return keys.Length > 0 && keys[^1] == vk && keys.Take(keys.Length - 1)
            .All(key => key.HasValue && (GetAsyncKeyState(key.Value) & 0x8000) != 0);
    }

    public static string GetKeyName(ushort vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return KeyMap.FirstOrDefault(pair => pair.Value == vk).Key ?? $"VK_{vk:X2}";
    }

    public static bool IsValidBinding(string? binding, bool command = false)
    {
        if (string.IsNullOrWhiteSpace(binding)) return false;
        var value = binding.Trim().ToUpperInvariant();
        if (command) return value is "SENS+" or "SENS-" or "GRAB" or "HIDE" or "SHOW" or "KEYBOARD" or "SETTINGS" or "MENU";
        if (value is "MOUSE_L" or "MOUSE_R" or "MOUSE_M" or "WHEEL_UP" or "WHEEL_DOWN") return true;
        var keys = value.Split('+', StringSplitOptions.TrimEntries);
        return keys.Length is > 0 and <= 5 && keys.All(k => TryMapKey(k).HasValue);
    }

    /// <summary>兜底：悬浮层被隐藏/关闭时，把所有可能还处于"按住"状态的键统一松开，
    /// 避免出现"人物一直往前走停不下来"这种最难受的残留状态。</summary>
    public static void ReleaseAll(IEnumerable<ushort> heldKeys, IEnumerable<MouseButton> heldButtons)
    {
        foreach (var vk in heldKeys.Distinct()) { try { KeyUp(vk); } catch { /* 尽力而为 */ } }
        foreach (var b in heldButtons.Distinct()) { try { MouseUp(b); } catch { /* 尽力而为 */ } }
    }
}
