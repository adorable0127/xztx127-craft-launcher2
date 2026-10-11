using System.Collections.Immutable;

namespace XCL2.App.Services.Plugins;

/// <summary>可从任意线程使用。每次调用重新检查当前授权，撤销不需要插件重载。</summary>
public sealed class PluginPermissionService
{
    private readonly PluginContext _context;
    internal PluginPermissionService(PluginContext context) { _context = context; }
    public bool Has(string permission)
    {
        if (_context.IsStopped) return false;
        if (_context.Metadata.IsLegacy)
            return permission is PluginPermissionNames.UiBasic or PluginPermissionNames.UiComponents;
        return IsAvailable(permission) && _context.Metadata.Permissions.Contains(permission, StringComparer.Ordinal) &&
            _context.GrantedPermissions.Contains(permission) && (!_context.BusinessDisabled || IsUi(permission));
    }
    public ImmutableArray<PluginPermissionGrant> GetSnapshot() => PluginPermissionNames.All.Select(permission =>
        new PluginPermissionGrant(permission, _context.Metadata.Permissions.Contains(permission, StringComparer.Ordinal),
            Has(permission), IsAvailable(permission))).ToImmutableArray();

    public void Demand(string permission)
    {
        _context.EnsureActive();
        if (!IsAvailable(permission)) throw new PluginOperationException("CapabilityUnavailable", "当前宿主没有实现该能力：" + permission);
        if (Has(permission)) return;
        _context.Log("PermissionDenied: " + permission);
        throw new PluginPermissionException(permission);
    }
    private static bool IsUi(string permission) => permission.StartsWith("ui.", StringComparison.Ordinal);
    internal static bool IsAvailable(string permission) => permission switch
    {
        PluginPermissionNames.ProjectsRead or PluginPermissionNames.ProjectsWrite or PluginPermissionNames.TasksManage => PluginApi.Version >= new Version(2, 3),
        PluginPermissionNames.DownloadsControl or PluginPermissionNames.InstancesWrite or PluginPermissionNames.ModsWrite or
        PluginPermissionNames.ResourcesRead or PluginPermissionNames.ResourcesInstall or PluginPermissionNames.JavaRead or
        PluginPermissionNames.JavaSelect => PluginApi.Version >= new Version(2, 2),
        PluginPermissionNames.LauncherControl or PluginPermissionNames.SettingsWrite or PluginPermissionNames.UiWindow => PluginApi.Version >= new Version(2, 1),
        PluginPermissionNames.UiBasic or PluginPermissionNames.UiComponents or PluginPermissionNames.UiPreview or PluginPermissionNames.WorldsRead or PluginPermissionNames.WorldsWrite => true,
        PluginPermissionNames.AccountsRead or PluginPermissionNames.InstancesRead or PluginPermissionNames.ModsRead or
        PluginPermissionNames.DownloadsRead or PluginPermissionNames.SettingsRead or PluginPermissionNames.EnvironmentRead or
        PluginPermissionNames.Lifecycle => PluginApi.Version >= new Version(1, 5),
        PluginPermissionNames.LaunchModify or PluginPermissionNames.DownloadIntercept or PluginPermissionNames.InstanceValidate => PluginApi.Version >= new Version(1, 6),
        PluginPermissionNames.Network or PluginPermissionNames.DownloadProvider or PluginPermissionNames.ModProvider or
        PluginPermissionNames.LoaderProvider or PluginPermissionNames.AuthProvider or PluginPermissionNames.InstanceFilesRead or
        PluginPermissionNames.InstanceFilesWrite or PluginPermissionNames.ProcessInstall => PluginApi.Version >= new Version(1, 7),
        PluginPermissionNames.UiNavigation or PluginPermissionNames.UiInstanceActions or PluginPermissionNames.UiSettings or
        PluginPermissionNames.UiBackground or PluginPermissionNames.Messaging or PluginPermissionNames.ExternalFilesRead => PluginApi.Version >= new Version(1, 8),
        _ => false
    };
}
