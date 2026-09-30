using System.Threading;
using System.Windows;
using XCL2.App.Views;

namespace XCL2.App.Services.Plugins;

/// <summary>稳定的扩展位置。所有经 OverlayDialogService 显示的弹窗均有 ForDialog 位置。</summary>
public static class PluginUiTargets
{
    public const string PluginSettings = "plugin.settings";
    public const string PluginManager = "dialog:PluginManagerDialog";
    public const string LoaderChoice = "dialog:LoaderChoiceDialog";
    public const string MultiLoaderInstall = "dialog:MultiLoaderInstallWindow";
    public const string CreateServer = "dialog:CreateServerWindow";
    public const string ExperimentalFeatures = "dialog:ExperimentalFeaturesWindow";
    public static string ForDialog<T>() where T : FrameworkElement, IOverlayDialog => "dialog:" + typeof(T).Name;
}

public sealed class PluginUiContext
{
    public PluginContext Plugin { get; }
    public string Target { get; }
    public CancellationToken Stopping => Plugin.Stopping;
    internal PluginUiContext(PluginContext plugin, string target) { Plugin = plugin; Target = target; }
}

/// <summary>每次打开弹窗都有独立上下文，Close 可携带结果；插件停止时弹窗自动关闭。</summary>
public sealed class PluginDialogContext
{
    private readonly Action<bool?> _close;
    public PluginContext Plugin { get; }
    public object? Parameter { get; }
    public CancellationToken Stopping => Plugin.Stopping;
    internal PluginDialogContext(PluginContext plugin, object? parameter, Action<bool?> close)
    { Plugin = plugin; Parameter = parameter; _close = close; }
    public void Close(bool? result = null) => Plugin.Ui.Invoke(() => _close(result));
}

public sealed record PluginDialogDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required Func<PluginDialogContext, FrameworkElement> CreateContent { get; init; }
    public double Width { get; init; } = 560;
    public bool DismissOnEscape { get; init; } = true;
    public bool DismissOnBackgroundClick { get; init; }
}

public sealed record PluginButtonDefinition
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public string Target { get; init; } = PluginUiTargets.PluginSettings;
    public string? Description { get; init; }
    public int Order { get; init; }
    public required Func<PluginUiContext, Task> OnClick { get; init; }
}

public enum PluginOptionKind { Toggle, Text, Number, Choice }
public sealed record PluginChoice(string Value, string Label);

/// <summary>自动生成控件并保存到当前插件 Config。Number 使用 double，Choice 使用字符串值。</summary>
public sealed record PluginOptionDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Target { get; init; } = PluginUiTargets.PluginSettings;
    public string? ConfigKey { get; init; }
    public string? Description { get; init; }
    public int Order { get; init; }
    public PluginOptionKind Kind { get; init; } = PluginOptionKind.Toggle;
    public object? DefaultValue { get; init; }
    public double Minimum { get; init; } = double.MinValue;
    public double Maximum { get; init; } = double.MaxValue;
    public IReadOnlyList<PluginChoice> Choices { get; init; } = Array.Empty<PluginChoice>();
    /// <summary>返回 null/空字符串表示合法，否则在控件旁显示错误且不保存。</summary>
    public Func<object?, string?>? Validate { get; init; }
    /// <summary>成功保存之后调用；回调异常只记录日志，不撤销已经保存的配置。</summary>
    public Action<object?>? OnChanged { get; init; }
}

public sealed record PluginPanelDefinition
{
    public required string Id { get; init; }
    public string Target { get; init; } = PluginUiTargets.PluginSettings;
    public int Order { get; init; }
    /// <summary>每次创建新控件，不可复用已挂在其他父控件下的 WPF 元素。</summary>
    public required Func<PluginUiContext, FrameworkElement> CreateContent { get; init; }
}

/// <summary>只读生命周期通知，不能替代宿主确认流程。Result 仅在关闭通知时有值。</summary>
public sealed record PluginDialogEvent(string Target, bool IsOpen, bool? Result);
