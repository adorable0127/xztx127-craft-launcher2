using System.Collections.Immutable;

namespace XCL2.App.Services.Plugins;

/// <summary>API 2.2：实例、模组、运行时与下载管理。不会授予任意文件或进程访问。</summary>
public sealed class PluginManagementService
{
    private readonly PluginContext _context;
    internal PluginManagementService(PluginContext context) => _context = context;
    private async Task<T> UiAsync<T>(string permission, Func<PluginHostBridge, T> operation, CancellationToken ct)
    {
        using var scope = new PluginOperationScope(_context, ct, permission);
        return await _context.Host.OnUiAsync(() => { scope.Check(); return operation(_context.Host); }, scope.Token).ConfigureAwait(false);
    }
    private async Task<T> InstanceAsync<T>(string instanceId, string permission,
        Func<PluginHostBridge, PluginHostBridge.IndexedInstance, T> operation, CancellationToken ct)
    {
        using var scope = new PluginOperationScope(_context, ct, permission);
        var host = _context.Host;
        await host.GetInstancesAsync(new(true), scope.Token).ConfigureAwait(false);
        return await host.OnUiAsync(() => { scope.Check(); return operation(host, host.RequireManagedInstance(instanceId)); }, scope.Token).ConfigureAwait(false);
    }
    public Task<ImmutableArray<PluginGameFolder>> GetFoldersAsync(CancellationToken ct = default) =>
        UiAsync(PluginPermissionNames.InstancesRead, host => host.CopyManagedFolders(), ct);
    public Task<PluginInstanceDirectories> GetInstanceDirectoriesAsync(string instanceId, CancellationToken ct = default) =>
        InstanceAsync(instanceId, PluginPermissionNames.InstancesRead, (host, instance) => host.CopyInstanceDirectories(instance), ct);
    public Task<bool> SetInstanceHiddenAsync(string instanceId, bool hidden, CancellationToken ct = default) =>
        InstanceAsync(instanceId, PluginPermissionNames.InstancesWrite, (host, instance) => host.SetInstanceHidden(instance, hidden), ct);
    /// <summary>返回改名后的新实例 ID；原 ID 随目录名变化而失效。</summary>
    public Task<string> RenameInstanceAsync(string instanceId, string newName, CancellationToken ct = default) =>
        InstanceAsync(instanceId, PluginPermissionNames.InstancesWrite, (host, instance) => host.RenameManagedInstance(instance, newName), ct);
    /// <summary>null 恢复全局隔离设置；只改变设置，不搬移已有存档和模组。</summary>
    public Task<bool> SetInstanceIsolationAsync(string instanceId, bool? isolated, CancellationToken ct = default) =>
        InstanceAsync(instanceId, PluginPermissionNames.InstancesWrite, (host, instance) => host.SetInstanceIsolation(instance, isolated), ct);
    public Task<PluginMod> SetModEnabledAsync(string instanceId, string fileName, bool enabled, CancellationToken ct = default) =>
        InstanceAsync(instanceId, PluginPermissionNames.ModsWrite, (host, instance) => host.SetManagedModEnabled(instance, fileName, enabled), ct);
    public Task<ImmutableArray<PluginJavaRuntime>> GetJavaRuntimesAsync(CancellationToken ct = default) =>
        UiAsync(PluginPermissionNames.JavaRead, host => host.CopyManagedJavas(), ct);
    /// <summary>instanceId 为空时设置全局默认；javaId 为空则清除覆盖并恢复自动/全局选择。</summary>
    public async Task<bool> SelectJavaAsync(string? javaId, string? instanceId = null, CancellationToken ct = default)
    {
        if (instanceId == null)
            return await UiAsync(PluginPermissionNames.JavaSelect, host => host.SelectManagedJava(javaId, null), ct).ConfigureAwait(false);
        return await InstanceAsync(instanceId, PluginPermissionNames.JavaSelect,
            (host, instance) => host.SelectManagedJava(javaId, instance), ct).ConfigureAwait(false);
    }
    public Task<PluginDownloadDetails> GetDownloadAsync(Guid downloadId, CancellationToken ct = default) =>
        UiAsync(PluginPermissionNames.DownloadsRead, host => host.CopyDownloadDetails(downloadId), ct);
    public Task<PluginDownloadDetails> PauseDownloadAsync(Guid downloadId, CancellationToken ct = default) =>
        UiAsync(PluginPermissionNames.DownloadsControl, host => host.ControlDownload(downloadId, "pause"), ct);
    public Task<PluginDownloadDetails> ResumeDownloadAsync(Guid downloadId, CancellationToken ct = default) =>
        UiAsync(PluginPermissionNames.DownloadsControl, host => host.ControlDownload(downloadId, "resume"), ct);
    public Task<bool> CancelDownloadAsync(Guid downloadId, CancellationToken ct = default) =>
        UiAsync(PluginPermissionNames.DownloadsControl, host => host.CancelManagedDownload(downloadId), ct);
    /// <summary>仅清理已完成、失败和取消的列表记录；不删除已下载文件。</summary>
    public Task<int> ClearFinishedDownloadsAsync(CancellationToken ct = default) =>
        UiAsync(PluginPermissionNames.DownloadsControl, host => host.ClearFinishedDownloads(), ct);
    public async Task<PluginDownloadAcceptance> RepairInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.InstancesWrite, PluginPermissionNames.ResourcesInstall);
        var host = _context.Host;
        await host.GetInstancesAsync(new(true), scope.Token).ConfigureAwait(false);
        return await host.OnUiAsync(() =>
        {
            scope.Check();
            var instance = host.RequireManagedInstance(instanceId);
            host.EnsureInstanceWritable(instance);
            var folder = new Models.GameFolder { Path = instance.Root, Name = instance.Root };
            var item = DownloadQueueService.Instance.StartNew("补全文件 " + instance.VersionId, async (item, token) =>
            {
                using var running = new PluginOperationScope(_context, token, PluginPermissionNames.InstancesWrite, PluginPermissionNames.ResourcesInstall);
                await host.OnUiAsync(() =>
                {
                    running.Check();
                    host.EnsureInstanceWritable(host.RequireManagedInstance(instanceId), checkDownloads: false);
                    return true;
                }, running.Token);
                var error = await host.Owner.RepairInstanceWithoutLaunchAsync(folder, instance.VersionId, item.CreateProgress(), running.Token);
                running.Check();
                if (error != null) throw new PluginOperationException("RepairFailed", host.Safe(error));
                host.NotifyInstanceCreated(instance.Root, instance.VersionId);
            }, instance.VersionId, instance.Root);
            return new PluginDownloadAcceptance(item.Id, "Queued");
        }, scope.Token).ConfigureAwait(false);
    }
}

