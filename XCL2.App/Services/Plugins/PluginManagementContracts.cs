using System.Collections.Immutable;

namespace XCL2.App.Services.Plugins;

/// <summary>API 2.2：只引用宿主索引的目录、实例、运行时和下载。</summary>
public sealed record PluginGameFolder(string Id, string Name, string Path, bool IsSelected);
public sealed record PluginInstanceDirectories(string InstanceId, string InstanceDirectory, string GameDirectory,
    string ModsDirectory, string ResourcePacksDirectory, string ShaderPacksDirectory, string SavesDirectory);
public sealed record PluginJavaRuntime(string Id, string Name, string JavawPath, int? MajorVersion, string Source,
    int Priority, bool IsSelected, bool Exists);
public sealed record PluginDownloadDetails(PluginDownload Download, bool CanPause, bool CanResume, bool CanCancel, bool IsRunning);
public sealed record PluginDownloadAcceptance(Guid DownloadId, string Code);
public sealed record PluginGameVersion(string Id, string Type, string ReleaseTime, bool IsLatestRelease, bool IsLatestSnapshot);
public enum PluginResourceKind { Mod, ResourcePack, Shader }
public sealed record PluginResourceSource(string Id, string Name, bool IsPlugin, ImmutableArray<PluginResourceKind> Kinds);
public sealed record PluginResourceQuery(string SourceId, PluginResourceKind Kind, string Query = "",
    string? GameVersion = null, string? Loader = null, int Offset = 0, int Limit = 20);
public sealed record PluginResourceProject(string SourceId, string Id, PluginResourceKind Kind, string Title,
    string Description, string Author);
public sealed record PluginResourceSearchResult(ImmutableArray<PluginResourceProject> Items, int Total, int Offset, int Limit);
public sealed record PluginResourceFileQuery(string SourceId, PluginResourceKind Kind, string ProjectId,
    string? GameVersion = null, string? Loader = null);
/// <summary>Token 仅本插件上下文可用，30 分钟有效；没有泄露带签名的下载 URL。</summary>
public sealed record PluginResourceFile(string Token, string Id, string Name, string FileName,
    ImmutableArray<string> GameVersions, ImmutableArray<string> Loaders, long? SizeBytes, string? Sha1);
/// <summary>只安装先前 files 返回的文件；默认禁止替换已有文件，允许替换必须显式传 true。</summary>
public sealed record PluginResourceInstallRequest(string Token, string InstanceId, bool ReplaceExisting = false);
