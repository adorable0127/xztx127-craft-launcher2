using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>搜索定位的临时描边独立于控件样式，结束时不会覆盖绑定、背景或已有特效。</summary>
internal sealed class FeatureSearchHighlight : IDisposable
{
    private FrameworkElement? _target;
    private AdornerLayer? _layer;
    private OutlineAdorner? _adorner;
    private DispatcherTimer? _timer;

    public FeatureSearchHighlight(FrameworkElement target)
    {
        _target = target;
        _layer = AdornerLayer.GetAdornerLayer(target);
        if (_layer == null) { _target = null; return; }
        _adorner = new OutlineAdorner(target);
        _layer.Add(_adorner);
        target.Unloaded += Target_Unloaded;
        _timer = new DispatcherTimer(DispatcherPriority.Background, target.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Target_Unloaded(object sender, RoutedEventArgs e) => Dispose();
    private void Timer_Tick(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer = null;
        }
        if (_target != null) _target.Unloaded -= Target_Unloaded;
        if (_layer != null && _adorner != null) _layer.Remove(_adorner);
        _target = null;
        _layer = null;
        _adorner = null;
    }

    private sealed class OutlineAdorner : Adorner
    {
        public OutlineAdorner(FrameworkElement target) : base(target)
        {
            IsHitTestVisible = false;
            IsClipEnabled = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            var brush = (AdornedElement as FrameworkElement)?.TryFindResource("AccentBrush") as Brush
                ?? Brushes.DodgerBlue;
            var bounds = new Rect(-3, -3, size.Width + 6, size.Height + 6);
            drawingContext.DrawRoundedRectangle(null, new Pen(brush, 2.5), bounds, 6, 6);
        }
    }
}
