using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    private bool _pluginSourcesUpdating;
    private bool _modSourceSelectionUpdating;
    private bool _pluginModSourcesOpen;

    private async Task OpenPluginModSourcesAsync()
    {
        // “插件”是进入独立来源选择器的菜单项，不是内置搜索引擎。
        // 立即恢复原选项，避免关闭窗口后标签与仍显示的搜索结果不一致。
        _modSourceSelectionUpdating = true;
        try
        {
            var tag = _currentModSource switch
            {
                ModSource.Modrinth => "modrinth",
                ModSource.CurseForge => "curseforge",
                _ => "combined"
            };
            ModSourceCombo.SelectedItem = ModSourceCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as string) == tag);
            ModSourceCombo.IsDropDownOpen = false;
        }
        finally { _modSourceSelectionUpdating = false; }

        if (_pluginModSourcesOpen) return;
        _pluginModSourcesOpen = true;
        try
        {
            // 等下拉框完成收起后再打开遮罩，键盘和鼠标入口共用这一路径。
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            if (IsLoaded)
                await OverlayDialogService.ShowModalAsync(new PluginModSourcesDialog(_owner, this));
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning(Loc.T("Str_Plugin_OpenModSourcesFailed", "无法打开插件 Mod 来源：") + ex.Message);
        }
        finally { _pluginModSourcesOpen = false; }
    }

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
        Loaded += (_, _) => { PluginProviderRegistry.Changed -= Refresh; PluginProviderRegistry.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => PluginProviderRegistry.Changed -= Refresh;
        Refresh();
    }
}
