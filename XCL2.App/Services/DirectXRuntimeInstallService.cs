using System.Diagnostics;
using System.IO;
using System.Net.Http;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 一键补全 DirectX 运行库：下载微软官方的 DirectX End-User Runtime 在线安装包（dxwebsetup.exe）并静默安装。
/// 它会补上 d3dx9_xx / d3dcompiler_xx / xinput1_x 等旧版 DirectX 组件 DLL，
/// 游戏或原生库提示"找不到 d3dx9_43.dll / d3dcompiler_47.dll / xinput1_3.dll"、
/// 退出码 0xC0000135（缺少 DLL）时用得上。注意这不是显卡驱动；显卡驱动请用「安装显卡驱动」。
/// </summary>
public static class DirectXRuntimeInstallService
{
    private const string WebSetupUrl =
        "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe";
    /// <summary>微软官方下载页，自动下载失败时让用户手动下载。</summary>
    public const string OfficialPage = "https://www.microsoft.com/download/details.aspx?id=35";

    private static bool _running;

    public static async void StartWithConfirmation()
    {
        if (!OperatingSystem.IsWindows())
        {
            MessageBoxDialog.ShowWarning("该功能仅支持 Windows。", "DirectX 运行库");
            return;
        }
        if (_running)
        {
            MessageBoxDialog.ShowInfo("DirectX 运行库正在安装中，请稍候。", "DirectX 运行库");
            return;
        }
        if (!MessageBoxDialog.ShowConfirm(
            "将从微软官方下载「DirectX 最终用户运行库」在线安装包并静默安装，补全缺失的 d3dx9 / d3dcompiler / xinput 等组件。\n" +
            "需要网络连接和管理员权限（会弹出系统 UAC 确认）；已经存在的组件不会被覆盖或降级。\n" +
            "这不是显卡驱动，不会更换你的显卡驱动。\n\n是否开始？",
            "补全 DirectX 运行库")) return;

        _running = true;
        var progress = new ProgressDialog("正在补全 DirectX 运行库...", indeterminate: true);
        progress.Show();
        var setupPath = Path.Combine(Path.GetTempPath(), $"dxwebsetup_{Guid.NewGuid():N}.exe");
        try
        {
            LauncherLogService.AppendLine("[DirectX] 开始下载 dxwebsetup.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 XCL2-Launcher/1.0");
                using var resp = await http.GetAsync(WebSetupUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(setupPath);
                await src.CopyToAsync(dst);
            }

            LauncherLogService.AppendLine("[DirectX] 开始静默安装");
            using var proc = Process.Start(new ProcessStartInfo(setupPath)
            {
                Arguments = "/Q",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (proc == null) throw new InvalidOperationException("无法启动安装程序。");
            await proc.WaitForExitAsync();
            progress.Close();

            LauncherLogService.AppendLine($"[DirectX] 安装程序退出，退出码 {proc.ExitCode}");
            if (proc.ExitCode == 0)
                MessageBoxDialog.ShowSuccess("DirectX 运行库已补全。请重新启动游戏试试；如果仍然报缺少 DLL，请再安装 Visual C++ 运行库。");
            else
                MessageBoxDialog.ShowWarning(
                    $"安装程序返回了退出码 {proc.ExitCode}，可能没有安装成功。\n" +
                    $"可以手动前往微软官方页面下载安装：\n{OfficialPage}\n\n（Windows 10/11 自带大部分 DirectX 组件，" +
                    "若系统更新不完整也可能导致安装失败，可先运行 Windows 更新。）", "DirectX 运行库");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            progress.Close();
            MessageBoxDialog.ShowInfo("已取消：需要管理员权限才能安装 DirectX 运行库。", "DirectX 运行库");
        }
        catch (Exception ex)
        {
            progress.Close();
            LauncherLogService.AppendLine("[DirectX] 安装失败：" + ex);
            MessageBoxDialog.ShowWarning(
                $"自动安装失败：{ex.Message}\n\n可以手动前往微软官方页面下载安装 DirectX 最终用户运行库：\n{OfficialPage}",
                "DirectX 运行库");
        }
        finally
        {
            _running = false;
            try { if (File.Exists(setupPath)) File.Delete(setupPath); } catch { /* 临时文件删不掉不影响 */ }
        }
    }
}
