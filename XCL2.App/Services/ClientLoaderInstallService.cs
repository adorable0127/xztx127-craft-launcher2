using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 客户端"游戏加载器"(Fabric/Forge/NeoForge) 安装。
///
/// 修复"无法下载游戏加载器"：之前"下载中心"的「游戏版本」面板只对接了 Mojang 原版 version_manifest，
/// InstallVersion_Click -> DownloadService.InstallVersionAsync 从头到尾都只处理原版安装，
/// 完全没有任何入口能把 Fabric/Forge/NeoForge 装进 .minecraft/versions/ 下——用户在"版本选择"页
/// 只能看到原版版本，选不到、也没地方下载任何加载器版本，这是这个问题的根因。
/// 服务端那边的 ServerCoreDownloadService 只解决"服务端核心"下载，跟客户端加载器完全是两回事，
/// 不能直接复用（服务端核心不需要生成客户端能启动的 version json）。
///
/// 各加载器的实现方式：
/// - Fabric: meta.fabricmc.net/v2 直接提供"客户端可用的完整 version json"
///   （GET /v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json），不需要本地跑安装器，
///   json 里已经包含 inheritsFrom 指向原版、mainClass、libraries 等全部信息，
///   LauncherService 已经原生支持 inheritsFrom 继承链（见该类关于 inheritsFrom 的注释），拿到就能直接用。
///   这是三种加载器里最简单可靠的一种，优先完整实现。
/// - Forge/NeoForge: 官方只发布"安装器 jar"，没有直接可下载的客户端 version json，必须本地用 Java
///   跑一次 `java -jar xxx-installer.jar --installClient <.minecraft目录>`，安装器会自己下载所需库文件
///   并在 versions/ 下写入对应的 version json + libraries。这里的实现直接复用
///   ServerCoreDownloadService.RunForgeInstallerAsync 已经验证过的"起进程 + 读输出 + 判断退出码"模式，
///   只是把参数从 --installServer 换成 --installClient。
/// </summary>
public class ClientLoaderInstallService : IDisposable
{
    /// <summary>
    /// 跟 LauncherService 里同名方法逻辑一致（该文件改名容错查找）：优先按"文件夹名/版本 id"找精确
    /// 文件名，找不到就退化为"文件夹里唯一一个该后缀的文件"。做成独立实例时需要把原版 client.jar
    /// 拷贝进加载器自己的文件夹，这里要用同样的容错方式去定位原版 jar，避免用户手动改过原版
    /// 版本文件夹名字时找不到文件。
    /// </summary>
    private static string? ResolveVersionFile(string dir, string preferredBaseName, string extension)
    {
        var exact = Path.Combine(dir, $"{preferredBaseName}.{extension}");
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        var matches = Directory.GetFiles(dir, $"*.{extension}");
        return matches.Length == 1 ? matches[0] : null;
    }

    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    /// <summary>Quilt 官方 Meta API，接口形状(端点路径/返回字段)跟 Fabric Meta 几乎一一对应——
    /// Quilt 本来就是从 Fabric Loader fork 出来的，两边团队一直保持 Meta API 兼容，
    /// 这也是下面 Quilt 相关方法能直接照抄 Fabric 那几个方法、只换 base url 和产物文件名前缀的原因。</summary>
    private const string QuiltMetaBase = "https://meta.quiltmc.org/v3";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly DownloadService _vanillaDownloader;

    /// <summary>Fabric API 走 Modrinth 下载（Fabric API 本身就是发布在 Modrinth 上的普通 mod，
    /// 复用现成的 ModrinthService 搜索+下载逻辑，不用重新实现一遍 Modrinth API 调用。</summary>
    private readonly ModrinthService _modrinth = new();

    /// <summary>按完整 AppConfig 构造：加载器安装同样需要先装一个原版底座（父版本），
    /// 走的正是内部这份 _vanillaDownloader，理应享受跟"下载中心-游戏版本"面板一样的
    /// 多线程下载/限速配置，而不是永远单线程——否则用户开了多线程下载，唯独装 Fabric/Forge 时
    /// 感觉不到任何加速，体验不一致。</summary>
    public ClientLoaderInstallService(AppConfig cfg)
        : this(cfg.Source)
    {
        _vanillaDownloader.Dispose();
        _vanillaDownloader = DownloadService.CreateFromConfig(cfg);
    }

    /// <summary>沿用旧签名：只传 DownloadSource，内部退化为单线程不限速的 DownloadService
    /// （等价于以前的行为）。保留这个构造是为了不强迫所有调用方立刻改成传完整 AppConfig。</summary>
    public ClientLoaderInstallService(DownloadSource source)
    {
        _vanillaDownloader = new DownloadService(source);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2Launcher", "1.0"));
    }

