using System.Windows;

namespace XCL2.App.Views;

/// <summary>撤回一条已发送消息时，如果待发送框里已经有用户还没发出去的内容，
/// 用这个弹窗问清楚要怎么处理冲突，避免悄悄覆盖用户正在写的东西。</summary>
public enum InputConflictChoice
{
    /// <summary>取消（不替换）：待发送框保持原样，撤回操作视为没有发生（消息也不从对话里移除）。</summary>
    Cancel,
    /// <summary>替换：待发送框原内容直接丢弃，换成撤回的消息内容。</summary>
    Replace,
    /// <summary>替换并复制：待发送框换成撤回内容之前，先把原内容复制到剪贴板，避免丢失。</summary>
    ReplaceAndCopy,
    /// <summary>不替换并复制：待发送框保持原样，但把撤回的消息内容复制到剪贴板，用户自己找地方粘贴。</summary>
    KeepAndCopy
}

public partial class InputConflictChoiceDialog : OverlayDialogControl
{
    public InputConflictChoice Choice { get; private set; } = InputConflictChoice.Cancel;

    public InputConflictChoiceDialog()
    {
        InitializeComponent();
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        Choice = InputConflictChoice.Replace;
        CloseWith(true);
    }

    private void ReplaceAndCopy_Click(object sender, RoutedEventArgs e)
    {
        Choice = InputConflictChoice.ReplaceAndCopy;
        CloseWith(true);
    }

    private void KeepAndCopy_Click(object sender, RoutedEventArgs e)
    {
        Choice = InputConflictChoice.KeepAndCopy;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = InputConflictChoice.Cancel;
        CloseWith(false);
    }
}
