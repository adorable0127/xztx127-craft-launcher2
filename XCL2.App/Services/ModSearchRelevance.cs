using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>模组搜索的名称匹配度。分数限定在 0~1，不把热度或接口返回顺序当成匹配度。</summary>
internal sealed class ModSearchRelevance
{
    private readonly string _query;
    private readonly Dictionary<string, double> _presetScores = new(StringComparer.OrdinalIgnoreCase);

    public string? PresetKeyword { get; private set; }
    public double BestSimilarity { get; private set; }
    public bool HasExactPresetMatch { get; private set; }
    public bool NeedsTranslation => !HasExactPresetMatch && BestSimilarity < 0.70;

    private ModSearchRelevance(string query) => _query = query;

    public static ModSearchRelevance Create(string query, ModSource source)
    {
        var context = new ModSearchRelevance(query);
        var bestPopularity = -1;
        foreach (var entry in WikiEntry.All)
        {
            if (string.IsNullOrWhiteSpace(entry.ChineseName) ||
                !entry.Slugs.TryGetValue(source, out var slug) || string.IsNullOrWhiteSpace(slug)) continue;

            // 中文专名及别名参与预设匹配，括号中的英文不稀释中文名称的分数。
            var name = entry.ChineseName.Split(new[] { " (", "（" }, StringSplitOptions.None)[0];
            var score = name.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(alias => NameSimilarity(alias, query)).DefaultIfEmpty(0).Max();
            if (!context._presetScores.TryGetValue(slug, out var previous) || score > previous)
                context._presetScores[slug] = score;
            if (score >= 1) context.HasExactPresetMatch = true;
            if (score > context.BestSimilarity ||
                (score > 0 && score == context.BestSimilarity && entry.Popularity > bestPopularity))
            {
                context.BestSimilarity = score;
                context.PresetKeyword = slug.Replace('-', ' ').Replace('/', ' ');
                bestPopularity = entry.Popularity;
            }
        }
        return context;
    }

    public double Score(UnifiedModItem item, string? translatedQuery)
    {
        var title = item.Title;
        string? slug = null;
        if (item.RawItem is ModrinthSearchHit mr)
        {
            title = mr.Title;
            slug = mr.Slug;
        }
        else if (item.RawItem is CurseForgeMod cf)
        {
            title = cf.Name;
            // 使用已有搜索响应里的项目链接取精确 slug，不增加详情请求。
            if (Uri.TryCreate(cf.Links?.WebsiteUrl, UriKind.Absolute, out var uri) &&
                uri.AbsolutePath.StartsWith("/minecraft/mc-mods/", StringComparison.OrdinalIgnoreCase))
                slug = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').Last());
        }

        var score = slug != null && _presetScores.TryGetValue(slug, out var presetScore) ? presetScore : 0;
        score = Math.Max(score, NameSimilarity(item.Title, _query));
        if (!string.IsNullOrWhiteSpace(translatedQuery))
        {
            score = Math.Max(score, NameSimilarity(title, translatedQuery));
            score = Math.Max(score, NameSimilarity(slug?.Replace('-', ' '), translatedQuery));
            // 简介只能补充排序，单凭简介中的通用词不能越过 70% 的名称匹配门槛。
            score = Math.Max(score, Math.Min(0.65, NameSimilarity(item.Description, translatedQuery)));
        }
        return score;
    }

    private static string Normalize(string value)
    {
        var simplified = ChineseModSearchTranslator.TraditionalToSimplified(value).ToLowerInvariant();
        var result = new StringBuilder(simplified.Length);
        foreach (var ch in simplified)
            if (char.IsLetterOrDigit(ch)) result.Append(ch);
        return result.ToString();
    }

    private static double NameSimilarity(string? candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(query)) return 0;
        var source = Normalize(candidate);
        var target = Normalize(query);
        if (source.Length == 0 || target.Length == 0) return 0;
        if (source == target) return 1;

        // 编辑距离归一化为真正的百分比，避免原模糊分数随名称长度增长而超过 100%。
        var previous = Enumerable.Range(0, target.Length + 1).ToArray();
        var current = new int[target.Length + 1];
        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= target.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        var score = 1.0 - previous[target.Length] / (double)Math.Max(source.Length, target.Length);

        // 英文完整单词命中允许项目带后缀（如 Storage Drawers Extra），不按字符子串误命中。
        if (!ChineseModSearchTranslator.IsChineseQuery(query))
        {
            var separators = new[] { ' ', '-', '_', '/', ':', '(', ')', '[', ']', '.', ',' };
            var queryWords = query.ToLowerInvariant().Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Where(word => word.Length > 0).Distinct().ToArray();
            var words = candidate.ToLowerInvariant().Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Where(word => word.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (queryWords.Length > 0)
                score = Math.Max(score, 0.9 * queryWords.Count(words.Contains) / queryWords.Length);
        }
        return Math.Clamp(score, 0, 1);
    }
}
