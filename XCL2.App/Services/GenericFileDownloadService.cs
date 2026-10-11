using System.IO;
using System.Net.Http;

namespace XCL2.App.Services;

/// <summary>
/// 百宝箱任意文件下载。公开文件接入统一镜像回退、限速与原子提交；
/// 用户指定的签名链接保持直连，自定义 User-Agent 保留在每次请求中。
/// 无来源哈希的文件只能验证传输完整性，不能据此证明内容真实。
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

        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            throw new ArgumentException("文件名不能包含目录或驱动器路径。", nameof(fileName));
        var destPath = Path.Combine(saveDir, fileName);
        return await DownloadEndpoints.DownloadFileWithFallbackAsync(_http, url, DownloadEndpoints.PreferMirror,
            destPath, "下载失败：请检查网络及链接的直接下载权限", progress, ct, userAgent: userAgent);
    }
}
