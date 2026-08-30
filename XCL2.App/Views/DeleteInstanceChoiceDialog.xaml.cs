using System.Windows;

namespace XCL2.App.Views;

/// <summary>
/// 删除实例前先弹出的"选择方式"弹窗：从列表中删除(不删文件) / 从电脑中删除(删除所有文件) / 取消。
/// 只负责收集用户选了哪一种，不做任何实际删除动作——调用方(VersionSelectPage)根据
/// <see cref="Choice"/> 的值决定后续流程：选"从电脑中删除"还需要再过一道 xztx127 确认。
/// </summary>
public partial class DeleteInstanceChoiceDialog : OverlayDialogControl
{
    public enum DeleteChoice
    {
        RemoveFromList,
        DeleteFromDisk,
    }

    /// <summary>用户选择的删除方式；ShowModal 返回 true 时保证有值。</summary>
    public DeleteChoice? Choice { get; private set; }

    public DeleteInstanceChoiceDialog(string instanceName)
    {
        InitializeComponent();
        TitleText.Text = $"删除实例「{instanceName}」";
    }

    private void RemoveFromList_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.RemoveFromList;
        CloseWith(true);
    }

    private void DeleteFromDisk_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.DeleteFromDisk;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
