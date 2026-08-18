using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Windows.Management.Deployment;

namespace XCL2.App.Services;

/// <summary>
/// 基岩版（UWP/GDK）客户端的"真正安装"——不是解压完直接跑 exe，而是调用 Windows
/// 公开的 <see cref="PackageManager"/> WinRT API 把包注册进系统的应用包清单。
///
/// ===== 为什么必须有这一步（参考 BedrockBoot 的 EasyLauncher.cs 反推）=====
/// BedrockBoot 靠一个闭源 NuGet 包（BedrockLauncher.Core）做注册，具体实现拿不到，
/// 但从它暴露的接口能确认三件事，本类照这个思路用公开 API 实现：
///   1. LaunchOptions.RegisterProgress 是 Progress&lt;DeploymentProgress&gt;类型——
///      说明启动前必须先"注册"（Register），这正是 PackageManager.AddPackageAsync
///      内部走的流程，DeploymentProgress 就是这个 API 原生返回的进度类型。
///   2. UWP 构建类型启动前会检测"开发者模式"（DeveloperModeHelper），侧载未经 Store
///      签发的包必须开发者模式，否则注册会直接失败。
///   3. 启动前检测 Microsoft.GamingServices 是否已装，没装直接拦截提示。
///
/// ===== 重要限制（我没有 Windows 环境实测，务必自己跑一遍）=====
/// - 官方基岩版客户端包是有微软签名的，AddPackageAsync 默认能装"已签名但未通过
///   Store 分发"的包，不强制要求开发者模式——但游戏内某些校验、Xbox Live 关联的
///   功能仍然可能因为"不是通过 Store 安装"而受限，这属于基岩版本身的设计，不是
///   注册步骤能解决的。
/// - 如果拿到的安装包路径下没有合法的 AppxManifest.xml（比如下载源给的其实是残缺
///   压缩包），注册会直接抛异常，调用方要接住并给用户清楚的提示，不能吞掉。
/// - 同一个 PackageFamilyName（Microsoft.MinecraftUWP_8wekyb3d8bbwe）系统里同时只能
///   注册一份，装新版本前系统会自动处理"升级替换"（AddPackageAsync 对同 Family
///   不同版本默认就是升级语义），但如果这台机器同时也用官方 Store 装过基岩版，
///   两者会互相覆盖——这是 Windows 应用包模型本身的限制，不是这段代码的 bug。
/// </summary>
public static class BedrockPackageRegistrationHelper
{
    /// <summary>正式版 PackageFamilyName，跟 <see cref="BedrockLaunchService"/> 保持一致。</summary>
    public const string PackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";

    /// <summary>
    /// 检测开发者模式是否已开启（读注册表，跟 BedrockBoot 的 DeveloperModeHelper 同一个
    /// 判断依据：HKLM 下 AppModelUnlock 或组策略 Appx 项的 AllowDevelopmentWithoutDevLicense）。
    /// 侧载未走 Store 签发的包，部分系统/包签名组合下需要这个开关，提前检测能给用户
    /// 明确的修复指引，而不是让注册失败后只看到一串英文异常堆栈。
    /// </summary>
    public static bool IsDeveloperModeEnabled()
    {
        string script = @"
            $paths = @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock',
                'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'
            )
            $valueName = 'AllowDevelopmentWithoutDevLicense'
            foreach ($path in $paths) {
                try {
                    $value = Get-ItemProperty -Path $path -Name $valueName -ErrorAction Stop
                    if ($value.$valueName -eq 1) { Write-Output 'true'; return }
                } catch { continue }
            }
            Write-Output 'false'
        ";
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(script);
            var encoded = Convert.ToBase64String(bytes);
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return string.Equals(output, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打开"系统 - 开发者选项"设置页，引导用户手动开启。</summary>
    public static void OpenDeveloperModeSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:developers") { UseShellExecute = true });
    }

    /// <summary>
    /// 在解压出来的目录里找 AppxManifest.xml。注册需要这个文件——它是包的身份清单，
    /// 单纯有 Minecraft.Windows.exe 不代表这是一个能注册的合法包（比如手动裁剪过的
    /// 解压产物就没有它）。
    /// </summary>
    public static string? FindAppxManifest(string extractedDir)
    {
        if (!Directory.Exists(extractedDir)) return null;
        return Directory.GetFiles(extractedDir, "AppxManifest.xml", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    /// <summary>当前系统是否已经注册了基岩版正式版这个包（不区分注册来源，Store 装的
    /// 和本方法注册的都算）。</summary>
    public static bool IsRegistered()
    {
        try
        {
            var pm = new PackageManager();
            return pm.FindPackagesForUser(string.Empty, PackageFamilyName).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把解压好的包目录注册进系统（PackageManager.AddPackageAsync）。
    /// 注册成功后应该用 <see cref="BedrockLaunchService.Launch"/>（shell:AppsFolder）
    /// 去启动，而不是直接 Process.Start 里面的 exe——注册过的包需要通过系统的应用
    /// 激活路径启动，才能拿到完整的包身份（存档路径、Xbox Live 关联等都依赖这个）。
    /// </summary>
    /// <param name="extractedDir">DownloadClientAsync 返回的解压目录。</param>
    /// <param name="progress">注册进度回调（百分比 0-100 + 当前阶段文字）。</param>
    public static async Task RegisterAsync(string extractedDir, IProgress<(int Percent, string State)>? progress = null)
    {
        var manifestPath = FindAppxManifest(extractedDir);
        if (manifestPath == null)
            throw new InvalidOperationException(
                $"在 {extractedDir} 下没有找到 AppxManifest.xml，这个解压产物不是一个合法的应用包，" +
                "无法注册。可能是下载源给的文件不完整，建议删除后重新下载。");

        var manifestUri = new Uri(manifestPath);
        var pm = new PackageManager();

        var tcs = new TaskCompletionSource();
        var deployment = pm.RegisterPackageAsync(manifestUri, null,
            DeploymentOptions.DevelopmentMode);

        deployment.Progress = (_, p) =>
        {
            progress?.Report(((int)p.percentage, p.state.ToString()));
        };
        deployment.Completed = (result, status) =>
        {
            if (status == Windows.Foundation.AsyncStatus.Error)
                tcs.TrySetException(new InvalidOperationException(
                    $"注册基岩版客户端包失败：{result.GetResults().ErrorText}"));
            else if (status == Windows.Foundation.AsyncStatus.Canceled)
                tcs.TrySetCanceled();
            else
                tcs.TrySetResult();
        };

        await tcs.Task;
    }

    /// <summary>
    /// 一站式：检测开发者模式 → 未注册则注册 → 通过系统应用激活路径启动。
    /// 调用方（BedrockPage/BedrockClientDownloadService）应该用这个方法替代
    /// "直接 Process.Start 解压出来的 exe"这条旧路径。
    /// </summary>
    public static async Task RegisterAndLaunchAsync(string extractedDir,
        IProgress<(int Percent, string State)>? progress = null)
    {
        if (!IsDeveloperModeEnabled())
            throw new InvalidOperationException(
                "系统未开启开发者模式，侧载（非 Microsoft Store 安装）方式注册应用包可能会被拒绝。\n" +
                "请前往「设置 → 系统 → 开发者选项」开启「开发人员模式」后重试。");

        if (!IsRegistered())
        {
            progress?.Report((0, "正在注册应用包"));
            await RegisterAsync(extractedDir, progress);
        }

        progress?.Report((100, "启动游戏"));
        BedrockLaunchService.Launch();
    }
}
