namespace XCL2.App.Services.Plugins;

/// <summary>
/// PluginManager 在调用 IPlugin.Initialize 时传进去的宿主上下文，一个插件一份、互不共享。
/// 插件只应该通过这里提供的成员跟启动器打交道，不要自己去猜启动器内部目录结构/私有服务，
/// 那些随时可能改，这里暴露出来的这几个成员会尽量保持稳定。
/// </summary>
public sealed class PluginContext : IDisposable
{
    /// <summary>该插件独享的配置读写入口，落盘在
    /// %AppData%/XCL2/Plugins/config/{PluginId}.json，
    /// 具体用法见 PluginConfigService 自己的注释。</summary>
    public PluginConfigService Config { get; }

    /// <summary>该插件独享的数据目录（%AppData%/XCL2/Plugins/data/{PluginId}/），
    /// 已在 Initialize 调用前确保存在。需要落盘缓存文件、下载文件的插件应该写在这里，
    /// 而不是写到 dll 自己所在目录（那个目录可能只读，或者被启动器整体当作只读区对待）。</summary>
    public string DataDirectory { get; }

    /// <summary>把一行文字写进启动器日志（会自动带上"[Plugin:该插件Id]"前缀），
    /// 排查问题时能直接在启动器"日志"页里看到，不需要插件自己另外弹窗/写文件。</summary>
    public Action<string> Log { get; }

    /// <summary>UI 注册 API 版本；原有 IPlugin 契约保持兼容。</summary>
    public Version ApiVersion => PluginApi.Version;

    public PluginMetadata Metadata { get; private set; }
    private System.Collections.Immutable.ImmutableHashSet<string> _grantedPermissions =
        System.Collections.Immutable.ImmutableHashSet<string>.Empty;
    internal System.Collections.Immutable.ImmutableHashSet<string> GrantedPermissions => System.Threading.Volatile.Read(ref _grantedPermissions);
    internal long AuthorizationRevision;
    internal event Action? AuthorizationChanged;
    internal void BindMetadata(PluginMetadata metadata, IEnumerable<string> granted)
    {
        Metadata = metadata;
        UpdatePermissions(granted);
    }
    internal void UpdatePermissions(IEnumerable<string> granted)
    {
        System.Threading.Volatile.Write(ref _grantedPermissions,
            System.Collections.Immutable.ImmutableHashSet.CreateRange(StringComparer.Ordinal, granted));
        System.Threading.Interlocked.Increment(ref AuthorizationRevision);
        if (Ui != null && !Permissions.Has(PluginPermissionNames.UiBasic)) Ui.Revoke();
        if (Components != null && !Permissions.Has(PluginPermissionNames.UiComponents)) { Components.Revoke(); Runtime.Revoke(); }
        AuthorizationChanged?.Invoke();
    }

    /// <summary>注册弹窗、按钮、选项、面板和弹窗生命周期订阅。</summary>
    public PluginUiService Ui { get; }

    /// <summary>任意已加载视图的组件定位、增加、移除、移动、替换与布局。</summary>
    public PluginComponentService Components { get; }

