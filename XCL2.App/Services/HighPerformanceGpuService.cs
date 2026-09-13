using System.Diagnostics;
using Microsoft.Win32;

namespace XCL2.App.Services;

/// <summary>
/// 为 Minecraft 的 javaw.exe 设置 Windows 图形性能首选项。
/// Windows 10/11 的“设置 → 系统 → 显示 → 图形”同样使用
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences：GpuPreference=2 表示高性能，
/// GpuPreference=0 表示不强制，由 Windows 决定。这里按“每次启动”覆盖到当前最终选中的
/// Java 可执行文件，因此不同实例即使选择不同策略，也会在真正启动自己之前写入自己的偏好。
/// </summary>
public static class HighPerformanceGpuService
{
    private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string NvidiaHighPerformanceShim = "0x800000001";

    /// <summary>
    /// 在 Process.Start 前应用偏好。失败仅记日志，不阻断游戏。
    /// </summary>
    public static void ApplyForLaunch(ProcessStartInfo psi, string executablePath, bool useHighPerformanceGpu)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath)) return;

        try
        {
            var fullPath = System.IO.Path.GetFullPath(executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(UserGpuPreferencesKey, writable: true);
            if (key != null)
            {
                var existing = key.GetValue(fullPath) as string;
                var preference = useHighPerformanceGpu ? 2 : 0;
                key.SetValue(fullPath, MergeGpuPreference(existing, preference), RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"[高性能独显] 写入 Windows GPU 首选项失败：{ex}");
        }

        try
        {
            // 这是按子进程注入的兼容提示，不污染启动器自身环境。开启时帮助部分
            // NVIDIA Optimus 驱动优先选择高性能 GPU；关闭时显式移除可能继承到的旧值。
            if (useHighPerformanceGpu)
                psi.Environment["SHIM_MCCOMPAT"] = NvidiaHighPerformanceShim;
            else
                psi.Environment.Remove("SHIM_MCCOMPAT");
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"[高性能独显] 设置子进程 GPU 环境提示失败：{ex}");
        }
    }

    private static string MergeGpuPreference(string? existing, int preference)
    {
        var parts = (existing ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        parts.Add($"GpuPreference={preference}");
        return string.Join(';', parts) + ";";
    }
}
