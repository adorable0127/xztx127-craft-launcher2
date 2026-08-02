using System.Windows;
using System.Windows.Input;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>
/// 启动游戏前的账户选择弹窗：修复"一键开始游戏只会自动选中默认账户，没法选"的问题——
/// 之前 MainWindow.LaunchInternalAsync 完全是静默调用 ConfigService.GetSelectedAccount()，
/// 有多个账户时用户完全没有机会在启动的这个时间点临时切换成另一个账户，只能先跳转到
/// 「账户管理」页手动切换、切回主页再点启动，多绕一层。
///
/// 这个弹窗只在"账户数量 &gt; 1"时才会由 MainWindow 弹出（只有一个账户/没有账户时，
/// 直接沿用原来的行为——没有账户就跳转账户管理页提示创建，只有一个账户就直接用它，
/// 不需要多此一举地为"唯一选项"也弹一次选择框）。
///
/// 迁移记录：原来是独立 Window（AccountPickerWindow），现在改成 Overlay 弹窗。
/// </summary>
public partial class AccountPickerDialog : OverlayDialogControl
{
    public Account? SelectedAccount { get; private set; }

    /// <summary>用户是否勾选了"记住这次选择，以后不再询问"。</summary>
    public bool RememberChoice { get; private set; }

    public AccountPickerDialog(IEnumerable<Account> accounts, string? currentlySelectedId)
    {
        InitializeComponent();
        AccountListBox.ItemsSource = accounts.ToList();
        AccountListBox.SelectedItem = AccountListBox.Items.Cast<Account>()
            .FirstOrDefault(a => a.Id == currentlySelectedId) ?? AccountListBox.Items.Cast<Account>().FirstOrDefault();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => AcceptSelection();

    private void AccountListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (AccountListBox.SelectedItem is not Account acc)
        {
            MessageBox.Show("请先选中一个账户。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedAccount = acc;
        RememberChoice = RememberChoiceCheck.IsChecked == true;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
