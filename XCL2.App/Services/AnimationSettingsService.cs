using XCL2.App.Models;

namespace XCL2.App.Services;

public enum AnimationKind
{
    WindowOpen,
    WindowMinimize,
    WindowRestore,
    WindowFullscreen,
    WindowClose,
    Page,
    Popup,
    Sidebar,
    ControlFeedback,
    Toast,
    Loading,
    SmoothScroll,
    SkinMotion,
    SkinAttack,
    SkinHurt
}

/// <summary>统一动画偏好入口。设置页的即时预览独立于配置，撤销时不会留下未保存值。</summary>
public static class AnimationSettingsService
{
    private static readonly string[] OptionKeys = Enum.GetNames<AnimationKind>();

    public static readonly IReadOnlyList<(AnimationKind Kind, string Label)> Options =
        new (AnimationKind Kind, string Label)[]
        {
            (AnimationKind.WindowOpen, "窗口打开"),
            (AnimationKind.WindowMinimize, "窗口最小化 / 收到托盘"),
            (AnimationKind.WindowRestore, "窗口恢复 / 从托盘还原"),
            (AnimationKind.WindowFullscreen, "窗口最大化 / 全屏切换"),
            (AnimationKind.WindowClose, "窗口关闭"),
            (AnimationKind.Page, "页面切换"),
            (AnimationKind.Popup, "弹窗 / 抽屉"),
            (AnimationKind.Sidebar, "侧边栏展开 / 收起"),
            (AnimationKind.ControlFeedback, "按钮 / 文字点击反馈"),
            (AnimationKind.Toast, "通知提示"),
            (AnimationKind.Loading, "加载指示"),
            (AnimationKind.SmoothScroll, "平滑滚动"),
            (AnimationKind.SkinMotion, "3D 皮肤日常动作"),
            (AnimationKind.SkinAttack, "3D 皮肤攻击动作"),
            (AnimationKind.SkinHurt, "3D 皮肤受伤动作 / 红色闪光")
        };

    private sealed record PreviewState(bool Enabled, bool LowPerformance, bool PageEnabled,
        double SpeedPercent, bool NoAnimations, NoAnimationOptimization Optimization,
        Dictionary<string, AnimationOption> Settings);

    private static PreviewState? _preview;

    private sealed record EffectiveState(double Speed, bool NoAnimations,
        NoAnimationOptimization Optimization, double[] Intensities);
    private static EffectiveState? _lastNotifiedState;

    public static event Action? Changed;

    public static bool NoAnimationModeEnabled
        => _preview?.NoAnimations ?? ConfigService.Active?.Config.CompletelyDisableAnimations ?? false;

    public static NoAnimationOptimization OptimizationPreference
        => _preview?.Optimization ?? ConfigService.Active?.Config.NoAnimationOptimization ?? NoAnimationOptimization.Balanced;

    public static double SpeedRatio
    {
        get
        {
            var percent = _preview?.SpeedPercent ?? ConfigService.Active?.Config.AnimationSpeedPercent ?? 75;
            return double.IsFinite(percent) ? Math.Clamp(percent / 100, 0.25, 1) : 0.75;
        }
    }

    /// <summary>75% 为传入的参考时长；100% 时快 1/3。受伤闪光不拉长，80ms 仍为 80ms。</summary>
    public static TimeSpan Duration(int milliseconds, AnimationKind kind)
    {
        if (!IsEnabled(kind)) return TimeSpan.Zero;
        var reference = Math.Max(0, milliseconds);
        return TimeSpan.FromMilliseconds(kind == AnimationKind.SkinHurt ? reference : reference * 0.75 / SpeedRatio);
    }

    public static double Intensity(AnimationKind kind)
    {
        // 命令行临时禁用不改写用户偏好，本次进程所有分类均静态显示。
        if (App.StartupArgs.DisableAnimations || NoAnimationModeEnabled) return 0;
        var index = (int)kind;
        if ((uint)index >= (uint)OptionKeys.Length) return 0;
        var preview = _preview;
        var config = ConfigService.Active?.Config;
        if (!(preview?.Enabled ?? config?.EnableUiAnimations ?? true)
            || (preview?.LowPerformance ?? config?.LowPerformanceMode ?? false)
            || (kind == AnimationKind.Page && !(preview?.PageEnabled ?? config?.EnablePageAnimations ?? true)))
            return 0;

        var settings = preview?.Settings ?? config?.AnimationSettings;
        if (settings == null || !settings.TryGetValue(OptionKeys[index], out var option) || option == null)
            return 1; // 旧配置没有这个分类时沿用默认效果。
        if (!option.Enabled) return 0;
        var percent = option.IntensityPercent;
        return double.IsFinite(percent) ? Math.Clamp(percent / 100, 0, 1) : 1;
    }

    public static bool IsEnabled(AnimationKind kind) => Intensity(kind) > 0;

    private static EffectiveState CaptureEffectiveState()
        => new(SpeedRatio, NoAnimationModeEnabled, OptimizationPreference,
            Options.Select(option => Intensity(option.Kind)).ToArray());

    public static void NotifyChanged()
    {
        var current = CaptureEffectiveState();
        var previous = _lastNotifiedState;
        _lastNotifiedState = current;
        // 即时预览已经应用了滑杆值。保存同一组值时无需再次结束动画，
        // 否则自动保存会把刚开始的预览/正在播放的过渡强行截断。
        if (previous != null && previous.Speed == current.Speed
            && previous.NoAnimations == current.NoAnimations
            && previous.Optimization == current.Optimization
            && previous.Intensities.SequenceEqual(current.Intensities)) return;
        Changed?.Invoke();
    }

    public static AnimationOption GetOption(AppConfig config, AnimationKind kind)
    {
        var index = (int)kind;
        if ((uint)index >= (uint)OptionKeys.Length
            || !config.AnimationSettings.TryGetValue(OptionKeys[index], out var option) || option == null)
            return new AnimationOption();
        return new AnimationOption { Enabled = option.Enabled, IntensityPercent = option.IntensityPercent };
    }

    public static void SetPreview(AppConfig config)
    {
        _lastNotifiedState ??= CaptureEffectiveState();
        _preview = new PreviewState(config.EnableUiAnimations, config.LowPerformanceMode,
            config.EnablePageAnimations, config.AnimationSpeedPercent, config.CompletelyDisableAnimations,
            config.NoAnimationOptimization, Options.ToDictionary(item => item.Kind.ToString(),
                item => GetOption(config, item.Kind), StringComparer.OrdinalIgnoreCase));
        NotifyChanged();
    }

    public static void ClearPreview()
    {
        if (_preview == null) return;
        _lastNotifiedState ??= CaptureEffectiveState();
        _preview = null;
        NotifyChanged();
    }
}
