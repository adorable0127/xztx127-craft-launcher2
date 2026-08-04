using System.Collections.Concurrent;

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
        ("https://maven.neoforged.net/releases",       "https://bmclapi2.bangbang93.com/maven"),
        ("https://files.minecraftforge.net/maven",     "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.minecraftforge.net",           "https://bmclapi2.bangbang93.com/maven"),
    };

    /// <summary>
    /// 把一个官方 URL 展开成候选列表（去重、按主机健康度稳定排序）。
    ///
    /// preferMirror = 用户在设置里选了镜像源。它只影响**顺序**，不影响候选集合——
    /// 这正是"自动切换"的关键：用户的偏好排第一，另一个作为兜底始终存在。
    /// </summary>
    public static IReadOnlyList<string> Candidates(string officialUrl, bool preferMirror)
    {
        var list = new List<string>(2);

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

    /// <summary>官方 URL → BMCLAPI 镜像 URL；没有对应规则时返回 null。</summary>
    public static string? ToMirror(string officialUrl)
    {
        if (string.IsNullOrWhiteSpace(officialUrl)) return null;

        foreach (var (official, mirrorPrefix) in BmclMap)
        {
            if (officialUrl.StartsWith(official, StringComparison.OrdinalIgnoreCase))
                return mirrorPrefix + officialUrl[official.Length..];
        }
        return null;
    }

    /// <summary>调试/界面展示用：当前被判定为"不健康"的主机列表。</summary>
    public static IReadOnlyList<string> UnhealthyHosts() =>
        Health.Where(kv => kv.Value.Penalty > 0).Select(kv => kv.Key).ToList();

    /// <summary>重置所有健康度记录（切换下载源、用户手动重试时调用）。</summary>
    public static void ResetHealth() => Health.Clear();
}
