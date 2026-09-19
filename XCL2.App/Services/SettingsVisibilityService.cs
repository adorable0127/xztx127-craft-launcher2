using System;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 「隐藏设置项」的元数据与核心判断逻辑。
///
/// 跟 <see cref="FeatureVisibilityService"/>（功能隐藏，F12）刻意分成两套，不合并：
/// - 功能隐藏管的是「导航按钮 / 子页面 / 功能入口」出不出现在界面上，作用范围是整个启动器；
/// - 这里管的是「设置页里某一条设置、或者某一个大类」出不出现在设置页上，作用范围只有设置页。
/// 两者粒度不同、key 空间不同、临时显示的快捷键也不同（F12 vs F10），共用一个集合只会让
/// 「我按哪个键能把东西找回来」变得更难解释，也容易出现 key 撞车。
///
/// Key 命名规则：
/// - 大类：`SetGroup.分类`，例如 "SetGroup.Java"；
/// - 单项：`Set.分类.名称`，例如 "Set.Java.Enforce"。
/// 全部是固定英文常量，不随界面语言变化，对应 <see cref="AppConfig.HiddenSettingKeys"/> 里存的内容。
///
/// 这里只描述「有哪些可隐藏的东西、它们对应设置页上的哪些控件」，真正的隐藏动作
/// （找控件、连带隐藏小标题/说明文字、整段分区一起收起）在 SettingsPage.ApplySettingsItemVisibility
/// 里实现——因为只有那边才拿得到 SettingsRootPanel 这棵可视化树。
/// </summary>
public static class SettingsVisibilityService
{
    /// <summary>
    /// 一个可隐藏的设置项。<see cref="Names"/> 填的是 Views/SettingsPage.xaml 里控件的 x:Name，
    /// 改 XAML 时如果重命名/删除了这些控件，这里要跟着改——找不到的名字会被静默跳过，
    /// 不会抛异常，所以漏改的表现是「这一项勾了没反应」而不是崩溃。
    /// </summary>
    public sealed class SettingItem
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";

        /// <summary>这一项对应的控件名。一项可以对应多个控件（例如"内存分配"同时含最小/最大两个输入框）。</summary>
        public string[] Names { get; init; } = Array.Empty<string>();

        /// <summary>true = 隐藏这一项时，把它所在的整段分区（小标题 + 说明 + 该分区下所有控件）
        /// 一起收起来。用于"一个分区里就这一条设置"的情况（语言、配色、访客模式这类），
        /// 否则只隐藏控件本身会留下一个孤零零的小标题。</summary>
        public bool WholeSection { get; init; }