    /// <summary>Fabric：客户端支持的 MC 版本列表（与服务端是同一份数据源）。</summary>
    public async Task<List<string>> GetFabricMcVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{FabricMetaBase}/versions/game", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && !stable.GetBoolean()) continue;
            result.Add(v.GetProperty("version").GetString()!);
        }
        return result;
    }

    /// <summary>Fabric：可用 loader 版本列表。</summary>
    public async Task<List<ServerCoreBuild>> GetFabricLoaderVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{FabricMetaBase}/versions/loader", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            result.Add(new ServerCoreBuild
            {
                DisplayVersion = v.GetProperty("version").GetString()!,
                IsRecommended = v.TryGetProperty("stable", out var stable) && stable.GetBoolean()
            });
        }
        return result;
    }

    /// <summary>Quilt：客户端支持的 MC 版本列表。跟 GetFabricMcVersionsAsync 是同一套过滤逻辑
    /// (只保留 stable=true 的正式版)，Quilt Meta 的 /versions/game 返回结构跟 Fabric Meta 一致。</summary>
    public async Task<List<string>> GetQuiltMcVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{QuiltMetaBase}/versions/game", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && !stable.GetBoolean()) continue;
            result.Add(v.GetProperty("version").GetString()!);
        }
        return result;
    }

    /// <summary>Quilt：可用 loader 版本列表。</summary>
    public async Task<List<ServerCoreBuild>> GetQuiltLoaderVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{QuiltMetaBase}/versions/loader", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            result.Add(new ServerCoreBuild
            {
                DisplayVersion = v.GetProperty("version").GetString()!,
                // Quilt Meta 的 loader 列表条目本身不像 Fabric 那样带 "stable" 字段，
                // 官方约定"不含 -beta/-rc 等预发布后缀的版本号"即视为稳定版，
                // 用字符串是否包含连字符来判断，跟 Quilt 官方文档/其它启动器(PCL2/HMCL)
                // 采用的判断口径一致。
                IsRecommended = !v.GetProperty("version").GetString()!.Contains('-')
            });
        }
        return result;
    }

    /// <summary>Forge：有安装器构建的 MC 版本列表（客户端和服务端安装器是同一个 jar，只是参数不同）。
    /// 实际逻辑已抽到 ForgeVersionQueryService（见该类注释：跟 ServerCoreDownloadService 消除重复代码）。</summary>
    public Task<List<string>> GetForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeVersionsAsync(_http, ct);

    public Task<List<ServerCoreBuild>> GetForgeInstallerVersionsAsync(string mcVersion, CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeInstallerVersionsAsync(_http, mcVersion, ct);

    /// <summary>
    /// NeoForge：可用完整版本号列表。逻辑已抽到 ForgeVersionQueryService（见该类注释里
    /// 关于 404 bug 根因的说明），这里只是保留原有的方法签名，方便调用方不用改。
    /// </summary>
    public Task<List<string>> GetNeoForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetNeoForgeVersionsAsync(_http, ct);

    /// <summary>Fabric API 在 Modrinth 上的项目 slug，固定值（官方项目，不会变）。</summary>
    private const string FabricApiModrinthSlug = "fabric-api";

    /// <summary>
    /// 安装 Fabric 客户端到 .minecraft/versions/{versionId}/。
    /// 步骤：1) 先确保原版父版本已安装（Fabric json 靠 inheritsFrom 引用它，父版本缺失会导致启动失败）；
    /// 2) 直接下载 Fabric Meta 提供的现成客户端 profile json；3) 按 json 里的 libraries 列表补下依赖库
    /// （复用 DownloadService 里已经验证过的"支持 Fabric/Quilt 风格 name+url 库条目"的下载逻辑，
    /// 不重新实现一遍，避免产生和已修复过的语言文件/native 库下载 bug 相同的问题）；
    /// 4) 可选：装好 Fabric Loader 之后再从 Modrinth 拉一份 Fabric API 放进 mods/（很多 Fabric 模组
    /// 都依赖它，是 Fabric 生态里事实上的"标准库"，新手不知道要单独装这个是很常见的安装失败原因）。
    /// </summary>
    /// <param name="installFabricApi">true 时额外从 Modrinth 下载安装 Fabric API（可选步骤，
    /// 失败不影响 Fabric Loader 本身的安装结果——见方法内部注释）。</param>
    public async Task<string> InstallFabricClientAsync(string minecraftDir, string mcVersion, string loaderVersion,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, bool installFabricApi = false)
    {
        // 1. 确保原版父版本已装好（Fabric 客户端启动需要原版 client.jar + assets）
        var parentVersionDir = Path.Combine(minecraftDir, "versions", mcVersion);
        if (!File.Exists(Path.Combine(parentVersionDir, $"{mcVersion}.jar")))
        {
            progress?.Report(new ProgressInfo("安装原版父版本", 0, 1, mcVersion));
            var manifest = await _vanillaDownloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                ?? throw new InvalidOperationException($"在版本清单中找不到 MC 版本 {mcVersion}，无法安装 Fabric 所需的原版父版本。");
            await _vanillaDownloader.InstallVersionAsync(minecraftDir, entry, progress, ct);
        }

        // 2. 拉取 installer 版本（取最新稳定版，用户不需要关心这个号）
        progress?.Report(new ProgressInfo("查询 Fabric installer 版本", 0, 1, loaderVersion));
        var installerJson = await _http.GetStringAsync($"{FabricMetaBase}/versions/installer", ct);
        using var installerDoc = JsonDocument.Parse(installerJson);
        string? installerVersion = null;
        foreach (var v in installerDoc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && stable.GetBoolean())
            {
                installerVersion = v.GetProperty("version").GetString();
                break;
            }
        }
        installerVersion ??= installerDoc.RootElement[0].GetProperty("version").GetString();

        // 3. 下载现成的客户端 profile json（含 inheritsFrom + libraries + mainClass，官方生成好的，不用自己拼）
        var profileUrl = $"{FabricMetaBase}/versions/loader/{mcVersion}/{loaderVersion}/{installerVersion}/profile/json";
        progress?.Report(new ProgressInfo("下载 Fabric 版本信息", 0, 1, "profile/json"));
        var profileJson = await _http.GetStringAsync(profileUrl, ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(profileJson)
            ?? throw new InvalidOperationException("Fabric 返回的版本信息解析失败。");

        // Fabric Meta 返回的 json 里 id 字段形如 "fabric-loader-0.15.11-1.20.1"，直接采用官方给的命名，
        // 与其它主流启动器保持一致，方便用户在多个启动器之间识别是同一个版本。
        var versionId = string.IsNullOrEmpty(detail.Id) ? $"fabric-loader-{loaderVersion}-{mcVersion}" : detail.Id;
        var versionDir = Path.Combine(minecraftDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        // 4. 把加载器实例做成"独立实例"：不再靠 inheritsFrom 指向单独共用的原版文件夹，而是把原版
        // client.jar 直接拷贝一份进这个加载器自己的版本文件夹，并去掉 profile json 里的 inheritsFrom
        // 字段。这样每个 Fabric 实例（哪怕对应同一个 MC 版本、不同的 mod 列表）都是完全独立的文件夹，
        // 跟纯净原版、以及其它加载器实例互不影响——删除/改名/单独导出一个 Fabric 实例，都不会波及
        // 原版文件夹或其它加载器实例，符合 PCL2/HMCL 里"每个版本都是独立实例"的直觉。
        // 之前 versionId 文件夹只落一份 json，靠 LauncherService 的 inheritsFrom 继承链去父版本
        // 文件夹里找 jar；现在把 jar 也落一份在本地文件夹，profile json 也顺手去掉 inheritsFrom，
        // 保证即使原版父版本文件夹以后被删掉，这个 Fabric 实例依然能独立启动。
        var parentJarPath = ResolveVersionFile(parentVersionDir, mcVersion, "jar");
        if (parentJarPath != null)
        {
            File.Copy(parentJarPath, Path.Combine(versionDir, $"{versionId}.jar"), overwrite: true);
        }

        // Fabric profile json 本身不带 assetIndex/assets/downloads 字段(它靠 inheritsFrom 指向原版
        // 去继承这些信息)。去掉 inheritsFrom 之后如果不补上，LauncherService 会因为找不到 assetsId
        // 而回退成 "legacy"，导致资源文件目录用错(新版本会找不到材质音效)。这里从原版自己的 json 里
        // 读一份出来，把这三个字段原样搬进 Fabric 自己的 json，保证独立后信息完整、不依赖父版本文件夹。
        var parentJsonPath = ResolveVersionFile(parentVersionDir, mcVersion, "json");
        if (parentJsonPath != null)
        {
            var parentDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
            if (parentDetail != null)
            {
                detail.AssetIndex ??= parentDetail.AssetIndex;
                detail.Assets ??= parentDetail.Assets;
                detail.Downloads ??= parentDetail.Downloads;
                detail.JavaVersion ??= parentDetail.JavaVersion;
            }
        }
        detail.InheritsFrom = null;
        detail.Id = versionId;
        var finalProfileJson = JsonSerializer.Serialize(detail,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
        await File.WriteAllTextAsync(versionJsonPath, finalProfileJson, ct);

        // 5. 补下 libraries：Fabric profile json 里的库全部是 "name+url" 风格（无 downloads 对象），
        // 复用 DownloadService 已经支持这种风格的下载逻辑，不重复实现一遍 Maven 坐标换算。
        progress?.Report(new ProgressInfo("下载 Fabric 加载器库文件", 0, Math.Max(detail.Libraries.Count, 1), versionId));
        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, detail, progress, ct);

        // 5. 可选：Fabric API（很多 Fabric mod 的硬依赖，新手常常不知道要单独装）。
        // 这一步失败不应该让整个 Fabric Loader 安装被判定为失败——loader 本身已经装好、
        // 可以正常启动游戏，Fabric API 只是"锦上添花"的常见依赖，装不上顶多是后续装某些 mod
        // 时提示缺依赖，用户还能再手动装一次，不应该因为这一步网络抖动就让用户以为 Fabric 都没装上。
        if (installFabricApi)
        {
            progress?.Report(new ProgressInfo("下载 Fabric API", 0, 1, FabricApiModrinthSlug));
            try
            {
                var versions = await _modrinth.GetVersionsAsync(FabricApiModrinthSlug, mcVersion, ct);
                // Fabric API 的 Modrinth 版本列表里同时有给 Fabric 用的和给 Quilt 用的构建，
                // 用 loaders 字段过滤，避免装到 Quilt 专用构建（能下载但 Fabric Loader 用不了）。
                var apiVersion = versions.FirstOrDefault(v =>
                    v.Loaders != null && v.Loaders.Any(l => l.Equals("fabric", StringComparison.OrdinalIgnoreCase)))
                    ?? versions.FirstOrDefault();

                if (apiVersion == null)
                {
                    progress?.Report(new ProgressInfo(
                        $"Fabric API 没有找到适配 MC {mcVersion} 的版本，已跳过（不影响 Fabric Loader 本身的安装）",
                        1, 1, FabricApiModrinthSlug));
                }
                else
                {
                    var apiProgress = new Progress<string>(msg =>
                        progress?.Report(new ProgressInfo("下载 Fabric API", 0, 1, msg)));
                    await _modrinth.DownloadResourceAsync(minecraftDir, ModrinthResourceType.Mod, apiVersion,
                        apiProgress, saveName: null, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 同上：Fabric API 下载失败不应该让整个安装抛异常，只在进度里提示一下，
                // 让用户知道"loader 装好了，但 Fabric API 这一步没成功"，而不是笼统地报"安装失败"。
                progress?.Report(new ProgressInfo(
                    $"Fabric API 下载失败（{ex.Message}），已跳过，不影响 Fabric Loader 本身的安装",
                    1, 1, FabricApiModrinthSlug));
            }
        }

        progress?.Report(new ProgressInfo("安装完成", 1, 1, versionId));
        return versionId;
    }

    /// <summary>QSL(Quilt Standard Libraries) 在 Modrinth 上的项目 slug，固定值（官方项目，不会变）。</summary>
    private const string QslModrinthSlug = "qsl";

    /// <summary>
    /// 安装 Quilt 客户端到 .minecraft/versions/{versionId}/。跟 InstallFabricClientAsync 的整体
    /// 步骤(装原版父版本 -> 下载官方现成 profile json -> 补库文件)基本一致，只有一处跟 Fabric 不同：
    /// Quilt Meta 的 profile json 端点是 "/versions/loader/{mc}/{loader}/profile/json"，
    /// 路径里不含"installer 版本"这一段——Quilt 的服务端把 installer 版本收敛成官方固定值，
    /// 不需要像 Fabric 那样先单独查一次 installer 列表再拼进 url。
    ///
    /// 可选步骤：QSL(Quilt Standard Libraries) 对应 Fabric API 在 Quilt 生态里的角色，同样发布在
    /// Modrinth 上(slug "qsl")。跟 Fabric API 一样做成可选自动安装项，交给用户在安装界面勾选，
    /// 不强制默认装——QSL 不是所有 Quilt mod 的通用硬依赖(存在纯用 Quilt 特性、完全不依赖 QSL 的
    /// mod)，但对绝大多数 Quilt 生态的模组来说都是常见依赖，跟 Fabric API 的定位一致，所以采用
    /// 同样"可选、默认不勾、失败不影响 Loader 本身安装结果"的处理方式。
    /// </summary>
    /// <param name="installQsl">true 时额外从 Modrinth 下载安装 QSL（可选步骤，失败不影响
    /// Quilt Loader 本身的安装结果——见方法内部注释）。</param>
    public async Task<string> InstallQuiltClientAsync(string minecraftDir, string mcVersion, string loaderVersion,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, bool installQsl = false)
    {
        // 1. 确保原版父版本已装好（Quilt 客户端启动同样需要原版 client.jar + assets）
        var parentVersionDir = Path.Combine(minecraftDir, "versions", mcVersion);
        if (!File.Exists(Path.Combine(parentVersionDir, $"{mcVersion}.jar")))
        {
            progress?.Report(new ProgressInfo("安装原版父版本", 0, 1, mcVersion));
            var manifest = await _vanillaDownloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                ?? throw new InvalidOperationException($"在版本清单中找不到 MC 版本 {mcVersion}，无法安装 Quilt 所需的原版父版本。");
            await _vanillaDownloader.InstallVersionAsync(minecraftDir, entry, progress, ct);
        }

        // 2. 下载现成的客户端 profile json（Quilt Meta 直接给完整 json，跟 Fabric 一样不需要
        // 本地跑安装器；路径里没有 installer 版本这一段，见方法上方注释）。
        var profileUrl = $"{QuiltMetaBase}/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
        progress?.Report(new ProgressInfo("下载 Quilt 版本信息", 0, 1, "profile/json"));
        var profileJson = await _http.GetStringAsync(profileUrl, ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(profileJson)
            ?? throw new InvalidOperationException("Quilt 返回的版本信息解析失败。");

        // Quilt Meta 返回的 json 里 id 字段形如 "quilt-loader-0.24.0-1.20.1"，直接采用官方给的命名。
        var versionId = string.IsNullOrEmpty(detail.Id) ? $"quilt-loader-{loaderVersion}-{mcVersion}" : detail.Id;
        var versionDir = Path.Combine(minecraftDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        // 跟 Fabric 一样做成"独立实例"：把原版 client.jar 拷贝进 Quilt 自己的版本文件夹，去掉
        // inheritsFrom，并从原版 json 补齐 assetIndex/assets/downloads/javaVersion 字段——
        // 理由和具体做法见 InstallFabricClientAsync 里的详细注释，这里两边保持一致。
        var parentJarPath = ResolveVersionFile(parentVersionDir, mcVersion, "jar");
        if (parentJarPath != null)
        {
            File.Copy(parentJarPath, Path.Combine(versionDir, $"{versionId}.jar"), overwrite: true);
        }
        var parentJsonPath = ResolveVersionFile(parentVersionDir, mcVersion, "json");
        if (parentJsonPath != null)
        {
            var parentDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
            if (parentDetail != null)
            {
                detail.AssetIndex ??= parentDetail.AssetIndex;
                detail.Assets ??= parentDetail.Assets;
                detail.Downloads ??= parentDetail.Downloads;
                detail.JavaVersion ??= parentDetail.JavaVersion;
            }
        }
        detail.InheritsFrom = null;
        detail.Id = versionId;
        var finalProfileJson = JsonSerializer.Serialize(detail,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
        await File.WriteAllTextAsync(versionJsonPath, finalProfileJson, ct);

        // 3. 补下 libraries：Quilt profile json 里的库同样是"name+url"风格(无 downloads 对象)，
        // 复用跟 Fabric 共用的同一套下载逻辑。
        progress?.Report(new ProgressInfo("下载 Quilt 加载器库文件", 0, Math.Max(detail.Libraries.Count, 1), versionId));
        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, detail, progress, ct);

        // 4. 可选：QSL（Quilt 生态里事实上的"标准库"，很多 Quilt 模组依赖它，跟 Fabric API 一样
        // 常有新手不知道要单独装）。这一步失败不应该让整个 Quilt Loader 安装被判定为失败——
        // loader 本身已经装好、可以正常启动游戏，QSL 只是常见依赖，装不上顶多是后续装某些 mod
        // 时提示缺依赖，用户还能再手动装一次，不应该因为网络抖动就让用户以为 Quilt 都没装上。
        if (installQsl)
        {
            progress?.Report(new ProgressInfo("下载 QSL", 0, 1, QslModrinthSlug));
            try
            {
                var versions = await _modrinth.GetVersionsAsync(QslModrinthSlug, mcVersion, ct);
                // QSL 的 Modrinth 版本列表理论上只发布 Quilt 构建，但保险起见仍按 loaders 字段过滤，
                // 跟 Fabric API 那边的处理方式保持一致，避免装到不兼容的构建。
                var qslVersion = versions.FirstOrDefault(v =>
                    v.Loaders != null && v.Loaders.Any(l => l.Equals("quilt", StringComparison.OrdinalIgnoreCase)))
                    ?? versions.FirstOrDefault();

                if (qslVersion == null)
                {
                    progress?.Report(new ProgressInfo(
                        $"QSL 没有找到适配 MC {mcVersion} 的版本，已跳过（不影响 Quilt Loader 本身的安装）",
                        1, 1, QslModrinthSlug));
                }
                else
                {
                    var qslProgress = new Progress<string>(msg =>
                        progress?.Report(new ProgressInfo("下载 QSL", 0, 1, msg)));
                    await _modrinth.DownloadResourceAsync(minecraftDir, ModrinthResourceType.Mod, qslVersion,
                        qslProgress, saveName: null, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 同上：QSL 下载失败不应该让整个安装抛异常，只在进度里提示一下，
                // 让用户知道"loader 装好了，但 QSL 这一步没成功"，而不是笼统地报"安装失败"。
                progress?.Report(new ProgressInfo(
                    $"QSL 下载失败（{ex.Message}），已跳过，不影响 Quilt Loader 本身的安装",
                    1, 1, QslModrinthSlug));
            }
        }

        progress?.Report(new ProgressInfo("安装完成", 1, 1, versionId));
        return versionId;
    }

    /// <summary>
    /// 安装 Forge/NeoForge 客户端：下载官方安装器 jar，本地用指定 Java 跑一次 --installClient。
    /// 与服务端的 RunForgeInstallerAsync 是同一套"起进程等退出码"模式，只是参数不同，
    /// 这里独立实现（而不是直接调用服务端那个方法）是因为服务端版本的参数/异常信息文案是按
    /// "服务端安装"场景写的，混用会让客户端场景下的报错提示文不对题。
    /// </summary>
    public async Task<string> InstallForgeOrNeoForgeClientAsync(string minecraftDir, ServerCoreType coreType,
        string fullVersion, string javaExePath, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        if (coreType is not (ServerCoreType.Forge or ServerCoreType.NeoForge))
            throw new ArgumentException("只支持 Forge/NeoForge。", nameof(coreType));
        if (!File.Exists(javaExePath))
            throw new FileNotFoundException("找不到可用的 Java，无法运行加载器安装器。", javaExePath);

        var mavenBase = coreType == ServerCoreType.Forge ? ForgeMavenBase : NeoForgeMavenBase;
        var prefix = coreType == ServerCoreType.Forge ? "forge" : "neoforge";
        var fileName = $"{prefix}-{fullVersion}-installer.jar";
        var url = $"{mavenBase}/{fullVersion}/{fileName}";

        // Forge/NeoForge 官方安装器是照搬 Mojang 官方启动器的行为写的，它在 --installClient 时会去读
        // <.minecraft>/launcher_profiles.json，如果这个文件不存在就直接报错退出（"There is no Minecraft
        // launcher profile ... you need to run the launcher first!"，退出码 1），即使 .minecraft 目录本身
        // 已经存在、版本也已经装好也不例外——它只认这一个文件存在与否，不检查目录里是否已经有版本。
        // 因为 XCL2 是独立启动器，从来不会自己生成这个 Mojang 专用的档案文件，所以只要是全新的
        // .minecraft 目录（或者是没被官方启动器打开过的目录），装 Forge/NeoForge 必现这个报错。
        // 这里在跑安装器之前主动写一份能满足安装器"文件存在且是合法 JSON"要求的最小占位文件，
        // 不需要跟真实的 Mojang 官方启动器格式完全一致（安装器只检查文件存在性和基本 JSON 结构，
        // 不会真的把这个当启动器状态使用），已存在的文件不覆盖，避免破坏用户可能真正用官方启动器
        // 生成过的档案数据。
        EnsureLauncherProfilesJson(minecraftDir);

        var tempDir = Path.Combine(Path.GetTempPath(), "xcl2-loader-installer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, fileName);

        progress?.Report(new ProgressInfo($"下载 {coreType} 安装器", 0, 2, fileName));
        await DownloadFileNoHashCheckAsync(url, installerPath, ct);

        progress?.Report(new ProgressInfo("正在运行安装器（首次运行可能需要下载额外库文件）", 1, 2, fileName));
        var psi = new ProcessStartInfo
        {
            FileName = javaExePath,
            ArgumentList = { "-jar", installerPath, "--installClient", minecraftDir },
            WorkingDirectory = tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 显式指定 UTF-8：不指定时用的是 Console.OutputEncoding（中文 Windows 下通常是 GBK/936），
            // 而 Forge/NeoForge 安装器（以及它内部调起的 Java 子进程）打印的诊断信息很多是 UTF-8，
            // 编码不一致会导致失败时捕获到的"最后输出"全是乱码，报错信息形同虚设，用户完全看不懂
            // 到底哪里失败了——这是"Forge 安装报错但看不出原因"的一个常见诱因，跟安装器本身是否
            // 真的失败无关，是纯粹的输出编码问题。
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var outputLines = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputLines.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) outputLines.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 安装器进程超时保护：Forge/NeoForge 安装器本身会再联网下载一批 library（不是本类
        // 之前已经下载好的 installer jar 本身，是安装器运行时自己另外拉取的依赖），网络卡住时
        // 之前的代码会无限等待 WaitForExitAsync，UI 侧表现为进度条停在"正在运行安装器"不再变化，
        // 用户既不知道是卡住了还是真的在跑、也没有任何机会中止，只能强杀整个启动器进程。
        // 这里给一个宽松但有限的超时（10 分钟，安装器本身网络下载可能比较慢，尤其国内直连
        // Forge/NeoForge 官方源较慢是已知情况，不能设得太短），超时后主动杀掉子进程并给出
        // 明确提示，而不是让调用方永远等不到结果。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        bool timedOut;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            timedOut = false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
        }

        if (timedOut)
        {
            throw new InvalidOperationException(
                $"{coreType} 客户端安装器运行超过 10 分钟仍未完成，已中止。这通常是因为安装器自己\n" +
                "另外联网下载依赖库时网络太慢或被墙——可以尝试更换网络环境（如使用代理）后重试，\n" +
                "或者检查本地防火墙/杀毒软件是否拦截了 Java 的联网请求。");
        }

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', outputLines.TakeLast(20));
            var fullOutput = string.Join('\n', outputLines);
            var hint = DiagnoseForgeInstallerFailure(fullOutput);
            throw new InvalidOperationException(
                $"{coreType} 客户端安装器执行失败（退出码 {process.ExitCode}）。{hint}最后输出：\n{tail}");
        }

        try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响安装已经完成这个事实 */ }

        // 安装器会自己在 versions/ 下生成形如 "{mcVersion}-{prefix}-{loaderVersion}" 的版本目录，
        // 这里在 versions/ 下找一个最近创建、名字包含加载器前缀的目录作为安装结果返回给调用方，
        // 不同 Forge/NeoForge 版本生成的确切目录命名格式有细微差异，用"最近修改时间"比精确拼字符串更稳妥。
        var versionsDir = Path.Combine(minecraftDir, "versions");
        var candidate = Directory.Exists(versionsDir)
            ? Directory.GetDirectories(versionsDir)
                .Where(d => Path.GetFileName(d).Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        // Forge/NeoForge 官方安装器生成的 version json 默认也是靠 inheritsFrom 指向原版文件夹
        // （跟 Fabric/Quilt 改造前的行为一样），同一份原版 jar 被多个加载器实例共用。这里同样把它
        // 改造成"独立实例"：把原版 client.jar 拷贝进安装器生成的这个文件夹，去掉 json 里的
        // inheritsFrom，并从原版 json 补齐 assetIndex/assets/downloads/javaVersion 字段——
        // 具体理由跟 InstallFabricClientAsync 里的注释一致，这里三种加载器统一处理方式。
        if (candidate != null)
        {
            try
            {
                var loaderVersionId = Path.GetFileName(candidate);
                var loaderJsonPath = ResolveVersionFile(candidate, loaderVersionId, "json");
                if (loaderJsonPath != null)
                {
                    var loaderDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(loaderJsonPath));
                    if (loaderDetail != null && !string.IsNullOrEmpty(loaderDetail.InheritsFrom))
                    {
                        var vanillaId = loaderDetail.InheritsFrom;
                        var vanillaDir = Path.Combine(minecraftDir, "versions", vanillaId);
                        var vanillaJarPath = ResolveVersionFile(vanillaDir, vanillaId, "jar");
                        if (vanillaJarPath != null)
                        {
                            File.Copy(vanillaJarPath, Path.Combine(candidate, $"{loaderVersionId}.jar"), overwrite: true);
                        }
                        var vanillaJsonPath = ResolveVersionFile(vanillaDir, vanillaId, "json");
                        if (vanillaJsonPath != null)
                        {
                            var vanillaDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(vanillaJsonPath));
                            if (vanillaDetail != null)
                            {
                                loaderDetail.AssetIndex ??= vanillaDetail.AssetIndex;
                                loaderDetail.Assets ??= vanillaDetail.Assets;
                                loaderDetail.Downloads ??= vanillaDetail.Downloads;
                                loaderDetail.JavaVersion ??= vanillaDetail.JavaVersion;
                            }
                        }
                        loaderDetail.InheritsFrom = null;
                        loaderDetail.Id = loaderVersionId;
                        var finalJson = JsonSerializer.Serialize(loaderDetail,
                            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                        await File.WriteAllTextAsync(loaderJsonPath, finalJson, ct);
                    }
                }
            }
            catch
            {
                // 独立实例化失败(比如原版 jar 意外找不到)不应该让整个 Forge/NeoForge 安装被判定为失败——
                // 安装器本身已经成功产出了一个能用 inheritsFrom 正常启动的版本，独立化只是锦上添花，
                // 失败了大不了退回旧的"共用原版文件夹"行为，游戏依然能正常启动。
            }
        }

        progress?.Report(new ProgressInfo("安装完成", 2, 2, fileName));
        return candidate != null ? Path.GetFileName(candidate) : fullVersion;
    }

    /// <summary>
    /// 根据安装器完整输出里的关键词，识别几种社区里最常见、有明确解决办法的 Forge/NeoForge
    /// 安装失败原因，返回一句可直接指导用户下一步该做什么的中文提示（识别不到就返回空字符串，
    /// 调用方仍然会展示原始输出尾巴，不会因为诊断失败而丢失信息）。
    ///
    /// 这几类是实际使用中反复出现、且原始安装器报错信息对普通用户很不友好的典型情况：
    /// - Java 版本不匹配（安装器要求特定 Java 版本运行，用户本地默认 Java 版本不满足）；
    /// - 网络下载失败/校验和不匹配（安装器自己联网下载 library 时失败，常见于国内直连不稳定）；
    /// - 磁盘空间不足；
    /// - 目标目录没有写入权限（常见于装在 Program Files 下、没有管理员权限运行的情况）。
    /// </summary>
    private static string DiagnoseForgeInstallerFailure(string output)
    {
        if (string.IsNullOrEmpty(output)) return "";

        // UnsupportedClassVersionError / "class file version" 是"用错 Java 主版本号跑安装器"的
        // 典型异常类型，比如用 Java 8 跑一个要求 Java 17+ 才能运行的新版安装器。
        if (output.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("class file version", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：这通常是本地选用的 Java 版本太旧，安装器本身需要更高版本的 Java 才能运行。" +
                "请在「安装新版本」弹窗里点「自动检测」重新匹配，或去「设置」页下载一个更新的 Java 版本后重试。\n";
        }

        if (output.Contains("NoSuchAlgorithmException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("SSLHandshakeException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ConnectException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("UnknownHostException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Downloading library", StringComparison.OrdinalIgnoreCase) && output.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：安装器在自己联网下载所需的库文件时失败了，通常是网络问题（国内直连 Forge/NeoForge\n" +
                "官方源经常不稳定）。可以尝试更换网络环境（如使用代理）后重试，多试几次也可能成功。\n";
        }

        if (output.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：磁盘空间不足，请清理出至少 1GB 可用空间后重试。\n";
        }

        if (output.Contains("AccessDeniedException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：没有写入权限，通常是因为 .minecraft 文件夹装在 C:\\Program Files 等需要管理员\n" +
                "权限才能写入的目录下。建议把游戏文件夹换到不需要额外权限的位置（如 D:\\Games\\.minecraft），\n" +
                "或者以管理员身份运行本启动器后重试。\n";
        }

        return "";
    }

    /// <summary>
    /// 确保 minecraftDir 下存在一份 launcher_profiles.json，满足 Forge/NeoForge 官方安装器
    /// --installClient 的前置检查。已存在时不覆盖（不破坏可能存在的真实数据）。
    /// 格式对齐官方启动器实际写出的最小结构（profiles/settings/version 三个顶层字段），
    /// 安装器只做"文件存在 + JSON 可解析"检查，不校验具体字段内容，这里给的是能通过校验的最小合法值。
    /// </summary>
    private static void EnsureLauncherProfilesJson(string minecraftDir)
    {
        Directory.CreateDirectory(minecraftDir);
        var path = Path.Combine(minecraftDir, "launcher_profiles.json");
        if (File.Exists(path)) return;

        const string minimalProfiles = """
        {
          "profiles": {},
          "settings": {
            "crashAssistance": true,
            "enableAdvanced": false,
            "enableAnalytics": true,
            "enableHistorical": false,
            "enableReleases": true,
            "enableSnapshots": false,
            "keepLauncherOpen": false,
            "profileSorting": "ByLastPlayed",
            "showGameLog": false,
            "showMenu": false,
            "soundOn": false
          },
          "version": 3
        }
        """;

        File.WriteAllText(path, minimalProfiles);
    }

    /// <summary>单次下载尝试超时，理由同 DownloadService.SingleAttemptTimeout：避免假死连接
    /// 拖到 _http 整体的 15 分钟超时才失败——同类"下载卡住"问题，这里跟 DownloadService 一起修。</summary>
    private static readonly TimeSpan SingleAttemptTimeout = TimeSpan.FromSeconds(45);

    private async Task DownloadFileNoHashCheckAsync(string url, string destPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        const int maxAttempts = 3;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var tmp = destPath + $".tmp{attempt}";
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(SingleAttemptTimeout);
            try
            {
                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs, attemptCts.Token);
                }
                if (new FileInfo(tmp).Length == 0)
                {
                    lastError = new IOException($"下载得到空文件: {url}");
                    TryDelete(tmp);
                    continue;
                }
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException(
                    $"下载单次尝试超时（{SingleAttemptTimeout.TotalSeconds:0}秒内无响应）: {url}");
                TryDelete(tmp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                TryDelete(tmp);
            }
        }
        // 友好化报错：如果最后一次失败是 404（安装器文件在 Maven 上找不到，常见于这个具体版本号
        // 已经被下架/移除），额外提示"换一个版本试试"，而不是只甩一个裸的 URL 让用户自己猜原因。
        var is404 = lastError is HttpRequestException hre &&
            (hre.StatusCode == System.Net.HttpStatusCode.NotFound || hre.Message.Contains("404"));
        var hint = is404
            ? "\n这通常是因为该版本的安装器已从官方仓库下架，建议在版本列表里换一个相近的版本重试。"
            : "";
        throw new IOException($"下载失败（已重试 {maxAttempts} 次）: {url}{hint}", lastError);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略清理失败 */ }
    }

    /// <summary>释放内部 _vanillaDownloader（进而释放它可能持有的智能限速后台采样任务）。</summary>
    public void Dispose() => _vanillaDownloader.Dispose();
}
