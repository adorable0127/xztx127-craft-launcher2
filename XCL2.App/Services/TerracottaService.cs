using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace XCL2.App.Services;

/// <summary>
/// 陶瓦联机(Terracotta | https://github.com/burningtnt/Terracotta) 集成服务。
///
/// 重要说明——这里做的是什么、不是什么：
/// 陶瓦联机本体是一个独立的 Rust 程序(基于 EasyTier 的 P2P 组网工具，针对 Minecraft 做了
/// 大量优化)，HMCL/PCL-CE/FMCL 等启动器是"打包/拉起这个独立程序"来提供联机入口，
/// 而不是在自己的代码里重新实现了一遍 EasyTier 的 P2P 打洞协议——那是一整套独立的网络工程，
/// 没有公开、稳定的"进程间通信协议"文档可以照抄(HMCL 的联机模块本身也不是开源的，见
/// zhaose233/HMCL-Clean 仓库描述:"remove ... multiplayer(not FOSS)")。
///
/// 集成方式(v2 起改为内置)：
/// 陶瓦联机的 Windows x64 可执行文件(0.4.2)已经作为 EmbeddedResource 编译进本程序集
/// (见 .csproj)，不再需要用户自己去 GitHub 下载、也不需要手动选择文件路径——
/// 首次点"启动陶瓦联机"时，EnsureExtracted 会把内嵌的字节流写到本地数据目录下
/// 固定的一个文件，之后每次直接复用这份已释放的文件(用文件大小 + 校验做一次轻量比对，
/// 避免每次启动都重新写一遍磁盘)。
/// 建房/加入房间/输入房间码这些交互，仍然全部由陶瓦联机自己的窗口完成，
/// 本启动器只负责"确保文件在本地 + 拉起进程"这一层，不假装重新实现了联机协议本身。
///
/// AppConfig.TerracottaExecutablePath 保留作为可选的手动覆盖——留空(默认)时总是使用内置版本，
/// 只有用户主动通过"选择可执行文件..."指定了别的路径时才会改用那个路径（比如以后陶瓦联机出了
/// 新版本、内置版本还没来得及更新，用户可以自己下载新版本临时替换）。
/// </summary>
public class TerracottaService
{
    public const string ReleasesUrl = "https://github.com/burningtnt/Terracotta/releases";
    public const string ProjectHomeUrl = "https://github.com/burningtnt/Terracotta";

    /// <summary>内嵌资源的逻辑名，需要跟 .csproj 里 EmbeddedResource 的 LogicalName 完全一致。</summary>
    private const string EmbeddedResourceName = "XCL2.App.Resources.Terracotta.terracotta-0.4.2-windows-x86_64.exe";

    /// <summary>内置版本释放到本地后的固定文件名，带版本号方便以后升级内置版本时
    /// 新旧文件不会互相覆盖冲突、也方便一眼看出当前用的是哪个版本。</summary>
    private const string ExtractedFileName = "terracotta-0.4.2-windows-x86_64.exe";

    /// <summary>
    /// 确保内置的陶瓦联机可执行文件已经释放到本地磁盘，返回可直接运行的完整路径。
    /// 每次调用都会做一次轻量校验(文件是否存在 + 大小是否一致)，只有文件缺失或大小不对
    /// (比如被用户手动删掉、或者内置版本升级后旧文件残留)时才会重新写一次，
    /// 避免每次点"启动陶瓦联机"都重复做一次磁盘 IO。
    /// </summary>
    private string EnsureExtracted()
    {
        var dir = Path.Combine(App.DataDir, "terracotta");
        Directory.CreateDirectory(dir);
        var targetPath = Path.Combine(dir, ExtractedFileName);

        var assembly = Assembly.GetExecutingAssembly();
        using var resourceStream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"内置的陶瓦联机可执行文件资源缺失(找不到嵌入资源 {EmbeddedResourceName})，安装包可能不完整。");

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == resourceStream.Length)
            return targetPath;

        // 先写到临时文件再原子性地移动过去，避免"写到一半时用户正好又点了一次启动"
        // 或者程序被强制结束导致目标路径留下一个不完整、无法运行的半成品文件。
        var tempPath = targetPath + ".tmp";
        using (var fileStream = File.Create(tempPath))
        {
            resourceStream.CopyTo(fileStream);
        }
        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>
    /// 解析当前应该使用的陶瓦联机可执行文件路径：
    /// 1. 如果用户手动指定过覆盖路径(AppConfig.TerracottaExecutablePath)且文件仍然存在，用那个；
    /// 2. 否则(默认情况)使用内置版本，首次调用时自动释放到本地。
    /// 这个方法保证总有一个可用路径返回，不会再出现"未检测到"的情况——内置版本恒定存在。
    /// </summary>
    public string ResolveExecutable(string? configuredOverridePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredOverridePath) && File.Exists(configuredOverridePath))
            return configuredOverridePath;

        return EnsureExtracted();
    }

    /// <summary>拉起陶瓦联机的窗口。内部会先确保文件已就绪(内置版本首次使用时自动释放)。</summary>
    public void Launch(string? configuredOverridePath)
    {
        var executablePath = ResolveExecutable(configuredOverridePath);
        Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        });
    }

    /// <summary>打开系统默认浏览器访问陶瓦联机的 GitHub Releases 页面。
    /// 默认流程已经不需要用户下载，目前联机页 UI 上没有暴露调用这个方法的按钮
    /// (内置版本已覆盖绝大多数场景)；有意保留这个方法 + 上面两个 URL 常量，方便以后
    /// 需要加"查看官网"/"检查是否有新版本"入口时直接复用，不算废弃代码。</summary>
    public void OpenReleasesPage()
    {
        Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
    }
}
