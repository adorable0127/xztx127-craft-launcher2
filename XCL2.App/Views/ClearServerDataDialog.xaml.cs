using System.Windows;
using XCL2.App.Services;   // Loc.T（代码内文案本地化，见 Services/Loc.cs）

namespace XCL2.App.Views;

/// <summary>
/// "清除服务器数据"确认弹窗：跟服务器列表卡片上的"删除"按钮（只移除启动器里的实例记录，
/// 保留磁盘上的服务端文件夹）是两件事——这里是真正把 world/ 存档、插件、配置、日志等
/// 服务端目录下所有文件连同实例记录一起永久删除，属于不可撤销的破坏性操作。
///
/// 只有"输入框里的内容跟服务器名称完全一致"时"永久删除"按钮才会启用，用打字这个动作本身
/// 拖住用户的手速，让人有机会在按下去之前再想一下，比一个"确定/取消"的 MessageBox 更难被
/// 手滑点掉——这个模式在很多"删除仓库/删除数据库"这类不可逆操作的产品里很常见。
///
/// 迁移记录：原来是独立 Window（ClearServerDataWindow），现在改成 Overlay 弹窗。
/// </summary>
public partial class ClearServerDataDialog : OverlayDialogControl
{
    private readonly string _expectedName;

    /// <summary>用户是否确认要清除（点了"永久删除"）。</summary>
    public bool Confirmed { get; private set; }

    /// <param name="instanceName">要清除的服务器实例显示名称，用户必须在输入框里原样输入这个值。</param>
    /// <param name="directory">服务端所在目录，仅用于警告文案里展示给用户看，不参与任何校验逻辑。</param>
    public ClearServerDataDialog(string instanceName, string directory)
    {
        _expectedName = instanceName;
        InitializeComponent();

        WarningText.Text =
            $"即将永久删除服务器「{instanceName}」的全部数据，包括世界存档、插件、配置文件和日志，" +
            $"目录：\n{directory}\n\n此操作不可撤销，删除后无法恢复。如果只是想暂时不在启动器里管理这个服务器、" +
            "但保留文件以后还能用，请改用列表卡片上的「删除」按钮（那个只移除启动器记录，不动磁盘文件）。";

        Loaded += (_, _) => ConfirmNameBox.Focus();
    }

    private void ConfirmNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ConfirmBtn.IsEnabled = ConfirmNameBox.Text == _expectedName;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // 双重保险：即便 IsEnabled 状态被绕过，这里再校验一次，不匹配就拒绝执行。
        if (ConfirmNameBox.Text != _expectedName)
        {
            ErrorText.Text = Loc.T("Str_Cs_The_Name_You_Typed_Doesn_T_Match_The_Ser", "输入的名称和服务器名称不一致，请仔细核对后重新输入。");
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
