using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;

namespace XCL2.App.Services;

/// <summary>
/// 下载端点解析：把一个官方 URL 展开成一串**按可用性排序的候选 URL**，并记录每个主机的健康度。
///
/// ===== 解决什么问题 =====
/// 旧的 DownloadService.RemapUrl 是"一锤子买卖"：用户在设置里选了官方源就永远走官方，
/// 选了 BMCLAPI 就永远走 BMCLAPI，**没有任何回退**。实际后果：
///
/// - BMCLAPI 偶发抽风（这是常态，不是意外）时，装一个版本要下几百个文件，
///   只要有一个文件卡住，重试 3 次全失败，整个安装就报错终止——用户看到的是
///   "下载失败"，但换个源明明就能下下来。
/// - 反过来选官方源的用户在国内网络下几乎必然超时，而 BMCLAPI 就在旁边可用。
/// - 整合包安装动辄 200+ 文件，单点失败概率被放大 200 倍，"整合包装到一半失败"
///   基本都是这么来的。
///
/// PCL/HMCL 口碑的一半来自"下得快且不失败"，靠的就是多源 + 自动切换 + 断点续传这三件套。
/// 这个类负责第一件：**多源**。
///
/// ===== 设计要点 =====
/// 1. **候选顺序跟用户设置走，但不锁死**：用户选官方就把官方排第一、镜像排后面，
///    反之亦然。只在前面的失败时才用后面的，所以正常情况下用户的选择被完全尊重。
/// 2. **主机健康度是进程内的、带时间衰减的**：某个主机连续失败会被降权，
///    一段时间后自动恢复（不是永久拉黑——镜像抽风通常是分钟级的）。
///    这样同一次安装里的后 199 个文件不会再去撞那个已知挂掉的源。
/// 3. **只做 URL 映射，不碰下载逻辑**：便于单独推理和替换。
/// </summary>
public static class DownloadEndpoints
{
    /// <summary>主机健康记录。失败计数会随时间衰减，避免一次偶发失败把一个源永久打入冷宫。</summary>
    private sealed class HostHealth
    {
        public int ConsecutiveFailures;
        public DateTime LastFailureUtc = DateTime.MinValue;

        /// <summary>惩罚分：越大排得越靠后。0 表示健康。</summary>
        public int Penalty
        {
            get
            {
                if (ConsecutiveFailures == 0) return 0;
                // 90 秒内的失败才算数——镜像抽风通常是分钟级的，
                // 过了这个窗口就当它已经恢复，重新给机会。
                var age = DateTime.UtcNow - LastFailureUtc;
                if (age > TimeSpan.FromSeconds(90)) return 0;
                return Math.Min(ConsecutiveFailures, 5);
            }
        }
    }

    private static readonly ConcurrentDictionary<string, HostHealth> Health = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>记一次失败。DownloadService 每次某个候选 URL 下载失败时调用。</summary>
    public static void ReportFailure(string url)
    {
        var host = HostOf(url);
        if (host == null) return;
        var h = Health.GetOrAdd(host, _ => new HostHealth());
        lock (h)
        {
            h.ConsecutiveFailures++;
            h.LastFailureUtc = DateTime.UtcNow;
        }
    }

    /// <summary>记一次成功——立刻清零该主机的失败计数，让它重新排到前面。</summary>
    public static void ReportSuccess(string url)
    {
        var host = HostOf(url);
        if (host == null) return;
        if (Health.TryGetValue(host, out var h))
        {
            lock (h) h.ConsecutiveFailures = 0;
        }
    }

