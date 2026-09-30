using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Services.Plugins;

public enum PluginComponentAction { Add, Move, Remove, Replace, Configure }

/// <summary>未指定的布局属性保持原值；Canvas 使用 WPF DIP 坐标。</summary>
public sealed record PluginComponentLayout
{
    public int? Row { get; init; }
    public int? Column { get; init; }
    public int? RowSpan { get; init; }
    public int? ColumnSpan { get; init; }
    public int? ZIndex { get; init; }
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double? Right { get; init; }
    public double? Bottom { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public Thickness? Margin { get; init; }
    public HorizontalAlignment? HorizontalAlignment { get; init; }
    public VerticalAlignment? VerticalAlignment { get; init; }
    public Dock? Dock { get; init; }
    public double? MinWidth { get; init; }
    public double? MinHeight { get; init; }
    public double? MaxWidth { get; init; }
    public double? MaxHeight { get; init; }
    public double? Opacity { get; init; }
    public Visibility? Visibility { get; init; }
    public bool? IsHitTestVisible { get; init; }
    public bool? ClipToBounds { get; init; }
    public Transform? RenderTransform { get; init; }
    public Point? RenderTransformOrigin { get; init; }
}

public sealed record PluginComponentDefinition
{
    public required string Id { get; init; }
    /// <summary>view:MainWindow、view:SettingsPage 或 dialog:某弹窗类名。</summary>
    public required string Target { get; init; }
    public PluginComponentAction Action { get; init; }
    /// <summary>组件 Name，或 Inspect 返回的 $root/0/1 路径；Add 时不需要。</summary>
    public string? Selector { get; init; }
    /// <summary>Add/Move 的容器选择器；$overlay 为自动创建、覆盖整个视图的绝对定位画布。</summary>
    public string? Parent { get; init; }
    public int Index { get; init; } = -1;
    public int Order { get; init; }
    public PluginComponentLayout Layout { get; init; } = new();
    /// <summary>Add/Replace 创建全新组件；可返回含自定义渲染后端的 WPF 宿主控件。</summary>
    public Func<PluginUiContext, FrameworkElement>? CreateContent { get; init; }
}

public sealed record PluginComponentInfo(string Path, string Name, string Type, string? ParentPath, double Width, double Height)
{
    public double X { get; init; }
    public double Y { get; init; }
    public int ZIndex { get; init; }
}

/// <summary>可撤销的组件树补丁；所有变更在 UI 线程串行执行，关闭插件时自动撤回。</summary>
public sealed class PluginComponentService : IDisposable
{
    private readonly PluginContext _context;
    private readonly Dictionary<string, PluginComponentDefinition> _definitions = new(StringComparer.Ordinal);
    private bool _disposed;
    internal PluginComponentService(PluginContext context) { _context = context; }
    internal PluginContext Context => _context;
    internal IEnumerable<PluginComponentDefinition> Definitions => _definitions.Values;

