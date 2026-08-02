using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>
/// 极简重命名弹窗：只做"输入一个非空、且不与传入的排除名单重复的新名称"这一件事，
/// 复用给"重命名服务器"/"重命名 Java"，将来也可以复用给其它需要单个文本输入的场景。
///
/// 迁移记录：原来是独立 Window（RenameInstanceWindow），现在改成挂在 MainWindow
/// Overlay 层里的 UserControl（继承 OverlayDialogControl，见 IOverlayDialog.cs）。
/// 原来"DialogResult = true; Close();"两行，现在统一改成调用基类的
/// CloseWith(true/false)——语义完全等价，只是从"关闭 Win32 窗口"变成"通知宿主
/// OverlayDialogService 把自己从 Overlay 层摘掉"。
/// </summary>
public partial class RenameInstanceDialog : OverlayDialogControl
{
    private readonly Func<string, bool> _isNameTaken;

    public string NewName { get; private set; } = "";

    /// <param name="currentName">预填到输入框里的当前名称。</param>
    /// <param name="isNameTaken">校验回调：传入去除首尾空白后的新名称，返回 true 表示这个名字已经被占用
    /// （调用方负责排除"实例改名改回自己原名"这种不算冲突的情况）。</param>
    /// <param name="title">弹窗标题——原来独立 Window 版本靠 Window.Title 显示在系统标题栏，
    /// Overlay 弹窗没有系统标题栏了，调用方如果需要自定义标题（比如"重命名 Java"），
    /// 通过这个可选参数传入，转成内部一个 TextBlock 显示；不传则不显示标题行
    /// （原版默认 Title="重命名服务器"其实从来没在窗口客户区里显示过，只出现在
    /// 系统标题栏，所以这里默认不显示也是等价的，需要的调用点显式传入即可）。</param>
    public RenameInstanceDialog(string currentName, Func<string, bool> isNameTaken, string? title = null)
    {
        _isNameTaken = isNameTaken;
        InitializeComponent();
        NameInputBox.Text = currentName;
        NameInputBox.SelectAll();
        Loaded += (_, _) => NameInputBox.Focus();

        if (!string.IsNullOrEmpty(title))
        {
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            ((StackPanel)Content).Children.Insert(0, titleBlock);
        }
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
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
