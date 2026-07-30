using System.Text.Json.Serialization;

namespace XCL2.App.Models;

/// <summary>
/// CurseForge API (api.curseforge.com) 的返回结构，目前只接入"地图/存档"这一类——
/// Modrinth 没有对应分类，这块靠 CurseForge 补上。
///
/// Minecraft 在 CurseForge 里的 gameId 固定是 432；地图/存档对应的 classId 是 17（Worlds）。
/// 这两个数字是 CurseForge 官方 Minecraft 分类体系里固定的，不随游戏版本变化。
/// </summary>
public static class CurseForgeConstants
{
    public const int MinecraftGameId = 432;
    public const int WorldsClassId = 17;
    /// <summary>Mods 分类的 classId，CurseForge Minecraft 分类体系固定值。</summary>
    public const int ModsClassId = 6;
    /// <summary>材质包(Resource Packs)分类的 classId，CurseForge Minecraft 分类体系固定值。
    /// 修复"资源包搜不到 CurseForge 结果"：之前 CurseForgeService 根本没有任何方法用这个 id 发过请求，
    /// 材质包分类在下载中心只接了 Modrinth 一条来源。</summary>
    public const int ResourcePacksClassId = 12;
    /// <summary>光影包(Shaders)分类的 classId，CurseForge Minecraft 分类体系固定值。</summary>
    public const int ShaderPacksClassId = 6552;
    /// <summary>数据包(Data Packs)分类的 classId，CurseForge Minecraft 分类体系固定值。</summary>
    public const int DataPacksClassId = 6945;
    /// <summary>Bukkit 插件(Bukkit Plugins)分类的 classId，CurseForge Minecraft 分类体系固定值。
    /// 用于服务端管理页的"插件下载"分类——之前该分类完全没实现，只是占位符。</summary>
    public const int BukkitPluginsClassId = 5;
}

/// <summary>材质包/光影包/数据包三种资源类型，供 CurseForgeService.SearchResourcesAsync/DownloadResourceAsync
/// 区分 classId 和下载落地目录，跟 ModrinthResourceType 的分类含义保持一致（去掉 Mod 是因为
/// Mod 已经有专门的 SearchModsAsync/DownloadModAsync）。</summary>
public enum CurseForgeResourceKind
{
    ResourcePack,
    Shader,
    DataPack,
    /// <summary>服务端插件(Bukkit/Spigot/Paper plugin jar)，下载落地目录是服务器实例的 plugins/ 文件夹。</summary>
    Plugin
}

/// <summary>GET /v1/mods (搜索) 的返回结构。</summary>
public class CurseForgeSearchResult
{
    [JsonPropertyName("data")] public List<CurseForgeMod> Data { get; set; } = new();
    [JsonPropertyName("pagination")] public CurseForgePagination? Pagination { get; set; }
}

public class CurseForgePagination
{
    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
}

public class CurseForgeMod : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
    [JsonPropertyName("authors")] public List<CurseForgeAuthor> Authors { get; set; } = new();
    [JsonPropertyName("logo")] public CurseForgeAsset? Logo { get; set; }
    [JsonPropertyName("latestFiles")] public List<CurseForgeFile> LatestFiles { get; set; } = new();
    /// <summary>官方 v1 /mods/search 返回的 links.websiteUrl，即这个项目在 curseforge.com 上的详情页地址。
    /// 用于详情页"转到 CurseForge"按钮——CurseForge 项目没有像 Modrinth 那样简单的
    /// "https://modrinth.com/mod/{slug}"拼接规律（slug 本身不在这个响应体里），直接用官方给的现成链接最可靠。</summary>
    [JsonPropertyName("links")] public CurseForgeLinks? Links { get; set; }

    public string AuthorsDisplay => Authors.Count == 0 ? "未知作者" : string.Join(", ", Authors.Select(a => a.Name));

    /// <summary>是否已收藏（地图分类新增的收藏按钮用）。反序列化 API 响应时不会有这个字段，
    /// 由 DownloadCenterPage 在把搜索结果塞进 _maps 集合前，按 FavoriteItems 里是否存在
    /// (Map, Id.ToString(), CurseForge) 这一条来赋值——纯前端展示状态，不参与 JSON 往返。</summary>
    [JsonIgnore]
    private bool _isFavorite;
    [JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnPropertyChanged(nameof(IsFavorite)); OnPropertyChanged(nameof(FavoriteLabel)); }
    }

    [JsonIgnore]
    public string FavoriteLabel => IsFavorite ? "★ 已收藏" : "☆ 收藏";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public class CurseForgeAuthor
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class CurseForgeAsset
{
    [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
}

/// <summary>CurseForge Mod 对象的 links 子结构，只取详情页需要的 websiteUrl 一个字段。</summary>
public class CurseForgeLinks
{
    [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; }
}

/// <summary>GET /v1/mods/{modId}/files 的返回结构（数组元素）：一个地图下所有可下载的文件版本。</summary>
public class CurseForgeFile
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("fileDate")] public string FileDate { get; set; } = "";
    [JsonPropertyName("fileLength")] public long FileLength { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("gameVersions")] public List<string> GameVersions { get; set; } = new();
}

public class CurseForgeFileListResult
{
    [JsonPropertyName("data")] public List<CurseForgeFile> Data { get; set; } = new();
}
