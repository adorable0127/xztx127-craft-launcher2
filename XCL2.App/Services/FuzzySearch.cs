namespace XCL2.App.Services;

// ============================================================================
// 鸣谢 / Credit
// ----------------------------------------------------------------------------
// 本文件中的模糊搜索算法（SearchSimilarity 字符串相似度计算 + Search 加权排序）
// 移植自 Plain Craft Launcher 2（PCL2，作者 Hakoyu 及社区贡献者）。
// PCL2 项目地址：https://github.com/Hex-Dragon/PCL2
// 中文搜索功能所依赖的 MC 百科 Mod 中文名数据库（WikiEntries.txt，见 WikiEntry.cs）
// 同样来自 PCL2 项目，一并感谢 PCL2 团队维护的这份数据。
// 本移植仅用于 XCL2（xztx127-craft-launcher 2）自身的非商业开源功能实现，
// 逻辑按 PCL2 原版 VB.NET 源码（Modules/Base/ModBase.vb 中的 "搜索" 区域）逐句翻译为 C#，
// 未做算法层面的改写。再次感谢 PCL2 开源社区。
// ============================================================================

/// <summary>
/// 单个用于搜索的文本源（例如"中文名"和"简介"可以分别作为一个 SearchSource，各自带权重）。
/// </summary>
public class SearchSource
{
    /// <summary>该文本源的所有别名（同一个源里任意一个别名匹配上即可，取其中相似度最高的一个）。</summary>
    public string[] Aliases { get; }

    /// <summary>该文本源在综合评分里的权重。</summary>
    public double Weight { get; }

    public SearchSource(string[] aliases, double weight = 1) { Aliases = aliases; Weight = weight; }
    public SearchSource(string text, double weight = 1) { Aliases = new[] { text }; Weight = weight; }
}

/// <summary>用于搜索的一个候选项（包裹住实际数据 Item，附带搜索用的文本源和搜索结果）。</summary>
public class SearchEntry<T>
{
    /// <summary>该项目对应的源数据。</summary>
    public required T Item { get; init; }

    /// <summary>该项目用于搜索的文本源列表；每个文本源单独加权，源内多个别名取最高相似度。</summary>
    public required List<SearchSource> SearchSource { get; init; }

    /// <summary>本次搜索算出的相似度（0~1 左右，具体范围取决于输入，仅用于同一批结果内部排序）。</summary>
    public double Similarity { get; set; }

    /// <summary>是否为"完全匹配"（查询按空格拆分后的每一段都在某个别名里精确出现）。</summary>
    public bool AbsoluteRight { get; set; }
}

/// <summary>
/// 移植自 PCL2 的模糊字符串搜索引擎。核心思路：
/// 在源文本里贪心地找查询串的最长连续匹配片段，找到就从源文本里"抠掉"这段避免重复计分，
/// 再从查询串剩余部分继续找下一段，如此重复直到查询串扫描完；每段匹配按长度做非线性加权
/// （越长的连续匹配加分越多）、按位置加权（匹配位置越接近查询串里的对应位置加分越多），
/// 最后按"总加权长度 / 查询长度"和"源文本长度倒数"综合出一个相似度分数。
/// 这样短查询词命中长源文本中的一个子串也能获得较高分数，同时避免长源文本"稀释"命中的问题。
/// </summary>
public static class FuzzySearch
{
    /// <summary>
    /// 获取搜索文本的相似度。Source 是被搜索的长内容，Query 是用户输入的搜索文本。
    /// </summary>
    private static double SearchSimilarity(string? source, string? query)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(query)) return 0;

        var str = new System.Text.StringBuilder(source.ToLowerInvariant().Replace(" ", ""));
        query = query.ToLowerInvariant().Replace(" ", "");
        var queryLength = query.Length;
        if (queryLength == 0) return 0;

        var sourceLength = str.Length;
        var qp = 0;
        double lenSum = 0;

        while (qp < queryLength)
        {
            // 以 qp 为查询串起点，在当前源文本(str，已去掉之前匹配段)里找最长连续匹配
            var sp = 0;
            var lenMax = 0;
            var spMax = 0;
            var currentSourceLength = str.Length;

            while (sp < currentSourceLength)
            {
                var len = 0;
                while (qp + len < queryLength && sp + len < currentSourceLength && str[sp + len] == query[qp + len])
                    len++;

                if (len > lenMax) { lenMax = len; spMax = sp; }
                sp += len > 0 ? len : 1;
            }

            if (lenMax > 0)
            {
                str.Remove(spMax, lenMax); // 从源文本里移除已匹配片段，避免下一轮重复计分
                var incWeight = Math.Pow(1.4, 3 + lenMax) - 3.6; // 长度加成：连续匹配越长，边际加分越高
                incWeight *= 1 + 0.3 * Math.Max(0, 3 - Math.Abs(qp - spMax)); // 位置加成：匹配位置越对应，加分越多
                lenSum += incWeight;
            }

            qp += lenMax > 0 ? lenMax : 1;
        }

        // 结果 = (总加权匹配长度 / 查询长度) * (源文本长度影响，越短的源文本单位匹配价值越高) * 短查询词加成
        return (lenSum / queryLength) * (3 / Math.Sqrt(sourceLength + 15)) * (queryLength <= 2 ? 3 - queryLength : 1);
    }

    /// <summary>获取多段带权重文本源的加权相似度（每段取其别名里的最高相似度，再按权重加权平均）。</summary>
    private static double SearchSimilarityWeighted(List<SearchSource> source, string query)
    {
        double totalWeight = 0, sum = 0;
        foreach (var pair in source)
        {
            if (pair.Aliases.Length > 0)
                sum += pair.Aliases.Max(a => SearchSimilarity(a, query)) * pair.Weight;
            totalWeight += pair.Weight;
        }
        return totalWeight > 0 ? sum / totalWeight : 0;
    }

    /// <summary>
    /// 对一批候选项进行加权模糊搜索，返回相似度较高的若干条结果（会原地修改每项的 Similarity /
    /// AbsoluteRight 字段）。完全匹配的项全部返回，不受 maxBlurCount 限制；模糊匹配的项
    /// 按相似度降序最多取 maxBlurCount 条，且相似度需 >= minBlurSimilarity 才会被纳入候选。
    /// </summary>
    public static List<SearchEntry<T>> Search<T>(List<SearchEntry<T>> entries, string query,
        int maxBlurCount = 5, double minBlurSimilarity = 0.1)
    {
        var resultList = new List<SearchEntry<T>>();
        if (entries.Count == 0) return resultList;

        var queryParts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<SearchEntry<T>>();

        foreach (var entry in entries)
        {
            entry.Similarity = SearchSimilarityWeighted(entry.SearchSource, query);
            entry.AbsoluteRight = queryParts.Length > 0 && queryParts.All(part =>
                entry.SearchSource.Any(source =>
                    source.Aliases.Any(alias =>
                        alias.Replace(" ", "").Contains(part, StringComparison.OrdinalIgnoreCase))));

            if (entry.AbsoluteRight || entry.Similarity >= minBlurSimilarity)
                candidates.Add(entry);
        }

        // 完全匹配优先，其余按相似度降序
        candidates = candidates
            .OrderByDescending(e => e.AbsoluteRight)
            .ThenByDescending(e => e.Similarity)
            .ToList();

        var blurCount = 0;
        foreach (var entry in candidates)
        {
            if (entry.AbsoluteRight)
            {
                resultList.Add(entry);
            }
            else
            {
                if (blurCount == maxBlurCount) break;
                resultList.Add(entry);
                blurCount++;
            }
        }
        return resultList;
    }
}
