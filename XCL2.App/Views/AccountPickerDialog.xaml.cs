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
    /// 刷新账户列表。
    /// 需求变更（本轮）：弹窗默认高亮的账户改成"最近创建的账户"，而不是"什么都不选"——
    /// 之前 currentlySelectedId 为空时（比如从来没有过显式选择、或者上面"添加账户"三个
    /// 入口成功后传 null）列表直接不选中任何一项，用户还得自己在一堆账户里找到刚创建
    /// 的那个才能点确定；现在 currentlySelectedId 为空时改为回退到 GetMostRecentlyCreatedAccount()，
    /// 对大多数场景（尤其是刚创建完账户）来说这就是用户想要的那个，省掉一步手动查找。
    /// 三个"添加账户"入口成功后仍然显式传 account.Id（更精确、不依赖时间戳排序结果跟
    /// 刚创建的账户一致），这里的回退只覆盖"一开始打开弹窗、且从未有过显式选择"这一种情况。
    /// 已经有过显式选择（currentlySelectedId 非空，即 cfg.LastSelectedAccountId 有值）时
    /// 行为不变——不会用"最近创建"覆盖用户之前手动做过的选择，避免账户列表里新增了
    /// 别的账户就意外改变了下次启动默认用的账户。
    /// </summary>
    private void RefreshList(string? currentlySelectedId)
    {
        var accounts = _configService.Accounts.ToList();
        AccountListBox.ItemsSource = accounts;

        Account? toSelect;
        if (!string.IsNullOrEmpty(currentlySelectedId))
        {
            toSelect = accounts.FirstOrDefault(a => a.Id == currentlySelectedId);
        }
        else
        {
            toSelect = _configService.GetMostRecentlyCreatedAccount();
        }
        AccountListBox.SelectedItem = toSelect;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => AcceptSelection();

    private void AccountListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (AccountListBox.SelectedItem is not Account acc)
        {
            MessageBoxDialog.ShowInfo("请先选中一个账户。", Loc.T("Str_Status_Tip", "提示"));
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
        RefreshList(currentlySelectedId: account.Id);
        AddAccountStatusText.Text = $"离线账户「{name}」已添加并选中，可直接点击「确定」使用。";
    }

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        AddAccountStatusText.Text = Loc.T("Str_Cs_Preparing_To_Sign_In_Please_Wait", "正在准备登录，请稍候...");

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
                popup = new DeviceCodeWindow(uri, code, cts);
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
                AddAccountStatusText.Text = Loc.T("Str_Cs_Microsoft_Sign_In_Failed_Or_Was_Cancelle", "微软账户登录失败或已取消，请重试。");
                return;
            }
            _configService.AddOrUpdateAccount(account);
            RefreshList(currentlySelectedId: account.Id);
            AddAccountStatusText.Text = $"微软账户「{account.Username}」登录成功并已选中，可直接点击「确定」使用。";
        }
        catch (OperationCanceledException)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            AddAccountStatusText.Text = Loc.T("Str_Cs_Sign_In_Cancelled", "登录已取消。");
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
            AddAccountStatusText.Text = Loc.T("Str_Cs_Sign_In_Failed_Check_Your_Connection_And", "登录出错，请检查网络连接后重试。");
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
                AddAccountStatusText.Text = Loc.T("Str_Cs_Embedded_Sign_In_Cancelled_You_Can_Use_B", "已取消内嵌登录，可以点击「浏览器登录」改用系统浏览器完成登录。");
            }
            return;
        }

        AddAccountStatusText.Text = Loc.T("Str_Cs_Opening_The_Embedded_Sign_In_Window", "正在打开内嵌登录窗口...");

        MicrosoftAuthService auth;
        string url, verifier;
        MicrosoftLoginWindow popup;
        try
        {
            auth = new MicrosoftAuthService();
            (url, verifier) = auth.BuildInteractiveAuthorizeUrl();
            popup = new MicrosoftLoginWindow(url);
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
            AddAccountStatusText.Text = Loc.T("Str_Cs_Sign_In_Cancelled", "登录已取消。");
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
                AddAccountStatusText.Text = Loc.T("Str_Cs_Microsoft_Sign_In_Failed_Please_Try_Agai", "微软账户登录失败，请重试。");
                return;
            }
            _configService.AddOrUpdateAccount(account);
            RefreshList(currentlySelectedId: account.Id);
            AddAccountStatusText.Text = $"微软账户「{account.Username}」登录成功并已选中，可直接点击「确定」使用。";
        }
        catch (AuthStepException ex)
        {
            AddAccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"微软账户登录(内嵌，账户选择弹窗内)出错: {ex}");
            AddAccountStatusText.Text = Loc.T("Str_Cs_Sign_In_Failed_Check_Your_Connection_And", "登录出错，请检查网络连接后重试。");
        }
    }
}
