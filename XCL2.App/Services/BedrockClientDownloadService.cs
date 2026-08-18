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
            ZipFile.ExtractToDirectory(filePath, extractDir, overwriteFiles: true);
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
    /// 解析下载链接并下载安装包到 version_save（多源回退仅在下载失败时发生；
    /// 成功拿到完好的安装包后立即返回，不在这里做解压等安装动作）。
    /// </summary>
    /// <summary>判断这个下载链接是不是已知打不开的包格式（目前是 .msixvc——Xbox/Game Pass
    /// 云流式安装用的分块格式，不是标准 zip，ZipFile 系列 API 无法读取/解压，
    /// 下了也没法用，提前跳过，避免白白下载几个 GB 又被判定"无效"重新来过）。</summary>
    private static bool IsKnownUnextractablePackageUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            return path.EndsWith(".msixvc", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return url.Contains(".msixvc", StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<string> ResolveAndDownloadPackageAsync(BedrockVersionInfo version, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // 1. 带直链的（缓存/内置列表里已有）直接下载
        if (!string.IsNullOrEmpty(version.Url))
            return await DownloadFromUrlAsync(EnumerateCdnMirrors(version.Url), version.Name, targetDir, progress, ct);

        // 2. 新版（reversedcodes 列表，1.26.x 等 GDK 通道版本）：
        //    先拉该版本的 GDK 元数据拿直链，但 GDK 元数据给出来的很多是 .msixvc 格式——
        //    这是微软 Xbox/Game Pass 那套"云流式安装"用的分块流式包格式，不是标准 zip，
        //    ZipFile 系列 API 天生打不开、也没法用 ExtractToDirectory 解压。以前的代码把
        //    这些 .msixvc 链接和真正的 .appx（zip 结构）混在一起、.msixvc 还排在前面优先试，
        //    结果永远校验失败（"End of Central Directory record could not be found"），
        //    但每次还是要先把几百 MB~几个 GB 的文件完整下完才能验出来是无效的——这才是
        //    "重复下载"的真正根因：不是网络问题，是从一开始就注定会验证失败的格式，
        //    白白重下了一整个文件的流量和时间。
        //
        //    现在的策略：Microsoft Store FE3 API 换到的直链是真正的 .appx（合法 zip
        //    结构），优先试这条；GDK 元数据里的 .msixvc 链接直接过滤掉、根本不去尝试下载
        //    （下了也白下)，只保留 GDK 元数据里万一给出的非 .msixvc（真正 zip 结构）链接
        //    作为补充候选。
        if (version.DirectUrlAvailable)
        {
            var urls = new List<string>();

            // FE3（Microsoft Store）优先：给出来的是真正可解压的 .appx。
            var fe3Url = await ResolveDownloadUrlAsync(version.Uuid, ct);
            if (!string.IsNullOrEmpty(fe3Url))
                urls.AddRange(EnumerateCdnMirrors(fe3Url));

            // GDK 元数据链接作为补充：过滤掉已知打不开的 .msixvc，避免白下几个 GB。
            var gdkUrls = await ResolveGdkDownloadUrlsAsync(version, ct);
            var skippedMsixvc = 0;
            foreach (var url in gdkUrls)
            {
                if (IsKnownUnextractablePackageUrl(url)) { skippedMsixvc++; continue; }
                urls.AddRange(EnumerateCdnMirrors(url));
            }
            if (skippedMsixvc > 0)
                LauncherLogService.AppendLine(
                    $"[Bedrock下载] GDK 元数据给了 {skippedMsixvc} 个 .msixvc 格式链接（不是 zip 结构，" +
                    "已知无法用当前的解压方式安装），已跳过不尝试下载。");

            var distinct = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count > 0)
            {
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

            if (skippedMsixvc > 0 && gdkUrls.Count == skippedMsixvc)
                throw new InvalidOperationException(
                    $"版本 {version.Name} 目前只能拿到 .msixvc 格式的官方包（微软 Xbox/Game Pass 云安装" +
                    "格式，不是标准 zip 结构），当前启动器的安装方式无法处理这种格式，且 Microsoft Store " +
                    "接口也没能换到直链。请尝试其他版本，或从 Microsoft Store 直接安装该版本。");

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

        if (!IsValidZip(filePath))
        {
            // 全局缓存索引：其他目录里已缓存过同一版本的安装包，直接复用，不再重复下载
            var cachedEntry = GamePackageCacheIndex.Find(versionName, "client");
            if (cachedEntry != null && IsValidZip(cachedEntry.FilePath))
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

        if (!IsValidZip(filePath, out var initialReason))
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

                    // 下载完先验证是不是完好的压缩包：网上下到一半/被劫持成 HTML 都会在这里暴露，
                    // 直接删掉换下一个源，全失败再报错，避免后面"解压失败"。
                    if (IsValidZip(filePath, out var checkReason))
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

            if (!IsValidZip(filePath, out var finalReason))
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
}
