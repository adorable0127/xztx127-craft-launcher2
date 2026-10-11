using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>可打断的可见过渡；播放速度决定时长，分类强度决定淡入与位移幅度。</summary>
public static class UiAnimationHelper
{
    private sealed class Transition
    {
        public int Generation;
        public TranslateTransform? Move;
        public ScaleTransform? Scale;
        public DispatcherTimer? Fallback;
        public Action? Finish;
        public bool Prepared;
        public bool PageTransition;
        public bool Popup;
        public CancellationTokenSource? PreparationCancellation;
        public TaskCompletionSource<bool>? Settled;
        public AnimationKind Kind;
        public double Strength;
        public double Speed;
    }
    private sealed class EntranceTransforms
    {
        public readonly TranslateTransform Move = new();
        public readonly ScaleTransform Scale = new(1, 1);
    }
    private static readonly ConditionalWeakTable<UIElement, Transition> Transitions = new();
    private static readonly ConditionalWeakTable<FrameworkElement, EntranceTransforms> Entrances = new();
    private static readonly List<WeakReference<UIElement>> ActiveElements = new();

    static UiAnimationHelper() => AnimationSettingsService.Changed += FinishActiveTransitions;

    // 只收尾参数真的改变的分类。调通知/加载的滑杆不能打断页面等其它过渡。
    private static void FinishActiveTransitions()
    {
        ActiveElements.RemoveAll(weak => !weak.TryGetTarget(out _));
        var active = ActiveElements.ToArray();
        foreach (var weak in active)
        {
            if (!weak.TryGetTarget(out var element)) continue;
            void Finish()
            {
                if (!Transitions.TryGetValue(element, out var state)) return;
                if (state.Strength == AnimationSettingsService.Intensity(state.Kind)
                    && state.Speed == AnimationSettingsService.SpeedRatio) return;
                state.Finish?.Invoke();
            }
            if (element.Dispatcher.CheckAccess()) Finish();
            else if (!element.Dispatcher.HasShutdownStarted) element.Dispatcher.BeginInvoke((Action)Finish);
        }
    }

