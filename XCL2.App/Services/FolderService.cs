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

                var (mcVersion, loader, loaderVersion) = GuessLoaderInfo(id, detail);

                // jar 文件名以 json 内部自带的 "id" 字段为准，而不是文件夹名：
                // 用户把版本文件夹改名后，文件夹里的 json/jar 文件名本身不会跟着变，
                // 只有外层文件夹名变了，这样改名后依然能正确判断"是否已装好"。
                var jarBaseName = string.IsNullOrEmpty(detail.Id) ? id : detail.Id;

                result.Add(new GameVersion
                {
                    Id = id,
                    McVersion = mcVersion,
                    ModLoader = loader,
                    ModLoaderVersion = loaderVersion,
                    FolderPath = dir,
                    IsInstalled = File.Exists(Path.Combine(dir, $"{jarBaseName}.jar")) || detail.InheritsFrom != null
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

    private static (string mcVersion, string? loader, string? loaderVersion) GuessLoaderInfo(string id, VersionDetail detail)
    {
        var lowerId = id.ToLowerInvariant();
        string? loader = null;
        if (lowerId.Contains("fabric")) loader = "Fabric";
        else if (lowerId.Contains("neoforge")) loader = "NeoForge";
        else if (lowerId.Contains("forge")) loader = "Forge";
        else if (lowerId.Contains("quilt")) loader = "Quilt";

        var mcVersion = detail.InheritsFrom ?? detail.Id;
        return (mcVersion, loader, loader != null ? "" : null);
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
}