    public IDisposable Register(PluginComponentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Target);
        if (!definition.Target.StartsWith("view:", StringComparison.Ordinal) && !definition.Target.StartsWith("dialog:", StringComparison.Ordinal))
            throw new ArgumentException("Target 必须为 view:类名 或 dialog:类名。");
        if (!Enum.IsDefined(definition.Action) || definition.Index < -1) throw new ArgumentException("无效组件操作或索引。");
        if (definition.Action != PluginComponentAction.Add) ArgumentException.ThrowIfNullOrWhiteSpace(definition.Selector);
        if (definition.Action is PluginComponentAction.Add or PluginComponentAction.Move) ArgumentException.ThrowIfNullOrWhiteSpace(definition.Parent);
        if (definition.Action is PluginComponentAction.Add or PluginComponentAction.Replace) ArgumentNullException.ThrowIfNull(definition.CreateContent);
        ArgumentNullException.ThrowIfNull(definition.Layout);
        IDisposable? result = null;
        _context.Ui.Invoke(() =>
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PluginComponentService));
            if (!_definitions.TryAdd(definition.Id, definition)) throw new ArgumentException($"组件注册重复：{definition.Id}");
            PluginComponentRegistry.Add(this);
            result = new PluginRegistration(() => _context.Ui.Invoke(() =>
            {
                if (_definitions.Remove(definition.Id)) PluginComponentRegistry.Refresh();
            }));
        });
        return result!;
    }
    /// <summary>检查当前已经加载的视图。命名选择器须唯一；匿名控件使用返回的路径。</summary>
    public IReadOnlyList<PluginComponentInfo> Inspect(string target)
    {
        IReadOnlyList<PluginComponentInfo> result = Array.Empty<PluginComponentInfo>();
        _context.Ui.Invoke(() => result = PluginComponentRegistry.Roots().Where(r => PluginComponentRegistry.Matches(r, target))
            .SelectMany(r => PluginComponentRegistry.Tree(r).Select(n => new PluginComponentInfo(n.Path, n.Element.Name,
                n.Element.GetType().FullName ?? n.Element.GetType().Name, n.Parent, n.Element.ActualWidth, n.Element.ActualHeight)
                { X = PluginComponentRegistry.Position(n.Element, r).X, Y = PluginComponentRegistry.Position(n.Element, r).Y,
                    ZIndex = Panel.GetZIndex(n.Element) })).ToArray());
        return result;
    }
    /// <summary>宿主动态重建组件后可显式重新应用当前注册。</summary>
    public void Refresh() => _context.Ui.Invoke(PluginComponentRegistry.Refresh);

    public void Dispose() => _context.Ui.Invoke(() =>
    {
        if (_disposed) return;
        _disposed = true;
        _definitions.Clear();
        PluginComponentRegistry.Remove(this);
    });
}

internal sealed class PluginRegistration : IDisposable
{
    private Action? _dispose;
    internal PluginRegistration(Action dispose) { _dispose = dispose; }
    public void Dispose() => System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

internal static class PluginComponentRegistry
{
    internal sealed record Node(FrameworkElement Element, string Path, string? Parent);
    private sealed class Host
    {
        internal readonly FrameworkElement Root;
        internal readonly List<Action> Undo = new();
        internal Host(FrameworkElement root) { Root = root; }
    }
    private static readonly List<PluginComponentService> Services = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Host> Hosts = new();
    private static readonly List<WeakReference<Host>> HostRefs = new();
    private static bool _initialized;
    private static bool _refreshing;
    private static bool _queued;
    private static bool _changedDuringRefresh;
    internal static bool Matches(FrameworkElement root, string target) => target == "view:" + root.GetType().Name ||
        (root is Views.IOverlayDialog && target == "dialog:" + root.GetType().Name);

    internal static Point Position(FrameworkElement element, FrameworkElement root)
    {
        try { return element.TranslatePoint(new Point(), root); }
        catch (InvalidOperationException) { return new Point(); }
    }

