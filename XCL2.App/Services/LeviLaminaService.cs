using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// LeviLamina（基岩版模组加载器）安装服务。从 BedrockBoot 的 LeviLaminaInstaller 原样移植
/// （除界面与 Octokit 换成本地镜像请求外，下载/缓存/依赖解析/安装逻辑保持一致）。
///
/// 流程：
///   1. 按 BDS 版本查 levilamina-client-version-db 找兼容的 LeviLamina 版本；
///   2. 下载 LeviLamina 源码包（GitHub 多源回退），解压读 tooth.json；
///   3. 从 tooth.json 解析依赖：LeviLamina 本体 / CrashLogger / bedrock-runtime-data / PreLoader，
///      逐个从对应 GitHub 仓库 Releases 里找匹配版本的发布资产；
///   4. 所有依赖并行下载（带缓存索引，重复安装不重复下载），失败自动重试；
///   5. 安装：LeviLamina 解压到 mods/，CrashLogger 解压到 mods/LeviLamina/，
///      bedrock-runtime-data 解压到服务端根目录，PreLoader.dll 装到
///      config/BedrockBoot2/mods/ 并登记进 mods.json（预加载注入）。
/// </summary>
public class LeviLaminaService
{
    // ===== 源（与 BedrockBoot 一致）=====
    public const string LeviLaminaVersionDbUrl =
        "https://raw.githubusercontent.com/LiteLDev/levilamina-client-version-db/refs/heads/main/version-db.json";

    public const string LeviLaminaSourceUrl =
        "https://github.com/LiteLDev/LeviLamina/archive/refs/tags/v{version}.zip";

    // ===== 目录（跟 BedrockBoot 的 PathList 对应，落在本启动器的数据目录下）=====
    private static string CacheFolder => Path.Combine(App.DataDir, "BedrockBoot.LeviLamina", "Cache");
    private static string SourceFolder => Path.Combine(App.DataDir, "BedrockBoot.LeviLamina", "Source");
    private static string TempFolder => Path.Combine(App.DataDir, "BedrockBoot.LeviLamina", "Temp");

    private enum DependenciesType { LeviLamina, CrashLogger, BedrockRtd, PreLoader }

    // ===== tooth.json 模型 =====
    private sealed class ToothManifest
    {
        [JsonPropertyName("tooth")] public string Tooth { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("variants")] public List<VariantEntry> Variants { get; set; } = new();
    }

