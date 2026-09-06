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

    private readonly string _requiredCode;

    /// <summary>确认通过时为 true，取消/校验未通过关闭时为 false。</summary>
    public bool Confirmed { get; private set; }

    public DangerousConfirmDialog(string title, string message) : this(title, message, RequiredCode) { }

    /// <summary>
    /// 需求修复：强制启动损坏/未下载完成的版本需要单独一套确认码（"我确认"），
    /// 不能沿用全局统一的 "xztx127"——那个是"我知道这是危险的注册表/系统级操作"，
    /// 语义上和"我知道这个版本文件不全，仍要坚持启动"是两件不同的事，混用会让同一个
    /// 确认码在不同场景下失去针对性。加这个重载而不是新建一个几乎一样的弹窗类，
    /// 复用已有的输入校验/错误提示 UI 逻辑。
    /// </summary>
    public DangerousConfirmDialog(string title, string message, string requiredCode)
    {
        InitializeComponent();
        _requiredCode = requiredCode;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmCodeHintText.Text = $"请输入确认码 {_requiredCode} 以继续：";
        Loaded += (_, _) => ConfirmCodeBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmCodeBox.Text.Trim() != _requiredCode)
        {
            ErrorText.Text = $"确认码不正确，请原样输入 {_requiredCode}。";
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
