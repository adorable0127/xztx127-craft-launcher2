using System.Collections.Immutable;
namespace XCL2.App.Services.Plugins;
public sealed class PluginQueryService(PluginContext context)
{
    private async Task<ImmutableArray<PluginInstance>> Instances(CancellationToken ct) => (await context.Launcher.Instances.GetSnapshotAsync(new(true, true), ct).ConfigureAwait(false)).Items;
    private async Task<ImmutableArray<PluginMod>> Mods(string instanceId, CancellationToken ct) => (await context.Launcher.Mods.GetSnapshotAsync(instanceId, ct).ConfigureAwait(false)).Items;
    private async Task<ImmutableArray<PluginDownload>> Downloads(CancellationToken ct) => (await context.Launcher.Downloads.GetSnapshotAsync(ct).ConfigureAwait(false)).Items;
    private async Task<ImmutableArray<PluginAccount>> Accounts(CancellationToken ct) => (await context.Launcher.Accounts.GetSnapshotAsync(ct).ConfigureAwait(false)).Items;
    [WorkbenchEndpoint("workbench.query.instances.installed")]
    public async Task<IReadOnlyList<PluginInstance>> GetInstalledInstances(CancellationToken ct = default) => (await Instances(ct)).Where(i => i.IsInstalled).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.incomplete")]
    public async Task<IReadOnlyList<PluginInstance>> GetIncompleteInstances(CancellationToken ct = default) => (await Instances(ct)).Where(i => !i.IsInstalled).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.hidden")]
    public async Task<IReadOnlyList<PluginInstance>> GetHiddenInstances(CancellationToken ct = default) => (await Instances(ct)).Where(i => i.IsHidden).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.visible")]
    public async Task<IReadOnlyList<PluginInstance>> GetVisibleInstances(CancellationToken ct = default) => (await Instances(ct)).Where(i => !i.IsHidden).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.vanilla")]
    public async Task<IReadOnlyList<PluginInstance>> GetVanillaInstances(CancellationToken ct = default) => (await Instances(ct)).Where(i => string.IsNullOrWhiteSpace(i.LoaderId)).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.byName")]
    public async Task<IReadOnlyList<PluginInstance>> FindInstancesByName(string name, CancellationToken ct = default) => (await Instances(ct)).Where(i => i.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.byVersion")]
    public async Task<IReadOnlyList<PluginInstance>> FindInstancesByVersion(string version, CancellationToken ct = default) => (await Instances(ct)).Where(i => i.MinecraftVersion == version).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.byLoader")]
    public async Task<IReadOnlyList<PluginInstance>> FindInstancesByLoader(string loader, CancellationToken ct = default) => (await Instances(ct)).Where(i => string.Equals(i.LoaderId, loader, StringComparison.OrdinalIgnoreCase)).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.get")]
    public async Task<PluginInstance?> GetInstance(string id, CancellationToken ct = default) => (await Instances(ct)).FirstOrDefault(i => i.Id == id);
    [WorkbenchEndpoint("workbench.query.instances.exists")]
    public async Task<bool> InstanceExists(string id, CancellationToken ct = default) => (await Instances(ct)).Any(i => i.Id == id);
    [WorkbenchEndpoint("workbench.query.instances.count")]
    public async Task<int> GetInstanceCount(CancellationToken ct = default) => (await Instances(ct)).Length;
    [WorkbenchEndpoint("workbench.query.instances.versions")]
    public async Task<IReadOnlyList<string>> GetMinecraftVersions(CancellationToken ct = default) => (await Instances(ct)).Select(i => i.MinecraftVersion).Distinct().OrderBy(x => x).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.loaders")]
    public async Task<IReadOnlyList<string>> GetLoaders(CancellationToken ct = default) => (await Instances(ct)).Select(i => i.LoaderId ?? "vanilla").Distinct().OrderBy(x => x).ToArray();
    [WorkbenchEndpoint("workbench.query.instances.page")]
    public async Task<IReadOnlyList<PluginInstance>> GetInstancePage(int offset = 0, int limit = 100, CancellationToken ct = default) => (await Instances(ct)).OrderBy(i => i.Name).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToArray();
    [WorkbenchEndpoint("workbench.query.mods.enabled")]
    public async Task<IReadOnlyList<PluginMod>> GetEnabledMods(string instanceId, CancellationToken ct = default) => (await Mods(instanceId, ct)).Where(m => m.IsEnabled).ToArray();
    [WorkbenchEndpoint("workbench.query.mods.disabled")]
    public async Task<IReadOnlyList<PluginMod>> GetDisabledMods(string instanceId, CancellationToken ct = default) => (await Mods(instanceId, ct)).Where(m => !m.IsEnabled).ToArray();
    [WorkbenchEndpoint("workbench.query.mods.byId")]
    public async Task<IReadOnlyList<PluginMod>> FindModsById(string instanceId, string modId, CancellationToken ct = default) => (await Mods(instanceId, ct)).Where(m => m.ModId == modId).ToArray();
    [WorkbenchEndpoint("workbench.query.mods.byName")]
    public async Task<IReadOnlyList<PluginMod>> FindModsByName(string instanceId, string name, CancellationToken ct = default) => (await Mods(instanceId, ct)).Where(m => m.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase) || m.FileName.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
    [WorkbenchEndpoint("workbench.query.mods.file")]
    public async Task<PluginMod?> GetModByFile(string instanceId, string fileName, CancellationToken ct = default) => (await Mods(instanceId, ct)).FirstOrDefault(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    [WorkbenchEndpoint("workbench.query.mods.count")]
    public async Task<int> GetModCount(string instanceId, CancellationToken ct = default) => (await Mods(instanceId, ct)).Length;
    [WorkbenchEndpoint("workbench.query.mods.bytes")]
    public async Task<long> GetModBytes(string instanceId, CancellationToken ct = default) => (await Mods(instanceId, ct)).Sum(m => m.FileSizeBytes);
    [WorkbenchEndpoint("workbench.query.mods.largest")]
    public async Task<IReadOnlyList<PluginMod>> GetLargestMods(string instanceId, int count = 20, CancellationToken ct = default) => (await Mods(instanceId, ct)).OrderByDescending(m => m.FileSizeBytes).Take(Math.Clamp(count, 1, 200)).ToArray();
    [WorkbenchEndpoint("workbench.query.mods.duplicateIds")]
    public async Task<IReadOnlyList<string>> GetDuplicateModIds(string instanceId, CancellationToken ct = default) => (await Mods(instanceId, ct)).Where(m => m.ModId != null).GroupBy(m => m.ModId!).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
    [WorkbenchEndpoint("workbench.query.mods.page")]
    public async Task<IReadOnlyList<PluginMod>> GetModPage(string instanceId, int offset = 0, int limit = 100, CancellationToken ct = default) => (await Mods(instanceId, ct)).OrderBy(m => m.FileName).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToArray();
    [WorkbenchEndpoint("workbench.query.downloads.active")]
    public async Task<IReadOnlyList<PluginDownload>> GetActiveDownloads(CancellationToken ct = default) => (await Downloads(ct)).Where(d => d.State == PluginDownloadState.Downloading).ToArray();
    [WorkbenchEndpoint("workbench.query.downloads.paused")]
    public async Task<IReadOnlyList<PluginDownload>> GetPausedDownloads(CancellationToken ct = default) => (await Downloads(ct)).Where(d => d.State == PluginDownloadState.Paused).ToArray();
    [WorkbenchEndpoint("workbench.query.downloads.completed")]
    public async Task<IReadOnlyList<PluginDownload>> GetCompletedDownloads(CancellationToken ct = default) => (await Downloads(ct)).Where(d => d.State == PluginDownloadState.Completed).ToArray();
    [WorkbenchEndpoint("workbench.query.downloads.failed")]
    public async Task<IReadOnlyList<PluginDownload>> GetFailedDownloads(CancellationToken ct = default) => (await Downloads(ct)).Where(d => d.State == PluginDownloadState.Failed).ToArray();
    [WorkbenchEndpoint("workbench.query.downloads.canceled")]
    public async Task<IReadOnlyList<PluginDownload>> GetCanceledDownloads(CancellationToken ct = default) => (await Downloads(ct)).Where(d => d.State == PluginDownloadState.Canceled).ToArray();
    [WorkbenchEndpoint("workbench.query.downloads.instance")]
    public async Task<IReadOnlyList<PluginDownload>> GetInstanceDownloads(string instanceId, CancellationToken ct = default) => (await Downloads(ct)).Where(d => d.InstanceId == instanceId).ToArray();
    [WorkbenchEndpoint("workbench.query.downloads.get")]
    public async Task<PluginDownload?> GetDownload(Guid id, CancellationToken ct = default) => (await Downloads(ct)).FirstOrDefault(d => d.Id == id);
    [WorkbenchEndpoint("workbench.query.downloads.count")]
    public async Task<int> GetDownloadCount(CancellationToken ct = default) => (await Downloads(ct)).Length;
    [WorkbenchEndpoint("workbench.query.accounts.offline")]
    public async Task<IReadOnlyList<PluginAccount>> GetOfflineAccounts(CancellationToken ct = default) => (await Accounts(ct)).Where(a => a.Kind == PluginAccountKind.Offline).ToArray();
    [WorkbenchEndpoint("workbench.query.accounts.microsoft")]
    public async Task<IReadOnlyList<PluginAccount>> GetMicrosoftAccounts(CancellationToken ct = default) => (await Accounts(ct)).Where(a => a.Kind == PluginAccountKind.Microsoft).ToArray();
    [WorkbenchEndpoint("workbench.query.accounts.authserver")]
    public async Task<IReadOnlyList<PluginAccount>> GetAuthServerAccounts(CancellationToken ct = default) => (await Accounts(ct)).Where(a => a.Kind == PluginAccountKind.AuthServer).ToArray();
    [WorkbenchEndpoint("workbench.query.accounts.guest")]
    public async Task<IReadOnlyList<PluginAccount>> GetGuestAccounts(CancellationToken ct = default) => (await Accounts(ct)).Where(a => a.IsGuest).ToArray();
    [WorkbenchEndpoint("workbench.query.accounts.get")]
    public async Task<PluginAccount?> GetAccount(string id, CancellationToken ct = default) => (await Accounts(ct)).FirstOrDefault(a => a.Id == id);
    [WorkbenchEndpoint("workbench.query.accounts.find")]
    public async Task<IReadOnlyList<PluginAccount>> FindAccounts(string name, CancellationToken ct = default) => (await Accounts(ct)).Where(a => a.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
    [WorkbenchEndpoint("workbench.query.accounts.count")]
    public async Task<int> GetAccountCount(CancellationToken ct = default) => (await Accounts(ct)).Length;
}
