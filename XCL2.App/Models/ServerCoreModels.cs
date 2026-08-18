namespace XCL2.App.Models;

/// <summary>
/// 服务端核心类型。
/// Vanilla/Paper/Fabric/Purpur/Folia/Velocity/Waterfall 是直接下载即用的服务端(或代理)本体 jar；
/// Forge/NeoForge 下载到的是安装器 jar，需要本地再跑一次 --installServer 才能生成真正的服务端本体
/// （见 ServerCoreDownloadService 里的说明）。
/// Spigot 官方不直接分发预编译 jar，要靠 BuildTools 在本地拉源码编译（需要 Git + JDK + 网络，
/// 耗时数分钟），技术路线和"直接下载 jar"完全不同，单独用 RunSpigotBuildToolsAsync 处理，
/// DownloadAsync 对 Spigot 只负责下载 BuildTools.jar 本体，不负责触发编译（编译由开服向导那一步
/// 显式调用，方便展示"正在编译，请稍候"这种和别的核心类型不一样的中间态）。
/// Purpur：api.purpurmc.org/v2/purpur，是 Paper 的下游 fork，直接分发预编译 jar，用法和 Paper
/// 那套"查询构建列表 + 下载"模式几乎一致，只是 API 域名和字段名不同。
/// Folia/Velocity/Waterfall：都是 PaperMC 官方项目，复用 Paper 同一个 fill.papermc.io/v3 API，
/// 只是 project key 分别是 folia/velocity/waterfall，下载字段的 key 依然是 "server:default"。
///
/// Quilt：这个枚举原本主要给"服务端核心"用，Quilt 官方没有独立的服务端核心分发(Quilt 服务端
/// 就是"原版服务端 jar + Quilt Loader 安装器"，跟 Fabric 服务端的搭建方式是同一套思路)，
/// 所以 Quilt 这一项目前只在"客户端加载器安装"(ClientLoaderInstallService/
/// InstallClientLoaderWindow/LoaderChoiceWindow)路径上使用，ServerCoreDownloadService
/// 里的 switch 分支不需要处理 Quilt，未涉及的分支保持原样即可。
///
/// Bukkit/BungeeCord 未纳入：Bukkit 官方(dev.bukkit.org)早已停止分发可直接运行的服务端本体，
/// 现在事实上的继承者就是这里已经支持的 Spigot(经 BuildTools 编译)/Paper 系；BungeeCord 官方
/// 已被 PaperMC 标记为 legacy/接近停止维护，继任者是这里已支持的 Velocity，两者都建议引导用户
/// 改用继任产品而不是再单独实现一遍逐渐被放弃的旧下载源。
/// </summary>
public enum ServerCoreType
{
    Vanilla,
    Paper,
    Fabric,
    Forge,
    NeoForge,
    Quilt,
    Purpur,
    Folia,
    Velocity,
    Waterfall,
    Spigot,

    // ===== 非主流五大加载器之外的客户端加载器 =====
    // 老版本 MC（尤其是 1.5.2 及更早）没有 Fabric/Forge/NeoForge/Quilt 中的大部分选项，
    // 但有 LiteLoader 这类当年就存在的加载器；OptiFine 严格说不是"加载器"而是客户端优化 mod，
    // 但安装形态（独立 installer jar，产出一个可选的 versions/ 目录）跟加载器完全一致，
    // 沿用同一套 LoaderChoiceDialog/ClientLoaderInstallService 基础设施最省事。
    // Cleanroom 是给 1.12.2 生态的 Forge-fork（用 TRIP 后端跑在新版 Java 上），
    // LabyMod 4 同样是"独立安装、产出自己的 versions/ 目录"的客户端增强层。
    OptiFine,
    LiteLoader,
    Cleanroom,
    LabyMod,
    /// <summary>Legacy Fabric：Fabric 生态里专门覆盖 1.13 之前老版本的独立分支项目，
    /// Meta API 跟官方 Fabric 是同一套接口形状（meta.legacyfabric.net 对 meta.fabricmc.net），
    /// 客户端安装产出的 profile json 结构也跟 Fabric 完全一致，只是数据源、Maven 仓库地址不同。</summary>
    LegacyFabric
}

/// <summary>某个 Minecraft 版本下，某个核心类型可选的"构建/加载器版本"列表里的一项。</summary>
public class ServerCoreBuild
{
    /// <summary>展示给用户看的版本号文本，例如 Paper 的 build 号"451"，或 Fabric loader 版本"0.16.9"。</summary>
    public string DisplayVersion { get; set; } = "";

