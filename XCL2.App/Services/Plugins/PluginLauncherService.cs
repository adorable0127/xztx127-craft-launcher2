using System.Collections.Immutable;

namespace XCL2.App.Services.Plugins;

/// <summary>任何线程均可 await；数据为独立不可变快照，磁盘扫描不在 UI 线程执行。</summary>
public sealed class PluginLauncherService
{
    public IPluginAccounts Accounts { get; }
    public IPluginInstances Instances { get; }
    public IPluginMods Mods { get; }
    public IPluginDownloads Downloads { get; }
    public IPluginSettings Settings { get; }
    public IPluginEnvironment Environment { get; }
    internal PluginLauncherService(PluginContext context)
    {
        Accounts = new AccountReader(context); Instances = new InstanceReader(context); Mods = new ModReader(context);
        Downloads = new DownloadReader(context); Settings = new SettingsReader(context); Environment = new EnvironmentReader(context);
    }
    private abstract class Reader
    {
        protected readonly PluginContext Context;
        protected Reader(PluginContext context) { Context = context; }
        protected async Task<T> Read<T>(string permission, Func<PluginHostBridge, CancellationToken, Task<T>> read, CancellationToken ct)
        {
            var host = Context.Host;
            Context.Permissions.Demand(permission);
            var revision = Interlocked.Read(ref Context.AuthorizationRevision);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Context.Stopping);
            try
            {
                var result = await read(host, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                Context.Permissions.Demand(permission);
                if (revision != Interlocked.Read(ref Context.AuthorizationRevision))
                    throw new PluginOperationException("AuthorizationChanged", "查询期间授权已变更，请重新查询。");
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (PluginOperationException) { throw; }
            catch (Exception ex)
            {
                Context.Log("ReadFailed: " + permission + "; " + ex.GetType().Name);
                throw new PluginOperationException("ReadFailed", "业务快照读取失败，请稍后重试。");
            }
        }
    }
    private sealed class AccountReader(PluginContext context) : Reader(context), IPluginAccounts
    {
        public Task<PluginListSnapshot<PluginAccount>> GetSnapshotAsync(CancellationToken ct = default) =>
            Read(PluginPermissionNames.AccountsRead, (host, token) => host.GetAccountsAsync(token), ct);
        public async Task<PluginAccount?> GetSelectedAsync(CancellationToken ct = default) =>
            (await GetSnapshotAsync(ct).ConfigureAwait(false)).Items.FirstOrDefault(a => a.IsSelected);
    }
    private sealed class InstanceReader(PluginContext context) : Reader(context), IPluginInstances
    {
        public Task<PluginListSnapshot<PluginInstance>> GetSnapshotAsync(PluginInstanceQuery? query = null, CancellationToken ct = default) =>
            Read(PluginPermissionNames.InstancesRead, (host, token) => host.GetInstancesAsync(query ?? new(), token), ct);
        public Task<PluginInstanceDetails?> GetDetailsAsync(string instanceId, CancellationToken ct = default) =>
            Read(PluginPermissionNames.InstancesRead, (host, token) => host.GetInstanceDetailsAsync(instanceId, token), ct);
        public async Task<PluginInstance?> GetSelectedAsync(CancellationToken ct = default) =>
            (await GetSnapshotAsync(ct: ct).ConfigureAwait(false)).Items.FirstOrDefault(i => i.IsSelected);
    }
    private sealed class ModReader(PluginContext context) : Reader(context), IPluginMods
    {
        public Task<PluginListSnapshot<PluginMod>> GetSnapshotAsync(string instanceId, CancellationToken ct = default) =>
            Read(PluginPermissionNames.ModsRead, (host, token) => host.GetModsAsync(instanceId, token), ct);
    }
    private sealed class DownloadReader(PluginContext context) : Reader(context), IPluginDownloads
    {
        public Task<PluginListSnapshot<PluginDownload>> GetSnapshotAsync(CancellationToken ct = default) =>
            Read(PluginPermissionNames.DownloadsRead, (host, token) => host.GetDownloadsAsync(token), ct);
    }
    private sealed class SettingsReader(PluginContext context) : Reader(context), IPluginSettings
    {
        public Task<PluginSettingsSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
            Read(PluginPermissionNames.SettingsRead, (host, token) => host.OnUiAsync(host.CopySettings, token), ct);
    }
    private sealed class EnvironmentReader(PluginContext context) : Reader(context), IPluginEnvironment
    {
        public Task<PluginEnvironmentSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
            Read(PluginPermissionNames.EnvironmentRead, (host, token) => host.OnUiAsync(host.CopyEnvironment, token), ct);
    }
}
