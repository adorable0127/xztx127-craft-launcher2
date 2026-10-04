using System.Collections.Immutable;
using System.IO;
using System.Windows;

namespace XCL2.App.Services.Plugins;

public sealed record PluginNavigationPageDefinition(string Id, string Title, Func<PluginUiContext, FrameworkElement> CreateContent,
    bool ShowInSimplifiedMode = false, ImmutableArray<string> Languages = default, int Order = 0);
public sealed record PluginInstanceActionDefinition(string Id, string Title,
    Func<PluginInstanceDetails, CancellationToken, ValueTask> ExecuteAsync, int Order = 0);
public sealed record PluginBackgroundDefinition(string Id, string Name, string RelativeImagePath);
public sealed record PluginBackgroundAsset(string RelativeImagePath);

public sealed partial class PluginUiService
{
    public IDisposable RegisterNavigationPage(PluginNavigationPageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(definition.CreateContent);
        return RegisterContribution("navigation", PluginPermissionNames.UiNavigation, definition.Id, definition);
    }
    public IDisposable RegisterInstanceAction(PluginInstanceActionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(definition.ExecuteAsync);
        return RegisterContribution("instance-action", PluginPermissionNames.UiInstanceActions, definition.Id, definition);
    }
    public IDisposable RegisterSetting(PluginOptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(definition.Kind) || !double.IsFinite(definition.Minimum) || !double.IsFinite(definition.Maximum) || definition.Minimum > definition.Maximum)
            throw new ArgumentException("设置类型或范围无效。");
        if (definition.Kind == PluginOptionKind.Choice && definition.Choices.Count == 0) throw new ArgumentException("选择设置需要候选项。");
        definition = definition with { Choices = Array.AsReadOnly(definition.Choices.ToArray()) };
        var error = ValueError(definition, DefaultValue(definition)); if (error != null) throw new ArgumentException(error);
        return RegisterContribution("setting", PluginPermissionNames.UiSettings, definition.Id, definition);
    }
    public IDisposable RegisterBackground(PluginBackgroundDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = PluginUiContributionRegistry.ResolveImage(_context, definition.RelativeImagePath);
        return RegisterContribution("background", PluginPermissionNames.UiBackground, definition.Id, definition);
    }
    private IDisposable RegisterContribution(string kind, string permission, string id, object definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _ = _context.Host; _context.Permissions.Demand(permission);
        IDisposable? result = null;
        Invoke(() => result = _context.Track(PluginUiContributionRegistry.Register(_context, kind, permission, id, definition)));
        return result!;
    }
    public async Task<PluginBackgroundAsset?> ImportBackgroundImageAsync(CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.UiBackground); _context.Permissions.Demand(PluginPermissionNames.ExternalFilesRead);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, Stopping);
        var chosen = await _context.Host.OnUiAsync(() =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Multiselect = false };
            return picker.ShowDialog() == true ? picker.FileName : null;
        }, stop.Token).ConfigureAwait(false);
        if (chosen == null) return null;
        var extension = Path.GetExtension(chosen).ToLowerInvariant();
        if (!PluginUiContributionRegistry.ImageExtensions.Contains(extension) || new FileInfo(chosen).Length > 32 * 1024 * 1024)
            throw new PluginOperationException("InvalidImage", "图片类型不支持或大小超过 32 MiB。");
        var relative = Path.Combine("backgrounds", Guid.NewGuid().ToString("N") + extension);
        var destination = Path.Combine(_context.DataDirectory, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await Task.Run(() => File.Copy(chosen, destination), stop.Token).ConfigureAwait(false);
        _context.Permissions.Demand(PluginPermissionNames.UiBackground); _context.Permissions.Demand(PluginPermissionNames.ExternalFilesRead);
        return new(relative);
    }
}

internal static class PluginUiContributionRegistry
{
    internal sealed record Entry(PluginContext Context, string Id, string Kind, string Permission, object Definition);
    private static readonly object Gate = new(); private static readonly List<Entry> Entries = new();
    internal static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    internal static event Action? Changed;
    internal static Entry[] Snapshot(string kind)
    {
        lock (Gate) return Entries.Where(e => e.Kind == kind && e.Context.Permissions.Has(e.Permission)).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
    }
    internal static IDisposable Register(PluginContext context, string kind, string permission, string id, object definition)
    {
        var entry = new Entry(context, context.PluginId + "/" + id, kind, permission, definition);
        lock (Gate)
        {
            if (Entries.Any(e => e.Kind == kind && e.Id == entry.Id)) throw new ArgumentException("UI 扩展重复注册。");
            Entries.Add(entry);
        }
        context.AuthorizationChanged += Refresh; Refresh();
        return new PluginRegistration(() => { lock (Gate) Entries.Remove(entry); context.AuthorizationChanged -= Refresh; Refresh(); });
    }
    internal static void Refresh()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList())
            try { handler(); } catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] UiContributionRefreshFailed: " + ex.GetType().Name); }
    }
    internal static string ResolveImage(PluginContext context, string relative)
    {
        context.Permissions.Demand(PluginPermissionNames.UiBackground);
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new PluginOperationException("InvalidPath", "背景路径必须在插件数据目录中。");
        var root = Path.GetFullPath(context.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !ImageExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
            throw new PluginOperationException("InvalidImage", "背景必须是插件数据目录内的图片。");
        for (var item = full; item != root && item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new PluginOperationException("InvalidImage", "背景不能使用链接文件。");
        if (!File.Exists(full) || new FileInfo(full).Length > 32 * 1024 * 1024) throw new PluginOperationException("InvalidImage", "背景不存在或超过 32 MiB。");
        return full;
    }
}
