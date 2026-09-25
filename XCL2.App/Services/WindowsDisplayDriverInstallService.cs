using System.Diagnostics;
using System.IO;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>使用 Windows Update 对当前设备匹配的 Display 类驱动执行搜索、下载、安装。</summary>
public static class WindowsDisplayDriverInstallService
{
    public static void StartWithConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            MessageBoxDialog.ShowWarning("该功能仅支持 Windows。请从显卡制造商官网下载对应驱动。", "显卡驱动");
            return;
        }
        if (!MessageBoxDialog.ShowConfirm(
            "将通过 Windows Update 搜索本机适用的‘显示适配器’驱动，下载并安装（可能需要管理员权限、网络连接和重启）。\n" +
            "不会安装其它类别的 Windows 更新，也不会覆盖系统 opengl32.dll。\n" +
            "若 Windows Update 没有适配的驱动，请使用 NVIDIA / AMD / Intel 显卡厂商的官方下载渠道。\n\n是否开始？",
            "一键尝试安装 OpenGL 显卡驱动")) return;
        try
        {
            var logDir = Path.Combine(App.DataDir, "logs");
            Directory.CreateDirectory(logDir);
            var log = Path.Combine(logDir, "opengl-driver-install.log");
            var script = Path.Combine(logDir, "install-display-driver.ps1");
            // Windows Update 已根据本机硬件 ID 和操作系统筛选可适用更新；这里只保留 Display 类型。
            var ps = """
$ErrorActionPreference = 'Stop'
$LogFile = '__LOG__'
function Write-Status($line) { Add-Content -LiteralPath $LogFile -Value ("$(Get-Date -Format o) " + $line) -Encoding UTF8 }
try {
    Write-Status '开始查询 Windows Update 显示适配器驱动'
    $session = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()
    $found = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'")
    $updates = New-Object -ComObject Microsoft.Update.UpdateColl
    for ($i = 0; $i -lt $found.Updates.Count; $i++) {
        $u = $found.Updates.Item($i)
        $display = $false
        try { $display = $u.DriverClass -eq 'Display' } catch { }
        if (-not $display) { continue }
        if ($u.EulaAccepted -eq $false) { $u.AcceptEula() }
        [void]$updates.Add($u)
        Write-Status ("已匹配：" + $u.Title)
    }
    if ($updates.Count -eq 0) { Write-Status '未找到可用的 Display 驱动；可通过厂商官网安装。'; exit 0 }
    $downloader = $session.CreateUpdateDownloader()
    $downloader.Updates = $updates
    $download = $downloader.Download()
    Write-Status ("下载结果：" + $download.ResultCode)
    if ($download.ResultCode -ne 2) { throw '显卡驱动下载未成功完成' }
    $installer = $session.CreateUpdateInstaller()
    $installer.Updates = $updates
    $result = $installer.Install()
    Write-Status ("安装结果：" + $result.ResultCode + "; 需要重启：" + $result.RebootRequired)
    if ($result.ResultCode -notin @(2,3)) { throw '显卡驱动安装未成功完成' }
} catch { Write-Status ("安装失败：" + $_.Exception.Message); exit 1 }
""".Replace("__LOG__", log.Replace("'", "''"));
            File.WriteAllText(script, ps, new System.Text.UTF8Encoding(true));
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            MessageBoxDialog.ShowInfo("已启动 Windows Update 显卡驱动安装任务。请等待操作完成，并在需要时重启。\n安装记录：" + log +
                "\n如果没有匹配的驱动，请访问显卡厂商官方驱动页面。", "驱动安装已启动");
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[OpenGL驱动安装] 启动失败：" + ex);
            MessageBoxDialog.ShowWarning("无法启动安装任务：" + ex.Message + "\n请尝试 Windows 设置 → Windows 更新 → 高级选项 → 可选更新，或到显卡厂商官网下载驱动。", "驱动安装失败");
        }
    }
}
