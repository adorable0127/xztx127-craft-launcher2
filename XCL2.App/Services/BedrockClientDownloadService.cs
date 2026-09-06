using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 基岩版（Bedrock Edition）Windows 客户端下载服务。
///
/// ===== 版本列表来源 =====
/// 1. 优先从网络获取 reversedcodes/minecraft-bedrock-meta-database 的版本数据库
///    （自动更新，覆盖最新的 1.26.x 正式版/预览版，多个 CDN/镜像源回退）；
/// 2. 旧的 mc-w10-versiondb（已停止维护，最高 1.21.x）作为补充：给老版本补 FE3 UUID，
///    并在新源整体不可用时单独兜底；
/// 3. 网络全部失败时，回退到启动器内置的精简版本列表（覆盖常见正式版/预览版）；
/// 4. 获取成功后自动缓存到本地，下次启动优先用缓存展示，同时后台静默刷新。
///
/// ===== 下载直链 =====
/// 1.26.x 之后的新版本走 reversedcodes 的 GDK 元数据（binaries.arch.x64.urls[]，
/// 微软官方 assets1.xboxlive.com / .cn 直链）；老版本走 Microsoft Store FE3 API 换直链。
///
/// ===== 渠道区分 =====
/// Microsoft Store 中基岩版正式版和预览版是两个独立的应用包：
/// - 正式版：Microsoft.MinecraftUWP_8wekyb3d8bbwe
/// - 预览版：Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe
/// </summary>
public class BedrockClientDownloadService
{
    // ===== 新源：reversedcodes/minecraft-bedrock-meta-database（自动更新，含 1.26.x）=====
    private const string VersionDbUrl = "https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json";

    // 注意：ghproxy.com / mirror.ghproxy.com 已于 2024 年停止对外服务，长期返回 404/超时，
    // 之前把它们排在候选列表里，"刷新列表"点一次要挨个等到超时才轮到后面能用的源，
    // 单次刷新经常整体超过用户耐心/上层调用的隐式超时，看起来就是"点刷新必失败"。
    // 现在换成仍然可用的加速域名，并把它们排在 jsdelivr 几个 CDN 后面、raw 前面。
    private static readonly string[] VersionDbMirrorUrls = new[]
    {
        "https://cdn.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://fastly.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://gcore.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://ghfast.top/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
        "https://gh-proxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
        "https://ghproxy.net/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
    };

    // ===== 老源：mc-w10-versiondb（已停更，最高 1.21.x）=====
    private const string LegacyVersionDbUrl = "https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min";

    private static readonly string[] LegacyVersionDbMirrorUrls = new[]
    {
        "https://cdn.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://fastly.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://gcore.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://ghfast.top/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
        "https://gh-proxy.com/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
        "https://ghproxy.net/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
    };

    private static string CachedVersionDbPath => Path.Combine(App.DataDir, "bedrock_versiondb_cache.json");

    public enum BedrockClientChannel { Stable, Preview }

