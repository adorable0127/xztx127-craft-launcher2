using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>统一镜像候选、进程内健康退避与有校验的原子下载。</summary>
public static class DownloadEndpoints
{
    private sealed class HostHealth
    {
        public int ConsecutiveFailures;
        public DateTime RetryAfterUtc;
        public int Penalty
        {
            get { lock (this) return DateTime.UtcNow < RetryAfterUtc ? ConsecutiveFailures : 0; }
        }
    }

    private static readonly ConcurrentDictionary<string, HostHealth> Health = new(StringComparer.OrdinalIgnoreCase);
    public static bool PreferMirror => ConfigService.Active?.Config.Source == DownloadSource.BMCLAPI;

    public static void ReportFailure(string url)
    {
        if (HostOf(url) is not { } host) return;
        var health = Health.GetOrAdd(host, _ => new HostHealth());
        lock (health)
        {
            if (DateTime.UtcNow >= health.RetryAfterUtc) health.ConsecutiveFailures = 0;
            health.ConsecutiveFailures = Math.Min(health.ConsecutiveFailures + 1, 5);
            health.RetryAfterUtc = DateTime.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, health.ConsecutiveFailures)));
        }
    }

    public static void ReportSuccess(string url)
    {
        if (HostOf(url) is { } host) Health.TryRemove(host, out _);
    }

    private static string? HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    public static IReadOnlyList<string> Candidates(string officialUrl, bool preferMirror)
    {
        var mirrors = DownloadMirrorPolicy.Mirrors(officialUrl, ConfigService.Active?.Config.DownloadMirrors);
        return OrderCandidates(preferMirror ? mirrors.Append(officialUrl) : new[] { officialUrl }.Concat(mirrors));
    }

    public static string? ToMirror(string officialUrl) =>
        DownloadMirrorPolicy.Mirrors(officialUrl, ConfigService.Active?.Config.DownloadMirrors).FirstOrDefault();

    public static IReadOnlyList<string> AllCandidates(string officialUrl, bool preferMirror) => Candidates(officialUrl, preferMirror);

    public static IReadOnlyList<string> OrderCandidates(IEnumerable<string> candidates) => candidates
        .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal)
        .OrderBy(url => Health.TryGetValue(HostOf(url) ?? "", out var h) ? h.Penalty : 0).ToArray();

    public static IReadOnlyList<string> UnhealthyHosts() =>
        Health.Where(kv => kv.Value.Penalty > 0).Select(kv => kv.Key).ToArray();

    public static void ResetHealth() => Health.Clear();

    private static bool HasCredentialHeaders(HttpClient http) => http.DefaultRequestHeaders.Any(header =>
        header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        header.Key.Contains("token", StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<string>> ResolveCandidatesAsync(HttpClient http, string officialUrl,
        bool preferMirror, string name, string? sha1, CancellationToken ct)
    {
        // 不把认证头或可能含签名的 URL 转交给公共/自定义镜像。
        if (HasCredentialHeaders(http) || !DownloadMirrorPolicy.MayMirror(officialUrl)) return new[] { officialUrl };
        return OrderCandidates(await Plugins.PluginProviderRegistry.AddMirrorAsync(
            officialUrl, name, sha1, Candidates(officialUrl, preferMirror), ct));
    }

    public static async Task<string> GetStringWithFallbackAsync(HttpClient http, string officialUrl,
        bool preferMirror, string friendlyMessage, CancellationToken ct = default)
    {
        var candidates = await ResolveCandidatesAsync(http, officialUrl, preferMirror, "metadata.json", null, ct);
        Exception? lastError = null;
        foreach (var url in candidates)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var resolved = await Plugins.PluginInterceptionRegistry.PrepareDownloadAsync(url, "metadata.json", null, attempt.Token);
                var result = await http.GetStringAsync(resolved, attempt.Token);
                // 镜像的 200 HTML 错误页不能阻断后续官方候选。
                using var document = JsonDocument.Parse(result);
                ReportSuccess(url);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not Plugins.PluginFlowCanceledException)
            {
                ReportFailure(url);
                lastError = ex;
            }
        }
        throw new InvalidOperationException(friendlyMessage, lastError);
    }

    public static Task<string> DownloadFileWithFallbackAsync(HttpClient http, string officialUrl, bool preferMirror,
        string destPath, string friendlyMessage, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default,
        string? expectedSha1 = null, string? expectedSha512 = null, string? expectedMd5 = null,
        long? expectedSize = null, string? userAgent = null, string? expectedSha256 = null) =>
        DownloadFilesWithFallbackAsync(http, new[] { officialUrl }, preferMirror, destPath, friendlyMessage,
            progress, ct, expectedSha1, expectedSha512, expectedMd5, expectedSize, userAgent, expectedSha256);

    /// <summary>所有官方清单 URL 都参与回退；哈希不匹配与空文件同样触发换源，不提交坏文件。</summary>
    public static async Task<string> DownloadFilesWithFallbackAsync(HttpClient http, IEnumerable<string> officialUrls,
        bool preferMirror, string destPath, string friendlyMessage, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default, string? expectedSha1 = null, string? expectedSha512 = null,
        string? expectedMd5 = null, long? expectedSize = null, string? userAgent = null, string? expectedSha256 = null)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(destPath);
        var expanded = new List<string>();
        foreach (var url in officialUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal).Take(16))
            expanded.AddRange(await ResolveCandidatesAsync(http, url, preferMirror, name, expectedSha1, ct));
        var candidates = OrderCandidates(expanded);
        if (candidates.Count == 0) throw new InvalidOperationException("没有可用的下载地址。");
        Exception? lastError = null;
        var speedLimit = Math.Max(0, ConfigService.Active?.Config.DownloadSpeedLimitKBps ?? 0) * 1024L;
        // 首轮迅速换源，全部失败后再进行一次有限重试；不对同一个坏源连续请求三次。
        for (var round = 0; round < 2; round++)
        {
            if (round > 0) await Task.Delay(500, ct);
            foreach (var url in OrderCandidates(candidates))
            {
                var temporary = destPath + ".fbpart-" + Guid.NewGuid().ToString("N");
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var resolved = await Plugins.PluginInterceptionRegistry.PrepareDownloadAsync(url, name, expectedSha1, attempt.Token);
                    using var request = new HttpRequestMessage(HttpMethod.Get, resolved);
                    if (!string.IsNullOrWhiteSpace(userAgent)) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    response.EnsureSuccessStatusCode();
                    var length = response.Content.Headers.ContentLength;
                    if (expectedSize is > 0 && length is > 0 && expectedSize != length)
                        throw new InvalidDataException("下载大小与资源清单不一致。");
                    await using (var input = await response.Content.ReadAsStreamAsync(attempt.Token))
                    await using (var output = File.Create(temporary))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        var watch = Stopwatch.StartNew();
                        int read;
                        while (true)
                        {
                            attempt.CancelAfter(TimeSpan.FromSeconds(45));
                            read = await input.ReadAsync(buffer, attempt.Token);
                            if (read == 0) break;
                            await output.WriteAsync(buffer.AsMemory(0, read), attempt.Token);
                            done += read;
                            if (speedLimit > 0)
                            {
                                var delay = done * 1000.0 / speedLimit - watch.Elapsed.TotalMilliseconds;
                                if (delay > 0)
                                {
                                    // 自身的限速等待不应被误判为网络无响应。
                                    attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                                    await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                                }
                            }
                            var total = expectedSize is > 0 ? expectedSize.Value : length ?? 0;
                            progress?.Report(new ProgressInfo("正在下载 " + name, Kilobytes(done), Kilobytes(total),
                                $"{done / 1048576.0:F1} MB · {HostOf(url)}"));
                        }
                        if (length is >= 0 && done != length.Value) throw new InvalidDataException("下载连接提前结束，文件不完整。");
                    }
                    attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                    ct.ThrowIfCancellationRequested();
                    await Task.Run(() => VerifyFile(temporary, expectedSha1, expectedSha512, expectedMd5, expectedSize, expectedSha256, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, destPath, overwrite: true);
                    ReportSuccess(url);
                    return destPath;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not Plugins.PluginFlowCanceledException)
                {
                    ReportFailure(url);
                    lastError = ex;
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }
        throw new IOException(friendlyMessage + "（已尝试所有可用下载源）", lastError);
    }

    private static int Kilobytes(long bytes) => (int)Math.Clamp(bytes / 1024, 0, int.MaxValue);

    internal static void VerifyFile(string path, string? sha1, string? sha512, string? md5, long? expectedSize, string? sha256 = null, CancellationToken ct = default)
    {
        var length = new FileInfo(path).Length;
        if (length == 0 || expectedSize is > 0 && expectedSize != length)
            throw new InvalidDataException("下载文件为空或大小不匹配。");
        foreach (var (expected, algorithm) in new[] { (sha512, HashAlgorithmName.SHA512), (sha256, HashAlgorithmName.SHA256), (sha1, HashAlgorithmName.SHA1), (md5, HashAlgorithmName.MD5) })
        {
            if (string.IsNullOrWhiteSpace(expected)) continue;
            using var hash = IncrementalHash.CreateHash(algorithm);
            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"文件校验失败（{algorithm.Name} 不匹配），正在尝试其他下载源。");
        }
    }
}
