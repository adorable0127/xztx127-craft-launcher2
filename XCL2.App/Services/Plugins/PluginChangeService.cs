using System.IO;
using XCL2.App.Models;
using XCL2.App.Services.Workbench;
using XCL2.App.Views;

namespace XCL2.App.Services.Plugins;

public sealed class PluginChangeService(PluginContext context)
{
    private ChangeJournal Journal => new(Path.Combine(context.DataDirectory, ".xcl2", "changes"));
    private string Target(string projectId, string path)
    {
        context.Permissions.Demand(PluginPermissionNames.ProjectsWrite);
        return ProjectFiles.Resolve(context.Projects.Root(projectId), path);
    }
    private FileChange Require(string projectId, string changeId)
    {
        var change = Journal.Get(changeId);
        _ = Target(projectId, change.Path); return change;
    }
    [WorkbenchEndpoint("workbench.changes.prepareWrite")]
    public FileChange PrepareWrite(string projectId, string path, string content, string? expectedHash = null)
    {
        var target = Target(projectId, path); var before = File.Exists(target) ? ProjectFiles.ReadBytes(target) : null;
        return Journal.Prepare(target, ProjectFiles.EncodeLike(content, before), expectedHash);
    }
    [WorkbenchEndpoint("workbench.changes.preparePatch")]
    public FileChange PreparePatch(string projectId, string path, string sha256, string oldText, string newText)
    {
        if (string.IsNullOrWhiteSpace(sha256)) throw new ArgumentException("需要 sha256 校验值。");
        var target = Target(projectId, path); var bytes = ProjectFiles.ReadBytes(target);
        var content = ProjectFiles.Patch(ProjectFiles.Decode(bytes).Text, oldText, newText);
        return Journal.Prepare(target, ProjectFiles.EncodeLike(content, bytes), sha256);
    }
    [WorkbenchEndpoint("workbench.changes.prepareDelete")]
    public FileChange PrepareDelete(string projectId, string path, string sha256)
    { if (string.IsNullOrWhiteSpace(sha256)) throw new ArgumentException("需要 sha256 校验值。"); return Journal.Prepare(Target(projectId, path), null, sha256); }
    [WorkbenchEndpoint("workbench.changes.list")]
    public IReadOnlyList<FileChange> List(string projectId)
    {
        var root = context.Projects.Root(projectId); context.Permissions.Demand(PluginPermissionNames.ProjectsWrite);
        return Journal.List().Where(c => { try { ProjectFiles.Resolve(root, c.Path); return true; } catch { return false; } }).ToArray();
    }
    [WorkbenchEndpoint("workbench.changes.get")]
    public FileChange Get(string projectId, string changeId) => Require(projectId, changeId);
    [WorkbenchEndpoint("workbench.changes.before")]
    public byte[]? GetBefore(string projectId, string changeId)
    { _ = Require(projectId, changeId); var bytes = Journal.Before(changeId); return bytes?.Length > 65536 ? throw new IOException("备份超过 64 KiB，请使用差异预览。") : bytes; }
    [WorkbenchEndpoint("workbench.changes.after")]
    public byte[]? GetAfter(string projectId, string changeId)
    { _ = Require(projectId, changeId); var bytes = Journal.After(changeId); return bytes?.Length > 65536 ? throw new IOException("备份超过 64 KiB，请使用差异预览。") : bytes; }
    [WorkbenchEndpoint("workbench.changes.diff")]
    public AiFileDiff GetDiff(string projectId, string changeId)
    {
        var change = Require(projectId, changeId);
        string? Decode(byte[]? data) { if (data == null) return ""; try { return ProjectFiles.Decode(data).Text; } catch { return null; } }
        var before = Decode(Journal.Before(changeId)); var after = Decode(Journal.After(changeId));
        return AiFileDiffBuilder.Build(before, after,
            change.BeforeHash == null ? AiFileChangeKind.Created : change.AfterHash == null ? AiFileChangeKind.Deleted : AiFileChangeKind.Modified,
            unavailableReason: before == null || after == null ? "二进制内容无法显示文本差异；已保存完整备份。" : null);
    }
    [WorkbenchEndpoint("workbench.changes.apply")]
    public async Task<FileChange> ApplyAsync(string projectId, string changeId, CancellationToken ct = default)
    {
        using var scope = new PluginOperationScope(context, ct, PluginPermissionNames.ProjectsRead, PluginPermissionNames.ProjectsWrite);
        var change = Require(projectId, changeId); var diff = GetDiff(projectId, changeId);
        var approved = await context.Host.OnUiAsync(() => { scope.Check(); return AiWorkbenchDialogs.Review(System.Windows.Application.Current.MainWindow, change, diff); }, scope.Token).ConfigureAwait(false);
        if (!approved) throw new OperationCanceledException("用户取消修改。");
        return await Task.Run(() => Journal.Apply(changeId, path => { scope.Check(); _ = Target(projectId, path); }), scope.Token).ConfigureAwait(false);
    }
    [WorkbenchEndpoint("workbench.changes.rollback")]
    public async Task<FileChange> RollbackAsync(string projectId, string changeId, CancellationToken ct = default)
    {
        using var scope = new PluginOperationScope(context, ct, PluginPermissionNames.ProjectsRead, PluginPermissionNames.ProjectsWrite);
        var change = Require(projectId, changeId);
        var approved = await context.Host.OnUiAsync(() => { scope.Check(); return MessageBoxDialog.ShowConfirm("插件申请恢复修改前内容：\n" + change.Path + "\n如有后续修改将停止。", "插件回滚"); }, scope.Token).ConfigureAwait(false);
        if (!approved) throw new OperationCanceledException("用户取消回滚。");
        return await Task.Run(() => Journal.Rollback(changeId, path => { scope.Check(); _ = Target(projectId, path); }), scope.Token).ConfigureAwait(false);
    }
}
