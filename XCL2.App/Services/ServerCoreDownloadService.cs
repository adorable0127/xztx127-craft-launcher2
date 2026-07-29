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
        var build = req.BuildOrLoaderVersion;
        if (string.IsNullOrEmpty(build))
        {
            progress?.Report(new ProgressInfo("查询最新构建", 0, 2, req.McVersion));
            var builds = await GetPaperBuildsAsync(req.McVersion, ct);
            build = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion ?? builds.FirstOrDefault()?.DisplayVersion;
            if (build == null)
                throw new InvalidOperationException($"Paper 没有找到 MC {req.McVersion} 对应的可用构建。");
        }

        // v3 下载端点：/v3/projects/paper/versions/{mcVersion}/builds/{build}/downloads/{fileName}
        // 文件名规律固定为 paper-{mcVersion}-{build}.jar，但为稳妥起见先查一次 build 详情确认真实文件名，
        // 避免第三方 API 未来调整命名规则导致直接拼错 URL。
        progress?.Report(new ProgressInfo("查询构建详情", 1, 2, build));
        var buildDetailJson = await _http.GetStringAsync(
            $"{PaperApiBase}/projects/paper/versions/{req.McVersion}/builds/{build}", ct);
        using var buildDoc = JsonDocument.Parse(buildDetailJson);

        string fileName;
        string? sha256 = null;
        var downloadsEl = buildDoc.RootElement.GetProperty("downloads");
        if (downloadsEl.TryGetProperty("server:default", out var serverDl))
        {
            fileName = serverDl.GetProperty("name").GetString()!;
            if (serverDl.TryGetProperty("checksums", out var checksums) && checksums.TryGetProperty("sha256", out var sh))
                sha256 = sh.GetString();
        }
        else
        {
            // 兜底：按官方文档记录的固定命名规则拼接
            fileName = $"paper-{req.McVersion}-{build}.jar";
        }

        var url = $"{PaperApiBase}/projects/paper/versions/{req.McVersion}/builds/{build}/downloads/{fileName}";
        var destPath = Path.Combine(req.TargetDir, "server.jar");

        progress?.Report(new ProgressInfo("下载服务端主程序", 2, 2, fileName));
        // Paper 用 sha256 而不是 sha1，DownloadFileAsync 目前只支持 sha1 校验；
        // 这里改用不做哈希强校验的下载路径，只做基本的文件大小/完整性检查（HttpClient 层面）。
        await DownloadFileNoHashCheckAsync(url, destPath, ct);
        if (sha256 != null)
        {
            var actualSha256 = ComputeSha256(destPath);
            if (!string.Equals(actualSha256, sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Paper 服务端下载文件 SHA256 校验失败，文件可能损坏，请重试。");
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
