using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Models;

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
    private readonly Dictionary<ushort, int> _keyReferences = new();
    private readonly Dictionary<TouchInputInjector.MouseButton, int> _buttonReferences = new();
    private readonly Dictionary<Border, (ushort[] Keys, TouchInputInjector.MouseButton? Button)> _activeBindings = new();
    private readonly double _buttonScale;
    private bool _releasingHeld;
    private bool _settingsOpen;
    private long _explicitModeUntil;
    private long _menuTransitionUntil;
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
    private const int GameModeStabilityMilliseconds = 300;
    private const int MenuModeStabilityMilliseconds = 1000;
    private const int ContactSettleMilliseconds = 180;
    private long _menuRequestedUntil;
    private bool _hasObservedMenu;

    // 一根手指负责菜单指针/视角，其他手指仍可独立按住虚拟键。
    private TouchDevice? _viewTouch;
    private Point _viewDragStart;
    private Point _viewDragLast;
    private DateTime _viewDragTime;
    private bool _viewDragMoved;
    private bool _viewDragIsMenu;
    private bool _menuPointerDown;
    private Point _menuPointerScreen;
    private bool _menuCarryDrag;
    private const double MenuDragThreshold = 12;
    private const int MenuPickUpHoldMilliseconds = 350;
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
        _buttonScale = !double.IsFinite(buttonScale) || buttonScale <= 0 ? 1.0 : buttonScale;
        RootGrid.Opacity = Math.Clamp(opacityPercent <= 0 ? 85 : opacityPercent, 20, 100) / 100.0;

        RestoreKey.Tag = new TouchControlDefinition { Id = "restore", Mode = TouchControlMode.Command, Binding = "SHOW" };
        HookKey(RestoreKey);
        RebuildControls();
        ControlCanvas.SizeChanged += (_, _) => PositionControls();
        TouchControlSettingsService.Changed += OnControlsChanged;
        ViewPad.TouchDown += ViewPad_TouchDown;
        ViewPad.TouchMove += ViewPad_TouchMove;
        ViewPad.TouchUp += ViewPad_TouchUp;
        ViewPad.LostTouchCapture += ViewPad_LostTouchCapture;
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

    /// <summary>被动观察游戏的鼠标约束；不向尚在启动/加载中的游戏发送光标控制消息。</summary>
    private bool? ReadGameCursorMode()
    {
        if (!IsGameForeground) return null;
        var mode = TouchInputInjector.ReadGameMenuMode(_gameHwnd, _hasObservedMenu);
        return IsGameForeground ? mode : null;
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

        // 低级钩子尚未就绪时仍拦住触摸提升消息，防止一个触点又被当成鼠标点一次。
        if (TouchInputInjector.IsTouchPromotedMouseMessage)
        {
            handled = true;
            return IntPtr.Zero;
        }

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
        if (_closingDown || _settingsOpen) return;
        if (_gameHwnd == IntPtr.Zero || !IsWindow(_gameHwnd)) { Close(); return; }
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero && !IsIconic(_gameHwnd))
        {
            // 窗口切换期间 GetForegroundWindow 可短暂返回 0；不要一次采样就撤销全部触点。
            ResetModeCandidate();
            if (_foregroundMissingSince < 0) _foregroundMissingSince = Environment.TickCount64;
            if (Environment.TickCount64 - _foregroundMissingSince < 1000) return;
        }
        else _foregroundMissingSince = -1;

        if (foreground != _gameHwnd || IsIconic(_gameHwnd))
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

        // 全屏切换/调整尺寸期间客户区可能短暂不可读；保留上一帧，不让虚拟键闪退。
        if (!TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd, out var left, out var top, out var right, out var bottom) ||
            right <= left || bottom <= top) return;

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
        else TouchMousePromotionFilter.Enable(_gameHwnd, _overlayHwnd, OnPhysicalKeyDown);
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
            _cursorProbePending || now < _nextCursorProbeAt || now < _modeChangeAllowedAfter || now < _explicitModeUntil) return;

        var generation = _inputGeneration;
        _cursorProbePending = true;
        _nextCursorProbeAt = now + 100;
        try
        {
            var menu = await Task.Run(ReadGameCursorMode);
            // 探测期间开始过新手势、隐藏过窗口或切走过游戏的结果不能再用于清理输入。
            if (_closingDown || _hidden || !IsVisible || !IsGameForeground ||
                HasActiveContact || generation != _inputGeneration) return;
            if (menu == true) _hasObservedMenu = true;
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
            // 用户主动按菜单键时迅速响应；自动隐藏按键需要持续一秒的可靠菜单证据。
            var stability = menu.Value
                ? (now < _menuRequestedUntil ? 100 : MenuModeStabilityMilliseconds)
                : GameModeStabilityMilliseconds;
            if (now - _candidateModeSince < stability) return;

            ReleaseHeld();
            _menuMode = menu.Value;
            ApplyOverlayVisibility();
        }
        catch { _candidateMenuMode = null; }
        finally { _cursorProbePending = false; }
    }

    private void ApplyOverlayVisibility()
    {
        ViewPad.Visibility = _hidden ? Visibility.Collapsed : Visibility.Visible;
        ControlCanvas.Visibility = _hidden ? Visibility.Collapsed : Visibility.Visible;
        foreach (var key in _keys)
        {
            if (ReferenceEquals(key, RestoreKey) || key.Tag is not TouchControlDefinition c) continue;
            key.Visibility = c.Enabled && (c.Visibility == TouchControlVisibility.Always ||
                (c.Visibility == TouchControlVisibility.Menu) == _menuMode) ? Visibility.Visible : Visibility.Collapsed;
        }
        RestoreKey.Visibility = _hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnControlsChanged()
    {
        if (_closingDown) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(OnControlsChanged)); return; }
        RebuildControls();
    }

    private void RebuildControls()
    {
        ReleaseHeld();
        ControlCanvas.Children.Clear();
        _keys.RemoveAll(k => !ReferenceEquals(k, RestoreKey));
        foreach (var control in TouchControlSettingsService.Load())
        {
            var key = new Border
            {
                Name = "Touch_" + System.Text.RegularExpressions.Regex.Replace(control.Id, "[^a-zA-Z0-9_]", "_"),
                Style = (Style)FindResource(control.Height <= 40 ? "SmallKeyStyle" : "PadKeyStyle"),
                Margin = new Thickness(0), Tag = control,
                ToolTip = control.Mode == TouchControlMode.Command ? control.Label : control.Binding,
                Child = new TextBlock { Text = control.Label.Replace("\\n", "\n"),
                    Style = (Style)FindResource(control.Height <= 40 ? "SmallKeyTextStyle" : "KeyTextStyle") }
            };
            ControlCanvas.Children.Add(key);
            HookKey(key);
        }
        PositionControls();
        ApplyOverlayVisibility();
    }

    private void PositionControls()
    {
        var width = ControlCanvas.ActualWidth;
        var height = ControlCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        foreach (var key in ControlCanvas.Children.OfType<Border>())
        {
            if (key.Tag is not TouchControlDefinition c) continue;
            key.Width = Math.Min(width, c.Width * _buttonScale);
            key.Height = Math.Min(height, c.Height * _buttonScale);
            var x = c.X * _buttonScale;
            var y = c.Y * _buttonScale;
            if (c.Anchor is TouchControlAnchor.TopRight or TouchControlAnchor.BottomRight) x = width - x - key.Width;
            else if (c.Anchor == TouchControlAnchor.BottomCenter) x += (width - key.Width) / 2;
            if (c.Anchor is TouchControlAnchor.BottomLeft or TouchControlAnchor.BottomRight or TouchControlAnchor.BottomCenter)
                y = height - y - key.Height;
            Canvas.SetLeft(key, Math.Clamp(x, 0, Math.Max(0, width - key.Width)));
            Canvas.SetTop(key, Math.Clamp(y, 0, Math.Max(0, height - key.Height)));
            Panel.SetZIndex(key, c.ZIndex);
        }
    }

    // ===================== 虚拟按键 =====================

    private void HookKey(Border key)
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
        key.LostMouseCapture += (_, _) => { if (ReferenceEquals(_mouseKey, key)) ReleaseMouseKey(); };
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
        if (_releasingHeld || (down && (_closingDown || !IsGameForeground))) return;
        if (sender.Tag is not TouchControlDefinition control) return;
        sender.Opacity = down ? 0.55 : 1.0;
        var name = control.Binding.Trim().ToUpperInvariant();
        if (control.Mode == TouchControlMode.Command)
        {
            if (down) HandleUiCommand(name);
            return;
        }
        if (control.Mode == TouchControlMode.Tap)
        {
            if (!down) return;
            if (control.MenuAction != TouchMenuAction.None) ApplyMenuAction(control.MenuAction);
            if (name is "WHEEL_UP" or "WHEEL_DOWN") TouchInputInjector.MouseWheel(name == "WHEEL_UP" ? 1 : -1);
            else { AcquireBinding(sender, name); ReleaseBinding(sender); }
            return;
        }
        if (control.Mode == TouchControlMode.Hold)
        {
            if (down) AcquireBinding(sender, name); else ReleaseBinding(sender);
            return;
        }
        if (!down) return;
        if (_activeBindings.ContainsKey(sender)) ReleaseBinding(sender); else AcquireBinding(sender, name);
        sender.Background = new SolidColorBrush(_activeBindings.ContainsKey(sender)
            ? Color.FromArgb(0xB3, 0x2E, 0x7D, 0x32) : Color.FromArgb(0x66, 0, 0, 0));
        sender.Opacity = 1;
    }

    private void AcquireBinding(Border owner, string name)
    {
        if (_activeBindings.ContainsKey(owner)) return;
        TouchInputInjector.MouseButton? button = name switch
        {
            "MOUSE_L" => TouchInputInjector.MouseButton.Left, "MOUSE_R" => TouchInputInjector.MouseButton.Right,
            "MOUSE_M" => TouchInputInjector.MouseButton.Middle, _ => null
        };
        var keys = button.HasValue ? Array.Empty<ushort>() : name.Split('+', StringSplitOptions.TrimEntries)
            .Select(TouchInputInjector.TryMapKey).Where(k => k.HasValue).Select(k => k!.Value).Distinct().ToArray();
        _activeBindings[owner] = (keys, button);
        foreach (var key in keys)
        {
            _keyReferences.TryGetValue(key, out var count); _keyReferences[key] = count + 1;
            if (count == 0) { _heldKeys.Add(key); TouchInputInjector.KeyDown(key); }
        }
        if (button is { } b)
        {
            _buttonReferences.TryGetValue(b, out var count); _buttonReferences[b] = count + 1;
            if (count == 0) { _heldButtons.Add(b); TouchInputInjector.MouseDown(b); }
        }
    }

    private void ReleaseBinding(Border owner)
    {
        if (!_activeBindings.Remove(owner, out var binding)) return;
        foreach (var key in Enumerable.Reverse(binding.Keys))
        {
            if (!_keyReferences.TryGetValue(key, out var count)) continue;
            if (count > 1) _keyReferences[key] = count - 1;
            else { _keyReferences.Remove(key); _heldKeys.Remove(key); TouchInputInjector.KeyUp(key); }
        }
        if (binding.Button is { } b && _buttonReferences.TryGetValue(b, out var references))
        {
            if (references > 1) _buttonReferences[b] = references - 1;
            else { _buttonReferences.Remove(b); _heldButtons.Remove(b); TouchInputInjector.MouseUp(b); }
        }
    }

    private void ApplyMenuAction(TouchMenuAction action)
    {
        var menu = action == TouchMenuAction.Open || (action == TouchMenuAction.Toggle && !_menuMode);
        ReleaseHeld();
        _menuMode = menu;
        if (menu) _hasObservedMenu = true;
        // 等待游戏处理刚注入的键；期间不发送绝对鼠标移动，以免变成视角跳转。
        _menuTransitionUntil = Environment.TickCount64 + 180;
        _explicitModeUntil = Environment.TickCount64 + 650;
        _menuRequestedUntil = Environment.TickCount64 + 2000;
        ApplyOverlayVisibility();
    }

    private void OnPhysicalKeyDown(ushort vk)
    {
        if (_closingDown || _settingsOpen || _hidden || !IsGameForeground) return;
        var control = _keys.Select(k => k.Tag).OfType<TouchControlDefinition>().FirstOrDefault(c => c.Enabled &&
            c.MenuAction != TouchMenuAction.None && c.Mode == TouchControlMode.Tap &&
            TouchInputInjector.MatchesKeyBinding(c.Binding, vk));
        if (control != null) ApplyMenuAction(control.MenuAction);
        else if (vk == 0x1B) ApplyMenuAction(TouchMenuAction.Toggle);
    }

    private void HandleUiCommand(string command)
    {
        switch (command)
        {
            case "SENS+": _sensitivity = Math.Min(4.0, _sensitivity + 0.2); break;
            case "SENS-": _sensitivity = Math.Max(0.4, _sensitivity - 0.2); break;
            case "GRAB": if (_gameHwnd != IntPtr.Zero) SetForegroundWindow(_gameHwnd); break;
            case "HIDE": SetHidden(!_hidden); break;
            case "SHOW": SetHidden(false); break;
            case "MENU": ApplyMenuAction(TouchMenuAction.Toggle); break;
            case "KEYBOARD":
                ReleaseHeld();
                try { ScreenKeyboardService.Show(); }
                catch (Exception ex) { ToastService.ShowWarning("屏幕键盘打开失败：" + ex.Message); }
                break;
            case "SETTINGS":
                // 离开当前触摸回调再显示可激活的设置窗口，防止遗留触摸捕获。
                ReleaseHeld();
                Dispatcher.BeginInvoke(new Action(OpenTouchSettings));
                break;
        }
    }

    private void OpenTouchSettings()
    {
        if (_settingsOpen || _closingDown) return;
        _settingsOpen = true;
        ReleaseHeld();
        TouchMousePromotionFilter.Disable(_overlayHwnd);
        Visibility = Visibility.Hidden;
        try { new TouchControlsSettingsWindow { Topmost = true }.ShowDialog(); }
        finally
        {
            _settingsOpen = false;
            _lastClientBounds = null;
            if (!_closingDown && IsWindow(_gameHwnd)) { SetForegroundWindow(_gameHwnd); FollowGameWindow(); }
        }
    }

    private void SetHidden(bool hidden)
    {
        ReleaseHeld();
        _hidden = hidden;
        SyncInputMode();
        ApplyOverlayVisibility();
        if (hidden || !IsGameForeground) TouchMousePromotionFilter.Disable(_overlayHwnd);
        else TouchMousePromotionFilter.Enable(_gameHwnd, _overlayHwnd, OnPhysicalKeyDown);
    }

    // ===================== 菜单指针 / 视角 =====================

    private void ViewPad_TouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_hidden || _closingDown || !IsGameForeground || _viewTouch != null) return;
        if (Environment.TickCount64 < _menuTransitionUntil) return;
        // 双向同步：关闭背包后的第一根手指就恢复相对视角，不再因旧菜单状态拒绝操作。
        var menu = TouchInputInjector.ReadGameMenuMode(_gameHwnd, _hasObservedMenu);
        if (Environment.TickCount64 >= _explicitModeUntil && menu.HasValue && menu.Value != _menuMode)
        {
            ReleaseHeld();
            _menuMode = menu.Value;
            if (_menuMode) _hasObservedMenu = true;
            ApplyOverlayVisibility();
        }
        ResetModeCandidate();
        _viewTouch = e.TouchDevice;
        _viewDragStart = _viewDragLast = e.GetTouchPoint(ViewPad).Position;
        _viewDragTime = DateTime.UtcNow;
        _viewDragMoved = false;
        _viewDragIsMenu = _menuMode;
        _menuCarryDrag = false;
        _lookRemainderX = _lookRemainderY = 0;
        if (!ViewPad.CaptureTouch(e.TouchDevice)) { _viewTouch = null; return; }
        if (_viewDragIsMenu)
        {
            if (!MoveMenuPointer(_viewDragStart) || !PressMenuPointer()) CancelViewGesture();
        }
    }

    private void ViewPad_LostTouchCapture(object? sender, TouchEventArgs e)
    {
        var touch = _viewTouch;
        if (touch == null || touch.Id != e.TouchDevice.Id) return;
        // 游戏收到按下后可能取得原生鼠标捕获。等这一轮输入处理结束，仍在屏幕上的
        // 同一个触点只恢复捕获，不补发按下，避免刚拿起物品就被提前松开。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(_viewTouch, touch)) return;
            try
            {
                if (_hidden || _closingDown || !IsGameForeground || !ViewPad.IsVisible ||
                    touch.GetTouchPoint(ViewPad).Action == TouchAction.Up)
                {
                    CancelViewGesture();
                    return;
                }
                if (!ReferenceEquals(touch.Captured, ViewPad) && !ViewPad.CaptureTouch(touch))
                    CancelViewGesture();
            }
            catch (InvalidOperationException) { CancelViewGesture(); }
        }));
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
        // 一次手势沿用落指时确认的模式。触摸造成的光标隐藏或临时约束变化不应
        // 中途松开鼠标；显式按 ESC/E、切换窗口和关闭悬浮层仍会统一取消手势。
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
        var moved = (point - _viewDragStart).Length >= MenuDragThreshold;
        if (_viewDragIsMenu && !_viewDragMoved && moved && _menuPointerDown &&
            (DateTime.UtcNow - _viewDragTime).TotalMilliseconds >= MenuPickUpHoldMilliseconds)
        {
            // Java 背包的拿起和放下通常是两次点击。长按后再拖动时，先在原位置
            // 完成第一次点击，物品随指针移动，松手时在目标格完成第二次点击。
            // 立即开始拖动仍保持原生按住行为，用于创造栏滚动条、菜单滑块和分配物品。
            ReleaseMenuPointer();
            _menuCarryDrag = true;
        }
        if (moved) _viewDragMoved = true;
        if (_viewDragIsMenu)
        {
            if (!MoveMenuPointer(point)) { CancelViewGesture(); return; }
        }
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
        if (_viewDragIsMenu)
        {
            if (!MoveMenuPointer(point)) { CancelViewGesture(); return; }
            // 只有真正的长按搬运手势才补放置点击，普通单击与原生拖动绝不多点一次。
            // 移出游戏窗口后松手保留拿起状态，不向窗口外投递放置/丢弃点击。
            if (_menuCarryDrag && _viewDragMoved && IsMenuPointerInsideGame())
                PressMenuPointer();
        }
        CancelViewGesture();
        // 菜单不使用短按判定：停留、滑块拖动、下拉选择也必须有对应的按下/抬起。
        if (tap && !_heldButtons.Contains(TouchInputInjector.MouseButton.Left))
            TouchInputInjector.MouseClick(TouchInputInjector.MouseButton.Left);
    }

    private bool MoveMenuPointer(Point point)
    {
        var screen = ViewPad.PointToScreen(point);
        if (!TouchInputInjector.MenuPointerMove(_gameHwnd, (int)Math.Round(screen.X),
                (int)Math.Round(screen.Y), _menuPointerDown)) return false;
        _menuPointerScreen = screen;
        return true;
    }

    private bool PressMenuPointer()
    {
        if (_menuPointerDown) return true;
        _menuPointerDown = TouchInputInjector.MenuLeftButton(_gameHwnd,
            (int)Math.Round(_menuPointerScreen.X), (int)Math.Round(_menuPointerScreen.Y), true);
        return _menuPointerDown;
    }

    private void ReleaseMenuPointer()
    {
        if (!_menuPointerDown) return;
        _menuPointerDown = false;
        TouchInputInjector.MenuLeftButton(_gameHwnd,
            (int)Math.Round(_menuPointerScreen.X), (int)Math.Round(_menuPointerScreen.Y), false);
    }

    private bool IsMenuPointerInsideGame()
    {
        return TouchInputInjector.TryGetClientAreaOnScreen(_gameHwnd, out var left, out var top,
                   out var right, out var bottom) &&
               _menuPointerScreen.X >= left && _menuPointerScreen.X < right &&
               _menuPointerScreen.Y >= top && _menuPointerScreen.Y < bottom;
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
        ReleaseMenuPointer();
        _menuCarryDrag = false;
        _lookRemainderX = _lookRemainderY = 0;
        if (touch != null && ReferenceEquals(touch.Captured, ViewPad)) ViewPad.ReleaseTouchCapture(touch);
    }

    // ===================== 收尾 =====================

    private void ReleaseHeld()
    {
        if (_releasingHeld) return;
        _releasingHeld = true;
        try
        {
            ResetModeCandidate();
            CancelViewGesture();
            TouchInputInjector.ReleaseAll(_heldKeys, _heldButtons);
            _heldKeys.Clear(); _heldButtons.Clear();
            _keyReferences.Clear(); _buttonReferences.Clear(); _activeBindings.Clear();
            var touches = _touchKeys.Values.ToList();
            _touchKeys.Clear();
            foreach (var touch in touches)
                if (ReferenceEquals(touch.Device.Captured, touch.Key)) touch.Key.ReleaseTouchCapture(touch.Device);
            ReleaseMouseKey();
            foreach (var key in _keys)
            {
                key.Opacity = 1.0;
                if (key.Tag is TouchControlDefinition { Mode: TouchControlMode.Toggle })
                    key.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
            }
        }
        finally { _releasingHeld = false; }
    }

    private void Teardown()
    {
        if (_closingDown) return;
        _closingDown = true;
        _followTimer.Stop();
        TouchControlSettingsService.Changed -= OnControlsChanged;
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
