using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class VersionSelectPage
{
    private void PluginInstanceMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Tag is PluginUiContributionRegistry.Entry).ToArray()) menu.Items.Remove(item);
        if (menu.PlacementTarget is not FrameworkElement target || target.DataContext is not GameVersion version) return;
        var root = _owner.ConfigService.Config.SelectedFolderPath;
        if (string.IsNullOrWhiteSpace(root)) return;
        var instanceId = PluginHostBridge.InstanceId(root, version.Id);
        foreach (var entry in PluginUiContributionRegistry.Snapshot("instance-action").OrderBy(i => ((PluginInstanceActionDefinition)i.Definition).Order).ThenBy(i => i.Id, StringComparer.Ordinal))
        {
            var definition = (PluginInstanceActionDefinition)entry.Definition;
            var action = new MenuItem { Header = entry.Context.Host.Safe(definition.Title), Tag = entry };
            action.Click += async (_, _) =>
            {
                try
                {
                    entry.Context.Permissions.Demand(PluginPermissionNames.UiInstanceActions);
                    var detail = await entry.Context.Host.GetInstanceDetailsAsync(instanceId, entry.Context.Stopping) ?? throw new PluginResourceNotFoundException();
                    await entry.Context.Callbacks.InvokeAsync(PluginPermissionNames.UiInstanceActions, "InstanceAction:" + entry.Id,
                        async token => { await definition.ExecuteAsync(detail, token); return true; }, entry.Context.Host.CallbackTimeout, entry.Context.Stopping);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { entry.Context.Log("InstanceActionFailed: " + ex.GetType().Name); MessageBoxDialog.ShowWarning(Loc.T("Str_Plugin_InstanceActionFailed", "插件实例操作失败。"), definition.Title); }
            };
            menu.Items.Add(action);
        }
    }
}
