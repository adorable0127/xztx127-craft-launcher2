using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>可打断的过渡；基础值保存终值，取消时钟后不会留下透明或缩小的界面。</summary>
public static class UiAnimationHelper
{
    private sealed class Transition
    {
        public int Generation;
        public TranslateTransform? Move;
        public ScaleTransform? Scale;
        public DispatcherTimer? Fallback;
    }
    private sealed class EntranceTransforms
    {
        public readonly TranslateTransform Move = new();
        public readonly ScaleTransform Scale = new(1, 1);
    }
    private static readonly ConditionalWeakTable<UIElement, Transition> Transitions = new();
    private static readonly ConditionalWeakTable<FrameworkElement, EntranceTransforms> Entrances = new();
    private static bool UiEnabled => ConfigService.Active?.Config.EnableUiAnimations ?? true;
    private static bool PageEnabled => ConfigService.Active?.Config.EnablePageAnimations ?? true;

    public static void Reset(UIElement element, TranslateTransform? move = null, ScaleTransform? scale = null)
    {
        var state = Transitions.GetValue(element, _ => new Transition());
        state.Generation++;
        state.Fallback?.Stop();
        state.Fallback = null;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetCurrentValue(UIElement.OpacityProperty, 1.0);
        ResetMove(state.Move);
        ResetScale(state.Scale);
        ResetMove(move);
        ResetScale(scale);
        state.Move = null;
        state.Scale = null;
    }
    private static void ResetMove(TranslateTransform? move)
    {
        if (move == null) return;
        move.BeginAnimation(TranslateTransform.YProperty, null);
        move.Y = 0;
    }
    private static void ResetScale(ScaleTransform? scale)
    {
        if (scale == null) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
    }
    private static DoubleAnimation Animation(double from, double to, TimeSpan duration, bool exiting)
        => new(from, to, new Duration(duration))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = exiting ? EasingMode.EaseIn : EasingMode.EaseOut }
        };

    private static void Run(UIElement element, double fromOpacity, double toOpacity, TimeSpan duration,
        bool enabled, TranslateTransform? move = null, double fromY = 0, double toY = 0,
        ScaleTransform? scale = null, double fromScale = 1, double toScale = 1,
        Action? onCompleted = null)
    {
        Reset(element, move, scale);
        var state = Transitions.GetValue(element, _ => new Transition());
        var generation = state.Generation;
        state.Move = move;
        state.Scale = scale;
        // 不缓存整棵窗口树，避免驱动/远程桌面下的大位图缓存转场黑块。
        element.SetCurrentValue(UIElement.OpacityProperty, toOpacity);
        if (move != null) move.Y = toY;
        if (scale != null) scale.ScaleX = scale.ScaleY = toScale;
        if (!enabled) { onCompleted?.Invoke(); return; }

        var finished = false;
        void Finish()
        {
            if (finished || state.Generation != generation) return;
            finished = true;
            state.Fallback?.Stop();
            state.Fallback = null;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (move != null) move.BeginAnimation(TranslateTransform.YProperty, null);
            if (scale != null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            onCompleted?.Invoke();
        }
        var fade = Animation(fromOpacity, toOpacity, duration, toOpacity < fromOpacity);
        fade.Completed += (_, _) => Finish();
        if (move != null)
            move.BeginAnimation(TranslateTransform.YProperty, Animation(fromY, toY, duration, toOpacity < fromOpacity));
        if (scale != null)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Animation(fromScale, toScale, duration, false));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Animation(fromScale, toScale, duration, false));
        }
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        // 渲染时钟暂停时也能收尾；新请求会取消旧回调。
        state.Fallback = new DispatcherTimer(DispatcherPriority.Background, element.Dispatcher)
            { Interval = duration + TimeSpan.FromMilliseconds(150) };
        state.Fallback.Tick += (_, _) => Finish();
        state.Fallback.Start();
    }
    public static void FadeOut(UIElement element, Action? onCompleted = null, double from = 1.0)
        => Run(element, from, 0, TimeSpan.FromMilliseconds(180), UiEnabled, onCompleted: onCompleted);
    /// <summary>弹出层（右键菜单/子菜单）专用：只淡入，不位移不缩放，且必须在显示的同一时刻同步调用。
    /// 弹出层所在的透明窗口尺寸在布局时就定死了，RenderTransform 位移/缩放会被窗口边缘裁掉，
    /// 看起来就是"开到一半卡住再突然出现"；延后一帧再启动则会先闪出完整菜单再重播。</summary>
    public static void PopupFadeIn(UIElement element)
        => Run(element, 0, 1, TimeSpan.FromMilliseconds(140), UiEnabled);
    public static void FadeIn(UIElement element, double to = 1.0, Action? onCompleted = null)
        => Run(element, 0, to, TimeSpan.FromMilliseconds(220), UiEnabled, onCompleted: onCompleted);
    public static void CrossFade(UIElement element, Action applyChange, double to = 1.0)
    {
        if (!UiEnabled) { Reset(element); applyChange(); return; }
        FadeOut(element, () => { applyChange(); FadeIn(element, to); }, element.Opacity);
    }
    public static void FadeInMove(UIElement element, TranslateTransform move, double offset = 16, Action? onCompleted = null)
        => Run(element, 0, 1, TimeSpan.FromMilliseconds(300), UiEnabled,
            move: move, fromY: offset, onCompleted: onCompleted);
    public static void FadeOutMove(UIElement element, TranslateTransform move, double offset = 16, Action? onCompleted = null)
        => Run(element, element.Opacity, 0, TimeSpan.FromMilliseconds(260), UiEnabled,
            move: move, fromY: move.Y, toY: offset, onCompleted: onCompleted);
    public static void SettleScale(UIElement element, ScaleTransform scale, Action? onCompleted = null)
        => Run(element, 0.88, 1, TimeSpan.FromMilliseconds(320), UiEnabled,
            scale: scale, fromScale: 0.975, onCompleted: onCompleted);
    public static void BeforeWindowStateChange(UIElement element, ScaleTransform scale, Action applyChange)
        => Run(element, element.Opacity, 0.88, TimeSpan.FromMilliseconds(130), UiEnabled,
            scale: scale, fromScale: scale.ScaleX, toScale: 0.985, onCompleted: applyChange);
    public static void PageEntrance(UIElement element, TranslateTransform move, ScaleTransform scale)
        => Run(element, 0, 1, TimeSpan.FromMilliseconds(320), PageEnabled,
            move: move, fromY: 18, scale: scale, fromScale: 0.97);
    public static void Entrance(FrameworkElement element, bool page = true)
    {
        var transforms = Entrances.GetValue(element, target =>
        {
            var result = new EntranceTransforms();
            var group = new TransformGroup();
            if (target.RenderTransform != null && target.RenderTransform != Transform.Identity)
                group.Children.Add(target.RenderTransform);
            group.Children.Add(result.Scale);
            group.Children.Add(result.Move);
            target.RenderTransform = group;
            target.RenderTransformOrigin = new Point(0.5, 0.5);
            return result;
        });
        Run(element, 0, 1, TimeSpan.FromMilliseconds(page ? 320 : 180), page ? PageEnabled : UiEnabled,
            move: transforms.Move, fromY: page ? 18 : 6, scale: transforms.Scale,
            fromScale: page ? 0.97 : 0.985);
    }
}