    /// <summary>启动内核、头像和实时 3D 渲染后端扩展。</summary>
    public PluginRuntimeService Runtime { get; }
    public PluginPermissionService Permissions { get; }
    public PluginLauncherService Launcher { get; }
    /// <summary>页面导航、窗口布局、启动控制与经过校验的设置写入。</summary>
    public PluginControlService Control { get; }
    public PluginEventService Events { get; }
    public PluginProviderService Providers { get; }
    public PluginNetworkService Network { get; }
    public PluginMessageService Messages { get; }
    public PluginPreviewService Preview { get; }
    public PluginWorldService Worlds { get; }
    /// <summary>API 2.2：实例、模组、Java 与下载队列管理。</summary>
    public PluginManagementService Management { get; }
    /// <summary>API 2.2：宿主资源查询、文件安装及原版版本安装。</summary>
    public PluginResourceService Resources { get; }
    public PluginProjectService Projects { get; }
    public PluginTaskService Tasks { get; }
    public PluginChangeService Changes { get; }
    public PluginQueryService Queries { get; }
    private PluginHostBridge? _host;
    private PluginCallbackRunner? _callbacks;
    private readonly object _lifetimeGate = new();
    private readonly List<IDisposable> _registrations = new();
    private int _disposed;
    private int _businessDisabled;
    internal bool IsStopped => System.Threading.Volatile.Read(ref _disposed) != 0 || Stopping.IsCancellationRequested;
    internal bool BusinessDisabled => System.Threading.Volatile.Read(ref _businessDisabled) != 0;
    internal PluginHostBridge Host => _host ?? throw new PluginHostUnavailableException();
    internal TimeSpan CallbackTimeout => _host?.CallbackTimeout ?? TimeSpan.FromSeconds(3);
    internal string Safe(string? text) => _host?.Safe(text) ?? text ?? "";
    internal void BindHost(PluginHostBridge host) { _host = host; }
    internal PluginCallbackRunner Callbacks
    {
        get { lock (_lifetimeGate) { EnsureActive(); return _callbacks ??= new PluginCallbackRunner(this); } }
    }
    internal void EnsureActive() { if (IsStopped) throw new ObjectDisposedException(nameof(PluginContext)); }
    internal void DisableBusiness()
    {
        if (System.Threading.Interlocked.Exchange(ref _businessDisabled, 1) != 0) return;
        System.Threading.Interlocked.Increment(ref AuthorizationRevision);
        Log("CallbackUnresponsive: 已暂停此插件的新业务回调，请在插件管理中重新加载。");
        AuthorizationChanged?.Invoke();
    }
    internal IDisposable Track(IDisposable registration)
    {
        IDisposable? tracked = null;
        tracked = new PluginRegistration(() =>
        {
            lock (_lifetimeGate) _registrations.Remove(tracked!);
            registration.Dispose();
        });
        lock (_lifetimeGate)
        {
            if (IsStopped) { registration.Dispose(); throw new ObjectDisposedException(nameof(PluginContext)); }
            _registrations.Add(tracked);
        }
        return tracked;
    }
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Ui.Dispose(); }
        finally
        {
            Events.Dispose();
            IDisposable[] registrations;
            lock (_lifetimeGate) { registrations = _registrations.ToArray(); _registrations.Clear(); _callbacks?.Dispose(); }
            foreach (var registration in registrations)
                try { registration.Dispose(); } catch (Exception ex) { Log("CleanupFailed: " + ex.GetType().Name); }
            _host = null;
        }
    }

    /// <summary>插件停止时取消，后台任务应传入此令牌。</summary>
    public System.Threading.CancellationToken Stopping => Ui.Stopping;

    public PluginContext(string pluginId, PluginConfigService config, string dataDirectory, Action<string> log)
    {
        Config = config;
        DataDirectory = dataDirectory;
        Log = message => log(PluginHostBridge.Current?.Safe(message) ?? message);
        PluginId = pluginId;
        Metadata = PluginManifestReader.Legacy(pluginId, pluginId, "0.0.0");
        Ui = new PluginUiService(this);
        Components = new PluginComponentService(this);
        Runtime = new PluginRuntimeService(this);
        Permissions = new PluginPermissionService(this);
        Launcher = new PluginLauncherService(this);
        Control = new PluginControlService(this);
        Events = new PluginEventService(this);
        Providers = new PluginProviderService(this);
        Network = new PluginNetworkService(this);
        Messages = new PluginMessageService(this);
        Preview = new PluginPreviewService(this);
        Worlds = new PluginWorldService(this);
        Management = new PluginManagementService(this);
        Resources = new PluginResourceService(this);
        Projects = new PluginProjectService(this);
        Tasks = new PluginTaskService(this);
        Changes = new PluginChangeService(this);
        Queries = new PluginQueryService(this);
    }

    /// <summary>原安装等于 IPlugin.Id，副本含独立实例后缀。使用此 Id 隔离注册项，方便插件代码里不用额外持有 IPlugin 引用
    /// 也能拿到自己的 Id（比如拼日志前缀、拼文件名）。</summary>
    public string PluginId { get; }
}
