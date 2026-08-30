using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 基岩版（Bedrock Edition）模组/插件下载服务（接口从 BedrockBoot 移植，不含界面）。
///
/// ===== 来源 =====
/// 1. 模组市场列表：拉 roundstudio 的插件市场接口（market-api.roundstudio.top/api/plugins），
///    每条对应一个 GitHub 仓库（owner/name），即一个 LeviLamina 模组；
/// 2. 模组发布包：从该仓库的 GitHub Releases 拿发布资产（.llplugin / .jar / .dll / .zip 等）；
/// 3. 下载：GitHub 直连不通时自动换 GitHub 加速镜像源（ghproxy 等）逐个回退。
///
/// ===== 安装位置 =====
/// LeviLamina 模组放到 BDS 服务端目录下的 mods/ 文件夹里。
///
/// ===== 说明 =====
/// 只提供"列表 / 发布信息 / 下载"的服务接口，界面后续按需接入。
/// </summary>
public class BedrockModService
{
    public const string MarketApiUrl = "https://market-api.roundstudio.top/api/plugins";

    /// <summary>GitHub 下载加速源：{url} 被替换成完整 GitHub 原始地址，{route} 被替换成去掉域名的路径。</summary>
    public static readonly (string Name, string Pattern)[] GithubMirrorSources =
    {
        ("GitHub 官方源", "{url}"),
        ("加速源 ①", "https://github1.roundstudio.top/{url}"),
        ("gh.tianpao.top", "https://gh.tianpao.top/{url}"),
        ("gh.tiouo.cc", "https://gh.tiouo.cc/{route}"),
        ("gh-proxy.com", "https://gh-proxy.com/{url}"),
        ("gh-proxy.net", "https://gh-proxy.net/{url}"),
        ("gh-proxy.org", "https://gh-proxy.org/{url}"),
        ("gitproxy.click", "https://gitproxy.click/{url}"),
    };

    // ==================== 模型 ====================

