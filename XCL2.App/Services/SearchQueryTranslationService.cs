using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>
/// 社区资源搜索专用的“用户原始中文 -> 英文搜索词”兜底翻译。
///
/// WikiEntries.txt 很适合“钠 -> Sodium”这种已有 MC 百科条目的专名，但如果静态库最相近条目
/// 与用户输入的匹配度低于 70%，继续拿那个条目的 slug 去搜反而会把结果带到错误项目。
/// 这时才调用通用翻译，把用户真正输入的词翻成英文，然后把英文交给 Modrinth/CurseForge。
///
/// 这里只在用户执行社区资源搜索且确实需要兜底时联网，不做后台请求；结果在本进程缓存。
/// 第一个翻译端点不可用时会尝试第二个公开端点；两者都失败才退回原有本地逻辑，
/// 不会因为“翻译服务暂时不可用”让整个社区资源搜索直接报错。
/// </summary>
public static class SearchQueryTranslationService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    static SearchQueryTranslationService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher/2.2.9 community-search-translation");
    }

    public static async Task<string?> TranslateToEnglishAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        if (!ChineseModSearchTranslator.IsChineseQuery(query)) return query;
        if (Cache.TryGetValue(query, out var cached)) return cached;

        var translated = await TryGoogleAsync(query, ct) ?? await TryMyMemoryAsync(query, ct);
        translated = translated?.Trim();
        if (string.IsNullOrWhiteSpace(translated) || string.Equals(translated, query, StringComparison.Ordinal))
            return null;

        Cache[query] = translated;
        return translated;
    }

    private static async Task<string?> TryGoogleAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single" +
                      "?client=gtx&sl=auto&tl=en&dt=t&q=" + Uri.EscapeDataString(query);
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var segments = doc.RootElement[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            var parts = new List<string>();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0) continue;
                if (segment[0].ValueKind == JsonValueKind.String && segment[0].GetString() is { Length: > 0 } text)
                    parts.Add(text);
            }
            return string.Concat(parts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryMyMemoryAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get" +
                      "?langpair=zh-CN%7Cen-US&q=" + Uri.EscapeDataString(query);
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var text) || text.ValueKind != JsonValueKind.String)
                return null;
            return WebUtility.HtmlDecode(text.GetString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
