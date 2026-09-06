using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 启动前完整性检查发现问题时的处理方式选择弹窗。
/// </summary>
public partial class IncompleteVersionDialog : OverlayDialogControl
{
    public enum ResultChoice
    {
        /// <summary>继续启动，同时后台补全缺失/损坏的文件。</summary>
        ContinueAndRepair,
        /// <summary>只是不再显示在实例列表里，磁盘文件不动（复用 InstanceDeletionService.HideFromList）。</summary>
        RemoveFromList,
        /// <summary>物理删除本地文件 + 从列表移除。具体是永久删除还是移入回收站，见 DeleteMode。</summary>
        DeleteFromDisk,
        /// <summary>不补全，直接强制启动，可能出现材质/声音/世界生成等问题。</summary>
        ForceLaunch,
    }

    /// <summary>用户最终选择；ShowModal 返回 true 时保证有值。</summary>
    public ResultChoice? Result { get; private set; }

    /// <summary>Result == DeleteFromDisk 时，具体是哪种删除方式。</summary>
    public DeleteInstanceChoiceDialog.DeleteChoice DeleteMode { get; private set; }
        = DeleteInstanceChoiceDialog.DeleteChoice.DeleteToRecycleBin;

    public IncompleteVersionDialog(string instanceName, IReadOnlyList<string> problems)
    {
        InitializeComponent();
        TitleText.Text = $"「{instanceName}」这个版本的文件不完整或已损坏";
        ProblemsText.Text = problems.Count == 0
            ? "（未能获取具体缺失清单）"
            : string.Join("\n", problems.Take(30)) + (problems.Count > 30 ? $"\n...等共 {problems.Count} 项" : "");
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        Result = ResultChoice.ContinueAndRepair;
        CloseWith(true);
    }

    private void RemoveFromList_Click(object sender, RoutedEventArgs e)
    {
        Result = ResultChoice.RemoveFromList;
        CloseWith(true);
    }

    /// <summary>
    /// "删除本地文件以及显示"：复用已有的 DeleteInstanceChoiceDialog 里"从电脑中删除
    /// （永久/回收站）"这两个选项——不重复实现一遍选择 UI。用户在子弹窗里选"取消"时，
    /// 这个弹窗保持打开，不当作最终结果。永久删除额外要求 xztx127 确认（跟
    /// VersionSelectPage 里已有的删除流程保持同一套确认口径）；回收站不需要。
    /// </summary>
    private void DeleteFiles_Click(object sender, RoutedEventArgs e)
    {
        var choiceDlg = new DeleteInstanceChoiceDialog(TitleText.Text);
        if (choiceDlg.ShowDialog() != true || choiceDlg.Choice == null) return;
        if (choiceDlg.Choice == DeleteInstanceChoiceDialog.DeleteChoice.RemoveFromList)
        {
            // 理论上不会走到这个分支（这里复用的子弹窗本来是给"删除本地文件"用的），
            // 但防御性地按"仅从列表删除"处理，而不是当成删除文件。
            Result = ResultChoice.RemoveFromList;
            CloseWith(true);
            return;
        }

        if (choiceDlg.Choice == DeleteInstanceChoiceDialog.DeleteChoice.DeleteFromDisk)
        {
            var confirmDlg = new DangerousConfirmDialog(
                "永久删除本地文件",
                "将彻底删除这个版本目录下的所有文件（存档、mod、资源包、日志等），此操作不可撤销。");
            if (confirmDlg.ShowDialog() != true || !confirmDlg.Confirmed) return;
        }

        DeleteMode = choiceDlg.Choice.Value;
        Result = ResultChoice.DeleteFromDisk;
        CloseWith(true);
    }

    /// <summary>
    /// 强行启动：两次二级确认。第一次是普通的"你确定吗"警示，第二次要求原样输入
    /// "我确认"这个专属确认码（不用全局的 xztx127，理由见 DangerousConfirmDialog 上的注释），
    /// 任意一次没通过都留在当前弹窗，不当作确认。
    /// </summary>
    private void ForceLaunch_Click(object sender, RoutedEventArgs e)
    {
        var firstConfirm = MessageBoxDialog.ShowConfirm(
            "文件不完整的情况下强行启动，可能会出现材质缺失、声音无法播放、光影/世界生成异常等各种问题，" +
            "而且这些问题不一定能靠日志看出原因。确定要跳过补全，直接启动吗？",
            "强行启动确认");
        if (!firstConfirm) return;

        var secondConfirm = new DangerousConfirmDialog(
            "再次确认强行启动",
            "这是最后一次确认：接下来启动使用的是不完整/已损坏的文件，出现任何游戏内异常都跟这次选择有关。",
            "我确认");
        if (secondConfirm.ShowDialog() != true || !secondConfirm.Confirmed) return;

        Result = ResultChoice.ForceLaunch;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
