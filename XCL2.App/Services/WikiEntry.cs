using System.IO;
using System.Reflection;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

// ============================================================================
// 鸣谢 / Credit：本文件的数据结构与解析逻辑、以及内嵌的 WikiEntries.txt 数据文件，
// 均来自 Plain Craft Launcher 2（PCL2，作者 Hakoyu 及社区贡献者，
// https://github.com/Hex-Dragon/PCL2）。中文名数据库是 PCL2 团队长期维护的 MC 百科
// Mod 中文名对照表，本项目仅将其加载/解析逻辑从原版 C# (PCLCS/Resource/WikiEntry.cs)
// 移植过来供 XCL2 使用，数据版权与整理成果归属 PCL2 项目及原作者，在此特别感谢。
// ============================================================================

/// <summary>
/// MC 百科条目：Mod 的中文译名 + 各平台 Slug + 热度排名。
///
/// 移植自 Plain Craft Launcher 2 (PCL2) 的 WikiEntry / WikiEntries.txt 数据库与解析逻辑，
/// 用于支撑"中文关键词直接搜到 Modrinth/CurseForge 上的 Mod"这个功能——原本 XCL2 里的
/// ModNameDictionary 只是一份几百条的手工词典，覆盖面有限；这里换成 PCL2 同款的近 3 万条
/// 全量数据库 + 模糊搜索算法，覆盖面和匹配准确度都大幅提升。
///
/// 数据文件格式（每行一条，用 ¨ 分隔一行内的多个别名条目，用 | 分隔"Slug 段"和"中文名段"）：
///   - Slug 段本身用 @ 表示不同平台：
///       "xxx@"   → 只有 CurseForge 的 slug 是 xxx
///       "@xxx"   → 只有 Modrinth 的 slug 是 xxx
///       "xxx@yyy"→ CurseForge 是 xxx，Modrinth 是 yyy
///       "xxx"    → 两个平台 slug 相同，都是 xxx（没有 @）
///   - 中文名段可能包含 "*"，代表"用英文名替换掉这个星号"（用于形如"某某模组 (Xxx)"的展示名）。
///   - 文件最后一行是所有条目按行号排列的热度数据，用 3 字符一组的 Base-86 编码；
///     数值越大代表该 Mod 在 MC 百科的浏览量排名越靠前（用于打分时的热度加成）。
/// </summary>
public class WikiEntry
{
    /// <summary>在原始数据文件中的行号（同一行内的多个别名条目共享同一行号/热度）。</summary>
    public int Id;

    /// <summary>中文译名。null 代表没有翻译（该条目只用于记录 Slug 对照，不参与中文搜索）。</summary>
    public string? ChineseName;

    /// <summary>各平台对应的 Slug（例如 "sodium"）。没有对应键代表该 Mod 不在这个平台上架。</summary>
    public Dictionary<ModSource, string> Slugs { get; } = new();

    /// <summary>MC 百科浏览量热度（越大越热门，用于同等相似度时的排序加成）。</summary>
    public int Popularity;

    private static readonly Lazy<List<WikiEntry>> _all = new(Load);

    /// <summary>内置数据库中的所有 MC 百科条目（首次访问时惰性加载并缓存，全程序生命周期只解析一次）。</summary>
    public static List<WikiEntry> All => _all.Value;

