using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「内存优化」：思路类似 PCL2 的自动内存分配——不是让用户自己拍脑袋填一个
/// -Xmx 数字，而是启动前根据"系统当前实际可用物理内存"动态算一个更合理的推荐值，
/// 避免两种常见翻车场景：
/// (1) 用户手动填的 -Xmx 远超过系统实际可用内存，启动时系统疯狂换页/近乎卡死；
/// (2) 用户随手填了个很保守的小数值（比如 1024MB），大内存机器上明明还有大把富余，
///     却让游戏在偏低的堆内存下运行，Mod 多的时候更容易 OOM。
///
/// 实现同样使用 GlobalMemoryStatusEx（跟 MemoryWatchdogService 复用同一个 Win32 API，
/// 但两者职责不同：Watchdog 是"运行时持续监控 + 预警"，这里是"启动前一次性计算推荐值"，
/// 不需要共享状态，各自独立调用即可）。
/// </summary>
public static class MemoryOptimizerService
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

    /// <summary>一次内存优化计算的结果，供 UI 展示"优化前 -> 优化后"的对比，以及供
    /// 启动流程直接取用推荐值。</summary>
    public record Recommendation(
        int RecommendedMinMemoryMb,
        int RecommendedMaxMemoryMb,
        ulong AvailPhysMb,
        ulong TotalPhysMb,
        string Explanation);

    /// <summary>
    /// 计算推荐的 -Xms/-Xmx（单位 MB）。
    /// 规则（对齐"够用又不至于把系统吃满"这个朴素目标，没有追求特别精密的模型）：
    /// - 最大堆 = 当前可用物理内存 - 预留量，但不超过总物理内存的 70%（给系统本身、
    ///   显卡驱动、后台程序留足够余量，避免"理论可用"和"实际能安全使用"之间的差距）；
    /// - 最大堆下限 1024MB（低于这个值 Minecraft 本身都可能起不来）；
    /// - 最小堆 = 最大堆的一半，但不低于 512MB、不高于 2048MB（沿用主流启动器"最小堆
    ///   没必要跟最大堆一样大，留出一点动态伸缩空间"的经验值，同时避免最小堆本身也大到
    ///   离谱）。
    /// </summary>
    public static Recommendation? Calculate(int reserveMb)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        try
        {
            if (!GlobalMemoryStatusEx(ref status)) return null;
        }
        catch
        {
            // 非 Windows / API 不可用等极端情况：静默返回 null，调用方应该保留用户原有的
            // 手动配置，不应该因为这个可选优化功能本身出错而影响正常启动流程。
            return null;
        }

        var availMb = status.ullAvailPhys / 1024 / 1024;
        var totalMb = status.ullTotalPhys / 1024 / 1024;

        var reserve = (ulong)Math.Max(256, reserveMb);
        var byAvail = availMb > reserve ? availMb - reserve : 0;
        var byTotalCap = (ulong)(totalMb * 0.7);

        var maxMb = (int)Math.Max(1024, Math.Min(byAvail, byTotalCap));
        var minMb = (int)Math.Clamp(maxMb / 2, 512, 2048);
        if (minMb > maxMb) minMb = maxMb; // 极端小内存机器兜底，避免 Min > Max 这种非法组合

        var explanation =
            $"系统总内存 {totalMb}MB，当前可用 {availMb}MB，预留 {reserve}MB 给系统/其它程序后，" +
            $"推荐最大内存 {maxMb}MB（不超过总内存的 70%），最小内存 {minMb}MB。";

        return new Recommendation(minMb, maxMb, availMb, totalMb, explanation);
    }
}
