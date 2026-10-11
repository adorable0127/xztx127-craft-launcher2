using System.Collections.Immutable;

namespace XCL2.App.Services.Plugins;

public sealed record PluginPageInfo(string Id, string Title, bool IsPluginPage);
public sealed record PluginWindowSnapshot(string Title, string State, double Width, double Height, bool Topmost, bool SidebarCollapsed);
public sealed record PluginControlSnapshot(PluginWindowSnapshot Window, bool IsLaunching, string? InstanceId, string? AccountId);
public sealed record PluginLaunchAcceptance(bool Accepted, string Code);
public enum PluginWindowAction { Activate, Minimize, Maximize, Restore }

/// <summary>未提供的字段保持原值。窗口布局只在本次启动器会话生效。</summary>
public sealed record PluginWindowPatch
{
    public string? Title { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool? Topmost { get; init; }
    public bool? SidebarCollapsed { get; init; }
}

/// <summary>仅公开非敏感设置。先验证整个补丁，再一次应用和保存；null 保持原值。</summary>
public sealed record PluginSettingsPatch
{
    public string? UiSkin { get; init; }
    public bool? IsDarkMode { get; init; }
    public string? CustomAccentColor { get; init; }
    public bool? SimplifiedModeEnabled { get; init; }
    public bool? EnablePageAnimations { get; init; }
    public bool? EnableUiAnimations { get; init; }
    public int? UiZoomPercent { get; init; }
    public bool? EnableWindowTransparency { get; init; }
    public int? WindowOpacityPercent { get; init; }
    public bool? EnableGlobalWindowTransparency { get; init; }
    public int? GlobalWindowOpacityPercent { get; init; }
    public int? TextOpacityPercent { get; init; }
    public int? BrightnessPercent { get; init; }
    public int? MinMemoryMb { get; init; }
    public int? MaxMemoryMb { get; init; }
    public bool? IsolateVersionsByDefault { get; init; }
    public string? BuiltInDownloadSource { get; init; }
    public double? CallbackTimeoutSeconds { get; init; }
}

public sealed record PluginAppearanceSnapshot(string UiSkin, bool IsDarkMode, string? CustomAccentColor,
    int UiZoomPercent, bool EnableWindowTransparency, int WindowOpacityPercent, bool EnableGlobalWindowTransparency,
    int GlobalWindowOpacityPercent, int TextOpacityPercent, int BrightnessPercent, double CallbackTimeoutSeconds,
    ImmutableArray<string> AvailableSkins);
