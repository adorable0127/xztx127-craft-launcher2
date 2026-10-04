using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    private bool _pluginSourcesUpdating;
    private void InitializePluginSources()
    {
        void Refresh()
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(Refresh)); return; }
            _pluginSourcesUpdating = true;
            try
            {
                foreach (var item in SourceCombo.Items.OfType<ComboBoxItem>().Where(i => i.Tag is string id && id.StartsWith("plugin:", StringComparison.Ordinal)).ToArray()) SourceCombo.Items.Remove(item);
                foreach (var provider in PluginProviderRegistry.Snapshot("download"))
                    SourceCombo.Items.Add(new ComboBoxItem { Content = provider.Name, Tag = "plugin:" + provider.Id });
                var selected = _owner.ConfigService.Config.PluginDownloadProviderId;
                SourceCombo.SelectedItem = SourceCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == "plugin:" + selected)
                    ?? SourceCombo.Items[_owner.ConfigService.Config.Source == Models.DownloadSource.BMCLAPI ? 1 : 0];
            }
            finally { _pluginSourcesUpdating = false; }
        }
        var mods = new Button { Content = Loc.T("Str_Plugin_ModSources", "插件 Mod 来源…"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        mods.Click += async (_, _) => await OverlayDialogService.ShowModalAsync(new PluginModSourcesDialog(_owner, this));
        if (ModSourceCombo.Parent is Panel panel) panel.Children.Add(mods);
        Loaded += (_, _) => { PluginProviderRegistry.Changed -= Refresh; PluginProviderRegistry.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => PluginProviderRegistry.Changed -= Refresh;
        Refresh();
    }
}
