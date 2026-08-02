using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XCL2.App.Services;

/// <summary>
/// 整合包导入导出：把一个版本目录下的 mods/config/resourcepacks/shaderpacks 打包成一个
/// .xclpack (本质是 zip) 文件，方便玩家之间分享，或者自己在不同电脑间迁移。
///
/// 只打包"和内容相关"的几个文件夹，不打包 saves(存档，体积可能很大且是个人游戏进度，不适合当整合包
/// 内容分享) 和 versions 内部的 jar/json 本体(游戏版本文件，导入方应该用启动器自己下载对应版本，
/// 而不是靠整合包夹带游戏本体，那样体积会非常大且容易涉及版权问题)。
/// </summary>
public class ModpackService
{
    private static readonly string[] IncludedFolders = { "mods", "config", "resourcepacks", "shaderpacks" };
    private const string ManifestFileName = "xclpack.json";

    /// <summary>
    /// 导出：把 versionDir 下 IncludedFolders 里存在的文件夹打包进 destZipPath。
    /// 打包前会写一个简单的 manifest（名称、版本号、加载器信息、打包时间），方便导入方核对内容。
    /// </summary>
    public void Export(string versionDir, string destZipPath, ModpackManifest manifest,
        IProgress<string>? progress = null)
        => Export(versionDir, destZipPath, manifest, progress == null ? null : new Progress<ProgressInfo>(p => progress.Report(p.Stage)));

