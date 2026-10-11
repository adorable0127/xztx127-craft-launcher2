using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>
/// 从右向左排列、宽度不够时向下换行的面板：第一个子元素在最右，依次往左摆；
/// 每一行都贴右边界。用于首页顶部的胶囊按钮条，缩小窗口时只会整体换行，不会错位或互相覆盖。
/// 子元素的 Margin 不用于间距，间距统一由 HorizontalSpacing / VerticalSpacing 控制。
/// </summary>
public sealed class RightAlignedWrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(RightAlignedWrapPanel),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(RightAlignedWrapPanel),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double HorizontalSpacing { get => (double)GetValue(HorizontalSpacingProperty); set => SetValue(HorizontalSpacingProperty, value); }
    public double VerticalSpacing { get => (double)GetValue(VerticalSpacingProperty); set => SetValue(VerticalSpacingProperty, value); }

    private List<List<UIElement>> BuildRows(double maxWidth)
    {
        var rows = new List<List<UIElement>>();
        var current = new List<UIElement>();
        double used = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var w = child.DesiredSize.Width;
            var need = current.Count == 0 ? w : used + HorizontalSpacing + w;
            if (current.Count > 0 && need > maxWidth)
            {
                rows.Add(current);
                current = new List<UIElement>();
                need = w;
            }
            current.Add(child);
            used = need;
        }
        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        var rows = BuildRows(maxWidth);
        double height = 0, width = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            double rowW = 0, rowH = 0;
            foreach (var c in rows[i]) { rowW += c.DesiredSize.Width; rowH = Math.Max(rowH, c.DesiredSize.Height); }
            rowW += HorizontalSpacing * (rows[i].Count - 1);
            width = Math.Max(width, rowW);
            height += rowH + (i > 0 ? VerticalSpacing : 0);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = BuildRows(finalSize.Width);
        double y = 0;
        foreach (var row in rows)
        {
            double rowH = row.Max(c => c.DesiredSize.Height);
            double x = finalSize.Width;
            foreach (var c in row)
            {
                x -= c.DesiredSize.Width;
                c.Arrange(new Rect(Math.Max(0, x), y + (rowH - c.DesiredSize.Height) / 2, c.DesiredSize.Width, c.DesiredSize.Height));
                x -= HorizontalSpacing;
            }
            y += rowH + VerticalSpacing;
        }
        return finalSize;
    }
}
