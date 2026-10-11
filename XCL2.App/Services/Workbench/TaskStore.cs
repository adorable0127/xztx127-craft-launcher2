using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services.Workbench;

public sealed class WorkStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Evidence { get; set; } = "";
}
public sealed class WorkTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Goal { get; set; } = "";
    public string ProjectRoot { get; set; } = "";
    public string Mode { get; set; } = "Plan";
    public string Status { get; set; } = "paused";
    public string NextAction { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<WorkStep> Steps { get; set; } = new();
    public List<string> ChangeIds { get; set; } = new();
    public List<string> Checkpoints { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public long Revision { get; set; }
}
public sealed class TaskStore(string directory)
{
    private static readonly object Gate = new();
    private string PathFor(string id) => Regex.IsMatch(id, "^[a-f0-9]{32}$") ? Path.Combine(directory, id + ".json") : throw new ArgumentException("任务 ID 无效。");
    public WorkTask Get(string id) { lock (Gate) return JsonSerializer.Deserialize<WorkTask>(File.ReadAllBytes(PathFor(id))) ?? throw new InvalidDataException(); }
    public IReadOnlyList<WorkTask> List()
    {
        lock (Gate)
        {
            var list = new List<WorkTask>();
            if (Directory.Exists(directory)) foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                try { list.Add(Get(Path.GetFileNameWithoutExtension(path))); } catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
            return list.OrderByDescending(t => t.UpdatedUtc).ToArray();
        }
    }
    public WorkTask Create(string title, string goal, string projectRoot = "")
    { var task = new WorkTask { Title = title, Goal = goal, ProjectRoot = projectRoot }; Save(task); return task; }
    public void Save(WorkTask task)
    {
        lock (Gate)
        {
            if (task.Steps == null || task.Steps.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 128) || task.Steps.Select(s => s.Id).Distinct().Count() != task.Steps.Count) throw new ArgumentException("步骤 ID 必须唯一且非空。");
            if (task.Steps.Count > 100 || task.Steps.Count(s => s.Status == "in_progress") > 1 || task.Steps.Any(s => s.Status is not ("pending" or "in_progress" or "completed" or "blocked")))
                throw new ArgumentException("任务步骤状态无效，最多一个步骤可处于进行中。");
            if (task.Mode is not ("Plan" or "Build") || task.Status is not ("paused" or "running" or "completed" or "interrupted" or "archived")) throw new ArgumentException("任务状态无效。");
            if (task.Title.Length > 300 || task.Goal.Length > 20000 || task.Notes.Length > 20000 || task.NextAction.Length > 10000 || task.Steps.Any(s => s.Title.Length > 1000 || s.Evidence.Length > 4000)) throw new ArgumentException("任务文本过长。");
            var path = PathFor(task.Id);
            if (File.Exists(path) && Get(task.Id).Revision != task.Revision) throw new IOException("任务已被其他会话更新，请重新加载。");
            task.Checkpoints = task.Checkpoints.TakeLast(100).ToList(); task.ChangeIds = task.ChangeIds.Distinct().TakeLast(1000).ToList();
            var oldRevision = task.Revision; task.UpdatedUtc = DateTime.UtcNow; task.Revision++;
            try { ProjectFiles.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(task)); } catch { task.Revision = oldRevision; throw; }
        }
    }
    public WorkTask Update(string id, Action<WorkTask> edit)
    { lock (Gate) { var task = Get(id); edit(task); Save(task); return task; } }
    public WorkTask Checkpoint(string id, string text) => Update(id, t => t.Checkpoints.Add(DateTime.UtcNow.ToString("O") + " " + text[..Math.Min(text.Length, 4000)]));
}
