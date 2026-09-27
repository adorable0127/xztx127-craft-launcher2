namespace XCL2.App.Services.Plugins;

/// <summary>
/// PluginManager 在调用 IPlugin.Initialize 时传进去的宿主上下文，一个插件一份、互不共享。
/// 插件只应该通过这里提供的成员跟启动器打交道，不要自己去猜启动器内部目录结构/私有服务，
/// 那些随时可能改，这里暴露出来的这几个成员会尽量保持稳定。
/// </summary>
public sealed class PluginContext
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

    public PluginContext(string pluginId, PluginConfigService config, string dataDirectory, Action<string> log)
    {
        Config = config;
        DataDirectory = dataDirectory;
        Log = log;
        PluginId = pluginId;
    }

    /// <summary>就是 IPlugin.Id，重复放一份在这里方便插件代码里不用额外持有 IPlugin 引用
    /// 也能拿到自己的 Id（比如拼日志前缀、拼文件名）。</summary>
    public string PluginId { get; }
}
