using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services.Plugins;

/// <summary>只在用户发起的登录回调期间有效。返回一次性凭据句柄，不返回密码或令牌。</summary>
public sealed class PluginLoginBroker
{
    private readonly PluginContext _context;
    private readonly Dictionary<Guid, Account> _tickets = new();
    private readonly CancellationToken _operation;
    private int _closed;
    internal PluginLoginBroker(PluginContext context, CancellationToken operation) { _context = context; _operation = operation; }
    public async Task<PluginLoginTicket?> AuthorizeAsync(PluginLoginRequest request, CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.AuthProvider);
        if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(PluginLoginBroker));
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentException("登录类型无效。");
        if (request.Kind != PluginLoginKind.Offline) _context.Permissions.Demand(PluginPermissionNames.Network);
        if (request.Kind == PluginLoginKind.Yggdrasil && (!PluginProviderRegistry.ValidUrl(request.AuthServerUrl) ||
            !request.AuthServerUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new PluginOperationException("InvalidAuthServer", "认证服务器必须使用 HTTPS。");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _operation, _context.Stopping);
        var host = _context.Host; Account? account; DeviceCodeWindow? popup = null;
        try
        {
            if (request.Kind == PluginLoginKind.MicrosoftDeviceCode)
            {
                var auth = new MicrosoftAuthService();
                auth.UserCodeReady += (uri, code) => host.Owner.Dispatcher.Invoke(() =>
                { popup = new DeviceCodeWindow(uri, code, stop); popup.Show(); });
                auth.StatusChanged += text => _ = host.Owner.Dispatcher.BeginInvoke(new Action(() => popup?.SetStatus(text)));
                account = await auth.LoginInteractiveAsync(stop.Token).ConfigureAwait(false);
            }
            else
            {
                var dialog = await host.OnUiAsync(() => new PluginCredentialDialog(_context.Metadata.Name, request), stop.Token).ConfigureAwait(false);
                var shown = await host.OnUiAsync(() => OverlayDialogService.ShowModalAsync(dialog), stop.Token).ConfigureAwait(false);
                if (await shown.ConfigureAwait(false) != true) return null;
                var input = await host.OnUiAsync(() => (dialog.Username, dialog.Password), stop.Token).ConfigureAwait(false);
                if (request.Kind == PluginLoginKind.Offline) account = OfflineAuthService.CreateOfflineAccount(input.Username);
                else account = await new AuthServerAuthService().LoginAsync(request.AuthServerUrl!, input.Username, input.Password, stop.Token).ConfigureAwait(false);
                await host.OnUiAsync(() => { dialog.ClearPassword(); return true; }, CancellationToken.None).ConfigureAwait(false);
            }
            if (account == null) return null;
            stop.Token.ThrowIfCancellationRequested(); _context.Permissions.Demand(PluginPermissionNames.AuthProvider);
            lock (_tickets)
            {
                if (Volatile.Read(ref _closed) != 0) return null;
                var id = Guid.NewGuid(); _tickets.Add(id, account); return new(id);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _context.Log("AuthenticationFailed: " + ex.GetType().Name); throw new PluginOperationException("AuthenticationFailed", "登录失败，请通过宿主账户页重试。"); }
        finally { if (popup != null) _ = host.Owner.Dispatcher.BeginInvoke(new Action(() => popup.Close())); }
    }
    internal Account Consume(PluginLoginTicket ticket)
    {
        lock (_tickets)
        {
            if (!_tickets.Remove(ticket.Id, out var account)) throw new PluginOperationException("InvalidLoginTicket", "登录句柄无效、来自其他插件或已过期。");
            return account;
        }
    }
    internal void Close() { Interlocked.Exchange(ref _closed, 1); lock (_tickets) _tickets.Clear(); }
}

internal sealed class PluginCredentialDialog : OverlayDialogControl
{
    private readonly TextBox _username = new() { Margin = new Thickness(0, 8, 0, 8), MinWidth = 280 };
    private readonly PasswordBox _password = new() { Margin = new Thickness(0, 8, 0, 8) };
    internal string Username => _username.Text.Trim();
    internal string Password => _password.Password;
    internal void ClearPassword() { _password.Clear(); }
    internal PluginCredentialDialog(string pluginName, PluginLoginRequest request)
    {
        Width = 480; var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = Loc.F("Str_Plugin_LoginTitle", "{0} · 登录", pluginName), FontSize = 20 });
        root.Children.Add(new TextBlock { Text = request.Kind == PluginLoginKind.Offline ? Loc.T("Str_Plugin_OfflineName", "离线账户名称") :
            Loc.F("Str_Plugin_AuthDestination", "将向以下认证服务器登录：{0}\n密码只由宿主处理。", request.AuthServerUrl), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_username); if (request.Kind == PluginLoginKind.Yggdrasil) root.Children.Add(_password);
        var ok = new Button { Content = Loc.T("Str_Ui_Sign_In", "登录"), Margin = new Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(Username)) CloseWith(true); };
        root.Children.Add(ok); Content = root;
    }
}
