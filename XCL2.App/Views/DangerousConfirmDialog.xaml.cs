using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>
/// 危险操作二次确认弹窗：关闭注册表功能 / 删除所有新增的启动器注册表项 / 清除本机痕迹
/// 三个操作共用。用户必须原样输入固定字符串 <see cref="RequiredCode"/>（"xztx127"）才能点
/// "确认执行"，纯粹起"确认你真的知道自己在做什么"的作用，不是真正的安全凭据。
/// </summary>
public partial class DangerousConfirmDialog : OverlayDialogControl
{
    public const string RequiredCode = "xztx127";

    /// <summary>确认通过时为 true，取消/校验未通过关闭时为 false。</summary>
    public bool Confirmed { get; private set; }

    public DangerousConfirmDialog(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        Loaded += (_, _) => ConfirmCodeBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmCodeBox.Text.Trim() != RequiredCode)
        {
            ErrorText.Text = $"确认码不正确，请原样输入 {RequiredCode}。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Confirmed = true;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        CloseWith(false);
    }
}
