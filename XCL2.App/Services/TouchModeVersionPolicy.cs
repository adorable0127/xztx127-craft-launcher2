using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 判断某个实例的游戏版本是否支持触屏模式。
/// Mojang 在 Java 版 26.3-snapshot-4 里更换了渲染器，从该快照起（含）触屏可用；
/// 在此之前的版本（26.3 正式版以下，含 26.3-snapshot-1~3、26.2 及更早、所有 1.x 与周快照）触屏行为异常。
/// </summary>
public static class TouchModeVersionPolicy
{
    private const int SupportedMajor = 26;
    private const int SupportedMinor = 3;
    private const int SupportedSnapshot = 4;

    public const string WarningMessage =
        "mojang在Java版26.3-snapshot-4里面换了下原来屎山渲染器，这个版本以后的版本支持触屏，" +
        "而在这个版本以前的版本，触屏就跟喝错药了似的，当前 26.3 正式版触屏属于正式版，" +
        "26.3 26.3-snapshot-4 以下的版本就属于预览版的触屏模式，所以建议使用 26.3 及以上的正式版来游玩。\n\n是否要继续？";

    private static readonly Regex NewSnapshot =
        new(@"(?<![\d.])(\d{1,2})\.(\d{1,2})(?:\.\d+)?-snapshot-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WeeklySnapshot =
        new(@"(?<!\d)\d{2}w\d{1,2}[a-z]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Numeric =
        new(@"(?<![\d.])(\d{1,2})\.(\d{1,2})", RegexOptions.Compiled);

    /// <summary>读取实例 json 与目录名，判断该实例是否属于"不支持触屏"的版本；无法判断时返回 false（不打扰）。</summary>
    public static bool IsBelowSupported(string versionDir, string versionId)
    {
        try
        {
            var candidates = new List<string?> { versionId };
            var jsonPath = FolderService.ResolveVersionJson(versionDir, versionId);
            if (jsonPath != null)
            {
                var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jsonPath));
                if (detail != null)
                {
                    candidates.Add(detail.Id);
                    candidates.Add(detail.InheritsFrom);
                    candidates.Add(detail.ClientVersion);
                }
            }
            var mc = VersionInfoResolver.ResolveMcVersion(versionDir, versionId);
            return IsBelowSupported(mc, candidates);
        }
        catch { return false; }
    }

    /// <summary>mcVersion 为解析出的原版号；candidates 为目录名/json 里可能带 "-snapshot-N" 的原始字符串。</summary>
    public static bool IsBelowSupported(string? mcVersion, IEnumerable<string?> candidates)
    {
        // 1. 新命名快照：26.3-snapshot-4 起才支持。
        foreach (var s in candidates.Prepend(mcVersion))
        {
            if (string.IsNullOrEmpty(s)) continue;
            var m = NewSnapshot.Match(s);
            if (!m.Success) continue;
            var major = int.Parse(m.Groups[1].Value);
            var minor = int.Parse(m.Groups[2].Value);
            var snap = int.Parse(m.Groups[3].Value);
            if (major != SupportedMajor || minor != SupportedMinor)
                return (major, minor).CompareTo((SupportedMajor, SupportedMinor)) < 0;
            return snap < SupportedSnapshot;
        }

        if (string.IsNullOrWhiteSpace(mcVersion)) return false;

        // 2. 旧式周快照（24w14a、26w14a 愚人节等）均早于 26.3。
        if (WeeklySnapshot.IsMatch(mcVersion)) return true;

        // 3. 正式版 / pre / rc：按 主.次 版本号比较。
        var n = Numeric.Match(mcVersion);
        if (!n.Success) return false;
        var ver = (int.Parse(n.Groups[1].Value), int.Parse(n.Groups[2].Value));
        return ver.CompareTo((SupportedMajor, SupportedMinor)) < 0;
    }
}