    private sealed class VariantEntry
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("assets")] public List<AssetEntry> Assets { get; set; } = new();
        [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
    }

    private sealed class AssetEntry
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("urls")] public List<string> Urls { get; set; } = new();
    }

    // ===== config/BedrockBoot2/mods.json 里的 ModInfo（PreLoader 预加载登记）=====
    private sealed class ModInfo
    {
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("isPreLoad")] public bool IsPreLoad { get; set; }
        [JsonPropertyName("injectDelay")] public int InjectDelay { get; set; }
    }

    // ===== 缓存 =====
    private readonly ConcurrentDictionary<string, bool> _downloadedFiles = new();
    private readonly string _cacheIndexFile;
    private readonly bool _useCache;

    public LeviLaminaService(bool useCache = true)
    {
        _useCache = useCache;
        _cacheIndexFile = Path.Combine(CacheFolder, "cache_index.txt");
        InitializeCache();
    }

    private void InitializeCache()
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            Directory.CreateDirectory(SourceFolder);
            Directory.CreateDirectory(TempFolder);

            if (_useCache && File.Exists(_cacheIndexFile))
            {
                var cachedFiles = File.ReadAllLines(_cacheIndexFile)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();
                foreach (var file in cachedFiles)
                    _downloadedFiles[file] = true;
            }
        }
        catch (Exception ex)
        {
            ReportError($"初始化缓存失败: {ex.Message}");
            throw;
        }
    }

    private void SaveToCacheIndex(string fileName)
    {
        try
        {
            if (!_useCache) return;
            _downloadedFiles[fileName] = true;
            File.AppendAllText(_cacheIndexFile, fileName + Environment.NewLine);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("保存 LeviLamina 缓存索引失败", ex);
        }
    }

    // ==================== 版本 ====================

    /// <summary>按 BDS 版本号找兼容的 LeviLamina 版本列表（version-db 多源回退）。</summary>
    public async Task<List<string>> GetVersionsAsync(string bdsVersion, CancellationToken ct = default)
    {
        var json = await FetchJsonWithMirrorsAsync(LeviLaminaVersionDbUrl, ct);
        if (json == null)
            throw new InvalidOperationException("获取 LeviLamina 版本库失败（网络不可用）");

        try
        {
            var targetVersion = bdsVersion.Replace(".", "");
            var result = new List<string>();
            using var doc = JsonDocument.Parse(json);
            // 结构：{ "versions": { "<bds版本>": ["<ll版本>", ...], ... } }
            if (doc.RootElement.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in versions.EnumerateObject())
                {
                    if (targetVersion.StartsWith(prop.Name.Replace(".", "")) && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        result = prop.Value.EnumerateArray()
                            .Select(v => v.GetString())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Cast<string>()
                            .ToList();
                    }
                }
            }

            if (result.Count <= 0)
            {
                var errorMsg = $"版本 {bdsVersion} 不适用于 LeviLamina";
                ReportError(errorMsg);
                throw new NullReferenceException(errorMsg);
            }
            return result;
        }
        catch (Exception ex)
        {
            ReportError($"获取版本列表失败: {ex.Message}");
            throw;
        }
    }

    // ==================== 安装 ====================

    /// <summary>
    /// 把 LeviLamina（及全部依赖）安装进指定 BDS 服务端目录。
    /// serverDir 是 BDS 根目录（含 bedrock_server.exe），bdsVersion 是服务端版本号，
    /// lmaVersion 是要装的 LeviLamina 版本（null 表示自动挑兼容列表里最新的）。
    /// </summary>
    public async Task InstallAsync(string serverDir, string bdsVersion, string? lmaVersion,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var progressAdapter = new InstallerProgressAdapter(progress);

        var versions = await GetVersionsAsync(bdsVersion, ct);
        if (string.IsNullOrWhiteSpace(lmaVersion)) lmaVersion = versions[0];
        if (!versions.Contains(lmaVersion))
            throw new InvalidOperationException($"LeviLamina 版本 {lmaVersion} 不适用于 BDS {bdsVersion}。可选：{string.Join(", ", versions)}");

        EnsureModsLink(serverDir);

        progressAdapter.Report("开始安装 LeviLamina...", 0);
        try
        {
            // 检查缓存中是否已有该版本
            if (_useCache && CheckCachedVersion(lmaVersion))
            {
                progressAdapter.Report("使用缓存安装...", 10);
                if (await InstallFromCache(serverDir, lmaVersion, progressAdapter, ct))
                {
                    progressAdapter.Report("缓存安装完成", 100);
                    return;
                }
                progressAdapter.Report("缓存安装失败，开始重新下载...", 20);
            }

            await DownloadAndInstallFresh(serverDir, lmaVersion, progressAdapter, ct);
            progressAdapter.Report("安装完成", 100);
        }
        catch (Exception ex)
        {
            ReportError($"安装过程中出现错误: {ex.Message}");
            throw;
        }
    }

    /// <summary>mods 目录符号链接处理（与 BedrockBoot 一致：普通文件夹删除，mods 实际指向
    /// config/BedrockBoot2/mods，卸载时删链接即可保留文件）。创建失败静默忽略。</summary>
    private static void EnsureModsLink(string serverDir)
    {
        var modsPath = Path.Combine(serverDir, "mods");
        var targetModsPath = Path.Combine(serverDir, "config", "BedrockBoot2", "mods");

        if (!Directory.Exists(targetModsPath))
            Directory.CreateDirectory(targetModsPath);

        if (Directory.Exists(modsPath))
        {
            try
            {
                var linkInfo = new DirectoryInfo(modsPath);
                var isLink = (linkInfo.Attributes & FileAttributes.ReparsePoint) != 0;
                if (!isLink)
                {
                    Directory.Delete(modsPath, true);
                }
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogFallback("检查 mods 目录失败，尝试删除", ex);
                try { Directory.Delete(modsPath, true); } catch { }
            }
        }

        try { Directory.CreateSymbolicLink(modsPath, targetModsPath); } catch { }
    }

    private bool CheckCachedVersion(string lmaVersion)
    {
        try
        {
            var sourcePath = Path.Combine(SourceFolder, $"{lmaVersion}.zip");
            if (!File.Exists(sourcePath)) return false;

            var tmpFolder = Path.Combine(TempFolder, $"ll_{lmaVersion}");
            if (!Directory.Exists(tmpFolder)) return false;

            var llJson = Path.Combine(tmpFolder, $"LeviLamina-{lmaVersion}", "tooth.json");
            if (!File.Exists(llJson)) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> InstallFromCache(string serverDir, string lmaVersion,
        InstallerProgressAdapter progressAdapter, CancellationToken ct)
    {
        try
        {
            var tmpFolder = Path.Combine(TempFolder, $"ll_{lmaVersion}");
            var llJson = Path.Combine(tmpFolder, $"LeviLamina-{lmaVersion}", "tooth.json");
            if (!File.Exists(llJson))
            {
                ReportError("找不到缓存中的 tooth.json 文件");
                return false;
            }

            var llManifest = LoadToothManifest(llJson);
            var depInfo = llManifest.Variants[1];

            if (string.IsNullOrEmpty(depInfo.Label) || depInfo.Label != "client")
            {
                ReportError("缓存文件无效：非客户端文件");
                return false;
            }

            var deps = await GetDependenceDownloadUrlsAsync(depInfo, ct);

            // 添加 LeviLamina 本身的 URL
            var allUrls = depInfo.Assets
                .SelectMany(asset => asset.Urls
                    .Select(url => url
                        .Replace("{{tooth}}", llManifest.Tooth)
                        .Replace("{{version}}", llManifest.Version)))
                .ToList();
            allUrls.ForEach(url => deps[DependenciesType.LeviLamina] = url);

            // 检查所有依赖文件（包括 LeviLamina 本身）是否都存在
            foreach (var dep in deps)
            {
                var fileName = GetCleanFileNameFromUri(dep.Value);
                var cachePath = Path.Combine(CacheFolder, fileName);
                if (!File.Exists(cachePath))
                {
                    ErrorPresenter.LogFallback("LeviLamina 缓存文件缺失", new Exception($"{fileName} - {dep.Key}"));
                    return false;
                }
            }

            await ProcessDependenciesFromCache(serverDir, deps, ct);
            return true;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("LeviLamina 缓存安装失败", ex);
            return false;
        }
    }

    private async Task ProcessDependenciesFromCache(string serverDir, Dictionary<DependenciesType, string> deps, CancellationToken ct)
    {
        var tasks = deps.Select(dep => Task.Run(() =>
        {
            var fileName = GetCleanFileNameFromUri(dep.Value);
            var cachePath = Path.Combine(CacheFolder, fileName);
            ProcessDependencyFile(dep.Key, cachePath, serverDir);
        }, ct)).ToList();

        await Task.WhenAll(tasks);
    }

    private async Task DownloadAndInstallFresh(string serverDir, string lmaVersion,
        InstallerProgressAdapter progressAdapter, CancellationToken ct)
    {
        progressAdapter.Report("下载 LeviLamina 源码...", 0);

        var sourceUrl = LeviLaminaSourceUrl.Replace("{version}", lmaVersion);
        var sourcePath = Path.Combine(SourceFolder, $"{lmaVersion}.zip");

        await DownloadWithRetry(sourceUrl, sourcePath, "LeviLamina 清单", 3, ct);
        progressAdapter.Report("下载 LeviLamina 源码...", 80);

        // 提取源码
        var tmpFolder = Path.Combine(TempFolder, $"ll_{lmaVersion}");
        if (Directory.Exists(tmpFolder))
            Directory.Delete(tmpFolder, true);

        try
        {
            ZipFile.ExtractToDirectory(sourcePath, tmpFolder, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            ReportError($"解压源码失败: {ex.Message}");
            throw;
        }

        var llJson = Path.Combine(tmpFolder, $"LeviLamina-{lmaVersion}", "tooth.json");
        if (!File.Exists(llJson))
        {
            ReportError("找不到 tooth.json 文件");
            throw new FileNotFoundException("找不到 tooth.json 文件", llJson);
        }

        progressAdapter.Report("下载 LeviLamina 源码...", 100);

        var llManifest = LoadToothManifest(llJson);
        var depInfo = llManifest.Variants[1];

        if (string.IsNullOrEmpty(depInfo.Label) || depInfo.Label != "client")
        {
            ReportError("非客户端文件");
            throw new InvalidOperationException("非客户端文件");
        }

        var deps = await GetDependenceDownloadUrlsAsync(depInfo, ct);

        // 添加 LeviLamina 本身的 URL
        var allUrls = depInfo.Assets
            .SelectMany(asset => asset.Urls
                .Select(url => url
                    .Replace("{{tooth}}", llManifest.Tooth)
                    .Replace("{{version}}", llManifest.Version)))
            .ToList();
        allUrls.ForEach(url => deps[DependenciesType.LeviLamina] = url);

        await DownloadAndProcessDependencies(serverDir, deps, progressAdapter, ct);
    }

    private async Task DownloadAndProcessDependencies(string serverDir, Dictionary<DependenciesType, string> deps,
        InstallerProgressAdapter progressAdapter, CancellationToken ct)
    {
        var tasks = new List<Task>();
        var errors = new ConcurrentBag<Exception>();

        foreach (var dep in deps)
        {
            var fileName = GetCleanFileNameFromUri(dep.Value);
            var cachePath = Path.Combine(CacheFolder, fileName);

            var task = Task.Run(async () =>
            {
                try
                {
                    // 检查缓存
                    if (_useCache && File.Exists(cachePath))
                    {
                        progressAdapter.Report($"{dep.Key} 使用缓存", 100);
                    }
                    else
                    {
                        await DownloadWithRetry(dep.Value, cachePath, $"{dep.Key}", 3, ct);
                        SaveToCacheIndex(fileName);
                    }

                    await ProcessDependencyFile(dep.Key, cachePath, serverDir);
                    progressAdapter.Report($"{dep.Key} 处理完成", 100);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
                    ReportError($"{dep.Key} 处理失败: {ex.Message}");
                }
            }, ct);

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        if (errors.Count > 0)
            throw new AggregateException("一个或多个组件处理失败", errors.ToList());
    }

    private Task ProcessDependencyFile(DependenciesType depType, string filePath, string serverDir)
    {
        switch (depType)
        {
            case DependenciesType.LeviLamina:
                ZipFile.ExtractToDirectory(filePath, Path.Combine(serverDir, "mods"), overwriteFiles: true);
                break;

            case DependenciesType.CrashLogger:
                var targetDir = Path.Combine(serverDir, "mods", "LeviLamina");
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                ZipFile.ExtractToDirectory(filePath, targetDir, overwriteFiles: true);
                break;

            case DependenciesType.BedrockRtd:
                var rtdFile = Path.Combine(serverDir, "bedrock_runtime_data");
                if (File.Exists(rtdFile))
                    File.Delete(rtdFile);
                ZipFile.ExtractToDirectory(filePath, serverDir, overwriteFiles: true);
                break;

            case DependenciesType.PreLoader:
                return InstallPreLoader(filePath, serverDir);
        }
        return Task.CompletedTask;
    }

    private Task InstallPreLoader(string preloaderPath, string serverDir)
    {
        try
        {
            var preloaderFile = Path.Combine(serverDir, "config", "BedrockBoot2", "mods", "PreLoader.dll");
            var modsConfigPath = Path.Combine(serverDir, "config", "BedrockBoot2", "mods.json");

            var modsDir = Path.Combine(serverDir, "config", "BedrockBoot2", "mods");
            if (!Directory.Exists(modsDir))
                Directory.CreateDirectory(modsDir);

            // 更新 mods.json
            if (File.Exists(modsConfigPath))
            {
                try
                {
                    var conf = JsonSerializer.Deserialize<List<ModInfo>>(File.ReadAllText(modsConfigPath))
                               ?? new List<ModInfo>();
                    if (!conf.Any(m => m.File.EndsWith("PreLoader.dll")))
                    {
                        conf.Add(new ModInfo { File = preloaderFile, IsPreLoad = true, InjectDelay = 0 });
                        File.WriteAllText(modsConfigPath, JsonSerializer.Serialize(conf, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch { /* mods.json 损坏则跳过登记 */ }
            }

            // 删除旧文件
            if (File.Exists(preloaderFile))
                File.Delete(preloaderFile);

            // 提取 PreLoader
            var tmpPath = Path.Combine(TempFolder, $"preload_{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(preloaderPath, tmpPath, overwriteFiles: true);
            var sourceFile = Path.Combine(tmpPath, "bin", "PreLoader.dll");

            if (File.Exists(sourceFile))
            {
                File.Move(sourceFile, preloaderFile, true);
            }
            else
            {
                throw new FileNotFoundException("在压缩包中找不到 PreLoader.dll", sourceFile);
            }

            try { Directory.Delete(tmpPath, true); } catch { }
        }
        catch (Exception ex)
        {
            ReportError($"安装 PreLoader 失败: {ex.Message}");
            throw;
        }
        return Task.CompletedTask;
    }

    // ==================== 依赖解析 ====================

    private async Task<Dictionary<DependenciesType, string>> GetDependenceDownloadUrlsAsync(
        VariantEntry delInfo, CancellationToken ct)
    {
        var notNecessarilyDel = new List<string>
        {
            "github.com/LiteLDev/levilamina-loc#client",
            "github.com/LiteLDev/PeEditor"
        };

        var result = new Dictionary<DependenciesType, string>();
        var errors = new ConcurrentBag<Exception>();
        var tasks = new List<Task>();

        foreach (var dep in delInfo.Dependencies)
        {
            if (notNecessarilyDel.Contains(dep.Key))
                continue;

            var depParts = dep.Key.Split('/');
            if (depParts.Length < 3)
                continue;

            var orgName = depParts[1];
            var repNameWithSuffix = depParts[2];
            var repName = repNameWithSuffix.Split('#')[0];

            tasks.Add(ProcessDependencyAsync(dep.Value, orgName, repName, result, errors, ct));
        }

        await Task.WhenAll(tasks);

        if (errors.Count > 0)
        {
            ReportError($"解析依赖失败: {string.Join("; ", errors.Select(e => e.Message))}");
            throw new AggregateException("解析依赖时发生错误", errors.ToList());
        }

        return result;
    }

    private async Task ProcessDependencyAsync(string depVersion, string orgName, string repName,
        Dictionary<DependenciesType, string> result, ConcurrentBag<Exception> errors, CancellationToken ct)
    {
        try
        {
            var versionPattern = depVersion.Replace("*", "");

            var releases = await GetGithubReleasesAsync(orgName, repName, ct);
            var matchingRelease = releases.FirstOrDefault(x =>
                x.TagName.Contains(versionPattern, StringComparison.OrdinalIgnoreCase) ||
                (x.Name != null && x.Name.Contains(versionPattern, StringComparison.OrdinalIgnoreCase)));

            if (matchingRelease == null)
                throw new Exception($"未找到匹配 {versionPattern} 的发布版本: {repName}");

            if (matchingRelease.Assets.Count == 0)
                throw new Exception($"发布版本没有资源文件: {repName} {matchingRelease.TagName}");

            var depType = repName switch
            {
                "CrashLogger" => (DependenciesType?)DependenciesType.CrashLogger,
                "bedrock-runtime-data" => DependenciesType.BedrockRtd,
                "PreLoader" => DependenciesType.PreLoader,
                _ => null
            };

            if (depType.HasValue)
            {
                lock (result)
                {
                    result[depType.Value] = matchingRelease.Assets[0].DownloadUrl;
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(new Exception($"处理依赖 {repName} 失败: {ex.Message}", ex));
        }
    }

    // ==================== GitHub Releases（Octokit 换成多源镜像请求）====================

    private sealed class GithubReleaseInfo
    {
        public string TagName { get; set; } = "";
        public string? Name { get; set; }
        public List<GithubReleaseAssetInfo> Assets { get; set; } = new();
    }

    private sealed class GithubReleaseAssetInfo
    {
        public string DownloadUrl { get; set; } = "";
    }

    private static readonly string[] GithubApiMirrors =
    {
        "https://api.github.com",
        "https://gh-proxy.com/https://api.github.com",
        "https://ghproxy.com/https://api.github.com",
        "https://mirror.ghproxy.com/https://api.github.com",
        "https://ghp.ci/https://api.github.com",
    };

    private static async Task<List<GithubReleaseInfo>> GetGithubReleasesAsync(string owner, string repo, CancellationToken ct)
    {
        foreach (var apiBase in GithubApiMirrors)
        {
            try
            {
                using var http = CreateHttp();
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                var json = await http.GetStringAsync($"{apiBase}/repos/{owner}/{repo}/releases", ct);
                var releases = ParseGithubReleases(json);
                if (releases.Count > 0) return releases;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 试下一个镜像 */ }
        }
        return new List<GithubReleaseInfo>();
    }

    private static List<GithubReleaseInfo> ParseGithubReleases(string json)
    {
        var list = new List<GithubReleaseInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                var r = new GithubReleaseInfo();
                if (rel.TryGetProperty("tag_name", out var t)) r.TagName = t.GetString() ?? "";
                if (rel.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) r.Name = n.GetString();
                if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        if (a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            var url = u.GetString();
                            if (!string.IsNullOrEmpty(url))
                                r.Assets.Add(new GithubReleaseAssetInfo { DownloadUrl = url });
                        }
                    }
                }
                if (!string.IsNullOrEmpty(r.TagName)) list.Add(r);
            }
        }
        catch { /* 解析失败返回空 */ }
        return list;
    }

    // ==================== 下载（多源 + 重试 + 进度）====================

    private async Task DownloadWithRetry(string url, string path, string description, int maxRetries, CancellationToken ct)
    {
        int retryCount = 0;

        while (retryCount <= maxRetries)
        {
            try
            {
                await DownloadGithubFileAsync(url, path, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (retryCount < maxRetries)
            {
                retryCount++;
                ErrorPresenter.LogFallback($"{description} 下载失败，正在重试 ({retryCount}/{maxRetries})", ex);
                await Task.Delay(1000 * retryCount, ct);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
            catch (Exception ex)
            {
                ReportError($"下载{description}失败: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>GitHub 文件下载：原始地址 + 各加速镜像源逐个尝试。</summary>
    private async Task DownloadGithubFileAsync(string fileUrl, string savePath, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var url in BedrockModService.EnumerateGithubSources(fileUrl))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(savePath);
                await src.CopyToAsync(dst, ct);
                if (File.Exists(savePath) && new FileInfo(savePath).Length > 0) return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex; }
        }
        throw new InvalidOperationException(
            $"从 {fileUrl} 下载失败，所有下载源均不可用。" +
            (lastError != null ? $"\n详细信息：{lastError.Message}" : ""));
    }

    // ==================== 工具 ====================

    private static string GetCleanFileNameFromUri(string uriString)
    {
        try
        {
            var uri = new Uri(uriString);
            var fileName = Path.GetFileName(uri.AbsolutePath);

            if (string.IsNullOrEmpty(fileName))
            {
                var segments = uri.Segments.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                if (segments.Count > 0)
                {
                    fileName = segments.Last().TrimEnd('/');
                }
            }

            if (string.IsNullOrEmpty(fileName))
            {
                fileName = $"file_{Math.Abs(uriString.GetHashCode())}";
            }

            return fileName;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("解析文件名失败", ex);
            return $"file_{Guid.NewGuid():N}";
        }
    }

    private static ToothManifest LoadToothManifest(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return JsonSerializer.Deserialize<ToothManifest>(doc.RootElement.GetRawText()) ?? new ToothManifest();
    }

    private static async Task<string?> FetchJsonWithMirrorsAsync(string url, CancellationToken ct)
    {
        var mirrors = new[]
        {
            "https://cdn.jsdelivr.net/gh/LiteLDev/levilamina-client-version-db@main/version-db.json",
            "https://fastly.jsdelivr.net/gh/LiteLDev/levilamina-client-version-db@main/version-db.json",
            "https://gcore.jsdelivr.net/gh/LiteLDev/levilamina-client-version-db@main/version-db.json",
            "https://ghp.ci/https://raw.githubusercontent.com/LiteLDev/levilamina-client-version-db/refs/heads/main/version-db.json",
            "https://ghproxy.com/https://raw.githubusercontent.com/LiteLDev/levilamina-client-version-db/refs/heads/main/version-db.json",
            "https://mirror.ghproxy.com/https://raw.githubusercontent.com/LiteLDev/levilamina-client-version-db/refs/heads/main/version-db.json",
        };

        foreach (var u in new[] { url }.Concat(mirrors))
        {
            try
            {
                using var http = CreateHttp();
                http.Timeout = TimeSpan.FromSeconds(15);
                var json = await http.GetStringAsync(u, ct);
                if (!string.IsNullOrWhiteSpace(json)) return json;
            }
            catch { /* 试下一个源 */ }
        }
        return null;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
        return http;
    }

    private void ReportError(string message)
        => ErrorPresenter.LogFallback("LeviLamina: " + message, null);

    /// <summary>把 InstallerProgress 的 Status 状态转成本服务用的进度文本。</summary>
    private sealed class InstallerProgressAdapter
    {
        private readonly IProgress<ProgressInfo>? _progress;

        public InstallerProgressAdapter(IProgress<ProgressInfo>? progress) => _progress = progress;

        public void Report(string message, int progress)
            => _progress?.Report(new ProgressInfo(message, progress, 100, "LeviLamina"));
    }
}