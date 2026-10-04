using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>26.3 第 4 快照为分界；优先读取客户端实际 ID，避免 release_target 抹掉快照序号。</summary>
internal static class TouchVersionPolicy
{
    private const string VersionPattern = @"(?<major>1|2[6-9]|[3-9]\d)\.(?<minor>\d+)(?:\.\d+)?(?:(?:[-_\s]*)(?:(?:snapshot|snap|t|快照)[-_\s]*(?<snapshot>\d+)|第(?<snapshot>\d+|[一二三四五六七八九十]+)个?快照|(?:pre-release|pre|rc|release[-_\s]*candidate)[-_\s]*\d+))?";

    private static bool? ParseExact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value.Trim(), "^" + VersionPattern + "$", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["major"].Value, out var major) &&
            int.TryParse(match.Groups["minor"].Value, out var minor))
        {
            if (major != 26 || minor != 3) return major > 26 || major == 26 && minor > 3;
            var snapshot = match.Groups["snapshot"];
            if (!snapshot.Success) return true; // 26.3 正式版、预发布和候选版本。
            if (int.TryParse(snapshot.Value, out var number)) return number >= 4;
            return snapshot.Value is not ("一" or "二" or "三");
        }
        // 旧周快照和经典 alpha/beta 版本均早于 26.3。
        if (Regex.IsMatch(value, @"^\d{2}w\d{2}[a-z]$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(value, @"^(?:[ab]\d|rd-|c0\.)", RegexOptions.IgnoreCase)) return false;
        return null;
    }

    public static bool? FromVersion(string? version)
    {
        var exact = ParseExact(version);
        if (exact.HasValue || string.IsNullOrWhiteSpace(version)) return exact;
        var title = Regex.Match(version, @"\bMinecraft\s+(?<version>" + VersionPattern + @")(?![\d.])", RegexOptions.IgnoreCase);
        if (title.Success) return ParseExact(title.Groups["version"].Value);
        // 非标准实例名只在候选版本唯一时采用，不能遇到第一个数字就决定路径。
        var candidates = Regex.Matches(version, @"(?<![\d.])" + VersionPattern + @"(?![\d.])", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(m => ParseExact(m.Value)).Where(v => v.HasValue).Distinct().ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool? ReadClientVersion(string dir, string id)
    {
        try
        {
            var jar = LauncherService.ResolveVersionFile(dir, id, "jar");
            if (jar == null) return null;
            using var archive = ZipFile.OpenRead(jar);
            var entry = archive.GetEntry("version.json");
            if (entry == null || entry.Length > 1024 * 1024) return null;
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            foreach (var field in new[] { "id", "name" })
            {
                if (json.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var result = ParseExact(value.GetString());
                    if (result.HasValue) return result;
                }
            }
            if (json.RootElement.TryGetProperty("release_target", out var target) && target.ValueKind == JsonValueKind.String)
            {
                // release_target=26.3 既可能是 T1 也可能是 T4，不能据此选择现代路径。
                var value = target.GetString();
                if (value != "26.3") return ParseExact(value);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // 加载器占位 jar / 老客户端可能没有 version.json，继续读外部元数据。
        }
        return null;
    }

    public static bool? Resolve(string minecraftDir, string versionId)
    {
        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chain = new List<(string Directory, string Id, VersionDetail Detail)>();
            var current = versionId;
            while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
            {
                var dir = Path.Combine(minecraftDir, "versions", current);
                var path = LauncherService.ResolveVersionFile(dir, current, "json");
                if (path == null) break;
                var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(path));
                if (detail == null) break;
                chain.Add((dir, current, detail));
                current = detail.InheritsFrom;
            }
            // 从最底层原版开始。整个继承链的真实客户端版本优先于所有文件夹名称。
            foreach (var item in chain.AsEnumerable().Reverse())
            {
                var result = ReadClientVersion(item.Directory, item.Id);
                if (result.HasValue) return result;
            }
            foreach (var item in chain.AsEnumerable().Reverse())
            {
                var result = ParseExact(item.Detail.Id) ?? ParseExact(item.Detail.InheritsFrom)
                    ?? ParseExact(item.Detail.ClientVersion);
                if (result.HasValue) return result;
            }
            return FromVersion(versionId);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("读取触控版本分流信息失败，将按版本名回退。", ex);
            return FromVersion(versionId);
        }
    }
}