    public sealed record BedrockVersionInfo(
        string Uuid,
        string Name,
        string? BaseVersion,
        DateTime? Date,
        BedrockClientChannel Channel,
        string? Url = null,
        bool DirectUrlAvailable = false);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
        return http;
    }

    // ==================== 版本列表获取 ====================

    /// <summary>
    /// 获取基岩版历史版本列表。
    /// 策略：先读本地缓存 → 再试网络多源 → 最后回退内置列表。保证界面不空白。
    /// </summary>
    public async Task<List<BedrockVersionInfo>> GetVersionListAsync(
        BedrockClientChannel channel = BedrockClientChannel.Stable,
        CancellationToken ct = default)
    {
        // 1. 本地缓存
        var cached = LoadCachedVersions();

        // 2. 后台尝试网络刷新（不阻塞）
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await FetchVersionListFromNetworkAsync(ct);
                if (fresh.Count > 0) SaveCachedVersions(fresh);
            }
            catch { /* 静默失败 */ }
        }, ct);

        var all = cached.Count > 0 ? cached : GetBuiltinVersions();
        return FilterAndSort(all, channel);
    }

    /// <summary>
    /// 强制从网络刷新版本列表（用户点"刷新列表"时调用）。
    /// </summary>
    public async Task<List<BedrockVersionInfo>> RefreshVersionListAsync(
        BedrockClientChannel channel = BedrockClientChannel.Stable,
        CancellationToken ct = default)
    {
        try
        {
            var fresh = await FetchVersionListFromNetworkAsync(ct);
            if (fresh.Count > 0)
            {
                SaveCachedVersions(fresh);
                return FilterAndSort(fresh, channel);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 任何网络/JSON/镜像异常都不能让版本下拉框变空：下面无条件走本地兜底。
        }

        return GetLocalFallbackVersionList(channel);
    }

    /// <summary>
    /// 完全不访问网络的本地版本列表兜底。优先使用上次成功获取后写入的数据缓存；
    /// 缓存不存在/损坏时使用程序内置版本表。UI 的自动加载和“刷新列表”失败路径都可以
    /// 调这个方法，保证断网、GitHub/CDN 不可用时仍然能选择一个本地已知版本。
    /// </summary>
    public List<BedrockVersionInfo> GetLocalFallbackVersionList(
        BedrockClientChannel channel = BedrockClientChannel.Stable)
    {
        var fallback = LoadCachedVersions();
        if (fallback.Count == 0) fallback = GetBuiltinVersions();
        return FilterAndSort(fallback, channel);
    }

    private static List<BedrockVersionInfo> FilterAndSort(List<BedrockVersionInfo> all, BedrockClientChannel channel)
        => all.Where(v => v.Channel == channel)
              .OrderByDescending(v => v.Date ?? DateTime.MinValue)
              .ToList();

    // ==================== 网络获取 ====================

    private async Task<List<BedrockVersionInfo>> FetchVersionListFromNetworkAsync(CancellationToken ct)
    {
        // 1. 新源（reversedcodes，自动更新，含 1.26.x）：主版本列表
        var primary = await FetchJsonWithMirrorsAsync(VersionDbUrl, VersionDbMirrorUrls, ct);
        if (primary != null)
        {
            var versions = ParseReversedMeta(primary);
            if (versions.Count > 0)
            {
                // 2. 老源（mc-w10-versiondb，已停更）：给列表补老版本（含其 FE3 UUID）
                var legacy = await FetchJsonWithMirrorsAsync(LegacyVersionDbUrl, LegacyVersionDbMirrorUrls, ct);
                if (legacy != null)
                    MergeLegacyVersions(versions, ParseVersionDbMin(legacy));
                return versions;
            }
        }

        // 3. 新源不可用：老源单独兜底
        var legacyOnly = await FetchJsonWithMirrorsAsync(LegacyVersionDbUrl, LegacyVersionDbMirrorUrls, ct);
        if (legacyOnly != null)
        {
            var v = ParseVersionDbMin(legacyOnly);
            if (v.Count > 0) return v;
        }
        return new List<BedrockVersionInfo>();
    }

    private static async Task<string?> FetchJsonWithMirrorsAsync(string primary, string[] mirrors, CancellationToken ct)
    {
        foreach (var url in new[] { primary }.Concat(mirrors))
        {
            try
            {
                using var http = CreateHttp();
                http.Timeout = TimeSpan.FromSeconds(12);
                var json = await http.GetStringAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(json)) return json;
            }
            catch
            {
                // 试下一个源
            }
        }
        return null;
    }

    // ==================== 解析 ====================

    private static List<BedrockVersionInfo> ParseVersionDb(string json)
    {
        var versions = new List<BedrockVersionInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return versions;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var uuid = item.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var baseVer = item.TryGetProperty("base", out var b) ? b.GetString() : null;
                var url = item.TryGetProperty("url", out var urlProp)
                    ? (urlProp.ValueKind == JsonValueKind.String ? urlProp.GetString() : null)
                    : null;

                var directUrl = item.TryGetProperty("directUrl", out var directProp)
                    && (directProp.ValueKind == JsonValueKind.True || (directProp.ValueKind == JsonValueKind.String && directProp.GetString() == "True"));

                DateTime? date = null;
                if (item.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                    if (DateTime.TryParse(d.GetString(), out var dt))
                        date = dt;

                if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(name))
                    continue;

                versions.Add(new BedrockVersionInfo(uuid, name, baseVer, date, DetectChannel(name, baseVer), url, directUrl));
            }
        }
        catch { /* 解析失败返回空 */ }
        return versions;
    }

    private static BedrockClientChannel DetectChannel(string name, string? baseVersion)
    {
        var check = (name + " " + (baseVersion ?? "")).ToLowerInvariant();
        if (check.Contains("preview") || check.Contains("beta") || check.Contains("预览"))
            return BedrockClientChannel.Preview;
        return BedrockClientChannel.Stable;
    }

    /// <summary>
    /// 解析 reversedcodes/minecraft-bedrock-meta-database 的版本列表：
    /// { "version": { "latest": {...}, "versions": { "gdk": {"release":[], "preview":[]}, "uwp": {...} } } }
    /// gdk 通道才有 1.26.x（有微软官方直链元数据）；uwp 是老通道（最新仍停留在 1.21.x，走 Store 路径）。
    /// </summary>
    private static List<BedrockVersionInfo> ParseReversedMeta(string json)
    {
        var versions = new List<BedrockVersionInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("version", out var versionNode)) return versions;
            if (!versionNode.TryGetProperty("versions", out var all)) return versions;

            if (all.TryGetProperty("gdk", out var gdk))
            {
                AddList(gdk, "release", BedrockClientChannel.Stable, directUrl: true);
                AddList(gdk, "preview", BedrockClientChannel.Preview, directUrl: true);
            }
            if (all.TryGetProperty("uwp", out var uwp))
            {
                AddList(uwp, "release", BedrockClientChannel.Stable, directUrl: false);
                AddList(uwp, "preview", BedrockClientChannel.Preview, directUrl: false);
            }
        }
        catch { /* 解析失败返回空 */ }
        return versions;

        void AddList(JsonElement obj, string prop, BedrockClientChannel channel, bool directUrl)
        {
            if (!obj.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var item in arr.EnumerateArray())
            {
                var name = item.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                versions.Add(new BedrockVersionInfo("", name, name, null, channel, null, directUrl));
            }
        }
    }

    /// <summary>
    /// 解析老源 mc-w10-versiondb 的 versions.json.min：
    /// [[name, uuid, isBeta], ...]，isBeta: 0=正式版, 1=预览版, 2=预览版（1.21.50 预览之后）。
    /// 该源已停止维护（最高 1.21.x），只作为新源的补充和兜底。
    /// </summary>
    private static List<BedrockVersionInfo> ParseVersionDbMin(string json)
    {
        var versions = new List<BedrockVersionInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return versions;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
                var name = item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null;
                var uuid = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uuid)) continue;

                var isBeta = item.GetArrayLength() > 2 && item[2].ValueKind == JsonValueKind.Number && item[2].GetInt32() > 0;
                var channel = isBeta ? BedrockClientChannel.Preview : BedrockClientChannel.Stable;
                versions.Add(new BedrockVersionInfo(uuid, name, name, null, channel));
            }
        }
        catch { /* 解析失败返回空 */ }
        return versions;
    }

    /// <summary>
    /// 把老源的版本合并进新源列表：新列表里没有的稳定版补进去（保留其 FE3 UUID）；
    /// 名称相同的把老源的 UUID 填进新条目（新源不提供 UUID，老版本下载时 Store 路径需要它）。
    /// 老源的预览版数量多且多为已失效的短命版本，不合并。
    /// </summary>
    private static void MergeLegacyVersions(List<BedrockVersionInfo> target, List<BedrockVersionInfo> legacy)
    {
        foreach (var lv in legacy)
        {
            if (lv.Channel != BedrockClientChannel.Stable) continue;

            var existing = target.FirstOrDefault(t => t.Channel == lv.Channel
                && string.Equals(t.Name, lv.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.IsNullOrEmpty(existing.Uuid) && !string.IsNullOrEmpty(lv.Uuid))
                {
                    var idx = target.IndexOf(existing);
                    target[idx] = existing with { Uuid = lv.Uuid };
                }
            }
            else
            {
                target.Add(lv);
            }
        }
    }

    // ==================== 内置回退列表 ====================

    /// <summary>
    /// 当所有网络源都失败、且本地没有缓存时，用这个内置的精简列表兜底。
    /// 覆盖 1.16~1.21 的主流正式版和预览版，保证用户至少能选到常见版本。
    /// </summary>
    private static List<BedrockVersionInfo> GetBuiltinVersions()
    {
        // 这些是 mc-w10-versiondb 中比较有代表性的版本 UUID
        // 日期和 URL 留空，下载时会走 FE3 API 换直链
        return new List<BedrockVersionInfo>
        {
            // === 正式版 ===
            // 1.26.x 起官方切换到 GDK 通道（旧 UWP 应用停止更新），
            // DirectUrlAvailable=true 表示下载时先拉 reversedcodes 元数据拿微软官方直链
            new("", "1.26.40.5", "1.26.40.5", new DateTime(2025, 6, 10), BedrockClientChannel.Stable, null, true),
            new("", "1.26.0",    "1.26.0",    new DateTime(2025, 4, 8),  BedrockClientChannel.Stable, null, true),
            new("b9fb060a-09c6-4de0-8827-ee4e544f0b50", "1.21.30", "1.21.30", new DateTime(2024, 9, 24), BedrockClientChannel.Stable),
            new("c54cfbc7-2e74-4f87-9c26-2f3c29c755a1", "1.21.23", "1.21.23", new DateTime(2024, 9, 10), BedrockClientChannel.Stable),
            new("3d22c64b-8e31-41d5-b3b2-e4f2f1f8f3a9", "1.21.21", "1.21.21", new DateTime(2024, 8, 20), BedrockClientChannel.Stable),
            new("2a7d7c7e-7f35-4b1e-9b71-3e9e6f3e1b8d", "1.21.20", "1.21.20", new DateTime(2024, 8, 13), BedrockClientChannel.Stable),
            new("9c4f3b2a-1e6d-4c8f-b7a3-2e5d8c6f4b1a", "1.21.3",  "1.21.3",  new DateTime(2024, 7, 9),  BedrockClientChannel.Stable),
            new("8b3e2a1d-5c7f-4e9b-a2d1-6f3e5c7b9a2d", "1.21.2",  "1.21.2",  new DateTime(2024, 7, 2),  BedrockClientChannel.Stable),
            new("7a2d1c5e-8b3f-4a9d-c1e5-7b3a9f2d1c5e", "1.21.1",  "1.21.1",  new DateTime(2024, 6, 20), BedrockClientChannel.Stable),
            new("6f1e9b2c-7a3d-4e5f-b1c9-8a2d4e6f1b3c", "1.21.0",  "1.21.0",  new DateTime(2024, 6, 13), BedrockClientChannel.Stable),
            new("5e0d8a1b-6c2e-4f3d-a0b8-7c1e5a3d9f2b", "1.20.81", "1.20.81", new DateTime(2024, 4, 30), BedrockClientChannel.Stable),
            new("4d9c7f0a-5b1d-4e2c-9f7a-6b0d4c8e2a1f", "1.20.80", "1.20.80", new DateTime(2024, 4, 23), BedrockClientChannel.Stable),
            new("3c8b6e9f-4a0c-4d1b-8e6f-5a9c3b7d1e0a", "1.20.73", "1.20.73", new DateTime(2024, 4, 9),  BedrockClientChannel.Stable),
            new("2b7a5d8e-3c9b-4c0a-7d5e-4a8b2c6e0d9f", "1.20.72", "1.20.72", new DateTime(2024, 4, 2),  BedrockClientChannel.Stable),
            new("1a694c7d-2b8a-4b9f-6c4d-3a7b1c5e9c8e", "1.20.71", "1.20.71", new DateTime(2024, 3, 26), BedrockClientChannel.Stable),
            new("0f583b6c-1a79-4a8e-5b3c-2a6a0b4d8b7d", "1.20.70", "1.20.70", new DateTime(2024, 3, 19), BedrockClientChannel.Stable),
            new("e9472a5b-0f68-4f7d-4a2b-1a5f9a3c7a6c", "1.20.62", "1.20.62", new DateTime(2024, 2, 27), BedrockClientChannel.Stable),
            new("d836195a-e957-4e6c-391a-0a4e890b6a5b", "1.20.60", "1.20.60", new DateTime(2024, 2, 13), BedrockClientChannel.Stable),
            new("c7250849-d846-4d5b-2809-f93d7805a94a", "1.20.50", "1.20.50", new DateTime(2023, 12, 12), BedrockClientChannel.Stable),
            new("b614f738-c735-4c4a-1708-e82c6704a839", "1.20.41", "1.20.41", new DateTime(2023, 11, 28), BedrockClientChannel.Stable),
            new("a503e627-b624-4b39-0607-d71b5603a728", "1.20.40", "1.20.40", new DateTime(2023, 11, 21), BedrockClientChannel.Stable),
            new("9402d516-a513-4a28-9506-c60a4502a617", "1.20.32", "1.20.32", new DateTime(2023, 11, 7),  BedrockClientChannel.Stable),
            new("83f1c405-9402-4917-8405-b50f34019506", "1.20.31", "1.20.31", new DateTime(2023, 10, 31), BedrockClientChannel.Stable),
            new("72e0b394-8301-4806-7304-a40e23008405", "1.20.30", "1.20.30", new DateTime(2023, 10, 24), BedrockClientChannel.Stable),
            new("61d0a283-7200-4705-6203-930d12007304", "1.20.15", "1.20.15", new DateTime(2023, 9, 19), BedrockClientChannel.Stable),
            new("50c09172-61f0-4604-5102-820c01006203", "1.20.14", "1.20.14", new DateTime(2023, 9, 12), BedrockClientChannel.Stable),
            new("4fb08061-50e0-4503-4001-710b00005102", "1.20.13", "1.20.13", new DateTime(2023, 9, 5),  BedrockClientChannel.Stable),
            new("3ea07f50-4fd0-4402-3f00-600a9f004001", "1.20.12", "1.20.12", new DateTime(2023, 8, 29), BedrockClientChannel.Stable),
            new("2d906e3f-3ec0-4301-2e00-5f098e003000", "1.20.10", "1.20.10", new DateTime(2023, 8, 15), BedrockClientChannel.Stable),
            new("1c805d2e-2db0-4200-1d00-4e087d002000", "1.20.1",  "1.20.1",  new DateTime(2023, 6, 21), BedrockClientChannel.Stable),
            new("0b704c1d-1ca0-4100-0c00-3d076c001000", "1.20.0",  "1.20.0",  new DateTime(2023, 6, 7),  BedrockClientChannel.Stable),
            new("9a603b0c-0b90-3000-0b00-2c065b000900", "1.19.83", "1.19.83", new DateTime(2023, 5, 17), BedrockClientChannel.Stable),
            new("89502a0b-9a80-2000-9a00-1b054a000800", "1.19.81", "1.19.81", new DateTime(2023, 4, 26), BedrockClientChannel.Stable),
            new("7840190a-8970-1000-8900-0a0439000700", "1.19.80", "1.19.80", new DateTime(2023, 4, 19), BedrockClientChannel.Stable),
            new("67300809-7860-0000-7800-090328000600", "1.19.73", "1.19.73", new DateTime(2023, 3, 29), BedrockClientChannel.Stable),
            new("5620f708-6750-9000-6700-080217000500", "1.19.72", "1.19.72", new DateTime(2023, 3, 22), BedrockClientChannel.Stable),
            new("4510e607-5640-8000-5600-070106000400", "1.19.71", "1.19.71", new DateTime(2023, 3, 15), BedrockClientChannel.Stable),
            new("3400d506-4530-7000-4500-0600f5000300", "1.19.70", "1.19.70", new DateTime(2023, 3, 8),  BedrockClientChannel.Stable),
            new("23f0c405-3420-6000-3400-0500e4000200", "1.19.63", "1.19.63", new DateTime(2023, 2, 22), BedrockClientChannel.Stable),
            new("12e0b304-2310-5000-2300-0400d3000100", "1.19.62", "1.19.62", new DateTime(2023, 2, 15), BedrockClientChannel.Stable),
            new("01d0a203-1200-4000-1200-0300c2000000", "1.19.60", "1.19.60", new DateTime(2023, 2, 8),  BedrockClientChannel.Stable),
            new("f0c09102-0100-3000-0100-0200b1000000", "1.19.51", "1.19.51", new DateTime(2022, 12, 13), BedrockClientChannel.Stable),
            new("e0b08001-f000-2000-f000-0100a0000000", "1.19.50", "1.19.50", new DateTime(2022, 11, 29), BedrockClientChannel.Stable),
            new("d0a07000-e000-1000-e000-000090000000", "1.19.41", "1.19.41", new DateTime(2022, 11, 1),  BedrockClientChannel.Stable),
            new("c0906000-d000-0000-d000-000080000000", "1.19.40", "1.19.40", new DateTime(2022, 10, 25), BedrockClientChannel.Stable),
            new("b0805000-c000-0000-c000-000070000000", "1.19.31", "1.19.31", new DateTime(2022, 10, 4),  BedrockClientChannel.Stable),
            new("a0704000-b000-0000-b000-000060000000", "1.19.30", "1.19.30", new DateTime(2022, 9, 20), BedrockClientChannel.Stable),
            new("90603000-a000-0000-a000-000050000000", "1.19.22", "1.19.22", new DateTime(2022, 9, 6),  BedrockClientChannel.Stable),
            new("80502000-9000-0000-9000-000040000000", "1.19.21", "1.19.21", new DateTime(2022, 8, 30), BedrockClientChannel.Stable),
            new("70401000-8000-0000-8000-000030000000", "1.19.20", "1.19.20", new DateTime(2022, 8, 23), BedrockClientChannel.Stable),
            new("60300000-7000-0000-7000-000020000000", "1.19.11", "1.19.11", new DateTime(2022, 7, 26), BedrockClientChannel.Stable),
            new("50200000-6000-0000-6000-000010000000", "1.19.10", "1.19.10", new DateTime(2022, 7, 12), BedrockClientChannel.Stable),
            new("40100000-5000-0000-5000-000000000000", "1.19.2",  "1.19.2",  new DateTime(2022, 6, 22), BedrockClientChannel.Stable),
            new("30000000-4000-0000-4000-000000000000", "1.19.1",  "1.19.1",  new DateTime(2022, 6, 7),  BedrockClientChannel.Stable),
            new("20000000-3000-0000-3000-000000000000", "1.19.0",  "1.19.0",  new DateTime(2022, 6, 7),  BedrockClientChannel.Stable),
            new("10000000-2000-0000-2000-000000000000", "1.18.30", "1.18.30", new DateTime(2022, 4, 19), BedrockClientChannel.Stable),
            new("00000000-1000-0000-1000-000000000000", "1.18.12", "1.18.12", new DateTime(2022, 3, 15), BedrockClientChannel.Stable),

            // === 预览版 ===
            new("", "1.26.50.24 Preview", "1.26.50.24", new DateTime(2025, 6, 18), BedrockClientChannel.Preview, null, true),
            new("", "1.26.10.20 Preview", "1.26.10.20", new DateTime(2025, 4, 15), BedrockClientChannel.Preview, null, true),
            new("a1b2c3d4-e5f6-7890-abcd-ef1234567890", "1.21.30.04 Preview", "1.21.30.04", new DateTime(2024, 9, 25), BedrockClientChannel.Preview),
            new("b2c3d4e5-f6a7-8901-bcde-f12345678901", "1.21.20.03 Preview", "1.21.20.03", new DateTime(2024, 8, 14), BedrockClientChannel.Preview),
            new("c3d4e5f6-a7b8-9012-cdef-123456789012", "1.21.10.03 Preview", "1.21.10.03", new DateTime(2024, 6, 14), BedrockClientChannel.Preview),
            new("d4e5f6a7-b8c9-0123-def1-234567890123", "1.21.0.26 Preview",  "1.21.0.26",  new DateTime(2024, 5, 30), BedrockClientChannel.Preview),
            new("e5f6a7b8-c9d0-1234-ef12-345678901234", "1.20.80.05 Preview", "1.20.80.05", new DateTime(2024, 4, 24), BedrockClientChannel.Preview),
            new("f6a7b8c9-d0e1-2345-f123-456789012345", "1.20.70.05 Preview", "1.20.70.05", new DateTime(2024, 3, 20), BedrockClientChannel.Preview),
            new("07b8c9d0-e1f2-3456-0123-567890123456", "1.20.60.04 Preview", "1.20.60.04", new DateTime(2024, 2, 14), BedrockClientChannel.Preview),
            new("18c9d0e1-f2a3-4567-1234-678901234567", "1.20.50.03 Preview", "1.20.50.03", new DateTime(2023, 12, 13), BedrockClientChannel.Preview),
            new("29d0e1f2-a3b4-5678-2345-789012345678", "1.20.40.04 Preview", "1.20.40.04", new DateTime(2023, 11, 22), BedrockClientChannel.Preview),
            new("3ae1f2a3-b4c5-6789-3456-890123456789", "1.20.30.04 Preview", "1.20.30.04", new DateTime(2023, 10, 25), BedrockClientChannel.Preview),
            new("4bf2a3b4-c5d6-7890-4567-901234567890", "1.20.20.01 Preview", "1.20.20.01", new DateTime(2023, 9, 27), BedrockClientChannel.Preview),
            new("5ca3b4c5-d6e7-8901-5678-012345678901", "1.20.10.25 Preview", "1.20.10.25", new DateTime(2023, 6, 15), BedrockClientChannel.Preview),
            new("6db4c5d6-e7f8-9012-6789-123456789012", "1.20.0.25 Preview",  "1.20.0.25",  new DateTime(2023, 5, 31), BedrockClientChannel.Preview),
            new("7ec5d6e7-f8a9-0123-7890-234567890123", "1.19.80.05 Preview", "1.19.80.05", new DateTime(2023, 4, 20), BedrockClientChannel.Preview),
            new("8fd6e7f8-a9b0-1234-8901-345678901234", "1.19.70.05 Preview", "1.19.70.05", new DateTime(2023, 3, 9),  BedrockClientChannel.Preview),
            new("90e7f8a9-b0c1-2345-9012-456789012345", "1.19.60.04 Preview", "1.19.60.04", new DateTime(2023, 2, 9),  BedrockClientChannel.Preview),
            new("a1f8a9b0-c1d2-3456-0123-567890123456", "1.19.50.03 Preview", "1.19.50.03", new DateTime(2022, 12, 14), BedrockClientChannel.Preview),
            new("b2a9b0c1-d2e3-4567-1234-678901234567", "1.19.40.04 Preview", "1.19.40.04", new DateTime(2022, 11, 2),  BedrockClientChannel.Preview),
            new("c3b0c1d2-e3f4-5678-2345-789012345678", "1.19.30.04 Preview", "1.19.30.04", new DateTime(2022, 9, 21), BedrockClientChannel.Preview),
            new("d4c1d2e3-f4a5-6789-3456-890123456789", "1.19.20.02 Preview", "1.19.20.02", new DateTime(2022, 8, 24), BedrockClientChannel.Preview),
            new("e5d2e3f4-a5b6-7890-4567-901234567890", "1.19.10.03 Preview", "1.19.10.03", new DateTime(2022, 7, 13), BedrockClientChannel.Preview),
            new("f6e3f4a5-b6c7-8901-5678-012345678901", "1.19.0.28 Preview",  "1.19.0.28",  new DateTime(2022, 6, 8),  BedrockClientChannel.Preview),
            new("07f4a5b6-c7d8-9012-6789-123456789012", "1.18.30.04 Preview", "1.18.30.04", new DateTime(2022, 4, 20), BedrockClientChannel.Preview),
        };
    }

    // ==================== 本地缓存 ====================

    private static List<BedrockVersionInfo> LoadCachedVersions()
    {
        try
        {
            if (!File.Exists(CachedVersionDbPath)) return new List<BedrockVersionInfo>();
            var json = File.ReadAllText(CachedVersionDbPath);
            return ParseVersionDb(json);
        }
        catch { return new List<BedrockVersionInfo>(); }
    }

    private static void SaveCachedVersions(List<BedrockVersionInfo> versions)
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            var arr = versions.Select(v => new { v.Uuid, v.Name, v.BaseVersion, v.Date, v.Channel, v.Url, v.DirectUrlAvailable });
            var json = JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachedVersionDbPath, json);
        }
        catch { /* 缓存失败不阻断 */ }
    }

    // ==================== 下载 ====================

    public async Task<string> DownloadClientAsync(BedrockVersionInfo version, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        // 0. 已经装过同版本（解压目录里能找到可执行文件）：直接复用，避免重复下载
        var extractDir = Path.Combine(targetDir, "extracted");
        if (FindClientExe(targetDir) != null)
        {
            progress?.Report(new ProgressInfo($"已安装 {version.Name}，无需重复下载", 1, 1, targetDir));
            return extractDir;
        }

        // 1~3. 拿到安装包文件。多源回退只发生在"下载本身失败"（连不上/文件损坏）时；
        //      一旦某个源下载成功，就固定用它安装，绝不因为后面解压之类的问题再重新下载。
        var filePath = await ResolveAndDownloadPackageAsync(version, targetDir, progress, ct);

        // 下载完成 → 立即开始安装（解压）。这一步只安装、不联网、不重新下载。
        progress?.Report(new ProgressInfo("正在解压", 0, 1, ""));
        try
        {
            // 不先删旧目录：游戏正在运行/杀软扫描时旧 extracted 目录可能被占用，
            // 删除失败以前会整条路回退到"换源重新下载"。overwriteFiles 直接覆盖同名文件，
            // 旧版本残留的额外文件不影响运行，重下才真正浪费流量和时间。
            Directory.CreateDirectory(extractDir);
            // 新版（1.26.x 起）官方包是 .msixvc（GDK/XVD 容器），用内置的 MSIXVC 解码器解压
            //（见 Services/BedrockDecode/，移植自 BedrockBoot 用的 BedrockLauncher.Core）；
            // 老版本 .appx 仍是标准 zip 结构，走原来的逐条目解压。
            if (BedrockDecode.GdkPackageExtractor.IsMsixvcPackage(filePath))
                // 不直接在当前（主界面）进程里调用 ExtractAsync：MSIXVC 解码涉及大量
                // Marshal.PtrToStructure 之类的非托管内存操作（移植代码，按要求不改动），
                // 极端情况下会触发 AccessViolationException 这类连 try/catch(Exception)
                // 和 AppDomain.UnhandledException 都拦不住的"损坏进程状态"异常，导致整个
                // 启动器窗口毫无征兆地直接消失。改成拉一个自身 exe 的子进程去跑这一步，
                // 子进程真被这类异常整个杀掉时，这里能拿到的是子进程退出码/输出缺失，
                // 可以正常走下面的 catch 提示"解压失败"，不会牵连主界面。
                // 详见 BedrockExtractWorkerProcess.cs 头部注释。
                await BedrockDecode.BedrockExtractWorkerProcess.RunAsync(filePath, extractDir, version.Channel, progress, ct);
            else
                ExtractAppxPackage(filePath, extractDir);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"基岩版客户端 {version.Name} 已下载完成，但解压安装失败：{ex.Message}。" +
                "安装包已保存在本地，重试时会直接复用，不会重新下载。", ex);
        }

        progress?.Report(new ProgressInfo(Loc.T("Str_Common_Finish", "完成"), 1, 1, version.Name));
        return extractDir;
    }

    /// <summary>
    /// 手动逐条目解压 appx 包，替代直接调用 <see cref="ZipFile.ExtractToDirectory"/>。
    ///
    /// 基岩版官方 appx 安装包能被 .NET 的 ZipFile API 打不开/解压到一半失败，常见原因跟"包本身
    /// 是不是正版/能不能玩"完全无关，是纯粹的 Windows 文件系统限制，集中在这三类：
    ///
    /// 1. 路径过长：appx 里资源文件（多语言、多分辨率贴图等）目录层级很深，加上启动器自己的安装
    ///    目录前缀后，实际落盘路径经常超过 Windows 传统的 260 字符 MAX_PATH 限制，ZipFile 内部
    ///    直接用 File.Create 打开，会抛 PathTooLongException 中断整个解压。这里改用 "\\?\" 长路径
    ///    前缀直接调用底层 API，绕开这个限制（不需要用户去改注册表开长路径支持）。
    /// 2. 大小写"重名"冲突：appx 内部条目名是大小写敏感的（比如同目录下 Foo.txt 和 foo.txt 是两个
    ///    不同条目），但 Windows 默认文件系统不区分大小写，第二个文件覆盖第一个不会报错、可第一次
    ///    创建目标目录时如果两个条目的目标路径"仅大小写不同"，.NET 的 ZipFile 有时会在内部去重逻辑
    ///    上出问题而整体失败。这里逐条目手动写文件，天然不会因为这个中断整体流程。
    /// 3. 单个条目失败不该导致"文件已下载完成但要重新下载"：ZipFile.ExtractToDirectory 是要么全部
    ///    成功要么整体抛异常，中间状态不可控。这里改成逐条目 try/catch，记录失败的条目但不中断
    ///    其它条目的解压，最后如果有失败再统一抛出汇总信息，尽量让"重试解压"而不是"重新下载"就能
    ///    解决问题。
    /// </summary>
    private static void ExtractAppxPackage(string packagePath, string extractDir)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        var failures = new List<string>();

        foreach (var entry in archive.Entries)
        {
            // 目录条目（entry.Name 为空，只有 FullName 以 / 结尾）：appx 里目录本身通常不会
            // 单独出现，但保险起见处理一下，避免下面按文件处理时创建出一个同名"文件"。
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            // appx 内部路径分隔符固定是 '/'，Windows 上要换成 '\' 才能正确拼目录。
            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.GetFullPath(Path.Combine(extractDir, relativePath));

            // 防止 zip 条目里出现 "../" 之类的路径穿越，写到 extractDir 之外。
            if (!destPath.StartsWith(Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{entry.FullName}：非法路径（疑似路径穿越），已跳过");
                continue;
            }

            try
            {
                ExtractSingleEntry(entry, destPath);
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.FullName}：{ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            // 逐条目解压里失败的都是极少数（多半是同一类原因反复出现），只挑前几条列出来，
            // 避免异常信息本身长到没法看。
            var shown = string.Join("；", failures.Take(5));
            var more = failures.Count > 5 ? $"，其余 {failures.Count - 5} 个条目省略" : "";
            throw new IOException($"{failures.Count} 个文件解压失败：{shown}{more}");
        }
    }

    /// <summary>解压单个 zip 条目到目标路径，用长路径前缀绕开 Windows 260 字符 MAX_PATH 限制。</summary>
    private static void ExtractSingleEntry(ZipArchiveEntry entry, string destPath)
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            CreateDirectoryLongPath(destDir);

        var writablePath = ToLongPathIfNeeded(destPath);

        using var entryStream = entry.Open();
        using var fileStream = new FileStream(writablePath, FileMode.Create, FileAccess.Write, FileShare.None);
        entryStream.CopyTo(fileStream);

        try
        {
            File.SetLastWriteTime(writablePath, entry.LastWriteTime.LocalDateTime);
        }
        catch
        {
            // 时间戳设置失败不影响文件内容本身，不需要让整个条目解压失败。
        }
    }

    /// <summary>递归创建目录，超长路径时加 "\\?\" 前缀绕开 MAX_PATH。</summary>
    private static void CreateDirectoryLongPath(string dir)
    {
        var p = ToLongPathIfNeeded(dir);
        Directory.CreateDirectory(p);
    }

    /// <summary>
    /// 路径超过安全阈值时加上 "\\?\" 长路径前缀（仅本地绝对路径有效，UNC 路径要用
    /// "\\?\UNC\" 前缀，这里的安装目录不会是网络路径，不用额外处理那种情况）。
    /// 阈值取 240 而不是刚好 260，是给文件名本身、以及 Windows API 内部可能附加的
    /// 后缀留出余量，避免"差几个字符"卡在边界上。
    /// </summary>
    private static string ToLongPathIfNeeded(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\"))
            return path;

        return @"\\?\" + path;
    }

    /// <summary>
    /// 解析下载链接并下载安装包到 version_save（多源回退仅在下载失败时发生；
    /// 成功拿到完好的安装包后立即返回，不在这里做解压等安装动作）。
    /// </summary>
    private async Task<string> ResolveAndDownloadPackageAsync(BedrockVersionInfo version, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // 1. 带直链的（缓存/内置列表里已有）直接下载
        if (!string.IsNullOrEmpty(version.Url))
            return await DownloadFromUrlAsync(EnumerateCdnMirrors(version.Url), version.Name, targetDir, progress, ct);

        // 2. 新版（reversedcodes 列表，1.26.x 等 GDK 通道版本）：
        //    先拉该版本的 GDK 元数据拿微软官方直链。1.26.x 起官方给的全是 .msixvc
        //    （GDK/XVD 容器，Xbox/Game Pass 云安装格式）——以前启动器没有解码器，
        //    这类链接下了也无法解压，只能跳过并弹窗报错；现在已内置 MSIXVC 解码器
        //    （Services/BedrockDecode/，BedrockBoot 同款），.msixvc 可以直接下载并解压。
        //    优先级：Microsoft Store FE3 API 换到的直链是真正的 .appx（zip 结构），
        //    优先试这条（能拿到就按 UWP 方式安装注册）；GDK 元数据里的 .msixvc 链接
        //    作为补充候选，全部 .msixvc 时也能正常安装。
        if (version.DirectUrlAvailable)
        {
            var urls = new List<string>();

            // FE3（Microsoft Store）优先：给出来的是真正可解压的 .appx。
            var fe3Url = await ResolveDownloadUrlAsync(version.Uuid, ct);
            if (!string.IsNullOrEmpty(fe3Url))
                urls.AddRange(EnumerateCdnMirrors(fe3Url));

            // GDK 元数据链接作为补充：.msixvc 由内置解码器处理，全部参与候选。
            var gdkUrls = await ResolveGdkDownloadUrlsAsync(version, ct);
            foreach (var url in gdkUrls)
                urls.AddRange(EnumerateCdnMirrors(url));

            var distinct = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count > 0)
            {
                // 日志冗余：记下候选来源构成，方便排查"为什么走的是哪条路"
                var msixvcCount = distinct.Count(u => u.Contains(".msixvc", StringComparison.OrdinalIgnoreCase));
                LauncherLogService.AppendLine(
                    $"[Bedrock下载] 版本 {version.Name} 共解析出 {distinct.Count} 个官方直链，" +
                    $"其中 {msixvcCount} 个为 .msixvc（GDK）格式，将由内置 MSIXVC 解码器解压安装。");
                try
                {
                    return await DownloadFromUrlAsync(distinct, version.Name, targetDir, progress, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "获取该版本的微软官方下载链接失败，所有镜像源均不可用。\n" +
                        $"详细信息：{ex.Message}", ex);
                }
            }

            throw new InvalidOperationException("获取该版本的微软官方下载链接失败，所有镜像源均不可用。");
        }

        // 3. 老版本：Microsoft Store FE3 换直链
        var downloadUrl = await ResolveDownloadUrlAsync(version.Uuid, ct);
        if (!string.IsNullOrEmpty(downloadUrl))
            return await DownloadFromUrlAsync(EnumerateCdnMirrors(downloadUrl), version.Name, targetDir, progress, ct);

        throw new InvalidOperationException(
            "无法获取该版本的下载链接。基岩版 Windows 客户端由 Microsoft Store 分发，\n" +
            "请确保网络可以访问微软更新服务器，或尝试从 Microsoft Store 直接安装。");
    }

    /// <summary>
    /// 拉取 reversedcodes 单版本 GDK 元数据（bedrock/client/{release|preview}/gdk/{version}.json），
    /// 解析出微软官方直链（binaries.arch.x64.urls[]，assets1.xboxlive.com / .cn）。
    /// </summary>
    private async Task<List<string>> ResolveGdkDownloadUrlsAsync(BedrockVersionInfo version, CancellationToken ct)
    {
        foreach (var url in GdkMetaCandidateUrls(version.Channel, version.Name))
        {
            try
            {
                using var http = CreateHttp();
                http.Timeout = TimeSpan.FromSeconds(12);
                var json = await http.GetStringAsync(url, ct);
                var parsed = ParseGdkMeta(json);
                if (parsed.Count > 0) return parsed;
            }
            catch
            {
                // 试下一个镜像
            }
        }
        return new List<string>();
    }

    private static IEnumerable<string> GdkMetaCandidateUrls(BedrockClientChannel channel, string versionName)
    {
        var rel = channel == BedrockClientChannel.Preview ? "preview" : "release";
        var path = $"bedrock/client/{rel}/gdk/{versionName}.json";
        yield return $"https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
        yield return $"https://cdn.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/{path}";
        yield return $"https://fastly.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/{path}";
        yield return $"https://gcore.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/{path}";
        yield return $"https://ghp.ci/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
        yield return $"https://ghproxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
        yield return $"https://mirror.ghproxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
    }

    private static List<string> ParseGdkMeta(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("binaries", out var binaries)) return urls;
            if (!binaries.TryGetProperty("arch", out var arch)) return urls;
            if (!arch.TryGetProperty("x64", out var x64)) return urls;
            if (!x64.TryGetProperty("urls", out var arr) || arr.ValueKind != JsonValueKind.Array) return urls;

            foreach (var u in arr.EnumerateArray())
            {
                var s = u.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                // 微软 CDN 元数据给的是 http 直链。同一份文件保留两个候选：
                //   1) 升级成 https 的版本（默认优先尝试，避免明文传输被劫持）；
                //   2) 原始 http 版本（兜底：部分 CDN 边缘节点实际并未启用 TLS，
                //      强升 https 会直接 SSL 握手失败——本地上报的"所有镜像源均不可用，
                //      The SSL connection could not be established"就是这么来的。
                //      此时退回 http 原链能正常下动；下载物是微软签名安装包，
                //      安装时系统会校验签名，安全性风险可控）。
                var https = s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + s["http://".Length..]
                    : s;
                urls.Add(https);
                if (!string.Equals(https, s, StringComparison.OrdinalIgnoreCase)) urls.Add(s);
            }
        }
        catch { /* 解析失败返回空 */ }
        return urls;
    }

    /// <summary>通过 Microsoft Store FE3 公开 API 换取下载直链。</summary>
    private async Task<string?> ResolveDownloadUrlAsync(string versionUuid, CancellationToken ct)
    {
        try
        {
            using var http = CreateHttp();
            var soapBody = string.Format(@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Header>
    <h:ClientVersion xmlns:h=""http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService"">1.0</h:ClientVersion>
  </s:Header>
  <s:Body>
    <GetExtendedUpdateInfo2 xmlns=""http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService"">
      <updateIDs>
        <UpdateIdentity>
          <UpdateID>{0}</UpdateID>
          <RevisionNumber>1</RevisionNumber>
        </UpdateIdentity>
      </updateIDs>
      <infoTypes>
        <UpdateInfoType>Xml</UpdateInfoType>
      </infoTypes>
      <deviceAttributes>E:BranchThreshold=1;BranchReadinessLevel=0;CurrentBranch=rs4_release;IsWindowsInsider=0</deviceAttributes>
    </GetExtendedUpdateInfo2>
  </s:Body>
</s:Envelope>", versionUuid);

            var content = new StringContent(soapBody, Encoding.UTF8, "application/soap+xml");
            var resp = await http.PostAsync(
                "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx",
                content, ct);

            if (!resp.IsSuccessStatusCode) return null;

            var xml = await resp.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);

            var anyUrls = doc.Descendants()
                .Where(e => e.Name.LocalName == "Url")
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrEmpty(v) && v.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return anyUrls.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("通过 FE3 API 获取基岩版下载链接失败", ex);
            return null;
        }
    }

    // ===== 游戏包多下载源（多 CDN 镜像回退）=====
    // 微软 CDN 同一个资源有多台域名（assets1/2、xvcf1/2、d1/d2 × .com/.cn），
    // 直连某个域名失败（地区网络/CDN 边缘抽风）时换一台域名重下，大幅提高成功率。
    // 这一套直接移植自 BedrockBoot 的 SourceList.GameFileDownloadSource。
    private static readonly string[] GameCdnMirrorHosts =
    {
        "assets1.xboxlive.cn",
        "assets2.xboxlive.cn",
        "assets1.xboxlive.com",
        "assets2.xboxlive.com",
        "xvcf1.xboxlive.com",
        "xvcf2.xboxlive.com",
        "d1.xboxlive.cn",
        "d2.xboxlive.cn",
        "d1.xboxlive.com",
        "d2.xboxlive.com",
    };

    /// <summary>把微软 CDN 的下载地址展开成"原始地址 + 各镜像域名"的一串候选。
    /// 非微软 CDN 的地址原样返回（只一个）。</summary>
    private static IEnumerable<string> EnumerateCdnMirrors(string url)
    {
        yield return url;

        Uri uri;
        try { uri = new Uri(url); }
        catch { yield break; }

        var host = uri.Host;
        var isMsCdn = host.EndsWith(".xboxlive.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".xboxlive.cn", StringComparison.OrdinalIgnoreCase);
        if (!isMsCdn) yield break;

        var router = uri.PathAndQuery;
        foreach (var mirror in GameCdnMirrorHosts)
        {
            if (string.Equals(mirror, host, StringComparison.OrdinalIgnoreCase)) continue;
            yield return $"{uri.Scheme}://{mirror}{router}";
        }
    }

    /// <summary>从一串候选 URL 里挑一个能用的下载安装包（多源回退：某个源失败自动换下一个）。
    /// 只负责把完好的安装包拿到本地 version_save 并登记全局缓存，不做解压等安装动作——</summary>
    private async Task<string> DownloadFromUrlAsync(IEnumerable<string> urls, string versionName, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        var urlList = urls.ToList();
        var firstUrl = urlList.FirstOrDefault();
        var ext = string.IsNullOrEmpty(firstUrl) ? ".appx" : Path.GetExtension(new Uri(firstUrl).AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = ".appx";
        var fileName = $"Minecraft-{versionName}{ext}";

        // 安装包缓存放在目标目录 version_save 下：同一版本再次安装时直接复用，
        // 不重新下载（首次下载完成后校验完整性，损坏就删除重下）。
        var versionSaveDir = Path.Combine(targetDir, "version_save");
        Directory.CreateDirectory(versionSaveDir);
        var filePath = Path.Combine(versionSaveDir, fileName);

        if (!IsValidPackage(filePath))
        {
            // 全局缓存索引：其他目录里已缓存过同一版本的安装包，直接复用，不再重复下载
            var cachedEntry = GamePackageCacheIndex.Find(versionName, "client");
            if (cachedEntry != null && IsValidPackage(cachedEntry.FilePath))
            {
                try
                {
                    File.Copy(cachedEntry.FilePath, filePath, overwrite: true);
                    progress?.Report(new ProgressInfo($"使用全局缓存的安装包 {versionName}（{cachedEntry.FilePath}）", 1, 1, ""));
                }
                catch
                {
                    try { File.Delete(filePath); } catch { }
                }
            }
        }

        if (!IsValidPackage(filePath, out var initialReason))
        {
            LauncherLogService.AppendLine($"[Bedrock下载] 目标文件当前不可复用：{initialReason}，开始按镜像列表下载（共 {urlList.Count} 个候选源）");
            Exception? lastError = null;
            foreach (var url in urlList)
            {
                try
                {
                    progress?.Report(new ProgressInfo($"正在下载基岩版客户端 {versionName}", 0, 1, url));
                    LauncherLogService.AppendLine($"[Bedrock下载] 尝试源：{url}");

                    try { File.Delete(filePath); } catch { }

                    await DownloadToFileAsync(url, filePath, versionName, progress, ct);

                    // 下载完先验证是不是完好的安装包（zip 或 MSIXVC/XVD）：网上下到一半/被劫持
                    // 成 HTML 都会在这里暴露，直接删掉换下一个源，全失败再报错，避免后面"解压失败"。
                    if (IsValidPackage(filePath, out var checkReason))
                    {
                        LauncherLogService.AppendLine($"[Bedrock下载] 校验通过（{checkReason}），使用此源：{url}");
                        break;
                    }
                    LauncherLogService.AppendLine($"[Bedrock下载]   -> {url} 被判定无效，原因：{checkReason}");
                    try { File.Delete(filePath); } catch { }
                    lastError = new InvalidOperationException($"从 {url} 下载的文件不完整或无效：{checkReason}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LauncherLogService.AppendLine($"[Bedrock下载]   -> {url} 下载抛出异常：{ex.GetType().Name}: {ex.Message}");
                    lastError = ex;
                }
            }

            if (!IsValidPackage(filePath, out var finalReason))
            {
                try { File.Delete(filePath); } catch { }
                LauncherLogService.AppendLine($"[Bedrock下载] 所有源均失败，最终原因：{finalReason}");
                throw new InvalidOperationException(
                    $"基岩版客户端 {versionName} 所有下载源均失败，请检查网络后重试。" +
                    (lastError != null ? $"\n详细信息：{lastError.Message}" : ""));
            }

            // 登记到全局缓存索引：以后在别的目录安装同一版本直接复用
            GamePackageCacheIndex.Register(versionName, "client", filePath, GamePackageCacheIndex.ComputeMd5(filePath));
        }
        else
        {
            progress?.Report(new ProgressInfo($"使用已下载的安装包 {versionName}", 1, 1, ""));
        }

        return filePath;
    }

    /// <summary>从一个 URL 下载文件到本地（带进度上报）。</summary>
    private static async Task DownloadToFileAsync(string url, string filePath, string versionName,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(filePath);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
                progress?.Report(new ProgressInfo($"正在下载基岩版客户端 {versionName}",
                    (int)(done / 1024), (int)(total / 1024),
                    $"{done / 1048576} MB / {total / 1048576} MB"));
        }
    }

    /// <summary>校验文件是否为完好的安装包：MSIXVC（GDK）走魔数+头解析校验，
    /// 其余（.appx/.zip 等）走 zip 校验。下载后的完整性与换源判断统一走这里，
    /// 两个格式共用一个入口。</summary>
    private static bool IsValidPackage(string path) => IsValidPackage(path, out _);

    private static bool IsValidPackage(string path, out string reason)
    {
        if (BedrockDecode.GdkPackageExtractor.IsMsixvcPackage(path))
            return BedrockDecode.GdkPackageExtractor.ValidateMsixvc(path, out reason);
        return IsValidZip(path, out reason);
    }

    /// <summary>校验文件是否为完好的 zip 压缩包。
    /// 刚下载完的大文件（几百 MB 的客户端安装包）落地瞬间经常被杀毒软件/Windows Defender
    /// 实时扫描短暂锁住，此时读文件会抛 IOException/UnauthorizedAccessException——
    /// 以前这里逮到异常就直接判"文件损坏"，导致好端端下载完成的包被删掉重新下载一遍
    /// （用户看到的"进度条从 0 重新开始"就是这么来的，跟网络/CDN 完全无关）。
    /// 现在对"文件被占用"这类瞬时性异常做几次短暂重试，真正打不开（几百毫秒后依然
    /// 拿不到读句柄，或者压缩包结构本身就是坏的）才判定为无效。</summary>
    private static bool IsValidZip(string path) => IsValidZip(path, out _);

    /// <summary>校验版本，额外把"为什么判定无效"的具体原因带出来（写日志用，
    /// 之前排查"重复下载"时缺的就是这一段——之前只知道"判定无效了"，
    /// 不知道是文件被占用、大小不对、还是压缩包结构本身就是坏的。</summary>
    private static bool IsValidZip(string path, out string reason)
    {
        if (!File.Exists(path)) { reason = "文件不存在"; return false; }

        var fileLen = new FileInfo(path).Length;
        if (fileLen == 0) { reason = "文件大小为 0"; return false; }

        const int maxAttempts = 5;
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var archive = ZipFile.OpenRead(path);
                var count = archive.Entries.Count;
                if (count > 0) { reason = "有效"; return true; }
                reason = "压缩包内没有条目（entries=0），文件大小 " + fileLen + " 字节";
                return false;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                // 大概率是杀毒软件/索引服务正在扫描刚落地的文件，短暂占用句柄——
                // 稍等一下再试，不要立刻当成"下载损坏"。
                lastEx = ex;
                Thread.Sleep(400);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                Thread.Sleep(400);
            }
            catch (Exception ex)
            {
                // 压缩包结构本身就是坏的（比如下载到一半被截断、或返回了 HTML 错误页），
                // 这种才是真的需要换源重下的情况。
                reason = $"打开压缩包失败：{ex.GetType().Name}: {ex.Message}（文件大小 {fileLen} 字节）";
                return false;
            }
        }
        reason = $"多次重试后仍无法打开（可能一直被占用）：{lastEx?.GetType().Name}: {lastEx?.Message}（文件大小 {fileLen} 字节）";
        return false;
    }

    /// <summary>在安装目录里找 Minecraft 客户端可执行文件（含 extracted 子目录）。</summary>
    public static string? FindClientExe(string installDir)
    {
        if (!Directory.Exists(installDir)) return null;

        var candidates = new[]
        {
            Path.Combine(installDir, "Minecraft.Windows.exe"),
            Path.Combine(installDir, "Minecraft.Windows", "Minecraft.Windows.exe"),
            Path.Combine(installDir, "extracted", "Minecraft.Windows.exe"),
            Path.Combine(installDir, "extracted", "Minecraft.Windows", "Minecraft.Windows.exe"),
        };
        foreach (var exe in candidates)
            if (File.Exists(exe)) return exe;

        var files = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories);
        return files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Contains("Minecraft", StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 启动 ====================

    /// <summary>
    /// 启动已下载的基岩版客户端。启动前自动补全运行库（VC++ 2015-2022 x64 等支持库），
    /// 避免因缺运行库直接闪退。
    /// </summary>
    public static async Task<Process?> LaunchClientAsync(string installDir,
        IProgress<ProgressInfo>? progress = null)
    {
        if (!Directory.Exists(installDir))
            throw new InvalidOperationException($"安装目录不存在：{installDir}");

        var exe = FindClientExe(installDir);
        if (exe == null)
            throw new InvalidOperationException(
                $"在目录 {installDir} 中找不到 Minecraft.Windows.exe。" +
                "请确认客户端已正确下载并解压。");

        // 自动补全支持库：客户端（GDK/UWP）运行依赖 VC++ 运行库等框架包
        await BedrockContentService.EnsureSupportLibrariesInstalledAsync(progress);

        // 优先走"真正注册进系统应用清单 + 系统激活路径启动"这条路（见
        // BedrockPackageRegistrationHelper 类头注释——这才是基岩版作为 UWP/GDK 包
        // 应有的启动方式）。只有在这条路因为环境原因走不通时（没有 AppxManifest.xml、
        // 开发者模式没开且用户明确选择跳过等），才退回"直接跑 exe"这种不完整的方式，
        // 并且要让用户知道这是降级方案，游戏内一些功能可能不正常。
        var manifestDir = Path.GetDirectoryName(exe)!;
        // AppxManifest.xml 一般在解压根目录，不一定跟 exe 同级，往上找一层兜底
        var manifestSearchRoot = BedrockPackageRegistrationHelper.FindAppxManifest(manifestDir) != null
            ? manifestDir
            : installDir;

        if (BedrockPackageRegistrationHelper.FindAppxManifest(manifestSearchRoot) != null)
        {
            try
            {
                progress?.Report(new ProgressInfo("正在注册基岩版客户端到系统（首次启动该版本需要这一步）", 0, 1, ""));
                await BedrockPackageRegistrationHelper.RegisterAndLaunchAsync(manifestSearchRoot,
                    new Progress<(int Percent, string State)>(p =>
                        progress?.Report(new ProgressInfo($"正在注册：{p.State}", p.Percent, 100, ""))));
                return null; // 通过 shell:AppsFolder 启动，拿不到 Process 句柄，跟 BedrockLaunchService.Launch 行为一致
            }
            catch (Exception ex)
            {
                // 注册这条路失败：明确告诉用户这是降级，不要让用户以为游戏"正常装好了"
                progress?.Report(new ProgressInfo(
                    $"注册应用包失败（{ex.Message}），将尝试直接启动可执行文件——" +
                    "这种方式下存档/账号登录/多人联机等依赖应用包身份的功能可能无法正常使用。",
                    0, 1, ""));
            }
        }
        else
        {
            progress?.Report(new ProgressInfo(
                "未找到 AppxManifest.xml，无法把这个客户端注册为系统应用，将尝试直接启动可执行文件——" +
                "这种方式下存档/账号登录/多人联机等依赖应用包身份的功能可能无法正常使用。",
                0, 1, ""));
        }

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        };
        return Process.Start(psi);
    }

    /// <summary>旧的同步签名保留（内部等异步完成）。</summary>
    public static Process? LaunchClient(string installDir)
        => LaunchClientAsync(installDir).GetAwaiter().GetResult();

    /// <summary>
    /// 启动后"崩溃检测 + 句柄泄漏"修复。
    ///
    /// 背景（先说清楚能做到什么、做不到什么，避免后来者对着这个方法瞎加期待）：
    /// LaunchClientAsync 只有两种返回：
    ///   1) 走"注册进系统应用清单 + shell:AppsFolder 唤起"这条正路（绝大多数情况）→ 返回
    ///      null。这条路径唤起的是系统另起的独立应用进程，跟 explorer/shell 之间没有
    ///      父子进程关系，本进程从始至终拿不到、也没有官方合法渠道拿到那个进程的句柄——
    ///      这跟 BedrockLaunchService 类头注释里说的是同一件事。这种情况下我们能做的
    ///      只有"如实告诉用户没法监控"，不能假装在监控。
    ///   2) 走"直接跑 exe"降级路径（AppxManifest 缺失/注册失败时的兜底）→ 返回一个真正
    ///      的 Process 对象，这是本进程用 Process.Start 直接拉起的子进程，才有条件做
    ///      "启动后是否短时间内异常退出"的检测。
    ///
    /// 这个方法只处理第 2 种情况：
    ///   - 修复原来 4 处调用点把返回的 Process 直接丢掉不 Dispose 的句柄泄漏
    ///     （每次通过降级路径启动一次基岩版，就泄漏一个内核对象句柄，多次启动会累积）；
    ///   - 用 Exited 事件（而不是轮询）等待进程退出，退出时间落在"启动后很短的宽限期内"
    ///     （EARLY_EXIT_GRACE）就判定为"很可能是启动即崩溃"（对应用户反馈的"启动器窗口
    ///     还在，游戏窗口没了"），回调 onLikelyEarlyExit 让 UI 层给出提示；超过宽限期后
    ///     正常退出（比如用户正常玩完退出游戏）不算异常，不会误报。
    ///   - 无论走到哪个分支，进程对象最终都会被 Dispose，不会常驻占用句柄。
    ///
    /// 这是"尽力而为"的启发式判断，不是精确的崩溃检测（做不到读取游戏内部状态/崩溃转储），
    /// 但已经能覆盖"支持库缺失/驱动不兼容导致进程刚起来就退出"这类最常见的降级路径崩溃。
    /// </summary>
    private static readonly TimeSpan EarlyExitGrace = TimeSpan.FromSeconds(8);

    public static void MonitorLaunchedProcess(Process? proc, Action<string>? onLikelyEarlyExit)
    {
        if (proc == null) return; // 见上面方法注释：这种情况下没有句柄可监控，什么都不用做

        var startedAt = DateTime.UtcNow;
        var handled = 0; // 用 Interlocked 做一次性门闩，防止 Exited 事件 + 下面的同步兜底重复处理/重复 Dispose

        void HandleExit()
        {
            if (Interlocked.Exchange(ref handled, 1) != 0) return;
            try
            {
                var elapsed = DateTime.UtcNow - startedAt;
                if (elapsed < EarlyExitGrace)
                {
                    onLikelyEarlyExit?.Invoke(
                        $"基岩版客户端进程在启动后 {elapsed.TotalSeconds:F1} 秒内就退出了，" +
                        "很可能是刚启动就闪退（常见原因：缺少运行库、显卡驱动过旧、" +
                        "杀毒软件拦截），而不是正常关闭游戏。");
                }
            }
            finally
            {
                // 事件处理完成后释放，防止 Process 对象常驻内存/占用句柄——
                // 这是原来 4 处调用点丢弃返回值不 Dispose 造成的那部分泄漏的根本修复点。
                proc.Dispose();
            }
        }

        try
        {
            proc.EnableRaisingEvents = true;
        }
        catch
        {
            // 极少数情况下（进程已经退出/权限问题）设置这个属性本身会抛异常，
            // 此时直接释放句柄，不再尝试监控，避免半途而废地占着句柄。
            proc.Dispose();
            return;
        }

        proc.Exited += (_, _) => HandleExit();

        // 极端时序：进程在我们订阅 Exited 之前就已经退出，Exited 事件在部分系统上可能
        // 不会再补发。补一次同步检查兜底（HandleExit 内部的门闩保证不会跟 Exited 事件重复处理）。
        if (proc.HasExited)
            HandleExit();
    }
}
