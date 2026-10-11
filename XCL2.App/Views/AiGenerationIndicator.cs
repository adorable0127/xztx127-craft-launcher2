using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Three live generation dots; clocks are removed when hidden/unloaded or animations are disabled.</summary>
public sealed class AiGenerationIndicator : Border
{
    private readonly Ellipse[] _dots;
    public AiGenerationIndicator()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _dots = Enumerable.Range(0, 3).Select(_ => new Ellipse { Width = 5, Height = 5, Margin = new Thickness(2, 0, 2, 0), Fill = Brushes.MediumSeaGreen }).ToArray();
        foreach (var dot in _dots) { dot.SetResourceReference(Shape.FillProperty, "AccentBrush"); row.Children.Add(dot); }
        Child = row;
        Loaded += (_, _) => { AnimationSettingsService.Changed -= Refresh; AnimationSettingsService.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { AnimationSettingsService.Changed -= Refresh; Stop(); };
        IsVisibleChanged += (_, _) => Refresh();
    }
    private void Stop() { foreach (var dot in _dots) { dot.BeginAnimation(OpacityProperty, null); dot.Opacity = .65; } }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        Stop();
        if (!IsLoaded || !IsVisible || !AnimationSettingsService.IsEnabled(AnimationKind.Loading) || !SystemParameters.ClientAreaAnimation) return;
        var duration = AnimationSettingsService.Duration(520, AnimationKind.Loading);
        for (int i = 0; i < _dots.Length; i++)
            _dots[i].BeginAnimation(OpacityProperty, new DoubleAnimation(.2, 1, duration)
            { BeginTime = TimeSpan.FromMilliseconds(i * 130), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
}