    public static void Reset(UIElement element, TranslateTransform? move = null, ScaleTransform? scale = null)
    {
        var state = Transitions.GetValue(element, _ => new Transition());
        state.Generation++;
        state.PreparationCancellation?.Cancel();
        state.PreparationCancellation?.Dispose();
        state.PreparationCancellation = null;
        state.Prepared = false;
        state.PageTransition = false;
        state.Settled?.TrySetResult(false);
        state.Settled = null;
        state.Finish = null;
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
        ActiveElements.RemoveAll(weak => !weak.TryGetTarget(out var target) || ReferenceEquals(target, element));
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

    private static void Run(UIElement element, double fromOpacity, double toOpacity, int milliseconds,
        AnimationKind kind, TranslateTransform? move = null, double fromY = 0, double toY = 0,
        ScaleTransform? scale = null, Action? onCompleted = null)
    {
        var strength = AnimationSettingsService.Intensity(kind);
        Reset(element, move, scale);
        var state = Transitions.GetValue(element, _ => new Transition());
        var generation = state.Generation;
        state.Move = move;
        state.Scale = scale;
        state.PageTransition = kind == AnimationKind.Page;
        state.Kind = kind;
        state.Strength = strength;
        state.Speed = AnimationSettingsService.SpeedRatio;
        state.Settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        element.SetCurrentValue(UIElement.OpacityProperty, toOpacity);
        if (move != null) move.Y = toY * strength;
        if (strength <= 0)
        {
            state.PageTransition = false;
            state.Settled.TrySetResult(true);
            onCompleted?.Invoke();
            return;
        }

        var duration = AnimationSettingsService.Duration(milliseconds, kind);
        var entering = toOpacity >= fromOpacity;
        // 强度只减弱可见的过渡，最终状态仍由调用者决定。退场完成与隐藏在同一回调。
        var animatedFrom = entering ? 1 - (1 - fromOpacity) * strength : fromOpacity;
        var animatedTo = entering ? toOpacity : fromOpacity + (toOpacity - fromOpacity) * strength;
        var finished = false;
        void Finish()
        {
            if (finished || state.Generation != generation) return;
            finished = true;
            state.Finish = null;
            state.Fallback?.Stop();
            state.Fallback = null;
            state.PageTransition = false;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (move != null) move.BeginAnimation(TranslateTransform.YProperty, null);
            ActiveElements.RemoveAll(weak => !weak.TryGetTarget(out var target) || ReferenceEquals(target, element));
            state.Settled?.TrySetResult(true);
            onCompleted?.Invoke();
        }
        state.Finish = Finish;
        ActiveElements.RemoveAll(weak => !weak.TryGetTarget(out var target) || ReferenceEquals(target, element));
        ActiveElements.Add(new WeakReference<UIElement>(element));
        var fade = Animation(animatedFrom, animatedTo, duration, !entering);
        fade.Completed += (_, _) => Finish();
        if (move != null && (fromY != 0 || toY != 0))
            move.BeginAnimation(TranslateTransform.YProperty, Animation(fromY * strength, toY * strength, duration, !entering));
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        // 渲染暂停时仍收尾；新过渡取消旧回调。完成后停止计时器并移除时钟。
        state.Fallback = new DispatcherTimer(DispatcherPriority.Background, element.Dispatcher)
            { Interval = duration + TimeSpan.FromMilliseconds(100) };
        state.Fallback.Tick += (_, _) => Finish();
        state.Fallback.Start();
    }
    public static void FadeOut(UIElement element, Action? onCompleted = null, double from = 1.0,
        AnimationKind kind = AnimationKind.Popup)
        => Run(element, from, 0, 160, kind, onCompleted: onCompleted);
    public static void PopupFadeIn(UIElement element)
        => Run(element, 0, 1, 220, AnimationKind.Popup);
    public static void FadeIn(UIElement element, double to = 1.0, Action? onCompleted = null,
        AnimationKind kind = AnimationKind.Popup)
        => Run(element, 0, to, 240, kind, onCompleted: onCompleted);
    public static void CrossFade(UIElement element, Action applyChange, double to = 1.0,
        AnimationKind kind = AnimationKind.Sidebar)
    {
        // 同步布局变更后播放一次完整淡入；无动画模式同步展示终态。
        Reset(element);
        applyChange();
        FadeIn(element, to, kind: kind);
    }
    public static void FadeInMove(UIElement element, TranslateTransform move, double offset = 14,
        Action? onCompleted = null, AnimationKind kind = AnimationKind.WindowOpen)
        => Run(element, 0.2, 1, 260, kind, move: move, fromY: offset, onCompleted: onCompleted);
    public static void FadeOutMove(UIElement element, TranslateTransform move, double offset = 10,
        Action? onCompleted = null, AnimationKind kind = AnimationKind.WindowClose)
        => Run(element, element.Opacity, kind == AnimationKind.WindowMinimize ? 0.25 : 0, 180, kind,
            move: move, fromY: move.Y, toY: offset, onCompleted: onCompleted);
    public static void SettleScale(UIElement element, ScaleTransform scale, Action? onCompleted = null,
        AnimationKind kind = AnimationKind.WindowRestore)
        => Run(element, kind == AnimationKind.WindowFullscreen ? 0.6 : 0.35, 1,
            kind == AnimationKind.WindowFullscreen ? 220 : 240, kind, scale: scale, onCompleted: onCompleted);
    public static void BeforeWindowStateChange(UIElement element, ScaleTransform scale, Action applyChange)
    {
        Reset(element, scale: scale);
        applyChange();
    }
    public static void PageEntrance(UIElement element, TranslateTransform move, ScaleTransform scale)
    {
        if (element is not FrameworkElement frameworkElement) return;
        PrepareEntrance(frameworkElement);
        StartPreparedEntrance(frameworkElement);
    }

    /// <summary>子面板首次随主页面一起挂载时，由父页面完成一段入场，避免透明度和位移叠乘。</summary>
    public static bool HasActivePageAncestor(FrameworkElement element)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(element) ?? element.Parent;
        while (parent != null)
        {
            if (parent is UIElement visual && Transitions.TryGetValue(visual, out var state) && state.PageTransition)
                return true;
            parent = parent is Visual || parent is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(parent) : LogicalTreeHelper.GetParent(parent);
        }
        return false;
    }

