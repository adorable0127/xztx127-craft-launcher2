using System.Net.Http;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 接入 MC百科 (www.mcmod.cn)，主要给中文用户提供"中文名搜索/中文百科介绍"的补充来源。
///
/// 重要限制：MC百科没有官方公开 API，也不提供直接的 mod 文件下载链接（它是一个百科/资料站，
/// 不是像 Modrinth/CurseForge 那样的托管平台）。这里用搜索页 HTML 做轻量解析，只用来"搜出
/// 条目名 + 百科页面链接"，不负责下载安装——用户点进去之后是引导跳转到 MC百科官网页面查看
/// 详情（该 mod 具体的下载渠道由百科页面自己给出，通常最终还是指向 CurseForge/Modrinth/官方论坛）。
///
/// 因为 HTML 结构可能随时改版，解析全程做防御式处理：任何一步解析失败都返回空列表而不是抛异常，
/// 保证"MC百科解析挂了"不会导致整个综合搜索或下载中心不可用，静默降级即可。
/// </summary>
public class McModService
{
    private const string SearchUrl = "https://search.mcmod.cn/s?key={0}&filter=1";
    private readonly HttpClient _http;

    public McModService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
    }

    /// <summary>
    /// 按关键词搜索 MC百科条目（模组分类，classid=1，即"Mod"分类，排除资料/教程等其他类型）。
    /// query 为空时直接返回空列表——MC百科搜索页本身要求关键词，不支持空关键词浏览热门。
    /// </summary>
    public async Task<List<McModSearchHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        var result = new List<McModSearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        try
        {
            var url = string.Format(SearchUrl, Uri.EscapeDataString(query));
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return result;

            var html = await resp.Content.ReadAsStringAsync(ct);
            result = ParseSearchResults(html);
        }
        catch
        {
            // 网络失败/HTML 改版解析失败：静默返回空列表，交给上层展示"没有结果"，
            // 不影响用户同时使用的 Modrinth/CurseForge 搜索结果。
        }

        return result;
    }

    /// <summary>
    /// 极简正则解析 MC百科搜索结果页。MC百科搜索结果条目通常形如：
    /// &lt;a href="https://www.mcmod.cn/class/1234.html" ...&gt;&lt;span&gt;某某模组&lt;/span&gt;...
    /// 只抓取指向 /class/ (模组详情页) 的链接，过滤掉资料页/教程页等其他类型的搜索结果。
    /// 解析结果不保证完整（HTML 改版会直接导致抓不到，见类注释），只作为轻量补充来源。
    /// </summary>
    private static List<McModSearchHit> ParseSearchResults(string html)
    {
        var hits = new List<McModSearchHit>();
        if (string.IsNullOrEmpty(html)) return hits;

        var matches = Regex.Matches(html,
            "<a[^>]+href=\"(https?://www\\.mcmod\\.cn/class/\\d+\\.html)\"[^>]*>\\s*(?:<[^>]+>\\s*)*([^<]{2,60})",
            RegexOptions.IgnoreCase);

        var seen = new HashSet<string>();
        foreach (Match m in matches)
        {
            var link = m.Groups[1].Value.Trim();
            var title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (title.Length == 0 || !seen.Add(link)) continue;

            hits.Add(new McModSearchHit { Title = title, PageUrl = link });
            if (hits.Count >= 20) break; // 避免正则在异常 HTML 上跑出过多噪音条目
        }

        return hits;
    }
}

/// <summary>MC百科搜索结果条目：只有标题和百科页面链接，不含下载信息（见 McModService 类注释）。</summary>
public class McModSearchHit
{
    public string Title { get; set; } = "";
    public string PageUrl { get; set; } = "";
}
