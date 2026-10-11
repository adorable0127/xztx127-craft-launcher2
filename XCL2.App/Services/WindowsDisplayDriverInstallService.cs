using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>经确认后通过 Windows Update 搜索、下载和安装显示驱动，在统一下载列表跟踪真实结果。</summary>
public static class WindowsDisplayDriverInstallService
{
    private static int _running;

    public static void StartWithConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            MessageBoxDialog.ShowWarning("该功能仅支持 Windows。请从显卡制造商官网下载对应驱动。", "显卡驱动");
            return;
        }
        if (Volatile.Read(ref _running) != 0)
        {
            ToastService.ShowInfo("显卡驱动任务已在右上角下载列表中运行，请等待完成。");
            return;
        }
        if (!MessageBoxDialog.ShowConfirm(
            "将通过 Windows Update 搜索本机适用的‘显示适配器’驱动，下载并安装（可能需要管理员权限、网络连接和重启）。\n" +
            "不会安装其它类别的 Windows 更新，也不会覆盖系统 opengl32.dll。\n" +
            "操作进度会显示在右上角下载列表。为避免损坏安装，开始后不能在 XCL 中暂停或强行取消。\n" +
            "若 Windows Update 没有适配的驱动，请使用 NVIDIA / AMD / Intel 显卡厂商的官方下载渠道。\n\n是否开始？",
            "一键尝试安装 OpenGL 显卡驱动")) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            DownloadQueueService.Instance.StartNew("下载并安装显卡驱动（Windows Update）", async (item, _) =>
            {
                try { await RunInstallAsync(item); }
                finally { Interlocked.Exchange(ref _running, 0); }
            }, supportsPause: false, supportsRetry: false, supportsCancel: false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _running, 0);
            MessageBoxDialog.ShowWarning("无法登记驱动安装任务：" + ex.Message, "驱动安装失败");
        }
    }

    private static async Task RunInstallAsync(DownloadQueueItem item)
    {
        var logDir = Path.Combine(App.DataDir, "logs");
        Directory.CreateDirectory(logDir);
        var basename = "opengl-driver-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + item.Id.ToString("N");
        var log = Path.Combine(logDir, basename + ".log");
        var statusPath = Path.Combine(logDir, basename + ".status");
        var script = Path.Combine(logDir, basename + ".ps1");
        // 同步 COM 接口没有可靠的字节进度，因此只展示真实阶段。
        var ps = """
$ErrorActionPreference = 'Stop'
$LogFile = '__LOG__'
$StatusFile = '__STATUS__'
function Write-Status($line) {
    Add-Content -LiteralPath $LogFile -Value ("$(Get-Date -Format o) " + $line) -Encoding UTF8
    Set-Content -LiteralPath $StatusFile -Value $line -Encoding UTF8
}
try {
    Write-Status '正在查询 Windows Update 显示适配器驱动'
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
    if ($updates.Count -eq 0) { Write-Status '检查完成，未找到可用的显示驱动；请检查厂商官网。'; exit 10 }
    Write-Status ("正在下载 " + $updates.Count + " 项显示驱动（Windows Update）")
    $downloader = $session.CreateUpdateDownloader()
    $downloader.Updates = $updates
    $download = $downloader.Download()
    if ($download.ResultCode -ne 2) { throw ("显卡驱动下载未成功完成，结果码：" + $download.ResultCode) }
    Write-Status '驱动已下载，正在安装；请勿关闭计算机'
    $installer = $session.CreateUpdateInstaller()
    $installer.Updates = $updates
    $result = $installer.Install()
    if ($result.ResultCode -ne 2) { throw ("显卡驱动安装未全部成功，结果码：" + $result.ResultCode) }
    if ($result.RebootRequired) { Write-Status '驱动安装完成，需要重启计算机'; exit 3010 }
    Write-Status '驱动安装完成'
    exit 0
} catch { Write-Status ("安装失败：" + $_.Exception.Message); exit 1 }
""".Replace("__LOG__", log.Replace("'", "''"))
            .Replace("__STATUS__", statusPath.Replace("'", "''"));
        File.WriteAllText(script, ps, new System.Text.UTF8Encoding(true));
        var progress = item.CreateProgress();
        progress.Report(new ProgressInfo("等待管理员授权；授权后将搜索显示驱动", 0, 0, ""));
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Windows 未返回驱动安装进程。");
            var exit = process.WaitForExitAsync();
            while (!exit.IsCompleted)
            {
                ReportStage();
                await Task.WhenAny(exit, Task.Delay(400));
            }
            await exit;
            ReportStage();
            item.CompletionText = process.ExitCode switch
            {
                0 => "驱动安装完成",
                10 => "检查完成，未找到适配驱动；可前往厂商官网下载",
                3010 => "驱动安装完成，需要重启计算机",
                _ => throw new InvalidOperationException(ReadStage() ?? $"驱动安装失败（退出码 {process.ExitCode}），详情：{log}")
            };
            LauncherLogService.AppendLine("[OpenGL驱动安装] " + item.CompletionText + "；记录：" + log);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("未授予管理员权限，已取消驱动安装。", ex);
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[OpenGL驱动安装] 失败：" + ex + "；记录：" + log);
            throw;
        }
        void ReportStage()
        {
            var stage = ReadStage();
            if (stage != null) progress.Report(new ProgressInfo(stage, 0, 0, ""));
        }
        string? ReadStage()
        {
            try
            {
                var value = File.ReadAllText(statusPath).Trim();
                return value.Length == 0 ? null : value;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
