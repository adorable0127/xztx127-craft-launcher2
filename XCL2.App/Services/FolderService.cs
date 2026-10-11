using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// .minecraft 多目录管理：添加/切换文件夹，扫描每个文件夹下 versions/ 里的实例。
/// 对应 PCL 的"文件夹列表 -> 当前文件夹 -> 添加已有文件夹"交互。
/// </summary>
public class FolderService
{
    public List<GameVersion> ScanVersions(string minecraftDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<GameVersion>();
        var versionsDir = Path.Combine(minecraftDir, "versions");
        if (!Directory.Exists(versionsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            // 以点开头的文件夹（如 .instance_backups）是启动器/系统的内部目录，不是实例，不放进版本列表。
            if (string.IsNullOrEmpty(id) || id.StartsWith('.')) continue;
            // 文件夹里一个 json 文件都没有，说明根本不是版本文件夹，同样不放进列表。
            if (!HasAnyJsonFile(dir)) continue;
            // 其余文件夹本身就是列表入口。json 损坏/缺 jar 的实例也保留，供用户修复或删除。
            var version = new GameVersion { Id = id, McVersion = id, FolderPath = dir };

            try
            {
                var jsonPath = ResolveVersionJson(dir, id);
                var detail = jsonPath == null ? null : JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jsonPath));

                // 修复"导出的整合包没有版本信息"：改用 VersionInfoResolver 做真正的解析，
                // 不再用原来那个 `InheritsFrom ?? Id` + 空字符串的假实现（见 VersionInfoResolver
                // 类头注释里对这个 bug 完整成因的说明）。
                var info = VersionInfoResolver.Resolve(dir, id, detail);
                version.McVersion = info.McVersion;
                version.ModLoader = info.ModLoader;
                version.ModLoaderVersion = info.ModLoaderVersion;

                if (detail == null)
                {
                    result.Add(version);
                    continue;
                }

                // jar 文件名以 json 内部自带的 "id" 字段为准，而不是文件夹名：
                // 用户把版本文件夹改名后，文件夹里的 json/jar 文件名本身不会跟着变，
                // 只有外层文件夹名变了，这样改名后依然能正确判断"是否已装好"。
                var jarBaseName = string.IsNullOrEmpty(detail.Id) ? id : detail.Id;

                // 修复"版本没装完整仍然显示为已安装"：之前 InheritsFrom != null 就直接判定
                // IsInstalled=true，没有校验它指向的父版本文件夹是否真的有 jar——如果父版本本身
                // 也没装完整（比如网络中断导致原版 client.jar 没下完就中止），这种残缺的继承链版本
                // 会被误判为"已安装"，用户点启动才发现根本起不来。这里对 InheritsFrom 的情况额外
                // 递归检查一次父版本自己的 jar 是否存在（只查一层，官方/主流加载器的继承链本来就
                // 不会嵌套超过一层：加载器版本 inherits 原版，原版不会再 inherits 别的版本）。
                var ownJarExists = LauncherService.ResolveVersionFile(dir, jarBaseName, "jar") != null;
                var inheritedJarExists = false;
                if (!ownJarExists && !string.IsNullOrEmpty(detail.InheritsFrom))
                {
                    var parentDir = Path.Combine(versionsDir, detail.InheritsFrom);
                    var parentJsonPath = ResolveVersionJson(parentDir, detail.InheritsFrom);
                    if (parentJsonPath != null)
                    {
                        try
                        {
                            var parentDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
                            var parentJarBaseName = string.IsNullOrEmpty(parentDetail?.Id) ? detail.InheritsFrom : parentDetail!.Id;
                            inheritedJarExists = parentDetail != null &&
                                LauncherService.ResolveVersionFile(parentDir, parentJarBaseName, "jar") != null;
                        }
                        catch { /* 父版本 json 本身损坏，视为继承链不完整 */ }
                    }
                }

                version.IsInstalled = ownJarExists || inheritedJarExists;
            }
            catch { /* 单个实例正在复制或元数据损坏时仍显示为未完成，不影响其他实例。 */ }
            result.Add(version);
        }
        return result.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>目录顶层是否至少有一个 .json 文件；目录正在被复制/无法访问时视为有，避免列表闪烁。</summary>
    private static bool HasAnyJsonFile(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Any(); }
        catch { return true; }
    }

    /// <summary>
    /// 优先按"文件夹名.json"查找（官方/常规命名方式）。如果用户把整个版本文件夹改了名字
    /// （不少第三方启动器都允许这么整理），文件夹名和内部 json 文件名就对不上了——这时退而
    /// 求其次：按版本字段识别内部 json，排除整合包清单、配置等额外 json。
    /// 多个版本描述文件无法确定时不随意选择；列表仍保留该文件夹并提示需要修复。
    /// </summary>
    internal static string? ResolveVersionJson(string dir, string folderId)
    {
        if (!Directory.Exists(dir)) return null;
        var exact = Path.Combine(dir, $"{folderId}.json");
        if (File.Exists(exact)) return exact;

        var jsonFiles = Directory.GetFiles(dir, "*.json");
        var candidates = new List<string>();
        foreach (var path in jsonFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString())) continue;
                var hasMainClass = root.TryGetProperty("mainClass", out var mainClass) &&
                    mainClass.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mainClass.GetString());
                var hasParent = root.TryGetProperty("inheritsFrom", out var parent) &&
                    parent.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(parent.GetString());
                if (!hasMainClass && !hasParent) continue;
                if (string.Equals(id.GetString(), folderId, StringComparison.OrdinalIgnoreCase)) return path;
                candidates.Add(path);
            }
            catch { /* 忽略无关或尚未写完的 json，继续寻找版本描述文件。 */ }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>扫描 &lt;minecraftDir&gt;/saves/ 下的存档名（用于数据包安装时选择目标存档）。</summary>
    public List<string> ScanSaves(string minecraftDir)
    {
        var savesDir = Path.Combine(minecraftDir, "saves");
        if (!Directory.Exists(savesDir)) return new List<string>();
        return Directory.GetDirectories(savesDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }

    public GameFolder AddFolder(AppConfig config, string path, string? name = null)
    {
        Directory.CreateDirectory(path);
        var folder = new GameFolder { Name = name ?? Path.GetFileName(path.TrimEnd('\\', '/')), Path = path };
        config.Folders.Add(folder);
        return folder;
    }

    /// <summary>"导入实例文件夹"：把一个现成的版本文件夹（源头可以是备份、其它启动器导出的实例，
    /// 只要求源文件夹本身直接含有版本 json）原样拷贝进 &lt;minecraftDir&gt;/versions/&lt;instanceName&gt;。
    /// 跟"导入整合包"的区别：这里不解析清单、不下载任何东西，纯粹是文件拷贝——用户导入的
    /// 就是一个已经能跑的现成实例。</summary>
    public void ImportVersionFolder(string minecraftDir, string sourceFolder, string instanceName)
    {
        if (Plugins.PluginInterceptionRegistry.HasInstanceInterceptors) throw new Plugins.PluginOperationException("AsyncRequired", "请使用异步实例导入。");
        ImportVersionFolderCore(minecraftDir, sourceFolder, instanceName);
    }
    public async Task ImportVersionFolderAsync(string minecraftDir, string sourceFolder, string instanceName, CancellationToken ct = default)
    {
        await Plugins.PluginInterceptionRegistry.ValidateInstanceAsync(minecraftDir, instanceName, "folder-import", ct);
        await Task.Run(() => { ct.ThrowIfCancellationRequested(); ImportVersionFolderCore(minecraftDir, sourceFolder, instanceName); }, ct);
    }
    private void ImportVersionFolderCore(string minecraftDir, string sourceFolder, string instanceName)
    {
        var versionsDir = Path.Combine(minecraftDir, "versions");
        Directory.CreateDirectory(versionsDir);
        var targetDir = Path.Combine(versionsDir, instanceName);
        if (Directory.Exists(targetDir))
            throw new InvalidOperationException($"目标文件夹「{instanceName}」已经存在。");

        CopyDirectoryRecursive(sourceFolder, targetDir);
        Plugins.PluginHostBridge.Current?.NotifyInstanceCreated(minecraftDir, instanceName);
    }

    /// <summary>"导入实例文件"：解压一个打包成 .zip 的实例（可能是"版本 json 直接在压缩包根目录"，
    /// 也可能是"压缩包里包了一层文件夹再放版本 json"这两种常见打包方式），归拢进
    /// &lt;minecraftDir&gt;/versions/&lt;instanceName&gt;。</summary>
    public void ImportVersionZip(string minecraftDir, string zipPath, string instanceName)
    {
        if (Plugins.PluginInterceptionRegistry.HasInstanceInterceptors) throw new Plugins.PluginOperationException("AsyncRequired", "请使用异步实例导入。");
        ImportVersionZipCore(minecraftDir, zipPath, instanceName);
    }
    public async Task ImportVersionZipAsync(string minecraftDir, string zipPath, string instanceName, CancellationToken ct = default)
    {
        await Plugins.PluginInterceptionRegistry.ValidateInstanceAsync(minecraftDir, instanceName, "zip-import", ct);
        await Task.Run(() => { ct.ThrowIfCancellationRequested(); ImportVersionZipCore(minecraftDir, zipPath, instanceName); }, ct);
    }
    private void ImportVersionZipCore(string minecraftDir, string zipPath, string instanceName)
    {
        var versionsDir = Path.Combine(minecraftDir, "versions");
        Directory.CreateDirectory(versionsDir);
        var targetDir = Path.Combine(versionsDir, instanceName);
        if (Directory.Exists(targetDir))
            throw new InvalidOperationException($"目标文件夹「{instanceName}」已经存在。");

        var tempDir = Path.Combine(Path.GetTempPath(), "XCL2_ImportInstance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempDir);

            // 兼容多层包装及 .minecraft/versions/<版本>，避免元数据被拷到实例的子目录里。
            var sourceDir = tempDir;
            while (ResolveVersionJson(sourceDir, Path.GetFileName(sourceDir)) == null)
            {
                var minecraftDirInZip = Path.Combine(sourceDir, ".minecraft");
                var versionsDirInZip = Path.Combine(sourceDir, "versions");
                if (Directory.Exists(minecraftDirInZip))
                {
                    sourceDir = minecraftDirInZip;
                    continue;
                }
                if (Directory.Exists(versionsDirInZip))
                {
                    sourceDir = versionsDirInZip;
                    continue;
                }
                var directories = Directory.GetDirectories(sourceDir)
                    .Where(d => !string.Equals(Path.GetFileName(d), "__MACOSX", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (directories.Length != 1) break;
                sourceDir = directories[0];
            }

            if (ResolveVersionJson(sourceDir, Path.GetFileName(sourceDir)) == null)
                throw new InvalidOperationException("压缩包里没有找到版本 json 文件，这可能不是一个完整的实例。");

            CopyDirectoryRecursive(sourceDir, targetDir);
            Plugins.PluginHostBridge.Current?.NotifyInstanceCreated(minecraftDir, instanceName);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时文件清理失败不影响导入结果 */ }
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, dir)));
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(targetDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
    }
}
