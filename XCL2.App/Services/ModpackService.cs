using System.IO;
using System.IO.Compression;
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
    {
        if (File.Exists(destZipPath)) File.Delete(destZipPath);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"xcl2_pack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var foldersFound = 0;
            foreach (var folder in IncludedFolders)
            {
                var src = Path.Combine(versionDir, folder);
                if (!Directory.Exists(src)) continue;

                progress?.Report($"打包 {folder} ...");
                CopyDirectory(src, Path.Combine(tmpDir, folder));
                foldersFound++;
            }

            if (foldersFound == 0)
                throw new InvalidOperationException("这个版本下没有找到 mods/config/resourcepacks/shaderpacks 中任何一个文件夹，没有可打包的内容。");

            progress?.Report("生成压缩包 ...");
            ZipFile.CreateFromDirectory(tmpDir, destZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            progress?.Report("导出完成");
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

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
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
