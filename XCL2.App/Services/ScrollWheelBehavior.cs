using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
    /// <summary>100% 灵敏度时单次滚轮"咔哒"一格对应的基准滚动像素距离。
    /// 旧版本固定使用 180px；现在默认灵敏度为 90%，实际默认步长约 162px，
    /// 仍明显快于 WPF 原生滚动，但比旧版稍微收敛一点。</summary>
    public const double BaseWheelStepPixels = 180;

    public const int MinSensitivityPercent = 50;
    public const int MaxSensitivityPercent = 200;
    public const int DefaultSensitivityPercent = 90;

    private static int _sensitivityPercent = DefaultSensitivityPercent;

    /// <summary>当前全局滚轮灵敏度百分比。</summary>
    public static int SensitivityPercent => _sensitivityPercent;

    /// <summary>当前实际使用的单格像素步长。</summary>
    public static double WheelStepPixels => BaseWheelStepPixels * _sensitivityPercent / 100.0;

    public static int ClampSensitivityPercent(int percent) =>
        Math.Clamp(percent, MinSensitivityPercent, MaxSensitivityPercent);

    /// <summary>设置全局滚轮灵敏度。设置页拖动滑块时可直接调用做实时预览。</summary>
    public static void SetSensitivityPercent(int percent) =>
        _sensitivityPercent = ClampSensitivityPercent(percent);

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

    public static readonly DependencyProperty ContainWheelProperty =
        DependencyProperty.RegisterAttached(
            "ContainWheel",
            typeof(bool),
            typeof(ScrollWheelBehavior),
            new PropertyMetadata(false, OnContainWheelChanged));

    public static void SetContainWheel(DependencyObject element, bool value) =>
        element.SetValue(ContainWheelProperty, value);

    public static bool GetContainWheel(DependencyObject element) =>
        (bool)element.GetValue(ContainWheelProperty);

    /// <summary>
    /// 抽屉/展开式设置区域可以把 ContainWheel 设为 true：鼠标位于该区域时，滚轮事件只留在
    /// 这个局部交互区，不再向外层长页面继续传递。这样用户还在抽屉里选颜色/调参数时，
    /// 不会因为滚轮把整个设置页一起带走。外层 PreviewMouseWheel 会先识别这个边界并放行，
    /// 到冒泡阶段再由边界把事件截住，因此内部真正需要滚动的子控件仍有机会先处理事件。
    /// </summary>
    private static void OnContainWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue)
            element.MouseWheel += ContainedElement_MouseWheel;
        else
            element.MouseWheel -= ContainedElement_MouseWheel;
    }

    private static void ContainedElement_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

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

        // 修复"设置主题选择页里，滚动字体/主题这类长下拉列表（抽屉）时，整个设置页会跟着
        // 一起跳动，并且被误判成正在修改设置从而打断用户进度"：
        //
        // 根因：ComboBox 的下拉列表用 Popup 承载，Popup 的内容在渲染上属于独立的顶层窗口
        // （见下面 CloseAnyOpenComboBoxDropdown 的注释），但 WPF 对 Popup 的路由事件做了
        // "缝合"——Popup 内容的逻辑父级仍然是 PlacementTarget（这里的 ComboBox），所以
        // PreviewMouseWheel 这种隧道事件，会先经过 Popup 外层、挂在设置页最外层 ScrollViewer
        // 上的这个处理器，然后才轮到 Popup 内部自己的列表 ScrollViewer。
        //
        // 而下面紧接着的 CloseAnyOpenComboBoxDropdown(sv) 原本是无条件执行的：只要滚轮事件
        // 路过这个外层 ScrollViewer，不管来源是不是就在这个展开的下拉列表本身里面，都会先把
        // 它关掉。于是用户想在字体列表这种几十项的长下拉里滚轮翻页时，第一下滚轮就会把
        // 下拉框直接关掉——下拉一关，事件来源(originalSource)所在的视觉子树跟着被拆掉，
        // 后面 HasScrollableAncestorBefore 沿视觉树往上找"内层可滚动祖先"时自然再也找不到
        // 刚刚已经被摘掉的那个下拉列表 ScrollViewer，只能一路找到外层，于是转而滚动了外层的
        // 设置长页面本身——表现出来就是"抽屉/下拉列表滚动时，全屏（外层大页面）跟着一起动"。
        // 同时某些 ComboBox 模板会在 IsDropDownOpen 被程序改成 false 时，把当前鼠标悬停/
        // 高亮的那一项提交为选中项，触发 SelectionChanged，从而被设置页的脏检测誤判为
        // "用户刚刚修改了一项设置"，打断用户原本只是想浏览选项、还没做决定的操作进度。
        //
        // 修法：在关闭下拉框之前，先判断这次滚轮事件是不是恰好来自某个仍处于展开状态的
        // Popup 内部（用 PresentationSource 是否与外层 ScrollViewer 一致来判断，Popup 内容
        // 的呈现源必然与主窗口不同）。如果是，说明用户正在滚动的就是这个下拉列表本身，
        // 这里什么都不做、直接放行，让隧道事件继续往下走到 Popup 内部自己的 ScrollViewer，
        // 由它按标准逻辑接管滚动；既不关闭下拉框，也不会让外层设置页跟着移动。只有当滚轮
        // 事件来自页面上其它地方（不是正在滚动的这个下拉列表自己）时，才继续走下面"顺手
        // 收起其它还开着的下拉框，避免其 Popup 跟丢定位"的原有逻辑。
        if (e.OriginalSource is DependencyObject popupSource && IsInsideOpenPopup(popupSource, sv))
        {
            return;
        }

        // 抽屉/展开式局部区域声明了 ContainWheel 时，外层 ScrollViewer 不允许抢这次滚轮。
        // 这里只 return、不设 Handled，给区域内部控件先处理；若内部没有处理，冒泡到该区域本身
        // 时 ContainedElement_MouseWheel 会截断，确保事件不会继续滚动整个外层页面。
        if (e.OriginalSource is DependencyObject containedSource &&
            IsInsideContainWheelBoundary(containedSource, sv))
        {
            return;
        }

        // 修复"下拉框展开时滚动页面，弹出的选项列表跟丢在原地、跟真正的下拉框错位分开"：
        // 根因见下面 DoubleAnimation 那段——ComboBox 的下拉 Popup 是独立的顶层窗口，靠监听
        // PlacementTarget 的 LayoutUpdated 事件同步重新定位；同步的 ScrollToVerticalOffset
        // 每次都会立刻触发一次 LayoutUpdated，Popup 跟得上，但这里为了解决"滚轮滑不动"改成了
        // DoubleAnimation 动画过渡滚动，动画插值帧由渲染线程直接推动，不保证每帧都可靠触发
        // Popup 重新定位，于是出现"页面滚动画面已经在动了，下拉框的浮层却停在旧位置没跟上"
        // 这种视觉分离。原生控件的通行做法本来就是"一滚动就该收起下拉框"，所以这里在真正
        // 开始滚动之前，先把视觉树里任何还展开着的 ComboBox 下拉收起来，从根源上避免这种
        // 半途而废的错位状态出现，而不是事后去修正 Popup 的位置。
        CloseAnyOpenComboBoxDropdown(sv);

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

    /// <summary>递归遍历视觉树，找到当前展开着(IsDropDownOpen=true)的 ComboBox 就把它收起来。
    /// 设置页这类长列表页面上同一时间通常最多只有一个下拉框是展开状态，但这里不假设"最多一个"，
    /// 找到的每一个都会收起——万一有嵌套/自定义控件内部各自持有一个 ComboBox，也能一并处理，
    /// 不会因为遗漏第二个而留下同样的错位问题。</summary>
    private static void CloseAnyOpenComboBoxDropdown(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox { IsDropDownOpen: true } combo)
            {
                combo.IsDropDownOpen = false;
            }
            CloseAnyOpenComboBoxDropdown(child);
        }
    }

    /// <summary>判断这次滚轮事件的原始来源(source)是不是身处某个当前展开着的 Popup（下拉列表、
    /// 展开式面板等）内部、且这个 Popup 是挂在 outer 这个外层 ScrollViewer 下面某个控件上的。
    /// 判断依据：Popup 内容单独渲染在自己的呈现源(PresentationSource)里，只是逻辑父级仍然
    /// 指回主窗口这一侧的 PlacementTarget——所以只要 source 的呈现源跟 outer 的呈现源不是
    /// 同一个，就说明 source 当前正处在某个展开的 Popup 内容里面，而不是主窗口可视树本身。
    /// 用 PresentationSource 判断而不是单纯沿视觉树 GetParent 往上爬，是因为视觉树在 Popup
    /// 边界处本来就是断开的（爬不回 outer），没法像 IsInsideContainWheelBoundary 那样直接
    /// 复用同一种"往上找"的写法。</summary>
    private static bool IsInsideOpenPopup(DependencyObject source, ScrollViewer outer)
    {
        if (source is not Visual sourceVisual) return false;

        var sourceRoot = PresentationSource.FromVisual(sourceVisual);
        var outerRoot = PresentationSource.FromVisual(outer);

        // 两者呈现源不同，说明 source 位于一个独立呈现的 Popup 内容树里（下拉列表、
        // 展开式浮层等），而不是主窗口本身这一支可视树上。
        return sourceRoot != null && !ReferenceEquals(sourceRoot, outerRoot);
    }

    private static bool IsInsideContainWheelBoundary(DependencyObject source, ScrollViewer outer)
    {
        var current = source;
        while (current != null && !ReferenceEquals(current, outer))
        {
            if (GetContainWheel(current))
                return true;

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
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