/// <summary>每次操作绑定插件生命周期和授权版本；撤销授权会取消正在进行的网络操作。</summary>
internal sealed class PluginOperationScope : IDisposable
{
    private readonly PluginContext _context;
    private readonly string[] _permissions;
    private readonly long _revision;
    private readonly CancellationTokenSource _linked;
    internal CancellationToken Token => _linked.Token;
    internal PluginOperationScope(PluginContext context, CancellationToken ct, params string[] permissions)
    {
        _context = context; _permissions = permissions;
        foreach (var permission in permissions) context.Permissions.Demand(permission);
        _revision = Interlocked.Read(ref context.AuthorizationRevision);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Stopping);
        context.AuthorizationChanged += OnAuthorizationChanged;
        try { Check(); }
        catch { Dispose(); throw; }
    }
    private void OnAuthorizationChanged() { try { _linked.Cancel(); } catch (ObjectDisposedException) { } }
    internal void Check()
    {
        foreach (var permission in _permissions) _context.Permissions.Demand(permission);
        if (_revision != Interlocked.Read(ref _context.AuthorizationRevision))
            throw new PluginOperationException("AuthorizationChanged", "操作期间授权已变更，请重新请求。");
        Token.ThrowIfCancellationRequested();
    }
    public void Dispose() { _context.AuthorizationChanged -= OnAuthorizationChanged; _linked.Dispose(); }
}
