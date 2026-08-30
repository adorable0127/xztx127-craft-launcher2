using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>
/// 系统内存溢出预警服务：定时检测"整机可用物理内存"是否过低，一旦跌破阈值就立即
/// 通过事件通知 UI 弹出警告窗口，让用户在系统真正耗尽内存、触发卡死/蓝屏之前
/// 主动关闭占用内存的游戏进程。
///
/// 为什么监控的是"整机可用物理内存"而不是"Java 堆内存"：
/// Java 的 -Xmx 堆内存溢出(OutOfMemoryError) 只会让游戏本身崩溃退出，不会导致
/// 操作系统蓝屏——蓝屏通常发生在物理内存 + 虚拟内存(分页文件)被整机耗尽、内核
/// 或驱动在极度内存压力下的分配失败/超时的场景。真正需要监控、需要提前预警的是
/// "系统还剩多少可用内存"，而不是某一个 Java 进程自己的堆使用率。
///
/// 实现方式：Windows API GlobalMemoryStatusEx（比 PerformanceCounter 更轻量、
/// 不依赖性能计数器服务是否可用，某些精简系统/权限受限环境下 PerformanceCounter
/// 可能取不到值，GlobalMemoryStatusEx 是更底层、更可靠的方式）。
/// </summary>
public sealed class MemoryWatchdogService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>可用物理内存百分比低于这个值时触发预警（默认 10%）。</summary>
    public int LowMemoryThresholdPercent { get; set; } = 10;

    /// <summary>可用物理内存低于这个绝对值(MB)时也触发预警，避免大内存机器上百分比阈值太迟钝
    /// （比如 64GB 内存的机器，10% 也还有 6.4GB，其实早就该提醒了）。两个条件任一满足即触发。</summary>
    public int LowMemoryThresholdMb { get; set; } = 1024;

    /// <summary>两次预警弹窗之间的最短间隔，避免用户选择"忽略"之后每次轮询都再弹一次、把屏幕刷满。</summary>
    public TimeSpan WarningCooldown { get; set; } = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _timer;
    private DateTime _lastWarningAtUtc = DateTime.MinValue;
    private bool _suppressedUntilRecovered;

    /// <summary>
    /// 触发预警时携带的信息：当前可用内存、总内存、占用百分比，供 UI 弹窗展示，
    /// 帮助用户判断"确实是内存快满了"而不是无端弹窗。
    /// </summary>
    public sealed record LowMemoryEventArgs(ulong AvailPhysMb, ulong TotalPhysMb, uint MemoryLoadPercent);

    /// <summary>检测到可用内存过低时触发，订阅方负责弹出警告窗口 / 决定是否关闭游戏进程。</summary>
    public event Action<LowMemoryEventArgs>? LowMemoryDetected;

    public MemoryWatchdogService(TimeSpan? pollInterval = null)
    {
        _timer = new DispatcherTimer
        {
            Interval = pollInterval ?? TimeSpan.FromSeconds(5)
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>
    /// 用户在预警窗口里点了"忽略，本次不再提醒"时调用：在内存回升到阈值以上之前
    /// 不再重复弹窗，避免用户已经决定"先不管"了还反复打扰；一旦回升到安全水位后
    /// 自动重新武装，下次再跌下去照常预警，不会永久失效。
    /// </summary>
    public void SuppressUntilRecovered() => _suppressedUntilRecovered = true;

    private void Poll()
    {
        if (!TryGetMemoryStatus(out var status)) return;

        var availMb = status.ullAvailPhys / 1024 / 1024;
        var totalMb = status.ullTotalPhys / 1024 / 1024;
        var isLow = status.dwMemoryLoad >= (100 - LowMemoryThresholdPercent) || availMb <= (ulong)LowMemoryThresholdMb;

        if (!isLow)
        {
            // 内存已经回升到安全水位，解除"本次不再提醒"的抑制状态，恢复正常监控。
            _suppressedUntilRecovered = false;
            return;
        }

        if (_suppressedUntilRecovered) return;
        if (DateTime.UtcNow - _lastWarningAtUtc < WarningCooldown) return;

        _lastWarningAtUtc = DateTime.UtcNow;
        LowMemoryDetected?.Invoke(new LowMemoryEventArgs(availMb, totalMb, status.dwMemoryLoad));
    }

    private static bool TryGetMemoryStatus(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX();
        status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        try
        {
            return GlobalMemoryStatusEx(ref status);
        }
        catch
        {
            // 极少数环境下(非 Windows / API 缺失)会抛异常，静默失败即可，
            // 不应该因为监控功能本身出错而影响启动器正常使用。
            return false;
        }
    }

    public void Dispose() => _timer.Stop();
}
