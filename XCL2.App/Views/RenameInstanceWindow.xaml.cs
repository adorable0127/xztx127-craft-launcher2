using System.Windows;

namespace XCL2.App.Views;

/// <summary>
/// 极简重命名弹窗：只做"输入一个非空、且不与传入的排除名单重复的新名称"这一件事，
/// 复用给"重命名服务器"，将来也可以复用给其它需要单个文本输入的场景。
/// 不用 Microsoft.VisualBasic.Interaction.InputBox 是因为那个要求额外引用
/// Microsoft.VisualBasic 程序集，且弹窗风格无法跟随本项目自己的 PanelBrush/PrimaryButton 换肤。
/// </summary>
public partial class RenameInstanceWindow : Window
{
    private readonly Func<string, bool> _isNameTaken;

    public string NewName { get; private set; } = "";

    /// <param name="currentName">预填到输入框里的当前名称。</param>
    /// <param name="isNameTaken">校验回调：传入去除首尾空白后的新名称，返回 true 表示这个名字已经被占用
    /// （调用方负责排除"实例改名改回自己原名"这种不算冲突的情况）。</param>
    public RenameInstanceWindow(string currentName, Func<string, bool> isNameTaken)
    {
        _isNameTaken = isNameTaken;
        InitializeComponent();
        NameInputBox.Text = currentName;
        NameInputBox.SelectAll();
        Loaded += (_, _) => NameInputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInputBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowError("名称不能为空。");
            return;
        }
        if (_isNameTaken(name))
        {
            ShowError("已经有一个同名的服务器了，请换一个名称。");
            return;
        }

        NewName = name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
