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

    public string SubTitle => ModLoader is null
        ? $"正式版 {McVersion}"
        : $"正式版 {McVersion}, {ModLoader} {ModLoaderVersion}";
}
