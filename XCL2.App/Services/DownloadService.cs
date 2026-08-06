using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

public record ProgressInfo(string Stage, int Done, int Total, string CurrentFile);

/// <summary>
/// 游戏核心文件下载：支持官方源(Mojang/launchermeta)与 BMCLAPI 镜像源切换。
/// 覆盖：version manifest -> version json -> client jar -> libraries -> natives -> assets。
/// </summary>
public class DownloadService : IDisposable
{
    private const string OfficialManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BmclManifestUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly DownloadSource _source;

    /// <summary>并发下载信号量：控制"同时进行的单文件下载数"，供 libraries/assets 这类批量小文件
    /// 场景使用；<see cref="InstallVersionAsync"/> 里单个 client jar 之类的一次性大文件不受影响
    /// （本来就只有一个，谈不上并发）。maxConcurrency=1 时退化为原来的串行行为。</summary>
    private readonly SemaphoreSlim _concurrencyGate;

    /// <summary>全局限速器：多线程下载时所有并发连接共享同一个实例，保证限速值是"总速度"而不是
    /// "单连接速度"。null 表示不限速。</summary>
    private readonly DownloadRateLimiter? _rateLimiter;

    /// <summary>智能限速监控：非 null 时会持续采样系统网络占用，动态调整 _rateLimiter 的目标速度。
    /// 生命周期跟 DownloadService 实例绑定，安装流程结束后调用方应该 Dispose 这个 DownloadService。</summary>
    private readonly SmartBandwidthMonitor? _bandwidthMonitor;

    /// <summary>
    /// 默认构造：单线程、不限速——保持跟旧版本完全一致的行为，供不关心新特性的旧调用方
    /// （比如 ClientLoaderInstallService 里的内部 loader 安装逻辑）继续直接
    /// `new DownloadService(source)` 而不用改动任何调用点。
    /// </summary>
    public DownloadService(DownloadSource source) : this(source, maxConcurrency: 1, speedLimitKBps: 0, smartThrottle: false)
    {
    }

    /// <summary>
    /// 完整构造：可指定并发线程数 / 固定限速 / 智能限速。三者语义见 AppConfig 里对应字段的注释：
    /// AppConfig.MaxDownloadThreads、AppConfig.DownloadSpeedLimitKBps、AppConfig.SmartBandwidthThrottle。
    /// </summary>
    public DownloadService(DownloadSource source, int maxConcurrency, int speedLimitKBps, bool smartThrottle)
    {
        _source = source;
        _concurrencyGate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        if (speedLimitKBps > 0 || smartThrottle)
        {
            _rateLimiter = new DownloadRateLimiter(speedLimitKBps > 0 ? speedLimitKBps * 1024L : 0);
            if (smartThrottle)
            {
                _bandwidthMonitor = new SmartBandwidthMonitor(_rateLimiter, speedLimitKBps);
            }
        }
    }

    /// <summary>释放智能限速监控占用的后台采样线程。安装流程结束后（成功/失败/取消都一样）应该调用，
    /// 避免每次新建 DownloadService 都留下一个永不退出的后台采样循环。不调用也不会导致下载出错，
    /// 只是那个采样任务会一直跑到进程退出——所以仍然建议显式释放，属于良好习惯而非强制要求。</summary>
    public void Dispose() => _bandwidthMonitor?.Dispose();

    /// <summary>
    /// 按用户设置页里的配置创建 DownloadService，供真正会下载"一大批文件"的场景使用
    /// （目前是 DownloadCenterPage 里的版本安装入口）——集中在这一处做配置到构造参数的映射，
    /// 避免每个调用点各自读一遍 EnableMultiThreadDownload/MaxDownloadThreads/DownloadSpeedLimitKBps/
    /// SmartBandwidthThrottle 四个字段、还要各自处理"多线程开关关闭时并发数应该视为 1"这种细节。
    /// 只是"拉一次版本清单 JSON"这种不涉及批量文件下载的场景，仍然可以直接
    /// `new DownloadService(source)`，没必要为了一次 HTTP 请求启动限速器/智能监控。
    /// </summary>
    public static DownloadService CreateFromConfig(Models.AppConfig cfg)
    {
        var threads = cfg.EnableMultiThreadDownload ? Math.Max(1, cfg.MaxDownloadThreads) : 1;
        return new DownloadService(cfg.Source, threads, cfg.DownloadSpeedLimitKBps, cfg.SmartBandwidthThrottle);
    }

