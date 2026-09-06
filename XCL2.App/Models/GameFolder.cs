namespace XCL2.App.Models;

/// <summary>
/// 一个 .minecraft 根目录（类似 PCL 的"文件夹列表"）。
/// 每个 GameFolder 下可以有多个版本(Version)。
/// </summary>
public class GameFolder
{
    public string Name { get; set; } = "当前文件夹";
    public string Path { get; set; } = "";
    public bool IsDefault { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// .minecraft/versions/<VersionId> 下的一个可运行版本。
/// </summary>
public class GameVersion
{
    public string Id { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string? ModLoader { get; set; } // Fabric / Forge / NeoForge / Quilt / null
    public string? ModLoaderVersion { get; set; }
    public string FolderPath { get; set; } = "";
    public bool IsInstalled { get; set; }

    // 需求修复："一键启动所选的实例"选择版本的下拉框没有显示用户自己设置的名称"：
    // Id 就是 versions/<Id> 的文件夹名，也正是用户改名时（TryRenameInstalledInstance）
    // 实际写回的那个值——SubTitle 之前完全没用到 Id，只拼了 McVersion/加载器，导致
    // 不管用户设置过什么名字，下拉框里看到的永远是"正式版 1.0"这种通用文案，
    // 装了很多个自定义命名的版本时根本分不清哪个是哪个。格式统一成
    // "用户设置的名称(版本 加载器 版本号)"；没装加载器的原版括号里只放版本号。
    public string SubTitle => ModLoader is null
        ? $"{Id}（{McVersion}）"
        : $"{Id}（{McVersion} {ModLoader} {ModLoaderVersion}）";
}
