namespace XCL2.App.Models;

/// <summary>插件（DLL / EXE）的启动策略。</summary>
public enum PluginLaunchMode
{
    /// <summary>每次启动器启动都运行已启用的插件（默认）。</summary>
    EachLaunch,
    /// <summary>只启动尚未成功启动过的插件（导入后运行一次）。</summary>
    OnceOnImport
}
