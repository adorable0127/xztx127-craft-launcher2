using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>只从仓库 GitHub Releases API 获取更新说明，不执行发布内容中的代码或 HTML。</summary>
public static class GitHubReleaseNotesService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private const string Api = "https://api.github.com/repos/adorable0127/xztx127-craft-launcher2/releases/";
    static GitHubReleaseNotesService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-ReleaseNotes");
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }
    public sealed record Notes(string Latest, string Current, string LatestUrl);

    /// <summary>单条历史发布记录：给「历史版本更新日志」列表用。</summary>
    public sealed record ReleaseEntry(string Tag, string Body, string Url, string PublishedAt);

    /// <summary>当前程序集版本号，格式跟 Release tag 尽量对齐（"v" + Major.Minor.Build）。
    /// FetchAsync 和 FetchHistoryAsync 都用这个来判断某一条 Release 是不是"当前安装版本"。</summary>
    public static string CurrentVersionString()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? "未知" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>只取"当前安装版本"这一条 Release 的日志正文，供更新完成后自动弹出的
    /// UpdateChangelogPopup 使用——它不需要"最新发布"那一半（用户已经就是最新版了，
    /// 重复展示反而让人confused这是不是又有新版本），只关心"我刚更新到的这个版本改了什么"。
    /// 找不到对应 tag 时返回 null，调用方自行降级提示。</summary>
    public static async Task<ReleaseEntry?> FetchCurrentVersionNotesAsync()
    {
        var currentVersion = CurrentVersionString();
        foreach (var tag in new[] { "v" + currentVersion, currentVersion })
        {
            using var response = await Http.GetAsync(Api + "tags/" + Uri.EscapeDataString(tag));
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var el = doc.RootElement;
            var body = el.TryGetProperty("body", out var b) ? b.GetString() ?? "（未填写）" : "（未填写）";
            var url = el.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var published = el.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "";
            return new ReleaseEntry(tag, body, url, published);
        }
        return null;
    }

    /// <summary>拉取最近若干条历史发布日志（不含"最新发布"重复的那一条也没关系，列表里再看一遍
    /// 无所谓），供「历史版本更新日志」展开列表使用。count 默认 10，避免一次拉太多。</summary>
    public static async Task<List<ReleaseEntry>> FetchHistoryAsync(int count = 10)
    {
        using var response = await Http.GetAsync(Api + $"?per_page={Math.Clamp(count, 1, 30)}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var list = new List<ReleaseEntry>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "未知" : "未知";
            var body = el.TryGetProperty("body", out var b) ? b.GetString() ?? "（未填写）" : "（未填写）";
            var url = el.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var published = el.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "";
            list.Add(new ReleaseEntry(tag, body, url, published));
        }
        return list;
    }

    public static async Task<Notes> FetchAsync()
    {
        using var latest = await Http.GetAsync(Api + "latest");
        latest.EnsureSuccessStatusCode();
        using var latestDoc = JsonDocument.Parse(await latest.Content.ReadAsStringAsync());
        var l = latestDoc.RootElement;
        string latestTag = l.GetProperty("tag_name").GetString() ?? "未知";
        string latestBody = l.TryGetProperty("body", out var lb) ? lb.GetString() ?? "（未填写）" : "（未填写）";
        string latestUrl = l.TryGetProperty("html_url", out var lu) ? lu.GetString() ?? "" : "";
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        string currentVersion = version == null ? "未知" : $"{version.Major}.{version.Minor}.{version.Build}";
        string current = $"当前本地版本：{currentVersion}\n";
        // 先按与当前程序集一致的 tag 请求；tag 不存在时明确显示未发布，而非拿最新版本冒充本地版本。
        foreach (var tag in new[] { "v" + currentVersion, currentVersion })
        {
            using var response = await Http.GetAsync(Api + "tags/" + Uri.EscapeDataString(tag));
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var el = doc.RootElement;
            current += el.TryGetProperty("body", out var body) ? body.GetString() ?? "（未填写）" : "（未填写）";
            return new Notes($"最新发布 {latestTag}\n{latestBody}", current, latestUrl);
        }
        return new Notes($"最新发布 {latestTag}\n{latestBody}", current + "GitHub 上暂未找到与当前程序版本一致的 Release。", latestUrl);
    }
}
