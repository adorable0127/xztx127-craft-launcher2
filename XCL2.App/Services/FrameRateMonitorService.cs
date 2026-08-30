using System.Windows.Media;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 只在用户开启"高性能模式"（更强的界面动效，见 AppConfig.EnableHighPerformanceMode）时才
/// 启动的轻量帧率监视器。需求原文很明确："仅在低性能设备上提示帧率问题"——不做任何自动降级
/// /自动关闭，用户自己权衡视觉效果和流畅度，这里只负责在检测到持续掉帧时用一次性 Toast 提醒，
/// 每次启动器会话最多提示一次，避免掉帧时反复弹烦人。
///
/// 实现上用 CompositionTarget.Rendering 这个"每次 WPF 要合成新一帧就会触发"的事件采样帧
/// 间隔，不需要额外开线程/定时器；只在高性能模式开启时挂钩，关闭时立刻摘掉，本身不产生
/// 任何持续开销，符合"不考虑性能开销，用户自己选"里"至少不要平白无故拖慢关掉这个开关的人"
/// 的隐含要求。
/// </summary>
public static class FrameRateMonitorService
{
    // 连续 3 秒的滑动窗口内平均帧率低于这个阈值，才判定为"这台设备撑不起高性能模式的动效"——
    // 阈值定得比"能不能看"宽松一些（30fps 对界面动效来说已经算能接受的下限），避免正常设备上
    // 偶尔一次掉帧（比如切页那一瞬间加载资源）就被误判打扰用户。
    private const double LowFpsThreshold = 30.0;
    private const double SustainedSeconds = 3.0;

    private static bool _hooked;
    private static bool _hasWarnedThisSession;
    private static DateTime _lastFrameTime;
    private static readonly Queue<(DateTime time, double fps)> _samples = new();

    /// <summary>由设置页保存设置时调用（跟 Win11EffectsService.SetEnabled / ThemeService.
    /// ApplyGlobalWindowTransparency 同一套"保存后立即生效"接线方式），以及 App 启动时用早期
    /// 配置调一次。</summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled == _hooked) return;
        if (enabled)
        {
            _samples.Clear();
            _lastFrameTime = DateTime.MinValue;
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (_lastFrameTime == DateTime.MinValue) { _lastFrameTime = now; return; }

        var delta = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;
        if (delta <= 0) return;

        var fps = 1.0 / delta;
        _samples.Enqueue((now, fps));
        while (_samples.Count > 0 && (now - _samples.Peek().time).TotalSeconds > SustainedSeconds)
            _samples.Dequeue();

        if (_hasWarnedThisSession || _samples.Count < 30) return; // 样本太少（刚启用/刚切页）先不判断

        var windowSeconds = (now - _samples.Peek().time).TotalSeconds;
        if (windowSeconds < SustainedSeconds) return; // 还没攒够一个完整的采样窗口

        var avgFps = _samples.Average(s => s.fps);
        if (avgFps >= LowFpsThreshold) return;

        _hasWarnedThisSession = true;
        // 只提醒，不动用户的设置——由用户自己决定要不要去设置页关掉这个开关。
        ToastService.ShowWarning("检测到界面持续掉帧，可能是设备性能吃紧导致——可以在设置页关闭"
            + "\"高性能模式\"改善流畅度（不会自动帮你关）");
    }
}