    /// <summary>是否官方标记为推荐/稳定构建（Paper 的 channel=default，Fabric 的 stable=true 等）。</summary>
    public bool IsRecommended { get; set; }

    /// <summary>部分加载器的"展示文本"和"实际调用安装接口所需的原始标识"不是同一个字符串
    /// （例如 OptiFine：DisplayVersion 是给用户看的 "HD_U_I6"，但下载接口要求分开传 type="HD_U"、
    /// patch="I6"）。这类场景把安装所需的原始标识按 "type|patch" 存在这里，调用方按 '|' 拆开用；
    /// 不需要这个的加载器（Fabric/Forge/...）保持 null，行为不变。</summary>
    public string? RawIdentifier { get; set; }
}

/// <summary>下载一个服务端核心所需的完整上下文，UI 层收集后传给 ServerCoreDownloadService。</summary>
public class ServerCoreDownloadRequest
{
    public ServerCoreType CoreType { get; set; }
    public string McVersion { get; set; } = "";

    /// <summary>Paper/Purpur/Folia/Velocity/Waterfall 的 build 号 / Fabric 的 loader 版本号；
    /// Vanilla/Forge/NeoForge/Spigot 不需要，留空即可。</summary>
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

    /// <summary>true = DownloadedFilePath 是 BuildTools.jar 本体，还需要调用
    /// ServerCoreDownloadService.RunSpigotBuildToolsAsync 在本地编译才能得到真正的服务端 jar
    /// （目前只有 Spigot 会是 true）。跟 RequiresInstall 分开一个字段，因为编译耗时数分钟、
    /// 需要 Git，UI 层要展示的中间态和"跑 Forge 安装器"完全不一样，不应该合并成同一个布尔值
    /// 让调用方猜"这里 RequiresInstall=true 到底是要跑安装器还是要编译"。</summary>
    public bool RequiresBuild { get; set; }

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
/// 没有公开 javaVersion 字段可查的核心类型。分界表（按用户最新确认的规则）：
///   1.16 及以下         -> Java 8
///   1.16 以上 ~ 1.20     -> Java 17（不含 1.20.1，即 1.20、1.20.0 仍是 17）
///   1.20.1 起           -> Java 21
///   1.26.1 及以上        -> Java 25（覆盖上面 21 那一档里 26.1 及以后的部分）
///
/// 修复说明（第三次修正）：这次把分界线整体按用户重新给出的表格改写，不再是"minor>=18
/// 用 17、minor==20 且 patch>=5 才跳到 21"这种旧算法——新规则里 1.20.1 就是 21 的起点
/// （不是 1.20.5），且 1.16 本身归在"以下"这一档用 8，"以上"从 1.17 开始才是 17。
/// 26.1+ 这条分支必须放在判断链最前面，否则会被"minor>=21 用 21"提前截胡，match 不到 25。
///
/// 跟"某个 mod 自己声明需要更高 Java"是两回事——后者(mod 声明)只在客户端
/// LauncherService.GetRequiredJavaMajorVersion 里处理，服务端这边完全没有 mod 依赖这个
/// 概念，靠的就是这张版本估算表。
/// </summary>
public static class ServerJavaRequirement
{
    public static int EstimateMajorVersionForMcVersion(string mcVersion)
    {
        var parsed = ParseVersionParts(mcVersion);
        if (parsed == null) return 21;

        var (major, minor, patch) = parsed.Value;

        // 年份制版本号（26.x 及以后，Minecraft 从 26 起换了命名方案）。
        // 旧代码这里一律 return 21，等于把所有新版本都当成"要 Java 21"——
        // 而 1.26.1+ 在旧命名下本来就要求 Java 25，年份制的 26.x 是同一批版本的新写法，
        // 要求不会比它更低。按 25 处理，跟下面 `minor >= 26 && patch >= 1` 那条规则保持一致。
        if (major >= 26) return 25;
        if (major != 1) return 21;

        // 1.26.1 及以上（含未来 1.27+）固定要求 Java 25，必须最先判断，
        // 否则会被下面 "minor >= 21 -> 21" 这条分支提前拦截。
        if (minor > 26) return 25;
        if (minor == 26 && patch >= 1) return 25;

        // 1.20.1 起（含 1.20.1、1.20.2...、1.21+ 到 1.26.0）要求 Java 21。
        if (minor > 20) return 21;
        if (minor == 20 && patch >= 1) return 21;

        // 1.16 以上（即 1.17 起）到 1.20（不含 1.20.1）要求 Java 17。
        if (minor >= 17) return 17;

        // 1.16 及以下要求 Java 8。
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