    private static List<WikiEntry> Load()
    {
        var text = ReadEmbeddedText("XCL2.App.Resources.Data.WikiEntries.txt");
        // 用 \n 切分而不是 Environment.NewLine：数据文件是从 PCL2 原样搬运过来的 Unix 换行文本，
        // 统一按 \n 切、再 TrimEnd('\r') 兜底，不依赖运行平台的换行约定。
        var dataLines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (dataLines.Count > 0 && dataLines[0] == "") dataLines.RemoveAt(0); // 文件首行是空行（对照原始格式）
        if (dataLines.Count > 0 && dataLines[^1] == "") dataLines.RemoveAt(dataLines.Count - 1);

        // 最后一行是热度数据：每 3 个 Base-86 字符解码出一个整数，按行号顺序对应前面的每一条 Mod 记录
        var popularityLine = dataLines[^1];
        dataLines.RemoveAt(dataLines.Count - 1);
        var popularities = new Queue<int>();
        for (var i = 0; i + 3 <= popularityLine.Length; i += 3)
            popularities.Enqueue(DecodeBase86(popularityLine.Substring(i, 3)));

        var results = new List<WikiEntry>(dataLines.Count * 2);
        var lineNumber = 0;
        foreach (var lineData in dataLines)
        {
            lineNumber++;
            if (lineData.Length == 0) continue;

            var popularity = popularities.Count > 0 ? popularities.Dequeue() : 0;
            foreach (var entryData in lineData.Split('¨'))
            {
                if (entryData.Length == 0) continue;
                var parts = entryData.Split('|');
                var slugsRaw = parts[0];
                var entry = new WikiEntry { Id = lineNumber, Popularity = popularity };

                if (slugsRaw.StartsWith('@'))
                {
                    entry.Slugs[ModSource.Modrinth] = slugsRaw[1..];
                }
                else if (slugsRaw.EndsWith('@'))
                {
                    var slug = slugsRaw[..^1];
                    entry.Slugs[ModSource.CurseForge] = slug;
                    entry.Slugs[ModSource.Modrinth] = slug;
                }
                else if (slugsRaw.Contains('@'))
                {
                    var split = slugsRaw.Split('@');
                    entry.Slugs[ModSource.CurseForge] = split[0];
                    entry.Slugs[ModSource.Modrinth] = split[1];
                }
                else if (slugsRaw.Length > 0)
                {
                    entry.Slugs[ModSource.CurseForge] = slugsRaw;
                }

                if (parts.Length >= 2)
                {
                    var chineseName = parts[^1];
                    if (chineseName.Contains('*'))
                    {
                        // 用第一个可用 Slug 转成"首字母大写、连字符转空格"的英文名替换掉 "*"
                        var englishName = Capitalize(entry.Slugs.Values.FirstOrDefault()?.Replace('-', ' ') ?? "");
                        chineseName = chineseName.Replace("*", $" ({englishName})");
                    }
                    entry.ChineseName = chineseName;
                }

                results.Add(entry);
            }
        }
        return results;
    }

    private static string Capitalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(" ", words);
    }

    /// <summary>
    /// Base-86 解码：字母表是可打印 ASCII 0x21–0x7D 中排除 " - . \ _ ` | 共 7 个字符后剩下的 86 个，
    /// 按 ASCII 序号排列。这是从 WikiEntries.txt 尾部的热度编码数据反推出的字母表（该文件由 PCL2
    /// 编码写出，具体编码器代码不在本项目引用范围内，这里按数据实际使用的字符集重建了对应的解码器）。
    /// </summary>
    private static readonly char[] Base86Alphabet = BuildBase86Alphabet();

    private static char[] BuildBase86Alphabet()
    {
        var excluded = new HashSet<char> { '"', '-', '.', '\\', '_', '`', '|' };
        var list = new List<char>();
        for (var c = (char)0x21; c <= (char)0x7D; c++)
            if (!excluded.Contains(c)) list.Add(c);
        return list.ToArray();
    }

    private static readonly Dictionary<char, int> Base86Lookup =
        Base86Alphabet.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);

    private static int DecodeBase86(string chunk)
    {
        var value = 0;
        foreach (var c in chunk)
        {
            if (!Base86Lookup.TryGetValue(c, out var digit)) continue; // 未知字符按 0 处理，不影响整体排序意义
            value = value * 86 + digit;
        }
        return value;
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"未找到内嵌资源：{resourceName}（请确认已在 csproj 中将 WikiEntries.txt 设为 EmbeddedResource）");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