    public async Task<VersionManifestRoot> GetVersionManifestAsync(CancellationToken ct = default)
    {
        // 修复：之前这里按 _source 硬选一个 URL，选了镜像源就永远只请求镜像，官方一旦同一时刻
        // 镜像抽风(BMCLAPI 偶发不同步/超时是常态)就直接报错，完全没有"换源重试一次"的机会。
        // 现在改用 DownloadEndpoints 的候选池：按用户偏好排第一，另一个源作为兜底，
        // 跟文件下载(DownloadFileAsync)已经在用的回退逻辑保持一致。
        var json = await DownloadEndpoints.GetStringWithFallbackAsync(
            _http, OfficialManifestUrl, _source != DownloadSource.Official,
            "无法获取 Minecraft 版本清单，请检查网络连接或稍后重试。", ct);
        return JsonSerializer.Deserialize<VersionManifestRoot>(json) ?? new VersionManifestRoot();
    }

    /// <summary>下载并安装一个原版版本到 .minecraft/versions/{id}/。 progress 用于回报 UI。</summary>
    public async Task InstallVersionAsync(string minecraftDir, VersionManifestEntry entry,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        var versionDir = Path.Combine(minecraftDir, "versions", entry.Id);
        Directory.CreateDirectory(versionDir);

        // 1. version json
        // 修复"Fabric/原版安装 404"：这是 Fabric 安装第一步"装原版父版本"实际发起的第一个网络
        // 请求，之前用 RemapUrl 只算出唯一一个 URL(镜像源用户就只请求镜像)，镜像对这个具体版本
        // 短暂不同步时直接 404 到底、异常原样抛给用户。改成 Candidates 回退：镜像不行立刻退回官方，
        // 顺序仍然尊重用户在设置里选的源。
        progress?.Report(new ProgressInfo("下载版本信息", 0, 1, entry.Id));
        var versionJsonText = await DownloadEndpoints.GetStringWithFallbackAsync(
            _http, entry.Url, _source != DownloadSource.Official,
            $"无法获取版本 {entry.Id} 的版本信息，请检查网络连接或稍后重试。", ct);
        var versionJsonPath = Path.Combine(versionDir, $"{entry.Id}.json");
        await File.WriteAllTextAsync(versionJsonPath, versionJsonText, ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(versionJsonText) ?? new VersionDetail();
        // DownloadLibrariesOnlyAsync 用 detail.Id 推算 natives 输出目录（见该方法注释）；
        // 极少数官方 version json 可能不带 "id" 字段（或者跟 manifest 里的 entry.Id 不一致），
        // 这里强制对齐成 entry.Id，保证 natives 目录跟本方法上面已经创建好的 versionDir 是同一个，
        // 不会因为 json 里的 id 缺失/不一致而把 natives 解到错误的路径。
        detail.Id = entry.Id;

        // 2. client jar
        if (detail.Downloads != null && detail.Downloads.TryGetValue("client", out var clientArtifact))
        {
            progress?.Report(new ProgressInfo("下载客户端主程序", 0, 1, $"{entry.Id}.jar"));
            var jarPath = Path.Combine(versionDir, $"{entry.Id}.jar");
            // 修复：不要在这里先用 RemapUrl 把 URL 换成镜像——DownloadFileAsync 内部的
            // DownloadEndpoints.Candidates() 会自己根据"官方 URL"算出镜像+官方两个候选并按健康度
            // 回退。如果这里先手动换成了镜像 URL 再传进去，Candidates() 面对的就已经是一个镜像域名，
            // 匹配不上 BmclMap 里的任何"官方前缀"，ToMirror() 返回 null，实际上只剩镜像这一个候选，
            // 官方源的回退能力被这一层"提前 remap"悄悄吃掉了——镜像对某个具体文件缺失/损坏时
            // （Forge 库文件里偶尔会有个别 jar 在镜像上缺失，log4j 这类基础库尤其常被引用到但
            // 未必每次都同步及时），完全没有退路，直接下载失败/下到损坏文件。
            await DownloadFileAsync(clientArtifact.Url, jarPath, clientArtifact.Sha1, ct);
        }

        // 3. libraries + natives
        await DownloadLibrariesOnlyAsync(minecraftDir, detail, progress, ct);

        // 4. assets
        if (detail.AssetIndex != null)
        {
            await DownloadAssetsAsync(minecraftDir, detail.AssetIndex, progress, ct);
        }

        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 1, 1, entry.Id));
    }

    /// <summary>
    /// 只下载/补全 libraries + natives，不碰 version json / client jar / assets。
    ///
    /// 抽取自 <see cref="InstallVersionAsync"/> 原本内联的第 3 步，供
    /// <see cref="ClientLoaderInstallService.InstallFabricClientAsync"/> 复用：Fabric 客户端安装
    /// 已经从 Fabric Meta 拿到了官方生成好的 version json（含 libraries 列表），不需要重新走
    /// InstallVersionAsync 完整流程（那会重复下载/覆盖 client jar、重新处理 assets 等，
    /// Fabric 场景下这些原版部分已经通过安装父版本另外处理过了），只需要单独把这份 json 里
    /// 列出的库文件和 natives 补齐即可。
    ///
    /// natives 输出目录固定为 minecraftDir/versions/{detail.Id}/natives——跟 InstallVersionAsync
    /// 内联逻辑原来的行为一致（原来是 versionDir/natives，versionDir 就是 versions/{entry.Id}/），
    /// 这里改用 detail.Id 而不是要求调用方额外传一个 versionDir 参数，是因为 Fabric profile json
    /// 自带的 id 字段（如 "fabric-loader-0.15.11-1.20.1"）本来就是调用方后续会用来创建版本文件夹的
    /// 那个 id，两者理应一致，没必要为了同一个值多加一个参数。
    /// </summary>
    public async Task DownloadLibrariesOnlyAsync(string minecraftDir, VersionDetail detail,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        var librariesDir = Path.Combine(minecraftDir, "libraries");
        var versionDir = Path.Combine(minecraftDir, "versions", detail.Id);
        var nativesDir = Path.Combine(versionDir, "natives");
        Directory.CreateDirectory(nativesDir);

        var applicable = detail.Libraries.Where(LibraryApplies).ToList();

        // 并发下载：libDone 用 Interlocked 递增，因为多个任务会同时写这个计数器；
        // progress?.Report 本身只是把一个不可变 record 扔进 IProgress（WPF 场景下底层通常是
        // Dispatcher.Invoke 编组回 UI 线程），多线程并发调用是安全的，不需要额外加锁。
        // _concurrencyGate 的初始并发数就是"单线程下载"和"多线程下载"两种模式的唯一区别——
        // 关闭多线程下载时 maxConcurrency=1，这里的 Task.WhenAll 实际上会退化成完全顺序执行，
        // 不需要为"是否启用多线程"另外写一份分支逻辑。
        int libDone = 0;
        var tasks = applicable.Select(async lib =>
        {
            await _concurrencyGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (lib.Downloads?.Artifact is { } art && !string.IsNullOrEmpty(art.Path))
                {
                    var dest = Path.Combine(librariesDir, art.Path.Replace('/', Path.DirectorySeparatorChar));
                    // 同上：不预先 RemapUrl，让 DownloadFileAsync 自己拿到官方 URL 去算候选池，
                    // 保留"镜像缺这个库就自动回退官方"的能力——这正是 Forge 装完却在启动时报
                    // "Module ... log4j not found" 的根因之一：库文件下载阶段镜像没有回退，
                    // 某个库（哪怕只有 log4j 这一个）下载失败/下到空文件，装的时候没有强校验，
                    // 结果是"看起来装完了"，直到真正启动、JPMS 解析模块时才暴露出这个库缺失。
                    await DownloadFileAsync(art.Url, dest, art.Sha1, ct);
                }
                else if (!string.IsNullOrEmpty(lib.Url))
                {
                    // Fabric/Quilt 风格：没有 downloads 对象，只有 "name" (Maven坐标) + "url" (仓库地址)。
                    // 之前这种条目被整体跳过，导致 loader 自身的 jar 从未下载，
                    // 装完之后一启动就因为 classpath 缺 mainClass 所在的 jar 而失败。
                    var relativePath = lib.GetMavenPath();
                    if (!string.IsNullOrEmpty(relativePath))
                    {
                        var dest = Path.Combine(librariesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                        var baseUrl = lib.Url.EndsWith("/") ? lib.Url : lib.Url + "/";
                        // Fabric/Quilt 的 name+url 条目没有单独给出 sha1，传空字符串表示不做哈希校验
                        // （DownloadFileAsync 对已存在的文件本来就会跳过重新下载）。
                        await DownloadFileAsync(baseUrl + relativePath, dest, "", ct);
                    }
                }

                // natives (Windows classifier)
                if (lib.Natives != null && lib.Natives.TryGetValue("windows", out var classifierKey))
                {
                    var key = classifierKey.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    if (lib.Downloads?.Classifiers != null && lib.Downloads.Classifiers.TryGetValue(key, out var nativeArt))
                    {
                        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jar");
                        await DownloadFileAsync(nativeArt.Url, tmp, nativeArt.Sha1, ct);
                        ExtractNatives(tmp, nativesDir);
                        File.Delete(tmp);
                    }
                }

                var done = Interlocked.Increment(ref libDone);
                progress?.Report(new ProgressInfo("下载依赖库", done, applicable.Count, lib.Name));
            }
            finally
            {
                _concurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task DownloadAssetsAsync(string minecraftDir, AssetIndexRef assetIndexRef,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var indexesDir = Path.Combine(minecraftDir, "assets", "indexes");
        Directory.CreateDirectory(indexesDir);
        var indexPath = Path.Combine(indexesDir, $"{assetIndexRef.Id}.json");

        // 同上：资源索引也是单次小请求，同样享受候选池回退，不再是"镜像不行就直接失败"。
        var indexJson = await DownloadEndpoints.GetStringWithFallbackAsync(
            _http, assetIndexRef.Url, _source != DownloadSource.Official,
            "无法获取资源索引文件，请检查网络连接或稍后重试。", ct);
        await File.WriteAllTextAsync(indexPath, indexJson, ct);
        var index = JsonSerializer.Deserialize<AssetIndexFile>(indexJson) ?? new AssetIndexFile();

        var objectsDir = Path.Combine(minecraftDir, "assets", "objects");
        int done = 0;
        var total = index.Objects.Count;

        // assets 通常是数量最多的一批小文件（成百上千个），最能体现多线程下载的收益；
        // 并发结构跟 DownloadLibrariesOnlyAsync 完全一致，同样复用 _concurrencyGate，
        // 保证 libraries 和 assets 两个阶段（虽然是先后调用，不会同时跑）用的是同一套并发上限配置。
        var assetTasks = index.Objects.Select(async pair =>
        {
            var (name, obj) = (pair.Key, pair.Value);
            await _concurrencyGate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var prefix = obj.Hash[..2];
                var dest = Path.Combine(objectsDir, prefix, obj.Hash);
                if (!File.Exists(dest) || new FileInfo(dest).Length != obj.Size)
                {
                    // 统一传官方 URL，由 DownloadFileAsync 内部展开成候选池（官方+镜像）。
                    // 之前这里按 _source 硬选一个源，assets 是数量最多的一批（成百上千个小文件），
                    // 恰恰最需要自动切换——一个源抽风时这里的失败会被放大上千倍。
                    var url = $"https://resources.download.minecraft.net/{prefix}/{obj.Hash}";
                    await DownloadFileAsync(url, dest, obj.Hash, ct, isSha1: true);
                }
                var d = Interlocked.Increment(ref done);
                if (d % 25 == 0 || d == total)
                    progress?.Report(new ProgressInfo("下载资源文件", d, total, name));
            }
            finally
            {
                _concurrencyGate.Release();
            }
        });

        await Task.WhenAll(assetTasks);
    }

    private static bool LibraryApplies(LibraryEntry lib) => lib.IsApplicableToCurrentOs();

    private static void ExtractNatives(string jarPath, string destDir)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("META-INF")) continue;
                if (entry.Name.EndsWith(".dll") || entry.Name.EndsWith(".so") || entry.Name.EndsWith(".dylib"))
                {
                    var dest = Path.Combine(destDir, entry.Name);
                    if (!File.Exists(dest))
                    {
                        using var entryStream = entry.Open();
                        using var fileStream = File.Create(dest);
                        entryStream.CopyTo(fileStream);
                    }
                }
            }
        }
        catch { /* 部分 native jar 可能已损坏，跳过 */ }
    }

    /// <summary>
    /// 下载单个文件并做完整性校验。
    ///
    /// 之前版本的 bug（会导致资源"看似装好、实际是坏文件"，比如语言文件
    /// assets/objects/xx/&lt;zh_cn.json 的 hash&gt; 下载不完整却被当成正常文件）：
    /// 下载完成后只是简单 `File.Move`，从来没有校验刚下载下来的内容跟 expectedSha1
    /// 是否一致——只有在"文件已存在、准备决定要不要跳过重新下载"这一处校验过 SHA1。
    /// 如果网络中途抖动导致连接提前结束但没有抛异常（例如服务端异常关闭连接、
    /// CDN 返回了不完整的 200 响应体等），CopyToAsync 会静默把不完整的数据写完，
    /// 这个文件就被当成"下载成功"留在磁盘上。像语言文件这种体积很小、出错也不会导致游戏
    /// 崩溃的资源，Minecraft 客户端加载失败时只会静默回退到内置英文，用户毫无感知，
    /// 表现为"选了中文，游戏里还是英文"。
    ///
    /// 修复：下载完成后强制校验 SHA1；不一致就重试（最多 3 次，每次换新临时文件，
    /// 避免复用已污染的连接/缓存）；全部失败则删除坏文件并抛出明确异常，
    /// 不再让损坏文件蒙混过关。
    /// </summary>
    /// <summary>
    /// 单次下载尝试的独立超时：修复"Fabric/依赖库下载卡住"问题的核心根因——
    /// 之前完全依赖 HttpClient 级别的整体 Timeout（本类 10 分钟，ClientLoaderInstallService 15 分钟），
    /// 一旦某次请求"假死"（TCP 连接建立但服务器长时间不回数据/不完整返回，常见于部分镜像源
    /// 抽风或用户网络中间设备的连接黑洞），单次 GetAsync 就会真的原地卡到那个整体超时才失败，
    /// 期间 UI 侧收不到任何新的 progress 回报（下载几百个 library 时，卡的是其中一个文件，
    /// 前面文件早就报过完成，用户看到的就是进度条长时间停在某个数字不动，跟真死锁表现一致）。
    /// 3 次重试 * 10~15 分钟整体超时，最坏情况下一个文件能卡 30~45 分钟。
    /// 现在改成每次尝试单独套一个较短的超时（跟每个文件的合理下载时长匹配，而不是跟"整个安装
    /// 流程要花多久"挂钩），假死的连接能在几十秒内被判定失败、快速进入下一次重试，
    /// 而不是死等到全局超时。
    /// </summary>
    private static readonly TimeSpan SingleAttemptTimeout = TimeSpan.FromSeconds(45);

    /// <summary>单个分片的最小体积。小于这个值就不值得分片（分片本身有建连开销）。</summary>
    private const long MinChunkSize = 4 * 1024 * 1024;   // 4 MB

    /// <summary>超过这个体积的文件才考虑多线程分片下载。client.jar / 大 mod 属于这一类。</summary>
    private const long MultiPartThreshold = 8 * 1024 * 1024;   // 8 MB

    private async Task DownloadFileAsync(string url, string destPath, string expectedSha1,
        CancellationToken ct, bool isSha1 = true)
    {
        if (File.Exists(destPath) && VerifySha1(destPath, expectedSha1)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // ===== 多源候选 =====
        // 关键改动：不再只用 RemapUrl 算出的那**一个** URL，而是拿到一串候选
        // （官方 + BMCLAPI，顺序按用户设置的偏好 + 各主机近期健康度排）。
        // 旧行为下 BMCLAPI 一抽风，装一个版本几百个文件就会有文件三次重试全挂、
        // 整个安装终止；而换个源明明就能下下来。这是"整合包装到一半失败"最主要的成因。
        var candidates = DownloadEndpoints.Candidates(url, _source != DownloadSource.Official);

        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            // 每个候选源各给 2 次机会：第 1 次可能续传，第 2 次从头来（排除 .part 本身损坏的情况）。
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await DownloadFromSingleUrlAsync(candidate, destPath, expectedSha1,
                        allowResume: attempt == 1, ct);

                    DownloadEndpoints.ReportSuccess(candidate);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;   // 用户真的取消了整个安装，直接往上抛
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    DownloadEndpoints.ReportFailure(candidate);
                }
            }
        }

        // 所有源都失败：不能把半成品留在原位当作"已安装"，否则用户完全无法察觉资源是坏的。
        TryDelete(destPath + ".part");
        var tried = string.Join(" / ", candidates.Select(c => { try { return new Uri(c).Host; } catch { return c; } }));
        throw new IOException(
            $"下载失败，已尝试全部下载源（{tried}）仍未成功：{Path.GetFileName(destPath)}", lastError);
    }

    /// <summary>
    /// 从**单个** URL 下载到 destPath，带断点续传和大文件分片。
    ///
    /// ===== 断点续传 =====
    /// 旧实现每次尝试都 File.Create(tmp)，从 0 字节重来。一个 25MB 的 client.jar 在
    /// 下到 24MB 时断线，就白下 24MB；网络差的用户可能永远下不完。
    /// 现在改成写 destPath + ".part"，失败时**保留**这个文件，下次带
    /// Range: bytes=&lt;已有长度&gt;- 续着下。服务端不支持 Range（返回 200 而不是 206）时
    /// 自动退回从头下载，不会写出错位的文件。
    ///
    /// ===== 分片并行 =====
    /// 超过 8MB 且服务端支持 Range 的文件，切成若干片并行下。单连接受 TCP 慢启动和
    /// 单流限速影响，实际带宽往往远低于线路上限；分片能把大文件的下载时间压下来一大截，
    /// 这也是 PCL"下得快"的直接来源之一。
    /// 分片只在**没有限速**时启用——开了限速还并行分片，等于绕过用户设定的速率上限。
    /// </summary>
    private async Task DownloadFromSingleUrlAsync(string url, string destPath, string expectedSha1,
        bool allowResume, CancellationToken ct)
    {
        var part = destPath + ".part";

        long existing = 0;
        if (allowResume && File.Exists(part))
        {
            try { existing = new FileInfo(part).Length; }
            catch { existing = 0; }
        }
        else
        {
            TryDelete(part);
        }

        // ---- 先探一次头，拿总长度和 Range 支持情况 ----
        long totalLength = -1;
        var acceptsRange = false;
        try
        {
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
            if (headResp.IsSuccessStatusCode)
            {
                totalLength = headResp.Content.Headers.ContentLength ?? -1;
                acceptsRange = headResp.Headers.AcceptRanges.Contains("bytes");
            }
        }
        catch
        {
            // HEAD 失败不是致命问题（有些 CDN 不支持 HEAD），退回普通 GET 流程。
        }

        // 已经下完了：直接改名验证
        if (totalLength > 0 && existing == totalLength)
        {
            await FinalizePartAsync(part, destPath, expectedSha1);
            return;
        }
        if (existing > 0 && totalLength > 0 && existing > totalLength)
        {
            // .part 比目标文件还大，说明这个残留文件跟当前 URL 对不上（可能换源了），丢掉重来。
            TryDelete(part);
            existing = 0;
        }

        // ---- 大文件 + 支持 Range + 没开限速 → 分片并行 ----
        if (totalLength >= MultiPartThreshold && acceptsRange && _rateLimiter == null && existing == 0)
        {
            await DownloadInChunksAsync(url, part, totalLength, ct);
            await FinalizePartAsync(part, destPath, expectedSha1);
            return;
        }

        // ---- 单流下载（可能带续传）----
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(SingleAttemptTimeout);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0 && acceptsRange)
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
        resp.EnsureSuccessStatusCode();

        // 请求了 Range 但服务端返回 200（不支持）：必须从头写，否则会把整份内容追加到
        // 已有数据后面，产出一个长度翻倍的坏文件——这是续传实现里最容易踩的坑。
        var append = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (existing > 0 && !append)
        {
            TryDelete(part);
            existing = 0;
        }

        await using (var fs = new FileStream(part, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None))
        {
            if (_rateLimiter == null)
            {
                await resp.Content.CopyToAsync(fs, attemptCts.Token);
            }
            else
            {
                var buffer = new byte[81920];
                await using var respStream = await resp.Content.ReadAsStreamAsync(attemptCts.Token);
                int read;
                while ((read = await respStream.ReadAsync(buffer, attemptCts.Token)) > 0)
                {
                    await _rateLimiter.ConsumeAsync(read, attemptCts.Token);
                    await fs.WriteAsync(buffer.AsMemory(0, read), attemptCts.Token);
                    _bandwidthMonitor?.ReportSelfBytes(read);
                }
            }
        }

        await FinalizePartAsync(part, destPath, expectedSha1);
    }

    /// <summary>把 .part 校验并改名成正式文件。校验不过就删掉 .part 并抛异常，
    /// 让上层换下一个源重试——绝不让损坏文件蒙混过关（这是之前"语言文件下坏了却当成功"的教训）。</summary>
    private static async Task FinalizePartAsync(string part, string destPath, string expectedSha1)
    {
        await Task.Yield();   // 让出一次，避免在高并发下长时间占住调用线程做同步哈希

        if (!VerifySha1(part, expectedSha1))
        {
            TryDelete(part);
            throw new IOException($"文件校验失败(SHA1 不匹配)：{Path.GetFileName(destPath)}");
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(part, destPath);
    }

    /// <summary>
    /// 分片并行下载：把 [0, total) 切成若干段，各自带 Range 头并行下，写进同一个稀疏文件的对应偏移。
    /// 任何一片失败都直接抛出，由上层换源/重试（部分成功的 .part 会被丢弃，
    /// 因为分片下载的中间态无法安全续传——不知道哪些区间已经写好了）。
    /// </summary>
    private async Task DownloadInChunksAsync(string url, string part, long total, CancellationToken ct)
    {
        // 分片数量跟并发上限挂钩，但不超过 8——再多收益会被建连开销吃掉，
        // 而且对镜像站不礼貌（同一个文件开一堆连接容易被限流）。
        var chunkCount = (int)Math.Min(8, Math.Max(2, total / MinChunkSize));
        var chunkSize = total / chunkCount;

        TryDelete(part);
        // 预分配文件长度，让各分片能直接 Seek 到自己的偏移写入。
        await using (var pre = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            pre.SetLength(total);

        var ranges = new List<(long From, long To)>();
        for (var i = 0; i < chunkCount; i++)
        {
            var from = i * chunkSize;
            var to = (i == chunkCount - 1) ? total - 1 : (from + chunkSize - 1);
            ranges.Add((from, to));
        }

        var tasks = ranges.Select(async r =>
        {
            using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            chunkCts.CancelAfter(SingleAttemptTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.From, r.To);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, chunkCts.Token);
            resp.EnsureSuccessStatusCode();

            // 服务端如果无视 Range 返回整个文件（200），分片写入就会互相覆盖成一坨垃圾。
            // 明确要求 206，不是就直接失败，让上层退回单流路径。
            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                throw new IOException("服务端不支持分片下载（未返回 206），改用单线程下载。");

            await using var fs = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.Seek(r.From, SeekOrigin.Begin);

            var buffer = new byte[81920];
            await using var stream = await resp.Content.ReadAsStreamAsync(chunkCts.Token);
            int read;
            while ((read = await stream.ReadAsync(buffer, chunkCts.Token)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), chunkCts.Token);
                _bandwidthMonitor?.ReportSelfBytes(read);
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            TryDelete(part);   // 分片中间态无法续传，失败即丢弃
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 忽略清理失败，不影响主流程报错 */ }
    }

    private static bool VerifySha1(string path, string expectedSha1)
    {
        if (string.IsNullOrEmpty(expectedSha1)) return true;
        try
        {
            using var sha1 = SHA1.Create();
            using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(sha1.ComputeHash(fs)).ToLowerInvariant();
            return hash == expectedSha1.ToLowerInvariant();
        }
        catch { return false; }
    }

    /// <summary>
    /// 将官方 URL 映射为 BMCLAPI 镜像 URL（当选择镜像源时）。
    ///
    /// 注意适用范围已经变窄：走 DownloadFileAsync 的文件下载**不再依赖这个方法**，
    /// 那条路径改用 DownloadEndpoints.Candidates 拿到官方+镜像的完整候选池并自动切换。
    /// 这里只剩下少数直接 GetStringAsync 的元数据请求（version manifest、asset index）在用，
    /// 那些是单次小请求，失败会立刻暴露给用户，不需要候选池。
    /// </summary>
    private string RemapUrl(string officialUrl)
    {
        if (_source == DownloadSource.Official) return officialUrl;

        if (officialUrl.Contains("launchermeta.mojang.com") || officialUrl.Contains("launcher.mojang.com"))
            return officialUrl
                .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com")
                .Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com");

        if (officialUrl.Contains("libraries.minecraft.net"))
            return officialUrl.Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven");

        if (officialUrl.Contains("resources.download.minecraft.net"))
            return officialUrl.Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets");

        return officialUrl;
    }
}
