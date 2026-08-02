using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class LoginPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly ObservableCollection<Account> _accounts = new();

    public LoginPage(MainWindow owner)
    {
        _owner = owner; // 统一先于 InitializeComponent 赋值，避免控件初始化时触发的事件访问到未赋值字段
        InitializeComponent();
        AccountListBox.ItemsSource = _accounts;
        Reload();
    }

    private void Reload()
    {
        _accounts.Clear();
        foreach (var a in _owner.ConfigService.Accounts) _accounts.Add(a);
    }

    private void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(OfflineNameBox.Text) ? "Player" : OfflineNameBox.Text.Trim();
        var account = OfflineAuthService.CreateOfflineAccount(name);
        _owner.ConfigService.AddOrUpdateAccount(account);
        _owner.ConfigService.SelectAccount(account.Id);
        Reload();
        _owner.RefreshSidebar();
        StatusText.Text = $"离线账户 {name} 已添加并选用。UUID: {account.Uuid}";
    }

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在准备登录，请稍候...";

        MicrosoftAuthService auth;
        try
        {
            auth = new MicrosoftAuthService();
        }
        catch (AuthStepException ex)
        {
            StatusText.Text = $"登录在「{ex.Step}」这一步失败，详情见弹窗。";
            ShowLoginFailure(ex);
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
        auth.StatusChanged += status =>
        {
            Dispatcher.Invoke(() => popup?.SetStatus(status));
        };

        try
        {
            var account = await auth.LoginInteractiveAsync(cts.Token);
            popup?.Dispatcher.Invoke(() => popup.Close());

            if (account == null)
            {
                StatusText.Text = "微软账户登录失败或已取消，请重试。";
                return;
            }
            _owner.ConfigService.AddOrUpdateAccount(account);
            _owner.ConfigService.SelectAccount(account.Id);
            Reload();
            _owner.RefreshSidebar();
            StatusText.Text = $"微软账户 {account.Username} 登录成功并已选用！";
        }
        catch (OperationCanceledException)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            StatusText.Text = "登录已取消。";
        }
        catch (AuthStepException ex)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            StatusText.Text = $"登录在「{ex.Step}」这一步失败，详情见弹窗。";
            ShowLoginFailure(ex);
        }
        catch (Exception ex)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            ErrorPresenter.LogTechnicalDetail($"微软账户登录(浏览器)出错: {ex}");
            StatusText.Text = "登录出错，请检查网络连接后重试。详细日志已记录，如果反复出现，请把日志发给可信的专业人士。";
        }
    }

    private async void AddMicrosoftEmbedded_Click(object sender, RoutedEventArgs e)
    {
        // WebView2 相关类型只在真正点击"内嵌登录"这里才会被用到/加载，不会跟主界面一起加载
        // (MainWindow/App.xaml 都不引用 WebView2 控件)。这里先做一次轻量探测：本机是否已经
        // 装了 WebView2 Runtime——如果没装，与其打开一个注定会失败、只能等 EnsureCoreWebView2Async
        // 抛异常才知道原因的空窗口，不如现在就提示清楚，让用户直接去下载运行时或者改用浏览器登录。
        if (!WebView2RuntimeDetector.IsAvailable())
        {
            var choice = MessageBoxDialog.ShowConfirm(
                "本机未检测到 WebView2 运行时，无法使用内嵌登录。\n\n" +
                "点「是」前往下载 WebView2 运行时（安装后重启本程序即可使用内嵌登录）；\n" +
                "点「否」改用「浏览器登录」（不需要 WebView2，效果相同，只是登录过程会在系统默认浏览器里完成，需要手动复制验证码/等待自动跳转）。",
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
                    StatusText.Text = $"打开下载页失败，请手动访问：{WebView2RuntimeDetector.DownloadUrl}";
                }
            }
            else
            {
                StatusText.Text = "已取消内嵌登录，可以点击「浏览器登录」改用系统浏览器完成登录。";
            }
            return;
        }

        StatusText.Text = "正在打开内嵌登录窗口...";

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
            StatusText.Text = $"登录在「{ex.Step}」这一步失败，详情见弹窗。";
            ShowLoginFailure(ex);
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
            StatusText.Text = "登录已取消。";
            return;
        }
        catch (AuthStepException ex)
        {
            StatusText.Text = $"登录在「{ex.Step}」这一步失败，详情见弹窗。";
            ShowLoginFailure(ex);
            return;
        }

        try
        {
            var account = await auth.LoginWithAuthorizationCodeAsync(code, verifier);
            if (account == null)
            {
                StatusText.Text = "微软账户登录失败，请重试。";
                return;
            }
            _owner.ConfigService.AddOrUpdateAccount(account);
            _owner.ConfigService.SelectAccount(account.Id);
            Reload();
            _owner.RefreshSidebar();
            StatusText.Text = $"微软账户 {account.Username} 登录成功并已选用！";
        }
        catch (AuthStepException ex)
        {
            StatusText.Text = $"登录在「{ex.Step}」这一步失败，详情见弹窗。";
            ShowLoginFailure(ex);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"微软账户登录(内嵌)出错: {ex}");
            StatusText.Text = "登录出错，请检查网络连接后重试。详细日志已记录，如果反复出现，请把日志发给可信的专业人士。";
        }
    }

    private async void AddAuthServer_Click(object sender, RoutedEventArgs e)
    {
        var apiRoot = AuthServerRootBox.Text?.Trim() ?? "";
        var username = AuthServerUsernameBox.Text?.Trim() ?? "";
        var password = AuthServerPasswordBox.Password ?? "";

        StatusText.Text = "正在登录认证服务器，请稍候...";

        try
        {
            var auth = new AuthServerAuthService();
            var account = await auth.LoginAsync(apiRoot, username, password);

            _owner.ConfigService.AddOrUpdateAccount(account);
            _owner.ConfigService.SelectAccount(account.Id);
            Reload();
            _owner.RefreshSidebar();

            // 登录成功后清空密码框：密码框本身就不该长期停留敏感内容，且账户已经保存好了，
            // 不需要用户手动清空再进行下一步操作。用户名/服务器地址保留，方便下次直接改密码重登，
            // 或者用同一个服务器再登另一个账号。
            AuthServerPasswordBox.Password = "";

            StatusText.Text = $"认证服务器账户 {account.Username} 登录成功并已选用！";
        }
        catch (AuthStepException ex)
        {
            StatusText.Text = $"登录在「{ex.Step}」这一步失败，详情见弹窗。";
            ErrorPresenter.ShowFriendlyError(ex.Message, $"[认证服务器登录失败 - {ex.Step}] {ex.Message}", "认证服务器登录失败");
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"认证服务器登录出错: {ex}");
            StatusText.Text = "登录出错，请检查网络连接后重试。详细日志已记录，如果反复出现，请把日志发给可信的专业人士。";
        }
    }

    private void SelectAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Account acc })
        {
            _owner.ConfigService.SelectAccount(acc.Id);
            _owner.RefreshSidebar();
            StatusText.Text = $"已切换到账户：{acc.DisplayLabel}";
        }
    }

    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Account acc }) return;
        if (acc.Type != AccountType.Offline) return; // 保险起见：微软账户不应该走到这里

        var skinService = new SkinService();
        var win = new SkinSelectWindow(acc, skinService) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
        {
            _owner.ConfigService.AddOrUpdateAccount(acc);
            Reload();
            StatusText.Text = $"已更新账户 {acc.Username} 的皮肤设置。";
        }
    }

    /// <summary>
    /// 统一处理登录失败的弹窗提示：不再把 HTTP 状态码/原始响应体这些工程细节直接糊给用户看，
    /// 只给一句"在哪一步失败了"的人话概括，完整技术细节写进 crash.log，引导用户在反复失败时
    /// 把完整日志文件发给可信的专业人士，或去 GitHub 反馈，而不是发窗口截图。
    /// </summary>
    private static void ShowLoginFailure(AuthStepException ex)
    {
        var friendly = ex.Step switch
        {
            "获取登录令牌" or "请求登录代码" => "登录请求失败，可能是网络连接问题，或微软登录服务暂时不可用，请检查网络后重试。",
            "Minecraft 服务登录" => "登录到 Minecraft 服务失败，可能是网络问题，也可能是这个微软账户还没有购买 Minecraft，请确认账户状态后重试。",
            "获取游戏档案" => "获取游戏档案失败，可能是这个微软账户还没有创建过 Minecraft 游戏角色，请先在官网完成角色创建。",
            _ => $"登录在「{ex.Step}」这一步失败，可能是网络连接问题，请检查网络后重试。"
        };

        ErrorPresenter.ShowFriendlyError(friendly, $"[登录失败 - {ex.Step}] {ex.Message}", "微软账户登录失败");
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Account acc })
        {
            _owner.ConfigService.RemoveAccount(acc.Id);
            Reload();
            _owner.RefreshSidebar();
        }
    }
}
