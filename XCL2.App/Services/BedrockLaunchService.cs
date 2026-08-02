using System.Diagnostics;

namespace XCL2.App.Services;

/// <summary>
/// 基岩版（Bedrock Edition）"轻量跳转"支持：只做"检测系统是否已安装 Minecraft for Windows，
/// 已安装就唤起它"这一件事，不下载、不管理版本、不接管任何 mod/Add-On——这些跟基岩版的引擎
/// （C++ 原生 + UWP/GDK 打包）、分发方式（Microsoft Store）、内容生态（Add-Ons，跟 Java 版
/// Forge/Fabric/NeoForge mod 完全不是一套体系）都跟这个启动器现有的 Java 版整套逻辑
/// （JavaService/LauncherService/ModrinthService 等）不兼容，这个类刻意保持"只跳转"这么小的范围。
///
/// 唤起方式：Windows 提供 shell:AppsFolder\&lt;PackageFamilyName&gt;!&lt;AppId&gt; 这个通用的
/// "按已注册的应用清单唤起"协议，只要这个应用包已经在系统里注册（不管它内部实际是旧版 UWP
/// 打包还是新版 GDK 打包，这一层协议不关心），就能用同一种方式唤起，不需要知道它具体安装
/// 在哪个磁盘路径、也不需要碰任何 Store 私有 API 或做任何逆向——这是官方支持、任何第三方
/// 程序都可以合法调用的唤起方式（跟"开始菜单"点击这个应用图标是同一条路径）。
///
/// 包信息来源：Minecraft for Windows（正式版）固定使用 PackageFamilyName
/// "Microsoft.MinecraftUWP_8wekyb3d8bbwe"，这是 Mojang/Microsoft 从最早的 UWP 版本沿用至今
/// 的包标识，即使内部安装目录/打包格式后续改成了 GDK（游戏文件挪到了
/// "&lt;安装盘&gt;\XboxGames\Minecraft for Windows\"），这个 PackageFamilyName 本身在系统里
/// 注册应用清单时保持不变，唤起协议依然有效。Preview 版是另一个独立包
/// （Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe），本类默认只处理正式版，不猜测/不兼顾
/// Preview，避免装了 Preview 没装正式版的用户被"检测到已安装"误导。
/// </summary>
public static class BedrockLaunchService
{
    /// <summary>Minecraft for Windows（正式版）固定的 PackageFamilyName，Windows 应用商店包的
    /// 稳定标识符，不随版本号变化。</summary>
    public const string PackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";

    /// <summary>
    /// 检测这个包是否已经安装在当前系统里。用 PowerShell 的 Get-AppxPackage 按
    /// PackageFamilyName 查询——这是 Windows 自带、面向普通用户/脚本开放的标准查询方式，
    /// 不需要管理员权限，也不需要读取受保护的 WindowsApps/XboxGames 目录（那些目录哪怕有
    /// 管理员权限，默认权限设置下也读不到内容，直接用文件是否存在来判断会不可靠）。
    ///
    /// 查询失败（比如 PowerShell 不可用、被组策略禁用等极端情况）时返回 false，调用方应该
    /// 把"未检测到已安装"和"检测本身失败"同等对待——都是"点击后应该提示用户自己去 Store
    /// 安装"，不需要对用户区分这两种内部原因。
    /// </summary>
    public static async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"(Get-AppxPackage -Name '{PackageFamilyName.Split('_')[0]}').Count\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return int.TryParse(output.Trim(), out var count) && count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 唤起已安装的 Minecraft for Windows。调用方应该先调用 IsInstalledAsync 确认已安装，
    /// 这里不重复检测——跟项目里其他"前置条件由外层页面负责校验"的约定一致（比如
    /// ExperimentalFeaturesWindow 不重复校验 token 解锁状态）。
    ///
    /// 用 explorer.exe 启动 shell:AppsFolder\...!App 而不是直接 Process.Start 那个协议字符串，
    /// 是因为 shell: 协议在部分 Windows 版本/权限组合下用 UseShellExecute=true 直接启动会
    /// 抛 Win32Exception("找不到该文件")，而交给 explorer.exe 去解析这个虚拟文件夹路径
    /// 是官方文档推荐、兼容性最好的方式（跟命令行下 "explorer shell:AppsFolder\..." 手动能跑通
    /// 是同一条路径）。"!App" 是 Minecraft for Windows 清单里的默认 Application Id。
    /// </summary>
    public static void Launch()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{PackageFamilyName}!App")
        {
            UseShellExecute = true
        });
    }
}
