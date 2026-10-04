using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>Win11 风格的布局浮窗。最小化/最大化悬停共用；首次悬停才创建，Win10 不实例化。</summary>
public sealed class Windows11SnapLayoutService : IDisposable
{
    private readonly Window _window;
    private readonly Button[] _buttons;
    private readonly DispatcherTimer _hover;
    private readonly DispatcherTimer _dismiss;
    private Button? _anchor;
    private Popup? _popup;
    private Border? _card;
    private bool _disposed;

    public Windows11SnapLayoutService(Window window, params Button[] buttons)
    {
        _window = window;
        _buttons = buttons;
        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _dismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _hover.Tick += Hover_Tick;
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); Close(); };
        foreach (var button in buttons)
        {
            button.MouseEnter += Button_MouseEnter;
            button.MouseLeave += Button_MouseLeave;
        }
        window.Deactivated += Window_Deactivated;
        window.LocationChanged += Window_LocationChanged;
        window.PreviewMouseDown += Window_PreviewMouseDown;
    }

    private void Button_MouseEnter(object sender, MouseEventArgs e)
    {
        _dismiss.Stop();
        _hover.Stop();
        _anchor = sender as Button;
        _hover.Start();
    }
    private void Button_MouseLeave(object sender, MouseEventArgs e)
    {
        _hover.Stop();
        _dismiss.Start();
    }
    private void Window_Deactivated(object? sender, EventArgs e) => Close();
    private void Window_LocationChanged(object? sender, EventArgs e) => Close();
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Close();

    private void Hover_Tick(object? sender, EventArgs e)
    {
        _hover.Stop();
        if (_disposed || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            _anchor?.IsMouseOver != true || !_window.IsActive ||
            _window.WindowState == WindowState.Minimized || WindowChromeService.IsFullScreen)
            return;
        EnsurePopup();
        PopulateLayouts();
        _popup!.PlacementTarget = _anchor;
        _popup.HorizontalOffset = -230;
        _popup.IsOpen = true;
        UiAnimationHelper.Entrance(_card!, page: false);
    }

    private void EnsurePopup()
    {
        if (_popup != null) return;
        _card = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Padding = new Thickness(14), MinWidth = 270
        };
        _card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
        _card.MouseEnter += (_, _) => _dismiss.Stop();
        _card.MouseLeave += (_, _) => _dismiss.Start();
        _popup = new Popup
        {
            Child = _card, Placement = PlacementMode.Bottom, VerticalOffset = 6,
            AllowsTransparency = true, StaysOpen = true, Focusable = false
        };
        _popup.Opened += (_, _) =>
        {
            // 浮窗使用独立软件目标，避免旧驱动在透明合成下出现空白块。
            if (PresentationSource.FromVisual(_card) is HwndSource source && source.CompositionTarget != null)
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        };
    }

    private static Rect R(double x, double y, double width, double height) => new(x, y, width, height);
    private static readonly Rect[][] Layouts =
    {
        new[] { R(0,0,.5,1), R(.5,0,.5,1) },
        new[] { R(0,0,2.0/3,1), R(2.0/3,0,1.0/3,1) },
        new[] { R(0,0,1.0/3,1), R(1.0/3,0,1.0/3,1), R(2.0/3,0,1.0/3,1) },
        new[] { R(0,0,.25,1), R(.25,0,.5,1), R(.75,0,.25,1) },
        new[] { R(0,0,.5,.5), R(.5,0,.5,.5), R(0,.5,.5,.5), R(.5,.5,.5,.5) },
        new[] { R(0,0,.5,1), R(.5,0,.5,.5), R(.5,.5,.5,.5) }
    };

    private void PopulateLayouts()
    {
        var stack = new StackPanel();
        var title = new TextBlock { Text = "窗口布局", FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        stack.Children.Add(title);
        var previews = new Grid();
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) previews.RowDefinitions.Add(new RowDefinition());
        var work = GetWorkArea();
        var dpi = VisualTreeHelper.GetDpi(_window);
        for (var i = 0; i < Layouts.Length; i++)
        {
            var preview = new Canvas { Width = 116, Height = 68, Margin = new Thickness(3, 4, 3, 4) };
            Grid.SetColumn(preview, i % 2);
            Grid.SetRow(preview, i / 2);
            for (var j = 0; j < Layouts[i].Length; j++)
            {
                var region = Layouts[i][j];
                // 任何区域都允许选择；区域小于窗口最小尺寸时，Snap() 会临时放宽最小尺寸。
                var usable = true;
                var zone = new Button
                {
                    Width = 116 * region.Width - 4, Height = 68 * region.Height - 4,
                    MinWidth = 0, MinHeight = 0, Style = new Style(typeof(Button)),
                    Padding = new Thickness(0), BorderThickness = new Thickness(1),
                    Cursor = usable ? Cursors.Hand : Cursors.Arrow, IsEnabled = usable,
                    ToolTip = usable ? $"布局 {i + 1} · 区域 {j + 1}" : "此区域小于启动器的最小尺寸",
                    Opacity = usable ? 1 : .35
                };
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
                border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
                border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
                border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
                zone.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
                zone.SetResourceReference(Control.BackgroundProperty, "GlowSoftBrush");
                zone.SetResourceReference(Control.BorderBrushProperty, "BorderBrush2");
                zone.MouseEnter += (_, _) => zone.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                zone.MouseLeave += (_, _) => zone.SetResourceReference(Control.BackgroundProperty, "GlowSoftBrush");
                zone.Click += (_, _) => Snap(region);
                System.Windows.Automation.AutomationProperties.SetName(zone, $"窗口布局 {i + 1} 区域 {j + 1}");
                Canvas.SetLeft(zone, 116 * region.X + 2);
                Canvas.SetTop(zone, 68 * region.Y + 2);
                preview.Children.Add(zone);
            }
            previews.Children.Add(preview);
        }
        stack.Children.Add(previews);
        _card!.Child = stack;
    }

    private void Snap(Rect region)
    {
        Close();
        var work = GetWorkArea();
        var dpi = VisualTreeHelper.GetDpi(_window);
        // 目标区域（DIP）比窗口最小尺寸还小：临时把 MinWidth/MinHeight 降到区域大小，
        // 否则 WPF 会把窗口撑回最小尺寸，贴边就"选不上"。窗口之后被拉回足够大时自动还原。
        var targetWidthDip = work.Width * region.Width / dpi.DpiScaleX;
        var targetHeightDip = work.Height * region.Height / dpi.DpiScaleY;
        RelaxMinSize(targetWidthDip, targetHeightDip);
        _window.WindowState = WindowState.Normal;
        SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero,
            (int)Math.Round(work.Left + work.Width * region.X),
            (int)Math.Round(work.Top + work.Height * region.Y),
            (int)Math.Round(work.Width * region.Width), (int)Math.Round(work.Height * region.Height), 0x0004);
        _window.Activate();
    }
    private double? _originalMinWidth, _originalMinHeight;
    private bool _restoreHooked;

    private void RelaxMinSize(double targetWidthDip, double targetHeightDip)
    {
        _originalMinWidth ??= _window.MinWidth;
        _originalMinHeight ??= _window.MinHeight;
        _window.MinWidth = Math.Min(_originalMinWidth.Value, Math.Max(200, Math.Floor(targetWidthDip)));
        _window.MinHeight = Math.Min(_originalMinHeight.Value, Math.Max(150, Math.Floor(targetHeightDip)));
        if (_restoreHooked) return;
        _restoreHooked = true;
        _window.SizeChanged += Window_SizeChangedRestoreMin;
    }

    private void Window_SizeChangedRestoreMin(object sender, SizeChangedEventArgs e)
    {
        if (_originalMinWidth is not double minW || _originalMinHeight is not double minH) return;
        if (_window.ActualWidth + 0.5 < minW || _window.ActualHeight + 0.5 < minH) return;
        _window.MinWidth = minW;
        _window.MinHeight = minH;
        _originalMinWidth = _originalMinHeight = null;
        _window.SizeChanged -= Window_SizeChangedRestoreMin;
        _restoreHooked = false;
    }

    private Rect GetWorkArea()
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(_window).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var work = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(_window);
        return new Rect(work.Left * dpi.DpiScaleX, work.Top * dpi.DpiScaleY,
            work.Width * dpi.DpiScaleX, work.Height * dpi.DpiScaleY);
    }
    public void Close()
    {
        _hover.Stop();
        _dismiss.Stop();
        if (_popup?.IsOpen != true || _card == null) return;
        UiAnimationHelper.FadeOut(_card, () => { if (_popup != null) _popup.IsOpen = false; }, _card.Opacity);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hover.Stop();
        _dismiss.Stop();
        foreach (var button in _buttons)
        {
            button.MouseEnter -= Button_MouseEnter;
            button.MouseLeave -= Button_MouseLeave;
        }
        _window.Deactivated -= Window_Deactivated;
        _window.LocationChanged -= Window_LocationChanged;
        _window.PreviewMouseDown -= Window_PreviewMouseDown;
        _window.SizeChanged -= Window_SizeChangedRestoreMin;
        if (_card != null) UiAnimationHelper.Reset(_card);
        if (_popup != null) _popup.IsOpen = false;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