    internal static IEnumerable<Node> Tree(FrameworkElement root)
    {
        var seen = new HashSet<FrameworkElement>();
        IEnumerable<Node> Walk(FrameworkElement element, string path, string? parent)
        {
            if (!seen.Add(element)) yield break;
            yield return new Node(element, path, parent);
            // 先逻辑子项，再模板可视子项；名称或路径歧义绝不静默取第一个。
            var children = LogicalTreeHelper.GetChildren(element).OfType<FrameworkElement>().ToList();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child && !children.Contains(child)) children.Add(child);
            for (var i = 0; i < children.Count; i++)
                foreach (var node in Walk(children[i], path + "/" + i, path)) yield return node;
        }
        return Walk(root, "$root", null);
    }
    internal static FrameworkElement[] Roots() => Application.Current == null ? Array.Empty<FrameworkElement>() :
        Application.Current.Windows.Cast<Window>().SelectMany(w => Tree(w)).Select(n => n.Element)
            .Where(e => e.IsLoaded && e is Window or UserControl or Page).Distinct().ToArray();
    internal static void Add(PluginComponentService service)
    {
        if (!_initialized)
        {
            _initialized = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) =>
                {
                    if (sender is FrameworkElement root && root is Window or UserControl or Page &&
                        Services.Any(s => s.Definitions.Any(d => Matches(root, d.Target))))
                    {
                        var isNew = !Hosts.TryGetValue(root, out _);
                        Attach(root);
                        if (isNew || Hosts.GetValue(root, r => new Host(r)).Undo.Count == 0) QueueRefresh();
                    }
                }));
        }
        if (!Services.Contains(service)) Services.Add(service);
        Refresh();
    }
    internal static void Remove(PluginComponentService service) { Services.Remove(service); Refresh(); }
    private static void Attach(FrameworkElement root)
    {
        if (Hosts.TryGetValue(root, out _)) return;
        var host = new Host(root);
        Hosts.Add(root, host);
        HostRefs.Add(new WeakReference<Host>(host));
        root.Unloaded += (_, _) => root.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (root.IsLoaded || _refreshing) return;
            Undo(host);
        }), DispatcherPriority.Background);
    }
    private static void QueueRefresh()
    {
        if (_queued || Application.Current == null) return;
        _queued = true;
        Application.Current.Dispatcher.BeginInvoke(new Action(() => { _queued = false; Refresh(); }), DispatcherPriority.Loaded);
    }
    internal static void Refresh()
    {
        if (_refreshing) { _changedDuringRefresh = true; return; }
        _refreshing = true;
        try
        {
            var roots = Roots();
            foreach (var root in roots.Where(r => Services.Any(s => s.Definitions.Any(d => Matches(r, d.Target))))) Attach(root);
            var hosts = HostRefs.Select(w => w.TryGetTarget(out var host) ? host : null).OfType<Host>().ToArray();
            HostRefs.RemoveAll(w => !w.TryGetTarget(out _));
            // 撤回顺序与应用顺序相反，允许多个插件按 Order 叠加修改同一组件。
            foreach (var host in Enumerable.Reverse(hosts)) Undo(host);
            foreach (var host in hosts.Where(h => h.Root.IsLoaded))
            {
                var definitions = Services.SelectMany(s => s.Definitions.Select(d => (Service: s, Definition: d)))
                    .Where(e => Matches(host.Root, e.Definition.Target)).OrderBy(e => e.Definition.Order)
                    .ThenBy(e => e.Service.Context.PluginId, StringComparer.Ordinal).ThenBy(e => e.Definition.Id, StringComparer.Ordinal).ToArray();
                foreach (var entry in definitions)
                {
                    try { host.Undo.Add(Apply(host.Root, entry.Service, entry.Definition)); }
                    catch (Exception ex) { entry.Service.Context.Log($"组件 {entry.Definition.Id} 应用失败：{ex.Message}"); }
                }
            }
        }
        finally { _refreshing = false; if (_changedDuringRefresh) { _changedDuringRefresh = false; QueueRefresh(); } }
    }
    private static void Undo(Host host)
    {
        foreach (var undo in host.Undo.AsEnumerable().Reverse())
            try { undo(); } catch (Exception ex) { LauncherLogService.AppendLine($"[PluginComponents] 撤回失败：{ex.Message}"); }
        host.Undo.Clear();
    }
    private static FrameworkElement Find(FrameworkElement root, string selector)
    {
        var nodes = Tree(root).Where(n => selector.StartsWith("$root", StringComparison.Ordinal) ? n.Path == selector : n.Element.Name == selector).ToArray();
        return nodes.Length == 1 ? nodes[0].Element : throw new InvalidOperationException($"组件选择器 {selector} 匹配 {nodes.Length} 个控件，请使用唯一名称或路径。");
    }
    private sealed class Slot
    {
        internal readonly FrameworkElement Parent;
        internal readonly int Index;
        internal Slot(FrameworkElement parent, int index) { Parent = parent; Index = index; }
        internal static Slot Of(FrameworkElement element)
        {
            var parent = element.Parent ?? VisualTreeHelper.GetParent(element);
            return parent switch
            {
                Panel p when p.Children.Contains(element) => new Slot(p, p.Children.IndexOf(element)),
                Decorator d when ReferenceEquals(d.Child, element) => new Slot(d, 0),
                ContentControl c when ReferenceEquals(c.Content, element) => new Slot(c, 0),
                _ => throw new InvalidOperationException("组件不在可修改的 Panel/Decorator/ContentControl 内；请替换其外层宿主。")
            };
        }
        internal void ValidateEmpty()
        {
            if (Parent is Panel) return;
            if (Parent is Decorator d && d.Child == null) return;
            if (Parent is ContentControl c && c.Content == null) return;
            throw new InvalidOperationException("目标容器已占用或不支持添加；请显式 Replace。 ");
        }
        internal void Insert(FrameworkElement element)
        {
            ValidateEmpty();
            if (Parent is Panel p) p.Children.Insert(Index < 0 ? p.Children.Count : Math.Min(Index, p.Children.Count), element);
            else if (Parent is Decorator d) d.Child = element;
            else if (Parent is ContentControl c) c.SetCurrentValue(ContentControl.ContentProperty, element);
        }
        internal void Remove(FrameworkElement element)
        {
            if (Parent is Panel p) p.Children.Remove(element);
            else if (Parent is Decorator d && ReferenceEquals(d.Child, element)) d.Child = null;
            else if (Parent is ContentControl c && ReferenceEquals(c.Content, element)) c.SetCurrentValue(ContentControl.ContentProperty, null);
        }
    }
    private static Action Apply(FrameworkElement root, PluginComponentService service, PluginComponentDefinition definition)
    {
        var undos = new Stack<Action>();
        try
        {
            var element = definition.Action == PluginComponentAction.Add ? null : Find(root, definition.Selector!);
            if (ReferenceEquals(root, element) && definition.Action != PluginComponentAction.Configure)
                throw new InvalidOperationException("不能删除或移动视图根对象；可以操作根对象的内容组件。");
            if (definition.Action is PluginComponentAction.Add or PluginComponentAction.Replace)
            {
                var created = definition.CreateContent!(new PluginUiContext(service.Context, definition.Target))
                    ?? throw new InvalidOperationException("组件工厂返回 null。");
                if (created.Parent != null || VisualTreeHelper.GetParent(created) != null) throw new InvalidOperationException("组件工厂必须创建未挂载的新控件。");
                Slot slot;
                if (element != null)
                {
                    slot = Slot.Of(element);
                    var original = element;
                    slot.Remove(original);
                    undos.Push(() => slot.Insert(original));
                    CopyPlacement(original, created);
                }
                else slot = new Slot(ResolveParent(root, definition.Parent!, undos), definition.Index);
                slot.Insert(created);
                undos.Push(() => slot.Remove(created));
                element = created;
            }
            else if (definition.Action == PluginComponentAction.Remove)
            {
                var slot = Slot.Of(element!);
                slot.Remove(element!);
                undos.Push(() => slot.Insert(element!));
            }
            else if (definition.Action == PluginComponentAction.Move)
            {
                var parent = ResolveParent(root, definition.Parent!, undos);
                if (Tree(element!).Any(n => ReferenceEquals(n.Element, parent))) throw new InvalidOperationException("不能把组件移动到自己或自己的子级。");
                var old = Slot.Of(element!);
                var next = new Slot(parent, definition.Index);
                if (!ReferenceEquals(old.Parent, parent)) next.ValidateEmpty();
                old.Remove(element!);
                undos.Push(() => old.Insert(element!));
                next.Insert(element!);
                undos.Push(() => next.Remove(element!));
            }
            if (definition.Action != PluginComponentAction.Remove) ApplyLayout(element!, definition.Layout, undos);
            return () =>
            {
                var failures = new List<Exception>();
                while (undos.TryPop(out var undo)) try { undo(); } catch (Exception ex) { failures.Add(ex); }
                if (failures.Count > 0) throw new AggregateException(failures);
            };
        }
        catch
        {
            while (undos.TryPop(out var undo)) try { undo(); } catch (Exception ex) { service.Context.Log($"组件回滚失败：{ex.Message}"); }
            throw;
        }
    }
    private static readonly object OverlayMarker = new();

    private static FrameworkElement ResolveParent(FrameworkElement root, string selector, Stack<Action> undos)
    {
        if (selector != "$overlay") return Find(root, selector);
        var existing = Tree(root).Select(n => n.Element).OfType<Canvas>()
            .FirstOrDefault(c => ReferenceEquals(c.Tag, OverlayMarker) && ReferenceEquals(c.DataContext, root));
        if (existing != null) return existing;
        // WPF Window/UserControl/Page 都有 Content。保留原内容对象及其绑定；撤销后恢复原树。
        FrameworkElement? original;
        Action<FrameworkElement?> setContent;
        if (root is ContentControl content && content.Content is FrameworkElement child)
        {
            original = child;
            setContent = value => content.SetCurrentValue(ContentControl.ContentProperty, value);
        }
        else if (root is Page page && page.Content is FrameworkElement pageChild)
        {
            original = pageChild;
            setContent = value => page.SetCurrentValue(Page.ContentProperty, value);
        }
        else throw new InvalidOperationException("$overlay 需要带 FrameworkElement 内容的 Window/UserControl/Page 根对象。");
        var wrapper = new Grid();
        var canvas = new Canvas { Name = "PluginAbsoluteOverlay", Tag = OverlayMarker, DataContext = root,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Panel.SetZIndex(canvas, int.MaxValue);
        setContent(null);
        undos.Push(() =>
        {
            setContent(null);
            wrapper.Children.Remove(original);
            setContent(original);
        });
        wrapper.Children.Add(original);
        wrapper.Children.Add(canvas);
        setContent(wrapper);
        return canvas;
    }

    private static readonly DependencyProperty[] Placement = { Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty,
        Grid.ColumnSpanProperty, Panel.ZIndexProperty, Canvas.LeftProperty, Canvas.TopProperty, Canvas.RightProperty, Canvas.BottomProperty,
        FrameworkElement.WidthProperty, FrameworkElement.HeightProperty, FrameworkElement.MarginProperty,
        FrameworkElement.HorizontalAlignmentProperty, FrameworkElement.VerticalAlignmentProperty, DockPanel.DockProperty };
    private static void CopyPlacement(FrameworkElement from, FrameworkElement to)
    { foreach (var property in Placement) to.SetCurrentValue(property, from.GetValue(property)); }
    private static void ApplyLayout(FrameworkElement e, PluginComponentLayout l, Stack<Action> undos)
    {
        void Set(DependencyProperty property, object? value)
        {
            if (value == null) return;
            var old = e.GetValue(property);
            var local = e.ReadLocalValue(property);
            var binding = BindingOperations.GetBindingBase(e, property);
            e.SetCurrentValue(property, value);
            undos.Push(() =>
            {
                // 保留动态资源和绑定表达式，原本无本地值的属性恢复样式/默认值。
                if (binding != null) BindingOperations.SetBinding(e, property, binding);
                else if (local == DependencyProperty.UnsetValue) e.ClearValue(property);
                else e.SetCurrentValue(property, old);
            });
        }
        Set(Grid.RowProperty, l.Row); Set(Grid.ColumnProperty, l.Column); Set(Grid.RowSpanProperty, l.RowSpan); Set(Grid.ColumnSpanProperty, l.ColumnSpan);
        Set(Panel.ZIndexProperty, l.ZIndex); Set(Canvas.LeftProperty, l.Left); Set(Canvas.TopProperty, l.Top);
        Set(Canvas.RightProperty, l.Right); Set(Canvas.BottomProperty, l.Bottom); Set(FrameworkElement.WidthProperty, l.Width);
        Set(FrameworkElement.HeightProperty, l.Height); Set(FrameworkElement.MarginProperty, l.Margin);
        Set(FrameworkElement.HorizontalAlignmentProperty, l.HorizontalAlignment); Set(FrameworkElement.VerticalAlignmentProperty, l.VerticalAlignment);
        Set(DockPanel.DockProperty, l.Dock);
        Set(FrameworkElement.MinWidthProperty, l.MinWidth); Set(FrameworkElement.MinHeightProperty, l.MinHeight);
        Set(FrameworkElement.MaxWidthProperty, l.MaxWidth); Set(FrameworkElement.MaxHeightProperty, l.MaxHeight);
        Set(UIElement.OpacityProperty, l.Opacity); Set(UIElement.VisibilityProperty, l.Visibility);
        Set(UIElement.IsHitTestVisibleProperty, l.IsHitTestVisible); Set(UIElement.ClipToBoundsProperty, l.ClipToBounds);
        Set(UIElement.RenderTransformProperty, l.RenderTransform); Set(UIElement.RenderTransformOriginProperty, l.RenderTransformOrigin);
    }
}
