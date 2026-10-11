using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XCL2.App.Services;

namespace XCL2.App.Views.Controls;

/// <summary>
/// 轻量的蓝紫轨道加载图标。几何图形只建立一次；每帧仅由 WPF 合成器旋转，
/// 不使用 DispatcherTimer、逐帧位图解码、布局动画或 CompositionTarget.Rendering。
/// 与 Resources/Loading/orbit-loading.gif 使用同一套图形。
/// </summary>
public sealed class LoadingIndicator : FrameworkElement
{
    private const double CanvasSize = 64;
    private static readonly DrawingGroup TrackDrawing = CreateTrackDrawing();
    private static readonly DrawingGroup OrbitDrawing = CreateOrbitDrawing();
    private readonly RotateTransform _rotation = new(0, 32, 32);
    private Window? _owner;
    private bool _subscribed;
    private bool _running;
    private double _strength = 1;
    private double _speed = -1;

    public LoadingIndicator()
    {
        Width = 56;
        Height = 56;
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        AutomationProperties.SetName(this, "正在加载");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var scale = Math.Min(ActualWidth, ActualHeight) / CanvasSize;
        if (!double.IsFinite(scale) || scale <= 0) return;

        drawingContext.PushTransform(new TranslateTransform(
            (ActualWidth - CanvasSize * scale) / 2,
            (ActualHeight - CanvasSize * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawDrawing(TrackDrawing);
        // 禁用动画时保留完整可辨认的静态图标；强度只改变动态图形的视觉浓度。
        drawingContext.PushOpacity(_strength <= 0 ? 0.85 : 0.5 + 0.5 * _strength);
        drawingContext.PushTransform(_rotation);
        drawingContext.DrawDrawing(OrbitDrawing);
        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            AnimationSettingsService.Changed += OnPreferencesChanged;
            _subscribed = true;
        }
        AttachOwner();
        ApplyPreferences();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            AnimationSettingsService.Changed -= OnPreferencesChanged;
            _subscribed = false;
        }
        DetachOwner();
        StopRotation();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyPreferences();

    private void OnOwnerStateChanged(object? sender, EventArgs e) => ApplyPreferences();

    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyPreferences();

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        StopRotation();
        DetachOwner();
        if (!_subscribed) return;
        AnimationSettingsService.Changed -= OnPreferencesChanged;
        _subscribed = false;
    }

    private void AttachOwner()
    {
        var owner = Window.GetWindow(this);
        if (ReferenceEquals(_owner, owner)) return;
        DetachOwner();
        _owner = owner;
        if (_owner == null) return;
        _owner.StateChanged += OnOwnerStateChanged;
        _owner.IsVisibleChanged += OnOwnerVisibilityChanged;
        _owner.Closed += OnOwnerClosed;
    }

    private void DetachOwner()
    {
        if (_owner == null) return;
        _owner.StateChanged -= OnOwnerStateChanged;
        _owner.IsVisibleChanged -= OnOwnerVisibilityChanged;
        _owner.Closed -= OnOwnerClosed;
        _owner = null;
    }

    private void OnPreferencesChanged()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)OnPreferencesChanged);
            return;
        }
        ApplyPreferences();
    }

    private void ApplyPreferences()
    {
        var strength = AnimationSettingsService.Intensity(AnimationKind.Loading);
        if (Math.Abs(_strength - strength) > 0.0001)
        {
            _strength = strength;
            InvalidateVisual();
        }
        if (!IsLoaded || !IsVisible || strength <= 0
            || (_owner != null && (!_owner.IsVisible || _owner.WindowState == WindowState.Minimized)))
        {
            StopRotation();
            return;
        }

        var speed = AnimationSettingsService.SpeedRatio;
        if (_running && Math.Abs(_speed - speed) < 0.0001) return;
        // 修改设置、隐藏后重新显示时从当前角度继续，避免视觉跳回第一帧。
        var angle = _rotation.Angle % 360;
        StopRotation();
        var duration = AnimationSettingsService.Duration(1200, AnimationKind.Loading);
        if (duration <= TimeSpan.Zero) return;
        _rotation.Angle = angle;
        _rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(angle, angle + 360, new Duration(duration))
            {
                RepeatBehavior = RepeatBehavior.Forever
            }, HandoffBehavior.SnapshotAndReplace);
        _running = true;
        _speed = speed;
    }

    private void StopRotation()
    {
        if (!_running) return;
        var angle = _rotation.Angle % 360;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _rotation.Angle = angle;
        _running = false;
        _speed = -1;
    }

    private static DrawingGroup CreateTrackDrawing()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawEllipse(null, CreatePen(Color.FromArgb(55, 105, 142, 210), 1.6), new Point(32, 32), 23, 23);
            context.DrawEllipse(null, CreatePen(Color.FromArgb(35, 120, 133, 220), 1), new Point(32, 32), 15, 15);
            var diamond = new StreamGeometry();
            using (var geometry = diamond.Open())
            {
                geometry.BeginFigure(new Point(32, 26.5), true, true);
                geometry.LineTo(new Point(37.5, 32), true, false);
                geometry.LineTo(new Point(32, 37.5), true, false);
                geometry.LineTo(new Point(26.5, 32), true, false);
            }
            diamond.Freeze();
            var core = new LinearGradientBrush(Color.FromRgb(104, 168, 255), Color.FromRgb(157, 114, 248), 45);
            core.Freeze();
            context.DrawGeometry(core, null, diamond);
        }
        drawing.Freeze();
        return drawing;
    }

    private static DrawingGroup CreateOrbitDrawing()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            const int segments = 48;
            for (var i = 0; i < segments; i++)
            {
                var t = (i + 0.5) / segments;
                var color = t < 0.55
                    ? Mix(Color.FromRgb(64, 189, 250), Color.FromRgb(90, 126, 255), t / 0.55)
                    : Mix(Color.FromRgb(90, 126, 255), Color.FromRgb(159, 103, 247), (t - 0.55) / 0.45);
                color.A = (byte)Math.Round(35 + 220 * t);
                DrawArc(context, 23, -160 + 248.0 * i / segments,
                    -160 + 248.0 * (i + 1) / segments + 0.25, CreatePen(color, 3.8));
            }
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(165, 117, 255)), null, Polar(23, 88), 2.65, 2.65);
            DrawArc(context, 15, 168, 228, CreatePen(Color.FromArgb(170, 88, 170, 251), 2));
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(205, 108, 195, 255)), null, Polar(15, 228), 1.65, 1.65);
        }
        drawing.Freeze();
        return drawing;
    }

    private static void DrawArc(DrawingContext context, double radius, double start, double end, Pen pen)
    {
        var path = new StreamGeometry();
        using (var geometry = path.Open())
        {
            geometry.BeginFigure(Polar(radius, start), false, false);
            geometry.ArcTo(Polar(radius, end), new Size(radius, radius), 0,
                end - start > 180, SweepDirection.Clockwise, true, false);
        }
        path.Freeze();
        context.DrawGeometry(null, pen, path);
    }

    private static Point Polar(double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(32 + radius * Math.Cos(radians), 32 + radius * Math.Sin(radians));
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
