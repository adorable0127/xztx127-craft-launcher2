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
/// 不抢焦点的游戏触屏悬浮层。菜单使用绝对定位和完整鼠标按下/移动/抬起，
/// 游戏视角只使用手指的相对位移。操作中的触点保持原模式，空闲时才确认菜单切换。
/// </summary>
public partial class TouchOverlayWindow : Window, IGlobalWindowTreatmentExempt
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_POINTERACTIVATE = 0x024B;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly IntPtr _gameHwnd;
    private IntPtr _overlayHwnd;
    private HwndSource? _source;
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly HashSet<TouchInputInjector.MouseButton> _heldButtons = new();
    private readonly Dictionary<ushort, bool> _toggleState = new();
    private readonly Dictionary<int, (TouchDevice Device, Border Key)> _touchKeys = new();
    private readonly List<Border> _keys = new();
    private Border? _mouseKey;
    private (int Left, int Top, int Width, int Height)? _lastClientBounds;
    private long _foregroundMissingSince = -1;

    // 光标探测最多同时进行一次，且只在后台等待游戏；绝不从触摸事件里同步询问游戏。
    private bool _cursorProbePending;
    private long _nextCursorProbeAt;
    private int _inputGeneration;
    private bool? _candidateMenuMode;
    private long _candidateModeSince;
    private long _modeChangeAllowedAfter;
    private const int ModeStabilityMilliseconds = 250;
    private const int ContactSettleMilliseconds = 180;

    // 一根手指负责菜单指针/视角，其他手指仍可独立按住虚拟键。
    private TouchDevice? _viewTouch;
    private Point _viewDragStart;
    private Point _viewDragLast;
    private DateTime _viewDragTime;
    private bool _viewDragMoved;
    private bool _viewDragIsMenu;
    private bool _menuPointerDown;
    private bool _menuMode = true;
    private bool _hidden;
    private bool _closingDown;
    private double _sensitivity;
    private double _lookRemainderX;
    private double _lookRemainderY;

    private bool HasActiveContact => _viewTouch != null || _touchKeys.Count != 0 || _mouseKey != null;

    private bool IsGameForeground => _gameHwnd != IntPtr.Zero &&
        GetForegroundWindow() == _gameHwnd && !IsIconic(_gameHwnd);

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
        ViewPad.LostTouchCapture += (_, e) =>
        {
            if (_viewTouch?.Id == e.TouchDevice.Id) CancelViewGesture();
        };
        ApplyOverlayVisibility();

        _followTimer.Tick += (_, _) => FollowGameWindow();
        Loaded += (_, _) => { FollowGameWindow(); _followTimer.Start(); };
        Closed += (_, _) => Teardown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _overlayHwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(_overlayHwnd, GWL_EXSTYLE);
        SetWindowLong(_overlayHwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
        _source = HwndSource.FromHwnd(_overlayHwnd);
        _source?.AddHook(WindowProc);
    }

    /// <summary>让游戏根据自己的光标模式设置光标，避免 WPF 的箭头覆盖游戏隐藏状态。
    /// 仅由后台探测调用；游戏的消息循环变慢时，触摸线程仍可持续投递位移。</summary>
    private bool? ReadGameCursorMode()
    {
        if (!IsGameForeground) return null;
        const uint SMTO_BLOCK = 0x0001;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        const int clientMouseMove = 1 | (0x0200 << 16); // HTCLIENT / WM_MOUSEMOVE
        if (SendMessageTimeout(_gameHwnd, WM_SETCURSOR, _gameHwnd,
            new IntPtr(clientMouseMove), SMTO_BLOCK | SMTO_ABORTIFHUNG, 20, out _) == IntPtr.Zero)
            return null;
        return IsGameForeground && TouchInputInjector.TryGetCursorVisibility(out var visible)
            ? visible : null;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is WM_MOUSEACTIVATE or WM_POINTERACTIVATE)
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE / PA_NOACTIVATE，保留输入但不抢焦点。
        }
        if (message == WM_SETCURSOR)
        {
            // 保留游戏设置的光标。每次鼠标位移都会产生此消息，不能在这里跨进程等待，
            // 也不能让 WPF 临时设置的箭头被误判为打开了菜单。
            handled = true;
            return new IntPtr(1);
        }
        if (!TouchInputInjector.IsMouseMessage(message)) return IntPtr.Zero;

        var injected = TouchInputInjector.IsInjectedMouseMessage;
        if (injected)
        {
            // SendInput 鼠标输入也会命中置顶的悬浮层。必须在 WPF 处理前截住并转给游戏，
            // 否则一次模拟点击会再次触发虚拟键/拖动，形成重复点击甚至输入循环。
            if (IsGameForeground)
                TouchInputInjector.ForwardMouseMessage(hwnd, _gameHwnd, message, wParam, lParam);
            handled = true;
        }
        else if (!_hidden && IsGameForeground &&
            TouchInputInjector.TryGetMouseMessagePosition(hwnd, message, lParam, out var x, out var y) &&
            ReferenceEquals(InputHitTest(PointFromScreen(new Point(x, y))), ViewPad))
        {
            // 外接鼠标在画面区域直接使用原来的鼠标消息，不把鼠标移动再模拟成第二份视角位移。
            TouchInputInjector.ForwardMouseMessage(hwnd, _gameHwnd, message, wParam, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void FollowGameWindow()
    {
        if (_closingDown) return;
        if (_gameHwnd == IntPtr.Zero || !IsWindow(_gameHwnd)) { Close(); return; }
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero && !IsIconic(_gameHwnd))
        {
            // 窗口切换期间 GetForegroundWindow 可短暂返回 0；不要一次采样就撤销全部触点。
            ResetModeCandidate();
            if (_foregroundMissingSince < 0) _foregroundMissingSince = Environment.TickCount64;
            if (Environment.TickCount64 - _foregroundMissingSince < 250) return;
        }
        else _foregroundMissingSince = -1;

        if (foreground != _gameHwnd || IsIconic(_gameHwnd) ||
            !TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd, out var left, out var top, out var right, out var bottom) ||
            right <= left || bottom <= top)
        {
            if (Visibility != Visibility.Hidden)
            {
                ReleaseHeld();
                Visibility = Visibility.Hidden;
                _lastClientBounds = null;
            }
            TouchMousePromotionFilter.Disable(_overlayHwnd);
            return;
        }

        // 原生客户区像素直接对齐，避免跨屏或 DPI 缩放时重复换算坐标；不覆盖游戏标题栏。
        // 只在显示、尺寸/位置变化或置顶状态丢失时调整，避免每 100ms 两次重排整个分层窗口。
        var bounds = (Left: left, Top: top, Width: right - left, Height: bottom - top);
        var showing = Visibility != Visibility.Visible;
        if (showing) Visibility = Visibility.Visible;
        if (showing || _lastClientBounds != bounds || (GetWindowLong(_overlayHwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
        {
            if (SetWindowPos(_overlayHwnd, HWND_TOPMOST, left, top, bounds.Width, bounds.Height, SWP_NOACTIVATE))
                _lastClientBounds = bounds;
        }
        if (_hidden) TouchMousePromotionFilter.Disable(_overlayHwnd);
        else TouchMousePromotionFilter.Enable(_gameHwnd, _overlayHwnd);
        SyncInputMode();
    }

    private void ResetModeCandidate()
    {
        _inputGeneration++;
        _candidateMenuMode = null;
        _modeChangeAllowedAfter = Environment.TickCount64 + ContactSettleMilliseconds;
    }

    private async void SyncInputMode()
    {
        var now = Environment.TickCount64;
        if (_closingDown || _hidden || !IsVisible || !IsGameForeground || HasActiveContact ||
            _cursorProbePending || now < _nextCursorProbeAt || now < _modeChangeAllowedAfter) return;

        var generation = _inputGeneration;
        _cursorProbePending = true;
        _nextCursorProbeAt = now + 100;
        try
        {
            var menu = await Task.Run(ReadGameCursorMode);
            // 探测期间开始过新手势、隐藏过窗口或切走过游戏的结果不能再用于清理输入。
            if (_closingDown || _hidden || !IsVisible || !IsGameForeground ||
                HasActiveContact || generation != _inputGeneration) return;
            if (!menu.HasValue || menu.Value == _menuMode)
            {
                _candidateMenuMode = null;
                return;
            }

            now = Environment.TickCount64;
            if (_candidateMenuMode != menu)
            {
                _candidateMenuMode = menu;
                _candidateModeSince = now;
                return;
            }
            if (now - _candidateModeSince < ModeStabilityMilliseconds) return;

            ReleaseHeld();
            _menuMode = menu.Value;
            ApplyOverlayVisibility();
        }
        catch { _candidateMenuMode = null; }
        finally { _cursorProbePending = false; }
    }

    private void ApplyOverlayVisibility()
    {
        foreach (var child in RootGrid.Children.OfType<UIElement>())
        {
            if (ReferenceEquals(child, RestoreKey)) continue;
            child.Visibility = _hidden ? Visibility.Collapsed : Visibility.Visible;
        }
        // 菜单/背包里的按钮不能被移动、动作和快捷栏虚拟键盖住。ESC 等功能键保留。
        var gameKeys = !_hidden && !_menuMode ? Visibility.Visible : Visibility.Collapsed;
        MovementKeys.Visibility = gameKeys;
        ActionKeys.Visibility = gameKeys;
        HotbarKeys.Visibility = gameKeys;
        RestoreKey.Visibility = _hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== 虚拟按键 =====================

    private void HookAllKeys(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border key && key.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                _keys.Add(key);
                key.TouchDown += Key_TouchDown;
                key.TouchUp += (_, e) => { e.Handled = true; ReleaseKeyTouch(e.TouchDevice.Id); };
                key.LostTouchCapture += (_, e) => ReleaseKeyTouch(e.TouchDevice.Id);
                key.MouseLeftButtonDown += Key_MouseDown;
                key.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (e.StylusDevice == null) ReleaseMouseKey();
                };
                key.LostMouseCapture += (_, _) =>
                {
                    if (ReferenceEquals(_mouseKey, key)) ReleaseMouseKey();
                };
            }
            HookAllKeys(child);
        }
    }

    private bool KeyHasTouch(Border key) => _touchKeys.Values.Any(value => ReferenceEquals(value.Key, key));

    private void Key_TouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_closingDown || !IsGameForeground || sender is not Border key || _touchKeys.ContainsKey(e.TouchDevice.Id)) return;
        ResetModeCandidate();
        var alreadyDown = KeyHasTouch(key) || ReferenceEquals(_mouseKey, key);
        _touchKeys[e.TouchDevice.Id] = (e.TouchDevice, key);
        if (!key.CaptureTouch(e.TouchDevice)) { _touchKeys.Remove(e.TouchDevice.Id); return; }
        if (!alreadyDown) OnKeyPressed(key, true);
    }

    private void ReleaseKeyTouch(int id)
    {
        if (!_touchKeys.Remove(id, out var touch)) return;
        ResetModeCandidate();
        if (ReferenceEquals(touch.Device.Captured, touch.Key)) touch.Key.ReleaseTouchCapture(touch.Device);
        if (!KeyHasTouch(touch.Key) && !ReferenceEquals(_mouseKey, touch.Key)) OnKeyPressed(touch.Key, false);
    }

    private void Key_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice != null || TouchInputInjector.IsInjectedMouseMessage ||
            _closingDown || !IsGameForeground || sender is not Border key) return;
        ReleaseMouseKey();
        ResetModeCandidate();
        _mouseKey = key;
        if (!key.CaptureMouse()) { _mouseKey = null; return; }
        if (!KeyHasTouch(key)) OnKeyPressed(key, true);
    }

    private void ReleaseMouseKey()
    {
        var key = _mouseKey;
        _mouseKey = null;
        if (key == null) return;
        ResetModeCandidate();
        if (key.IsMouseCaptured) key.ReleaseMouseCapture();
        if (!KeyHasTouch(key)) OnKeyPressed(key, false);
    }

    private void OnKeyPressed(Border sender, bool down)
    {
        if (down && (_closingDown || !IsGameForeground)) return;
        if (sender.Tag is not string tag) return;
        var parts = tag.Split(':', 2);
        if (parts.Length != 2) return;
        var kind = parts[0];
        var name = parts[1];
        sender.Opacity = down ? 0.55 : 1.0;

        switch (kind)
        {
            case "UI":
                if (down) HandleUiCommand(name);
                return;
            case "TAP":
                if (!down) return;
                if (name.StartsWith("MOUSE_")) { ClickMouse(name); return; }
                if (TouchInputInjector.TryMapKey(name) is ushort tapVk)
                {
                    // 用户主动打开/关闭菜单时可以结束旧操作，避免另一根手指仍按 W 导致
                    // 自动模式确认一直等待；普通视角滑动和跳跃不会走这条路径。
                    if (name is "ESC" or "E" or "T" or "SLASH") ReleaseHeld();
                    TouchInputInjector.KeyTap(tapVk);
                }
                return;
            case "HOLD":
                if (name.StartsWith("MOUSE_"))
                {
                    var button = name == "MOUSE_R" ? TouchInputInjector.MouseButton.Right : TouchInputInjector.MouseButton.Left;
                    if (down && _heldButtons.Add(button)) TouchInputInjector.MouseDown(button);
                    else if (!down && _heldButtons.Remove(button)) TouchInputInjector.MouseUp(button);
                    return;
                }
                if (TouchInputInjector.TryMapKey(name) is ushort holdVk)
                {
                    if (down && _heldKeys.Add(holdVk)) TouchInputInjector.KeyDown(holdVk);
                    else if (!down && _heldKeys.Remove(holdVk)) TouchInputInjector.KeyUp(holdVk);
                }
                return;
            case "TOGGLE":
                if (!down || TouchInputInjector.TryMapKey(name) is not ushort toggleVk) return;
                var on = _toggleState.TryGetValue(toggleVk, out var current) && current;
                if (on)
                {
                    TouchInputInjector.KeyUp(toggleVk);
                    _heldKeys.Remove(toggleVk);
                }
                else
                {
                    TouchInputInjector.KeyDown(toggleVk);
                    _heldKeys.Add(toggleVk);
                }
                _toggleState[toggleVk] = !on;
                sender.Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x66, 0, 0, 0)
                    : Color.FromArgb(0xB3, 0x2E, 0x7D, 0x32));
                sender.Opacity = 1.0;
                return;
        }
    }

    private static void ClickMouse(string name) => TouchInputInjector.MouseClick(name == "MOUSE_R"
        ? TouchInputInjector.MouseButton.Right : TouchInputInjector.MouseButton.Left);

    private void HandleUiCommand(string command)
    {
        switch (command)
        {
            case "SENS+": _sensitivity = Math.Min(4.0, _sensitivity + 0.2); break;
            case "SENS-": _sensitivity = Math.Max(0.4, _sensitivity - 0.2); break;
            case "GRAB":
                if (_gameHwnd != IntPtr.Zero) SetForegroundWindow(_gameHwnd);
                break;
            case "HIDE": SetHidden(!_hidden); break;
            case "SHOW": SetHidden(false); break;
        }
    }

    private void SetHidden(bool hidden)
    {
        ReleaseHeld();
        _hidden = hidden;
        SyncInputMode();
        ApplyOverlayVisibility();
        if (hidden || !IsGameForeground) TouchMousePromotionFilter.Disable(_overlayHwnd);
        else TouchMousePromotionFilter.Enable(_gameHwnd, _overlayHwnd);
    }

    // ===================== 菜单指针 / 视角 =====================

    private void ViewPad_TouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_hidden || _closingDown || !IsGameForeground || _viewTouch != null) return;
        ResetModeCandidate();
        _viewTouch = e.TouchDevice;
        _viewDragStart = _viewDragLast = e.GetTouchPoint(ViewPad).Position;
        _viewDragTime = DateTime.UtcNow;
        _viewDragMoved = false;
        _viewDragIsMenu = _menuMode;
        _lookRemainderX = _lookRemainderY = 0;
        if (!ViewPad.CaptureTouch(e.TouchDevice)) { _viewTouch = null; return; }
        // 菜单点击已经让游戏锁定光标时，新手势直接接续为相对视角操作。
        if (_viewDragIsMenu && TouchInputInjector.TryGetCursorVisibility(out var visible) && !visible)
        {
            _viewDragIsMenu = false;
            _menuMode = false;
            ApplyOverlayVisibility();
        }
        if (_viewDragIsMenu)
        {
            MoveMenuPointer(_viewDragStart);
            _menuPointerDown = true;
            TouchInputInjector.MouseDown(TouchInputInjector.MouseButton.Left);
        }
    }

    private bool ContinueViewGesture(TouchDevice device)
    {
        if (_viewTouch?.Id != device.Id) return false;
        if (_hidden || _closingDown || IsIconic(_gameHwnd)) { CancelViewGesture(); return false; }
        var foreground = GetForegroundWindow();
        if (foreground != _gameHwnd)
        {
            if (foreground != IntPtr.Zero) CancelViewGesture();
            return false;
        }
        if (_viewDragIsMenu && TouchInputInjector.TryGetCursorVisibility(out var visible) && !visible)
        {
            CancelViewGesture();
            return false;
        }
        return true;
    }

    private void ViewPad_TouchMove(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (!ContinueViewGesture(e.TouchDevice))
        {
            // 短暂没有前台窗口时丢弃这一帧位移，恢复后不积累成一次突然转向。
            if (_viewTouch?.Id == e.TouchDevice.Id) _viewDragLast = e.GetTouchPoint(ViewPad).Position;
            return;
        }
        var point = e.GetTouchPoint(ViewPad).Position;
        if ((point - _viewDragStart).Length >= 12) _viewDragMoved = true;
        if (_viewDragIsMenu) MoveMenuPointer(point);
        else ApplyLook(ViewPad.PointToScreen(point) - ViewPad.PointToScreen(_viewDragLast));
        _viewDragLast = point;
    }

    private void ViewPad_TouchUp(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (!ContinueViewGesture(e.TouchDevice))
        {
            if (_viewTouch?.Id == e.TouchDevice.Id) CancelViewGesture();
            return;
        }
        var point = e.GetTouchPoint(ViewPad).Position;
        var tap = !_viewDragIsMenu && !_viewDragMoved && (point - _viewDragStart).Length < 12 &&
            (DateTime.UtcNow - _viewDragTime).TotalMilliseconds < 300;
        if (_viewDragIsMenu) MoveMenuPointer(point);
        CancelViewGesture();
        // 菜单不使用短按判定：停留、滑块拖动、下拉选择也必须有对应的按下/抬起。
        if (tap && !_heldButtons.Contains(TouchInputInjector.MouseButton.Left))
            TouchInputInjector.MouseClick(TouchInputInjector.MouseButton.Left);
    }

    private void MoveMenuPointer(Point point)
    {
        var screen = ViewPad.PointToScreen(point);
        TouchInputInjector.MoveCursorTo((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
    }

    private void ApplyLook(Vector screenDelta)
    {
        // PointToScreen 已包含窗口 DPI 与按钮缩放，不能再乘一次 DPI。
        var fx = screenDelta.X * _sensitivity + _lookRemainderX;
        var fy = screenDelta.Y * _sensitivity + _lookRemainderY;
        var dx = (int)Math.Truncate(fx);
        var dy = (int)Math.Truncate(fy);
        _lookRemainderX = fx - dx;
        _lookRemainderY = fy - dy;
        TouchInputInjector.MouseMoveRelative(Math.Clamp(dx, -250, 250), Math.Clamp(dy, -250, 250));
    }

    private void CancelViewGesture()
    {
        var touch = _viewTouch;
        _viewTouch = null;
        if (touch != null) ResetModeCandidate();
        if (_menuPointerDown)
        {
            _menuPointerDown = false;
            TouchInputInjector.MouseUp(TouchInputInjector.MouseButton.Left);
        }
        _lookRemainderX = _lookRemainderY = 0;
        if (touch != null && ReferenceEquals(touch.Captured, ViewPad)) ViewPad.ReleaseTouchCapture(touch);
    }

    // ===================== 收尾 =====================

    private void ReleaseHeld()
    {
        ResetModeCandidate();
        CancelViewGesture();
        TouchInputInjector.ReleaseAll(_heldKeys, _heldButtons);
        _heldKeys.Clear();
        _heldButtons.Clear();
        foreach (var key in _toggleState.Keys.ToList()) _toggleState[key] = false;

        var touches = _touchKeys.Values.ToList();
        _touchKeys.Clear();
        foreach (var touch in touches)
            if (ReferenceEquals(touch.Device.Captured, touch.Key)) touch.Key.ReleaseTouchCapture(touch.Device);
        ReleaseMouseKey();
        foreach (var key in _keys) key.Opacity = 1.0;
        SprintKey.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        SneakKey.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
    }

    private void Teardown()
    {
        if (_closingDown) return;
        _closingDown = true;
        _followTimer.Stop();
        ReleaseHeld();
        TouchMousePromotionFilter.Disable(_overlayHwnd);
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    public void ShutdownOverlay()
    {
        Teardown();
        try { Close(); } catch { /* 已经关闭 */ }
    }
}
