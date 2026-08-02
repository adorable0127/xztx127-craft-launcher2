using System.Collections.Generic;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// "功能隐藏"功能的核心逻辑：用户可以在设置页勾选隐藏一批主页面/子页面/特定功能，
/// 隐藏后这些入口不出现在界面上；在任意界面按 F12 可以临时把它们全部显示出来
/// （只影响当前这一次显示，不改配置，松开/切换页面后仍按配置里的隐藏状态走——
/// 这里用一个进程内静态标记 <see cref="TemporaryRevealActive"/> 表示"F12 临时显示"是否生效，
/// MainWindow 收到 F12 按键时切换这个标记并让当前页面重新应用一次可见性）。
///
/// Key 命名规则：`分类.名称`，全部是英文常量字符串，不随界面语言切换而改变，
/// 用来对应 <see cref="AppConfig.HiddenFeatureKeys"/> 里存的内容。
/// </summary>
public static class FeatureVisibilityService
{
    // ===== 主页面 =====
    public const string NavDownload = "Nav.Download";
    public const string NavSettings = "Nav.Settings";
    public const string NavToolbox = "Nav.Toolbox";

    // ===== 子页面：设置 =====
    public const string SettingsLaunch = "Settings.Launch";
    public const string SettingsJava = "Settings.Java";
    public const string SettingsManage = "Settings.Manage";
    public const string SettingsMultiplayer = "Settings.Multiplayer";
    public const string SettingsPersonalize = "Settings.Personalize";
    public const string SettingsLanguage = "Settings.Language";
    public const string SettingsMisc = "Settings.Misc";
    public const string SettingsUpdate = "Settings.Update";
    public const string SettingsAbout = "Settings.About";
    public const string SettingsFeedback = "Settings.Feedback";
    public const string SettingsViewLogs = "Settings.ViewLogs";

    // ===== 子页面：工具 =====
    public const string ToolMultiplayer = "Tool.Multiplayer";
    public const string ToolToolbox = "Tool.Toolbox";

    // ===== 子页面：实例设置 =====
    public const string InstanceEdit = "Instance.Edit";
    public const string InstanceExport = "Instance.Export";
    public const string InstanceSaves = "Instance.Saves";
    public const string InstanceScreenshots = "Instance.Screenshots";
    public const string InstanceMods = "Instance.Mods";
    public const string InstanceResourcePacks = "Instance.ResourcePacks";
    public const string InstanceShaderPacks = "Instance.ShaderPacks";
    public const string InstanceSchematics = "Instance.Schematics";
    public const string InstanceServers = "Instance.Servers";

    // ===== 特定功能 =====
    public const string FeatureInstanceManage = "Feature.InstanceManage";
    public const string FeatureModUpdate = "Feature.ModUpdate";
    public const string FeatureHideToggleItself = "Feature.HideToggleItself";

    /// <summary>分组展示用：设置页"功能隐藏"面板按这个结构渲染勾选框，
    /// 跟 Views/SettingsPage.xaml 里手写的复选框一一对应，改这里同时要改那边的 XAML。</summary>
    public static readonly (string GroupLabel, (string Key, string Label)[] Items)[] Groups =
    {
        ("主页面", new[]
        {
            (NavDownload, "下载"), (NavSettings, "设置"), (NavToolbox, "工具"),
        }),
        ("子页面 设置", new[]
        {
            (SettingsLaunch, "启动"), (SettingsJava, "Java"), (SettingsManage, "管理"),
            (SettingsMultiplayer, "联机"), (SettingsPersonalize, "个性化"), (SettingsLanguage, "语言"),
            (SettingsMisc, "杂项"), (SettingsUpdate, "软件更新"), (SettingsAbout, "关于"),
            (SettingsFeedback, "反馈"), (SettingsViewLogs, "查看日志"),
        }),
        ("子页面 工具", new[]
        {
            (ToolMultiplayer, "联机"), (ToolToolbox, "百宝箱"),
        }),
        ("子页面 实例设置", new[]
        {
            (InstanceEdit, "修改"), (InstanceExport, "导出"), (InstanceSaves, "存档"),
            (InstanceScreenshots, "截图"), (InstanceMods, "Mod"), (InstanceResourcePacks, "资源包"),
            (InstanceShaderPacks, "光影包"), (InstanceSchematics, "投影原理图"), (InstanceServers, "服务器"),
        }),
        ("特定功能", new[]
        {
            (FeatureInstanceManage, "实例管理"), (FeatureModUpdate, "Mod 更新"),
            (FeatureHideToggleItself, "功能隐藏"),
        }),
    };

    /// <summary>F12 临时显示是否生效（进程内状态，不持久化）。</summary>
    public static bool TemporaryRevealActive { get; set; }

    /// <summary>某个功能是否应该显示：没被隐藏，或者被隐藏但 F12 临时显示正生效。</summary>
    public static bool IsVisible(AppConfig cfg, string key)
        => TemporaryRevealActive || !cfg.HiddenFeatureKeys.Contains(key);
}
