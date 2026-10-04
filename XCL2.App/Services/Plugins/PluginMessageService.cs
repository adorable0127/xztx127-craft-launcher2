using System.Collections.Immutable;
using System.Text.Json;

namespace XCL2.App.Services.Plugins;

public sealed record PluginPeer(string InstanceId, string DeclaredId, string Version);
public sealed record PluginMessage(string SenderPluginId, string Channel, JsonElement Payload);
public sealed record PluginPeerUnavailable(string PluginId) : PluginNotification;

/// <summary>具名 JSON 消息；只向清单声明的依赖实例发送，不共享可变对象或服务引用。</summary>
public sealed class PluginMessageService
{
    private readonly PluginContext _context;
    internal PluginMessageService(PluginContext context) { _context = context; }
    public ImmutableArray<PluginPeer> GetPeers()
    {
        _context.Permissions.Demand(PluginPermissionNames.Messaging);
        return _context.Host.Owner.Plugins.Plugins.Where(p => p.Context != null && !p.Context.IsStopped && p.Metadata != null &&
            p.Context.Permissions.Has(PluginPermissionNames.Messaging) && _context.Metadata.Dependencies.Any(d =>
                d.Id == p.Metadata.DeclaredId && PluginSemanticVersion.InRange(p.Metadata.Version, d.VersionRange)))
            .Select(p => new PluginPeer(p.Context!.PluginId, p.Metadata!.DeclaredId, p.Metadata.Version)).OrderBy(p => p.InstanceId, StringComparer.Ordinal).ToImmutableArray();
    }
    public IDisposable Subscribe(string channel, Func<PluginMessage, CancellationToken, ValueTask> callback)
    {
        _context.Permissions.Demand(PluginPermissionNames.Messaging); ArgumentException.ThrowIfNullOrWhiteSpace(channel); ArgumentNullException.ThrowIfNull(callback);
        if (channel.Length > 128) throw new ArgumentException("消息通道名超过 128 字符。");
        return _context.Track(PluginMessageRegistry.Add(_context, channel, callback));
    }
    public async Task<int> SendAsync(string targetPluginId, string channel, JsonElement payload, CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.Messaging);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        if (!GetPeers().Any(p => p.InstanceId == targetPluginId)) throw new PluginOperationException("UndeclaredDependency", "目标必须是清单声明且已加载的依赖插件实例。");
        var text = payload.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 65536 || _context.Host.Safe(text) != text)
            throw new PluginOperationException("InvalidMessage", "消息超过 64 KiB 或包含受保护凭据。");
        var revision = Interlocked.Read(ref _context.AuthorizationRevision);
        var result = await PluginMessageRegistry.SendAsync(_context, targetPluginId, new(_context.PluginId, channel, payload.Clone()), ct).ConfigureAwait(false);
        _context.Permissions.Demand(PluginPermissionNames.Messaging);
        if (revision != Interlocked.Read(ref _context.AuthorizationRevision)) throw new PluginOperationException("AuthorizationChanged", "发送期间授权已变更。");
        return result;
    }
}
internal static class PluginMessageRegistry
{
    private sealed record Entry(PluginContext Context, string Channel, Func<PluginMessage, CancellationToken, ValueTask> Callback);
    private static readonly object Gate = new(); private static readonly List<Entry> Entries = new();
    internal static IDisposable Add(PluginContext context, string channel, Func<PluginMessage, CancellationToken, ValueTask> callback)
    {
        var entry = new Entry(context, channel, callback); lock (Gate) Entries.Add(entry);
        return new PluginRegistration(() => { lock (Gate) Entries.Remove(entry); });
    }
    internal static async Task<int> SendAsync(PluginContext sender, string target, PluginMessage message, CancellationToken ct)
    {
        Entry[] listeners; lock (Gate) listeners = Entries.Where(e => e.Context.PluginId == target && e.Channel == message.Channel).ToArray();
        var delivered = 0;
        foreach (var entry in listeners)
        {
            sender.Permissions.Demand(PluginPermissionNames.Messaging);
            var result = await entry.Context.Callbacks.InvokeAsync(PluginPermissionNames.Messaging, "Message:" + message.Channel,
                async token => { await entry.Callback(message, token); return true; }, entry.Context.Host.CallbackTimeout, ct).ConfigureAwait(false);
            if (result.Success) delivered++;
        }
        return delivered;
    }
}
