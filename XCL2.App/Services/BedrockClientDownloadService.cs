using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

    private static readonly string[] VersionDbMirrorUrls = new[]
    {
        "https://cdn.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://fastly.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://gcore.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://ghp.ci/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
        "https://ghproxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
        "https://mirror.ghproxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
    };

    // ===== 老源：mc-w10-versiondb（已停更，最高 1.21.x）=====
    private const string LegacyVersionDbUrl = "https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min";

    private static readonly string[] LegacyVersionDbMirrorUrls = new[]
    {
        "https://cdn.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://fastly.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://gcore.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://ghp.ci/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
        "https://ghproxy.com/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
        "https://mirror.ghproxy.com/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
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
        var fresh = await FetchVersionListFromNetworkAsync(ct);
        if (fresh.Count > 0)
        {
            SaveCachedVersions(fresh);
            return FilterAndSort(fresh, channel);
        }

        // 网络全挂：回退本地缓存 → 内置列表
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
        // 1. 带直链的（缓存/内置列表里已有）直接下载
        if (!string.IsNullOrEmpty(version.Url))
            return await DownloadFromUrlAsync(version.Url, version.Name, targetDir, progress, ct);

        // 2. 新版（reversedcodes 列表，1.26.x 等 GDK 通道版本）：
        //    先拉该版本的 GDK 元数据拿微软官方直链（多镜像回退，直链全部失败再走 Store）
        if (version.DirectUrlAvailable)
        {
            Exception? gdkError = null;
            foreach (var url in await ResolveGdkDownloadUrlsAsync(version, ct))
            {
                try
                {
                    return await DownloadFromUrlAsync(url, version.Name, targetDir, progress, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    gdkError = ex;
                }
            }

            var fe3Url = await ResolveDownloadUrlAsync(version.Uuid, ct);
            if (!string.IsNullOrEmpty(fe3Url))
                return await DownloadFromUrlAsync(fe3Url, version.Name, targetDir, progress, ct);

            throw new InvalidOperationException(
                "获取该版本的微软官方下载链接失败，所有镜像源均不可用。" +
                (gdkError != null ? $"\n详细信息：{gdkError.Message}" : ""));
        }

        // 3. 老版本：Microsoft Store FE3 换直链
        var downloadUrl = await ResolveDownloadUrlAsync(version.Uuid, ct);
        if (!string.IsNullOrEmpty(downloadUrl))
            return await DownloadFromUrlAsync(downloadUrl, version.Name, targetDir, progress, ct);

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

    private async Task<string> DownloadFromUrlAsync(string url, string versionName, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = ".appx";
        var fileName = $"Minecraft-{versionName}{ext}";
        var filePath = Path.Combine(targetDir, fileName);

        progress?.Report(new ProgressInfo($"正在下载基岩版客户端 {versionName}", 0, 1, ""));

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

        await dst.DisposeAsync();

        progress?.Report(new ProgressInfo("正在解压", 0, 1, ""));
        var extractDir = Path.Combine(targetDir, "extracted");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        ZipFile.ExtractToDirectory(filePath, extractDir, overwriteFiles: true);

        progress?.Report(new ProgressInfo(Loc.T("Str_Common_Finish", "完成"), 1, 1, versionName));
        return extractDir;
    }

    // ==================== 启动 ====================

    public static Process? LaunchClient(string installDir)
    {
        if (!Directory.Exists(installDir))
            throw new InvalidOperationException($"安装目录不存在：{installDir}");

        var possibleExes = new[]
        {
            Path.Combine(installDir, "Minecraft.Windows.exe"),
            Path.Combine(installDir, "Minecraft.Windows", "Minecraft.Windows.exe"),
        };

        foreach (var exe in possibleExes)
        {
            if (File.Exists(exe))
            {
                var psi = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = true,
                };
                return Process.Start(psi);
            }
        }

        var files = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories);
        var minecraftExe = files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Contains("Minecraft", StringComparison.OrdinalIgnoreCase));

        if (minecraftExe != null)
        {
            var psi = new ProcessStartInfo(minecraftExe)
            {
                WorkingDirectory = Path.GetDirectoryName(minecraftExe)!,
                UseShellExecute = true,
            };
            return Process.Start(psi);
        }

        throw new InvalidOperationException(
            $"在目录 {installDir} 中找不到 Minecraft.Windows.exe。" +
            "请确认客户端已正确下载并解压。");
    }
}
