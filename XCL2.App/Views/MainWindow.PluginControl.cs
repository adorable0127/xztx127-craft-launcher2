using System.Collections.Immutable;
using System.Windows;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private (string Id, string Title, Action Navigate)[] PluginBuiltInPages => new (string, string, Action)[]
    {
        ("home", "首页", NavigateToHome), ("versions", "版本管理", NavigateToVersions),
        ("downloads", "下载中心", NavigateToDownloadCenter), ("multiplayer", "联机", NavigateToMultiplayer),
        ("mods", "Mod 管理", NavigateToModManager), ("worlds", "存档", NavigateToWorlds),
        ("servers", "服务端管理", NavigateToServerManager), ("accounts", "账户", NavigateToAccounts),
        ("settings", "设置", NavigateToSettings), ("logs", "日志", NavigateToLogs),
        ("ai", "AI 助手", NavigateToAiAssistant), ("toolbox", "百宝箱", NavigateToToolbox),
        ("bedrock", "基岩版启动", NavigateToBedrock), ("about", "鸣谢与帮助", NavigateToAboutHelp),
        ("api", "API 开发中心", NavigateToApiDocumentation)
    };

    internal ImmutableArray<PluginPageInfo> GetPluginNavigablePages() => PluginBuiltInPages
        .Select(p => new PluginPageInfo(p.Id, p.Title, false))
        .Concat(PluginUiContributionRegistry.Snapshot("navigation").Select(e =>
            new PluginPageInfo("plugin:" + e.Id, e.Context.Safe(((PluginNavigationPageDefinition)e.Definition).Title), true)))
        .ToImmutableArray();

    internal bool NavigateFromPlugin(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        var generation = _pageNavigationGeneration;
        var builtIn = PluginBuiltInPages.FirstOrDefault(p => p.Id == pageId);
        if (builtIn.Navigate != null) builtIn.Navigate();
        else
        {
            var entry = PluginUiContributionRegistry.Snapshot("navigation").FirstOrDefault(e => "plugin:" + e.Id == pageId)
                ?? throw new PluginOperationException("UnknownPage", "页面不存在或所属插件已停止。");
            var definition = (PluginNavigationPageDefinition)entry.Definition;
            NavigateLazy(() =>
            {
                entry.Context.Permissions.Demand(PluginPermissionNames.UiNavigation);
                var content = definition.CreateContent(new PluginUiContext(entry.Context, "navigation:" + entry.Id));
                if (content == null || content.Parent != null) throw new PluginOperationException("InvalidUi", "插件页面必须返回新的控件。");
                return content;
            }, _ => _activePluginPage = entry.Id);
        }
        return _pageNavigationGeneration != generation;
    }

    public void NavigateToApiDocumentation() => NavigateLazy(() => new PluginApiDocumentationPage());
    internal bool PluginLaunchInProgress => _isCancelLaunchState || !LaunchGameBtn.IsEnabled;
    internal PluginWindowSnapshot CopyPluginWindowSnapshot() => new(Title, WindowState.ToString(),
        double.IsFinite(Width) ? Width : ActualWidth, double.IsFinite(Height) ? Height : ActualHeight, Topmost, _sidebarCollapsed);

    internal PluginWindowSnapshot UpdateWindowFromPlugin(PluginWindowPatch patch)
    {
        if (patch.Title is { Length: > 128 } || patch.Title?.Any(char.IsControl) == true)
            throw new PluginOperationException("InvalidArgument", "窗口标题最长 128 字符，不能含控制字符。");
        if (patch.Width is { } width && (!double.IsFinite(width) || width < MinWidth || width > 7680) ||
            patch.Height is { } height && (!double.IsFinite(height) || height < MinHeight || height > 4320))
            throw new PluginOperationException("InvalidArgument", "窗口尺寸超出允许范围。");
        if (patch.Title != null) Title = patch.Title;
        if (patch.Width.HasValue) Width = patch.Width.Value;
        if (patch.Height.HasValue) Height = patch.Height.Value;
        if (patch.Topmost.HasValue) Topmost = patch.Topmost.Value;
        if (patch.SidebarCollapsed.HasValue) ApplySidebarCollapsedState(patch.SidebarCollapsed.Value);
        return CopyPluginWindowSnapshot();
    }

    internal PluginWindowSnapshot SetWindowStateFromPlugin(PluginWindowAction action)
    {
        switch (action)
        {
            case PluginWindowAction.Activate: RestoreFromTray(); Activate(); break;
            case PluginWindowAction.Minimize: WindowState = WindowState.Minimized; break;
            case PluginWindowAction.Maximize: Show(); WindowState = WindowState.Maximized; break;
            case PluginWindowAction.Restore: RestoreFromTray(); WindowState = WindowState.Normal; break;
            default: throw new PluginOperationException("InvalidArgument", "未知窗口操作。");
        }
        return CopyPluginWindowSnapshot();
    }

    internal PluginLaunchAcceptance LaunchFromPlugin()
    {
        if (PluginLaunchInProgress || DateTime.UtcNow - _lastLaunchClickAtUtc < LaunchClickCooldown ||
            DateTime.UtcNow - _lastLaunchStateSwitchAtUtc < LaunchStateSwitchGuard) return new(false, "Busy");
        if (ConfigService.GetSelectedAccount() == null) return new(false, "AccountNotFound");
        if (string.IsNullOrWhiteSpace(ConfigService.Config.SelectedVersionId)) return new(false, "InstanceNotFound");
        Launch_Click(this, new RoutedEventArgs());
        return new(PluginLaunchInProgress, PluginLaunchInProgress ? "Accepted" : "NotStarted");
    }

    internal bool CancelLaunchFromPlugin()
    {
        if (!_isCancelLaunchState) return false;
        HandleCancelLaunchClick();
        return true;
    }

    internal void EnsurePluginSettingsWritable()
    {
        // 设置页持有控件快照；后台写入会被该页下一次保存覆盖，因此要求先离开设置页。
        if (MainContent.Content is SettingsPage)
            throw new PluginOperationException("SettingsPageOpen", "请先离开设置页，再通过 API 修改启动器设置。");
    }

    internal void ApplyPluginSettings()
    {
        var cfg = ConfigService.Config;
        ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, cfg.UiSkin, cfg.IsDarkMode, cfg.CustomAccentColor);
        ThemeService.ApplyWindowTransparency(cfg.EnableWindowTransparency, cfg.WindowOpacityPercent);
        ThemeService.ApplyGlobalWindowTransparency(cfg.EnableGlobalWindowTransparency, cfg.GlobalWindowOpacityPercent);
        ThemeService.ApplyTextOpacity(cfg.TextOpacityPercent);
        ThemeService.ApplyBrightness(cfg.BrightnessPercent);
        UiZoomService.PreviewPercent(cfg.UiZoomPercent);
        ApplyConfigDrivenVisibility();
        RefreshSidebar();
    }
}
