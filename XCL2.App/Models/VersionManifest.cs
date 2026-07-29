using System.Text.Json.Serialization;

namespace XCL2.App.Models;

public class VersionManifestRoot
{
    [JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<VersionManifestEntry> Versions { get; set; } = new();
}

public class LatestVersions
{
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
}

public enum VersionCategory
{
    Release,
    Snapshot,
    AprilFools,
    Legacy // old_alpha / old_beta
}

public class VersionManifestEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("releaseTime")] public string ReleaseTime { get; set; } = "";

    /// <summary>
    /// Mojang 官方 version_manifest 的 "type" 字段只有 release / snapshot / old_beta / old_alpha 四种，
    /// 愚人节版本(如 15w14a、2.0、3D Shareware v1.34、22w13oneblockatatime、23w13a_or_b、24w14potato、
    /// 25w14craftmine、26w14a)在 manifest 里也被标记成 "snapshot"，官方没有单独区分，只能靠已知 id 名单识别。
    /// 这里维护一份已知愚人节版本清单；未来每年新出的愚人节版本需要手动追加到这个集合里。
    ///
    /// 26w14a ("Herdcraft Update") 是 2026 年愚人节快照，2026-04-01 发布，
    /// 沿用旧的 "w<周数><字母>" 快照命名格式（不同于同年正式版 26.x 的编号规则）。
    /// </summary>
    private static readonly HashSet<string> KnownAprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "2.0", "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
        "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a"
    };

    public VersionCategory GetCategory()
    {
        if (Type is "old_alpha" or "old_beta") return VersionCategory.Legacy;
        if (KnownAprilFoolsIds.Contains(Id)) return VersionCategory.AprilFools;
        return Type == "release" ? VersionCategory.Release : VersionCategory.Snapshot;
    }
}

/// <summary>version_id.json 的核心字段（客户端 jar、库、资源索引、主类等）</summary>
public class VersionDetail
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("mainClass")] public string MainClass { get; set; } = "";
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("arguments")] public ArgumentsSpec? Arguments { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArgumentsLegacy { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryEntry> Libraries { get; set; } = new();
    [JsonPropertyName("downloads")] public Dictionary<string, DownloadArtifact>? Downloads { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionSpec? JavaVersion { get; set; }

    /// <summary>
    /// 部分较新版本(约 1.13/18w47b 之后)的 version json 里会额外带一个 clientVersion 字段，
    /// 记录跟 id 字段独立的"真实游戏版本号"(id 字段可能被启动器/用户改名，比如
    /// "26.2服务器"，clientVersion 依然是干净的 "26.2")。
    ///
    /// 这个字段的存在与否，被用作判断"这个版本是不是新版本机制"的依据：
    /// 从 18w47b 起，client.jar 内部会自带一份独立的 version.json 元数据，Minecraft
    /// 主菜单左下角的版本文字优先读取这份内嵌数据，不再采信启动参数里的 "--version"；
    /// 而更老的版本没有这套内嵌机制，主菜单文字就是直接显示 "--version" 参数原文。
    /// 用户实测证实：低版本能通过 "--version" 参数追加水印文字成功，高版本不行，
    /// 与这个机制完全吻合。
    /// </summary>
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
}

public class JavaVersionSpec
{
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; } = 17;
}

public class ArgumentsSpec
{
    [JsonPropertyName("game")] public List<object>? Game { get; set; }
    [JsonPropertyName("jvm")] public List<object>? Jvm { get; set; }
}

public class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class DownloadArtifact
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
}

public class LibraryEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("rules")] public List<LibraryRule>? Rules { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }

    /// <summary>
    /// Fabric/Quilt 风格的库条目没有 "downloads" 对象，只给 Maven 坐标 name + 仓库 url，
    /// 例如 {"name":"net.fabricmc:fabric-loader:0.15.11","url":"https://maven.fabricmc.net/"}。
    /// </summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>
    /// 把 "group:artifact:version[:classifier]" 坐标换算成标准 maven 仓库相对路径：
    /// group(.替换为/)/artifact/version/artifact-version[-classifier].jar
    /// 用于 downloads.artifact 缺失（Fabric/Quilt 等）时定位/拼接库文件。
    /// </summary>
    public string? GetMavenPath()
    {
        if (string.IsNullOrWhiteSpace(Name)) return null;
        var parts = Name.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>
    /// 按照 Mojang version json 的 rules 规则，判断这条库在当前系统(Windows)下是否适用。
    /// 没有 rules 字段的条目一律视为适用；有 rules 就按顺序应用，最后一条匹配的规则的 action 生效——
    /// 这是官方启动器和主流第三方启动器共用的标准算法。本启动器只跑在 Windows 上，固定按 "windows" 匹配。
    /// 之前只有下载阶段(DownloadService)做了这个过滤，启动阶段(LauncherService)完全没做，
    /// 导致版本 json 里 Linux/macOS 专用的 native 库条目(本来就不会被下载)在启动时被误判成"缺失"，
    /// 纯净原版也会报"依赖库不存在"。现在两边统一调用这一个方法，不会再出现口径不一致。
    /// </summary>
    public bool IsApplicableToCurrentOs()
    {
        if (Rules == null || Rules.Count == 0) return true;
        var allow = false;
        foreach (var rule in Rules)
        {
            var matchesOs = rule.Os == null || !rule.Os.ContainsKey("name") ||
                (rule.Os.TryGetValue("name", out var osName) && osName == "windows");
            if (matchesOs) allow = rule.Action == "allow";
        }
        return allow;
    }
}

public class LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadArtifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public class LibraryRule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public Dictionary<string, string>? Os { get; set; }
}

/// <summary>asset index 内容：虚拟资源文件到 hash 的映射</summary>
public class AssetIndexFile
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

public class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}
