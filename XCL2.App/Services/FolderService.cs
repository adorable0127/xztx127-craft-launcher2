using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// .minecraft 多目录管理：添加/切换文件夹，扫描每个文件夹下 versions/ 里已安装的版本。
/// 对应 PCL 的"文件夹列表 -> 当前文件夹 -> 添加已有文件夹"交互。
/// </summary>
public class FolderService
{
    public List<GameVersion> ScanVersions(string minecraftDir)
    {
        var result = new List<GameVersion>();
        var versionsDir = Path.Combine(minecraftDir, "versions");
        if (!Directory.Exists(versionsDir)) return result;

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = ResolveVersionJson(dir, id);
            if (jsonPath == null) continue;

            try
            {
                var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jsonPath));
                if (detail == null) continue;

                // 修复"导出的整合包没有版本信息"：改用 VersionInfoResolver 做真正的解析，
                // 不再用原来那个 `InheritsFrom ?? Id` + 空字符串的假实现（见 VersionInfoResolver
                // 类头注释里对这个 bug 完整成因的说明）。
                var info = VersionInfoResolver.Resolve(dir, id, detail);
                var mcVersion = info.McVersion;
                var loader = info.ModLoader;
                var loaderVersion = info.ModLoaderVersion;

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
                var ownJarExists = File.Exists(Path.Combine(dir, $"{jarBaseName}.jar"));
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
                            inheritedJarExists = File.Exists(Path.Combine(parentDir, $"{parentJarBaseName}.jar"));
                        }
                        catch { /* 父版本 json 本身损坏，视为继承链不完整 */ }
                    }
                }

                result.Add(new GameVersion
                {
                    Id = id,
                    McVersion = mcVersion,
                    ModLoader = loader,
                    ModLoaderVersion = loaderVersion,
                    FolderPath = dir,
                    IsInstalled = ownJarExists || inheritedJarExists
                });
            }
            catch { /* 跳过损坏的版本 */ }
        }
        return result;
    }

    /// <summary>
    /// 优先按"文件夹名.json"查找（官方/常规命名方式）。如果用户把整个版本文件夹改了名字
    /// （不少第三方启动器都允许这么整理），文件夹名和内部 json 文件名就对不上了——这时退而
    /// 求其次：文件夹里改名不会影响"内部"文件名，只影响文件夹本身，所以如果这个文件夹下
    /// 就唯一一个 .json 文件，直接认它就是版本 json。
    /// </summary>
    private static string? ResolveVersionJson(string dir, string folderId)
    {
        var exact = Path.Combine(dir, $"{folderId}.json");
        if (File.Exists(exact)) return exact;

        var jsonFiles = Directory.GetFiles(dir, "*.json");
        return jsonFiles.Length == 1 ? jsonFiles[0] : null;
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
        var versionsDir = Path.Combine(minecraftDir, "versions");
        Directory.CreateDirectory(versionsDir);
        var targetDir = Path.Combine(versionsDir, instanceName);
        if (Directory.Exists(targetDir))
            throw new InvalidOperationException($"目标文件夹「{instanceName}」已经存在。");

        CopyDirectoryRecursive(sourceFolder, targetDir);
    }

    /// <summary>"导入实例文件"：解压一个打包成 .zip 的实例（可能是"版本 json 直接在压缩包根目录"，
    /// 也可能是"压缩包里包了一层文件夹再放版本 json"这两种常见打包方式），归拢进
    /// &lt;minecraftDir&gt;/versions/&lt;instanceName&gt;。</summary>
    public void ImportVersionZip(string minecraftDir, string zipPath, string instanceName)
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

            // 压缩包里如果只包了一层文件夹（没有多余文件），就把这一层"透传"掉，
            // 避免装出来变成 versions/<instanceName>/<原文件夹名>/xxx.json 这种多套一层的结构。
            var entries = Directory.GetFileSystemEntries(tempDir);
            var sourceDir = tempDir;
            if (entries.Length == 1 && Directory.Exists(entries[0]))
                sourceDir = entries[0];

            if (Directory.GetFiles(sourceDir, "*.json", SearchOption.TopDirectoryOnly).Length == 0)
                throw new InvalidOperationException("压缩包里没有找到版本 json 文件，这可能不是一个完整的实例。");

            CopyDirectoryRecursive(sourceDir, targetDir);
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
            Directory.CreateDirectory(dir.Replace(sourceDir, targetDir));
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(sourceDir, targetDir), overwrite: true);
    }
}
