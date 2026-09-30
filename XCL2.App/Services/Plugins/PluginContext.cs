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
    public Version ApiVersion { get; } = new(1, 3);

    /// <summary>注册弹窗、按钮、选项、面板和弹窗生命周期订阅。</summary>
    public PluginUiService Ui { get; }

    /// <summary>任意已加载视图的组件定位、增加、移除、移动、替换与布局。</summary>
    public PluginComponentService Components { get; }

    /// <summary>启动内核、头像和实时 3D 渲染后端扩展。</summary>
    public PluginRuntimeService Runtime { get; }

    public void Dispose() => Ui.Dispose();

    /// <summary>插件停止时取消，后台任务应传入此令牌。</summary>
    public System.Threading.CancellationToken Stopping => Ui.Stopping;

    public PluginContext(string pluginId, PluginConfigService config, string dataDirectory, Action<string> log)
    {
        Config = config;
        DataDirectory = dataDirectory;
        Log = log;
        PluginId = pluginId;
        Ui = new PluginUiService(this);
        Components = new PluginComponentService(this);
        Runtime = new PluginRuntimeService(this);
    }

    /// <summary>原安装等于 IPlugin.Id，副本含独立实例后缀。使用此 Id 隔离注册项，方便插件代码里不用额外持有 IPlugin 引用
    /// 也能拿到自己的 Id（比如拼日志前缀、拼文件名）。</summary>
    public string PluginId { get; }
}
