using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 从一个已安装的版本目录里，解析出「真实的原版 Minecraft 版本号」和「加载器 + 加载器版本号」。
///
/// ===== 为什么要单独抽这个类（修复"导出的整合包没有版本信息，无法导入"）=====
/// 旧的 FolderService.GuessLoaderInfo 有两个硬伤，直接导致导出的整合包不可用：
///
/// 1) McVersion 取的是 `detail.InheritsFrom ?? detail.Id`。
///    这在"加载器 json 靠 inheritsFrom 继承原版"的年代是对的，但本项目的
///    ClientLoaderInstallService 为了做"独立实例"，安装 Fabric/Quilt 时会**主动把
///    inheritsFrom 置为 null**、并把 Id 设成 "fabric-loader-0.15.11-1.20.1" 这种完整版本 ID。
///    于是 InheritsFrom 是 null，回退取 Id，McVersion 就变成了
///    "fabric-loader-0.15.11-1.20.1" —— 这不是一个合法的 Minecraft 版本号。
///
/// 2) ModLoaderVersion 直接写死成 `loader != null ? "" : null`，也就是**永远是空字符串**，
///    从来没有真正解析过。
///
/// 这两个值会被 ModManagerPage 原样塞进 ModpackManifest，再被 ExportMrpack 写进
/// modrinth.index.json 的 dependencies：
///     "dependencies": { "minecraft": "fabric-loader-0.15.11-1.20.1", "fabric-loader": "" }
/// 这份 index 不符合 mrpack schema（minecraft 必须是真实版本号、加载器版本不能是空串），
/// Modrinth App / PrismLauncher / 本启动器自己再导入时都拿不到有效版本信息 —— 这就是
/// "导出的整合包根本没有代表版本信息，无法导入"的完整成因。
///
/// ===== 解析优先级（从最可靠到最兜底）=====
/// A. version json 里的 libraries：加载器自己的 Maven 坐标是最权威的来源，
///    形如 net.fabricmc:fabric-loader:0.15.11 / net.neoforged:neoforge:21.1.66 /
///    net.minecraftforge:forge:1.20.1-47.2.0 / org.quiltmc:quilt-loader:0.24.0。
/// B. inheritsFrom：存在就是货真价实的父版本号。
/// C. clientVersion 字段：Forge/NeoForge 生成的 json 里常带这个字段，直接就是原版版本号。
/// D. 按版本 ID 的命名约定反解（fabric-loader-{loader}-{mc} 等）。
/// E. 正则从任意字符串里抓版本号（同时支持 1.x.y 老命名和 26.2 这种新命名，见
///    MinecraftVersionPattern 的注释）。
/// </summary>
public static class VersionInfoResolver
{
    /// <summary>
    /// 匹配 Minecraft 版本号的正则，同时覆盖三种命名：
    /// - 传统正式版：1.16.5 / 1.21 / 1.7.10
    /// - 新版年份制正式版：26.1 / 26.2（截图里下载中心和资源详情页出现的就是这一类；
    ///   旧的 LauncherService.ExtractBaseMinecraftVersion 只写了 `1\.\d+`，对这类版本号
    ///   一个都匹配不上，是另一处潜在的连带 bug）
    /// - 快照/预览版：24w14a / 1.21-pre1 / 1.20.5-rc1
    /// 顺序很重要：先试带后缀的预览版形态，再试纯数字，避免 "1.21-pre1" 只被截出 "1.21"。
    /// </summary>
    private static readonly Regex SnapshotPattern =
        new(@"\b\d{2}w\d{1,2}[a-z]\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PreReleasePattern =
        new(@"\b\d{1,2}\.\d{1,2}(?:\.\d{1,2})?-(?:pre|rc)\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NumericVersionPattern =
        new(@"\b\d{1,2}\.\d{1,2}(?:\.\d{1,2})?\b", RegexOptions.Compiled);

    /// <summary>解析结果。ModLoader 为 null 表示纯原版；ModLoaderVersion 为 null 表示
    /// 确实解析不出加载器版本号（调用方应当把它当成"未知"处理并整个省略该字段，
    /// **不要**退化成空字符串写进清单文件——空串正是导致 mrpack 无效的原因之一）。</summary>
    public sealed record Result(string McVersion, string? ModLoader, string? ModLoaderVersion);

    /// <summary>
    /// 主入口：给一个版本目录 + 该目录的版本 ID + 已反序列化好的 VersionDetail，
    /// 返回可信的版本信息。detail 传 null 时会自行尝试读取目录里的 json。
    /// </summary>
    public static Result Resolve(string versionDir, string versionId, VersionDetail? detail)
    {
        detail ??= TryLoadDetail(versionDir, versionId);

        var (loader, loaderVersion, mcFromLibrary) = ResolveFromLibraries(detail);

        // ---- 加载器类型兜底：库里没认出来就退回看版本 ID 里的关键字 ----
        loader ??= GuessLoaderFromId(versionId);

        // ---- 加载器版本兜底：按各家命名约定从版本 ID 反解 ----
        loaderVersion ??= GuessLoaderVersionFromId(versionId, loader);

        // ---- 原版版本号：按可靠度依次尝试 ----
        var mcVersion =
            FirstUsable(mcFromLibrary)
            ?? FirstUsable(detail?.InheritsFrom)
            ?? FirstUsable(detail?.ClientVersion)
            ?? GuessMcVersionFromId(versionId, loader)
            ?? ExtractAnyVersion(versionId)
            ?? ExtractAnyVersion(detail?.Id)
            ?? versionId; // 实在解析不出来才退回 ID 本身（至少不会是 null）

        return new Result(mcVersion, loader, loaderVersion);
    }

    /// <summary>只要原版版本号的轻量重载（不需要构造 VersionDetail 的调用方用）。</summary>
    public static string ResolveMcVersion(string versionDir, string versionId)
        => Resolve(versionDir, versionId, null).McVersion;

    /// <summary>
    /// 判断一个字符串"看起来像不像一个真实的 Minecraft 版本号"。
    /// 导出整合包前用它做最后一道闸：不像的话宁可留空/报错，也不要写一个
    /// "fabric-loader-0.15.11-1.20.1" 进 dependencies.minecraft 让整个包作废。
    /// </summary>
    public static bool LooksLikeMcVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (SnapshotPattern.IsMatch(v) && SnapshotPattern.Match(v).Value.Length == v.Length) return true;
        if (PreReleasePattern.IsMatch(v) && PreReleasePattern.Match(v).Value.Length == v.Length) return true;
        var m = NumericVersionPattern.Match(v);
        return m.Success && m.Value.Length == v.Length;
    }

    // ==================== 内部实现 ====================

    private static string? FirstUsable(string? s)
        => LooksLikeMcVersion(s) ? s!.Trim() : null;

    private static VersionDetail? TryLoadDetail(string versionDir, string versionId)
    {
        try
        {
            var exact = Path.Combine(versionDir, $"{versionId}.json");
            var path = File.Exists(exact)
                ? exact
                : Directory.Exists(versionDir)
                    ? Directory.GetFiles(versionDir, "*.json") is { Length: 1 } only ? only[0] : null
                    : null;
            if (path == null) return null;
            return JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 libraries 的 Maven 坐标里读加载器信息。这是最权威的来源——加载器把自己的
    /// 版本号写在自己的库坐标里，不依赖任何命名约定，用户怎么给版本文件夹改名都不影响。
    /// Forge 的坐标形如 net.minecraftforge:forge:1.20.1-47.2.0，一并能拿到原版版本号。
    /// </summary>
    private static (string? Loader, string? LoaderVersion, string? McVersion) ResolveFromLibraries(VersionDetail? detail)
    {
        if (detail?.Libraries == null) return (null, null, null);

        foreach (var lib in detail.Libraries)
        {
            var name = lib?.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Maven 坐标：group:artifact:version[:classifier]
            var parts = name.Split(':');
            if (parts.Length < 3) continue;
            var group = parts[0];
            var artifact = parts[1];
            var version = parts[2];

            if (group.Equals("net.fabricmc", StringComparison.OrdinalIgnoreCase) &&
                artifact.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
                return ("Fabric", version, null);

            if (group.Equals("org.quiltmc", StringComparison.OrdinalIgnoreCase) &&
                artifact.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase))
                return ("Quilt", version, null);

            if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                (artifact.Equals("neoforge", StringComparison.OrdinalIgnoreCase) ||
                 artifact.Equals("forge", StringComparison.OrdinalIgnoreCase)))
                return ("NeoForge", version, null);

            if (group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
                artifact.Equals("forge", StringComparison.OrdinalIgnoreCase))
            {
                // Forge 的 version 段形如 "1.20.1-47.2.0"：前半是原版版本号，后半才是 Forge 版本号。
                var dash = version.IndexOf('-');
                if (dash > 0)
                {
                    var mc = version[..dash];
                    var forgeVer = version[(dash + 1)..];
                    return ("Forge", forgeVer, LooksLikeMcVersion(mc) ? mc : null);
                }
                return ("Forge", version, null);
            }
        }

        return (null, null, null);
    }

    private static string? GuessLoaderFromId(string versionId)
    {
        var lower = versionId.ToLowerInvariant();
        // 注意顺序：neoforge 必须在 forge 之前判断，否则 "neoforge-21.1.66" 会被误判成 Forge。
        if (lower.Contains("neoforge")) return "NeoForge";
        if (lower.Contains("fabric")) return "Fabric";
        if (lower.Contains("quilt")) return "Quilt";
        if (lower.Contains("forge")) return "Forge";
        if (lower.Contains("optifine")) return "OptiFine";
        if (lower.Contains("liteloader")) return "LiteLoader";
        return null;
    }

    /// <summary>
    /// 按各加载器的官方版本 ID 命名约定反解加载器版本号：
    /// - Fabric / Quilt: "fabric-loader-{loaderVersion}-{mcVersion}"
    /// - Forge:          "{mcVersion}-forge-{forgeVersion}" 或 "{mcVersion}-forge{forgeVersion}"
    /// - NeoForge:       "neoforge-{neoforgeVersion}"
    /// </summary>
    private static string? GuessLoaderVersionFromId(string versionId, string? loader)
    {
        if (loader == null) return null;

        switch (loader)
        {
            case "Fabric":
            case "Quilt":
            {
                var m = Regex.Match(versionId,
                    @"^(?:fabric|quilt)-loader-(?<lv>[\w.+\-]+?)-(?<mc>[\w.\-]+)$",
                    RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["lv"].Value;
                break;
            }
            case "Forge":
            {
                var m = Regex.Match(versionId, @"forge[-_]?(?<fv>\d[\w.\-]*)$", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["fv"].Value;
                break;
            }
            case "NeoForge":
            {
                var m = Regex.Match(versionId, @"neoforge[-_]?(?<nv>\d[\w.\-]*)$", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["nv"].Value;
                break;
            }
        }
        return null;
    }

    /// <summary>按命名约定反解原版版本号（跟 GuessLoaderVersionFromId 是同一批正则的另一半）。</summary>
    private static string? GuessMcVersionFromId(string versionId, string? loader)
    {
        if (loader is "Fabric" or "Quilt")
        {
            var m = Regex.Match(versionId,
                @"^(?:fabric|quilt)-loader-(?<lv>[\w.+\-]+?)-(?<mc>[\w.\-]+)$",
                RegexOptions.IgnoreCase);
            if (m.Success && LooksLikeMcVersion(m.Groups["mc"].Value)) return m.Groups["mc"].Value;
        }

        if (loader == "Forge")
        {
            // "1.20.1-forge-47.2.0" → 取第一段
            var m = Regex.Match(versionId, @"^(?<mc>[\d.]+)[-_]forge", RegexOptions.IgnoreCase);
            if (m.Success && LooksLikeMcVersion(m.Groups["mc"].Value)) return m.Groups["mc"].Value;
        }

        return null;
    }

    /// <summary>从任意字符串里抓出第一个"看起来像版本号"的片段。
    /// 顺序：快照 → 预发布 → 普通数字版本，保证 "1.21-pre1" 不会被截成 "1.21"。</summary>
    public static string? ExtractAnyVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var snap = SnapshotPattern.Match(text);
        if (snap.Success) return snap.Value;

        var pre = PreReleasePattern.Match(text);
        if (pre.Success) return pre.Value;

        var num = NumericVersionPattern.Match(text);
        return num.Success ? num.Value : null;
    }
}
