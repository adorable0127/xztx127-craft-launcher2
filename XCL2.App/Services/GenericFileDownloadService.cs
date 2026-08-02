using System.IO;
using System.Net.Http;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「下载自定义文件」：对应截图里"用高速多线程下载引擎下载任意文件"的需求。
/// 这里不接入项目里 DownloadService 那套面向"游戏核心文件批量下载"设计的并发/限速/
/// 智能限速体系（那一套是为 libraries/assets 这种成千上万个小文件的场景设计的，
/// 对"下载单个用户指定的任意文件"这种场景反而过重），改用一个简单直接的单文件
/// HttpClient 下载 + 进度回调，职责更单一、代码量也更小。
///
/// 关于截图里提到的"部分网站（例如百度网盘）可能会报错 403 已禁止"：这是网盘一类服务
/// 对外部程序发起的下载请求做的反爬虫/防盗链校验，不是这里的下载逻辑本身有问题，
/// 如实保留这条提示，让用户对"某些链接下不了"有心理预期，而不是误以为是启动器坏了。
/// </summary>
public class GenericFileDownloadService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// 下载任意 URL 到指定目录/文件名。userAgent 为空时使用 HttpClient 默认值
    /// （不强行伪装成浏览器 UA，是否需要自定义 UA 交给用户自己根据目标网站的要求填写，
    /// 呼应截图里"User-Agent"这个独立输入框的设计）。
    /// </summary>
    public async Task<string> DownloadAsync(string url, string saveDir, string fileName, string? userAgent,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("下载地址不能为空。", nameof(url));
        Directory.CreateDirectory(saveDir);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            // 用户没填文件名：尝试从 URL 路径里取最后一段当文件名，取不到就兜底成一个
            // 带时间戳的通用名，避免因为文件名冲突/为空导致下载失败。
            try
            {
                var uri = new Uri(url);
                fileName = Path.GetFileName(uri.LocalPath);
            }
            catch { /* URL 格式不规范时忽略，走下面的兜底 */ }

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"下载失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n" +
                "如果是网盘类地址（如百度网盘），可能是对方站点拒绝了外部程序的直接下载请求，" +
                "并非启动器本身的问题，可以尝试改用官方客户端下载或先转存到其它直链服务。");

        var totalBytes = resp.Content.Headers.ContentLength ?? -1;
        var destPath = Path.Combine(saveDir, fileName);
        var tempPath = destPath + ".tmp";

        await using (var fs = File.Create(tempPath))
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                if (totalBytes > 0)
                {
                    progress?.Report(new ProgressInfo("下载文件", (int)(totalRead / 1024), (int)(totalBytes / 1024), fileName));
                }
            }
        }
        File.Move(tempPath, destPath, overwrite: true);
        return destPath;
    }
}