    public sealed class BedrockModInfo
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("iconUrl")] public string IconUrl { get; set; } = "";
        [JsonPropertyName("repositoryUrl")] public string RepositoryUrl { get; set; } = "";
        [JsonPropertyName("repositoryOwner")] public string RepositoryOwner { get; set; } = "";
        [JsonPropertyName("repositoryName")] public string RepositoryName { get; set; } = "";
        [JsonPropertyName("pluginName")] public string PluginName { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("icon")] public string Icon { get; set; } = "";
        [JsonPropertyName("labels")] public List<string> Labels { get; set; } = new();
    }

    public sealed class BedrockModRelease
    {
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime? PublishedAt { get; set; }
        public List<BedrockModReleaseAsset> Assets { get; set; } = new();
    }

    public sealed class BedrockModReleaseAsset
    {
        public string Name { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public long Size { get; set; }
    }

    private sealed class MarketResponse
    {
        [JsonPropertyName("plugins")] public List<BedrockModInfo> Plugins { get; set; } = new();
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
        return http;
    }

    // ==================== 模组市场列表 ====================

    /// <summary>获取基岩版模组市场列表。</summary>
    public async Task<List<BedrockModInfo>> GetModListAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = CreateHttp();
            http.Timeout = TimeSpan.FromSeconds(15);
            var json = await http.GetStringAsync(MarketApiUrl, ct);
            var resp = JsonSerializer.Deserialize<MarketResponse>(json);
            return resp?.Plugins ?? new List<BedrockModInfo>();
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("获取基岩版模组市场列表失败", ex);
            return new List<BedrockModInfo>();
        }
    }

    // ==================== GitHub Releases ====================

    /// <summary>拉取某个模组仓库的 GitHub Releases（api.github.com + 代理镜像回退）。</summary>
    public async Task<List<BedrockModRelease>> GetModReleasesAsync(string owner, string repo, CancellationToken ct = default)
    {
        var apiUrls = new[]
        {
            $"https://api.github.com/repos/{owner}/{repo}/releases",
            $"https://gh-proxy.com/https://api.github.com/repos/{owner}/{repo}/releases",
            $"https://ghproxy.com/https://api.github.com/repos/{owner}/{repo}/releases",
            $"https://mirror.ghproxy.com/https://api.github.com/repos/{owner}/{repo}/releases",
            $"https://ghp.ci/https://api.github.com/repos/{owner}/{repo}/releases",
        };

        foreach (var url in apiUrls)
        {
            try
            {
                using var http = CreateHttp();
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                var json = await http.GetStringAsync(url, ct);
                var releases = ParseReleases(json);
                if (releases.Count > 0) return releases;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 试下一个镜像 */ }
        }
        return new List<BedrockModRelease>();
    }

    private static List<BedrockModRelease> ParseReleases(string json)
    {
        var list = new List<BedrockModRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                var r = new BedrockModRelease();
                if (rel.TryGetProperty("tag_name", out var t)) r.Tag = t.GetString() ?? "";
                if (rel.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) r.Name = n.GetString() ?? "";
                if (rel.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String)
                    if (DateTime.TryParse(p.GetString(), out var dt)) r.PublishedAt = dt;

                if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var asset = new BedrockModReleaseAsset();
                        if (a.TryGetProperty("name", out var an)) asset.Name = an.GetString() ?? "";
                        if (a.TryGetProperty("browser_download_url", out var au)) asset.DownloadUrl = au.GetString() ?? "";
                        if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number) asset.Size = sz.GetInt64();
                        if (!string.IsNullOrEmpty(asset.Name)) r.Assets.Add(asset);
                    }
                }

                if (!string.IsNullOrEmpty(r.Tag)) list.Add(r);
            }
        }
        catch { /* 解析失败返回空 */ }
        return list;
    }

    // ==================== 下载（多源回退） ====================

    /// <summary>
    /// 下载一个 GitHub 发布资产到指定 BDS 服务端目录下的 mods/ 文件夹（自动多个加速源回退）。
    /// </summary>
    public async Task<string> DownloadModAsync(string assetUrl, string installDir, string? fileName = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var modsDir = Path.Combine(installDir, "mods");
        Directory.CreateDirectory(modsDir);

        var name = fileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Path.GetFileName(new Uri(assetUrl).AbsolutePath); } catch { }
        }
        if (string.IsNullOrWhiteSpace(name)) name = $"mod-{Guid.NewGuid():N}.llplugin";
        var dest = Path.Combine(modsDir, name);

        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
        {
            progress?.Report(new ProgressInfo($"模组已存在：{name}", 1, 1, dest));
            return dest;
        }

        Exception? lastError = null;
        foreach (var url in EnumerateGithubSources(assetUrl))
        {
            try
            {
                progress?.Report(new ProgressInfo($"正在下载模组 {name}", 0, 1, url));

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(dest);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                        progress?.Report(new ProgressInfo($"正在下载模组 {name}",
                            (int)(done / 1024), (int)(total / 1024),
                            $"{done / 1048576} MB / {total / 1048576} MB"));
                }

                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                {
                    progress?.Report(new ProgressInfo($"模组下载完成：{name}", 1, 1, dest));
                    return dest;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex; }
        }

        try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        throw new InvalidOperationException(
            $"模组 {name} 下载失败，所有下载源均不可用。" +
            (lastError != null ? $"\n详细信息：{lastError.Message}" : ""));
    }

    /// <summary>把 GitHub 原始地址展开成"原始 + 各加速源"的一串候选 URL。</summary>
    public static IEnumerable<string> EnumerateGithubSources(string fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)) yield break;
        var route = fileUrl.Replace("https://github.com/", "").Replace("http://github.com/", "");
        foreach (var (_, pattern) in GithubMirrorSources)
            yield return pattern.Replace("{url}", fileUrl).Replace("{route}", route);
    }
}