    private static EntranceTransforms GetEntranceTransforms(FrameworkElement element)
        => Entrances.GetValue(element, target =>
        {
            var result = new EntranceTransforms();
            var group = new TransformGroup();
            if (target.RenderTransform != null && target.RenderTransform != Transform.Identity)
                group.Children.Add(target.RenderTransform);
            group.Children.Add(result.Move);
            target.RenderTransform = group;
            return result;
        });

    /// <summary>挂载或显示内容前设定起点；此时不启动时钟，不消耗首布局阶段的动画时间。</summary>
    public static void PrepareEntrance(FrameworkElement element, bool page = true)
    {
        Reset(element);
        var kind = page ? AnimationKind.Page : AnimationKind.Popup;
        var strength = AnimationSettingsService.Intensity(kind);
        if (strength <= 0 || (page && HasActivePageAncestor(element))) return;
        var state = Transitions.GetValue(element, _ => new Transition());
        state.Prepared = true;
        state.PageTransition = page;
        state.Popup = !page;
        state.Kind = kind;
        state.Strength = strength;
        state.Speed = AnimationSettingsService.SpeedRatio;
        state.Move = page ? GetEntranceTransforms(element).Move : null;
        // 保持参与绘制，先建立可视化数据；加载覆盖层会遮住主页面的这一准备帧。
        element.SetCurrentValue(UIElement.OpacityProperty, Math.Max(0.001, 1 - strength));
        if (state.Move != null) state.Move.Y = 18 * strength;
        state.Finish = () => Reset(element);
        ActiveElements.Add(new WeakReference<UIElement>(element));
    }

    public static void StartPreparedEntrance(FrameworkElement element)
        => _ = StartPreparedEntranceAsync(element);

    /// <summary>等待首次布局与一次合成提交，再启动单次入场；任务在完成或被新导航打断后结束。</summary>
    public static async Task<bool> StartPreparedEntranceAsync(FrameworkElement element,
        Action? onStarting = null, CancellationToken cancellationToken = default)
    {
        if (!Transitions.TryGetValue(element, out var state) || !state.Prepared)
        {
            onStarting?.Invoke();
            return true;
        }
        if (state.PreparationCancellation != null) return false;
        var generation = state.Generation;
        var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.PreparationCancellation = preparation;
        var ready = await UiRenderScheduler.AfterRenderAsync(element, preparation.Token);
        if (state.Generation != generation) return false;
        if (preparation.IsCancellationRequested)
        {
            Reset(element);
            return false;
        }
        state.PreparationCancellation = null;
        preparation.Dispose();
        var popup = state.Popup;
        var move = state.Move;
        if (!ready || !element.IsLoaded || !element.IsVisible || (!popup && HasActivePageAncestor(element)))
        {
            Reset(element);
            onStarting?.Invoke();
            return true;
        }
        onStarting?.Invoke();
        Run(element, 0, 1, popup ? 220 : 320, popup ? AnimationKind.Popup : AnimationKind.Page,
            move: move, fromY: popup ? 0 : 18);
        return state.Settled == null || await state.Settled.Task;
    }
    public static void Entrance(FrameworkElement element, bool page = true)
    {
        // Popup HWND 的尺寸是固定的，位移会被窗口边缘裁剪，使用完整淡入。
        if (!page) { PopupFadeIn(element); return; }
        PrepareEntrance(element);
        StartPreparedEntrance(element);
    }
}
