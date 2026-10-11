using System.Collections.Immutable;

namespace XCL2.App.Services.Plugins;

/// <summary>API 2.1：受授权控制的启动器操作，不暴露窗口、配置或凭据对象。</summary>
public sealed class PluginControlService
{
    private readonly PluginContext _context;
    internal PluginControlService(PluginContext context) => _context = context;

    private async Task<T> InvokeAsync<T>(string permission, Func<PluginHostBridge, T> action, CancellationToken ct)
    {
        _context.Permissions.Demand(permission);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping);
        var host = _context.Host;
        return await host.OnUiAsync(() =>
        {
            // 调用排队时可能撤销授权；在真正改动之前再次检查。
            linked.Token.ThrowIfCancellationRequested();
            _context.Permissions.Demand(permission);
            return action(host);
        }, linked.Token).ConfigureAwait(false);
    }

    public Task<ImmutableArray<PluginPageInfo>> GetPagesAsync(CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.UiNavigation, host => host.Owner.GetPluginNavigablePages(), ct);
    /// <summary>通过宿主正常页面导航，保留设置页的保存/放弃确认。</summary>
    public Task<bool> NavigateAsync(string pageId, CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.UiNavigation, host => host.Owner.NavigateFromPlugin(pageId), ct);
    public Task<PluginControlSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.LauncherControl, host => host.CopyControlSnapshot(), ct);
    public Task<PluginWindowSnapshot> GetWindowAsync(CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.UiWindow, host => host.Owner.CopyPluginWindowSnapshot(), ct);
    public Task<PluginWindowSnapshot> UpdateWindowAsync(PluginWindowPatch patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return InvokeAsync(PluginPermissionNames.UiWindow, host => host.Owner.UpdateWindowFromPlugin(patch), ct);
    }
    public Task<PluginWindowSnapshot> SetWindowStateAsync(PluginWindowAction action, CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.UiWindow, host => host.Owner.SetWindowStateFromPlugin(action), ct);
    public Task<PluginAppearanceSnapshot> GetAppearanceAsync(CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.SettingsRead, host => host.CopyAppearance(), ct);
    public Task<PluginAppearanceSnapshot> UpdateSettingsAsync(PluginSettingsPatch patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return InvokeAsync(PluginPermissionNames.SettingsWrite, host => host.UpdateSettings(patch), ct);
    }
    public Task<bool> SelectAccountAsync(string accountId, CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.LauncherControl, host => host.SelectAccount(accountId), ct);
    public async Task<bool> SelectInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.LauncherControl);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping);
        await _context.Host.GetInstancesAsync(new(true), linked.Token).ConfigureAwait(false);
        return await InvokeAsync(PluginPermissionNames.LauncherControl, host => host.SelectInstance(instanceId), linked.Token).ConfigureAwait(false);
    }
    /// <summary>Accepted 只表示已进入宿主启动流程；成功与否订阅 PluginGameStarted/PluginGameExited。</summary>
    public Task<PluginLaunchAcceptance> LaunchSelectedAsync(CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.LauncherControl, host => host.Owner.LaunchFromPlugin(), ct);
    public Task<bool> CancelLaunchAsync(CancellationToken ct = default) =>
        InvokeAsync(PluginPermissionNames.LauncherControl, host => host.Owner.CancelLaunchFromPlugin(), ct);
}
