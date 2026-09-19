using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 触屏模式的"套娃层"：一个始终置顶、永不抢焦点的透明窗口，贴在 Minecraft 游戏窗口上面，
/// 把手指的点按/拖动翻译成真实键鼠输入（注入逻辑见 <see cref="TouchInputInjector"/>）。
/// 布局参考 FCL：左下 WASD、右下跳跃/潜行/丢弃/攻击/使用、顶部 ESC/F3/DEL 等功能条、
/// 底部快捷栏 1-9、中间大片区域拖动转视角。
///
/// 三个必须守住的技术点，任何一个破了整层就失效：
/// 1. WS_EX_NOACTIVATE：窗口永远不能获得焦点。否则手指一碰悬浮层，前台窗口就变成悬浮层本身，
///    SendInput 注入的按键会发给悬浮层而不是游戏，表现为"点了没反应"。
/// 2. 隐藏（点击穿透）时把可命中的元素统统 Collapsed：WPF 的分层窗口在"没有可命中内容"的
///    区域本来就会让点击落到下面的窗口，所以不需要额外加 WS_EX_TRANSPARENT 去猜，
///    直接收掉命中区域最稳，也不会影响那个用来复原的小角标。
/// 3. 退出/隐藏前必须把按住的键全部松开：否则会留下"一直往前走""一直潜行"这种残留状态。
/// </summary>
public partial class TouchOverlayWindow : Window, IGlobalWindowTreatmentExempt
{
    // ===================== Win32 =====================

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// 把悬浮层重新抬到 Z 序最顶端。
    ///
    /// 为什么不能只靠 WPF 的 Topmost 属性：给一个已经是 true 的依赖属性再赋 true，WPF 判定
    /// 值没变化、直接跳过，根本不会再调一次 SetWindowPos——而游戏在切全屏/无边框、或者被
    /// Alt+Tab 重新激活时，Windows 会把游戏窗口抬到 topmost 组里并盖在我们上面，这时悬浮层
    /// 虽然"逻辑上还是 Topmost"，实际却被压在游戏画面底下，表现就是虚拟按键整个看不见。
    /// 所以这里直接调 Win32 强制重排，并带上 NOACTIVATE 保证抬升过程不抢走游戏的焦点
    /// （一旦抢焦点，注入的按键就发给悬浮层而不是游戏，整层立刻失效）。
    /// </summary>
    private void ForceTopmost()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Topmost = true;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch { /* 抬窗失败只影响观感，不能让它把游戏或悬浮层带崩 */ }
    }

    // ===================== 状态 =====================

    private IntPtr _gameHwnd;
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>当前处于"按住"状态的键/鼠标键。关闭或隐藏时统一松开，防止残留。</summary>
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly HashSet<TouchInputInjector.MouseButton> _heldButtons = new();

    /// <summary>切换式按键（疾跑 Ctrl / 潜行 Shift）当前是否处于按下状态。</summary>
    private readonly Dictionary<ushort, bool> _toggleState = new();

    /// <summary>视角拖动：每个触摸点/鼠标各自记录上一次坐标，支持"一只手走路一只手转视角"。</summary>
    private readonly Dictionary<int, Point> _viewDragLast = new();
    private readonly Dictionary<int, Point> _viewDragStart = new();
    private readonly Dictionary<int, DateTime> _viewDragTime = new();

    /// <summary>视角灵敏度：手指位移(DIP) -> 注入的鼠标相对位移倍率。顶部"灵敏±"按钮可调。</summary>
    private double _sensitivity;

    /// <summary>视角位移的小数残余（见 ApplyLook）：注入的相对位移只能是整数像素，
    /// 每次截断丢掉的那部分攒在这里，下一次一起补上，保证慢速拖动也是连续的。</summary>
    private double _lookRemainderX;
    private double _lookRemainderY;

    private bool _hidden;
    private bool _closingDown;

    /// <summary>上一次 DragTo 看到的"光标是否可见"。用来抓住"可见→不可见"这一瞬间
    /// （游戏刚锁光标进世界），见 DragTo 里的说明。初始值给 true，这样万一游戏一开局
    /// 就已经是锁定状态（比如悬浮层是中途挂上去的），第一帧最多是漏抓一次居中，
    /// 不会误把"游戏本来就没打开"当成一次假的锁定瞬间去挪光标。</summary>
    private bool _cursorWasVisible = true;

    public TouchOverlayWindow(IntPtr gameHwnd, double buttonScale, double opacityPercent, double sensitivity)
    {
        InitializeComponent();
        _gameHwnd = gameHwnd;
        _sensitivity = sensitivity <= 0 ? 1.4 : sensitivity;

        RootScale.ScaleX = RootScale.ScaleY = buttonScale <= 0 ? 1.0 : buttonScale;
        RootGrid.Opacity = Math.Clamp(opacityPercent <= 0 ? 85 : opacityPercent, 20, 100) / 100.0;

        HookAllKeys(RootGrid);

        ViewPad.TouchDown += ViewPad_TouchDown;
        ViewPad.TouchMove += ViewPad_TouchMove;
        ViewPad.TouchUp += ViewPad_TouchUp;
        ViewPad.MouseLeftButtonDown += ViewPad_MouseDown;
        ViewPad.MouseMove += ViewPad_MouseMove;
        ViewPad.MouseLeftButtonUp += ViewPad_MouseUp;

        _followTimer.Tick += (_, _) => FollowGameWindow();
        Loaded += (_, _) => { FollowGameWindow(); _followTimer.Start(); };
        Closed += (_, _) => Teardown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        // NOACTIVATE：永不抢焦点（整层能工作的前提）；TOOLWINDOW：不出现在 Alt+Tab / 任务栏里。
        // WS_EX_LAYERED 一定要保留：AllowsTransparency=True 的 WPF 窗口正是靠它做逐像素
        // alpha 合成的。这里显式 OR 回来是一道保险——万一以后又有哪段全局代码（历史上就是
        // ThemeService 的整窗透明逻辑，见 WindowTreatmentPolicy 类注释）把这个样式摘掉，
        // 整层就会变成一块没有画面的不透明灰方块，按钮和透明度全部消失。
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
        ForceTopmost();
    }

    // ===================== 跟随游戏窗口 =====================

    /// <summary>
    /// 每 200ms 对齐一次游戏窗口的位置和大小；游戏被最小化、切到后台、或已经退出时自动隐藏，
    /// 避免悬浮层孤零零盖在桌面或别的程序上面。窗口像素坐标要按 DPI 换算成 WPF 的 DIP，
    /// 平板普遍开着 150%/200% 缩放，不换算会错位一大截。
    /// </summary>
    private void FollowGameWindow()
    {
        if (_closingDown) return;

        if (_gameHwnd == IntPtr.Zero || !IsWindow(_gameHwnd))
        {
            Close();
            return;
        }

        var fg = GetForegroundWindow();
        var gameActive = fg == _gameHwnd && !IsIconic(_gameHwnd);
        if (!gameActive)
        {
            // 切走时收起按住的键，否则回到游戏时人物还在往前走。
            if (Visibility == Visibility.Visible) ReleaseHeld();
            Visibility = Visibility.Hidden;
            // 游戏不在前台了就必须立刻把全局触摸拦截摘掉，否则用户切出去用别的程序时
            // 会发现"整个平板点哪都没反应"。见 TouchMousePromotionFilter 类注释。
            TouchMousePromotionFilter.Disable();
            return;
        }

        // 用客户区而不是整个窗口矩形：游戏如果是有边框的窗口模式，标题栏右上角原生的
        // 最小化/最大化(全屏)/关闭三个按钮就在标题栏那一条上。之前贴的是 GetWindowRect
        // （连标题栏一起盖住），悬浮层的透明拖拽区会把点在那三个按钮上的手指也当成
        // 视角拖动吃掉，按钮再也点不到。只贴客户区，标题栏留在悬浮层外面，
        // 那三个按钮自然重新可以点——不需要在悬浮层里再画一遍。
        if (!TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd, out var cLeft, out var cTop, out var cRight, out var cBottom))
        {
            if (!GetWindowRect(_gameHwnd, out var fallback)) return;
            cLeft = fallback.Left; cTop = fallback.Top; cRight = fallback.Right; cBottom = fallback.Bottom;
        }

        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = m.Transform(new Point(cLeft, cTop));
        var bottomRight = m.Transform(new Point(cRight, cBottom));

        Left = topLeft.X;
        Top = topLeft.Y;
        Width = Math.Max(1, bottomRight.X - topLeft.X);
        Height = Math.Max(1, bottomRight.Y - topLeft.Y);
        var wasHiddenByFollow = Visibility != Visibility.Visible;
        Visibility = Visibility.Visible;

        // 游戏全屏切换 / 重新获得焦点之后，悬浮层很容易被压到游戏窗口下面。每次跟随都强制
        // 重排一次 Z 序（见 ForceTopmost 的说明，光赋值 Topmost=true 是没用的）。
        // 刚从隐藏恢复的那一帧一定要抬；平时也抬——SetWindowPos 在已经处于顶层时是廉价的，
        // 200ms 一次的开销可以忽略，但漏抬一次用户就会看到"按键不见了"。
        ForceTopmost();
        if (wasHiddenByFollow) Dispatcher.BeginInvoke(new Action(ForceTopmost), DispatcherPriority.Background);

        // 只有"游戏在前台 + 悬浮层没被用户收起来"这个窗口期内才拦截系统合成的触摸鼠标事件。
        // 用户点了"隐藏"（整层点击穿透、想直接摸游戏画面）时必须放行，否则他连游戏都点不动。
        if (_hidden) TouchMousePromotionFilter.Disable();
        else TouchMousePromotionFilter.Enable();
    }

    // ===================== 按键事件绑定 =====================

    /// <summary>递归给所有带 Tag 的 Border 挂上触摸/鼠标的按下与抬起。
    /// 同时处理 Touch 和 Mouse 是为了"平板用手指、外接鼠标调试"两种场景都能用；
    /// TouchDown 里把事件标记为已处理，系统就不会再把它提升成一次重复的鼠标事件。</summary>
    private void HookAllKeys(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border b && b.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                b.TouchDown += (s, e) => { e.Handled = true; OnKeyPressed((Border)s!, true); };
                b.TouchUp += (s, e) => { e.Handled = true; OnKeyPressed((Border)s!, false); };
                b.TouchLeave += (s, e) => { e.Handled = true; OnKeyPressed((Border)s!, false); };
                b.MouseLeftButtonDown += (s, e) => { e.Handled = true; OnKeyPressed((Border)s!, true); };
                b.MouseLeftButtonUp += (s, e) => { e.Handled = true; OnKeyPressed((Border)s!, false); };
                b.MouseLeave += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) OnKeyPressed((Border)s!, false); };
            }
            HookAllKeys(child);
        }
    }

    /// <summary>
    /// 统一的按键分发。Tag 的写法：
    /// HOLD:W        按住生效（移动、攻击等）
    /// TAP:ESC       按下即触发一次（功能键、快捷栏）
    /// TOGGLE:SHIFT  点一下按住、再点一下松开（疾跑/潜行）
    /// HOLD:MOUSE_L  鼠标左/右键按住
    /// UI:xxx        悬浮层自身的功能，不发给游戏
    /// </summary>
    private void OnKeyPressed(Border sender, bool down)
    {
        if (sender.Tag is not string tag) return;
        var parts = tag.Split(':', 2);
        if (parts.Length != 2) return;
        var kind = parts[0];
        var name = parts[1];

        // 按下时给一点视觉反馈，手指盖住按钮时也能从边缘看出来按上了。
        sender.Opacity = down ? 0.55 : 1.0;

        switch (kind)
        {
            case "UI":
                if (down) HandleUiCommand(name);
                return;

            case "TAP":
                if (!down) return;
                if (name.StartsWith("MOUSE_")) { ClickMouse(name); return; }
                if (TouchInputInjector.TryMapKey(name) is ushort tapVk) TouchInputInjector.KeyTap(tapVk);
                return;

            case "HOLD":
                if (name.StartsWith("MOUSE_"))
                {
                    var btn = name == "MOUSE_R" ? TouchInputInjector.MouseButton.Right : TouchInputInjector.MouseButton.Left;
                    if (down) { TouchInputInjector.MouseDown(btn); _heldButtons.Add(btn); }
                    else { TouchInputInjector.MouseUp(btn); _heldButtons.Remove(btn); }
                    return;
                }
                if (TouchInputInjector.TryMapKey(name) is ushort holdVk)
                {
                    if (down) { TouchInputInjector.KeyDown(holdVk); _heldKeys.Add(holdVk); }
                    else { TouchInputInjector.KeyUp(holdVk); _heldKeys.Remove(holdVk); }
                }
                return;

            case "TOGGLE":
                if (!down) return;
                if (TouchInputInjector.TryMapKey(name) is not ushort toggleVk) return;
                var on = _toggleState.TryGetValue(toggleVk, out var cur) && cur;
                if (on)
                {
                    TouchInputInjector.KeyUp(toggleVk);
                    _heldKeys.Remove(toggleVk);
                    _toggleState[toggleVk] = false;
                    sender.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
                }
                else
                {
                    TouchInputInjector.KeyDown(toggleVk);
                    _heldKeys.Add(toggleVk);
                    _toggleState[toggleVk] = true;
                    // 保持按下时换成高亮绿，一眼能看出"现在是潜行/疾跑中"。
                    sender.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x2E, 0x7D, 0x32));
                }
                // 切换键按下后立刻恢复不透明度，否则会一直保持半透明看不出状态色。
                sender.Opacity = 1.0;
                return;
        }
    }

    private static void ClickMouse(string name)
        => TouchInputInjector.MouseClick(name == "MOUSE_R"
            ? TouchInputInjector.MouseButton.Right
            : TouchInputInjector.MouseButton.Left);

    /// <summary>悬浮层自身的功能键。</summary>
    private void HandleUiCommand(string cmd)
    {
        switch (cmd)
        {
            case "SENS+":
                _sensitivity = Math.Min(4.0, _sensitivity + 0.2);
                break;
            case "SENS-":
                _sensitivity = Math.Max(0.4, _sensitivity - 0.2);
                break;
            case "GRAB":
                // "聚焦"：手动把游戏窗口重新置为前台。极少数情况下（比如切过别的程序回来）
                // 前台窗口不是游戏，这时候注入的按键会跑到别处，点一下这个按钮就能恢复。
                if (_gameHwnd != IntPtr.Zero) SetForegroundWindow(_gameHwnd);
                break;
            case "HIDE":
                SetHidden(!_hidden);
                break;
            case "SHOW":
                SetHidden(false);
                break;
        }
    }

    /// <summary>隐藏 = 收掉所有可命中的元素，让触摸直接落到下面的游戏窗口（真正的"点击穿透"），
    /// 只留右上角一个"显示"小角标可以点回来。适合需要用系统触摸键盘打字、或者临时想直接摸屏幕的时候。</summary>
    private void SetHidden(bool hidden)
    {
        _hidden = hidden;
        ReleaseHeld();
        foreach (var child in RootGrid.Children.OfType<UIElement>())
        {
            if (ReferenceEquals(child, RestoreKey)) continue;
            child.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        }
        RestoreKey.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;

        // 穿透状态下手指要直接落到游戏上，这时候必须把触摸拦截放开。
        if (hidden) TouchMousePromotionFilter.Disable();
        else TouchMousePromotionFilter.Enable();
    }

    // ===================== 视角拖动区 =====================

    private void ViewPad_TouchDown(object? sender, TouchEventArgs e)
    {
        var id = e.TouchDevice.Id;
        BeginDrag(id, e.GetTouchPoint(ViewPad).Position);
        ViewPad.CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }

    private void ViewPad_TouchMove(object? sender, TouchEventArgs e)
    {
        DragTo(e.TouchDevice.Id, e.GetTouchPoint(ViewPad).Position);
        e.Handled = true;
    }

    private void ViewPad_TouchUp(object? sender, TouchEventArgs e)
    {
        var id = e.TouchDevice.Id;
        ViewPad.ReleaseTouchCapture(e.TouchDevice);
        FinishDrag(id, e.GetTouchPoint(ViewPad).Position);
        e.Handled = true;
    }

    private void ViewPad_MouseDown(object sender, MouseButtonEventArgs e)
    {
        BeginDrag(-1, e.GetPosition(ViewPad));
        ViewPad.CaptureMouse();
    }

    private void ViewPad_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        DragTo(-1, e.GetPosition(ViewPad));
    }

    private void ViewPad_MouseUp(object sender, MouseButtonEventArgs e)
    {
        ViewPad.ReleaseMouseCapture();
        FinishDrag(-1, e.GetPosition(ViewPad));
    }

    private void BeginDrag(int id, Point p)
    {
        _viewDragLast[id] = p;
        _viewDragStart[id] = p;
        _viewDragTime[id] = DateTime.UtcNow;
        // 每次落指都把上一次拖动残留的小数位清零，避免隔了很久的两次拖动之间
        // 攒下的半个像素突然在新的一笔开头跳一下。
        _lookRemainderX = 0;
        _lookRemainderY = 0;
        // 重新落指时也把"光标是否可见"同步一遍：如果游戏早在这次落指之前就已经锁上了
        // 光标（比如上一根手指抬起、隔了一会儿才按下第二根），这里同步成当前真实状态，
        // 避免 DragTo 把"很久以前就发生过的锁定"误判成"刚刚发生"，多余居中一次光标。
        _cursorWasVisible = TouchInputInjector.IsCursorVisible();
    }

    /// <summary>
    /// 手指在视角区移动。这里分成两种完全不同的处理，区别在于"游戏此刻有没有抓住光标"：
    ///
    /// ① 游戏画面里（光标被抓走、隐藏）——注入**相对位移**。
    ///    游戏这时候只认"光标相对上一帧动了多少"，绝对位置对它没有意义（它每帧都会把
    ///    光标拉回窗口中心）。手指抬起之后光标还在中心，跟手指在哪毫无关系，这正是
    ///    Minecraft 这套为鼠标设计的机制在触屏上唯一还能用的姿势。
    ///
    /// ② 背包/菜单/聊天里（光标可见）——**直接把光标拉到手指位置**（基岩版的手感）。
    ///    这种界面里光标有真实绝对位置，手指点哪就该是哪，用相对位移反而要一点点"蹭"过去。
    ///
    /// 至于"手指的绝对坐标被系统合成成鼠标事件、害得视角疯狂乱转"那个老毛病，
    /// 不是在这里躲的——那份多余的位移根本不是我们发的，得在系统层面拦掉，
    /// 见 TouchMousePromotionFilter 类注释。
    /// </summary>
    private void DragTo(int id, Point p)
    {
        if (!_viewDragLast.TryGetValue(id, out var last)) return;

        var cursorVisibleNow = TouchInputInjector.IsCursorVisible();

        // 光标刚刚从"可见"变成"不可见"：这正是游戏这一帧抓走光标、进入锁定视角的瞬间。
        // 这时候不管手指上一次落在哪儿，都强制把光标先钉在游戏客户区正中间，再当成
        // 一次全新的落指处理（不补算这一帧的位移）——否则光标从"手指上次点的地方"
        // 跳到游戏内部认定的中心，这一大截跳变会被游戏当成一次视角增量，
        // 表现就是刚锁上的瞬间视角猛地转飞一圈，跟有没有调灵敏度、有没有拦截触摸
        // 合成鼠标事件都没关系，纯粹是"光标该在哪"这件事本身对不上。
        if (_cursorWasVisible && !cursorVisibleNow)
        {
            TouchInputInjector.CenterCursor(_gameHwnd);
            _viewDragLast[id] = p;
            _cursorWasVisible = cursorVisibleNow;
            return;
        }
        _cursorWasVisible = cursorVisibleNow;

        if (cursorVisibleNow)
        {
            var screen = ViewPad.PointToScreen(p);
            TouchInputInjector.MoveCursorTo((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        }
        else
        {
            ApplyLook(p - last);
        }

        _viewDragLast[id] = p;
    }

    /// <summary>把手指位移换算成鼠标相对位移注入进去。乘以 DPI 缩放是为了让不同缩放比例下
    /// 的手感一致——DIP 位移在 200% 缩放的平板上对应的物理像素是两倍。</summary>
    private void ApplyLook(Vector delta)
    {
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        // 小数位要攒起来，不能每次 Round 掉：低灵敏度下慢慢拖，每一步算出来可能是 0.4 像素，
        // 直接四舍五入就永远是 0，表现为"轻轻拖动视角纹丝不动，用力一拖又猛地跳一下"。
        var fx = delta.X * _sensitivity * scale + _lookRemainderX;
        var fy = delta.Y * _sensitivity * scale + _lookRemainderY;

        var dx = (int)Math.Truncate(fx);
        var dy = (int)Math.Truncate(fy);
        _lookRemainderX = fx - dx;
        _lookRemainderY = fy - dy;

        // 单步位移封顶：正常手指一次移动事件最多几十像素，出现几百上千像素的单步位移
        // 只可能是异常来源（触摸点 id 复用、事件丢帧后补一个大跳变等）。不封顶的话，
        // 一次这样的跳变就是玩家眼里"视角突然甩飞一圈"。封顶比丢弃好：丢弃会让快速
        // 转身直接断掉，封顶只是转得比手指慢一点。
        const int MaxStep = 250;
        dx = Math.Clamp(dx, -MaxStep, MaxStep);
        dy = Math.Clamp(dy, -MaxStep, MaxStep);

        TouchInputInjector.MouseMoveRelative(dx, dy);
    }

    /// <summary>抬手时判断这是一次"拖动转视角"还是一次"点击"：位移小于 12 DIP 且时长小于 300ms
    /// 视为点击，注入一次鼠标左键——这样在物品栏/菜单界面里也能直接用手指点按钮，
    /// 不需要先切回键鼠模式。</summary>
    private void FinishDrag(int id, Point end)
    {
        var isTap = false;
        if (_viewDragStart.TryGetValue(id, out var start) && _viewDragTime.TryGetValue(id, out var t))
        {
            var moved = (end - start).Length;
            isTap = moved < 12 && (DateTime.UtcNow - t).TotalMilliseconds < 300;
        }
        _viewDragLast.Remove(id);
        _viewDragStart.Remove(id);
        _viewDragTime.Remove(id);

        if (!isTap) return;

        // 界面里（光标可见）点击之前先把光标挪到手指那一点上，否则点击会落在光标上一次
        // 停留的位置——那正是"点背包里的物品没反应、却把旁边那格给点了"的成因。
        // 游戏画面里（光标被抓住）则绝对不能挪光标：那等于凭空制造一次视角位移。
        if (TouchInputInjector.IsCursorVisible())
        {
            var screen = ViewPad.PointToScreen(end);
            TouchInputInjector.MoveCursorTo((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        }

        TouchInputInjector.MouseClick(TouchInputInjector.MouseButton.Left);
    }

    // ===================== 收尾 =====================

    private void ReleaseHeld()
    {
        TouchInputInjector.ReleaseAll(_heldKeys, _heldButtons);
        _heldKeys.Clear();
        _heldButtons.Clear();
        foreach (var k in _toggleState.Keys.ToList()) _toggleState[k] = false;
        SprintKey.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        SneakKey.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
    }

    private void Teardown()
    {
        _closingDown = true;
        _followTimer.Stop();
        ReleaseHeld();
        // 无条件卸载，不做任何状态判断：这里漏一次，用户的平板就会变成"触摸完全失灵"，
        // 而且他根本不会把这件事跟"刚才关了个游戏"联系起来。Disable 本身是幂等的。
        TouchMousePromotionFilter.Disable();
    }

    /// <summary>供外部（游戏进程退出、用户在设置里关掉触屏模式）安全关闭。</summary>
    public void ShutdownOverlay()
    {
        Teardown();
        try { Close(); } catch { /* 已经关了就算了 */ }
    }
}
