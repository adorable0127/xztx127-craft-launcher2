using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>
/// 基岩版（Bedrock Edition）内容管理。
///
/// ===== 先说清楚基岩版能做什么、不能做什么 =====
///
/// 基岩版跟 Java 版的分发方式**完全不是一回事**：
/// - Java 版：Mojang 有公开的 version_manifest_v2.json，client.jar 谁都能下，
///   启动器下载 + 拼 classpath 就能跑。
/// - 基岩版（Windows）：是 MSIX/Appx 包，**只通过 Microsoft Store 分发**，
///   跟微软账号的许可证绑死，**没有公开 CDN 可以下载客户端**。
///
/// 所以"下载基岩版"这句话拆开来是三件复杂度差一个数量级的事：
///
///   ① 检测 + 唤起已安装的基岩版  → 已经做了，见 BedrockLaunchService
///   ② 管理世界 / 材质包 / 行为包  → **本类负责**，纯本地文件操作，无任何限制
///   ③ 多版本切换（装旧版基岩）     → 需要用户账号自己的 Store 许可证，
///                                    且系统同时只能注册一个 Minecraft 包，
///                                    切版本要卸载再 Add-AppxPackage -Register + 开发者模式。
///                                    **绕过许可证获取客户端包这条路本启动器不做。**
///
/// 另外有一件事是**完全公开免费**的：**Bedrock Dedicated Server（BDS）**，
/// Mojang 在官网直接提供 zip 下载，不需要任何账号或许可证。所以"下载基岩版服务端"
/// 这个功能可以完整实现，本类的 <see cref="DownloadDedicatedServerAsync"/> 就是干这个的。
///
/// ===== 本类负责的具体内容 =====
/// - .mcworld  → 世界存档，解压到 minecraftWorlds/
/// - .mcpack   → 单个资源包/行为包，按 manifest 里的模块类型分流
/// - .mcaddon  → 附加包（内部是若干 .mcpack 的容器），先拆再逐个装
/// - .mctemplate → 世界模板，装到 world_templates/
/// - BDS 服务端 zip 下载 + 解压
/// </summary>
public class BedrockContentService
{
    /// <summary>
    /// 基岩版数据根目录。Minecraft for Windows 把用户数据放在 UWP 应用的 LocalState 下，
    /// 这个路径对普通用户是**可读写**的（跟 WindowsApps 那种受保护目录不同），
    /// 所以导入世界/附加包不需要管理员权限，也不需要碰任何 Store 私有 API。
    /// </summary>
    public static string ComMojangDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
        "LocalState", "games", "com.mojang");

    public static bool IsBedrockDataPresent => Directory.Exists(ComMojangDir);

    public enum BedrockContentKind { World, ResourcePack, BehaviorPack, Addon, WorldTemplate, Unknown }

    /// <summary>一次导入的结果。</summary>
    public sealed record ImportResult(List<string> Installed, List<string> Failed);

    /// <summary>按扩展名判断类型。.mcpack 内部还要读 manifest 才能确定是资源包还是行为包，
    /// 那一步在 ImportPackAsync 里做，这里只做粗分类。</summary>
    public static BedrockContentKind ClassifyByExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mcworld" => BedrockContentKind.World,
            ".mcaddon" => BedrockContentKind.Addon,
            ".mcpack" => BedrockContentKind.ResourcePack,   // 待 manifest 细分
            ".mctemplate" => BedrockContentKind.WorldTemplate,
            _ => BedrockContentKind.Unknown,
        };

    /// <summary>
    /// 批量导入基岩版内容。
    /// 基岩版没装（com.mojang 目录不存在）时直接抛出带说明的异常，
    /// 而不是自己建一个目录——那样游戏根本读不到，用户还以为装好了。
    /// </summary>
    public ImportResult ImportMany(IEnumerable<string> paths, IProgress<string>? progress = null)
    {
        if (!IsBedrockDataPresent)
            throw new InvalidOperationException(
                "没有找到基岩版的数据目录，说明这台电脑上还没有安装 Minecraft for Windows（基岩版）。\n" +
                "请先从 Microsoft Store 安装基岩版并至少启动一次（首次启动才会创建数据目录），再来导入内容。");

        var installed = new List<string>();
        var failed = new List<string>();

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            try
            {
                switch (ClassifyByExtension(path))
                {
                    case BedrockContentKind.World:
                        ImportWorld(path);
                        installed.Add($"{name} → 世界存档");
                        break;
                    case BedrockContentKind.WorldTemplate:
                        ExtractIntoNewSubdir(path, Path.Combine(ComMojangDir, "world_templates"));
                        installed.Add($"{name} → 世界模板");
                        break;
                    case BedrockContentKind.Addon:
                        var n = ImportAddon(path, progress);
                        installed.Add($"{name} → 附加包（含 {n} 个子包）");
                        break;
                    case BedrockContentKind.ResourcePack:
                        var kind = ImportPack(path);
                        installed.Add($"{name} → {(kind == BedrockContentKind.BehaviorPack ? "行为包" : "资源包")}");
                        break;
                    default:
                        failed.Add($"{name}（不是基岩版能识别的文件类型）");
                        break;
                }
                progress?.Report($"已导入 {name}");
            }
            catch (Exception ex)
            {
                failed.Add($"{name}（{ex.Message}）");
            }
        }

        return new ImportResult(installed, failed);
    }

    private static void ImportWorld(string mcworldPath)
        => ExtractIntoNewSubdir(mcworldPath, Path.Combine(ComMojangDir, "minecraftWorlds"));

    /// <summary>
    /// .mcpack：读内部 manifest.json 的 modules[].type 判断是资源包还是行为包。
    /// type 为 "data" / "script" 的是行为包，"resources" 的是资源包。
    /// 读不出来时按资源包处理（更常见，且装错了用户在游戏里一眼能看出来并手动挪走）。
    /// </summary>
    private static BedrockContentKind ImportPack(string mcpackPath)
    {
        var kind = BedrockContentKind.ResourcePack;
        try
        {
            using var archive = ZipFile.OpenRead(mcpackPath);
            var manifest = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifest != null)
            {
                using var reader = new StreamReader(manifest.Open());
                var text = reader.ReadToEnd();
                if (Regex.IsMatch(text, "\"type\"\\s*:\\s*\"(data|script)\"", RegexOptions.IgnoreCase))
                    kind = BedrockContentKind.BehaviorPack;
            }
        }
        catch { /* 读不出就按资源包 */ }

        var target = kind == BedrockContentKind.BehaviorPack
            ? Path.Combine(ComMojangDir, "development_behavior_packs")
            : Path.Combine(ComMojangDir, "development_resource_packs");

        ExtractIntoNewSubdir(mcpackPath, target);
        return kind;
    }

    /// <summary>
    /// .mcaddon 是一个"包的容器"：里面装着若干 .mcpack，或者直接是若干带 manifest.json 的子目录。
    /// 先解到临时目录，再把里面每个包各自按类型装好。
    /// </summary>
    private int ImportAddon(string mcaddonPath, IProgress<string>? progress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"xcl2_mcaddon_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var count = 0;
        try
        {
            ZipFile.ExtractToDirectory(mcaddonPath, tmp);

            // 情况 A：里面是若干 .mcpack 文件
            foreach (var pack in Directory.GetFiles(tmp, "*.mcpack", SearchOption.AllDirectories))
            {
                ImportPack(pack);
                count++;
                progress?.Report($"已装入子包 {Path.GetFileName(pack)}");
            }

            // 情况 B：里面直接是若干带 manifest.json 的子目录
            if (count == 0)
            {
                foreach (var manifest in Directory.GetFiles(tmp, "manifest.json", SearchOption.AllDirectories))
                {
                    var packDir = Path.GetDirectoryName(manifest)!;
                    var text = File.ReadAllText(manifest);
                    var isBehavior = Regex.IsMatch(text, "\"type\"\\s*:\\s*\"(data|script)\"", RegexOptions.IgnoreCase);
                    var target = Path.Combine(ComMojangDir,
                        isBehavior ? "development_behavior_packs" : "development_resource_packs",
                        UniqueDirName(Path.Combine(ComMojangDir,
                            isBehavior ? "development_behavior_packs" : "development_resource_packs"),
                            Path.GetFileName(packDir)));
                    CopyDir(packDir, target);
                    count++;
                }
            }

            if (count == 0)
                throw new InvalidOperationException("这个 .mcaddon 里没有找到任何有效的资源包/行为包。");

            return count;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    /// <summary>把 zip 解到目标父目录下一个以文件名命名的新子目录（重名自动加序号）。
    /// 带路径穿越防护，挡住 zip 里构造的 "../" 条目。</summary>
    private static void ExtractIntoNewSubdir(string zipPath, string parentDir)
    {
        Directory.CreateDirectory(parentDir);
        var name = UniqueDirName(parentDir, Path.GetFileNameWithoutExtension(zipPath));
        var target = Path.Combine(parentDir, name);
        Directory.CreateDirectory(target);

        using var archive = ZipFile.OpenRead(zipPath);
        var rootFull = Path.GetFullPath(target);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar)) rootFull += Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var dest = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.GetFullPath(dest).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                continue;   // 路径穿越，跳过
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static string UniqueDirName(string parent, string desired)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) desired = desired.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(desired)) desired = "imported";
        var name = desired;
        var i = 2;
        while (Directory.Exists(Path.Combine(parent, name)))
        {
            name = $"{desired} ({i})"; i++;
        }
        return name;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ==================== Bedrock Dedicated Server ====================

    /// <summary>
    /// Mojang 的 BDS 下载页。BDS **完全公开免费**，不需要账号也不需要许可证，
    /// 这也是"下载基岩版"这个需求里唯一能被完整实现的部分。
    ///
    /// 不把下载直链写死在代码里的原因：Mojang 的 BDS 直链带版本号
    /// （形如 .../bin-win/bedrock-server-1.21.44.01.zip），每次更新都变，
    /// 写死必然很快失效。改成从下载页里正则抓当前直链。
    /// </summary>
    private const string BdsPageUrl = "https://www.minecraft.net/en-us/download/server/bedrock";

    private static readonly Regex BdsLinkPattern = new(
        @"https://[^\s""']*bin-win/bedrock-server-[\d.]+\.zip",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record BdsInfo(string Url, string Version);

    /// <summary>抓当前 BDS 的下载直链和版本号。</summary>
    public async Task<BdsInfo> ResolveDedicatedServerUrlAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // Mojang 的下载页对无 UA 的请求会返回 403，带一个正常浏览器 UA。
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");

        var html = await http.GetStringAsync(BdsPageUrl, ct);
        var m = BdsLinkPattern.Match(html);
        if (!m.Success)
            throw new InvalidOperationException(
                "没能从 Minecraft 官网找到基岩版服务端的下载链接。\n" +
                "可能是官网改版了，或者当前网络访问不到 minecraft.net。可以稍后重试，或手动去官网下载。");

        var url = m.Value;
        var ver = Regex.Match(url, @"bedrock-server-([\d.]+)\.zip").Groups[1].Value;
        return new BdsInfo(url, string.IsNullOrEmpty(ver) ? "未知" : ver);
    }

    /// <summary>
    /// 下载并解压 BDS 到指定目录。
    /// </summary>
    public async Task<string> DownloadDedicatedServerAsync(string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        progress?.Report(new ProgressInfo("正在查询服务端版本", 0, 1, ""));
        var info = await ResolveDedicatedServerUrlAsync(ct);

        Directory.CreateDirectory(targetDir);
        var zipPath = Path.Combine(Path.GetTempPath(), $"bedrock-server-{info.Version}.zip");

        progress?.Report(new ProgressInfo($"正在下载基岩版服务端 {info.Version}", 0, 1, ""));

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");

            using var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress?.Report(new ProgressInfo($"正在下载基岩版服务端 {info.Version}",
                        (int)(done / 1024), (int)(total / 1024), $"{done / 1048576} MB / {total / 1048576} MB"));
            }
        }

        progress?.Report(new ProgressInfo("正在解压", 0, 1, targetDir));

        // BDS 的 zip 顶层就是 bedrock_server.exe + 若干目录，直接解到目标目录即可。
        // 注意 overwriteFiles: true 会覆盖 server.properties / allowlist.json 这类配置文件，
        // 所以这里对已存在的配置文件做保护——升级服务端时不该把用户的配置冲掉。
        var protectedFiles = new[] { "server.properties", "allowlist.json", "permissions.json", "whitelist.json" };
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var rootFull = Path.GetFullPath(targetDir);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar)) rootFull += Path.DirectorySeparatorChar;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var dest = Path.Combine(targetDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                if (!Path.GetFullPath(dest).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

                if (protectedFiles.Contains(entry.Name, StringComparer.OrdinalIgnoreCase) && File.Exists(dest))
                    continue;   // 保住用户已有的配置

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        try { File.Delete(zipPath); } catch { }

        progress?.Report(new ProgressInfo(Loc.T("Str_Common_Finish", "完成"), 1, 1, info.Version));
        return info.Version;
    }
}