        /// <summary>true = 不隐藏控件本身，而是隐藏「包着它、且直接挂在 SettingsRootPanel 下」的那一行容器。
        /// 用于滑块+数值文字、宽×高两个输入框这类被一个 StackPanel/Grid 包起来的组合控件——
        /// 只隐藏控件本身会留下旁边的"×""%"等散装文字。</summary>
        public bool HideParentRow { get; init; }
    }

    /// <summary>一个大类：一个标题 + 一批设置项。<see cref="SectionAnchors"/> 里填的控件
    /// 所在的整段分区，会在「整类隐藏」时一并收起（连小标题一起），单项隐藏时不受影响。</summary>
    public sealed class SettingGroup
    {
        public string Key { get; init; } = "";
        public string GroupLabel { get; init; } = "";
        public SettingItem[] Items { get; init; } = Array.Empty<SettingItem>();
        public string[] SectionAnchors { get; init; } = Array.Empty<string>();
    }

    public static readonly SettingGroup[] Groups =
    {
        new SettingGroup
        {
            Key = "SetGroup.General",
            GroupLabel = "基础与模式",
            Items = new[]
            {
                new SettingItem { Key = "Set.General.AdvancedMode", Label = "高手模式", Names = new[] { "AdvancedModeCheck", "AdvancedModeHintText" } },
                new SettingItem { Key = "Set.General.SimplifiedMode", Label = "简洁模式", Names = new[] { "SimplifiedModeCheck" } },
                new SettingItem { Key = "Set.General.Language", Label = "启动器界面语言", Names = new[] { "OpenLanguagePickerBtn" }, WholeSection = true },
                new SettingItem { Key = "Set.General.ReopenWizard", Label = "重新打开新手向导", Names = new[] { "ReopenWizardBtn" } },
                new SettingItem { Key = "Set.General.AutoSave", Label = "设置保存行为", Names = new[] { "SettingsAutoSaveCheck" }, WholeSection = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.WindowTray",
            GroupLabel = "窗口与托盘",
            SectionAnchors = new[] { "CloseActionCombo" },
            Items = new[]
            {
                new SettingItem { Key = "Set.WindowTray.CloseAction", Label = "关闭按钮行为", Names = new[] { "CloseActionCombo" } },
                new SettingItem { Key = "Set.WindowTray.AutoStart", Label = "开机自启动", Names = new[] { "AutoStartOnBootCheck" } },
                new SettingItem { Key = "Set.WindowTray.AutoStartBehavior", Label = "自启后的窗口状态", Names = new[] { "AutoStartLaunchBehaviorCombo" } },
                new SettingItem { Key = "Set.WindowTray.PostLaunch", Label = "启动游戏后的窗口行为", Names = new[] { "PostGameLaunchActionCombo" } },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Java",
            GroupLabel = "Java 运行时",
            SectionAnchors = new[] { "JavaListBox" },
            Items = new[]
            {
                new SettingItem { Key = "Set.Java.SimpleVersion", Label = "Java 版本（普通模式）", Names = new[] { "SimpleJavaPanel" } },
                new SettingItem { Key = "Set.Java.Advanced", Label = "Java 高级选项", Names = new[] { "AdvancedJavaPanel" } },
                new SettingItem { Key = "Set.Java.Enforce", Label = "强制使用匹配的 Java", Names = new[] { "EnforceJavaVersionMatchCheck" } },
                new SettingItem { Key = "Set.Java.Download", Label = "一键下载 Java", Names = new[] { "DownloadJavaBtn" } },
                new SettingItem { Key = "Set.Java.ScanDisk", Label = "全盘扫描 Java", Names = new[] { "ScanDiskForJavaBtn" } },
                new SettingItem { Key = "Set.Java.List", Label = "Java 列表", Names = new[] { "JavaListBox" }, WholeSection = true },
                new SettingItem { Key = "Set.Java.Default", Label = "默认 Java", Names = new[] { "DefaultJavaCombo" } },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Launch",
            GroupLabel = "启动参数",
            Items = new[]
            {
                new SettingItem { Key = "Set.Launch.HighPerfGpu", Label = "使用高性能独立显卡", Names = new[] { "HighPerformanceGpuLaunchCheck" } },
                new SettingItem { Key = "Set.Launch.MinMemory", Label = "最小内存", Names = new[] { "MinMemBox" } },
                new SettingItem { Key = "Set.Launch.MaxMemory", Label = "最大内存", Names = new[] { "MaxMemBox" } },
                new SettingItem { Key = "Set.Launch.WindowSize", Label = "游戏窗口大小", Names = new[] { "WidthBox" }, HideParentRow = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Game",
            GroupLabel = "游戏与实例",
            Items = new[]
            {
                new SettingItem { Key = "Set.Game.Language", Label = "游戏内语言", Names = new[] { "GameLanguageCombo" } },
                new SettingItem { Key = "Set.Game.VersionTypeLabel", Label = "游戏内水印文字", Names = new[] { "GameVersionTypeLabelBox" } },
                new SettingItem { Key = "Set.Game.Console", Label = "游戏日志控制台窗口", Names = new[] { "GameConsoleWindowCheck" } },
                new SettingItem { Key = "Set.Game.InjectionScan", Label = "注入检测", Names = new[] { "InjectionScanCheck" } },
                new SettingItem { Key = "Set.Game.ModIcons", Label = "显示 Mod 图标", Names = new[] { "ShowModIconsCheck" } },
                new SettingItem { Key = "Set.Game.ServerGuide", Label = "联机引导提示", Names = new[] { "ShowServerNetworkGuideCheck" } },
                new SettingItem { Key = "Set.Game.Isolate", Label = "版本隔离", Names = new[] { "IsolateVersionsCheck" } },
                new SettingItem { Key = "Set.Game.IsolateResourcePacks", Label = "资源包隔离", Names = new[] { "IsolateResourcePacksCheck" } },
                new SettingItem { Key = "Set.Game.Drop", Label = "拖入文件安装", Names = new[] { "ModpackDropNewInstanceCheck" }, WholeSection = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Download",
            GroupLabel = "下载",
            SectionAnchors = new[] { "MultiThreadDownloadCheck", "GameVersionNoPopupCheck", "ModFileNamingStyleCombo", "DownloadPopupDetailCheck" },
            Items = new[]
            {
                new SettingItem { Key = "Set.Download.Source", Label = "下载源", Names = new[] { "SourceCombo" } },
                new SettingItem { Key = "Set.Download.MultiThread", Label = "多线程下载", Names = new[] { "MultiThreadDownloadCheck" } },
                new SettingItem { Key = "Set.Download.ThreadCount", Label = "下载线程数", Names = new[] { "ThreadCountPanel" } },
                new SettingItem { Key = "Set.Download.SpeedLimit", Label = "下载限速", Names = new[] { "SpeedLimitBox" }, HideParentRow = true },
                new SettingItem { Key = "Set.Download.SmartThrottle", Label = "智能限速", Names = new[] { "SmartThrottleCheck" } },
                new SettingItem { Key = "Set.Download.CompleteNotify", Label = "下载完成后提示", Names = new[] { "GameVersionNoPopupCheck" }, WholeSection = true },
                new SettingItem { Key = "Set.Download.ModNaming", Label = "社区资源文件命名", Names = new[] { "ModFileNamingStyleCombo" }, WholeSection = true },
                new SettingItem { Key = "Set.Download.PopupDetail", Label = "下载列表详情", Names = new[] { "DownloadPopupDetailCheck" }, WholeSection = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Appearance",
            GroupLabel = "外观与视觉效果",
            SectionAnchors = new[] { "Win11EffectsCheck", "TitleBarFontCombo", "PopupCustomAppearanceCheck", "UiSkinCombo", "AutoThemeLightStartHourCombo" },
            Items = new[]
            {
                new SettingItem { Key = "Set.Appearance.Win11", Label = "Win11 视觉效果", Names = new[] { "Win11EffectsCheck" } },
                new SettingItem { Key = "Set.Appearance.WinUi3", Label = "WinUI 3 设计语言", Names = new[] { "WinUi3DesignCheck" } },
                new SettingItem { Key = "Set.Appearance.Backdrop", Label = "背景材质", Names = new[] { "BackdropMaterialPanel" } },
                new SettingItem { Key = "Set.Appearance.Font", Label = "界面字体", Names = new[] { "AppFontCombo" } },
                new SettingItem { Key = "Set.Appearance.FontLayers", Label = "字体分层设置", Names = new[] { "TitleBarFontCombo" }, WholeSection = true },
                new SettingItem { Key = "Set.Appearance.Brightness", Label = "亮度", Names = new[] { "BrightnessSlider" }, HideParentRow = true },
                new SettingItem { Key = "Set.Appearance.WindowTransparency", Label = "窗口透明开关", Names = new[] { "WindowTransparencyCheck" } },
                new SettingItem { Key = "Set.Appearance.WindowOpacity", Label = "窗口不透明度", Names = new[] { "WindowOpacitySlider" }, HideParentRow = true },
                new SettingItem { Key = "Set.Appearance.Background", Label = "自定义背景图片", Names = new[] { "CustomBackgroundPanel" }, HideParentRow = true },
                new SettingItem { Key = "Set.Appearance.Frost", Label = "背景磨砂程度", Names = new[] { "CustomBackgroundFrostSlider" }, HideParentRow = true },
                new SettingItem { Key = "Set.Appearance.GlobalTransparency", Label = "全局窗口透明度", Names = new[] { "GlobalWindowTransparencyCheck" } },
                new SettingItem { Key = "Set.Appearance.TextOpacity", Label = "文字不透明度", Names = new[] { "TextOpacitySlider" }, HideParentRow = true },
                new SettingItem { Key = "Set.Appearance.PopupDrawer", Label = "弹窗与抽屉外观", Names = new[] { "PopupCustomAppearanceCheck" }, WholeSection = true },
                new SettingItem { Key = "Set.Appearance.Skin", Label = "配色主题", Names = new[] { "UiSkinCombo" }, WholeSection = true },
                new SettingItem { Key = "Set.Appearance.AutoTheme", Label = "深浅色自动循环", Names = new[] { "AutoThemeLightStartHourCombo" }, WholeSection = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Performance",
            GroupLabel = "性能与交互",
            SectionAnchors = new[] { "MouseWheelSensitivitySlider", "UiZoomEnabledCheck" },
            Items = new[]
            {
                new SettingItem { Key = "Set.Perf.LowPerformance", Label = "低性能模式", Names = new[] { "LowPerformanceModeCheck" } },
                new SettingItem { Key = "Set.Perf.HighPerformance", Label = "高性能模式", Names = new[] { "HighPerformanceModeCheck" } },
                new SettingItem { Key = "Set.Perf.AlwaysOnTop", Label = "窗口置顶", Names = new[] { "AlwaysOnTopCheck" } },
                new SettingItem { Key = "Set.Perf.WindowAnimations", Label = "窗口过渡动画", Names = new[] { "WindowAnimationsCheck" } },
                new SettingItem { Key = "Set.Perf.PageAnimations", Label = "页面切换动画", Names = new[] { "PageAnimationsCheck" } },
                new SettingItem { Key = "Set.Perf.MouseWheel", Label = "鼠标滚轮灵敏度", Names = new[] { "MouseWheelSensitivitySlider" }, WholeSection = true },
                new SettingItem { Key = "Set.Perf.UiZoom", Label = "界面缩放快捷键", Names = new[] { "UiZoomEnabledCheck" }, WholeSection = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Backup",
            GroupLabel = "备份",
            SectionAnchors = new[] { "ScheduledBackupCheck" },
            Items = new[]
            {
                new SettingItem { Key = "Set.Backup.Scheduled", Label = "实例定时备份", Names = new[] { "ScheduledBackupCheck" }, WholeSection = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Account",
            GroupLabel = "账户与隐私",
            SectionAnchors = new[] { "GuestModeCheck", "AccountTokenGraceDaysBox", "ReadAgreementsButton" },
            Items = new[]
            {
                new SettingItem { Key = "Set.Account.Guest", Label = "访客模式", Names = new[] { "GuestModeCheck" }, WholeSection = true },
                new SettingItem { Key = "Set.Account.SkinApi", Label = "皮肤站 API 地址", Names = new[] { "SkinApiRootBox" }, HideParentRow = true },
                new SettingItem { Key = "Set.Account.TokenGrace", Label = "正版令牌保留时效", Names = new[] { "AccountTokenGraceDaysBox" }, WholeSection = true },
                new SettingItem { Key = "Set.Account.Agreements", Label = "协议与账户", Names = new[] { "ReadAgreementsButton" }, WholeSection = true },
            }
        },
        new SettingGroup
        {
            Key = "SetGroup.Advanced",
            GroupLabel = "高级与危险操作",
            SectionAnchors = new[] { "AiAssistantSettingsBtn", "ExperimentalFeaturesBtn", "UseMachineWideRegistryCheck", "DisableRegistryBtn", "OpenHiddenItemsButton" },
            Items = new[]
            {
                new SettingItem { Key = "Set.Advanced.Ai", Label = "AI 助手设置入口", Names = new[] { "AiAssistantSettingsBtn" }, WholeSection = true },
                new SettingItem { Key = "Set.Advanced.Experimental", Label = "实验性功能入口", Names = new[] { "ExperimentalFeaturesBtn" }, WholeSection = true },
                new SettingItem { Key = "Set.Advanced.FeatureHide", Label = "隐藏项目入口", Names = new[] { "OpenHiddenItemsButton" }, WholeSection = true },
                new SettingItem { Key = "Set.Advanced.Registry", Label = "注册表存储", Names = new[] { "UseMachineWideRegistryCheck" }, WholeSection = true },
                new SettingItem { Key = "Set.Advanced.Danger", Label = "危险操作", Names = new[] { "DisableRegistryBtn" }, WholeSection = true },
            }
        },
    };

    /// <summary>F10 临时显示是否生效（进程内状态，不持久化，也不写回配置）。</summary>
    public static bool TemporaryRevealActive { get; set; }

    /// <summary>这个 key（大类或单项）当前是不是应该显示出来。</summary>
    public static bool IsVisible(AppConfig cfg, string key)
        => TemporaryRevealActive || !cfg.HiddenSettingKeys.Contains(key);

    /// <summary>当前配置里有没有任何被隐藏的设置项。MainWindow 用它决定这一次 F10
    /// 该不该被"临时显示"抢走——没有隐藏任何东西时不拦截，F10 交回系统/其它处理，
    /// 不给没用这个功能的用户添乱。</summary>
    public static bool HasAnyHidden(AppConfig cfg) => cfg.HiddenSettingKeys.Count > 0;
}
