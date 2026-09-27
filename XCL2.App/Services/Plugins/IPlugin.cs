using System.Windows.Controls;

namespace XCL2.App.Services.Plugins;

/// <summary>
/// 每个 XCL2 插件的 dll 里必须有且仅有一个实现了这个接口的公共类（无参构造函数），
/// PluginManager 用反射扫描并实例化它——具体“怎么写一个插件”见仓库根目录下的
/// PLUGIN_GUIDE.md，这里只放接口本身，不重复讲用法。
///
/// 生命周期：PluginManager.ScanAndLoad() 根据启动策略对已启用 DLL 调用 Initialize，
/// 主窗口退出或重新扫描时对本轮成功运行的实例调用 Shutdown。
/// "只在导入时启动一次"模式下，已运行过的 DLL 在后续启动中不会实例化。
/// </summary>
public interface IPlugin
{
    /// <summary>插件唯一 ID，建议用"作者名.插件名"这种反向域名风格的短字符串，
    /// 例如 "someone.better-skin-tools"。这个 ID 同时也是配置文件、日志前缀的 key，
    /// 上线之后不要再改，否则等于变成了一个新插件（旧配置对不上）。</summary>
    string Id { get; }

    /// <summary>展示给用户看的插件名称，随时可以改，不影响配置归属。</summary>
    string DisplayName { get; }

    /// <summary>插件版本号，纯展示用，建议跟随语义化版本（1.0.0 这种）。</summary>
    string Version { get; }

    /// <summary>一两句话说明这个插件是干什么的，显示在插件管理列表里。</summary>
    string Description { get; }

    /// <summary>插件作者，展示用，可以留空字符串。</summary>
    string Author { get; }

    /// <summary>
    /// 启动器加载插件时调用一次。ctx 提供了配置读写、日志、以及向"插件"页面
    /// 挂一个自定义设置面板的能力——具体每个成员的用法见 PluginContext 自己的注释。
    /// 这里面不应该做耗时的同步操作（网络请求、大文件 IO 等），耗时工作请自己开
    /// Task.Run，不要卡住启动器主界面的启动过程。
    /// </summary>
    void Initialize(PluginContext ctx);

    /// <summary>
    /// 插件被用户关闭，或者启动器正常退出时调用一次，用来释放插件自己占用的资源
    /// （文件句柄、定时器、后台线程等）。如果插件没有需要清理的东西，留空实现即可。
    /// </summary>
    void Shutdown();

    /// <summary>
    /// 可选：返回一个插件自己的配置界面（一个 UserControl），显示在"插件管理"里
    /// 该插件条目的"配置"按钮之后。不需要图形配置界面的插件（比如只是后台跑点逻辑的）
    /// 可以直接返回 null——这时"配置"按钮会改成显示一个通用的、基于 PluginContext.Config
    /// 里已保存键值对的只读 JSON 预览，方便用户至少能看到当前保存了什么。
    /// </summary>
    UserControl? CreateSettingsPanel(PluginContext ctx) => null;
}

/// <summary>
/// 可选：插件的配置面板（CreateSettingsPanel 返回的那个 UserControl）如果希望在用户点击
/// 宿主弹窗（PluginSettingsHostDialog）的"保存"按钮时统一收口保存逻辑，就让这个 UserControl
/// 顺带实现这个接口。典型用法：面板上的输入控件先只改自己的本地状态/临时字段，不逐个字段
/// 调用 ctx.Config.Set 落盘，等用户点"保存"时，在 Save() 里一次性把当前所有字段值批量写入
/// PluginContext.Config——比"改一个存一个"更适合有校验、有多个关联字段的表单。
///
/// 不实现这个接口也完全没问题：ctx.Config.Set 本身每次调用都会立即同步落盘（见
/// PluginConfigService.Set 的注释），所以"改了就存"风格的面板不需要额外接这个接口，
/// 宿主弹窗的"保存"按钮对它们来说只是一次友好的"已保存"确认。</summary>
public interface IPluginSettingsPanel
{
    /// <summary>把面板当前的输入状态批量写入 PluginContext.Config。抛出异常会被宿主弹窗
    /// 捕获并以警告形式提示用户，弹窗不会关闭，方便用户看到哪里没填对再改。</summary>
    void Save();
}
