using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 启动游戏前的账户选择弹窗：修复"一键开始游戏只会自动选中默认账户，没法选"的问题——
/// 之前 MainWindow.LaunchInternalAsync 完全是静默调用 ConfigService.GetSelectedAccount()，
/// 有多个账户时用户完全没有机会在启动的这个时间点临时切换成另一个账户，只能先跳转到
/// 「账户管理」页手动切换、切回主页再点启动，多绕一层。
///
/// 这个弹窗现在由 MainWindow.LaunchInternalAsync 在"账户数量 &gt; 1"或者"唯一账户从未被
/// 显式选中过"时弹出（真正"完全没有账户"的情况仍然沿用旧行为——直接跳转账户管理页提示
/// 创建）。同时这个弹窗现在也内置了"添加账户"（离线/浏览器登录/内嵌登录）三个入口，
/// 不再要求用户必须先取消这个弹窗、跳去"账户管理"页添加完账户后再重新点一次启动。
///
/// 迁移记录：原来是独立 Window（AccountPickerWindow），现在改成 Overlay 弹窗。
/// </summary>
public partial class AccountPickerDialog : OverlayDialogControl
{
    private readonly ConfigService _configService;

    public Account? SelectedAccount { get; private set; }

    /// <summary>用户是否勾选了"记住这次选择，以后不再询问"。</summary>
    public bool RememberChoice { get; private set; }

    public AccountPickerDialog(ConfigService configService, IEnumerable<Account> accounts, string? currentlySelectedId)
    {
        _configService = configService;
        InitializeComponent();
        RefreshList(currentlySelectedId);
    }

    /// <summary>
    /// 供 MainWindow 调用的便捷重载：直接传 owner MainWindow，内部取 owner.ConfigService，
    /// 不需要调用方额外拆出 ConfigService 参数。目前唯一调用点见 MainWindow.LaunchInternalAsync。
    /// </summary>
    public AccountPickerDialog(MainWindow owner, IEnumerable<Account> accounts, string? currentlySelectedId)
        : this(owner.ConfigService, accounts, currentlySelectedId)
    {
    }

    /// <summary>
    /// 刷新账户列表；currentlySelectedId 为空（比如"添加账户"新建/登录完成之后）时，
    /// 需求变更：新建/登录出来的账户不再自动选中列表项——列表选中状态保持"没有选中任何一项"，
    /// 由用户自己点选，避免用户以为点了「添加」就等于自动确认要用这个新账户启动。
    /// </summary>
    private void RefreshList(string? currentlySelectedId)
    {
        var accounts = _configService.Accounts.ToList();
        AccountListBox.ItemsSource = accounts;
        AccountListBox.SelectedItem = string.IsNullOrEmpty(currentlySelectedId)
            ? null
            : accounts.FirstOrDefault(a => a.Id == currentlySelectedId);
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

    // ========== 添加账户（离线 / 浏览器登录 / 内嵌登录）==========
    // 三个方法照抄 LoginPage.xaml.cs 里对应方法的登录逻辑，只是登录/创建成功后不再调用
    // ConfigService.SelectAccount 自动选中——按需求变更，创建/登录账户之后不应该默认选中，
    // 只刷新列表，由用户自己在列表里点选、再点「使用这个账户启动」或双击确认。

    private void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(OfflineNameBox.Text) ? "Player" : OfflineNameBox.Text.Trim();
        var account = OfflineAuthService.CreateOfflineAccount(name);
        _configService.AddOrUpdateAccount(account);
        RefreshList(currentlySelectedId: null);
        AddAccountStatusText.Text = $"离线账户「{name}」已添加，请在上面的列表中选中它。";
    }

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        AddAccountStatusText.Text = "正在准备登录，请稍候...";

        MicrosoftAuthService auth;
        try
        {
            auth = new MicrosoftAuthService();
        }
        catch (AuthStepException ex)
        {
            AddAccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
            return;
        }

