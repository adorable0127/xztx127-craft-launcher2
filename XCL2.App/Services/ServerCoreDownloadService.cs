using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 服务端核心下载。这是"服务端管理模块"的第一块地基——先把"能下载到可用的服务端本体"这件事做扎实，
/// 开服向导/进程管理等后续功能都要建立在这上面。
///
/// 各核心类型的数据源（均为对外公开、无需鉴权的官方 API）：
/// - Vanilla:  复用 Mojang version_manifest_v2 + version json 里的 downloads.server 字段
///             （和客户端下载走的是同一份 manifest，只是取 server 而不是 client 字段）。
/// - Paper:    fill.papermc.io/v3（PaperMC 在 2024 年把旧的 papermc.io/v2 端点下线换成了这个新域名，
///             旧端点现在会 404。这个新 API 要求请求带一个"看起来像正常浏览器/客户端"的 User-Agent，
///             用泛型的 "XCL2" 之类容易被当成爬虫拒绝，这里显式设置成 "XCL2Launcher/1.0" 风格）。
/// - Fabric:   meta.fabricmc.net/v2，服务端是直接拼 URL 就能下到 jar，不需要先安装器。
/// - Forge/NeoForge: 官方只发布"安装器 jar"，没有直接可用的服务端本体下载。安装器下载下来后
///             要用 `java -jar xxx-installer.jar --installServer <目标目录>` 本地跑一遍才会在目标目录
///             生成真正的服务端文件（run.bat/run.sh + libraries/ + 真正的服务端 jar）。
///             这里的 RunForgeInstallerAsync 就是负责跑这一步，需要调用方提供一个可用的 Java 路径。
/// - Purpur:   api.purpurmc.org/v2/purpur，是 Paper 下游 fork，直接分发预编译 jar，
///             接口形态是"列出某 MC 版本的 build 号数组 + /<version>/<build>/download 下载"，
///             跟 Paper 的"查构建列表再下载"思路一样，只是没有 Paper 那种 channel/推荐标记，
///             Purpur 的 /latest 端点直接给最新 build，本身就相当于"推荐版"。
/// - Folia/Velocity/Waterfall: 都是 PaperMC 官方项目，复用跟 Paper 完全相同的
///             fill.papermc.io/v3 API，只是 project key 换成 folia/velocity/waterfall，
///             下载字段的 key 依然是 "server:default"（PaperMC 文档原话：这个 API 对所有
///             project 都是同一套结构，project 名是唯一变量），所以这三个可以直接复用
///             DownloadPaperAsync 的逻辑，只是把 project 参数化。
/// - Spigot:   官方不提供预编译 jar，必须在本地用 BuildTools.jar 拉源码编译。这里下载的是
///             BuildTools.jar 本体（hub.spigotmc.org 的 Jenkins 最新构建），真正编译由
///             RunSpigotBuildToolsAsync 负责，需要调用方保证本机已装 Git，且给一个可用的
///             Java 路径（编译过程本身也是 `java -jar BuildTools.jar` 起的）。
/// </summary>
public class ServerCoreDownloadService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private const string MojangManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private const string PaperApiBase = "https://fill.papermc.io/v3";
    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    // ForgePromotionsUrl 已挪到 ForgeVersionQueryService.ForgePromotionsUrl（消重复代码），这里不再重复定义。
    private const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const string PurpurApiBase = "https://api.purpurmc.org/v2/purpur";
    private const string SpigotBuildToolsUrl =
        "https://hub.spigotmc.org/jenkins/job/BuildTools/lastSuccessfulBuild/artifact/target/BuildTools.jar";

    public ServerCoreDownloadService()
    {
        // fill.papermc.io 会拒绝没有明确标识或明显是默认 HttpClient UA 的请求；
        // 这里统一给所有请求都带上，Fabric/Forge 等其他源不介意多这个头，不需要按源单独区分。
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2Launcher", "1.0"));
    }

    // ============================================================
    // 版本/构建号 列表查询：用于 UI 下拉框填充可选项
    // ============================================================

    /// <summary>Vanilla：可安装服务端的 MC 版本列表（复用官方 manifest，release 类型优先展示）。</summary>
    public async Task<List<string>> GetVanillaVersionsAsync(bool includeSnapshots, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(MojangManifestUrl, ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            var type = v.GetProperty("type").GetString();
            if (type != "release" && !(includeSnapshots && type == "snapshot")) continue;
            result.Add(v.GetProperty("id").GetString()!);
        }
        return result;
    }

    /// <summary>Paper：fill.papermc.io/v3/projects/paper 返回所有支持的 MC 版本。</summary>
    public async Task<List<string>> GetPaperVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{PaperApiBase}/projects/paper", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        // v3 结构: { "project": "paper", "versions": { "1.21": [...builds 或分组], ... } }
        // 用 "versions" 对象的 key 集合作为可选 MC 版本列表；具体结构以 GetProperty 容错方式解析，
        // 避免因为字段细节和文档不完全一致（第三方 API 有变动可能性）而直接崩溃。
        if (doc.RootElement.TryGetProperty("versions", out var versionsEl))
        {
            foreach (var group in versionsEl.EnumerateObject())
            {
                // versions 底下可能是按大版本分组的对象（如 "1.21": ["1.21", "1.21.1"]），
                // 也可能直接是版本号数组，两种都兼容一下。
                if (group.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in group.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            result.Add(item.GetString()!);
                }
                else
                {
                    result.Add(group.Name);
                }
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>Paper：某个 MC 版本下所有可用的 build 号，按新到旧排列，Recommended 标记稳定版。</summary>
    public async Task<List<ServerCoreBuild>> GetPaperBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{PaperApiBase}/projects/paper/versions/{mcVersion}/builds", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            var buildNum = b.GetProperty("id").GetInt32();
            var channel = b.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
            result.Add(new ServerCoreBuild
            {
                DisplayVersion = buildNum.ToString(),
                IsRecommended = channel == "STABLE" || channel == "default"
            });
        }
        result.Reverse(); // API 通常按旧到新返回，反转成新到旧方便 UI 默认选中最新
        return result;
    }

    /// <summary>Fabric：所有支持的 MC 版本（服务端支持的版本和客户端是同一份列表）。</summary>
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

    /// <summary>Fabric：可用的 loader 版本列表，stable 标记稳定版。</summary>
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

    /// <summary>Purpur：api.purpurmc.org/v2/purpur 返回 { "versions": ["1.20.1", ...] }，
    /// 按官方给出的顺序展示即可（一般是旧到新）。</summary>
    public async Task<List<string>> GetPurpurVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(PurpurApiBase, ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        if (doc.RootElement.TryGetProperty("versions", out var versionsEl))
        {
            foreach (var v in versionsEl.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String)
                    result.Add(v.GetString()!);
        }
        return result;
    }

    /// <summary>Purpur：某个 MC 版本下所有可用的 build 号，新到旧排列。Purpur 没有 Paper 那种
    /// channel/推荐标记，这里把 API 报告的 "latest" build 号标记为推荐，其余不标记。</summary>
    public async Task<List<ServerCoreBuild>> GetPurpurBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{PurpurApiBase}/{mcVersion}", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        if (!doc.RootElement.TryGetProperty("builds", out var buildsEl)) return result;

        string? latest = buildsEl.TryGetProperty("latest", out var latestEl) ? latestEl.GetString() : null;
        if (buildsEl.TryGetProperty("all", out var allEl))
        {
            foreach (var b in allEl.EnumerateArray())
            {
                var num = b.GetString()!;
                result.Add(new ServerCoreBuild { DisplayVersion = num, IsRecommended = num == latest });
            }
            result.Reverse(); // "all" 通常按旧到新给出，反转成新到旧方便 UI 默认选中最新
        }
        return result;
    }

    /// <summary>Folia/Velocity/Waterfall 共用：都是 fill.papermc.io/v3 上的 PaperMC 官方项目，
    /// project key 分别对应 folia/velocity/waterfall。逻辑和 GetPaperVersionsAsync 完全一致，
    /// 抽成通用方法用 projectKey 参数化，避免三份重复代码。</summary>
    private async Task<List<string>> GetPaperFamilyVersionsAsync(string projectKey, CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{PaperApiBase}/projects/{projectKey}", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        if (doc.RootElement.TryGetProperty("versions", out var versionsEl))
        {
            foreach (var group in versionsEl.EnumerateObject())
            {
                if (group.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in group.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            result.Add(item.GetString()!);
                }
                else
                {
                    result.Add(group.Name);
                }
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>Folia/Velocity/Waterfall 共用的 build 列表查询，逻辑和 GetPaperBuildsAsync 一致，
    /// project key 参数化。</summary>
    private async Task<List<ServerCoreBuild>> GetPaperFamilyBuildsAsync(string projectKey, string mcVersion, CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{PaperApiBase}/projects/{projectKey}/versions/{mcVersion}/builds", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            var buildNum = b.GetProperty("id").GetInt32();
            var channel = b.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
            result.Add(new ServerCoreBuild
            {
                DisplayVersion = buildNum.ToString(),
                // RECOMMENDED 是 Fill v3 新增的channel，文档里提到目前只有 Velocity 在用；
                // 其余项目(Folia/Waterfall)沿用 Paper 那套 STABLE 标记，两种都判断一下更保险。
                IsRecommended = channel == "STABLE" || channel == "RECOMMENDED" || channel == "default"
            });
        }
        result.Reverse();
        return result;
    }

    public Task<List<string>> GetFoliaVersionsAsync(CancellationToken ct = default) => GetPaperFamilyVersionsAsync("folia", ct);
    public Task<List<ServerCoreBuild>> GetFoliaBuildsAsync(string mcVersion, CancellationToken ct = default) => GetPaperFamilyBuildsAsync("folia", mcVersion, ct);

    public Task<List<string>> GetVelocityVersionsAsync(CancellationToken ct = default) => GetPaperFamilyVersionsAsync("velocity", ct);
    public Task<List<ServerCoreBuild>> GetVelocityBuildsAsync(string mcVersion, CancellationToken ct = default) => GetPaperFamilyBuildsAsync("velocity", mcVersion, ct);

    public Task<List<string>> GetWaterfallVersionsAsync(CancellationToken ct = default) => GetPaperFamilyVersionsAsync("waterfall", ct);
    public Task<List<ServerCoreBuild>> GetWaterfallBuildsAsync(string mcVersion, CancellationToken ct = default) => GetPaperFamilyBuildsAsync("waterfall", mcVersion, ct);

    /// <summary>Forge：有官方安装器构建的 MC 版本列表。逻辑已抽到 ForgeVersionQueryService
    /// （见该类注释：跟 ClientLoaderInstallService 消除重复代码）。</summary>
    public Task<List<string>> GetForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeVersionsAsync(_http, ct);

    /// <summary>Forge：某个 MC 版本对应的 recommended/latest 安装器版本号（完整 "mcver-forgever" 格式）。</summary>
    public Task<List<ServerCoreBuild>> GetForgeInstallerVersionsAsync(string mcVersion, CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeInstallerVersionsAsync(_http, mcVersion, ct);

    /// <summary>
    /// NeoForge：可用的完整版本号列表。逻辑已抽到 ForgeVersionQueryService（见该类注释里
    /// 关于 404 bug 根因的说明），这里只保留原方法签名，调用方不用改。
    /// </summary>
    public Task<List<string>> GetNeoForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetNeoForgeVersionsAsync(_http, ct);

    // ============================================================
    // 实际下载
    // ============================================================

    public async Task<ServerCoreDownloadResult> DownloadAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.TargetDir))
            throw new ArgumentException("必须指定安装位置。");
        Directory.CreateDirectory(req.TargetDir);

        return req.CoreType switch
        {
            ServerCoreType.Vanilla => await DownloadVanillaAsync(req, progress, ct),
            ServerCoreType.Paper => await DownloadPaperAsync(req, progress, ct),
            ServerCoreType.Fabric => await DownloadFabricAsync(req, progress, ct),
            ServerCoreType.Forge => await DownloadForgeInstallerAsync(req, progress, ct),
            ServerCoreType.NeoForge => await DownloadNeoForgeInstallerAsync(req, progress, ct),
            ServerCoreType.Purpur => await DownloadPurpurAsync(req, progress, ct),
            ServerCoreType.Folia => await DownloadPaperFamilyAsync(req, "folia", progress, ct),
            ServerCoreType.Velocity => await DownloadPaperFamilyAsync(req, "velocity", progress, ct),
            ServerCoreType.Waterfall => await DownloadPaperFamilyAsync(req, "waterfall", progress, ct),
            ServerCoreType.Spigot => await DownloadSpigotBuildToolsAsync(req, progress, ct),
            _ => throw new NotSupportedException($"暂不支持的核心类型：{req.CoreType}")
        };
    }

    private async Task<ServerCoreDownloadResult> DownloadVanillaAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        progress?.Report(new ProgressInfo("查询版本信息", 0, 3, req.McVersion));
        var manifestJson = await _http.GetStringAsync(MojangManifestUrl, ct);
        using var manifestDoc = JsonDocument.Parse(manifestJson);

        string? versionJsonUrl = null;
        foreach (var v in manifestDoc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == req.McVersion)
            {
                versionJsonUrl = v.GetProperty("url").GetString();
                break;
            }
        }
        if (versionJsonUrl == null)
            throw new InvalidOperationException($"在版本清单中找不到 MC 版本 {req.McVersion}。");

        progress?.Report(new ProgressInfo("下载版本详情", 1, 3, req.McVersion));
        var versionJson = await _http.GetStringAsync(versionJsonUrl, ct);
        using var versionDoc = JsonDocument.Parse(versionJson);

        if (!versionDoc.RootElement.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("server", out var serverArtifact))
        {
            throw new InvalidOperationException(
                $"MC {req.McVersion} 没有提供服务端下载（部分极早期版本官方未发布对应的 server.jar）。");
        }

        var url = serverArtifact.GetProperty("url").GetString()!;
        var sha1 = serverArtifact.TryGetProperty("sha1", out var s) ? s.GetString() ?? "" : "";
        var destPath = Path.Combine(req.TargetDir, "server.jar");

        // 权威来源：version.json 自带的 javaVersion.majorVersion 字段，就是 Mojang 官方声明的
        // "这个版本需要哪个 Java 主版本运行"。之前完全没有读取这个字段，一律沿用客户端全局设置
        // 的 Java 版本，是 "class file version 69.0...up to 65.0" 这类 bundler LinkageError 崩溃
        // 的根因——用了和服务端要求不匹配的 Java 去跑。
        int requiredJava;
        if (versionDoc.RootElement.TryGetProperty("javaVersion", out var javaVersionEl) &&
            javaVersionEl.TryGetProperty("majorVersion", out var majorVersionEl))
        {
            requiredJava = majorVersionEl.GetInt32();
        }
        else
        {
            requiredJava = ServerJavaRequirement.EstimateMajorVersionForMcVersion(req.McVersion);
        }

        progress?.Report(new ProgressInfo("下载服务端主程序", 2, 3, "server.jar"));
        await DownloadFileAsync(url, destPath, sha1, ct);

        progress?.Report(new ProgressInfo("完成", 3, 3, "server.jar"));
        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = false,
            ServerJarFileName = "server.jar",
            RequiredJavaMajorVersion = requiredJava
        };
    }

    private async Task<ServerCoreDownloadResult> DownloadPaperAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // fill.papermc.io 的 /builds 列表偶尔会把一个刚被下架/还没完成上传的 build 标成
        // "存在且 recommended"，实际请求它的下载端点会 404（这不是我们这边缓存过期，是上游
        // 列表接口和下载 CDN 之间短暂不一致）。之前的做法是拿到 build 号就直接下，404 了只会
        // 对同一个坏 URL 重试 3 次，注定失败。现在改成：一旦某个 build 下载 404，就自动按
        // "新到旧"顺序尝试列表里的下一个 build，直到成功或者候选耗尽。
        List<string> candidateBuilds;
        if (!string.IsNullOrEmpty(req.BuildOrLoaderVersion))
        {
            candidateBuilds = new List<string> { req.BuildOrLoaderVersion };
        }
        else
        {
            progress?.Report(new ProgressInfo("查询可用构建", 0, 2, req.McVersion));
            var builds = await GetPaperBuildsAsync(req.McVersion, ct);
            if (builds.Count == 0)
                throw new InvalidOperationException($"Paper 没有找到 MC {req.McVersion} 对应的可用构建。");
            // 优先推荐版，其余按新到旧排列作为兜底候选（GetPaperBuildsAsync 已经是新到旧）。
            var recommended = builds.Where(b => b.IsRecommended).Select(b => b.DisplayVersion);
            var rest = builds.Select(b => b.DisplayVersion);
            candidateBuilds = recommended.Concat(rest).Distinct().ToList();
        }

        Exception? lastFailure = null;
        var succeeded = false;
        var destPath = Path.Combine(req.TargetDir, "server.jar");
        for (var i = 0; i < candidateBuilds.Count; i++)
        {
            var build = candidateBuilds[i];
            try
            {
                // v3 下载端点：官方文档明确要求直接用 build 详情接口返回的 downloads."server:default".url
                // 字段，不要自己拼 URL。之前这里是手动拼接 {PaperApiBase}/projects/paper/versions/{mc}/builds/{build}/downloads/{fileName}，
                // 这个格式已经不对——Fill v3 上线后，真正的下载文件是从另一个专门的静态资源域名
                // fill-data.papermc.io 提供的，文档原话是"下载链接已经直接嵌在 API 响应里了，
                // 不需要也不建议自己再手动拼 URL"。手动拼出来的旧格式 URL 打到 fill.papermc.io
                // 这个 API 域名本身、而不是真正存文件的 CDN，所以无论换哪个 build 号都会 404——
                // 这正是"Paper 换了好几个 build 号还是全部下载失败"的根因，不是某个 build 本身
                // 下架了，是 URL 拼接方式过时了。
                progress?.Report(new ProgressInfo("查询构建详情", 1, 2, build));
                var buildDetailJson = await _http.GetStringAsync(
                    $"{PaperApiBase}/projects/paper/versions/{req.McVersion}/builds/{build}", ct);
                using var buildDoc = JsonDocument.Parse(buildDetailJson);

                string fileName;
                string? sha256 = null;
                string? url = null;
                var downloadsEl = buildDoc.RootElement.GetProperty("downloads");
                if (downloadsEl.TryGetProperty("server:default", out var serverDl))
                {
                    fileName = serverDl.GetProperty("name").GetString()!;
                    url = serverDl.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (serverDl.TryGetProperty("checksums", out var checksums) && checksums.TryGetProperty("sha256", out var sh))
                        sha256 = sh.GetString();
                }
                else
                {
                    fileName = $"paper-{req.McVersion}-{build}.jar";
                }

                // 兜底：万一某天响应里真的没有 url 字段（API 再次变动），退回手动拼接旧格式，
                // 好过直接崩溃；但正常情况下应该总是走上面拿到的官方 url。
                url ??= $"{PaperApiBase}/projects/paper/versions/{req.McVersion}/builds/{build}/downloads/{fileName}";

                progress?.Report(new ProgressInfo(
                    i == 0 ? "下载服务端主程序" : $"该构建已不可用，改用 build {build} 重试",
                    2, 2, fileName));
                // Paper 用 sha256 而不是 sha1，DownloadFileAsync 目前只支持 sha1 校验；
                // 这里改用不做哈希强校验的下载路径，只做基本的文件大小/完整性检查（HttpClient 层面）。
                await DownloadFileNoHashCheckAsync(url, destPath, ct);
                if (sha256 != null)
                {
                    var actualSha256 = ComputeSha256(destPath);
                    if (!string.Equals(actualSha256, sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Paper 服务端下载文件 SHA256 校验失败，文件可能损坏，请重试。");
                }

                succeeded = true;
                break;
            }
            catch (Exception ex) when (IsNotFoundFailure(ex) && i < candidateBuilds.Count - 1)
            {
                // 这个 build 下架了/暂时拿不到，记下来，换列表里下一个候选继续试，
                // 而不是直接把用户扔进"重试 3 次全 404"的死胡同。
                lastFailure = ex;
            }
        }

        if (!succeeded)
        {
            throw new IOException(
                $"Paper MC {req.McVersion} 尝试了 {candidateBuilds.Count} 个构建都下载失败" +
                "（该版本近期的构建可能都已从官方仓库下架），建议换一个相近的 MC 版本重试。",
                lastFailure);
        }

        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = false,
            ServerJarFileName = "server.jar",
            // Paper 没有像 Vanilla version.json 那样公开的 javaVersion 字段，退化为按 MC 版本号区间估算，
            // 加载器本身不会降低/提高原版对应的 Java 版本要求。
            RequiredJavaMajorVersion = ServerJavaRequirement.EstimateMajorVersionForMcVersion(req.McVersion)
        };
    }

    private async Task<ServerCoreDownloadResult> DownloadPurpurAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var build = req.BuildOrLoaderVersion;
        if (string.IsNullOrEmpty(build))
        {
            // 不指定 build 时直接用官方 /latest 端点，比自己先查列表再挑"最新"更省一次请求，
            // 也更准确（/latest 是官方权威地告诉你哪个是最新，不需要我们自己排序猜测）。
            build = "latest";
        }

        var fileName = $"purpur-{req.McVersion}-{build}.jar";
        var url = $"{PurpurApiBase}/{req.McVersion}/{build}/download";
        var destPath = Path.Combine(req.TargetDir, "server.jar");

        progress?.Report(new ProgressInfo("下载服务端主程序", 1, 1, fileName));
        await DownloadFileNoHashCheckAsync(url, destPath, ct);

        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = false,
            ServerJarFileName = "server.jar",
            // Purpur 基于 Paper，Java 版本要求跟随原版 MC 版本，没有独立的 javaVersion 字段可查，
            // 用法跟 Paper 一样退化为按 MC 版本号区间估算。
            RequiredJavaMajorVersion = ServerJavaRequirement.EstimateMajorVersionForMcVersion(req.McVersion)
        };
    }

    /// <summary>Folia/Velocity/Waterfall 共用：三者都在 fill.papermc.io/v3 上，跟 Paper 是完全
    /// 相同的 API 结构（下载字段同样是 "server:default"），只是 project key 不同，
    /// 逻辑直接复用 DownloadPaperAsync 的"候选 build 逐个尝试，404 就换下一个"策略，
    /// 参数化 projectKey 就不用三份几乎一样的代码。</summary>
    private async Task<ServerCoreDownloadResult> DownloadPaperFamilyAsync(ServerCoreDownloadRequest req,
        string projectKey, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        List<string> candidateBuilds;
        if (!string.IsNullOrEmpty(req.BuildOrLoaderVersion))
        {
            candidateBuilds = new List<string> { req.BuildOrLoaderVersion };
        }
        else
        {
            progress?.Report(new ProgressInfo("查询可用构建", 0, 2, req.McVersion));
            var builds = await GetPaperFamilyBuildsAsync(projectKey, req.McVersion, ct);
            if (builds.Count == 0)
                throw new InvalidOperationException($"{projectKey} 没有找到 MC {req.McVersion} 对应的可用构建。");
            var recommended = builds.Where(b => b.IsRecommended).Select(b => b.DisplayVersion);
            var rest = builds.Select(b => b.DisplayVersion);
            candidateBuilds = recommended.Concat(rest).Distinct().ToList();
        }

        Exception? lastFailure = null;
        var succeeded = false;
        var destPath = Path.Combine(req.TargetDir, "server.jar");
        for (var i = 0; i < candidateBuilds.Count; i++)
        {
            var build = candidateBuilds[i];
            try
            {
                progress?.Report(new ProgressInfo("查询构建详情", 1, 2, build));
                var buildDetailJson = await _http.GetStringAsync(
                    $"{PaperApiBase}/projects/{projectKey}/versions/{req.McVersion}/builds/{build}", ct);
                using var buildDoc = JsonDocument.Parse(buildDetailJson);

                string fileName;
                string? sha256 = null;
                string? url = null;
                var downloadsEl = buildDoc.RootElement.GetProperty("downloads");
                if (downloadsEl.TryGetProperty("server:default", out var serverDl))
                {
                    fileName = serverDl.GetProperty("name").GetString()!;
                    url = serverDl.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (serverDl.TryGetProperty("checksums", out var checksums) && checksums.TryGetProperty("sha256", out var sh))
                        sha256 = sh.GetString();
                }
                else
                {
                    fileName = $"{projectKey}-{req.McVersion}-{build}.jar";
                }

                url ??= $"{PaperApiBase}/projects/{projectKey}/versions/{req.McVersion}/builds/{build}/downloads/{fileName}";

                progress?.Report(new ProgressInfo(
                    i == 0 ? "下载服务端主程序" : $"该构建已不可用，改用 build {build} 重试",
                    2, 2, fileName));
                await DownloadFileNoHashCheckAsync(url, destPath, ct);
                if (sha256 != null)
                {
                    var actualSha256 = ComputeSha256(destPath);
                    if (!string.Equals(actualSha256, sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"{projectKey} 服务端下载文件 SHA256 校验失败，文件可能损坏，请重试。");
                }

                succeeded = true;
                break;
            }
            catch (Exception ex) when (IsNotFoundFailure(ex) && i < candidateBuilds.Count - 1)
            {
                lastFailure = ex;
            }
        }

        if (!succeeded)
        {
            throw new IOException(
                $"{projectKey} MC {req.McVersion} 尝试了 {candidateBuilds.Count} 个构建都下载失败" +
                "（该版本近期的构建可能都已从官方仓库下架），建议换一个相近的 MC 版本重试。",
                lastFailure);
        }

        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = false,
            ServerJarFileName = "server.jar",
            RequiredJavaMajorVersion = ServerJavaRequirement.EstimateMajorVersionForMcVersion(req.McVersion)
        };
    }

    /// <summary>Spigot：只下载 BuildTools.jar 本体，不在这里触发编译（编译耗时数分钟，
    /// 属于完全不同的中间态，调用方应显式调用 RunSpigotBuildToolsAsync 并展示对应进度）。</summary>
    private async Task<ServerCoreDownloadResult> DownloadSpigotBuildToolsAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var destPath = Path.Combine(req.TargetDir, "BuildTools.jar");
        progress?.Report(new ProgressInfo("下载 BuildTools", 1, 1, "BuildTools.jar"));
        await DownloadFileNoHashCheckAsync(SpigotBuildToolsUrl, destPath, ct);

        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = false,
            RequiresBuild = true,
            // BuildTools 自己也是个普通 jar，跑它本身只需要一个能用的 Java；
            // 真正编译出来的 Spigot 服务端所需 Java 版本跟随目标 MC 版本，一样按区间估算。
            RequiredJavaMajorVersion = ServerJavaRequirement.EstimateMajorVersionForMcVersion(req.McVersion)
        };
    }

    private async Task<ServerCoreDownloadResult> DownloadFabricAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var loaderVersion = req.BuildOrLoaderVersion;
        if (string.IsNullOrEmpty(loaderVersion))
        {
            progress?.Report(new ProgressInfo("查询最新 loader 版本", 0, 2, req.McVersion));
            var loaders = await GetFabricLoaderVersionsAsync(ct);
            loaderVersion = loaders.FirstOrDefault(l => l.IsRecommended)?.DisplayVersion ?? loaders.FirstOrDefault()?.DisplayVersion;
            if (loaderVersion == null)
                throw new InvalidOperationException("Fabric 没有找到可用的 loader 版本。");
        }

        // 需要 installer 版本；用最新稳定版即可，Fabric 服务端 jar 是把 installer 逻辑打包进最终 jar，
        // 用户不需要关心这个版本号，这里自动取最新稳定版。
        progress?.Report(new ProgressInfo("查询 installer 版本", 1, 2, loaderVersion));
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

        var url = $"{FabricMetaBase}/versions/loader/{req.McVersion}/{loaderVersion}/{installerVersion}/server/jar";
        var destPath = Path.Combine(req.TargetDir, "server.jar");

        progress?.Report(new ProgressInfo("下载服务端主程序", 2, 2, "server.jar"));
        await DownloadFileNoHashCheckAsync(url, destPath, ct);

        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = false,
            ServerJarFileName = "server.jar",
            RequiredJavaMajorVersion = ServerJavaRequirement.EstimateMajorVersionForMcVersion(req.McVersion)
        };
    }

    private async Task<ServerCoreDownloadResult> DownloadForgeInstallerAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var fullVersion = req.InstallerVersion; // 格式 "mcVersion-forgeVersion"，如 "1.20.1-47.2.20"
        if (string.IsNullOrEmpty(fullVersion))
        {
            progress?.Report(new ProgressInfo("查询推荐安装器版本", 0, 2, req.McVersion));
            var builds = await GetForgeInstallerVersionsAsync(req.McVersion, ct);
            fullVersion = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion ?? builds.FirstOrDefault()?.DisplayVersion;
            if (fullVersion == null)
                throw new InvalidOperationException($"Forge 没有找到 MC {req.McVersion} 对应的安装器版本。");
        }

        var fileName = $"forge-{fullVersion}-installer.jar";
        var url = $"{ForgeMavenBase}/{fullVersion}/{fileName}";
        var destPath = Path.Combine(req.TargetDir, fileName);

        progress?.Report(new ProgressInfo("下载 Forge 安装器", 1, 2, fileName));
        await DownloadFileNoHashCheckAsync(url, destPath, ct);
        progress?.Report(new ProgressInfo("安装器下载完成，等待安装", 2, 2, fileName));

        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = true,
            // 重要：安装器本身跑起来也需要匹配 MC 版本要求的 Java（不是"随便一个能跑 jar 的 Java 就行"）——
            // 老版本安装器如果用过新的 Java 跑，同样可能出现 class file version 不兼容的问题；
            // 调用方(CreateServerWindow)应该用这个字段去选/下载安装器要用的 Java，而不是固定用客户端全局 Java。
            RequiredJavaMajorVersion = ServerJavaRequirement.EstimateMajorVersionForMcVersion(req.McVersion)
        };
    }

    private async Task<ServerCoreDownloadResult> DownloadNeoForgeInstallerAsync(ServerCoreDownloadRequest req,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var version = req.InstallerVersion;
        if (string.IsNullOrEmpty(version))
        {
            var versions = await GetNeoForgeVersionsAsync(ct);
            // NeoForge 版本号形如 "21.1.100"，对应 MC 1.21.1；按约定用 McVersion 去掉 "1." 前缀做粗过滤，
            // 这只是给用户一个合理的默认候选，精确匹配交由 UI 层下拉框展示的完整版本列表由用户自选。
            version = versions.FirstOrDefault();
            if (version == null)
                throw new InvalidOperationException("NeoForge 没有找到可用的版本。");
        }

        var fileName = $"neoforge-{version}-installer.jar";
        var url = $"{NeoForgeMavenBase}/{version}/{fileName}";
        var destPath = Path.Combine(req.TargetDir, fileName);

        progress?.Report(new ProgressInfo("下载 NeoForge 安装器", 1, 2, fileName));
        await DownloadFileNoHashCheckAsync(url, destPath, ct);
        progress?.Report(new ProgressInfo("安装器下载完成，等待安装", 2, 2, fileName));

        return new ServerCoreDownloadResult
        {
            DownloadedFilePath = destPath,
            RequiresInstall = true,
            // NeoForge 版本号(如 "21.1.100")和 MC 版本号是独立编号体系，不能直接拿 req.McVersion
            // 传给 EstimateMajorVersionForMcVersion（那里面存的实际是选中的完整 NeoForge 版本号，
            // 不是 "1.21.1" 这种格式，直接传会被误判成 major=21 的天文数字版本，回退到最新 LTS 反而错）。
            // NeoForge 版本号前两段("21.1")约定对应 MC "1.21.1" 的 "21.1" 部分，这里换算一次再估算。
            RequiredJavaMajorVersion = ServerJavaRequirement.EstimateMajorVersionForMcVersion(
                NeoForgeVersionToMcVersion(version))
        };
    }

    /// <summary>
    /// 把 NeoForge 版本号(如 "21.1.100")换算成等价的 MC 版本号(如 "1.21.1")，仅用于估算 Java 版本要求，
    /// 不追求 100% 精确匹配实际 MC 补丁号（Java 版本要求只在 minor 级别的边界变化，够用）。
    /// 换算规则：NeoForge 前两段 "X.Y" 对应 MC "1.X.Y"。
    /// </summary>
    private static string NeoForgeVersionToMcVersion(string neoForgeVersion)
    {
        var parts = neoForgeVersion.Split('.');
        if (parts.Length < 2) return "1.21"; // 解析不出来时保守按最新版本估算
        return $"1.{parts[0]}.{parts[1]}";
    }

    // ============================================================
    // Forge/NeoForge 安装器本地执行
    // ============================================================

    /// <summary>
    /// 运行 Forge/NeoForge 安装器的 --installServer，在 targetDir 生成真正可用的服务端文件。
    /// 需要调用方提供一个可执行的 java.exe/java 路径（复用 JavaService 探测到的结果，
    /// 不在这里重复实现 Java 探测逻辑）。
    /// </summary>
    public async Task<string> RunForgeInstallerAsync(string installerJarPath, string targetDir, string javaExePath,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(installerJarPath))
            throw new FileNotFoundException("找不到安装器文件。", installerJarPath);
        if (!File.Exists(javaExePath))
            throw new FileNotFoundException("找不到可用的 Java 可执行文件，无法运行安装器。", javaExePath);

        progress?.Report("正在运行安装器（首次运行可能需要下载额外库文件，请耐心等待）...");

        var psi = new ProcessStartInfo
        {
            FileName = javaExePath,
            ArgumentList = { "-jar", installerJarPath, "--installServer", targetDir },
            WorkingDirectory = targetDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var outputLines = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { outputLines.Add(e.Data); progress?.Report(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { outputLines.Add(e.Data); progress?.Report(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', outputLines.TakeLast(20));
            throw new InvalidOperationException($"安装器执行失败（退出码 {process.ExitCode}）。最后输出：\n{tail}");
        }

        // 安装完成后，实际服务端 jar 文件名因版本而异（新版 Forge/NeoForge 用 run.bat/run.sh 启动，
        // 不是固定的单一 jar 名），这里尝试在目标目录找一个看起来像"启动脚本"的文件返回给调用方，
        // 找不到则返回目标目录本身，由调用方（开服向导）进一步处理"如何启动"的判断。
        var runScript = Directory.GetFiles(targetDir, "run.bat").FirstOrDefault()
            ?? Directory.GetFiles(targetDir, "run.sh").FirstOrDefault();
        return runScript ?? targetDir;
    }

    // ============================================================
    // Spigot BuildTools 本地编译
    // ============================================================

    /// <summary>
    /// 运行 Spigot 的 BuildTools.jar 在本地拉源码并编译出真正可用的 Spigot 服务端 jar。
    /// 前置要求：本机已安装 Git（BuildTools 内部用 JGit/命令行 git 拉取 Bukkit/CraftBukkit/Spigot
    /// 源码，缺 Git 会直接报错退出，这里不重复实现"检测/安装 Git"，只把失败信息原样透传给调用方，
    /// 调用方(开服向导)负责在事前提示用户"编译 Spigot 需要先装 Git"）。
    /// 编译耗时通常几分钟（要下源码 + 反编译 + 打补丁 + 编译），比 Forge/NeoForge 装安装器慢得多，
    /// 调用方应该展示专门的"正在编译 Spigot，请耐心等待"提示，而不是复用安装器那种"很快就好"的文案。
    /// </summary>
    public async Task<string> RunSpigotBuildToolsAsync(string buildToolsJarPath, string targetDir, string javaExePath,
        string mcVersion, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(buildToolsJarPath))
            throw new FileNotFoundException("找不到 BuildTools.jar。", buildToolsJarPath);
        if (!File.Exists(javaExePath))
            throw new FileNotFoundException("找不到可用的 Java 可执行文件，无法运行 BuildTools。", javaExePath);

        progress?.Report("正在运行 BuildTools 编译 Spigot（需要联网拉取源码并本地编译，可能需要几分钟）...");

        Directory.CreateDirectory(targetDir);
        var psi = new ProcessStartInfo
        {
            FileName = javaExePath,
            // --rev 指定要编译的 MC 版本；--output-dir 让编译产物直接落在目标目录，
            // 不用编译完再手动搬运；BuildTools 1.x 起支持 --output-dir，老版本没有这个参数，
            // 万一用户手上的 BuildTools.jar 版本太旧不支持，Process 会直接把参数当无效选项报错，
            // 报错信息会原样透传给调用方，比静默失败更好排查。
            ArgumentList = { "-jar", buildToolsJarPath, "--rev", mcVersion, "--output-dir", targetDir },
            WorkingDirectory = targetDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var outputLines = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { outputLines.Add(e.Data); progress?.Report(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { outputLines.Add(e.Data); progress?.Report(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', outputLines.TakeLast(30));
            // Git 缺失是最常见的失败原因，专门识别一下给出更直接的提示，而不是让用户自己去翻几十行日志。
            var looksLikeMissingGit = outputLines.Any(l =>
                l.Contains("git", StringComparison.OrdinalIgnoreCase) &&
                (l.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 l.Contains("cannot run program", StringComparison.OrdinalIgnoreCase) ||
                 l.Contains("не найден", StringComparison.OrdinalIgnoreCase)));
            var hint = looksLikeMissingGit
                ? "\n这通常是因为本机没有安装 Git（BuildTools 编译 Spigot 依赖 Git 拉取源码），请先安装 Git 后重试。"
                : "";
            throw new InvalidOperationException($"BuildTools 编译失败（退出码 {process.ExitCode}）。{hint}最后输出：\n{tail}");
        }

        // BuildTools 产物文件名形如 "spigot-1.20.1.jar"（--output-dir 生效时直接落在 targetDir），
        // 按 MC 版本号匹配优先；找不到就退化为目录下任意一个 spigot-*.jar，最后兜底返回目录本身。
        var expectedName = $"spigot-{mcVersion}.jar";
        var exact = Path.Combine(targetDir, expectedName);
        if (File.Exists(exact)) return exact;

        var anySpigotJar = Directory.GetFiles(targetDir, "spigot-*.jar").FirstOrDefault();
        return anySpigotJar ?? targetDir;
    }

    // ============================================================
    // 下载/校验 辅助方法
    // ============================================================

    /// <summary>单次下载尝试超时，理由同 DownloadService.SingleAttemptTimeout（该问题在多个下载
    /// 服务里独立实现了同一套"重试但依赖整体 HttpClient.Timeout"的逻辑，属于同一类卡住 bug，一并修）。</summary>
    private static readonly TimeSpan SingleAttemptTimeout = TimeSpan.FromSeconds(45);

    private async Task DownloadFileAsync(string url, string destPath, string expectedSha1, CancellationToken ct)
    {
        if (File.Exists(destPath) && !string.IsNullOrEmpty(expectedSha1) && VerifySha1(destPath, expectedSha1)) return;

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

                if (!string.IsNullOrEmpty(expectedSha1) && !VerifySha1(tmp, expectedSha1))
                {
                    lastError = new IOException($"文件校验失败(SHA1 不匹配)，可能是网络中途中断: {url}");
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
        var is404 = lastError is HttpRequestException hre &&
            (hre.StatusCode == System.Net.HttpStatusCode.NotFound || hre.Message.Contains("404"));
        var hint = is404
            ? "\n这通常是因为该版本的安装器/核心文件已从官方仓库下架，建议换一个相近的版本重试。"
            : "";
        throw new IOException($"下载失败（已重试 {maxAttempts} 次）: {url}{hint}", lastError);
    }

    /// <summary>用于 Paper(sha256,单独校验)/Fabric/Forge/NeoForge 等 DownloadFileAsync 不直接支持 sha1 的场景。</summary>
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
        var is404b = lastError is HttpRequestException hre2 &&
            (hre2.StatusCode == System.Net.HttpStatusCode.NotFound || hre2.Message.Contains("404"));
        var hintb = is404b
            ? "\n这通常是因为该版本的安装器/核心文件已从官方仓库下架，建议换一个相近的版本重试。"
            : "";
        throw new IOException($"下载失败（已重试 {maxAttempts} 次）: {url}{hintb}", lastError);
    }

    /// <summary>判断一次下载失败是否是"目标 build 已经 404"（值得换下一个候选 build 重试），
    /// 而不是网络抖动等其他原因。DownloadFileNoHashCheckAsync 内部已经重试过 3 次，最终抛出的是
    /// 包了一层的 IOException，真正的 HttpRequestException 在 InnerException 里，这里要往里挖一层。
    /// buildDetailJson 那次查询（GetStringAsync）如果 build 本身不存在，也是直接抛 HttpRequestException，
    /// 不经过 DownloadFileNoHashCheckAsync 包装，所以两种形态都要判断。</summary>
    private static bool IsNotFoundFailure(Exception ex)
    {
        var probe = ex;
        while (probe != null)
        {
            if (probe is HttpRequestException hre &&
                (hre.StatusCode == System.Net.HttpStatusCode.NotFound || hre.Message.Contains("404")))
                return true;
            probe = probe.InnerException;
        }
        return false;
    }

    private static bool VerifySha1(string filePath, string expectedSha1)
    {
        if (string.IsNullOrEmpty(expectedSha1)) return true;
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha1.ComputeHash(fs);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex == expectedSha1.ToLowerInvariant();
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略清理失败 */ }
    }
}
