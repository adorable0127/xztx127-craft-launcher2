using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XCL2.App.Services;

/// <summary>点击只在控件边缘画一层浅描边；文字不缩放、不淡出，避免字形重采样闪烁。</summary>
public static class ControlFeedbackBehavior
{
    private sealed class FeedbackState
    {
        public FeedbackAdorner? Adorner;
        public AdornerLayer? Layer;
        public int Generation;
    }

    private sealed class FeedbackAdorner : Adorner
    {
        private readonly Pen _pen;
        private readonly double _radius;

        public FeedbackAdorner(ButtonBase button, double intensity) : base(button)
        {
            IsHitTestVisible = false;
            Focusable = false;
            var accent = button.TryFindResource("AccentBrush") as SolidColorBrush;
            var brush = new SolidColorBrush(accent?.Color ?? Color.FromRgb(24, 104, 232))
            { Opacity = 0.28 * intensity };
            brush.Freeze();
            _pen = new Pen(brush, 1.2);
            _pen.Freeze();
            var border = button.Template?.FindName("TileBorder", button) as Border
                         ?? button.Template?.FindName("bd", button) as Border;
            _radius = Math.Max(2, border?.CornerRadius.TopLeft ?? 4);
            Opacity = 0;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width < 2 || size.Height < 2) return;
            drawingContext.DrawRoundedRectangle(null, _pen,
                new Rect(0.75, 0.75, size.Width - 1.5, size.Height - 1.5), _radius, _radius);
        }
    }

    private static readonly ConditionalWeakTable<ButtonBase, FeedbackState> States = new();
    private static readonly List<WeakReference<ButtonBase>> ActiveButtons = new();
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((sender, _) =>
            {
                if (sender is ButtonBase button) Animate(button, pressed: true);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((sender, _) =>
            {
                if (sender is ButtonBase button) Animate(button, pressed: false);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((sender, e) =>
            {
                if (!e.IsRepeat && (e.Key is Key.Space or Key.Enter) && sender is ButtonBase button)
                    Animate(button, pressed: true);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewKeyUpEvent,
            new KeyEventHandler((sender, e) =>
            {
                if ((e.Key is Key.Space or Key.Enter) && sender is ButtonBase button)
                    Animate(button, pressed: false);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.LostMouseCaptureEvent,
            new MouseEventHandler((sender, _) =>
            {
                if (sender is ButtonBase button) Animate(button, pressed: false);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((sender, _) =>
            {
                if (sender is ButtonBase button) Remove(button);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.UnloadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ButtonBase button) Remove(button);
            }), handledEventsToo: true);
        AnimationSettingsService.Changed += ResetActive;
    }

    private static void ResetActive()
    {
        foreach (var reference in ActiveButtons.ToArray())
        {
            if (!reference.TryGetTarget(out var button)) continue;
            if (button.Dispatcher.CheckAccess()) Remove(button);
            else if (!button.Dispatcher.HasShutdownStarted)
                button.Dispatcher.BeginInvoke(new Action(() => Remove(button)));
        }
        ActiveButtons.Clear();
    }

    private static void Remove(ButtonBase button)
    {
        if (States.TryGetValue(button, out var state))
        {
            state.Generation++;
            if (state.Adorner != null)
            {
                state.Adorner.BeginAnimation(UIElement.OpacityProperty, null);
                state.Layer?.Remove(state.Adorner);
            }
            state.Adorner = null;
            state.Layer = null;
        }
        ActiveButtons.RemoveAll(reference => !reference.TryGetTarget(out var active) || ReferenceEquals(active, button));
    }

    private static void Animate(ButtonBase button, bool pressed)
    {
        var intensity = AnimationSettingsService.Intensity(AnimationKind.ControlFeedback);
        if (intensity <= 0 || !button.IsEnabled || !button.IsLoaded)
        {
            Remove(button);
            return;
        }
        var state = States.GetValue(button, _ => new FeedbackState());
        if (state.Adorner == null)
        {
            if (!pressed) return;
            var layer = AdornerLayer.GetAdornerLayer(button);
            if (layer == null) return;
            state.Adorner = new FeedbackAdorner(button, intensity);
            state.Layer = layer;
            layer.Add(state.Adorner);
            ActiveButtons.RemoveAll(reference => !reference.TryGetTarget(out _));
            ActiveButtons.Add(new WeakReference<ButtonBase>(button));
        }
        var adorner = state.Adorner;
        var from = adorner.Opacity;
        var generation = ++state.Generation;
        adorner.SetCurrentValue(UIElement.OpacityProperty, pressed ? 1.0 : 0.0);
        var animation = new DoubleAnimation(from, pressed ? 1 : 0,
            AnimationSettingsService.Duration(pressed ? 80 : 120, AnimationKind.ControlFeedback))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) =>
        {
            if (state.Generation != generation) return;
            if (pressed) adorner.BeginAnimation(UIElement.OpacityProperty, null);
            else Remove(button);
        };
        adorner.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