    /// <summary>
    /// 导出（带真正的文件级进度）：IProgress&lt;ProgressInfo&gt; 版本，Done/Total 是已复制/总计
    /// 的文件数，CurrentFile 是正在复制的文件名——配合 ProgressDialog 可以显示真实的进度条和
    /// "当前正在导出哪个文件"，而不是旧版 IProgress&lt;string&gt; 那种只有几句阶段性文字、
    /// 复制大量文件时进度条长时间不动、看起来像卡死的问题。
    /// </summary>
    public void Export(string versionDir, string destZipPath, ModpackManifest manifest,
        IProgress<ProgressInfo>? progress)
    {
        if (File.Exists(destZipPath)) File.Delete(destZipPath);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"xcl2_pack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            // 先扫一遍算总文件数，才能算出真实的 Done/Total 百分比——只扫存在的文件夹，
            // 跟下面实际复制时的过滤条件一致，避免总数和实际复制数对不上。
            var sourceFolders = IncludedFolders
                .Select(f => Path.Combine(versionDir, f))
                .Where(Directory.Exists)
                .ToList();
            var totalFiles = sourceFolders.Sum(CountFiles);
            var doneFiles = 0;

            if (sourceFolders.Count == 0)
                throw new InvalidOperationException("这个版本下没有找到 mods/config/resourcepacks/shaderpacks 中任何一个文件夹，没有可打包的内容。");

            foreach (var folder in IncludedFolders)
            {
                var src = Path.Combine(versionDir, folder);
                if (!Directory.Exists(src)) continue;
                CopyDirectory(src, Path.Combine(tmpDir, folder), (file, name) =>
                {
                    doneFiles++;
                    progress?.Report(new ProgressInfo("打包文件", doneFiles, totalFiles, name));
                });
            }

            progress?.Report(new ProgressInfo("生成压缩包", totalFiles, totalFiles, Path.GetFileName(destZipPath)));
            ZipFile.CreateFromDirectory(tmpDir, destZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            progress?.Report(new ProgressInfo("导出完成", totalFiles, totalFiles, ""));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 忽略临时目录清理失败 */ }
        }
    }

    /// <summary>
    /// 导入：把 zip 包内 IncludedFolders 对应的文件夹解压合并到 targetVersionDir。
    /// 默认采用"合并覆盖"策略——已存在同名文件会被整合包内容覆盖，不存在的文件保留，
    /// 这样用户可以把整合包"叠加"安装到一个已有版本上，而不是每次都完全清空重装。
    /// 如果用户想要"全新安装"，应该先自己新建一个空的版本目录再导入。
    /// </summary>
    public ModpackManifest? Import(string zipPath, string targetVersionDir, IProgress<string>? progress = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("找不到整合包文件。", zipPath);

        ModpackManifest? manifest = null;
        Directory.CreateDirectory(targetVersionDir);

        progress?.Report("解压整合包 ...");
        using var archive = ZipFile.OpenRead(zipPath);

        var manifestEntry = archive.GetEntry(ManifestFileName);
        if (manifestEntry != null)
        {
            try
            {
                using var stream = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<ModpackManifest>(stream);
            }
            catch
            {
                // manifest 损坏或格式不对不影响继续导入内容文件，只是拿不到展示用的元信息
            }
        }

        var importedAny = false;
        foreach (var entry in archive.Entries)
        {
            // 目录条目 Name 为空，跳过；只处理落在 IncludedFolders 白名单下的文件条目，
            // 防止 zip 包里混入奇怪路径（例如故意构造的 "../../xxx" 路径穿越）写到白名单目录之外。
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (entry.FullName == ManifestFileName) continue;

            var normalizedPath = entry.FullName.Replace('\\', '/');
            var topFolder = normalizedPath.Split('/')[0];
            if (!IncludedFolders.Contains(topFolder)) continue;

            var destPath = Path.Combine(targetVersionDir, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            var destFullPath = Path.GetFullPath(destPath);
            var targetFullPath = Path.GetFullPath(targetVersionDir);
            if (!destFullPath.StartsWith(targetFullPath, StringComparison.OrdinalIgnoreCase))
                continue; // 路径穿越防护：解出来的最终路径必须落在目标版本目录内部

            Directory.CreateDirectory(Path.GetDirectoryName(destFullPath)!);
            entry.ExtractToFile(destFullPath, overwrite: true);
            importedAny = true;
        }

        if (!importedAny)
            throw new InvalidOperationException("这个压缩包里没有找到 mods/config/resourcepacks/shaderpacks 任何内容，可能不是有效的整合包文件。");

        progress?.Report("导入完成");
        return manifest;
    }

    /// <summary>
    /// 判断一个整合包 zip 是不是 Modrinth 的 .mrpack 格式：只要顶层有 modrinth.index.json 就是。
    /// .xclpack / 普通 .zip 整合包顶层是 xclpack.json 或者直接就是 mods/config 等文件夹，
    /// 不会有这个文件，两者不会误判。
    /// </summary>
    public static bool IsMrpack(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.GetEntry("modrinth.index.json") != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 导入 Modrinth .mrpack 整合包：跟 .xclpack/.zip 完全不同的格式——.mrpack 本身不含 mod jar 文件，
    /// 只有一份 modrinth.index.json 清单（列出每个 mod 的下载直链 + SHA1），以及一个 overrides/
    /// 文件夹（config、资源包等直接文件）。导入需要：
    /// 1) 解析 modrinth.index.json，按每个 file 条目的 downloads[0] 直链下载到 targetVersionDir 对应路径
    ///    （通常是 mods/xxx.jar，但清单里的 path 字段可能指向 config/ 等其它位置，照抄 path 用）。
    /// 2) 解压 overrides/（以及可选的 client-overrides/）目录内容到 targetVersionDir 根下。
    /// 3) 下载失败的单个 mod 不中止整个导入（部分整合包体积很大，某个源暂时挂了不该导致整体失败），
    ///    累计失败的文件名收集起来，导入完成后通过返回值告知调用方。
    /// </summary>
    public async Task<MrpackImportResult> ImportMrpackAsync(string mrpackPath, string targetVersionDir,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(mrpackPath))
            throw new FileNotFoundException("找不到整合包文件。", mrpackPath);

        Directory.CreateDirectory(targetVersionDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher/1.0 (+https://github.com/xztx127-craft/xcl2)");

        using var archive = ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("这不是一个有效的 Modrinth 整合包(.mrpack)：找不到 modrinth.index.json。");

        MrpackIndex? index;
        using (var s = indexEntry.Open())
            index = JsonSerializer.Deserialize<MrpackIndex>(s);
        if (index == null)
            throw new InvalidOperationException("modrinth.index.json 解析失败，文件可能已损坏。");

        // 1) 解压 overrides/（config、资源包等）到目标版本目录根下
        progress?.Report("解压附带文件(overrides) ...");
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var normalized = entry.FullName.Replace('\\', '/');
            string? relative = null;
            if (normalized.StartsWith("overrides/")) relative = normalized["overrides/".Length..];
            else if (normalized.StartsWith("client-overrides/")) relative = normalized["client-overrides/".Length..];
            if (relative == null || relative.Length == 0) continue;

            var destPath = Path.Combine(targetVersionDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var destFull = Path.GetFullPath(destPath);
            if (!destFull.StartsWith(Path.GetFullPath(targetVersionDir), StringComparison.OrdinalIgnoreCase)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            entry.ExtractToFile(destFull, overwrite: true);
        }

        // 2) 按清单下载每个 mod/资源文件。清单里的 env.client 标记这个文件是否是客户端需要的，
        //    "unsupported" 表示这个 mod 在客户端不需要，跳过（常见于服务端专属 mod）。
        var failed = new List<string>();
        var files = index.Files.Where(f => f.Env == null || f.Env.Client != "unsupported").ToList();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var url = file.Downloads.FirstOrDefault();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(file.Path))
            {
                failed.Add(file.Path ?? "(未知文件)");
                continue;
            }

            progress?.Report($"下载 {Path.GetFileName(file.Path)} ({i + 1}/{files.Count}) ...");
            var destPath = Path.Combine(targetVersionDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var destFull = Path.GetFullPath(destPath);
            if (!destFull.StartsWith(Path.GetFullPath(targetVersionDir), StringComparison.OrdinalIgnoreCase))
            {
                failed.Add(file.Path);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(destFull);
                await resp.Content.CopyToAsync(fs, ct);
            }
            catch
            {
                // 单个文件下载失败不中止整体导入——大型整合包里某一个 mod 源偶尔失效很常见，
                // 让用户能先玩上，之后自己去 Mod 管理页补装这一个即可，比整体导入失败友好得多。
                failed.Add(file.Path);
            }
        }

        progress?.Report("导入完成");
        return new MrpackImportResult(index.Name, index.Dependencies?.Minecraft, failed);
    }

    /// <summary>
    /// 导出为 Modrinth 格式(.mrpack)。
    ///
    /// 跟导入方向的 ImportMrpackAsync 不对称：真正"标准"的 .mrpack 导出应该在 modrinth.index.json
    /// 的 files[] 里给每个 mod 写一条 Modrinth 下载直链 + sha1，本地不含 jar 实体，体积很小。
    /// 但本地已安装的 mod jar 并不会记录自己当初是从哪个 Modrinth 项目/版本下载来的
    /// (LocalModInfo 只有文件路径/大小，没有 project id / version id / 直链这些字段)，
    /// 没有可靠依据去反查"这个 jar 对应 Modrinth 上的哪个版本"，勉强去猜(比如按文件名模糊匹配)
    /// 很容易匹配错，导入方拿到错的 mod 版本比拿不到更糟。
    ///
    /// 所以这里采用 Modrinth 官方 mrpack 格式规范里明确允许的另一种合法用法：
    /// files[] 留空，把 mods/config/resourcepacks/shaderpacks 全部实体文件放进 overrides/ 目录
    /// (跟 ImportMrpackAsync 读 overrides/ 的逻辑对称)。这样生成的 .mrpack：
    /// - 严格符合 modrinth.index.json 的 schema(games/formatVersion/name/dependencies 齐全)，
    ///   能被 Modrinth App、PrismLauncher 等其它启动器正常识别为 mrpack 并导入；
    /// - 不依赖任何"猜测"的下载直链，内容 100% 就是用户本地实际在用的文件，不会出现导入后
    ///   版本对不上的问题；
    /// - 代价是文件体积等同于内容本身(不是纯清单)，这是"能追溯来源"与"能保证正确性"之间的
    ///   权衡，这里选择保证正确性。
    /// </summary>
    public void ExportMrpack(string versionDir, string destMrpackPath, ModpackManifest manifest,
        IProgress<string>? progress = null)
        => ExportMrpack(versionDir, destMrpackPath, manifest, progress == null ? null : new Progress<ProgressInfo>(p => progress.Report(p.Stage)));

    /// <summary>ExportMrpack 的文件级进度版本，见 Export(...IProgress&lt;ProgressInfo&gt;) 的注释。</summary>
    public void ExportMrpack(string versionDir, string destMrpackPath, ModpackManifest manifest,
        IProgress<ProgressInfo>? progress)
    {
        if (File.Exists(destMrpackPath)) File.Delete(destMrpackPath);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"xcl2_mrpack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var index = new MrpackIndex
            {
                Name = string.IsNullOrWhiteSpace(manifest.Name) ? "XCL2 整合包" : manifest.Name,
                Dependencies = new MrpackDependencies
                {
                    Minecraft = manifest.McVersion,
                    FabricLoader = string.Equals(manifest.ModLoader, "Fabric", StringComparison.OrdinalIgnoreCase)
                        ? manifest.ModLoaderVersion : null,
                    Forge = string.Equals(manifest.ModLoader, "Forge", StringComparison.OrdinalIgnoreCase)
                        ? manifest.ModLoaderVersion : null,
                    NeoForge = string.Equals(manifest.ModLoader, "NeoForge", StringComparison.OrdinalIgnoreCase)
                        ? manifest.ModLoaderVersion : null,
                },
                // Files 有意留空，见上方注释：所有实体内容走 overrides/，不写远程直链。
                Files = new List<MrpackFile>(),
            };

            progress?.Report(new ProgressInfo("生成索引文件", 0, 1, "modrinth.index.json"));
            var indexJson = JsonSerializer.Serialize(index, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
            // Modrinth 官方规范要求 index.json 里还有一个 formatVersion 和 game 字段，
            // MrpackIndex 模型目前只映射了导入用得到的字段(见类定义处注释)，这里手动
            // 补上这两个固定字段而不改动导入路径用的模型，避免影响 ImportMrpackAsync。
            var indexObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(indexJson)!;
            var withHeader = new Dictionary<string, object?>
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                ["versionId"] = manifest.ExportedAtUtc,
            };
            foreach (var kv in indexObj) withHeader[kv.Key] = kv.Value;
            File.WriteAllText(Path.Combine(tmpDir, "modrinth.index.json"),
                JsonSerializer.Serialize(withHeader, new JsonSerializerOptions { WriteIndented = true }));

            var overridesDir = Path.Combine(tmpDir, "overrides");
            var sourceFolders = IncludedFolders
                .Select(f => Path.Combine(versionDir, f))
                .Where(Directory.Exists)
                .ToList();
            var totalFiles = sourceFolders.Sum(CountFiles);
            var doneFiles = 0;

            if (sourceFolders.Count == 0)
                throw new InvalidOperationException("这个版本下没有找到 mods/config/resourcepacks/shaderpacks 中任何一个文件夹，没有可打包的内容。");

            foreach (var folder in IncludedFolders)
            {
                var src = Path.Combine(versionDir, folder);
                if (!Directory.Exists(src)) continue;
                CopyDirectory(src, Path.Combine(overridesDir, folder), (file, name) =>
                {
                    doneFiles++;
                    progress?.Report(new ProgressInfo("打包文件", doneFiles, totalFiles, name));
                });
            }

            progress?.Report(new ProgressInfo("生成压缩包", totalFiles, totalFiles, Path.GetFileName(destMrpackPath)));
            ZipFile.CreateFromDirectory(tmpDir, destMrpackPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            progress?.Report(new ProgressInfo("导出完成", totalFiles, totalFiles, ""));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 忽略临时目录清理失败 */ }
        }
    }

    private static int CountFiles(string dir)
    {
        var count = Directory.GetFiles(dir).Length;
        foreach (var sub in Directory.GetDirectories(dir))
            count += CountFiles(sub);
        return count;
    }

    /// <summary>onFile 在每复制完一个文件后回调一次(源文件全路径, 用于展示的文件名)，
    /// 供导出流程汇报真实的"已复制/总数 + 当前文件名"进度；不传则跟原来行为一致，
    /// 只是纯复制不汇报。</summary>
    private static void CopyDirectory(string sourceDir, string destDir, Action<string, string>? onFile = null)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
            onFile?.Invoke(file, Path.GetFileName(file));
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)), onFile);
    }
}

