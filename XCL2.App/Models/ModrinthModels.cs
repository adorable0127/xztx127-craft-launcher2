using System.Text.Json.Serialization;

namespace XCL2.App.Models;

/// <summary>
/// 资源分类：跟启动器下载中心左侧几个 Tab 对应。
///
/// 映射到 Modrinth 真实的 project_type（官方只有 mod / modpack / resourcepack / shader / plugin 五种）：
/// - 材质包 -> project_type=resourcepack（有专门类型）
/// - 数据包 -> project_type=mod + categories=datapack（Modrinth 没给数据包单独的 project_type，
///   是用 mod 类型加 "datapack" 分类标签来区分的，这是 Modrinth 官方推荐的查询方式）
/// - 光影包 -> project_type=shader（有专门类型）
/// - 地图/存档 -> Modrinth 官方目前没有"世界存档"这个 project_type（2022 年博客提过"payouts 上线后会做"，
///   到现在也一直没有独立分类），所以这里不伪造一个不准确的查询；地图 Tab 先保持"暂不支持"提示，
///   等 Modrinth 真正支持或者接入 CurseForge 后再启用。
/// </summary>
public enum ModrinthResourceType
{
    ResourcePack,
    DataPack,
    Shader,
    Mod,
    /// <summary>服务端插件(Bukkit/Spigot/Paper/Purpur plugin jar)。Modrinth 项目类型为
    /// "project_type:plugin"，下载落地目录是服务器实例的 plugins/ 文件夹。</summary>
    Plugin
}

/// <summary>GET /v2/search 的返回结构。</summary>
public class ModrinthSearchResult
{
    [JsonPropertyName("hits")] public List<ModrinthSearchHit> Hits { get; set; } = new();
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public class ModrinthSearchHit
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
}

/// <summary>GET /v2/project/{id}/version 的返回结构（数组）：一个项目下的所有可下载版本。</summary>
public class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = ""; // release/beta/alpha
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("date_published")] public string DatePublished { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();

    /// <summary>这个版本依赖的其他项目/版本，真实对应 Modrinth API 的 version.dependencies 字段
    /// （见 https://docs.modrinth.com/api/operations/getversionfromidornumber/）。用于在版本详情里
    /// 展示"前置资源"（比如 Iris Shaders 依赖 Sodium）。dependency_type 为 "required" 的才需要
    /// 提示用户一并安装，"optional"/"incompatible"/"embedded" 不在"前置资源"里强调。</summary>
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();
}

/// <summary>version.dependencies 数组里的一项。project_id/version_id 都可能是 null——
/// 有的依赖只锁定到具体某个 version_id，有的只声明 project_id（不锁定具体版本，装最新的即可），
/// 两者不总是同时存在，实际使用时优先认 project_id（用来反查该依赖项目当前的图标/名称/详情页）。</summary>
public class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
}

public class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("hashes")] public ModrinthFileHashes? Hashes { get; set; }
}

public class ModrinthFileHashes
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
}
