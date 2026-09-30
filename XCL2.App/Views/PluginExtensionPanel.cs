using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>宿主渲染注册项，插件不必查找或修改宿主私有控件。</summary>
internal sealed class PluginExtensionPanel : UserControl, IDisposable
{
    private readonly string _target;
    private readonly PluginUiService? _owner;
    private readonly StackPanel _items = new();
    private long _revision = -1;
    private bool _disposed;
    private bool _building;

    internal PluginExtensionPanel(string target, PluginUiService? owner = null)
    {
        _target = target;
        _owner = owner;
        Content = _items;
        Visibility = Visibility.Collapsed;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        PluginUiRegistry.Changed -= Refresh;
        PluginUiRegistry.Changed += Refresh;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => PluginUiRegistry.Changed -= Refresh;

    private void Refresh()
    {
        if (_disposed || _building || _revision == PluginUiRegistry.Revision) return;
        _building = true;
        _revision = PluginUiRegistry.Revision;
        try
        {
            _items.Children.Clear();
            var services = _owner == null ? PluginUiRegistry.Snapshot() : new[] { _owner };
            var entries = services.Where(s => !s.IsDisposed)
                .SelectMany(s => s.Entries.Select(value => (Service: s, Value: value)))
                .Where(e => TargetOf(e.Value) == _target)
                .OrderBy(e => OrderOf(e.Value)).ThenBy(e => e.Service.Context.PluginId, StringComparer.Ordinal)
                .ThenBy(e => IdOf(e.Value), StringComparer.Ordinal).ToArray();
            foreach (var entry in entries)
            {
                if (entry.Service.IsDisposed) continue;
                try
                {
                    var control = Build(entry.Service, entry.Value);
                    if (control != null) _items.Children.Add(new Border { Child = control, Margin = new Thickness(0, 4, 0, 4) });
                }
                catch (Exception ex) { entry.Service.Context.Log($"创建 UI 扩展失败：{ex.Message}"); }
            }
            Visibility = _items.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _building = false;
            // 控件工厂可能新增注册项，下一轮 UI 消息再刷新，避免递归重建。
            if (!_disposed && IsLoaded && _revision != PluginUiRegistry.Revision)
                Dispatcher.BeginInvoke(new Action(Refresh));
        }
    }

    private static string? TargetOf(object entry) => entry switch
    { PluginButtonDefinition b => b.Target, PluginOptionDefinition o => o.Target, PluginPanelDefinition p => p.Target, _ => null };
    private static int OrderOf(object entry) => entry switch
    { PluginButtonDefinition b => b.Order, PluginOptionDefinition o => o.Order, PluginPanelDefinition p => p.Order, _ => 0 };
    private static string IdOf(object entry) => entry switch
    { PluginButtonDefinition b => b.Id, PluginOptionDefinition o => o.Id, PluginPanelDefinition p => p.Id, _ => "" };

    private FrameworkElement? Build(PluginUiService service, object definition)
    {
        var context = new PluginUiContext(service.Context, _target);
        if (definition is PluginPanelDefinition panel)
            return panel.CreateContent(context) ?? throw new InvalidOperationException("面板工厂返回了 null。");
        if (definition is PluginOptionDefinition option) return BuildOption(service, option);
        if (definition is not PluginButtonDefinition action) return null;
        var button = new Button { Content = action.Text, ToolTip = action.Description,
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
        var box = new StackPanel();
        var error = ErrorText();
        box.Children.Add(button);
        box.Children.Add(error);
        button.Click += async (_, _) =>
        {
            if (service.IsDisposed || !button.IsEnabled) return;
            button.IsEnabled = false;
            error.Text = "";
            try { await action.OnClick(context); }
            catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested) { }
            catch (Exception ex)
            {
                error.Text = "执行失败：" + ex.Message;
                service.Context.Log($"按钮 {action.Id} 执行失败：{ex.Message}");
            }
            finally { button.IsEnabled = !service.IsDisposed; }
        };
        return box;
    }

    private static TextBlock ErrorText() => new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    private static FrameworkElement BuildOption(PluginUiService service, PluginOptionDefinition option)
    {
        var panel = new StackPanel { ToolTip = option.Description };
        var error = ErrorText();
        var value = service.ReadOption(option);
        bool Save(object? next)
        {
            error.Text = service.SaveOption(option, next) ?? "";
            return error.Text.Length == 0;
        }
        if (option.Kind == PluginOptionKind.Toggle)
        {
            var check = new CheckBox { Content = option.Title, IsChecked = (bool)value };
            check.Click += (_, _) => { if (!Save(check.IsChecked == true)) check.IsChecked = (bool)service.ReadOption(option); };
            panel.Children.Add(check);
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = option.Title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            if (option.Kind == PluginOptionKind.Choice)
            {
                var combo = new ComboBox { ItemsSource = option.Choices, DisplayMemberPath = nameof(PluginChoice.Label),
                    SelectedValuePath = nameof(PluginChoice.Value), SelectedValue = (string)value, MinWidth = 160,
                    HorizontalAlignment = HorizontalAlignment.Left };
                var reverting = false;
                combo.SelectionChanged += (_, _) =>
                {
                    if (reverting || combo.SelectedValue is not string selected) return;
                    if (Save(selected)) return;
                    reverting = true;
                    combo.SelectedValue = service.ReadOption(option);
                    reverting = false;
                };
                panel.Children.Add(combo);
            }
            else
            {
                var row = new DockPanel();
                var apply = new Button { Content = "应用", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
                DockPanel.SetDock(apply, Dock.Right);
                row.Children.Add(apply);
                var text = new TextBox { Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "", MinWidth = 160 };
                row.Children.Add(text);
                apply.Click += (_, _) =>
                {
                    if (option.Kind == PluginOptionKind.Text) Save(text.Text);
                    else if (double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)) Save(number);
                    else error.Text = "请输入有效数字。";
                };
                panel.Children.Add(row);
            }
        }
        if (!string.IsNullOrWhiteSpace(option.Description)) panel.Children.Add(new TextBlock
        { Text = option.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(error);
        return panel;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PluginUiRegistry.Changed -= Refresh;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _items.Children.Clear();
    }
}

internal sealed class PluginSettingsContent : UserControl, IPluginSettingsPanel, IDisposable
{
    private readonly UserControl _original;
    private readonly PluginExtensionPanel _extensions;
    internal PluginSettingsContent(UserControl original, PluginUiService service)
    {
        _original = original;
        _extensions = new PluginExtensionPanel(PluginUiTargets.PluginSettings, service);
        var panel = new StackPanel();
        panel.Children.Add(original);
        panel.Children.Add(_extensions);
        Content = panel;
    }
    public void Save() { if (_original is IPluginSettingsPanel savable) savable.Save(); }
    public void Dispose() { _extensions.Dispose(); if (Content is Panel panel) panel.Children.Clear(); Content = null; }
}

/// <summary>给每个 Overlay 增加独立扩展区，不替换原弹窗的按钮和结果逻辑。</summary>
internal sealed class PluginOverlayView : DockPanel, IDisposable
{
    private readonly ContentControl _dialog;
    private readonly PluginExtensionPanel _extensions;
    internal PluginOverlayView(IOverlayDialog dialog)
    {
        _extensions = new PluginExtensionPanel(PluginUiRegistry.Target(dialog));
        var scroll = new ScrollViewer { Content = _extensions, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetDock(scroll, Dock.Bottom);
        Children.Add(scroll);
        _dialog = new ContentControl { Content = dialog };
        Children.Add(_dialog);
    }
    public void Dispose() { _extensions.Dispose(); _dialog.Content = null; Children.Clear(); }
}

internal sealed class PluginRegisteredDialog : OverlayDialogControl
{
    internal PluginDialogDefinition Definition { get; }
    internal string Target { get; }
    internal bool CompletedDuringCreation { get; private set; }
    internal bool? CreationResult { get; private set; }
    private bool _creating = true;
    internal PluginRegisteredDialog(PluginContext plugin, PluginDialogDefinition definition, object? parameter)
    {
        Definition = definition;
        Target = plugin.Ui.GetDialogTarget(definition.Id);
        Width = definition.Width;
        MaxWidth = Math.Max(240, (Application.Current.MainWindow?.ActualWidth ?? 800) - 80);
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = definition.Title, FontWeight = FontWeights.Bold, FontSize = 18,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => CloseWith(null);
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        var context = new PluginDialogContext(plugin, parameter, result =>
        {
            if (_creating) { CompletedDuringCreation = true; CreationResult = result; }
            else CloseWith(result);
        });
        var content = definition.CreateContent(context) ?? throw new InvalidOperationException("弹窗工厂返回了 null。");
        panel.Children.Add(new ScrollViewer { Content = content,
            MaxHeight = Math.Max(120, (Application.Current.MainWindow?.ActualHeight ?? 700) - 240),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = panel;
        _creating = false;
    }
}
