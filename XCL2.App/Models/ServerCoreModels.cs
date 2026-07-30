namespace XCL2.App.Models;

/// <summary>
/// 服务端核心类型。这里只覆盖"下载方式已经调研确认、可以直接实现"的几种：
/// Vanilla/Paper/Fabric 是直接下载即用的服务端 jar；Forge/NeoForge 下载到的是安装器 jar，
/// 需要本地再跑一次 --installServer 才能生成真正的服务端本体（见 ServerCoreDownloadService 里的说明）。
/// Spigot/Purpur 暂不在这批实现范围内：Spigot 官方没有直接分发预编译 jar，传统上要靠 BuildTools
/// 本地编译（耗时且依赖用户机器上的 JDK/网络环境，技术路线和上面几种完全不同，风险点也不同，
/// 不应该和已经验证过的下载型核心混在同一批一起做，留到下一批单独实现）。
/// Purpur 有官方直发 jar（api.purpurmc.org），后续接入时可以复用 Vanilla/Paper 那套"直接下载 jar"的模式。
///
/// Quilt：这个枚举原本主要给"服务端核心"用，Quilt 官方没有独立的服务端核心分发(Quilt 服务端
/// 就是"原版服务端 jar + Quilt Loader 安装器"，跟 Fabric 服务端的搭建方式是同一套思路)，
/// 所以 Quilt 这一项目前只在"客户端加载器安装"(ClientLoaderInstallService/
/// InstallClientLoaderWindow/LoaderChoiceWindow)路径上使用，ServerCoreDownloadService
/// 里的 switch 分支不需要处理 Quilt，未涉及的分支保持原样即可。
/// </summary>
public enum ServerCoreType
{
    Vanilla,
    Paper,
    Fabric,
    Forge,
    NeoForge,
    Quilt
}

/// <summary>某个 Minecraft 版本下，某个核心类型可选的"构建/加载器版本"列表里的一项。</summary>
public class ServerCoreBuild
{
    /// <summary>展示给用户看的版本号文本，例如 Paper 的 build 号"451"，或 Fabric loader 版本"0.16.9"。</summary>
    public string DisplayVersion { get; set; } = "";

    /// <summary>是否官方标记为推荐/稳定构建（Paper 的 channel=default，Fabric 的 stable=true 等）。</summary>
    public bool IsRecommended { get; set; }
}

/// <summary>下载一个服务端核心所需的完整上下文，UI 层收集后传给 ServerCoreDownloadService。</summary>
public class ServerCoreDownloadRequest
{
    public ServerCoreType CoreType { get; set; }
    public string McVersion { get; set; } = "";

    /// <summary>Paper 的 build 号 / Fabric 的 loader 版本号；Vanilla/Forge/NeoForge 不需要，留空即可。</summary>
    public string? BuildOrLoaderVersion { get; set; }

    /// <summary>Forge/NeoForge 专用：安装器版本号（对应 Vanilla 版本可能有多个 loader 版本可选）。</summary>
    public string? InstallerVersion { get; set; }

    /// <summary>目标安装目录（对应清单里"安装位置可选"）。</summary>
    public string TargetDir { get; set; } = "";
}

/// <summary>
/// 下载完成后的结果：区分"已经是可直接启动的服务端 jar"和"还需要本地再跑安装器"两种情况，
/// 调用方（未来的开服向导）据此决定是直接进入下一步，还是先展示"正在安装服务端..."的中间态。
/// </summary>
public class ServerCoreDownloadResult
{
    public string DownloadedFilePath { get; set; } = "";

    /// <summary>true = DownloadedFilePath 就是安装器，还需要调用 ServerCoreDownloadService.RunForgeInstallerAsync。</summary>
    public bool RequiresInstall { get; set; }

    /// <summary>安装完成后实际用于启动服务端的 jar 文件名（Vanilla/Paper/Fabric 下载完就是这个；
    /// Forge/NeoForge 要等 RunForgeInstallerAsync 跑完后才能确定实际文件名，这里先留空）。</summary>
    public string? ServerJarFileName { get; set; }

    /// <summary>
    /// 这个服务端核心运行所需的 Java 主版本号。Vanilla 从 Mojang version.json 的
    /// javaVersion.majorVersion 字段读取（权威）；其余核心类型退化为按 MC 版本号区间估算
    /// （见 ServerJavaRequirement），比完全不管、硬用客户端全局 Java 版本要可靠得多——
    /// 后者正是 "UnsupportedClassVersionError: class file version 69.0 ... up to 65.0"
    /// 这类崩溃的根因。
    /// </summary>
    public int RequiredJavaMajorVersion { get; set; } = 21;
}

/// <summary>
/// MC 版本号到所需 Java 主版本号的估算规则，用于 Paper/Fabric/Forge/NeoForge 这些
/// 没有公开 javaVersion 字段可查的核心类型。规则来自 Mojang 官方公布的各版本 Java 要求：
/// 1.26.2+ 用 Java 25，1.21~1.26.1 用 Java 21，1.18~1.20.4 用 Java 17，1.17 用 Java 16，更早用 Java 8。
///
/// 修复说明：之前这里 `minor >= 21` 这一条分支把 1.21 及以后的所有版本（包括 26.2）
/// 都笼统地估算成 Java 21。但 26.2 官方要求的是 Java 25，这是 Mojang 对这个版本本身的
/// 硬性要求，跟"某个 mod 自己声明需要更高 Java"是两回事——后者(mod 声明)只在客户端
/// LauncherService.GetRequiredJavaMajorVersion 里处理，服务端这边完全没有 mod 依赖这个
/// 概念，靠的就是这张版本估算表，之前表里没跟上 26.2 官方要求，导致新建/安装 26.2 服务端
/// 核心时会选用 Java 21，跟客户端同一类问题：启动瞬间因为 Java 主版本不够被 JVM 直接拒绝
/// 运行（UnsupportedClassVersionError），表现为"一启动就退出"。
/// </summary>
public static class ServerJavaRequirement
{
    public static int EstimateMajorVersionForMcVersion(string mcVersion)
    {
        var parsed = ParseVersionParts(mcVersion);
        if (parsed == null) return 21;

        var (major, minor, patch) = parsed.Value;
        if (major != 1) return 21;

        if (minor >= 26) return patch >= 2 ? 25 : 21;
        if (minor >= 21) return 21;
        if (minor == 20) return patch >= 5 ? 21 : 17;
        if (minor >= 18) return 17;
        if (minor == 17) return 16;
        return 8;
    }

    private static (int major, int minor, int patch)? ParseVersionParts(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return null;
        var parts = mcVersion.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        var patch = 0;
        if (parts.Length >= 3) int.TryParse(parts[2], out patch);
        return (major, minor, patch);
    }
}