        var cts = new CancellationTokenSource();
        DeviceCodeWindow? popup = null;

        auth.UserCodeReady += (uri, code) =>
        {
            Dispatcher.Invoke(() =>
            {
                popup = new DeviceCodeWindow(uri, code, cts) { Owner = Window.GetWindow(this) };
                popup.Show();
            });
        };
        auth.StatusChanged += status => Dispatcher.Invoke(() => popup?.SetStatus(status));

        try
        {
            var account = await auth.LoginInteractiveAsync(cts.Token);
            popup?.Dispatcher.Invoke(() => popup.Close());

            if (account == null)
            {
                AddAccountStatusText.Text = "微软账户登录失败或已取消，请重试。";
                return;
            }
            _configService.AddOrUpdateAccount(account);
            RefreshList(currentlySelectedId: null);
            AddAccountStatusText.Text = $"微软账户「{account.Username}」登录成功，请在上面的列表中选中它。";
        }
        catch (OperationCanceledException)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            AddAccountStatusText.Text = "登录已取消。";
        }
        catch (AuthStepException ex)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            AddAccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            ErrorPresenter.LogTechnicalDetail($"微软账户登录(浏览器，账户选择弹窗内)出错: {ex}");
            AddAccountStatusText.Text = "登录出错，请检查网络连接后重试。";
        }
    }

    private async void AddMicrosoftEmbedded_Click(object sender, RoutedEventArgs e)
    {
        if (!WebView2RuntimeDetector.IsAvailable())
        {
            var choice = MessageBoxDialog.ShowConfirm(
                "本机未检测到 WebView2 运行时，无法使用内嵌登录。\n\n" +
                "点「是」前往下载 WebView2 运行时（安装后重启本程序即可使用内嵌登录）；\n" +
                "点「否」改用「浏览器登录」（不需要 WebView2，效果相同）。",
                "未检测到 WebView2 运行时");

            if (choice)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(WebView2RuntimeDetector.DownloadUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    ErrorPresenter.LogTechnicalDetail($"打开 WebView2 下载页失败: {ex}");
                    AddAccountStatusText.Text = $"打开下载页失败，请手动访问：{WebView2RuntimeDetector.DownloadUrl}";
                }
            }
            else
            {
                AddAccountStatusText.Text = "已取消内嵌登录，可以点击「浏览器登录」改用系统浏览器完成登录。";
            }
            return;
        }

        AddAccountStatusText.Text = "正在打开内嵌登录窗口...";

        MicrosoftAuthService auth;
        string url, verifier;
        MicrosoftLoginWindow popup;
        try
        {
            auth = new MicrosoftAuthService();
            (url, verifier) = auth.BuildInteractiveAuthorizeUrl();
            popup = new MicrosoftLoginWindow(url) { Owner = Window.GetWindow(this) };
        }
        catch (AuthStepException ex)
        {
            AddAccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
            return;
        }

        string code;
        try
        {
            popup.Show();
            code = await popup.WaitForCodeAsync();
        }
        catch (OperationCanceledException)
        {
            AddAccountStatusText.Text = "登录已取消。";
            return;
        }
        catch (AuthStepException ex)
        {
            AddAccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
            return;
        }

        try
        {
            var account = await auth.LoginWithAuthorizationCodeAsync(code, verifier);
            if (account == null)
            {
                AddAccountStatusText.Text = "微软账户登录失败，请重试。";
                return;
            }
            _configService.AddOrUpdateAccount(account);
            RefreshList(currentlySelectedId: null);
            AddAccountStatusText.Text = $"微软账户「{account.Username}」登录成功，请在上面的列表中选中它。";
        }
        catch (AuthStepException ex)
        {
            AddAccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"微软账户登录(内嵌，账户选择弹窗内)出错: {ex}");
            AddAccountStatusText.Text = "登录出错，请检查网络连接后重试。";
        }
    }
}
