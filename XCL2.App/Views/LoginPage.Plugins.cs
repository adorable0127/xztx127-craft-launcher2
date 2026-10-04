using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class LoginPage
{
    private void InitializePluginLogins()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var choices = new ComboBox { Width = 230, DisplayMemberPath = nameof(PluginProviderInfo.Name) };
        var login = new Button { Content = Loc.T("Str_Plugin_Login", "插件登录"), Margin = new Thickness(10, 0, 0, 0) };
        row.Children.Add(choices); row.Children.Add(login); DockPanel.SetDock(row, Dock.Top);
        if (StatusText.Parent is DockPanel panel) panel.Children.Insert(panel.Children.IndexOf(StatusText), row);
        void Refresh()
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(Refresh)); return; }
            var selected = (choices.SelectedItem as PluginProviderInfo)?.Id;
            choices.ItemsSource = PluginProviderRegistry.Snapshot("login");
            choices.SelectedItem = choices.Items.OfType<PluginProviderInfo>().FirstOrDefault(p => p.Id == selected) ?? choices.Items.OfType<PluginProviderInfo>().FirstOrDefault();
            row.Visibility = choices.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        Loaded += (_, _) => { PluginProviderRegistry.Changed -= Refresh; PluginProviderRegistry.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => PluginProviderRegistry.Changed -= Refresh;
        login.Click += async (_, _) =>
        {
            if (choices.SelectedItem is not PluginProviderInfo info) return; login.IsEnabled = false;
            PluginLoginBroker? broker = null;
            try
            {
                var entry = PluginProviderRegistry.Get(info.Id, "login");
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(entry.Context.Stopping);
                broker = new PluginLoginBroker(entry.Context, stop.Token);
                var ticket = await PluginProviderRegistry.InvokeAsync(entry,
                    token => ((IPluginLoginProvider)entry.Provider).LoginAsync(broker, token), stop.Token, TimeSpan.FromMinutes(10));
                if (ticket == null) return;
                var account = broker.Consume(ticket); entry.Context.Permissions.Demand(PluginPermissionNames.AuthProvider);
                _owner.ConfigService.AddOrUpdateAccount(account); Reload(); _owner.RefreshSidebar();
                StatusText.Text = Loc.T("Str_Plugin_LoginSucceeded", "登录成功，请在账户列表中选用。");
            }
            catch (OperationCanceledException) { StatusText.Text = Loc.T("Str_Cs_Sign_In_Cancelled", "登录已取消。"); }
            catch (Exception ex) { StatusText.Text = ex is PluginOperationException operation ? operation.SafeMessage : Loc.T("Str_Plugin_LoginFailed", "插件登录失败，可以使用内置登录方式。"); }
            finally { broker?.Close(); login.IsEnabled = true; }
        };
        Refresh();
    }
}
