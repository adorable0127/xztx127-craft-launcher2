using XCL2.App.Models;

namespace XCL2.App.Services;

// ============================================================================
// 鸣谢 / Credit：本文件的中文搜索匹配思路（提取候选英文单词、按热度和相似度加权投票选出
// 最终搜索关键词）移植自 Plain Craft Launcher 2（PCL2）的
// Modules/Resource/ResourceSearcher.vb 中"中文搜索"部分的逻辑，一并感谢 PCL2 团队。
// ============================================================================

/// <summary>
/// 中文关键词 → 英文搜索词 的翻译器。取代旧版 ModNameDictionary（几百条手工词典），
/// 改为基于 PCL2 同款的 WikiEntry 全量数据库（近 3 万条 MC 百科 Mod 中文名）+ 模糊搜索算法，
/// 覆盖面和准确度都大幅提升——不再需要用户搜的词精确等于词典里手打的某个别名，
/// 而是用编辑距离式的相似度匹配去"猜"用户想搜的是哪个 Mod。
///
/// 匹配流程（对应 Modrinth 和 CurseForge 分别独立算一遍，因为两边的 Slug 可能不同）：
/// 1. 用查询词在 WikiEntry 数据库的中文名上做模糊搜索，取相似度较高的一批候选 Mod；
/// 2. 从每个候选的 Slug/中文名里提取候选英文单词（过滤掉纯数字、"mod"/"forge" 等噪声词，
///    并合并"能由其他候选词拼出来的词"，例如 "ender io" 和 "enderio" 只保留前者）；
/// 3. 每个候选词按"匹配相似度 × 该 Mod 的热度"加权投票，得票最高的单词作为最终替换关键词。
/// </summary>
public static class ChineseModSearchTranslator
{
    private static readonly HashSet<string> NoiseWordSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "of", "mod", "and", "forge", "fabric", "for", "quilt", "neoforge"
    };

    /// <summary>结果：翻译出的英文关键词（可能为 null），以及本次中文搜索直接命中的候选 Mod
    /// 的 Slug 列表（Modrinth 场景下可用于"直接按 Slug 批量取工程"，比重新搜索关键词更精确，
    /// 对应 PCL2 的 ModrinthSlugs 直接获取逻辑）。</summary>
    public readonly record struct TranslationResult(string? Keyword, IReadOnlyList<string> DirectSlugs);

    /// <summary>
    /// 判断查询词是否需要走中文搜索翻译（含至少一个中日韩统一表意文字字符）。
    /// 和 PCL2 一致：只有查询词包含中文时才触发，纯英文查询直接原样发给 Modrinth/CurseForge。
    /// </summary>
    public static bool IsChineseQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return false;
        foreach (var c in query)
            if (c is >= '\u4e00' and <= '\u9fbb') return true;
        return false;
    }

    /// <summary>
    /// 尝试把中文查询词翻译成给指定平台用的英文搜索词。source 只能是 Modrinth 或 CurseForge
    /// （对应 WikiEntry.Slugs 的键），不支持传 Combined。
    /// </summary>
    public static TranslationResult Translate(string query, ModSource source)
    {
        if (!IsChineseQuery(query)) return new TranslationResult(null, Array.Empty<string>());

        var normalized = query.Trim().ToLowerInvariant();
        normalized = TraditionalToSimplified(normalized); // 繁体转简体，兼容用户输入繁体中文

        var candidateEntries = BuildSearchEntries(source);
        var searchResults = FuzzySearch.Search(candidateEntries, normalized, maxBlurCount: 100, minBlurSimilarity: 0.25);
        if (searchResults.Count == 0) return new TranslationResult(null, Array.Empty<string>());

        if (source == ModSource.CurseForge)
        {
            // CurseForge 的搜索接口要求查询词里每个词都必须匹配上，所以只能选一个 Mod 来搜，
            // 优先选"完全匹配"里最热门的一个；没有完全匹配则选相似度最高里最热门的一个。
            var maxSimilarity = searchResults.Max(s => s.Similarity);
            var pool = searchResults[0].AbsoluteRight
                ? searchResults.Where(r => r.AbsoluteRight).ToList()
                : searchResults.Where(r => Math.Abs(r.Similarity - maxSimilarity) < 1e-9).ToList();
            var target = pool.OrderByDescending(r => r.Item.Popularity).First();
            var keyword = string.Join(" ", ExtractWords(target, source));
            return new TranslationResult(string.IsNullOrEmpty(keyword) ? null : keyword, Array.Empty<string>());
        }
        else
        {
            // Modrinth 支持多词查询，这里用"候选词加权投票"选出综合最优的一个关键词，
            // 同时把命中的 Slug 列表一并返回，供调用方按需直接批量拉取工程（更精确，见 PCL2 的
            // "Modrinth 直接获取工程"逻辑）。
            var wordWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in searchResults)
            {
                foreach (var word in ExtractWords(result, source))
                {
                    var similarity = result.SearchSource.Any(s => s.Aliases.Contains(normalized)) ? 1000 : result.Similarity;
                    wordWeights.TryGetValue(word, out var existing);
                    wordWeights[word] = existing + similarity * Math.Max(result.Item.Popularity, 1);
                }
            }

            string? keyword = wordWeights.Count > 0 ? wordWeights.MaxBy(w => w.Value).Key : null;
            var slugs = searchResults.Take(100)
                .Select(r => r.Item.Slugs.GetValueOrDefault(ModSource.Modrinth))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .Distinct()
                .ToList();
            return new TranslationResult(keyword, slugs);
        }
    }

    private static List<SearchEntry<WikiEntry>> BuildSearchEntries(ModSource source)
    {
        var list = new List<SearchEntry<WikiEntry>>();
        foreach (var entry in WikiEntry.All)
        {
            if (!entry.Slugs.ContainsKey(source)) continue;

            if (entry.ChineseName != null)
            {
                var beforeParen = BeforeFirst(entry.ChineseName, " (");
                var afterParen = AfterFirst(entry.ChineseName, " (");
                list.Add(new SearchEntry<WikiEntry>
                {
                    Item = entry,
                    SearchSource = new List<SearchSource>
                    {
                        // 部分 Mod 中文名里用 "/" 分隔了多个别名
                        new(TraditionalToSimplified(beforeParen).Split('/', StringSplitOptions.RemoveEmptyEntries), 1),
                        new(TraditionalToSimplified(afterParen) + entry.Slugs[source], 0.5)
                    }
                });
            }
            else
            {
                list.Add(new SearchEntry<WikiEntry>
                {
                    Item = entry,
                    SearchSource = new List<SearchSource> { new(entry.Slugs[source], 0.5) }
                });
            }
        }
        return list;
    }

    /// <summary>从匹配到的 WikiEntry 里提取可能的英文候选词（分词、去噪声、去重、去冗余）。</summary>
    private static List<string> ExtractWords(SearchEntry<WikiEntry> result, ModSource source)
    {
        var candidates = new List<string>();
        if (result.Item.Slugs.TryGetValue(source, out var slug))
            candidates.Add(slug.Replace('-', ' ').Replace('/', ' '));

        if (result.Item.ChineseName != null)
        {
            var s = AfterLast(result.Item.ChineseName, " (");
            s = s.TrimEnd(')', ' ');
            s = BeforeFirst(s, " - ");
            s = s.Replace('-', ' ').Replace('/', ' ').Replace(':', ' ').Replace('(', ' ').Replace(")", "");
            candidates.Add(s);
        }

        var words = candidates
            .SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Trim('{', '[', '(').TrimEnd('}', ']', ')').ToLowerInvariant())
            .Where(w =>
                w.Length > 1 &&
                !NoiseWordSet.Contains(w) &&
                !double.TryParse(w, out _) &&
                IsAsciiOnly(w))
            .Distinct()
            .ToList();

        // 如果一个词可以由其他候选词拼成，就去掉这个词（例如 "ender io enderio" 里的
        // "enderio" 会被剔除，只保留 "ender" "io"，避免同一概念重复计票）
        bool CanForm(string s)
        {
            if (words.Contains(s)) return true;
            foreach (var c in words)
                if (c.Length < s.Length && s.StartsWith(c, StringComparison.Ordinal) && CanForm(s[c.Length..]))
                    return true;
            return false;
        }
        return words.Where(w => !words.Any(c => c.Length < w.Length && w.StartsWith(c, StringComparison.Ordinal) && CanForm(w[c.Length..]))).ToList();
    }

    private static bool IsAsciiOnly(string s) => s.All(c => c < 128);

    private static string BeforeFirst(string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? s : s[..i];
    }

    private static string AfterFirst(string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? "" : s[(i + sep.Length)..];
    }

    private static string AfterLast(string s, string sep)
    {
        var i = s.LastIndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? s : s[(i + sep.Length)..];
    }

    /// <summary>
    /// 极简繁体转简体：只覆盖 Mod 中文名/搜索场景里出现概率较高的常用字，不追求完整覆盖
    /// 所有繁简字符（完整方案需要引入一整张繁简映射表，超出这个轻量搜索辅助功能的必要范围；
    /// 未覆盖到的字符会原样保留，不影响后续的模糊相似度匹配——模糊搜索本身能容忍少量未转换字符）。
    /// </summary>
    private static string TraditionalToSimplified(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (CommonTraditionalToSimplified.TryGetValue(chars[i], out var simp))
                chars[i] = simp;
        return new string(chars);
    }

    private static readonly Dictionary<char, char> CommonTraditionalToSimplified = new()
    {
        ['機'] = '机', ['動'] = '动', ['個'] = '个', ['們'] = '们', ['來'] = '来', ['對'] = '对',
        ['會'] = '会', ['時'] = '时', ['說'] = '说', ['與'] = '与', ['為'] = '为', ['產'] = '产',
        ['發'] = '发', ['實'] = '实', ['開'] = '开', ['關'] = '关', ['華'] = '华', ['歷'] = '历',
        ['線'] = '线', ['興'] = '兴', ['見'] = '见', ['視'] = '视', ['覺'] = '觉', ['質'] = '质',
        ['資'] = '资', ['進'] = '进', ['連'] = '连', ['選'] = '选', ['過'] = '过', ['還'] = '还',
        ['這'] = '这', ['邊'] = '边', ['醫'] = '医', ['釋'] = '释', ['鐵'] = '铁', ['錢'] = '钱',
        ['長'] = '长', ['門'] = '门', ['間'] = '间', ['雲'] = '云', ['電'] = '电', ['頭'] = '头',
        ['題'] = '题', ['類'] = '类', ['風'] = '风', ['飛'] = '飞', ['馬'] = '马', ['魔'] = '魔',
        ['龍'] = '龙', ['廠'] = '厂', ['懷'] = '怀', ['戰'] = '战', ['擴'] = '扩',
        ['數'] = '数', ['書'] = '书', ['樹'] = '树'
    };
}
