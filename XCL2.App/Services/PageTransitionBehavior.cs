using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Services;

public static class PageTransitionBehavior
{
    public static readonly DependencyProperty AnimateOnVisibleProperty = DependencyProperty.RegisterAttached(
        "AnimateOnVisible", typeof(bool), typeof(PageTransitionBehavior), new PropertyMetadata(false, OnVisibleBehaviorChanged));
    public static bool GetAnimateOnVisible(DependencyObject target) => (bool)target.GetValue(AnimateOnVisibleProperty);
    public static void SetAnimateOnVisible(DependencyObject target, bool value) => target.SetValue(AnimateOnVisibleProperty, value);
    /// <summary>弹出层（ContextMenu / 子菜单）专用：每次变为可见时同步淡入一次，不位移不缩放，
    /// 不监听 Loaded（避免与 IsVisibleChanged 重复触发导致动画重播）。</summary>
    public static readonly DependencyProperty PopupFadeOnVisibleProperty = DependencyProperty.RegisterAttached(
        "PopupFadeOnVisible", typeof(bool), typeof(PageTransitionBehavior), new PropertyMetadata(false, OnPopupFadeChanged));
    public static bool GetPopupFadeOnVisible(DependencyObject target) => (bool)target.GetValue(PopupFadeOnVisibleProperty);
    public static void SetPopupFadeOnVisible(DependencyObject target, bool value) => target.SetValue(PopupFadeOnVisibleProperty, value);
    private static void OnPopupFadeChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += PopupFade_IsVisibleChanged;
            element.Unloaded += Popup_Unloaded;
        }
        else
        {
            element.IsVisibleChanged -= PopupFade_IsVisibleChanged;
            element.Unloaded -= Popup_Unloaded;
            UiAnimationHelper.Reset(element);
        }
    }
    private static void Popup_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) UiAnimationHelper.Reset(element);
    }
    private static void PopupFade_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)e.NewValue) UiAnimationHelper.PopupFadeIn(element);
        else UiAnimationHelper.Reset(element);
    }
    public static readonly DependencyProperty AnimateSelectionProperty = DependencyProperty.RegisterAttached(
        "AnimateSelection", typeof(bool), typeof(PageTransitionBehavior), new PropertyMetadata(false, OnSelectionBehaviorChanged));
    public static bool GetAnimateSelection(DependencyObject target) => (bool)target.GetValue(AnimateSelectionProperty);
    public static void SetAnimateSelection(DependencyObject target, bool value) => target.SetValue(AnimateSelectionProperty, value);
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool), typeof(PageTransitionBehavior), new PropertyMetadata(false, OnExpandedChanged));
    public static bool GetIsExpanded(DependencyObject target) => (bool)target.GetValue(IsExpandedProperty);
    public static void SetIsExpanded(DependencyObject target, bool value) => target.SetValue(IsExpandedProperty, value);
    private static readonly DependencyProperty VisibleCyclePreparedProperty = DependencyProperty.RegisterAttached(
        "VisibleCyclePrepared", typeof(bool), typeof(PageTransitionBehavior), new PropertyMetadata(false));

    private static void OnVisibleBehaviorChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.Loaded += Element_Loaded;
            element.IsVisibleChanged += Element_IsVisibleChanged;
            element.Unloaded += Element_Unloaded;
            if (element.IsVisible) PrepareVisibleEntrance(element);
        }
        else
        {
            element.Loaded -= Element_Loaded;
            element.IsVisibleChanged -= Element_IsVisibleChanged;
            element.Unloaded -= Element_Unloaded;
            Cancel(element);
        }
    }
    private static void Element_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { IsVisible: true } element) return;
        // Loaded 与 IsVisibleChanged 属于同一次入树过程，不能重新启动第二个淡入。
        // 初次挂载时若主页面已经准备了过渡，由父级统一带动，避免透明度/位移叠加。
        if (UiAnimationHelper.HasActivePageAncestor(element))
        {
            UiAnimationHelper.Reset(element);
            element.SetValue(VisibleCyclePreparedProperty, true);
            return;
        }
        PrepareVisibleEntrance(element);
    }
    private static void Element_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)e.NewValue) PrepareVisibleEntrance(element); else Cancel(element);
    }
    private static void Element_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) Cancel(element);
    }
    private static void Cancel(FrameworkElement element)
    {
        element.SetValue(VisibleCyclePreparedProperty, false);
        UiAnimationHelper.Reset(element);
    }
    private static void PrepareVisibleEntrance(FrameworkElement element)
    {
        if ((bool)element.GetValue(VisibleCyclePreparedProperty)) return;
        element.SetValue(VisibleCyclePreparedProperty, true);
        PrepareEntrance(element);
    }
    private static void PrepareEntrance(FrameworkElement element)
    {
        if (UiAnimationHelper.HasActivePageAncestor(element))
        {
            UiAnimationHelper.Reset(element);
            return;
        }
        // 在可见事件内先设置起点；调度等待只负责开播，不再延后把已显示的内容拉回起点。
        UiAnimationHelper.PrepareEntrance(element);
        UiAnimationHelper.StartPreparedEntrance(element);
    }
    private static void OnSelectionBehaviorChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TabControl tabs) return;
        if ((bool)e.NewValue)
        {
            tabs.SelectionChanged += Tabs_SelectionChanged;
            tabs.Loaded += Tabs_Loaded;
            tabs.Unloaded += Tabs_Unloaded;
        }
        else
        {
            tabs.SelectionChanged -= Tabs_SelectionChanged;
            tabs.Loaded -= Tabs_Loaded;
            tabs.Unloaded -= Tabs_Unloaded;
            CancelSelection(tabs);
        }
    }
    private static void Tabs_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TabControl tabs) CancelSelection(tabs);
    }
    private static void Tabs_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TabControl tabs) PrepareSelectedContent(tabs);
    }
    private static void CancelSelection(TabControl tabs)
    {
        if (tabs.Template?.FindName("PART_SelectedContentHost", tabs) is FrameworkElement content)
            Cancel(content);
    }
    private static void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs || !ReferenceEquals(e.OriginalSource, tabs)) return;
        PrepareSelectedContent(tabs);
    }
    private static void PrepareSelectedContent(TabControl tabs)
    {
        if (!tabs.IsLoaded || !tabs.IsVisible) return;
        tabs.ApplyTemplate();
        if (tabs.Template?.FindName("PART_SelectedContentHost", tabs) is FrameworkElement content)
            PrepareEntrance(content);
    }
    private static void OnExpandedChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        Cancel(element);
        if ((bool)e.NewValue)
        {
            element.Visibility = Visibility.Visible;
            // 首次生成分组时由整份列表入场；逐个子组再淡入会叠加大量时钟。
            if (element.IsLoaded) PrepareEntrance(element);
        }
        else if (!element.IsLoaded || !element.IsVisible)
            element.Visibility = Visibility.Collapsed;
        else
            UiAnimationHelper.FadeOut(element, () =>
            {
                if (!GetIsExpanded(element)) element.Visibility = Visibility.Collapsed;
            }, element.Opacity, kind: AnimationKind.Page);
    }
}
