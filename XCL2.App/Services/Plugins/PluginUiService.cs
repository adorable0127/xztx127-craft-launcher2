using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services.Plugins;

/// <summary>插件实例独享的 UI 注册表。所有注册、回调及控件创建在 UI 线程串行执行。</summary>
public sealed class PluginUiService : IDisposable
{
    private readonly PluginContext _context;
    private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);
    private readonly List<Action<PluginDialogEvent>> _observers = new();
    private readonly HashSet<PluginRegisteredDialog> _openDialogs = new();
    private readonly CancellationTokenSource _stopping = new();
    internal bool IsDisposed { get; private set; }
    public CancellationToken Stopping => _stopping.Token;
    internal PluginContext Context => _context;
    internal IEnumerable<object> Entries => _entries.Values;
    private Dispatcher Dispatcher => Application.Current?.Dispatcher
        ?? throw new InvalidOperationException("启动器 UI 尚未创建。");

    internal PluginUiService(PluginContext context) { _context = context; }

    internal void Invoke(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private T Invoke<T>(Func<T> action)
    {
        var dispatcher = Dispatcher;
        return dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
    }

    private void EnsureActive()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(PluginUiService));
    }

    public Task RunOnUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Dispatcher.InvokeAsync(() => { EnsureActive(); action(); }).Task;
    }

    public IDisposable RegisterDialog(PluginDialogDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Title);
        ArgumentNullException.ThrowIfNull(definition.CreateContent);
        if (!double.IsFinite(definition.Width) || definition.Width < 240 || definition.Width > 1600)
            throw new ArgumentOutOfRangeException(nameof(definition.Width), "弹窗宽度必须介于 240 和 1600。");
        return Add("dialog", definition.Id, definition);
    }

    public IDisposable RegisterButton(PluginButtonDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Target);
        ArgumentNullException.ThrowIfNull(definition.OnClick);
        return Add("button", definition.Id, definition);
    }

    public IDisposable RegisterOption(PluginOptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Target);
        if (definition.ConfigKey != null) ArgumentException.ThrowIfNullOrWhiteSpace(definition.ConfigKey);
        if (!Enum.IsDefined(definition.Kind) || !double.IsFinite(definition.Minimum) ||
            !double.IsFinite(definition.Maximum) || definition.Minimum > definition.Maximum)
            throw new ArgumentException("选项类型或数值范围无效。");
        ArgumentNullException.ThrowIfNull(definition.Choices);
        var choices = definition.Choices.ToArray();
        if (choices.Any(c => c == null || string.IsNullOrWhiteSpace(c.Value) || string.IsNullOrWhiteSpace(c.Label)) ||
            choices.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() != choices.Length ||
            (definition.Kind == PluginOptionKind.Choice && choices.Length == 0))
            throw new ArgumentException("下拉选项必须具有不重复的非空 Value 和 Label。");
        definition = definition with { Choices = Array.AsReadOnly(choices) };
        var value = DefaultValue(definition);
        var error = ValueError(definition, value);
        if (error != null) throw new ArgumentException(error);
        return Add("option", definition.Id, definition);
    }

    public IDisposable RegisterPanel(PluginPanelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Target);
        ArgumentNullException.ThrowIfNull(definition.CreateContent);
        return Add("panel", definition.Id, definition);
    }

    private IDisposable Add(string kind, string id, object definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Invoke<IDisposable>(() =>
        {
            EnsureActive();
            var key = kind + ":" + id;
            if (!_entries.TryAdd(key, definition)) throw new ArgumentException($"重复注册：{key}");
            PluginUiRegistry.Add(this);
            PluginUiRegistry.Refresh();
            return new Registration(() => Invoke(() =>
            {
                if (!_entries.Remove(key)) return;
                if (kind == "dialog")
                    foreach (var dialog in _openDialogs.Where(d => d.Definition.Id == id).ToArray()) dialog.Close();
                PluginUiRegistry.Refresh();
            }));
        });
    }

    public IDisposable SubscribeDialogs(Action<PluginDialogEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return Invoke<IDisposable>(() =>
        {
            EnsureActive();
            _observers.Add(observer);
            PluginUiRegistry.Add(this);
            return new Registration(() => Invoke(() => _observers.Remove(observer)));
        });
    }

    internal void Notify(PluginDialogEvent notification)
    {
        foreach (var observer in _observers.ToArray())
        {
            if (IsDisposed) break;
            try { observer(notification); }
            catch (Exception ex) { _context.Log($"弹窗事件回调失败：{ex.Message}"); }
        }
    }

    public string GetDialogTarget(string id) => "plugin:" + _context.PluginId + "/" + id;

    public Task<bool?> ShowDialogAsync(string id, object? parameter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Invoke(() =>
        {
            EnsureActive();
            if (!_entries.TryGetValue("dialog:" + id, out var entry) || entry is not PluginDialogDefinition definition)
                throw new KeyNotFoundException($"未注册弹窗：{id}");
            return OpenAsync(definition, parameter);
        });
    }

    public Task<bool?> ShowMessageAsync(string title, string message)
        => ShowPromptAsync(title, message, false);

    public async Task<bool> ConfirmAsync(string title, string message)
        => await ShowPromptAsync(title, message, true) == true;

    private Task<bool?> ShowPromptAsync(string title, string message, bool confirm) => Invoke(() =>
    {
        EnsureActive();
        return OpenAsync(new PluginDialogDefinition
        {
            Id = confirm ? "$confirm" : "$message", Title = title,
            CreateContent = dialog =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                if (confirm)
                {
                    var cancel = new Button { Content = "取消", Margin = new Thickness(4), Padding = new Thickness(16, 6, 16, 6) };
                    cancel.Click += (_, _) => dialog.Close(false);
                    buttons.Children.Add(cancel);
                }
                var ok = new Button { Content = "确定", Margin = new Thickness(4), Padding = new Thickness(16, 6, 16, 6) };
                ok.Click += (_, _) => dialog.Close(true);
                buttons.Children.Add(ok);
                panel.Children.Add(buttons);
                return panel;
            }
        }, null);
    });

    private async Task<bool?> OpenAsync(PluginDialogDefinition definition, object? parameter)
    {
        var dialog = new PluginRegisteredDialog(_context, definition, parameter);
        EnsureActive();
        if (dialog.CompletedDuringCreation) return dialog.CreationResult;
        _openDialogs.Add(dialog);
        try { return await OverlayDialogService.ShowModalAsync(dialog, definition.DismissOnBackgroundClick, definition.DismissOnEscape); }
        finally { _openDialogs.Remove(dialog); }
    }

    internal static object DefaultValue(PluginOptionDefinition option) => option.DefaultValue ?? (option.Kind switch
    {
        PluginOptionKind.Toggle => (object)false,
        PluginOptionKind.Number => Math.Clamp(0d, option.Minimum, option.Maximum),
        PluginOptionKind.Choice => option.Choices[0].Value,
        _ => ""
    });

    internal static string? ValueError(PluginOptionDefinition option, object? value) => option.Kind switch
    {
        PluginOptionKind.Toggle when value is not bool => "开关选项需要 bool 值。",
        PluginOptionKind.Text when value is not string => "文本选项需要 string 值。",
        PluginOptionKind.Number when value is not double number || !double.IsFinite(number) || number < option.Minimum || number > option.Maximum
            => $"请输入 {option.Minimum} 到 {option.Maximum} 之间的有限数值（double）。",
        PluginOptionKind.Choice when value is not string choice || !option.Choices.Any(c => c.Value == choice) => "请选择有效的条目。",
        _ => null
    };

    internal object ReadOption(PluginOptionDefinition option)
    {
        var fallback = DefaultValue(option);
        var key = option.ConfigKey ?? option.Id;
        object value = option.Kind switch
        {
            PluginOptionKind.Toggle => _context.Config.Get(key, (bool)fallback),
            PluginOptionKind.Number => _context.Config.Get(key, (double)fallback),
            _ => _context.Config.Get(key, (string)fallback)
        };
        return ValueError(option, value) == null ? value : fallback;
    }

    internal string? SaveOption(PluginOptionDefinition option, object? value)
    {
        try
        {
            EnsureActive();
            var error = ValueError(option, value) ?? option.Validate?.Invoke(value);
            if (!string.IsNullOrEmpty(error)) return error;
            _context.Config.Set(option.ConfigKey ?? option.Id, value);
        }
        catch (Exception ex) { return ex.Message; }
        try { option.OnChanged?.Invoke(value); }
        catch (Exception ex) { _context.Log($"选项 {option.Id} 已保存，但回调失败：{ex.Message}"); }
        return null;
    }

    public void Dispose() => Invoke(() =>
    {
        if (IsDisposed) return;
        IsDisposed = true;
        try { _stopping.Cancel(); }
        catch (Exception ex) { _context.Log($"停止令牌回调失败：{ex.Message}"); }
        foreach (var dialog in _openDialogs.ToArray()) dialog.Close();
        _openDialogs.Clear();
        _context.Runtime.Dispose();
        _context.Components.Dispose();
        _entries.Clear();
        _observers.Clear();
        PluginUiRegistry.Remove(this);
    });

    private sealed class Registration : IDisposable
    {
        private Action? _remove;
        public Registration(Action remove) { _remove = remove; }
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}

internal static class PluginUiRegistry
{
    private static readonly List<PluginUiService> Services = new();
    internal static event Action? Changed;
    internal static long Revision { get; private set; }
    internal static PluginUiService[] Snapshot() => Services.Where(s => !s.IsDisposed).ToArray();
    internal static void Add(PluginUiService service) { if (!Services.Contains(service)) Services.Add(service); }
    internal static void Remove(PluginUiService service) { Services.Remove(service); Refresh(); }
    internal static void Refresh()
    {
        Revision++;
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { LauncherLogService.AppendLine($"[PluginUI] 刷新扩展失败：{ex.Message}"); }
        }
    }
    internal static string Target(IOverlayDialog dialog) => dialog is PluginRegisteredDialog pluginDialog
        ? pluginDialog.Target : "dialog:" + dialog.GetType().Name;
    internal static void Publish(IOverlayDialog dialog, bool open, bool? result = null)
    {
        var notification = new PluginDialogEvent(Target(dialog), open, result);
        foreach (var service in Snapshot()) service.Notify(notification);
    }
}
