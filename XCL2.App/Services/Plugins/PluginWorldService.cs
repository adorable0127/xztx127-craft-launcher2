using System.Windows;
using XCL2.App.Services.Worlds;
using XCL2.App.Views;

namespace XCL2.App.Services.Plugins;

public sealed class PluginWorldService
{
    private readonly PluginContext _context;
    internal PluginWorldService(PluginContext context) { _context = context; }
    private void Demand(string permission)
    { _context.Permissions.Demand(permission); if (ConfigService.Active?.Config.EnableWorldPluginApi != true) throw new InvalidOperationException("存档 API 未由用户开启。"); }
    public async Task<IReadOnlyList<WorldArchive>> GetSnapshotAsync(CancellationToken ct = default)
    {
        Demand(PluginPermissionNames.WorldsRead); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping);
        var roots = await Application.Current.Dispatcher.InvokeAsync(WorldCatalog.GameRoots);
        var result = await Task.Run(() => WorldArchiveService.Discover(roots), linked.Token);
        linked.Token.ThrowIfCancellationRequested(); Demand(PluginPermissionNames.WorldsRead); return result;
    }
    public IReadOnlyList<WorldEditField> GetEditSchema()
    { Demand(PluginPermissionNames.WorldsRead); return WorldArchiveService.EditableFields; }
    public async Task<WorldArchive> GetAsync(string worldPath, CancellationToken ct = default)
    {
        Demand(PluginPermissionNames.WorldsRead); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping);
        await Application.Current.Dispatcher.InvokeAsync(() => WorldCatalog.RequireKnown(worldPath));
        var result = await Task.Run(() => WorldArchiveService.Read(worldPath), linked.Token);
        linked.Token.ThrowIfCancellationRequested(); Demand(PluginPermissionNames.WorldsRead); return result;
    }
    public async Task<string> UpdateAsync(string worldPath, WorldEdit edit, CancellationToken ct = default)
    {
        Demand(PluginPermissionNames.WorldsWrite); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping);
        edit = Freeze(edit);
        WorldArchiveService.Validate(edit);
        var approved = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            WorldCatalog.RequireKnown(worldPath);
            return MessageBoxDialog.ShowConfirm($"插件 {_context.Metadata.Name} 请求修改存档：\n{worldPath}\n{WorldArchiveService.DescribeEdit(edit)}\n请先退出游戏。启动器将自动备份 level.dat。", "插件存档修改请求");
        });
        if (!approved) throw new OperationCanceledException("用户拒绝修改存档。");
        linked.Token.ThrowIfCancellationRequested(); Demand(PluginPermissionNames.WorldsWrite);
        return await Task.Run(() => { linked.Token.ThrowIfCancellationRequested(); Demand(PluginPermissionNames.WorldsWrite); return WorldArchiveService.Update(worldPath, edit); }, linked.Token);
    }
    public async Task<string> GenerateCopyAsync(string worldPath, string destinationPath, WorldEdit edit, CancellationToken ct = default)
    {
        Demand(PluginPermissionNames.WorldsWrite); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping);
        edit = Freeze(edit);
        WorldArchiveService.Validate(edit);
        var approved = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            WorldCatalog.RequireKnown(worldPath); WorldCatalog.RequireKnown(destinationPath);
            return MessageBoxDialog.ShowConfirm($"插件 {_context.Metadata.Name} 请求生成存档副本：\n来源：{worldPath}\n目标：{destinationPath}\n{WorldArchiveService.DescribeEdit(edit)}\n保留已有区块，不覆盖现有目录。请先退出游戏。", "插件生成存档副本请求");
        });
        if (!approved) throw new OperationCanceledException("用户拒绝生成存档副本。");
        linked.Token.ThrowIfCancellationRequested(); Demand(PluginPermissionNames.WorldsWrite);
        return await Task.Run(() => { linked.Token.ThrowIfCancellationRequested(); Demand(PluginPermissionNames.WorldsWrite); return WorldArchiveService.GenerateCopy(worldPath, destinationPath, edit, linked.Token); }, linked.Token);
    }
    private static WorldEdit Freeze(WorldEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return edit with { GameRules = edit.GameRules?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
    }
}