    private static string? HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return null; }
    }

    // ===== 镜像映射表 =====
    // 每一项是 (官方前缀, 镜像前缀)。BMCLAPI 是国内最主要的 Minecraft 资源镜像，
    // 路径规则跟官方一一对应，所以纯前缀替换就够，不需要额外的路径变换。
    private static readonly (string Official, string Mirror)[] BmclMap =
    {
        ("https://launchermeta.mojang.com",            "https://bmclapi2.bangbang93.com"),
        ("https://piston-meta.mojang.com",             "https://bmclapi2.bangbang93.com"),
        ("https://launcher.mojang.com",                "https://bmclapi2.bangbang93.com"),
        ("https://piston-data.mojang.com",             "https://bmclapi2.bangbang93.com"),
        ("https://libraries.minecraft.net",            "https://bmclapi2.bangbang93.com/maven"),
        ("https://resources.download.minecraft.net",   "https://bmclapi2.bangbang93.com/assets"),
        ("https://maven.fabricmc.net",                 "https://bmclapi2.bangbang93.com/maven"),
        ("https://meta.fabricmc.net",                  "https://bmclapi2.bangbang93.com/fabric-meta"),
        // Quilt Meta（/v2、/v3 接口都同步）——实测 BMCLAPI 有对应的 quilt-meta 镜像。
        ("https://meta.quiltmc.org",                   "https://bmclapi2.bangbang93.com/quilt-meta"),
        ("https://maven.neoforged.net/releases",       "https://bmclapi2.bangbang93.com/maven"),
        ("https://files.minecraftforge.net/maven",     "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.minecraftforge.net",           "https://bmclapi2.bangbang93.com/maven"),
    };

    // ===== GitHub 前缀代理（ghproxy 系）=====
    // 处理 GitHub 直连（release 资产、raw 文件）在部分网络下不可达的问题：
    // 镜像 URL = 代理前缀 + 完整官方 URL。跟 BMCLAPI 的"前缀替换"规则不同，
    // 所以单独一张表。LoaderJarDownloadService（Cleanroom 等 GitHub Releases 发布的
    // 加载器）和基岩版服务端归档都靠它拿备选源。
    private static readonly string[] GhProxyPrefixes =
    {
        "https://ghp.ci/",
        "https://ghproxy.com/",
        "https://mirror.ghproxy.com/",
    };

    private static readonly string[] GithubHosts =
    {
        "https://github.com",
        "https://raw.githubusercontent.com",
    };

    /// <summary>
    /// 把一个官方 URL 展开成候选列表（去重、按主机健康度稳定排序）。
    ///
    /// preferMirror = 用户在设置里选了镜像源。它只影响**顺序**，不影响候选集合——
    /// 这正是"自动切换"的关键：用户的偏好排第一，另一个作为兜底始终存在。
    /// </summary>
    public static IReadOnlyList<string> Candidates(string officialUrl, bool preferMirror)
    {
        var list = new List<string>(4);

        var mirror = ToMirror(officialUrl);

        if (preferMirror && mirror != null)
        {
            list.Add(mirror);
            list.Add(officialUrl);
        }
        else
        {
            list.Add(officialUrl);
            if (mirror != null) list.Add(mirror);
        }

        // 去重（官方 URL 本身可能就已经是镜像地址，比如调用方传进来的是 Fabric meta 的镜像）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = list.Where(u => !string.IsNullOrWhiteSpace(u) && seen.Add(u)).ToList();

        // 按健康度稳定排序：只把"最近连续失败过的主机"往后挪，
        // 健康的保持调用方给定的相对顺序（OrderBy 是稳定排序）。
        return deduped
            .OrderBy(u => Health.TryGetValue(HostOf(u) ?? "", out var h) ? h.Penalty : 0)
            .ToList();
    }

    /// <summary>
    /// 官方 URL → 镜像 URL（BMCLAPI 前缀替换 + GitHub 前缀代理两类规则）；
    /// 没有对应规则时返回 null。
    /// </summary>
    public static string? ToMirror(string officialUrl)
    {
        if (string.IsNullOrWhiteSpace(officialUrl)) return null;

        foreach (var (official, mirrorPrefix) in BmclMap)
        {
            if (officialUrl.StartsWith(official, StringComparison.OrdinalIgnoreCase))
                return mirrorPrefix + officialUrl[official.Length..];
        }

        foreach (var host in GithubHosts)
        {
            if (officialUrl.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            {
                // 返回第一个代理前缀即可：多个 ghproxy 之间由健康度机制互相回退。
                // （Candidates 的调用方拿到的是一个 URL，多个代理走不到；所以这里只回一个，
                //   剩下的在下方 AllMirrors 里给 DownloadFileWithFallbackAsync 用。）
                return GhProxyPrefixes[0] + officialUrl;
            }
        }
        return null;
    }

    /// <summary>某个 URL 的全部候选（含所有 ghproxy 前缀），给批量下载回退逻辑用。</summary>
    public static IReadOnlyList<string> AllCandidates(string officialUrl, bool preferMirror)
    {
        var list = new List<string>(6);
        foreach (var url in Candidates(officialUrl, preferMirror))
        {
            if (!list.Contains(url, StringComparer.OrdinalIgnoreCase))
                list.Add(url);
        }

        foreach (var host in GithubHosts)
        {
            if (!officialUrl.StartsWith(host, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var prefix in GhProxyPrefixes.Skip(1))
            {
                var proxy = prefix + officialUrl;
                if (!list.Contains(proxy, StringComparer.OrdinalIgnoreCase))
                    list.Add(proxy);
            }
            break;
        }
        return list;
    }

    /// <summary>调试/界面展示用：当前被判定为"不健康"的主机列表。</summary>
    public static IReadOnlyList<string> UnhealthyHosts() =>
        Health.Where(kv => kv.Value.Penalty > 0).Select(kv => kv.Key).ToList();

    /// <summary>重置所有健康度记录（切换下载源、用户手动重试时调用）。</summary>
    public static void ResetHealth() => Health.Clear();

    /// <summary>
    /// 修复"Fabric 安装 404"类问题的根因：GetVersionManifestAsync/InstallVersionAsync 的
    /// version json、asset index，以及 ClientLoaderInstallService 里 Fabric/Quilt/Forge Meta
    /// 的 GetStringAsync 调用，之前全部是"单 URL、不回退"——用户选了镜像源(BMCLAPI)时，
    /// 一旦镜像对这个具体版本/接口正好没同步好(镜像抽风是常态，尤其是新版本刚发布的头几天)，
    /// 直接 404 到底，用户看到一句原始的 HttpRequestException，完全不知道换源就能解决。
    /// 而同样是这个源的**文件**下载(DownloadFileAsync)其实早就有 Candidates() 多候选回退，
    /// 只有这几个"单次小请求"的元数据接口没享受到——这个方法就是把同样的回退能力补给它们：
    /// 依次尝试 Candidates() 给出的候选 URL(顺序按用户偏好+主机健康度)，某个 404/失败就换下一个，
    /// 全部试完还失败才把最后一次的异常包成对用户友好的提示抛出去。
    /// </summary>
    public static async Task<string> GetStringWithFallbackAsync(
        HttpClient http, string officialUrl, bool preferMirror, string friendlyMessage, CancellationToken ct = default)
    {
        var candidates = Candidates(officialUrl, preferMirror);
        Exception? lastError = null;

        foreach (var url in candidates)
        {
            try
            {
                var result = await http.GetStringAsync(url, ct);
                ReportSuccess(url);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ReportFailure(url);
                lastError = ex;
                // 换下一个候选继续试，不立刻抛出——这正是"回退"的核心。
            }
        }

        // 所有候选都失败了：包一句人话，但保留原始异常做 InnerException，方便崩溃日志排查。
        throw new InvalidOperationException(friendlyMessage, lastError);
    }

    /// <summary>
    /// 带多候选回退的**文件**下载：跟 GetStringWithFallbackAsync 同一套思路，
    /// 给"加载器 Jar / 服务端归档"这类二进制文件下载用。所有候选源依次尝试，
    /// 失败自动换下一个，全部失败才把最后一次异常包装成用户友好的提示抛出
    /// （**不会**为镜像失败弹窗——换源本来就是自动的）。
    ///
    /// 进度单位跟 DownloadService 保持一致：current/total 都是 KB。
    /// </summary>
    public static async Task<string> DownloadFileWithFallbackAsync(
        HttpClient http, string officialUrl, bool preferMirror, string destPath,
        string friendlyMessage, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var candidates = AllCandidates(officialUrl, preferMirror);
        Exception? lastError = null;

        foreach (var url in candidates)
        {
            var tmpPath = destPath + ".fbpart";
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmpPath);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                var fileName = Path.GetFileName(destPath);
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (progress != null && total > 0)
                        progress.Report(new ProgressInfo("正在下载 " + fileName,
                            (int)(done / 1024), (int)(total / 1024),
                            $"{done / 1048576} MB / {total / 1048576} MB"));
                }
                await dst.DisposeAsync();

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmpPath, destPath);

                ReportSuccess(url);
                return destPath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ReportFailure(url);
                lastError = ex;
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* 忽略清理失败 */ }
            }
        }

        throw new InvalidOperationException(friendlyMessage, lastError);
    }
}
