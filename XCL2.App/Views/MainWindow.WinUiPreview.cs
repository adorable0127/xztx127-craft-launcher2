using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private bool _previewVisualMode;
    private bool _previewSwitching;
    public event Action? NativePreviewStateChanged;
    // Public compatibility name now represents the full launcher UI mode.
    public bool IsNativePreviewOpen => _previewVisualMode;
    internal bool CanLaunchFromNativePreview => !_isCancelLaunchState && LaunchGameBtn.IsEnabled
        && DateTime.UtcNow - _lastLaunchClickAtUtc >= LaunchClickCooldown
        && DateTime.UtcNow - _lastLaunchStateSwitchAtUtc >= LaunchStateSwitchGuard;

    private void InitializePreviewVisualMode()
    {
        var contentDescriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
        if (contentDescriptor != null)
        {
            EventHandler changed = (_, _) => UpdatePreviewNavigationSelection();
            contentDescriptor.AddValueChanged(MainContent, changed);
            Closed += (_, _) => contentDescriptor.RemoveValueChanged(MainContent, changed);
        }
        if (ConfigService.Config.PreviewModeEnabled || App.StartupArgs.EnterPreviewMode) SetPreviewVisualMode(true, persist: false);
    }
    public bool OpenWinUiPreview() => SetPreviewVisualMode(true);
    public void CloseNativePreview() => SetPreviewVisualMode(false);

    private bool SetPreviewVisualMode(bool enabled, bool persist = true)
    {
        Dispatcher.VerifyAccess();
        if (_previewSwitching) return false;
        if (_previewVisualMode == enabled) return true;
        bool previous = _previewVisualMode;
        bool previousConfig = ConfigService.Config.PreviewModeEnabled;
        bool wasVisible = IsVisible;
        var focused = Keyboard.FocusedElement;
        _previewSwitching = true;
        try
        {
            DownloadQueuePopup.IsOpen = false;
            // Remove the old presentation before showing the new one.
            // Existing page objects retain handlers, drafts and running tasks.
            if (wasVisible) Hide();
            PreviewFluentTheme.SetEnabled(enabled, this);
            _previewVisualMode = enabled;
            ApplyPreviewLayout();
            ThemeService.ApplyWinUi3Typography(enabled || ConfigService.Config.EnableWinUi3Design);
            if (persist) { ConfigService.Config.PreviewModeEnabled = enabled; ConfigService.Save(); }
            RaisePreviewStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            _previewVisualMode = previous;
            ConfigService.Config.PreviewModeEnabled = previousConfig;
            try
            {
                PreviewFluentTheme.SetEnabled(previous, this);
                ApplyPreviewLayout();
                ThemeService.ApplyWinUi3Typography(previous || ConfigService.Config.EnableWinUi3Design);
                if (persist) ConfigService.Save();
            }
            catch (Exception restoreError) { LauncherLogService.AppendLine("[界面切换恢复] " + restoreError); }
            RaisePreviewStateChanged();
            LauncherLogService.AppendLine("[新界面预览] " + ex);
            if (wasVisible) Show();
            MessageBoxDialog.ShowWarning("新界面切换失败，已恢复原界面：" + ex.Message, "新界面预览");
            return false;
        }
        finally
        {
            _previewSwitching = false;
            if (wasVisible)
            {
                Show(); Activate();
                if (focused is UIElement { IsVisible: true } element) element.Focus();
            }
        }
    }
    private void ApplyPreviewLayout()
    {
        double height = WindowChromeService.IsFullScreen ? 0 : _previewVisualMode ? 40 : 32;
        TitleBarRow.Height = new GridLength(height);
        CustomTitleBar.Visibility = WindowChromeService.IsFullScreen ? Visibility.Collapsed : Visibility.Visible;
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            if (chrome.IsFrozen) { chrome = (WindowChrome)chrome.Clone(); WindowChrome.SetWindowChrome(this, chrome); }
            chrome.CaptionHeight = height;
        }
        ContentAreaBorder.Margin = new Thickness(_previewVisualMode ? 24 : 16);
        ApplySidebarCollapsedState(_sidebarCollapsed);
        MaximizeRestoreButton.Visibility = Visibility.Visible;
        if (!WindowChromeService.IsFullScreen) ResizeMode = ResizeMode.CanResize;
        UpdatePreviewNavigationSelection();
    }

    private void UpdatePreviewNavigationSelection()
    {
        var active = MainContent.Content?.GetType().Name switch
        {
            "HomePage" => NavHomeButton,
            "VersionSelectPage" => NavVersionsButton,
            "DownloadCenterPage" or "ModDetailPage" => NavDownloadButton,
            "MultiplayerPage" => NavMultiplayerButton,
            "WorldArchivePage" => NavWorldsButton,
            "ModManagerPage" => NavModManagerButton,
            "ServerManagerPage" => NavServerManagerButton,
            "ToolboxPage" => NavToolboxButton,
            "BedrockPage" => NavBedrockButton,
            "AboutHelpPage" => NavAboutHelpButton,
            "LoginPage" => NavAccountsButton,
            "SettingsPage" => NavSettingsButton,
            "MorePage" => NavMoreButton,
            "LogsPage" => NavLogsButton,
            "AiAssistantPanel" => NavAiAssistantButton,
            "PluginApiDocumentationPage" => NavPluginsButton,
            _ => null
        };
        foreach (var button in new[] { NavHomeButton, NavVersionsButton, NavDownloadButton,
            NavMultiplayerButton, NavWorldsButton, NavModManagerButton, NavServerManagerButton,
            NavToolboxButton, NavBedrockButton, NavAboutHelpButton, NavAccountsButton,
            NavSettingsButton, NavMoreButton, NavLogsButton, NavAiAssistantButton,
            NavExperimentalButton, NavPluginsButton })
            button.Tag = _previewVisualMode && ReferenceEquals(button, active) ? "PreviewSelected" : null;
        if (MainContent.Content is DependencyObject page) PreviewFluentTheme.ApplyTo(page);
    }

    private void NavPlugins_Click(object sender, RoutedEventArgs e) => OverlayDialogService.ShowModal(new PluginManagerDialog(this));
    private void RaisePreviewStateChanged()
    {
        if (NativePreviewStateChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { LauncherLogService.AppendLine("[新界面状态同步] " + ex.Message); }
        }
    }
}
