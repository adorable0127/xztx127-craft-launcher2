using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private readonly List<Button> _pluginNavButtons = new();
    private string? _activePluginPage;
    private string? _appliedPluginBackground;
    private bool _refreshingPluginUi;
    private bool _pluginUiInitialized;
    private void InitializePluginUi()
    {
        if (_pluginUiInitialized) return; _pluginUiInitialized = true;
        PluginUiContributionRegistry.Changed += RefreshPluginUi;
        ConfigService.Saved += RefreshPluginUi;
        Closed += (_, _) => { PluginUiContributionRegistry.Changed -= RefreshPluginUi; ConfigService.Saved -= RefreshPluginUi; };
        RefreshPluginUi();
    }
    private void RefreshPluginUi()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(RefreshPluginUi)); return; }
        if (_refreshingPluginUi) return; _refreshingPluginUi = true;
        try
        {
            foreach (var button in _pluginNavButtons) NavButtonsPanel.Children.Remove(button); _pluginNavButtons.Clear();
            var navigation = PluginUiContributionRegistry.Snapshot("navigation").Where(entry =>
            {
                var definition = (PluginNavigationPageDefinition)entry.Definition;
                return (!ConfigService.Config.SimplifiedModeEnabled || definition.ShowInSimplifiedMode) &&
                    (definition.Languages.IsDefaultOrEmpty || definition.Languages.Contains(ConfigService.Config.LauncherLanguage, StringComparer.OrdinalIgnoreCase));
            }).ToArray();
            if (_activePluginPage != null && !navigation.Any(e => e.Id == _activePluginPage)) { _activePluginPage = null; ShowHome(); }
            foreach (var entry in navigation.OrderBy(e => ((PluginNavigationPageDefinition)e.Definition).Order).ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                var definition = (PluginNavigationPageDefinition)entry.Definition;
                if ((ConfigService.Config.SimplifiedModeEnabled && !definition.ShowInSimplifiedMode) ||
                    (!definition.Languages.IsDefaultOrEmpty && !definition.Languages.Contains(ConfigService.Config.LauncherLanguage, StringComparer.OrdinalIgnoreCase))) continue;
                var button = new Button { Content = entry.Context.Host.Safe(definition.Title), ToolTip = definition.Title, Margin = new Thickness(0, 4, 0, 0) };
                button.SetResourceReference(StyleProperty, "SideNavButton");
                button.Click += (_, _) =>
                {
                    try
                    {
                        entry.Context.Permissions.Demand(PluginPermissionNames.UiNavigation);
                        var content = definition.CreateContent(new PluginUiContext(entry.Context, "navigation:" + entry.Id));
                        if (content == null || content.Parent != null) throw new InvalidOperationException("插件页面必须返回新的控件。");
                        SetMainContent(content); if (ReferenceEquals(MainContent.Content, content)) _activePluginPage = entry.Id;
                    }
                    catch (Exception ex) { entry.Context.Log("PageCreationFailed: " + ex.GetType().Name); MessageBoxDialog.ShowWarning(Loc.T("Str_Plugin_PageFailed", "插件页面创建失败，可以返回首页。"), definition.Title); }
                };
                NavButtonsPanel.Children.Add(button); _pluginNavButtons.Add(button);
            }
            var background = PluginUiContributionRegistry.Snapshot("background").FirstOrDefault(e => e.Id == ConfigService.Config.PluginBackgroundId);
            if (background != null)
            {
                try
                {
                    var image = PluginUiContributionRegistry.ResolveImage(background.Context, ((PluginBackgroundDefinition)background.Definition).RelativeImagePath);
                    if (_appliedPluginBackground != image && SetCustomBackgroundImage(image)) _appliedPluginBackground = image;
                }
                catch (Exception ex) { background.Context.Log("BackgroundFailed: " + ex.GetType().Name); RestorePluginBackground(); }
            }
            else RestorePluginBackground();
        }
        finally { _refreshingPluginUi = false; }
    }
    private void RestorePluginBackground()
    {
        if (_appliedPluginBackground == null) return; _appliedPluginBackground = null;
        if (!SetCustomBackgroundImage(ConfigService.Config.CustomBackgroundImagePath)) SetCustomBackgroundImage(null);
    }
}
