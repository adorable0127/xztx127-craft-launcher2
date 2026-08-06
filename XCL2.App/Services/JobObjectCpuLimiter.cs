using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// Windows Job Object 的最小封装，只用于一件事：把一个进程放进 Job 里，
/// 通过 JOBOBJECT_CPU_RATE_CONTROL_INFORMATION 设置 CPU 使用率硬上限。
/// 这是操作系统内核层面的限制（类似 Linux cgroups 的 cpu.max），
/// 不是"定时检测超标就杀进程"那种不精确的应用层模拟。
///
/// 只支持 CPU 限制：Job Object 也能设置内存上限(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)，
/// 但服务端内存已经通过 -Xmx 参数由 JVM 自己严格控制（JVM 堆不会超过 -Xmx），
/// 不需要再叠加一层 Job Object 内存限制；两者同时设置反而可能因为 JVM 除堆外还有
/// 元空间/直接内存等开销，被 Job Object 的限制先一步触发导致进程被杀，
/// 所以这里内存限制仍然完全交给 -Xmx/-Xms 处理，Job Object 只负责 CPU。
///
/// 失败处理：全部方法在失败时只记录/吞掉异常，不影响服务器进程本身的启动——
/// CPU 限制是"锦上添花"的资源管控功能，不应该因为这层限制设置失败就让整个开服流程失败。
/// </summary>
public static class JobObjectCpuLimiter
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private enum JobObjectInfoType
    {
        CpuRateControlInformation = 15
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRateOrWeight; // 当 ControlFlags 含 HARD_CAP 时，单位是万分之一(如 5000 = 50%)
    }

    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    /// <summary>
    /// 创建一个新 Job Object，把 process 放进去，并设置 CPU 使用率硬上限。
    /// 返回的 IntPtr 是 Job 句柄，调用方需要在进程结束后自行 CloseHandle(job)，
    /// 否则句柄泄漏（虽然进程退出/程序退出时系统最终也会回收，但显式关闭是更规范的做法）。
    /// 返回 IntPtr.Zero 表示设置失败（例如非 Windows 环境、权限不足等），
    /// 调用方应该把这种情况当作"限制没生效但进程仍正常运行"处理，不视为致命错误。
    /// </summary>
    public static IntPtr TryLimitCpu(System.Diagnostics.Process process, int cpuPercent)
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        if (cpuPercent is < 1 or > 100) return IntPtr.Zero;

        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var cpuInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRateOrWeight = (uint)(cpuPercent * 100) // 转换成万分之一单位
            };

            var size = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(cpuInfo, ptr, false);
                if (!SetInformationJobObject(job, JobObjectInfoType.CpuRateControlInformation, ptr, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }

            return job;
        }
        catch
        {
            // 任何一步失败都视为"限制设置失败"，不抛异常给调用方——见类注释里的失败处理原则
            if (job != IntPtr.Zero) { try { CloseHandle(job); } catch { /* 忽略 */ } }
            return IntPtr.Zero;
        }
    }

    public static void Release(IntPtr jobHandle)
    {
        if (jobHandle == IntPtr.Zero) return;
        try { CloseHandle(jobHandle); } catch { /* 忽略 */ }
    }
}
