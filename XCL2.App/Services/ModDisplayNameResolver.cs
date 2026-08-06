using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 搜索结果"展示名"解析：中文优先，英文名跟在后面（"中文名 (English Name)"），没有中文对照的
/// 保持原样只显示英文。跟 ChineseModSearchTranslator（负责把用户输入的中文关键词翻译成英文
/// 搜索词）是两回事——这里是反过来，把搜索结果的英文标题"翻"成中文展示，两者共用同一份
/// WikiEntry 数据库，但各自独立、互不依赖。
///
/// 匹配方式：只做"Slug 精确匹配"，不做模糊/相似度匹配。原因是这里是展示环节而不是搜索环节——
/// 模糊匹配用于搜索时"猜"用户想要哪个是可接受的（猜错了用户看结果不对会重新搜），但用于展示时
/// 如果猜错，会把一个不相关 Mod 的中文名安在这个结果标题上，等于展示了错误信息，比不展示中文名
/// 更糟。所以只有能定位到"确实是这个 Slug"的情况才替换标题，找不到就原样显示英文标题。
///
/// 平台差异：
/// - Modrinth 搜索结果自带精确的 project slug 字段，可以直接查表精确匹配。
/// - CurseForge 官方 v1 /mods/search 接口不在响应体里返回 slug（只有 id 和 name），
///   所以 CurseForge 结果目前查不到中文名，继续显示英文标题——不用 Mod 名称做模糊匹配代替，
///   避免上面说的"张冠李戴"问题。以后如果 CurseForge 搜索改用带 slug 的接口（如
///   /mods/{id} 详情接口批量补查），可以在这里加一条 CurseForge 分支复用同一套格式化逻辑。
/// </summary>
public static class ModDisplayNameResolver
{
    /// <summary>WikiEntry.All 按 (Source, Slug) 建的索引，Slug 统一转小写比较——
    /// Modrinth slug 大小写在实践中基本都是小写，这里做一次 ToLowerInvariant 兜底更保险。
    /// 惰性构建一次，跟 WikiEntry.All 本身的惰性加载策略一致。</summary>
    private static readonly Lazy<Dictionary<(ModSource Source, string Slug), string>> _bySlug = new(() =>
    {
        var dict = new Dictionary<(ModSource, string), string>();
        foreach (var entry in WikiEntry.All)
        {
            if (string.IsNullOrEmpty(entry.ChineseName)) continue;
            foreach (var (source, slug) in entry.Slugs)
            {
                if (string.IsNullOrEmpty(slug)) continue;
                var key = (source, slug.ToLowerInvariant());
                // 同一个 slug 在数据文件里可能出现多次（不同别名行），保留第一条即可，
                // 数据整理时越靠前的别名通常越是"官方/最常用"的那个中文名。
                if (!dict.ContainsKey(key)) dict[key] = entry.ChineseName!;
            }
        }
        return dict;
    });

    /// <summary>
    /// 给定平台 + Slug + 该结果原本的英文标题，返回展示用的标题：
    /// 查到中文名 → "中文名 (English Title)"；查不到 → 原样返回英文标题。
    /// slug 为空（目前只有 CurseForge 会传空）时直接返回原标题，不查表。
    /// </summary>
    public static string Resolve(ModSource source, string? slug, string englishTitle)
    {
        if (string.IsNullOrEmpty(slug)) return englishTitle;
        if (!_bySlug.Value.TryGetValue((source, slug.ToLowerInvariant()), out var chineseName))
            return englishTitle;

        // 中文名和英文标题偶尔会重复（数据库里个别条目中文名就是照抄英文名），这种情况不用再
        // 显示一遍 "Foo (Foo)"，直接返回英文标题即可。
        if (string.Equals(chineseName, englishTitle, StringComparison.OrdinalIgnoreCase))
            return englishTitle;

        return $"{chineseName} ({englishTitle})";
    }
}
