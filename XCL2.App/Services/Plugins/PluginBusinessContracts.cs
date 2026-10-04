using System.Collections.Immutable;

namespace XCL2.App.Services.Plugins;

public sealed record PluginListSnapshot<T>(long Revision, DateTimeOffset CapturedAtUtc, ImmutableArray<T> Items);
public enum PluginAccountKind { Offline, Microsoft, AuthServer, Custom }
public sealed record PluginAccount(string Id, PluginAccountKind Kind, string DisplayName, string? Uuid, bool IsSelected, bool IsGuest);
public sealed record PluginInstanceQuery(bool IncludeHidden = false, bool IncludeIncomplete = true);
public sealed record PluginInstance(string Id, string Name, string MinecraftVersion, string? LoaderId,
    string? LoaderVersion, bool IsInstalled, bool IsSelected, bool IsHidden);
public enum PluginJavaResolutionState { Configured, Unresolved }
public sealed record PluginInstanceDetails(PluginInstance Instance, string InstanceDirectory, string GameDirectory,
    bool Isolated, string? ConfiguredJavaPath, PluginJavaResolutionState JavaResolutionState, ImmutableArray<string> ConfiguredJvmArguments);
public sealed record PluginMod(string FileName, string DisplayName, string? ModId, string? Version, bool IsEnabled, long FileSizeBytes);
public enum PluginDownloadState { Downloading, Paused, Completed, Failed, Canceled }
public sealed record PluginDownload(Guid Id, string Name, PluginDownloadState State, double ProgressPercent,
    string? PhaseCode, string? InstanceId, string? ErrorCode);
public sealed record PluginSettingsSnapshot(long Revision, string LauncherLanguage, string UiSkin, bool IsDarkMode,
    bool SimplifiedModeEnabled, bool EnablePageAnimations, bool EnableUiAnimations, int UiZoomPercent,
    int MinMemoryMb, int MaxMemoryMb, bool IsolateVersionsByDefault, string BuiltInDownloadSource);
public sealed record PluginEnvironmentSnapshot(string LauncherVersion, Version ApiVersion, string DataRootDirectory, string LauncherLanguage);

public interface IPluginAccounts
{
    Task<PluginListSnapshot<PluginAccount>> GetSnapshotAsync(CancellationToken ct = default);
    Task<PluginAccount?> GetSelectedAsync(CancellationToken ct = default);
}
public interface IPluginInstances
{
    Task<PluginListSnapshot<PluginInstance>> GetSnapshotAsync(PluginInstanceQuery? query = null, CancellationToken ct = default);
    Task<PluginInstanceDetails?> GetDetailsAsync(string instanceId, CancellationToken ct = default);
    Task<PluginInstance?> GetSelectedAsync(CancellationToken ct = default);
}
public interface IPluginMods { Task<PluginListSnapshot<PluginMod>> GetSnapshotAsync(string instanceId, CancellationToken ct = default); }
public interface IPluginDownloads { Task<PluginListSnapshot<PluginDownload>> GetSnapshotAsync(CancellationToken ct = default); }
public interface IPluginSettings { Task<PluginSettingsSnapshot> GetSnapshotAsync(CancellationToken ct = default); }
public interface IPluginEnvironment { Task<PluginEnvironmentSnapshot> GetSnapshotAsync(CancellationToken ct = default); }

public class PluginOperationException : Exception
{
    public string Code { get; }
    public string SafeMessage => Message;
    public PluginOperationException(string code, string safeMessage) : base(safeMessage) { Code = code; }
}
public sealed class PluginPermissionException : PluginOperationException
{
    public string Permission { get; }
    public PluginPermissionException(string permission) : base("PermissionDenied", "插件没有获准使用权限：" + permission) { Permission = permission; }
}
public sealed class PluginHostUnavailableException : PluginOperationException
{
    public PluginHostUnavailableException() : base("HostUnavailable", "此插件上下文未连接到运行中的启动器。") { }
}
public sealed class PluginResourceNotFoundException : PluginOperationException
{
    public PluginResourceNotFoundException() : base("ResourceNotFound", "实例或业务资源已不存在。") { }
}
public sealed record PluginPermissionGrant(string Permission, bool Declared, bool Granted, bool Available);

public abstract record PluginNotification
{
    public long Sequence { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
}
public sealed record PluginLauncherReady : PluginNotification
{
    public string LauncherVersion { get; init; } = "unknown";
    public Version ApiVersion { get; init; } = PluginApi.Version;
}
public sealed record PluginLauncherStopping : PluginNotification { public string ReasonCode { get; init; } = "Closing"; }
public sealed record PluginAccountAdded(PluginAccount Account) : PluginNotification;
public sealed record PluginAccountUpdated(PluginAccount Account, ImmutableArray<string> ChangedFields) : PluginNotification;
public sealed record PluginAccountRemoved(string AccountId) : PluginNotification;
public sealed record PluginAccountSelectionChanged(PluginAccount? Account) : PluginNotification;
public sealed record PluginInstanceCreated(PluginInstance Instance) : PluginNotification;
public sealed record PluginInstanceRemoved(string InstanceId) : PluginNotification;
public sealed record PluginInstanceRenamed(string OldInstanceId, string NewInstanceId, string OldName, string NewName) : PluginNotification;
public sealed record PluginInstanceSelectionChanged(string? InstanceId) : PluginNotification;
public sealed record PluginInstanceCatalogChanged(string ReasonCode) : PluginNotification;
public sealed record PluginInstanceVisibilityChanged(string InstanceId, bool IsHidden) : PluginNotification;
public sealed record PluginDownloadCreated(PluginDownload Download) : PluginNotification;
public sealed record PluginDownloadProgressChanged(PluginDownload Download) : PluginNotification;
public sealed record PluginDownloadCompleted(PluginDownload Download) : PluginNotification;
public sealed record PluginDownloadFailed(PluginDownload Download) : PluginNotification;
public sealed record PluginDownloadCanceled(Guid DownloadId, string ReasonCode) : PluginNotification;
public sealed record PluginDownloadRemoved(Guid DownloadId) : PluginNotification;
public sealed record PluginModInstalled(string InstanceId, PluginMod Mod) : PluginNotification;
public sealed record PluginModRemoved(string InstanceId, string FileName) : PluginNotification;
public sealed record PluginModEnabledChanged(string InstanceId, string FileName, bool IsEnabled) : PluginNotification;
public sealed record PluginModCatalogChanged(string InstanceId) : PluginNotification;
public sealed record PluginGameStarted(string InstanceId, int Pid) : PluginNotification;
public sealed record PluginGameExited(string InstanceId, int Pid, int? ExitCode, bool UserRequestedClose) : PluginNotification;
public sealed record PluginGameCrashed(string InstanceId, int Pid, int? ExitCode) : PluginNotification;
public sealed record PluginSettingsChanged(ImmutableArray<string> ChangedKeys) : PluginNotification;
