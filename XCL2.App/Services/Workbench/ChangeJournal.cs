using System.IO;
using System.Text.Json;

namespace XCL2.App.Services.Workbench;

public sealed class FileChange
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string State { get; set; } = "Prepared";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Durable byte-for-byte backup before commit; optimistic conflict checks for apply and undo.</summary>
public sealed class ChangeJournal(string directory)
{
    private static readonly object Gate = new();
    private string Dir(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$")
        ? System.IO.Path.Combine(directory, id) : throw new ArgumentException("变更 ID 无效。");
    private void Save(FileChange change) => ProjectFiles.AtomicWrite(System.IO.Path.Combine(Dir(change.Id), "change.json"), JsonSerializer.SerializeToUtf8Bytes(change));
    public FileChange Get(string id) => JsonSerializer.Deserialize<FileChange>(File.ReadAllBytes(System.IO.Path.Combine(Dir(id), "change.json"))) ?? throw new InvalidDataException();
    public IReadOnlyList<FileChange> List()
    {
        lock (Gate)
        {
            if (!Directory.Exists(directory)) return Array.Empty<FileChange>();
            var list = new List<FileChange>();
            foreach (var dir in Directory.EnumerateDirectories(directory))
                try { list.Add(Get(System.IO.Path.GetFileName(dir))); } catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
            return list.OrderByDescending(x => x.CreatedUtc).ToArray();
        }
    }
    public byte[]? Before(string id) => Get(id).BeforeHash == null ? null : ProjectFiles.ReadBytes(System.IO.Path.Combine(Dir(id), "before.bin"));
    public byte[]? After(string id) => Get(id).AfterHash == null ? null : ProjectFiles.ReadBytes(System.IO.Path.Combine(Dir(id), "after.bin"));
    public FileChange Prepare(string path, byte[]? after, string? expectedHash = null)
    {
        lock (Gate)
        {
            path = System.IO.Path.GetFullPath(path);
            ProjectFiles.Resolve(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path));
            var before = File.Exists(path) ? ProjectFiles.ReadBytes(path) : null;
            var beforeHash = before == null ? null : ProjectFiles.Hash(before);
            if (expectedHash != null && !string.Equals(expectedHash, beforeHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件已变化，请重新读取后生成补丁。");
            if (after?.LongLength > ProjectFiles.MaxFileBytes) throw new IOException("变更超过 16 MiB 上限。");
            var change = new FileChange { Path = path, BeforeHash = beforeHash, AfterHash = after == null ? null : ProjectFiles.Hash(after) };
            if (change.BeforeHash == change.AfterHash) throw new InvalidOperationException("文件内容没有变化。");
            Directory.CreateDirectory(Dir(change.Id));
            if (before != null) ProjectFiles.AtomicWrite(System.IO.Path.Combine(Dir(change.Id), "before.bin"), before);
            if (after != null) ProjectFiles.AtomicWrite(System.IO.Path.Combine(Dir(change.Id), "after.bin"), after);
            Save(change); return change;
        }
    }
    private static string? CurrentHash(string path) => File.Exists(path) ? ProjectFiles.HashFile(path) : null;
    private static void ValidateTarget(FileChange change) => ProjectFiles.Resolve(System.IO.Path.GetDirectoryName(change.Path)!, System.IO.Path.GetFileName(change.Path));
    private static void VerifyPayload(byte[]? data, string? hash)
    { if ((data == null ? null : ProjectFiles.Hash(data)) != hash) throw new IOException("备份校验失败，未修改文件。"); }
    public FileChange Apply(string id, Action<string>? authorize = null)
    {
        lock (Gate)
        {
            var change = Get(id); authorize?.Invoke(change.Path); ValidateTarget(change);
            if (change.State != "Prepared") throw new InvalidOperationException("变更已处理，不能重复提交。");
            if (CurrentHash(change.Path) != change.BeforeHash) throw new IOException("预览后文件被修改，已停止提交。");
            var after = After(id); VerifyPayload(after, change.AfterHash);
            change.State = "Applying"; Save(change);
            authorize?.Invoke(change.Path); ValidateTarget(change);
            if (CurrentHash(change.Path) != change.BeforeHash) throw new IOException("提交前文件被修改。");
            if (after == null) File.Delete(change.Path); else ProjectFiles.AtomicWrite(change.Path, after);
            change.State = "Applied"; Save(change); return change;
        }
    }
    public FileChange Rollback(string id, Action<string>? authorize = null)
    {
        lock (Gate)
        {
            var change = Get(id); authorize?.Invoke(change.Path); ValidateTarget(change);
            if (change.State is not ("Applied" or "Applying" or "RollingBack")) throw new InvalidOperationException("变更尚未应用或已经回滚。");
            var hash = CurrentHash(change.Path);
            if (change.State is "Applying" or "RollingBack" && hash == change.BeforeHash)
            { change.State = "RolledBack"; Save(change); return change; }
            if (hash != change.AfterHash) throw new IOException("文件有后续修改，回滚已停止；请先保存或合并这些修改。");
            var before = Before(id); VerifyPayload(before, change.BeforeHash);
            change.State = "RollingBack"; Save(change);
            authorize?.Invoke(change.Path); ValidateTarget(change);
            if (CurrentHash(change.Path) != change.AfterHash) throw new IOException("回滚前文件被修改。");
            if (before == null) File.Delete(change.Path); else ProjectFiles.AtomicWrite(change.Path, before);
            change.State = "RolledBack"; Save(change); return change;
        }
    }
}
