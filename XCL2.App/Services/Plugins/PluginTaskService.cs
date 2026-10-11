using System.IO;
using XCL2.App.Services.Workbench;
namespace XCL2.App.Services.Plugins;
public sealed class PluginTaskService(PluginContext context)
{
    private TaskStore Store { get { context.Permissions.Demand(PluginPermissionNames.TasksManage); return new(Path.Combine(context.DataDirectory, ".xcl2", "tasks")); } }
    private WorkTask Edit(string id, Action<WorkTask> edit) => Store.Update(id, edit);
    private static WorkStep Step(WorkTask task, string stepId) => task.Steps.FirstOrDefault(s => s.Id == stepId) ?? throw new ArgumentException("步骤不存在。");
    [WorkbenchEndpoint("workbench.tasks.create")]
    public WorkTask Create(string title, string goal, string projectRoot = "") => Store.Create(title, goal, projectRoot);
    [WorkbenchEndpoint("workbench.tasks.get")]
    public WorkTask Get(string id) => Store.Get(id);
    [WorkbenchEndpoint("workbench.tasks.list")]
    public IReadOnlyList<WorkTask> List() => Store.List();
    [WorkbenchEndpoint("workbench.tasks.find")]
    public IReadOnlyList<WorkTask> Find(string query) => List().Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Goal.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    [WorkbenchEndpoint("workbench.tasks.active")]
    public IReadOnlyList<WorkTask> GetActive() => List().Where(t => t.Status is "running" or "paused" or "interrupted").ToArray();
    [WorkbenchEndpoint("workbench.tasks.completed")]
    public IReadOnlyList<WorkTask> GetCompleted() => List().Where(t => t.Status == "completed").ToArray();
    [WorkbenchEndpoint("workbench.tasks.archived")]
    public IReadOnlyList<WorkTask> GetArchived() => List().Where(t => t.Status == "archived").ToArray();
    [WorkbenchEndpoint("workbench.tasks.rename")]
    public WorkTask Rename(string id, string title) => Edit(id, t => t.Title = title);
    [WorkbenchEndpoint("workbench.tasks.goal")]
    public WorkTask SetGoal(string id, string goal) => Edit(id, t => t.Goal = goal);
    [WorkbenchEndpoint("workbench.tasks.project")]
    public WorkTask SetProjectRoot(string id, string projectRoot) => Edit(id, t => t.ProjectRoot = projectRoot);
    [WorkbenchEndpoint("workbench.tasks.mode")]
    public WorkTask SetMode(string id, string mode) => Edit(id, t => t.Mode = mode);
    [WorkbenchEndpoint("workbench.tasks.nextAction")]
    public WorkTask SetNextAction(string id, string nextAction) => Edit(id, t => t.NextAction = nextAction);
    [WorkbenchEndpoint("workbench.tasks.notes")]
    public WorkTask SetNotes(string id, string notes) => Edit(id, t => t.Notes = notes);
    [WorkbenchEndpoint("workbench.tasks.appendNotes")]
    public WorkTask AppendNotes(string id, string notes) => Edit(id, t => t.Notes += "\n" + notes);
    [WorkbenchEndpoint("workbench.tasks.addStep")]
    public WorkTask AddStep(string id, string title) => Edit(id, t => t.Steps.Add(new WorkStep { Title = title }));
    [WorkbenchEndpoint("workbench.tasks.removeStep")]
    public WorkTask RemoveStep(string id, string stepId) => Edit(id, t => t.Steps.Remove(Step(t, stepId)));
    [WorkbenchEndpoint("workbench.tasks.renameStep")]
    public WorkTask RenameStep(string id, string stepId, string title) => Edit(id, t => Step(t, stepId).Title = title);
    [WorkbenchEndpoint("workbench.tasks.stepStatus")]
    public WorkTask SetStepStatus(string id, string stepId, string status) => Edit(id, t => Step(t, stepId).Status = status);
    [WorkbenchEndpoint("workbench.tasks.stepEvidence")]
    public WorkTask SetStepEvidence(string id, string stepId, string evidence) => Edit(id, t => Step(t, stepId).Evidence = evidence);
    [WorkbenchEndpoint("workbench.tasks.replaceSteps")]
    public WorkTask ReplaceSteps(string id, List<WorkStep> steps) => Edit(id, t => t.Steps = steps);
    [WorkbenchEndpoint("workbench.tasks.steps")]
    public IReadOnlyList<WorkStep> GetSteps(string id) => Get(id).Steps;
    [WorkbenchEndpoint("workbench.tasks.pendingSteps")]
    public IReadOnlyList<WorkStep> GetPendingSteps(string id) => Get(id).Steps.Where(s => s.Status == "pending").ToArray();
    [WorkbenchEndpoint("workbench.tasks.completedSteps")]
    public IReadOnlyList<WorkStep> GetCompletedSteps(string id) => Get(id).Steps.Where(s => s.Status == "completed").ToArray();
    [WorkbenchEndpoint("workbench.tasks.checkpoint")]
    public WorkTask Checkpoint(string id, string text) => Store.Checkpoint(id, text);
    [WorkbenchEndpoint("workbench.tasks.checkpoints")]
    public IReadOnlyList<string> GetCheckpoints(string id) => Get(id).Checkpoints;
    [WorkbenchEndpoint("workbench.tasks.pause")]
    public WorkTask Pause(string id) => Edit(id, t => t.Status = "paused");
    [WorkbenchEndpoint("workbench.tasks.resume")]
    public WorkTask Resume(string id) => Edit(id, t => t.Status = "running");
    [WorkbenchEndpoint("workbench.tasks.complete")]
    public WorkTask Complete(string id) => Edit(id, t => { if (t.Steps.Any(s => s.Status != "completed")) throw new InvalidOperationException("仍有未完成步骤。"); t.Status = "completed"; });
    [WorkbenchEndpoint("workbench.tasks.archive")]
    public WorkTask Archive(string id) => Edit(id, t => t.Status = "archived");
    [WorkbenchEndpoint("workbench.tasks.addChange")]
    public WorkTask AddChange(string id, string changeId) => Edit(id, t => t.ChangeIds.Add(changeId));
    [WorkbenchEndpoint("workbench.tasks.changes")]
    public IReadOnlyList<string> GetChanges(string id) => Get(id).ChangeIds;
    [WorkbenchEndpoint("workbench.tasks.progress")]
    public double GetProgress(string id) => Get(id) is var t && t.Steps.Count > 0 ? 100.0 * t.Steps.Count(s => s.Status == "completed") / t.Steps.Count : 0;
    [WorkbenchEndpoint("workbench.tasks.clone")]
    public WorkTask Clone(string id, string title) => Get(id) is var original ? Copy(original, title) : throw new ArgumentException();
    private WorkTask Copy(WorkTask original, string title)
    {
        var copy = Store.Create(title, original.Goal, original.ProjectRoot);
        copy.Steps = original.Steps.Select(s => new WorkStep { Title = s.Title }).ToList();
        copy.Notes = original.Notes; Store.Save(copy); return copy;
    }
}
