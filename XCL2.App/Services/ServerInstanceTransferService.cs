using System.IO;
using System.IO.Compression;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 服务器实例的导入导出（"存档导入导出"）。
///
/// 和客户端 ModpackService 的关键差异：ModpackService 只打包 mods/config/resourcepacks/
/// shaderpacks 几个内容文件夹，是"分享整合包内容"场景；这里打包的是整个服务端目录
/// （含 world 存档、plugins、server.properties 等所有文件），是"迁移/备份一个完整服务器实例"
/// 场景，所以不做文件夹白名单过滤，Directory 下所有内容原样进包。
///
/// 导出包内额外附带一份 ServerInstance 的配置快照（内存/CPU上限、加载器类型等），导入时
/// 可以据此原样恢复一个新的 ServerInstance 记录，不需要用户重新走一遍创建向导手动填参数。
/// </summary>
public class ServerInstanceTransferService
{
    private const string ManifestFileName = "xcl2-server-manifest.json";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// 导出：把 instance.Directory 整个目录打包进 destZipPath，附带配置快照。
    /// </summary>
    public void Export(ServerInstance instance, string destZipPath, IProgress<string>? progress = null)
    {
        if (!Directory.Exists(instance.Directory))
            throw new DirectoryNotFoundException($"服务器目录不存在，无法导出：{instance.Directory}");

        if (File.Exists(destZipPath)) File.Delete(destZipPath);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"xcl2_server_export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            progress?.Report("写入配置快照 ...");
            var manifest = ServerInstanceManifest.FromInstance(instance);
            File.WriteAllText(Path.Combine(tmpDir, ManifestFileName), JsonSerializer.Serialize(manifest, JsonOpts));

            progress?.Report("复制服务器文件 ...");
            var contentDir = Path.Combine(tmpDir, "content");
            CopyDirectory(instance.Directory, contentDir);

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
    /// 导入：解压到 targetDir，返回配置快照（若包内没有 manifest，返回 null，调用方应提示
    /// 用户手动补全配置，比如加载器类型、内存设置等，因为无法从纯文件内容可靠地反推）。
    /// targetDir 若已存在内容，采用"合并覆盖"策略，与 ModpackService 保持一致的语义。
    /// </summary>
    public ServerInstanceManifest? Import(string zipPath, string targetDir, IProgress<string>? progress = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("找不到导入的存档文件。", zipPath);

        Directory.CreateDirectory(targetDir);
        var targetFullPath = Path.GetFullPath(targetDir);

        progress?.Report("解压存档 ...");
        using var archive = ZipFile.OpenRead(zipPath);

        ServerInstanceManifest? manifest = null;
        var manifestEntry = archive.GetEntry(ManifestFileName);
        if (manifestEntry != null)
        {
            try
            {
                using var stream = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<ServerInstanceManifest>(stream);
            }
            catch
            {
                // manifest 损坏不影响继续导入文件内容，只是拿不到"原样恢复配置"的能力
            }
        }

        var importedAny = false;
        const string contentPrefix = "content/";
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目
            var normalizedPath = entry.FullName.Replace('\\', '/');
            if (!normalizedPath.StartsWith(contentPrefix, StringComparison.Ordinal)) continue;

            var relativePath = normalizedPath[contentPrefix.Length..];
            var destPath = Path.Combine(targetDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destFullPath = Path.GetFullPath(destPath);

            // 路径穿越防护：解出来的最终路径必须落在目标目录内部，防止 zip 内构造
            // "content/../../xxx" 之类的条目写到目标目录之外。
            if (!destFullPath.StartsWith(targetFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destFullPath)!);
            entry.ExtractToFile(destFullPath, overwrite: true);
            importedAny = true;
        }

        if (!importedAny)
            throw new InvalidOperationException("这个压缩包里没有找到有效的服务器文件内容，可能不是有效的存档导出文件。");

        progress?.Report("导入完成");
        return manifest;
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

/// <summary>
/// 导出包内附带的配置快照。刻意不直接复用 ServerInstance 本体做序列化对象——
/// Id/Directory 这两个字段导入到新机器/新路径后必须重新生成，不能照搬原实例的，
/// 用独立的类型显式排除这两个字段，避免以后 ServerInstance 加字段时无意间也序列化进导出包。
/// </summary>
public class ServerInstanceManifest
{
    public string DisplayName { get; set; } = "";
    public ServerCoreType CoreType { get; set; }
    public string McVersion { get; set; } = "";
    public string LaunchTarget { get; set; } = "server.jar";
    public bool LaunchTargetIsScript { get; set; }
    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;
    public int? CpuLimitPercent { get; set; }
    public int? DiskLimitMb { get; set; }
    public string ExtraJvmArgs { get; set; } = "";
    public string ExportedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

    public static ServerInstanceManifest FromInstance(ServerInstance instance) => new()
    {
        DisplayName = instance.DisplayName,
        CoreType = instance.CoreType,
        McVersion = instance.McVersion,
        LaunchTarget = instance.LaunchTarget,
        LaunchTargetIsScript = instance.LaunchTargetIsScript,
        MinMemoryMb = instance.MinMemoryMb,
        MaxMemoryMb = instance.MaxMemoryMb,
        CpuLimitPercent = instance.CpuLimitPercent,
        DiskLimitMb = instance.DiskLimitMb,
        ExtraJvmArgs = instance.ExtraJvmArgs
    };

    /// <summary>
    /// 用这份快照的配置字段构造一个全新的 ServerInstance（Id 重新生成，Directory 由调用方指定，
    /// 不是导出时的原路径——导入方很可能装在不同电脑/不同盘符下）。
    /// </summary>
    public ServerInstance ToNewInstance(string directory)
    {
        return new ServerInstance
        {
            DisplayName = DisplayName,
            Directory = directory,
            CoreType = CoreType,
            McVersion = McVersion,
            LaunchTarget = LaunchTarget,
            LaunchTargetIsScript = LaunchTargetIsScript,
            MinMemoryMb = MinMemoryMb,
            MaxMemoryMb = MaxMemoryMb,
            CpuLimitPercent = CpuLimitPercent,
            DiskLimitMb = DiskLimitMb,
            ExtraJvmArgs = ExtraJvmArgs
        };
    }
}