/// <summary>整合包内附带的元信息，导入方可以据此展示"这个包是什么"，不影响实际导入逻辑。</summary>
public class ModpackManifest
{
    public string Name { get; set; } = "";
    public string? McVersion { get; set; }
    public string? ModLoader { get; set; }
    public string? ModLoaderVersion { get; set; }
    public string ExportedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>Modrinth modrinth.index.json 的结构（只映射导入需要用到的字段）。</summary>
public class MrpackIndex
{
    public string Name { get; set; } = "";
    [JsonPropertyName("dependencies")] public MrpackDependencies? Dependencies { get; set; }
    public List<MrpackFile> Files { get; set; } = new();
}

public class MrpackDependencies
{
    public string? Minecraft { get; set; }
    [JsonPropertyName("fabric-loader")] public string? FabricLoader { get; set; }
    [JsonPropertyName("forge")] public string? Forge { get; set; }
    [JsonPropertyName("neoforge")] public string? NeoForge { get; set; }
}

public class MrpackFile
{
    public string Path { get; set; } = "";
    public List<string> Downloads { get; set; } = new();
    public MrpackFileEnv? Env { get; set; }
}

public class MrpackFileEnv
{
    public string? Client { get; set; }
}

/// <summary>mrpack 导入结果：整合包名字、清单里声明的 MC 版本(可能为空)、下载失败的文件路径列表。</summary>
public record MrpackImportResult(string Name, string? McVersion, List<string> FailedFiles);
