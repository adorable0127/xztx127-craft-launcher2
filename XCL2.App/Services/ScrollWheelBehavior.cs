using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XCL2.App.Services;

/// <summary>
/// 全局提升 ScrollViewer 的鼠标滚轮滚动灵敏度。
///
/// 问题根因：WPF ScrollViewer 处理 MouseWheel 事件时默认按
/// SystemParameters.WheelScrollLines（Windows 系统设置里的"每次滚动的行数"，
/// 常见默认值是 3）换算成像素距离来滚动，而这个"行"是按当前字体的行高估算的——
/// 在「设置」页这种控件又多又密、内容总高度远超视窗高度的长页面里，3 行的像素距离
/// 相对页面总长度小得可怜，导致用户疯狂划滚轮却感觉"基本滑不动"。
///
/// 修法：拦截 PreviewMouseWheel，改成固定的、更大的像素步长，并且不再用同步的
/// ScrollToVerticalOffset 直接跳转，而是用 DoubleAnimation 平滑过渡到目标偏移
/// （原因见下面 ScrollViewer_PreviewMouseWheel 里的详细注释：直接跳转会在设置页这类
/// 控件密集的长页面上引起明显卡顿）。这里没有新建 UserControl 或者到处手写事件订阅，
/// 而是用附加属性(Attached Property)：只要在 XAML 里给 ScrollViewer 加一个
/// ScrollWheelBehavior.EnableFastWheel="True"，就能直接获得这个效果，不需要碰任何
/// .xaml.cs 代码。
/// </summary>
public static class ScrollWheelBehavior
{
    /// <summary>单次滚轮"咔哒"一格对应的滚动像素距离。默认系统行为大约只有 45~60px
    /// 左右（3 行 × 约 15~20px 行高），这里直接给到 180px，大约是原来的 3~4 倍，
    /// 明显感觉"划一下就能走一大截"，但也没有大到"划一下直接冲到底"那么夸张。</summary>
    public static double WheelStepPixels { get; set; } = 180;

    public static readonly DependencyProperty EnableFastWheelProperty =
        DependencyProperty.RegisterAttached(
            "EnableFastWheel",
            typeof(bool),
            typeof(ScrollWheelBehavior),
            new PropertyMetadata(false, OnEnableFastWheelChanged));

    public static void SetEnableFastWheel(DependencyObject element, bool value) =>
        element.SetValue(EnableFastWheelProperty, value);

    public static bool GetEnableFastWheel(DependencyObject element) =>
        (bool)element.GetValue(EnableFastWheelProperty);

    private static void OnEnableFastWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;

        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
        }
        else
        {
            sv.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
            sv.ClearValue(TargetOffsetProperty);
            sv.BeginAnimation(ScrollViewerOffsetHelper.VerticalOffsetProperty, null);
        }
    }

    /// <summary>记录"这次连续滚动最终应该停在哪个像素偏移"，把同一串快速滚轮事件的多次
    /// Delta 累加到同一个目标值上，而不是每格都独立起播一个新动画、互相打断重来。</summary>
    private static readonly DependencyProperty TargetOffsetProperty =
        DependencyProperty.RegisterAttached("TargetOffset", typeof(double), typeof(ScrollWheelBehavior),
            new PropertyMetadata(double.NaN));

    private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // 根因：之前这里直接调用 sv.ScrollToVerticalOffset(newOffset)——这是同步的、
        // 立即完成的一次性跳转，每次 MouseWheel 事件（快速划动时一秒能触发几十次）都会
        // 各自强制走一次完整的布局(layout)+排版(arrange)。设置页控件多、内容高，一次布局
        // 本身就比普通页面贵，几十次/秒的同步布局叠加起来，表现就是划得越快、越感觉"卡顿"。
        //
        // 修法：改用 WPF 动画系统的 DoubleAnimation 做位移，把"目标偏移量"记在附加属性里
        // 累加——同一串连续滚动只更新一次动画目标而不是每格都重新触发同步布局；动画由渲染
        // 线程驱动插值，过渡更平滑，也不会阻塞 UI 线程。
        var current = double.IsNaN((double)sv.GetValue(TargetOffsetProperty))
            ? sv.VerticalOffset
            : (double)sv.GetValue(TargetOffsetProperty);

        var steps = e.Delta / 120.0;
        var target = current - steps * WheelStepPixels;

        // 提前 Clamp，避免动画播到一半才发现越界又反向纠正，出现"回弹"的观感。
        target = Math.Max(0, Math.Min(target, sv.ScrollableHeight));

        sv.SetValue(TargetOffsetProperty, target);

        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        // 动画结束清掉目标缓存，避免下一次全新的滚动误用上一次滚到底后残留的旧目标值。
        animation.Completed += (_, _) => sv.ClearValue(TargetOffsetProperty);

        sv.BeginAnimation(ScrollViewerOffsetHelper.VerticalOffsetProperty, animation);
        e.Handled = true;
    }
}

/// <summary>
/// ScrollViewer.VerticalOffset 本身是只读属性，没法直接作为动画目标（DoubleAnimation 需要
/// 一个可写的依赖属性）。这里用一个"影子"依赖属性，PropertyChanged 回调里转调用
/// ScrollToVerticalOffset，间接实现"能被动画驱动的滚动偏移"——这是 WPF 里给 ScrollViewer
/// 做平滑滚动动画的标准写法。
/// </summary>
internal static class ScrollViewerOffsetHelper
{
    public static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset",
            typeof(double),
            typeof(ScrollViewerOffsetHelper),
            new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
            sv.ScrollToVerticalOffset((double)e.NewValue);
    }
}
