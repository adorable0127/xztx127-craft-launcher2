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

    /// <summary>功能隐藏面板里单个可勾选项：一个功能 key 对应界面上显示的中文名。
    /// 必须是带真实属性的类而不是 ValueTuple——WPF 的 {Binding Key}/{Binding Label}
    /// 靠反射按属性名取值，ValueTuple 的元素名（Key/Label）只是编译期元数据，运行时
    /// 实际字段名是 Item1/Item2，绑定会静默失败，导致面板渲染出来是空的（看起来"没做"）。</summary>
    public sealed class FeatureItem
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
    }

    /// <summary>功能隐藏面板里的一个分组：一个分组标题 + 一批 <see cref="FeatureItem"/>。
    /// 同样必须用真实属性的类，理由见 <see cref="FeatureItem"/> 上的注释。</summary>
    public sealed class FeatureGroup
    {
        public string GroupLabel { get; init; } = "";
        public FeatureItem[] Items { get; init; } = System.Array.Empty<FeatureItem>();
    }

    /// <summary>分组展示用：设置页"功能隐藏"面板按这个结构渲染勾选框，
    /// 跟 Views/SettingsPage.xaml 里手写的复选框一一对应，改这里同时要改那边的 XAML。</summary>
    public static readonly FeatureGroup[] Groups =
    {
        new FeatureGroup
        {
            GroupLabel = "主页面",
            Items = new[]
            {
                new FeatureItem { Key = NavDownload, Label = "下载" },
                new FeatureItem { Key = NavSettings, Label = "设置" },
                new FeatureItem { Key = NavToolbox, Label = "工具" },
            }
        },
        new FeatureGroup
        {
            GroupLabel = "子页面 设置",
            Items = new[]
            {
                new FeatureItem { Key = SettingsLaunch, Label = "启动" },
                new FeatureItem { Key = SettingsJava, Label = "Java" },
                new FeatureItem { Key = SettingsManage, Label = "管理" },
                new FeatureItem { Key = SettingsMultiplayer, Label = "联机" },
                new FeatureItem { Key = SettingsPersonalize, Label = "个性化" },
                new FeatureItem { Key = SettingsLanguage, Label = "语言" },
                new FeatureItem { Key = SettingsMisc, Label = "杂项" },
                new FeatureItem { Key = SettingsUpdate, Label = "软件更新" },
                new FeatureItem { Key = SettingsAbout, Label = "关于" },
                new FeatureItem { Key = SettingsFeedback, Label = "反馈" },
                new FeatureItem { Key = SettingsViewLogs, Label = "查看日志" },
            }
        },
        new FeatureGroup
        {
            GroupLabel = "子页面 工具",
            Items = new[]
            {
                new FeatureItem { Key = ToolMultiplayer, Label = "联机" },
                new FeatureItem { Key = ToolToolbox, Label = "百宝箱" },
            }
        },
        new FeatureGroup
        {
            GroupLabel = "子页面 实例设置",
            Items = new[]
            {
                new FeatureItem { Key = InstanceEdit, Label = "修改" },
                new FeatureItem { Key = InstanceExport, Label = "导出" },
                new FeatureItem { Key = InstanceSaves, Label = "存档" },
                new FeatureItem { Key = InstanceScreenshots, Label = "截图" },
                new FeatureItem { Key = InstanceMods, Label = "Mod" },
                new FeatureItem { Key = InstanceResourcePacks, Label = "资源包" },
                new FeatureItem { Key = InstanceShaderPacks, Label = "光影包" },
                new FeatureItem { Key = InstanceSchematics, Label = "投影原理图" },
                new FeatureItem { Key = InstanceServers, Label = "服务器" },
            }
        },
        new FeatureGroup
        {
            GroupLabel = "特定功能",
            Items = new[]
            {
                new FeatureItem { Key = FeatureInstanceManage, Label = "实例管理" },
                new FeatureItem { Key = FeatureModUpdate, Label = "Mod 更新" },
                new FeatureItem { Key = FeatureHideToggleItself, Label = "功能隐藏" },
            }
        },
    };

    /// <summary>F12 临时显示是否生效（进程内状态，不持久化）。</summary>
    public static bool TemporaryRevealActive { get; set; }

    /// <summary>某个功能是否应该显示：没被隐藏，或者被隐藏但 F12 临时显示正生效。</summary>
    public static bool IsVisible(AppConfig cfg, string key)
        => TemporaryRevealActive || !cfg.HiddenFeatureKeys.Contains(key);
}
