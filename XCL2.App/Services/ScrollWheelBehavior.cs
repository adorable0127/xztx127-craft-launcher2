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

        // 修复"内嵌 ListBox/列表控件里滚轮不起作用"：这个事件处理器订阅的是 PreviewMouseWheel，
        // 而 Preview 系列事件是"隧道"路由——从最外层的元素开始，一路向下传到鼠标实际所在的
        // 那个子控件，沿途每一层都会先经过这里。之前的写法不管鼠标在哪儿，只要事件路过这个
        // 外层 ScrollViewer 就无条件 e.Handled = true 并滚动"外层"的偏移量——这样一来，事件
        // 根本传不到内层的 ListBox（比如 Java 列表、已安装版本列表）自己的 ScrollViewer 那里，
        // 内层列表自身永远收不到 MouseWheel，表现就是"鼠标悬停在这些列表上划滚轮，列表纹丝
        // 不动"（而外层大页面又可能因为这一小块区域根本没跨越可视边界，看起来跟没反应一样）。
        //
        // 修法：先检查鼠标当前是否正处于某个"自己能滚、并且还没滚到头"的内层滚动控件之上
        // （ListBox/ListView/ComboBox 等自带 ScrollViewer 的控件，或用户手动嵌套的 ScrollViewer）。
        // 如果是，就把这次事件让给它自己处理（不设 e.Handled，交由路由继续走到 Bubble 阶段，
        // 内层控件的默认滚动逻辑会接管），外层这里直接 return，不抢它的滚动。只有当鼠标不在
        // 任何内层可滚动控件上、或者内层已经滚到顶/底再滚不动了，才由外层这个 ScrollViewer
        // 接管，实现类似浏览器"子容器滚到头之后，继续滚动带动外层页面"的直觉行为。
        if (e.OriginalSource is DependencyObject source && HasScrollableAncestorBefore(source, sv, e.Delta))
        {
            return;
        }

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

    /// <summary>从事件真正的来源(originalSource)往上找，直到碰到外层这个 ScrollViewer(outer)为止——
    /// 途中如果先遇到一个自己内部还能再滚(没滚到顶/底)的可滚动控件，就返回 true，表示这次滚轮
    /// 应该交给那个内层控件自己处理，外层不要抢。ListBox/ListView/ComboBox 等控件的默认模板里
    /// 都内嵌了一个 ScrollViewer，所以只需要找"内层 ScrollViewer"即可，不需要特判具体控件类型。</summary>
    private static bool HasScrollableAncestorBefore(DependencyObject source, ScrollViewer outer, int wheelDelta)
    {
        var current = source;
        while (current != null && !ReferenceEquals(current, outer))
        {
            if (current is ScrollViewer inner && !ReferenceEquals(inner, outer))
            {
                // wheelDelta > 0 是向上滚：只要还没到顶（VerticalOffset > 0）就还有空间可滚。
                // wheelDelta < 0 是向下滚：只要还没到底（ScrollableHeight - VerticalOffset 还有余量）。
                var canScrollUp = inner.VerticalOffset > 0.0;
                var canScrollDown = inner.VerticalOffset < inner.ScrollableHeight;
                if ((wheelDelta > 0 && canScrollUp) || (wheelDelta < 0 && canScrollDown))
                {
                    return true;
                }
                // 内层已经滚到头，滚不动了——不 return，继续往上找（让事件最终交给外层处理，
                // 实现"内层滚到底后自动带动外层继续滚"的效果）。
            }

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
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
