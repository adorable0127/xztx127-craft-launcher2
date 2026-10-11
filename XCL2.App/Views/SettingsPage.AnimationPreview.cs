using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private sealed record AnimationPreviewControl(Button Button, TranslateTransform Move, ScaleTransform Scale);

    private readonly Dictionary<AnimationKind, AnimationPreviewControl> _animationPreviewControls = new();
    private DispatcherTimer? _animationPreviewDebounceTimer;
    private AnimationKind? _pendingAnimationPreview;

    private static bool HasAutomaticAnimationPreview(AnimationKind kind)
        => kind is AnimationKind.WindowOpen or AnimationKind.WindowMinimize or AnimationKind.WindowRestore
            or AnimationKind.WindowFullscreen or AnimationKind.WindowClose or AnimationKind.Page
            or AnimationKind.Popup or AnimationKind.Sidebar;

    private void AddAnimationPreview(Grid row, AnimationKind kind, string label)
    {
        if (!HasAutomaticAnimationPreview(kind)
            && kind is not AnimationKind.ControlFeedback and not AnimationKind.Toast) return;

        // 固定列宽和高度；预览只改变 RenderTransform/Opacity，不触发整页重新布局。
        if (row.ColumnDefinitions.Count == 3)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        var button = new Button
        {
            Content = kind == AnimationKind.ControlFeedback ? "点我体验" : "预览",
            Width = 64, Height = 24, MinWidth = 0, MinHeight = 0,
            Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(4, 1, 4, 1),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            ToolTip = kind switch
            {
                AnimationKind.ControlFeedback => "点击此按钮体验实际点击反馈，不改变设置。",
                AnimationKind.Toast => "显示一条通知，体验当前通知强度。",
                _ => $"在此按钮上预览{label}，不改变真实窗口状态；0% 时保持静止。"
            }
        };
        var move = new TranslateTransform();
        var scale = new ScaleTransform(1, 1);
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(move);
        button.RenderTransform = transform;
        _animationPreviewControls[kind] = new AnimationPreviewControl(button, move, scale);
        button.Click += (_, _) =>
        {
            _animationPreviewDebounceTimer?.Stop();
            _pendingAnimationPreview = null;
            PlayAnimationPreview(kind);
        };
        Grid.SetColumn(button, 3);
        row.Children.Add(button);
    }

    private void QueueAnimationPreview(AnimationKind kind)
    {
        if (!HasAutomaticAnimationPreview(kind) || !IsLoaded || !IsVisible) return;
        _animationPreviewDebounceTimer ??= CreateAnimationPreviewTimer();
        _animationPreviewDebounceTimer.Stop();
        _pendingAnimationPreview = kind;
        _animationPreviewDebounceTimer.Start();
    }

    private DispatcherTimer CreateAnimationPreviewTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            // 鼠标仍按在滑块上时等待释放；键盘调整也会在最后一次变化后合并为一段预览。
            if (AnimationSpeedSlider.IsMouseCaptureWithin) return;
            if (_pendingAnimationPreview is { } pending
                && _animationControls.TryGetValue(pending, out var controls)
                && controls.Intensity.IsMouseCaptureWithin) return;
            timer.Stop();
            var kind = _pendingAnimationPreview;
            _pendingAnimationPreview = null;
            if (kind.HasValue) PlayAnimationPreview(kind.Value);
        };
        return timer;
    }

    private void PlayAnimationPreview(AnimationKind kind)
    {
        if (!IsLoaded || !IsVisible || !_animationPreviewControls.TryGetValue(kind, out var preview)) return;
        var target = preview.Button;
        UiAnimationHelper.Reset(target, preview.Move, preview.Scale);
        if (!target.IsVisible || !target.IsEnabled) return;
        if (kind == AnimationKind.Toast)
        {
            // 0% 时通知仍应可见，只关闭动画；此入口只由明确点击触发。
            ToastService.ShowInfo("这是一条动画预览通知");
            return;
        }
        if (!AnimationSettingsService.IsEnabled(kind)) return;

        // 与真实入口共用同一强度/时长和过渡实现，禁止改变主窗口状态或循环播放。
        switch (kind)
        {
            case AnimationKind.WindowOpen:
                UiAnimationHelper.FadeInMove(target, preview.Move);
                break;
            case AnimationKind.WindowMinimize:
            case AnimationKind.WindowClose:
                UiAnimationHelper.FadeOutMove(target, preview.Move,
                    onCompleted: () => UiAnimationHelper.Reset(target, preview.Move, preview.Scale), kind: kind);
                break;
            case AnimationKind.WindowRestore:
            case AnimationKind.WindowFullscreen:
                UiAnimationHelper.SettleScale(target, preview.Scale, kind: kind);
                break;
            case AnimationKind.Page:
                UiAnimationHelper.Entrance(target);
                break;
            case AnimationKind.Popup:
                UiAnimationHelper.Entrance(target, page: false);
                break;
            case AnimationKind.Sidebar:
                UiAnimationHelper.CrossFade(target, () => { });
                break;
            // 普通按钮的鼠标/键盘事件已由 ControlFeedbackBehavior 处理，不合成另一段动画。
            case AnimationKind.ControlFeedback:
                break;
        }
    }

    private void StopAnimationPreviews()
    {
        _animationPreviewDebounceTimer?.Stop();
        _pendingAnimationPreview = null;
        foreach (var preview in _animationPreviewControls.Values)
            UiAnimationHelper.Reset(preview.Button, preview.Move, preview.Scale);
    }
}
