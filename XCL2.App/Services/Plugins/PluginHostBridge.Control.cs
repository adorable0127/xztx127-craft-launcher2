using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services.Plugins;

internal sealed partial class PluginHostBridge
{
    internal PluginControlSnapshot CopyControlSnapshot() => new(_owner.CopyPluginWindowSnapshot(),
        _owner.PluginLaunchInProgress, SelectedInstanceId(), _config.GetSelectedAccount()?.Id);

    internal PluginAppearanceSnapshot CopyAppearance()
    {
        var cfg = _config.Config;
        return new(cfg.UiSkin, cfg.IsDarkMode, cfg.CustomAccentColor, cfg.UiZoomPercent,
            cfg.EnableWindowTransparency, cfg.WindowOpacityPercent, cfg.EnableGlobalWindowTransparency,
            cfg.GlobalWindowOpacityPercent, cfg.TextOpacityPercent, cfg.BrightnessPercent,
            CallbackTimeout.TotalSeconds, ThemeService.AllSkins.ToImmutableArray());
    }

    internal bool SelectAccount(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (_owner.PluginLaunchInProgress) throw new PluginOperationException("Busy", "启动进行中，暂时不能切换账户。");
        if (_config.Config.GuestModeEnabled)
        {
            if (_config.GuestAccount?.Id == accountId) return true;
            throw new PluginOperationException("GuestMode", "访客模式只允许当前临时账户。");
        }
        if (!_config.Accounts.Any(a => a.Id == accountId)) throw new PluginResourceNotFoundException();
        _config.SelectAccount(accountId);
        _owner.RefreshSidebar();
        return true;
    }

    internal bool SelectInstance(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (_owner.PluginLaunchInProgress) throw new PluginOperationException("Busy", "启动进行中，暂时不能切换实例。");
        if (!_instances.TryGetValue(instanceId, out var instance) || !Directory.Exists(instance.Details.InstanceDirectory))
            throw new PluginResourceNotFoundException();
        if (!instance.Details.Instance.IsInstalled) throw new PluginOperationException("InstanceIncomplete", "实例尚未安装完成。");
        _config.Config.SelectedFolderPath = instance.Root;
        _config.Config.SelectedVersionId = instance.VersionId;
        _config.Save();
        _owner.RefreshSidebar();
        return true;
    }

    internal PluginAppearanceSnapshot UpdateSettings(PluginSettingsPatch patch)
    {
        _owner.EnsurePluginSettingsWritable();
        var cfg = _config.Config;
        static void Range(int? value, int min, int max, string name)
        {
            if (value.HasValue && (value < min || value > max))
                throw new PluginOperationException("InvalidArgument", $"{name} 必须在 {min}～{max} 之间。");
        }
        Range(patch.UiZoomPercent, UiZoomService.MinPercent, UiZoomService.MaxPercent, nameof(patch.UiZoomPercent));
        Range(patch.WindowOpacityPercent, 20, 100, nameof(patch.WindowOpacityPercent));
        Range(patch.GlobalWindowOpacityPercent, 50, 100, nameof(patch.GlobalWindowOpacityPercent));
        Range(patch.TextOpacityPercent, 50, 100, nameof(patch.TextOpacityPercent));
        Range(patch.BrightnessPercent, 0, 200, nameof(patch.BrightnessPercent));
        Range(patch.MinMemoryMb, 256, 1048576, nameof(patch.MinMemoryMb));
        Range(patch.MaxMemoryMb, 256, 1048576, nameof(patch.MaxMemoryMb));
        if ((patch.MinMemoryMb ?? cfg.MinMemoryMb) > (patch.MaxMemoryMb ?? cfg.MaxMemoryMb))
            throw new PluginOperationException("InvalidArgument", "最小内存不能大于最大内存。");
        if (patch.UiSkin != null && !ThemeService.AllSkins.Contains(patch.UiSkin, StringComparer.Ordinal))
            throw new PluginOperationException("InvalidArgument", "未知皮肤，请使用 appearance.snapshot 返回的 availableSkins。");
        if (patch.CustomAccentColor != null && !Regex.IsMatch(patch.CustomAccentColor, "^#[0-9a-fA-F]{6}$"))
            throw new PluginOperationException("InvalidArgument", "强调色必须是 #RRGGBB。");
        if (patch.BuiltInDownloadSource != null && patch.BuiltInDownloadSource is not ("official" or "bmclapi"))
            throw new PluginOperationException("InvalidArgument", "下载源只能为 official 或 bmclapi。");
        if (patch.CallbackTimeoutSeconds.HasValue && (!double.IsFinite(patch.CallbackTimeoutSeconds.Value) ||
            patch.CallbackTimeoutSeconds.Value < 0.1 || patch.CallbackTimeoutSeconds.Value > 3600))
            throw new PluginOperationException("InvalidArgument", "回调等待时间必须在 0.1～3600 秒之间。");

        // 全部校验完成以后才改动；禁止通过反射任意修改 AppConfig 或授权配置。
        if (patch.UiSkin != null) cfg.UiSkin = patch.UiSkin;
        if (patch.IsDarkMode.HasValue) cfg.IsDarkMode = patch.IsDarkMode.Value;
        if (patch.CustomAccentColor != null) cfg.CustomAccentColor = patch.CustomAccentColor;
        if (patch.SimplifiedModeEnabled.HasValue) cfg.SimplifiedModeEnabled = patch.SimplifiedModeEnabled.Value;
        if (patch.EnablePageAnimations.HasValue) cfg.EnablePageAnimations = patch.EnablePageAnimations.Value;
        if (patch.EnableUiAnimations.HasValue) cfg.EnableUiAnimations = patch.EnableUiAnimations.Value;
        if (patch.UiZoomPercent.HasValue) cfg.UiZoomPercent = patch.UiZoomPercent.Value;
        if (patch.EnableWindowTransparency.HasValue) cfg.EnableWindowTransparency = patch.EnableWindowTransparency.Value;
        if (patch.WindowOpacityPercent.HasValue) cfg.WindowOpacityPercent = patch.WindowOpacityPercent.Value;
        if (patch.EnableGlobalWindowTransparency.HasValue) cfg.EnableGlobalWindowTransparency = patch.EnableGlobalWindowTransparency.Value;
        if (patch.GlobalWindowOpacityPercent.HasValue) cfg.GlobalWindowOpacityPercent = patch.GlobalWindowOpacityPercent.Value;
        if (patch.TextOpacityPercent.HasValue) cfg.TextOpacityPercent = patch.TextOpacityPercent.Value;
        if (patch.BrightnessPercent.HasValue) cfg.BrightnessPercent = patch.BrightnessPercent.Value;
        if (patch.MinMemoryMb.HasValue) cfg.MinMemoryMb = patch.MinMemoryMb.Value;
        if (patch.MaxMemoryMb.HasValue) cfg.MaxMemoryMb = patch.MaxMemoryMb.Value;
        if (patch.IsolateVersionsByDefault.HasValue) cfg.IsolateVersionsByDefault = patch.IsolateVersionsByDefault.Value;
        if (patch.BuiltInDownloadSource != null) cfg.Source = patch.BuiltInDownloadSource == "official" ? DownloadSource.Official : DownloadSource.BMCLAPI;
        if (patch.CallbackTimeoutSeconds.HasValue) cfg.PluginCallbackTimeoutSeconds = patch.CallbackTimeoutSeconds.Value;
        _config.Save();
        _owner.ApplyPluginSettings();
        return CopyAppearance();
    }
}
