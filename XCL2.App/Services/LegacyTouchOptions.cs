using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Services.Workbench;

namespace XCL2.App.Services;

/// <summary>Temporarily use the legacy cursor-message path; restore only rawMouseInput, never the whole options file.</summary>
internal sealed class LegacyTouchOptions : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Users = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Raw = new(@"(?m)^rawMouseInput:[^\r\n]*(?:\r?\n|$)");
    private readonly string _dir;
    private bool _disposed;
    private sealed record Identity(int Pid, long Start);
    private sealed record Backup(string? Line, int OwnerPid, long OwnerStart, List<Identity> Games);
    private LegacyTouchOptions(string dir) => _dir = dir;
    private static string Record(string dir) => Path.Combine(dir, "xcl", "touch-backup", "legacy-options.json");
    private static bool ProcessAlive(int pid, long start)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime().Ticks == start; }
        catch { return false; }
    }
    private static bool OwnerAlive(Backup backup)
    {
        return ProcessAlive(backup.OwnerPid, backup.OwnerStart) || (backup.Games ?? new()).Any(g => ProcessAlive(g.Pid, g.Start));
    }
    public void Attach(Process process)
    {
        lock (Gate)
        {
            if (_disposed || process.HasExited) return;
            var record = Record(_dir);
            var saved = JsonSerializer.Deserialize<Backup>(File.ReadAllText(record))!;
            saved.Games.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks));
            ProjectFiles.AtomicWrite(record, JsonSerializer.SerializeToUtf8Bytes(saved));
        }
    }
    private static void Restore(string dir)
    {
        var record = Record(dir); if (!File.Exists(record)) return;
        var saved = JsonSerializer.Deserialize<Backup>(File.ReadAllText(record))!;
        var options = Path.Combine(dir, "options.txt");
        if (File.Exists(options))
        {
            var bytes = ProjectFiles.ReadBytes(options); var text = ProjectFiles.Decode(bytes).Text;
            var matches = Raw.Matches(text);
            if (matches.Count > 0 && matches.Cast<Match>().All(m => m.Value.TrimEnd('\r', '\n') == "rawMouseInput:false"))
                ProjectFiles.AtomicWrite(options, ProjectFiles.EncodeLike(Raw.Replace(text, saved.Line == null ? "" : saved.Line.TrimEnd('\r', '\n') + (text.Contains("\r\n") ? "\r\n" : "\n"), 1), bytes));
        }
        File.Delete(record);
    }
    public static LegacyTouchOptions Acquire(string directory)
    {
        var dir = Path.GetFullPath(directory);
        lock (Gate)
        {
            if (Users.TryGetValue(dir, out var users)) { Users[dir] = users + 1; return new(dir); }
            var record = Record(dir);
            if (File.Exists(record))
            {
                var saved = JsonSerializer.Deserialize<Backup>(File.ReadAllText(record))!;
                if (OwnerAlive(saved)) throw new IOException("另一个启动器正在使用此目录的旧版触屏配置。");
                Restore(dir);
            }
            var path = Path.Combine(dir, "options.txt"); var bytes = File.Exists(path) ? ProjectFiles.ReadBytes(path) : null;
            var text = bytes == null ? "" : ProjectFiles.Decode(bytes).Text;
            var old = Raw.Match(text);
            using var current = Process.GetCurrentProcess();
            ProjectFiles.AtomicWrite(record, JsonSerializer.SerializeToUtf8Bytes(new Backup(old.Success ? old.Value : null, current.Id, current.StartTime.ToUniversalTime().Ticks, new())));
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var updated = old.Success ? Raw.Replace(text, "rawMouseInput:false" + newline) : text + (text.Length > 0 && !text.EndsWith('\n') ? newline : "") + "rawMouseInput:false" + newline;
            try { ProjectFiles.AtomicWrite(path, ProjectFiles.EncodeLike(updated, bytes)); }
            catch { File.Delete(record); throw; }
            Users[dir] = 1;
            LauncherLogService.AppendLine("[旧版触屏] 本次运行使用传统鼠标消息；退出游戏后恢复原始输入选项。");
            return new(dir);
        }
    }
    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return; _disposed = true;
            if (--Users[_dir] > 0) return; Users.Remove(_dir);
            try { Restore(_dir); } catch (Exception ex) { LauncherLogService.AppendLine("[旧版触屏] 配置恢复失败，将在下次启动重试：" + ex.Message); }
        }
    }
}
