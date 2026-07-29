using System.Diagnostics;

namespace XCL2.App.Services;

/// <summary>
/// 智能限速监控：周期性采样"系统网卡总吞吐"和"本程序自己下载消耗的吞吐"，
/// 用两者的差值估算"其他程序正在使用的带宽"，据此动态调整
/// <see cref="DownloadRateLimiter.BytesPerSecond"/>，实现"不抢占其他程序带宽"的效果。
///
/// 判断逻辑（简化但足够实用的启发式，不追求精确测出物理链路总带宽——那需要用户手动跑一次
/// 测速才知道，做不到全自动）：
/// - 采样窗口内，网卡总发送+接收字节数 减去 本程序 HttpClient 已消耗的字节数，得到"其他流量"。
/// - "其他流量"明显偏高（超过一个较低的阈值，默认 200KB/s）时，认为用户正在做别的网络相关的事
///   （看视频/语音通话/别的下载工具等），把下载速率压低到一个保守值（默认 512KB/s），
///   给其他程序让出带宽。
/// - "其他流量"很低时，认为链路基本空闲，解除限制（BytesPerSecond 设为 0，即不限速，
///   跑满 <see cref="DownloadRateLimiter"/> 桶允许的最大速度）。
/// - 中间地带（有一些但不算多的其他流量）按比例给一个居中的限速值，避免在阈值边界反复跳变。
///
/// 局限：System.Net.NetworkInformation 的网卡计数器统计的是"进程外部可观测到的系统级流量"，
/// 无法精确区分"其他流量"具体是哪个程序产生的，也无法得知物理链路的真实总带宽上限——
/// 这是一个操作系统层面的固有限制，不引入额外的第三方驱动/内核扩展是做不到更精确的。
/// 这里的实现足以覆盖"用户一边看视频一边下载游戏文件，希望下载让着点视频"这类常见场景。
/// </summary>
public sealed class SmartBandwidthMonitor : IDisposable
{
    private const long OtherTrafficLowThresholdBytesPerSec = 200 * 1024;   // 低于这个值：链路基本空闲，不限速
    private const long OtherTrafficHighThresholdBytesPerSec = 1024 * 1024; // 高于这个值：明显有其他大流量活动，压到保守值
    private const long ThrottledTargetBytesPerSec = 512 * 1024;            // 判定"应该让路"时的目标下载速率
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly DownloadRateLimiter _limiter;
    private readonly int? _manualCapKBps; // 用户手动设置的固定上限（0/null=无手动上限），智能限速计算出的目标不能超过它
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private long _selfBytesConsumedSinceLastSample;
    private readonly object _selfLock = new();

    public SmartBandwidthMonitor(DownloadRateLimiter limiter, int manualCapKBps)
    {
        _limiter = limiter;
        _manualCapKBps = manualCapKBps > 0 ? manualCapKBps : null;
        _loopTask = Task.Run(MonitorLoopAsync);
    }

    /// <summary>下载代码每读到一批字节后调用，用于统计"本程序自己消耗了多少流量"，
    /// 从系统总流量里扣掉这部分才能估算出"别人用了多少"。</summary>
    public void ReportSelfBytes(int bytes)
    {
        lock (_selfLock) { _selfBytesConsumedSinceLastSample += bytes; }
    }

    private async Task MonitorLoopAsync()
    {
        var nics = SafeGetNics();
        var lastTotal = SafeReadTotalBytes(nics);
        var lastTime = Stopwatch.GetTimestamp();

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SampleInterval, _cts.Token);
            }
            catch (OperationCanceledException) { break; }

            var now = Stopwatch.GetTimestamp();
            var elapsedSec = (now - lastTime) / (double)Stopwatch.Frequency;
            lastTime = now;
            if (elapsedSec <= 0) continue;

            var currentTotal = SafeReadTotalBytes(nics);
            var totalDelta = Math.Max(0, currentTotal - lastTotal);
            lastTotal = currentTotal;

            long selfDelta;
            lock (_selfLock)
            {
                selfDelta = _selfBytesConsumedSinceLastSample;
                _selfBytesConsumedSinceLastSample = 0;
            }

            var totalRate = (long)(totalDelta / elapsedSec);
            var selfRate = (long)(selfDelta / elapsedSec);
            var otherRate = Math.Max(0, totalRate - selfRate);

            long targetBytesPerSec;
            if (otherRate <= OtherTrafficLowThresholdBytesPerSec)
            {
                targetBytesPerSec = 0; // 不限速
            }
            else if (otherRate >= OtherTrafficHighThresholdBytesPerSec)
            {
                targetBytesPerSec = ThrottledTargetBytesPerSec;
            }
            else
            {
                // 线性插值：其他流量在低/高阈值之间时，下载速率也在"不限速的一个较高参考值"
                // 和 ThrottledTargetBytesPerSec 之间线性过渡，避免在阈值边界附近速率反复横跳。
                var span = OtherTrafficHighThresholdBytesPerSec - OtherTrafficLowThresholdBytesPerSec;
                var ratio = (otherRate - OtherTrafficLowThresholdBytesPerSec) / (double)span;
                const long highRefBytesPerSec = 4 * 1024 * 1024; // "基本空闲"时给的参考速率上限
                targetBytesPerSec = (long)(highRefBytesPerSec - ratio * (highRefBytesPerSec - ThrottledTargetBytesPerSec));
            }

            if (_manualCapKBps is { } capKBps)
            {
                var capBytes = capKBps * 1024L;
                if (targetBytesPerSec <= 0 || targetBytesPerSec > capBytes)
                    targetBytesPerSec = capBytes;
            }

            _limiter.BytesPerSecond = targetBytesPerSec;
        }
    }

    private static List<System.Net.NetworkInformation.NetworkInterface> SafeGetNics()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .ToList();
        }
        catch
        {
            // 极少数环境下(权限受限/驱动异常)枚举网卡可能抛异常；智能限速是锦上添花功能，
            // 拿不到网卡列表就直接放弃采样（后续 SafeReadTotalBytes 对空列表返回 0，
            // 差值算出来的 otherRate 也会是 0，等价于"不限速"，不影响正常下载）。
            return new();
        }
    }

    private static long SafeReadTotalBytes(List<System.Net.NetworkInformation.NetworkInterface> nics)
    {
        long total = 0;
        foreach (var nic in nics)
        {
            try
            {
                var stats = nic.GetIPv4Statistics();
                total += stats.BytesReceived + stats.BytesSent;
            }
            catch { /* 单个网卡读取失败不影响其他网卡的统计 */ }
        }
        return total;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(1)); } catch { /* 忽略退出时的等待异常 */ }
        _cts.Dispose();
    }
}
