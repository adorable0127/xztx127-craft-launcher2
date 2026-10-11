using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    private void RefreshTaskUi()
    {
        if (TaskSummaryText == null) return;
        try
        {
            var task = _service?.GetTask(_session);
            TaskSummaryText.Text = task == null ? "任务步骤会自动保存；可从历史对话或“跨会话续做”恢复。" :
                $"{task.Title} · {task.Steps.Count(s => s.Status == "completed")}/{task.Steps.Count} 步完成 · {task.Mode}\n" +
                string.Join("\n", task.Steps.Select((s, i) => $"{i + 1}. [{s.Status}] {s.Title}")) +
                (string.IsNullOrWhiteSpace(task.NextAction) ? "" : "\n下一步：" + task.NextAction);
            ResumeTaskButton.IsEnabled = !_sending;
            ChangeHistoryButton.IsEnabled = !_sending;
        }
        catch (Exception ex) { TaskSummaryText.Text = "读取任务断点失败：" + ex.Message; }
    }
    private void ResumeTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending) return;
        try
        {
            var task = _service.GetTask(_session) ?? _service.Tasks.List().FirstOrDefault(t => t.Status != "archived");
            if (task == null) { MessageBoxDialog.ShowInfo("暂无已保存任务。", "继续任务"); return; }
            _session = _service.ResumeTask(task.Id);
            RememberCurrentSession(); RebuildBubbles(); RefreshHistory(); RefreshTaskUi();
            InputTextBox.Text = "从保存的断点继续任务，先核对当前项目文件和已完成步骤。";
            InputTextBox.Focus();
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "继续任务失败"); }
    }
    private void ChangeHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending) return;
        try
        {
            var ids = _service.GetTask(_session)?.ChangeIds ?? new List<string>();
            // Also recover durable commits if the process stopped before a chat checkpoint.
            ids = ids.Concat(_session.Messages.SelectMany(m => m.Activities).Where(a => a.ChangeId != null).Select(a => a.ChangeId!)).Distinct().ToList();
            ids = ids.Concat(AiFileAccessService.Changes.List().Where(c => c.State is "Applied" or "Applying" or "RollingBack").Select(c => c.Id)).Distinct().ToList();
            AiWorkbenchDialogs.History(Window.GetWindow(this), ids);
        }
        catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "变更历史"); }
    }
}
