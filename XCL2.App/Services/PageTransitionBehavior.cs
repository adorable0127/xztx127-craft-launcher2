using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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
        if ((bool)e.NewValue) element.IsVisibleChanged += PopupFade_IsVisibleChanged;
        else element.IsVisibleChanged -= PopupFade_IsVisibleChanged;
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
    private static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached(
        "Request", typeof(int), typeof(PageTransitionBehavior), new PropertyMetadata(0));

    private static void OnVisibleBehaviorChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.Loaded += Element_Loaded;
            element.IsVisibleChanged += Element_IsVisibleChanged;
            element.Unloaded += Element_Unloaded;
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
        if (sender is FrameworkElement { IsVisible: true } element) QueueEntrance(element);
    }
    private static void Element_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)e.NewValue) QueueEntrance(element); else Cancel(element);
    }
    private static void Element_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) Cancel(element);
    }
    private static void Cancel(FrameworkElement element)
    {
        element.SetValue(RequestProperty, (int)element.GetValue(RequestProperty) + 1);
        UiAnimationHelper.Reset(element);
    }
    private static void QueueEntrance(FrameworkElement element)
    {
        var request = (int)element.GetValue(RequestProperty) + 1;
        element.SetValue(RequestProperty, request);
        _ = element.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (element.IsLoaded && element.IsVisible && (int)element.GetValue(RequestProperty) == request)
                UiAnimationHelper.Entrance(element);
        }), DispatcherPriority.Loaded);
    }
    private static void OnSelectionBehaviorChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TabControl tabs) return;
        if ((bool)e.NewValue) tabs.SelectionChanged += Tabs_SelectionChanged;
        else tabs.SelectionChanged -= Tabs_SelectionChanged;
    }
    private static void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs || !ReferenceEquals(e.OriginalSource, tabs)) return;
        _ = tabs.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!tabs.IsLoaded || !tabs.IsVisible) return;
            tabs.ApplyTemplate();
            if (tabs.Template.FindName("PART_SelectedContentHost", tabs) is FrameworkElement content)
                QueueEntrance(content);
        }), DispatcherPriority.Loaded);
    }
    private static void OnExpandedChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        Cancel(element);
        if ((bool)e.NewValue)
        {
            element.Visibility = Visibility.Visible;
            QueueEntrance(element);
        }
        else if (!element.IsLoaded || !element.IsVisible)
            element.Visibility = Visibility.Collapsed;
        else
            UiAnimationHelper.FadeOut(element, () =>
            {
                if (!GetIsExpanded(element)) element.Visibility = Visibility.Collapsed;
            }, element.Opacity);
    }
}
