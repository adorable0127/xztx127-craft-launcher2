using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>ListBox/ComboBox 展示用的简单包装：把 InstalledJava 转成一行好读的文本，
/// 同时保留对原始记录的引用，方便选中后取回 Id。null Entry 代表"（不指定，使用自动探测）"这一项。</summary>
public class JavaListItem
{
    public InstalledJava? Entry { get; init; }
    public string DisplayText => Entry == null
        ? "（不指定，使用自动探测）"
        : (Entry.MajorVersion is > 0 ? $"{Entry.Name}  [Java {Entry.MajorVersion}]  " : $"{Entry.Name}  [版本未知]  ") + Entry.JavawPath;
}

public partial class SettingsPage : UserControl
{
    private readonly MainWindow _owner;

    /// <summary>设置页"编辑追踪/自动保存气泡"相关状态，见 HookDirtyTracking / OnSettingsEdited 注释。
    /// _suppressDirtyTracking 在构造函数里加载初始值期间为 true，避免"打开设置页把控件填充
    /// 成当前配置值"这个过程本身被误判成一次用户编辑。</summary>
    private bool _suppressDirtyTracking = true;
    private bool _hasUnsavedChanges;
    private DispatcherTimer? _editDebounceTimer;
    private string? _preAutoSaveSnapshotJson;
    private bool _suppressAccentPickerSync;
    // 选择“自定义”本身只表示展开调色抽屉；在用户真正选定/应用颜色前，不应被视为一次设置修改。
    private bool _customThemeSelectionPending;
    private string _lastSavedUiFingerprint = "";
    private bool _uiFingerprintReady;
    // 兜底轮询：ComboBox 的 DropDownClosed/SelectionChanged 事件在部分环境下排查下来
    // 死活不触发（具体原因还没查清楚），导致"改了设置、留在页面上"这条路径完全没有任何
    // 信号能触发保存提示，只有等到用户真的切页/关闭时才会被动地重新计算一次。这里加一个
    // 500ms 的轮询定时器，不依赖任何控件事件，直接定期比较"当前界面值"跟"上次检查时的值"，
    // 有变化就照常走 OnSettingsEdited 的防抖流程——相当于给事件通知上了一道不依赖 WPF
    // 路由事件是否正常工作的保险，哪怕以后查清了事件不触发的根因，这个兜底留着也无害。
    private DispatcherTimer? _dirtyPollTimer;
    private string? _lastPolledFingerprint;
    private bool _settingsOpenBackupCreated;
    private bool _transparencyReadabilityWarningShown;
    // 功能隐藏的编辑副本：用户勾选时只改这里，不提前碰 ConfigService.Config。
    // 这样手动保存模式下不会被其它即时动作的 ConfigService.Save() 顺带持久化。
    private HashSet<string> _pendingHiddenFeatureKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>供 MainWindow.SetMainContent 在切页前查询："当前设置页是否有未保存的改动"。
    /// 只有非自动保存模式下才会变成 true——自动保存模式下每次改动都会立即落盘，
    /// 不存在"未保存"这个状态。</summary>
    public bool HasUnsavedChanges => _hasUnsavedChanges;

    public SettingsPage(MainWindow owner)
    {
        _owner = owner; // 统一先于 InitializeComponent 赋值，避免控件初始化时触发的事件访问到未赋值字段
        InitializeComponent();
        var cfg = _owner.ConfigService.Config;
        _pendingHiddenFeatureKeys = new HashSet<string>(cfg.HiddenFeatureKeys, StringComparer.OrdinalIgnoreCase);

        // 不在设置页构造/Loaded 阶段重复 ApplyForCurrentState。MainWindow 在首帧前已经把全局
        // 主题资源同步到最终配置；这里再刷一次会重建全窗口样式，反而制造“切到设置页时按钮
        // 先浅蓝→深蓝→浅蓝”的可见闪烁。设置页只读取当前配置，不主动重置全局主题。

        // 防抖预览定时器属于本页；切走设置页后必须停止，否则用户刚输入颜色就切页时，
        // 300ms 后旧页面的计时器仍会突然改全局主题，看起来像“切页后按钮自己变色”。
        Unloaded += (_, _) => _accentApplyDebounceTimer?.Stop();
        Loaded += (_, _) =>
        {
            if (_settingsOpenBackupCreated) return;
            _settingsOpenBackupCreated = true;
            try
            {
                var backup = _owner.ConfigService.CreateSettingsOpenBackup();
                if (_owner.ConfigService.RecordSettingsPageOpenAndShouldShowRestoreTip())
                {
                    Dispatcher.BeginInvoke(new Action(() => MessageBoxDialog.ShowInfo(
                        $"你知道吗？如果你修改错误设置，可以在下面的文件夹里找到自动备份的 config，复制一份并替换 config 就可以还原了！\n\n{Path.GetDirectoryName(backup)}",
                        "设置备份提示")), DispatcherPriority.ContextIdle);
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.AppendLine($"[设置备份] 创建失败：{ex.Message}");
            }
        };

        MinMemBox.Text = cfg.MinMemoryMb.ToString();
        MaxMemBox.Text = cfg.MaxMemoryMb.ToString();
        WidthBox.Text = cfg.WindowWidth.ToString();
        HeightBox.Text = cfg.WindowHeight.ToString();
        SourceCombo.SelectedIndex = cfg.Source == DownloadSource.Official ? 1 : 0;
        SelectComboByTag(GameLanguageCombo, cfg.GameLanguage);
        GameVersionTypeLabelBox.Text = cfg.GameVersionTypeLabel;
        PageAnimationsCheck.IsChecked = cfg.EnablePageAnimations;
        LowPerformanceModeCheck.IsChecked = cfg.LowPerformanceMode;
        AlwaysOnTopCheck.IsChecked = cfg.AlwaysOnTop;
        UiZoomEnabledCheck.IsChecked = cfg.EnableUiZoomShortcut;
        UiZoomWheelCheck.IsChecked = cfg.UiZoomShortcutBindings.Contains(UiZoomShortcutMode.CtrlWheel);
        UiZoomArrowCheck.IsChecked = cfg.UiZoomShortcutBindings.Contains(UiZoomShortcutMode.CtrlArrow);
        UiZoomBindingsPanel.IsEnabled = cfg.EnableUiZoomShortcut;
        UiZoomLevelPanel.IsEnabled = cfg.EnableUiZoomShortcut;
        UiZoomSlider.Value = UiZoomService.CurrentPercent;
        UiZoomPercentText.Text = $"{UiZoomService.CurrentPercent}%";
        MouseWheelSensitivitySlider.Value = ScrollWheelBehavior.ClampSensitivityPercent(cfg.MouseWheelSensitivityPercent);
        MouseWheelSensitivityValueText.Text = $"{(int)MouseWheelSensitivitySlider.Value}%";
        ScrollWheelBehavior.SetSensitivityPercent((int)MouseWheelSensitivitySlider.Value);
        ScheduledBackupCheck.IsChecked = cfg.ScheduledInstanceBackupEnabled;
        ScheduledBackupIntervalBox.Text = cfg.ScheduledInstanceBackupIntervalHours.ToString();
        ScheduledBackupRetentionBox.Text = cfg.ScheduledInstanceBackupRetentionCount.ToString();
        BackupOnStartupCheck.IsChecked = cfg.BackupInstanceOnStartup;
        BackupOnCloseCheck.IsChecked = cfg.BackupInstanceOnClose;
        SelectComboByTag(LifecycleBackupTargetModeCombo, cfg.LifecycleBackupTargetMode.ToString());
        if (LifecycleBackupTargetModeCombo.SelectedItem == null) LifecycleBackupTargetModeCombo.SelectedIndex = 0;
        LifecycleBackupVersionIdsBox.Text = string.Join(Environment.NewLine, cfg.LifecycleBackupVersionIds ?? new List<string>());
        UpdateLifecycleBackupTargetsUi();
        InjectionScanCheck.IsChecked = cfg.EnableInjectionScan;
        GameConsoleWindowCheck.IsChecked = cfg.EnableGameConsoleWindow;
        ShowModIconsCheck.IsChecked = cfg.ShowModIcons;
        ShowServerNetworkGuideCheck.IsChecked = cfg.ShowServerNetworkGuideOnStart;
        IsolateVersionsCheck.IsChecked = cfg.IsolateVersionsByDefault;

        // 外观与视觉效果：Win11 新光效 + 窗口透明度，均默认关闭，见 AppConfig 对应字段注释。
        Win11EffectsCheck.IsChecked = cfg.EnableWin11VisualEffects;
        WinUi3DesignCheck.IsChecked = cfg.EnableWinUi3Design;
        SelectComboByTag(AppFontCombo, cfg.AppFontFamily);
        if (AppFontCombo.SelectedItem == null) AppFontCombo.SelectedIndex = 0; // 兜底：旧配置没有这一项时默认选中"跟随系统"

        // 字体分层设置：三个下拉框的候选项是"跟随全局"+本机全部已安装系统字体，运行时才能
        // 知道具体有哪些字体，所以在这里现填 Items（不是写死在 XAML 里），并按当前配置选中。
        PopulateScopedFontCombo(TitleBarFontCombo, cfg.AppFontFamily_TitleBar);
        PopulateScopedFontCombo(SidebarFontCombo, cfg.AppFontFamily_Sidebar);
        PopulateScopedFontCombo(ContentFontCombo, cfg.AppFontFamily_Content);
        BrightnessSlider.Value = Math.Clamp(cfg.BrightnessPercent, 0, 200);
        BrightnessValueText.Text = $"{(int)BrightnessSlider.Value}%";
        SelectComboByTag(BackdropMaterialCombo, cfg.Win11BackdropMaterial);
        if (BackdropMaterialCombo.SelectedItem == null) BackdropMaterialCombo.SelectedIndex = 0; // 兜底：旧配置没有这一项时默认选中"云母 Mica"
        BackdropMaterialPanel.IsEnabled = cfg.EnableWin11VisualEffects;
        WindowTransparencyCheck.IsChecked = cfg.EnableWindowTransparency;
        CustomBackgroundImagePathBox.Text = cfg.CustomBackgroundImagePath ?? "";
        var frostPercent = Math.Clamp(cfg.CustomBackgroundFrostPercent, 25, 100);
        CustomBackgroundFrostSlider.Value = frostPercent;
        CustomBackgroundFrostValueText.Text = $"{frostPercent}%";
        WindowOpacitySlider.Value = cfg.WindowOpacityPercent;
        WindowOpacityValueText.Text = $"{cfg.WindowOpacityPercent}%";
        WindowOpacitySlider.IsEnabled = cfg.EnableWindowTransparency;
        GlobalWindowTransparencyCheck.IsChecked = cfg.EnableGlobalWindowTransparency;
        GlobalWindowOpacitySlider.Value = cfg.GlobalWindowOpacityPercent;
        GlobalWindowOpacityValueText.Text = $"{cfg.GlobalWindowOpacityPercent}%";
        GlobalWindowOpacitySlider.IsEnabled = cfg.EnableGlobalWindowTransparency;
        TextOpacitySlider.Value = Math.Clamp(cfg.TextOpacityPercent, 50, 100);
        TextOpacityValueText.Text = $"{(int)TextOpacitySlider.Value}%";

        // 弹窗/抽屉独立外观：见 AppConfig.Popup*/Drawer* 字段注释，默认都关闭（跟随主界面）。
        PopupCustomAppearanceCheck.IsChecked = cfg.PopupUseCustomAppearance;
        PopupOpacitySlider.Value = Math.Clamp(cfg.PopupOpacityPercent, 20, 100);
        PopupOpacityValueText.Text = $"{(int)PopupOpacitySlider.Value}%";
        PopupFrostSlider.Value = Math.Clamp(cfg.PopupFrostPercent, 0, 100);
        PopupFrostValueText.Text = $"{(int)PopupFrostSlider.Value}%";
        PopupTextOpacitySlider.Value = Math.Clamp(cfg.PopupTextOpacityPercent, 40, 100);
        PopupTextOpacityValueText.Text = $"{(int)PopupTextOpacitySlider.Value}%";
        PopupOpacitySlider.IsEnabled = PopupFrostSlider.IsEnabled = PopupTextOpacitySlider.IsEnabled = cfg.PopupUseCustomAppearance;

        DrawerCustomAppearanceCheck.IsChecked = cfg.DrawerUseCustomAppearance;
        DrawerOpacitySlider.Value = Math.Clamp(cfg.DrawerOpacityPercent, 20, 100);
        DrawerOpacityValueText.Text = $"{(int)DrawerOpacitySlider.Value}%";
        DrawerFrostSlider.Value = Math.Clamp(cfg.DrawerFrostPercent, 0, 100);
        DrawerFrostValueText.Text = $"{(int)DrawerFrostSlider.Value}%";
        DrawerTextOpacitySlider.Value = Math.Clamp(cfg.DrawerTextOpacityPercent, 40, 100);
        DrawerTextOpacityValueText.Text = $"{(int)DrawerTextOpacitySlider.Value}%";
        DrawerOpacitySlider.IsEnabled = DrawerFrostSlider.IsEnabled = DrawerTextOpacitySlider.IsEnabled = cfg.DrawerUseCustomAppearance;

        // 拖拽安装默认值：三个下拉框按 Tag 匹配当前配置值。
        ModpackDropNewInstanceCheck.IsChecked = cfg.ModpackDropCreatesNewInstance;
        SelectComboByTag(ZipDropDefaultCombo, cfg.ZipDropDefault.ToString());
        SelectComboByTag(ServerJarDropCombo, cfg.ServerPageJarDropTarget.ToString());
        SelectComboByTag(DefaultJarDropCombo, cfg.DefaultJarDropTarget.ToString());
        IsolateResourcePacksCheck.IsChecked = cfg.IsolateResourcePacksByDefault;
        // CurseForge 地图下载走内置 Key，不再需要在这里读取/展示用户配置状态（见下方删除说明）。

        MultiThreadDownloadCheck.IsChecked = cfg.EnableMultiThreadDownload;
        ThreadCountBox.Text = cfg.MaxDownloadThreads.ToString();
        ThreadCountPanel.Visibility = cfg.EnableMultiThreadDownload ? Visibility.Visible : Visibility.Collapsed;
        SpeedLimitBox.Text = cfg.DownloadSpeedLimitKBps.ToString();
        SmartThrottleCheck.IsChecked = cfg.SmartBandwidthThrottle;

        // Java 版本下拉框：8~26 全部可选（高手模式用）
        for (int v = 8; v <= 26; v++)
        {
            JavaVersionCombo.Items.Add(new ComboBoxItem { Content = $"Java {v}", Tag = v });
        }
        SelectComboByTag(JavaVersionCombo, cfg.PreferredJavaMajorVersion);
        SelectComboByTag(JavaArchCombo, cfg.PreferredJavaArch);
        SelectComboByTag(JavaInstallModeCombo, cfg.PreferredJavaInstallMode);
        EnforceJavaVersionMatchCheck.IsChecked = cfg.EnforceJavaVersionMatch;

        // 普通模式的简化版本下拉框：只有 8/17/21/25 四个选项，XAML 里已经写死了这四项，
        // 这里只需要按已保存的偏好版本尽量选中对应项；如果偏好版本不在这四个里
        // （比如之前在高手模式下选了别的版本号），就退回默认的 21。
        SelectComboByTag(SimpleJavaVersionCombo, cfg.PreferredJavaMajorVersion);
        if (SimpleJavaVersionCombo.SelectedItem == null ||
            ((SimpleJavaVersionCombo.SelectedItem as ComboBoxItem)?.Tag as string) == null)
        {
            SelectComboByTag(SimpleJavaVersionCombo, "21");
        }

        AdvancedModeCheck.IsChecked = cfg.AdvancedMode; // 与主页的"普通模式/高手模式"开关共享同一个配置项，两边保持同步
        UpdateAdvancedVisibility();

        // 窗口与托盘：下拉框选项顺序跟 XAML 里四个 ComboBoxItem 的声明顺序一一对应
        // （DirectClose=0，MinimizeToTray=1，Minimize=2，AskEachTime=3），初始化时按当前
        // 配置选中对应项；用 _isInitializingCloseTraySettings 标记暂时挡住下面
        // SelectionChanged/Checked 事件在"程序自己赋值触发"时误当成"用户手动改的"再写一次
        // 配置（虽然写同样的值也不会错，但没必要在页面刚打开时就产生一次多余的
        // ConfigService.Save() 磁盘写入）。
        _isInitializingCloseTraySettings = true;
        CloseActionCombo.SelectedIndex = cfg.DefaultCloseAction switch
        {
            CloseButtonAction.MinimizeToTray => 1,
            CloseButtonAction.Minimize => 2,
            CloseButtonAction.AskEachTime => 3,
            _ => 0
        };
        AutoStartOnBootCheck.IsChecked = cfg.AutoStartOnBoot;
        AutoStartLaunchBehaviorCombo.SelectedIndex = cfg.AutoStartBehavior switch
        {
            AutoStartLaunchBehavior.Minimize => 1,
            AutoStartLaunchBehavior.MinimizeToTray => 2,
            _ => 0
        };
        AutoStartLaunchBehaviorCombo.IsEnabled = cfg.AutoStartOnBoot;
        PostGameLaunchActionCombo.SelectedIndex = cfg.PostGameLaunchAction switch
        {
            Models.PostGameLaunchAction.Minimize => 1,
            Models.PostGameLaunchAction.MinimizeToTray => 2,
            Models.PostGameLaunchAction.Close => 3,
            _ => 0
        };
        _isInitializingCloseTraySettings = false;

        GuestModeCheck.IsChecked = cfg.GuestModeEnabled;

        // 配色皮肤下拉框：内容项在这里现填而不是写死在 XAML 里，这样 ThemeService.AllSkins
        // 以后新增皮肤时只需要改 ThemeService 一个地方，不用再回来同步 XAML。
        //
        // 白色/蓝色/黄色/紫色/粉色五套色系均已在 ThemeService 中完整定义并可用，每个色系
        // 都各自有浅色版/深色版，具体显示哪个由下面的 IsDarkModeCheck 决定，这里只选色相。
        UiSkinCombo.Items.Clear();
        foreach (var skin in ThemeService.AllSkins)
        {
            var item = new ComboBoxItem
            {
                Content = ThemeService.GetDisplayName(skin),
                Tag = skin
            };
            UiSkinCombo.Items.Add(item);
        }

        SelectComboByTag(UiSkinCombo, cfg.UiSkin);
        if (UiSkinCombo.SelectedItem == null) UiSkinCombo.SelectedIndex = 0; // 兜底：配置文件里存了非法值时退回第一项(白色)
        _customThemeSelectionPending = false;
        UpdateCustomThemePanelVisibility();

        // Win11 高级特效开启时锁定为"水"主题（见 ThemeService.SkinAquatic 类注释）：
        // 打开设置页时如果配置里已经是开启状态，这里要在控件刚填充完就立即锁一次，
        // 不然要等用户手动点一下开关才会触发 VisualEffectsToggle_Changed。
        ApplyAquaticLockIfNeeded();

        // 自动循环的两个小时下拉框：0~23 全部可选，内容同样在这里现填。
        for (var hour = 0; hour <= 23; hour++)
        {
            AutoThemeLightStartHourCombo.Items.Add(new ComboBoxItem { Content = $"{hour:00}:00", Tag = hour });
            AutoThemeDarkStartHourCombo.Items.Add(new ComboBoxItem { Content = $"{hour:00}:00", Tag = hour });
        }
        SelectComboByTag(AutoThemeLightStartHourCombo, cfg.AutoThemeLightStartHour);
        SelectComboByTag(AutoThemeDarkStartHourCombo, cfg.AutoThemeDarkStartHour);

        SkinApiRootBox.Text = cfg.SkinApiRoot;
        CustomAccentColorBox.Text = cfg.CustomAccentColor ?? (cfg.UiSkin == ThemeService.SkinCustom ? "#4C9AFF" : "");

        AccountTokenGraceDaysBox.Text = cfg.AccountTokenGracePeriodDays.ToString();
        UseMachineWideRegistryCheck.IsChecked = cfg.UseMachineWideRegistry;
        RefreshRegistryStatusText();

        // 功能隐藏：绑定分组数据源，再按已保存的 HiddenFeatureKeys 逐个勾选。
        // 用 Loaded 事件而不是构造函数里直接遍历，是因为此时 ItemsControl 的
        // 容器（每个 CheckBox）还没真正生成，直接找子控件会全部落空。
        FeatureHideList.ItemsSource = FeatureVisibilityService.Groups;
        Loaded += (_, _) => RunWithoutDirtyTracking(() => InitFeatureHideChecks(cfg));

        RefreshJavaList();

        // 需求：启动器在启动时(这里指打开设置页时)自动刷新一次 Java 列表，不需要用户每次都手动点
        // "刷新（自动探测）"按钮才能发现新装的 Java。复用同一份 QuickDetectJavaAsync 逻辑
        // （已经在读取候选时把 AppData 目录也纳入扫描范围，见 JavaService 的改动），
        // 静默合并新探测到的候选，不弹确认框、不因为探测失败而报错打扰用户——
        // 这只是锦上添花的自动填充，失败了大不了跟以前一样，用户还能手动点按钮。
        _ = AutoDetectJavaOnLoadAsync();

        // "是否可以直接保存，无需点击保存设置"：见 AppConfig.SettingsAutoSaveWithoutConfirm 注释。
        SettingsAutoSaveCheck.IsChecked = cfg.SettingsAutoSaveWithoutConfirm;
        DownloadPopupDetailCheck.IsChecked = cfg.DownloadPopupShowDetailStats;
        DownloadPopupSizeModeCombo.SelectedIndex = Math.Clamp(cfg.DownloadPopupSizeDisplayMode, 0, 2);
        HighPerformanceModeCheck.IsChecked = cfg.EnableHighPerformanceMode;

        // 所有加载初始值的代码到这里结束，之后任何控件值变化都应该视为"用户真的动了一下"，
        // 从这里开始挂编辑追踪、并放开 _suppressDirtyTracking。
        LauncherLogService.AppendLine("[设置未保存诊断] 构造函数：即将调用 HookDirtyTracking()。");
        HookDirtyTracking();
        LauncherLogService.AppendLine("[设置未保存诊断] 构造函数：HookDirtyTracking() 已返回，即将放开 _suppressDirtyTracking。");
        _suppressDirtyTracking = false;

        // 等动态 ItemsControl/ComboBox 容器真正生成以后再记录一次“已保存界面快照”。
        // 后续收到任何 Changed 事件时先对比这个快照：值实际没变（例如后台 Java 列表刷新、
        // 控件重新套主题、程序性重选同一个项目）就不会再误弹“设置已修改”。
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastSavedUiFingerprint = BuildSettingsUiFingerprint();
            _lastPolledFingerprint = _lastSavedUiFingerprint;
            _uiFingerprintReady = true;
            _hasUnsavedChanges = false;
        }), DispatcherPriority.ContextIdle);

        // 见字段注释：不依赖任何控件事件的兜底轮询。只在页面还挂在可视化树上时跑，
        // Unloaded 时停掉，避免页面被替换/回收之后定时器还在后台空转。
        _dirtyPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _dirtyPollTimer.Tick += (_, _) =>
        {
            if (_suppressDirtyTracking || !_uiFingerprintReady) return;
            string current;
            try { current = BuildSettingsUiFingerprint(); }
            catch (Exception ex)
            {
                LauncherLogService.AppendLine($"[设置未保存诊断] 轮询计算指纹时抛出异常：{ex}");
                return;
            }
            // 跟"上一次轮询检查时"的值比，不是跟"已保存"的值比：已经进入 dirty 状态、
            // 提示卡片已经弹出来之后，只要用户没有再继续动，就不用每 500ms 重新弹一次
            // 同一张卡片（ShowActionPrompt 本身按 key 去重，但重复调用还是会重新播放一次
            // 淡入动画，闪一下很难看）。真的又有新变化时这两个值才会不一样。
            if (current == _lastPolledFingerprint) return;
            _lastPolledFingerprint = current;
            LauncherLogService.AppendLine("[设置未保存诊断] 轮询发现界面值跟上一次检查时不一样，走 OnSettingsEdited。");
            OnSettingsEdited();
        };
        _dirtyPollTimer.Start();
        Unloaded += (_, _) => _dirtyPollTimer?.Stop();
    }

    /// <summary>
    /// "编辑追踪"：不逐个给上百个控件的各自 Changed 事件手动加代码，而是在 UserControl 根节点
    /// 上用 AddHandler 监听几种常见控件都会向上冒泡的路由事件（文本框 TextChanged、下拉框
    /// SelectionChanged、复选框 Checked/Unchecked、滑块 ValueChanged），一次性覆盖本页几乎
    /// 所有输入控件，新增设置项时不需要再回来给这里加一行。
    /// 触发后不是每次都立即处理：见 OnSettingsEdited 的防抖说明。
    /// </summary>
    private void HookDirtyTracking()
    {
        // 诊断日志：不再猜测"事件触不触发"，直接打印这个方法本身有没有执行、
        // FindVisualChildren<ComboBox> 到底找到了哪些下拉框、有没有在中途因为异常提前退出。
        // 如果日志里连这行"开始"都没有，说明 HookDirtyTracking() 根本没被调用到；
        // 如果"开始"出现了但列表里没有 CloseActionCombo，说明 FindVisualChildren 没找到它；
        // 如果 try 块里抛了异常，会打印出具体异常信息，而不是像之前那样可能被悄悄吞掉。
        LauncherLogService.AppendLine("[设置未保存诊断] HookDirtyTracking 开始执行。");
        try
        {
            var comboNames = new System.Collections.Generic.List<string>();
            foreach (var c in FindVisualChildren<ComboBox>(this))
                comboNames.Add(string.IsNullOrEmpty(c.Name) ? "(无名字)" : c.Name);
            LauncherLogService.AppendLine($"[设置未保存诊断] FindVisualChildren<ComboBox> 共找到 {comboNames.Count} 个：{string.Join(", ", comboNames)}");
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine($"[设置未保存诊断] 枚举 ComboBox 时抛出异常：{ex}");
        }

        // ComboBox 展开时用户还处在“浏览候选项/尚未确认”的阶段。SelectionChanged 在某些模板、
        // 键盘导航和主题重套过程中会在下拉尚未关闭时触发；如果此时立刻进入自动保存，就会
        // 弹出“已自动保存/回退”气泡打断正在进行的选择。统一等 DropDownClosed 后再比对指纹，
        // 没真的换选项时指纹相同，自然什么都不会发生。
        try
        {
            foreach (var combo in FindVisualChildren<ComboBox>(this))
            {
                var comboRef = combo;
                combo.DropDownClosed += (_, _) =>
                {
                    LauncherLogService.AppendLine($"[设置未保存诊断] ComboBox '{comboRef.Name}' 触发 DropDownClosed，suppressDirtyTracking={_suppressDirtyTracking}");
                    if (!_suppressDirtyTracking) OnSettingsEdited();
                };

                // 补充兜底：DropDownClosed 在自定义 ComboBox 模板下是否稳定触发，
                // 排查下来并不可靠（哪怕模板里 Popup 命名正确）。SelectionChanged 是
                // Selector 的核心事件，只要 SelectedItem/SelectedIndex 真的变了就一定
                // 会触发，不依赖任何模板部件命名，用它做主要检测手段更稳妥。
                // 即使下拉还开着、用户正用键盘上下浏览也没关系——防抖 + 后面的指纹比较
                // 本来就是按"400ms 内没有再变化"才最终判定，不会因为提前触发就误报。
                combo.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) =>
                {
                    LauncherLogService.AppendLine($"[设置未保存诊断] ComboBox '{comboRef.Name}' 触发 SelectionChanged(AddHandler,handledEventsToo)，suppressDirtyTracking={_suppressDirtyTracking}，当前选中={comboRef.SelectedItem}");
                    if (!_suppressDirtyTracking) OnSettingsEdited();
                }), handledEventsToo: true);
            }
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine($"[设置未保存诊断] 挂 ComboBox 事件时抛出异常：{ex}");
        }

        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
        {
            // 搜索框只是页面内的筛选/定位工具，不是设置项。用户在这里输入关键字时绝不能
            // 触发“设置已修改”、自动保存或离开页面时的未保存确认。
            if (ReferenceEquals(e.OriginalSource, SettingsSearchBox)) return;

            // 只把用户正在编辑的文本框算作“修改”。后台 Java 刷新、Loaded 初始化、
            // 导入操作给只读框回填路径等程序赋值，不应该凭空弹“设置已修改”。
            if (e.OriginalSource is TextBox tb && !tb.IsKeyboardFocusWithin) return;
            OnSettingsEdited();
        }));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ComboBox combo)
            {
                // 下拉还开着时先不保存，等 DropDownClosed 再统一判断。
                if (combo.IsDropDownOpen) return;
                if (!combo.IsKeyboardFocusWithin) return;

                // “自定义”仅展开调色区域，真正选颜色之前不是已提交的主题选择。
                if (ReferenceEquals(combo, UiSkinCombo) && _customThemeSelectionPending) return;
            }
            if (e.OriginalSource is ListBox list && !list.IsKeyboardFocusWithin) return;
            OnSettingsEdited();
        }));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not System.Windows.Controls.CheckBox and not System.Windows.Controls.RadioButton) return;
            if (e.OriginalSource is Control c && !c.IsKeyboardFocusWithin && !c.IsMouseOver) return;
            OnSettingsEdited();
        }));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not System.Windows.Controls.CheckBox and not System.Windows.Controls.RadioButton) return;
            if (e.OriginalSource is Control c && !c.IsKeyboardFocusWithin && !c.IsMouseOver) return;
            OnSettingsEdited();
        }));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.Primitives.ScrollBar) return;
            if (e.OriginalSource is Slider slider)
            {
                // RGB 三根滑块只是“取色器内部的临时值”，用户点“使用 RGB 颜色”前不算设置变更。
                if (ReferenceEquals(slider, AccentRSlider) || ReferenceEquals(slider, AccentGSlider) || ReferenceEquals(slider, AccentBSlider))
                    return;
                if (!slider.IsKeyboardFocusWithin && !slider.IsMouseCaptureWithin) return;
            }
            OnSettingsEdited();
        }));
    }

    /// <summary>
    /// 编辑追踪的统一入口：任何被 HookDirtyTracking 监听到的控件变化都会走到这里。
    /// 用一个 400ms 的防抖计时器合并短时间内的连续触发（比如拖动透明度滑块一次拖动会
    /// 连续触发几十次 ValueChanged），避免拖一次滑块就自动保存/弹气泡几十次。
    /// 防抖到点后才真正判断"自动保存"还是"仅提示"两条分支，见 AppConfig.SettingsAutoSaveWithoutConfirm。
    /// </summary>
    private void OnSettingsEdited()
    {
        if (_suppressDirtyTracking) return;
        LauncherLogService.AppendLine("[设置未保存诊断] OnSettingsEdited：重启 400ms 防抖计时器。");

        _editDebounceTimer?.Stop();
        _editDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _editDebounceTimer.Tick += (_, _) =>
        {
            _editDebounceTimer!.Stop();
            HandleDebouncedEdit();
        };
        _editDebounceTimer.Start();
    }

    private void MarkCurrentUiAsSaved()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastSavedUiFingerprint = BuildSettingsUiFingerprint();
            _uiFingerprintReady = true;
            _hasUnsavedChanges = false;
        }), DispatcherPriority.ContextIdle);
    }

    private void HandleDebouncedEdit() => ProcessCurrentUiEdit(showManualPrompt: true, showAutoSavePrompt: true);

    /// <summary>
    /// 真正执行一次“界面值 vs 最近保存值”的比较。两个 show*Prompt 参数分别控制手动保存卡和
    /// 自动保存后的“回退”卡；切页/关闭前可以只同步 dirty 状态而不制造重复提示。
    /// 这时必须先把 400ms 防抖里尚未处理的最后一次改动同步算进去，否则用户刚改完一个抽屉选项
    /// 就立刻点叉号，会因为计时器还没到点而被误判成“没有未保存设置”。
    /// </summary>
    private void ProcessCurrentUiEdit(bool showManualPrompt, bool showAutoSavePrompt)
    {
        // 诊断日志：目前有反馈说"改了设置抽屉里的控件，左下角始终不弹保存/回退提示"，
        // 但从代码走查看不出哪一步在正常安装环境下会失败。先把这条链路每一步的判断结果
        // 落到启动器日志里，下次复现问题时直接看日志就能知道是卡在了哪一步
        // （没进这个方法 / 指纹判断成"没变" / 判断成"变了"但 Toast 没弹出来），
        // 而不用继续凭代码推测。定位到具体问题后应移除或降级这些日志。
        LauncherLogService.AppendLine($"[设置未保存诊断] ProcessCurrentUiEdit 被调用，showManualPrompt={showManualPrompt} showAutoSavePrompt={showAutoSavePrompt} uiFingerprintReady={_uiFingerprintReady}");

        // 路由事件会被 WPF 的模板重建、后台刷新等程序动作触发。只有“可保存控件的实际值”
        // 跟上一次保存后的快照不同，才算真正修改；这样在什么都没改时不会莫名弹保存提示。
        if (!_uiFingerprintReady)
        {
            _lastSavedUiFingerprint = BuildSettingsUiFingerprint();
            _uiFingerprintReady = true;
            LauncherLogService.AppendLine("[设置未保存诊断] 指纹尚未就绪，本次只记录基线，不判断改动。");
            return;
        }

        var currentFingerprint = BuildSettingsUiFingerprint();
        if (string.Equals(currentFingerprint, _lastSavedUiFingerprint, StringComparison.Ordinal))
        {
            _hasUnsavedChanges = false;
            LauncherLogService.AppendLine("[设置未保存诊断] 界面指纹跟上次保存时一致，判定为没有真实改动，不弹提示。");
            return;
        }
        LauncherLogService.AppendLine("[设置未保存诊断] 界面指纹跟上次保存时不一致，判定为真实改动，准备弹提示。");

        var cfg = _owner.ConfigService.Config;

        if (cfg.SettingsAutoSaveWithoutConfirm)
        {
            // 自动保存分支：先把保存前的完整配置快照下来（供"回退"按钮用），
            // 再走跟点击"保存设置"按钮完全相同的落盘逻辑。正常编辑时显示“回退”操作卡；
            // 关闭/切页前的同步 flush 不再额外闪一张卡片，避免模态确认与操作卡叠在一起。
            _preAutoSaveSnapshotJson = System.Text.Json.JsonSerializer.Serialize(cfg);
            PerformSave();
            _hasUnsavedChanges = false;

            LauncherLogService.AppendLine($"[设置未保存诊断] 自动保存分支：PerformSave 已执行，showAutoSavePrompt={showAutoSavePrompt}");
            if (showAutoSavePrompt)
            {
                ToastService.ShowActionPrompt(
                    "设置已自动保存", "回退", RollbackAutoSave,
                    hint: "点击回退可撤销最近一次自动保存",
                    key: "settings-autosave");
            }
        }
        else
        {
            _hasUnsavedChanges = true;

            // 手动保存模式下，设置一有真实变化就保留操作卡，直到用户明确保存/撤销。
            // 即将关闭/切页时由调用方弹模态三选一，所以这里可以只更新 dirty 状态而不重复弹卡片。
            LauncherLogService.AppendLine($"[设置未保存诊断] 手动保存分支：showManualPrompt={showManualPrompt}，即将调用 ToastService.ShowActionPrompt。");
            if (showManualPrompt)
            {
                ToastService.ShowActionPrompt(
                    "设置已修改，是否保存？", "保存", () => PerformSave(),
                    "撤销", DiscardChanges,
                    key: "settings-dirty");
            }
        }
    }

    /// <summary>
    /// 供 MainWindow 在切页/真正关闭窗口之前调用。立即结算还卡在 400ms 防抖中的最后一次编辑：
    /// 自动保存模式会先完成保存；手动保存模式只把 HasUnsavedChanges 更新为准确值，
    /// 再由 MainWindow 的三选一确认决定保存、放弃还是取消。
    /// </summary>
    public void FlushPendingEditsForLeave(bool preserveAutoSaveRollbackPrompt = false)
    {
        if (_suppressDirtyTracking) return;
        _editDebounceTimer?.Stop();
        // 离页时如果自动保存已开启，仍保留“回退”卡片供用户在其它页面撤销；
        // 真正关闭窗口时则没有显示它的意义，调用方传 false 即可。手动保存分支不在这里
        // 额外弹操作卡，因为 MainWindow 紧接着会给出保存/放弃/取消的模态选择。
        ProcessCurrentUiEdit(showManualPrompt: false, showAutoSavePrompt: preserveAutoSaveRollbackPrompt);
    }

    /// <summary>用户在“未保存设置”确认框里选择取消关闭/重新编辑后，重新保证操作卡可见。
    /// 这主要覆盖“修改后不足 400ms 就点关闭/导航”的边界情况：同步 Flush 已经识别出 dirty，
    /// 但为了避免跟模态框重叠，Flush 本身没有显示手动保存卡；用户决定留下后再补回来。</summary>
    public void ShowPendingEditPromptIfNeeded()
    {
        if (_suppressDirtyTracking) return;
        ProcessCurrentUiEdit(showManualPrompt: true, showAutoSavePrompt: true);
    }

    /// <summary>把设置页中真正可编辑、会参与保存的常用控件压成稳定字符串，用来判断“值到底有没有变”。</summary>
    private string BuildSettingsUiFingerprint()
    {
        var parts = new List<string>();

        string Key(FrameworkElement element, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(element.Name)) return element.Name;
            if (element.Tag is string tag && !string.IsNullOrWhiteSpace(tag)) return fallback + ":" + tag;
            if (element is ContentControl cc && cc.Content is string content && !string.IsNullOrWhiteSpace(content))
                return fallback + ":" + content;
            return fallback;
        }

        foreach (var tb in FindVisualChildren<TextBox>(this))
        {
            // 搜索框不属于配置内容；否则只输入搜索关键字也会改变指纹，被误判成设置修改。
            if (tb.IsReadOnly || ReferenceEquals(tb, SettingsSearchBox)) continue;
            parts.Add($"T|{Key(tb, "TextBox")}|{tb.Text}");
        }
        foreach (var cb in FindVisualChildren<CheckBox>(this))
            parts.Add($"C|{Key(cb, "CheckBox")}|{cb.IsChecked}");
        foreach (var rb in FindVisualChildren<RadioButton>(this))
            parts.Add($"R|{Key(rb, "RadioButton")}|{rb.IsChecked}");
        foreach (var combo in FindVisualChildren<ComboBox>(this))
        {
            string value;
            if (ReferenceEquals(combo, UiSkinCombo) && _customThemeSelectionPending)
            {
                // 只展开“自定义”调色抽屉时，指纹仍按当前已保存主题计算。
                value = _owner.ConfigService.Config.UiSkin;
            }
            else
            {
                value = combo.SelectedItem switch
                {
                    JavaListItem java => java.Entry?.Id ?? "",
                    ComboBoxItem item => item.Tag?.ToString() ?? item.Content?.ToString() ?? "",
                    _ => combo.SelectedValue?.ToString() ?? combo.SelectedItem?.ToString() ?? ""
                };
            }
            parts.Add($"S|{Key(combo, "ComboBox")}|{value}");
        }
        foreach (var slider in FindVisualChildren<Slider>(this))
        {
            if (ReferenceEquals(slider, AccentRSlider) || ReferenceEquals(slider, AccentGSlider) || ReferenceEquals(slider, AccentBSlider))
                continue;
            parts.Add($"V|{Key(slider, "Slider")}|{Math.Round(slider.Value, 3)}");
        }

        // 自定义窗口背景现在由“导入/清除”按钮立即应用并立即持久化，
        // 因此只读路径框不属于批量保存内容，也不参与未保存设置指纹。

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    /// <summary>"回退"按钮：把 HandleDebouncedEdit 里保存的那份"自动保存前"配置快照
    /// 整份写回 Config（走跟 ConfigService.PatchDefaults 同一套反射赋值套路，逐个可写属性
    /// 复制），持久化后刷新设置页（丢弃当前实例，重新 new 一个显示最新配置），
    /// 保证界面上的控件立即跟着回退结果同步，不会停留在回退前的值上。</summary>
    private void RollbackAutoSave()
    {
        if (_preAutoSaveSnapshotJson == null) return;
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(_preAutoSaveSnapshotJson);
        if (snapshot == null) return;

        _owner.ConfigService.ReplaceConfigFieldsFrom(snapshot);
        _owner.ConfigService.Save();

        // 回退不能只改 config.json：自动保存时已经同步过的系统/运行时副作用也必须一起恢复，
        // 否则会出现“文件回退了，但开机启动/缩放/功能隐藏/访客模式仍保持新状态”的半回退。
        var cfg = _owner.ConfigService.Config;
        AutoStartService.Apply(cfg.AutoStartOnBoot);
        ApplyAllVisualEffectsFromConfig();
        _owner.ApplyDownloadPopupDetailMode();
        FrameRateMonitorService.SetEnabled(cfg.EnableHighPerformanceMode);
        _owner.RefreshGuestModeState();
        _owner.ReevaluateAutoThemeCycle();
        _owner.RefreshSidebar();
        _owner.ApplyFeatureVisibility();

        _preAutoSaveSnapshotJson = null;
        _owner.NavigateToSettings();
        ToastService.ShowInfo("已回退到上一次自动保存之前的设置。");
    }

    /// <summary>"撤销"按钮（非自动保存分支）：这次编辑还没落盘，cfg 里的字段仍然是改动前的值，
    /// 只需要把设置页整页重新打开一次，界面控件就会重新从 cfg 读到没被改动过的旧值，
    /// 不需要额外维护一份"逐控件原始值"的映射。</summary>
    private void DiscardChanges()
    {
        _hasUnsavedChanges = false;
        ToastService.DismissActionPrompt("settings-dirty");
        ApplyAllVisualEffectsFromConfig();
        _owner.NavigateToSettings();
    }

    /// <summary>供 MainWindow 在"切换页面时有未保存改动"的三选一确认里选了"放弃"时调用：
    /// 只清掉未保存标记，不像 DiscardChanges 那样重新导航回设置页——调用方接下来
    /// 就会把主内容区切换成用户真正想去的那个页面，这里没必要多跳一次设置页。</summary>
    public void DiscardUnsavedChangesWithoutNavigating()
    {
        _hasUnsavedChanges = false;
        ToastService.DismissActionPrompt("settings-dirty");
        // 设置页支持若干“只预览、不落盘”的视觉项；选择放弃后切走页面时要恢复已保存状态。
        ApplyAllVisualEffectsFromConfig();
    }

    /// <summary>回退自动保存之后，跟 Save_Click 结尾同样需要重新应用一遍视觉相关的效果
    /// （配色/透明度/Win11 特效），避免"配置文件已经回退了，但当前已打开窗口的画面
    /// 还停留在回退前的样子"这种不同步。</summary>
    private void ApplyAllVisualEffectsFromConfig()
    {
        var cfg = _owner.ConfigService.Config;
        ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, cfg.UiSkin, cfg.IsDarkMode, cfg.CustomAccentColor);
        ThemeService.ApplyWindowTransparency(cfg.EnableWindowTransparency, cfg.WindowOpacityPercent);
        ThemeService.ApplyGlobalWindowTransparency(cfg.EnableGlobalWindowTransparency, cfg.GlobalWindowOpacityPercent);
        ThemeService.ApplyTextOpacity(cfg.TextOpacityPercent);
        _owner.Topmost = cfg.AlwaysOnTop;
        if (!string.IsNullOrWhiteSpace(cfg.CustomBackgroundImagePath) && File.Exists(cfg.CustomBackgroundImagePath))
            ApplyBackgroundImage(cfg.CustomBackgroundImagePath);
        else
            _owner.SetCustomBackgroundImage(null);

        var material = Enum.TryParse<Win11EffectsService.BackdropMaterial>(cfg.Win11BackdropMaterial, out var m)
            ? m : Win11EffectsService.BackdropMaterial.Mica;
        Win11EffectsService.SetEnabled(cfg.EnableWin11VisualEffects, material);
        Win11EffectsService.SetWinUi3Enabled(cfg.EnableWinUi3Design);
        ThemeService.ApplyFontFamily(cfg.AppFontFamily, cfg.EnableWinUi3Design);
        FontService.ApplyScopedFonts(_owner, cfg);
        ThemeService.ApplyBrightness(cfg.BrightnessPercent);
        ScrollWheelBehavior.SetSensitivityPercent(cfg.MouseWheelSensitivityPercent);
        UiZoomService.PreviewPercent(cfg.UiZoomPercent);
    }

    /// <summary>
    /// 静默的启动时自动探测：跟 RefreshDetectJava_Click 用的是同一个 QuickDetectJavaAsync，
    /// 唯一区别是这里不改按钮文字、不强制要求用户点击，页面一打开就在后台跑一次。
    /// 找到新 Java 会自动登记进列表并刷新界面；探测失败/没有新发现都完全静默，不弹窗。
    /// </summary>
    private async Task AutoDetectJavaOnLoadAsync()
    {
        try
        {
            var javaService = new JavaService();
            var candidates = await javaService.QuickDetectJavaAsync();

            var cfg = _owner.ConfigService.Config;
            var existingPaths = new HashSet<string>(
                cfg.InstalledJavas.Select(j => j.JavawPath), StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var candidate in candidates)
            {
                if (existingPaths.Contains(candidate.JavawPath)) continue;

                int? major = candidate.Version != null
                    ? JavaService.ParseJavaMajorVersion($"\"{candidate.Version}\"")
                    : null;
                _owner.ConfigService.RegisterJava(candidate.JavawPath, major, "Detected");
                existingPaths.Add(candidate.JavawPath);
                added++;
            }

            if (added > 0)
            {
                _owner.ConfigService.Save();
                RunWithoutDirtyTracking(RefreshJavaList);
                StatusText.Text = $"已自动探测到 {added} 个新 Java 并加入列表。";
            }
        }
        catch { /* 静默失败：这是打开设置页时的自动锦上添花操作，不应该弹窗打扰用户 */ }
    }

    /// <summary>供 MainWindow.ScanJavaInBackgroundAsync 在启动时静默扫描完成后调用：
    /// 如果用户当前正好停留在「设置」页，让新登记的 Java 立刻反映到列表框里，
    /// 不需要用户手动切出去再切回来才能看到。RefreshJavaList 本身保持 private，
    /// 只加这一层公开转发，避免把内部刷新细节暴露给外部随意调用。</summary>
    public void RefreshJavaListPublic() => RunWithoutDirtyTracking(RefreshJavaList);

    /// <summary>程序内部刷新控件时临时关闭“设置已修改”追踪。
    /// 自动 Java 探测、功能隐藏列表初始化等都会触发 SelectionChanged/Checked，
    /// 这些不是用户手动改设置，不能因此弹出“设置已修改”。</summary>
    private void RunWithoutDirtyTracking(Action action)
    {
        var previous = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try { action(); }
        finally { _suppressDirtyTracking = previous; }
    }

    /// <summary>重新从 cfg.InstalledJavas 刷新列表框 + 全局默认下拉框的内容，并尽量保留原来选中的那一项。
    /// 按 Priority 升序展示（数值越小越靠前=优先级越高），跟 FindJava 自动匹配实际尝试的顺序一致——
    /// 之前这里直接按 InstalledJavas 原始存储顺序(等于添加顺序)展示，跟"优先级"这个概念没有关联，
    /// 用户上移/下移调整过后列表看起来却好像没变化(因为展示顺序压根不看 Priority)。</summary>
    private void RefreshJavaList()
    {
        var cfg = _owner.ConfigService.Config;
        var ordered = _owner.ConfigService.GetJavaListInPriorityOrder();

        var previouslySelectedId = (JavaListBox.SelectedItem as JavaListItem)?.Entry?.Id;

        JavaListBox.Items.Clear();
        foreach (var j in ordered)
            JavaListBox.Items.Add(new JavaListItem { Entry = j });
        if (previouslySelectedId != null)
        {
            var restore = JavaListBox.Items.Cast<JavaListItem>().FirstOrDefault(i => i.Entry?.Id == previouslySelectedId);
            if (restore != null) JavaListBox.SelectedItem = restore;
        }

        DefaultJavaCombo.Items.Clear();
        DefaultJavaCombo.Items.Add(new JavaListItem { Entry = null }); // "不指定"
        foreach (var j in ordered)
            DefaultJavaCombo.Items.Add(new JavaListItem { Entry = j });
        DefaultJavaCombo.SelectedItem = DefaultJavaCombo.Items.Cast<JavaListItem>()
            .FirstOrDefault(i => i.Entry?.Id == cfg.SelectedJavaId) ?? DefaultJavaCombo.Items[0];
    }

    /// <summary>"↑ 提高优先级"：跟上一条交换 Priority，已经是第一条时点击无效果（找不到可交换的上一项）。
    /// 交换后立即保存配置(跟其它 Java 列表操作一致，不需要等用户点"保存设置")——排序是纯粹的
    /// 组织性调整，不像内存大小/JVM参数那样需要"预览效果、确认后再生效"的缓冲。</summary>
    private void MoveJavaUp_Click(object sender, RoutedEventArgs e)
    {
        if (JavaListBox.SelectedItem is not JavaListItem { Entry: { } entry }) return;
        _owner.ConfigService.MoveJavaPriority(entry.Id, moveUp: true);
        _owner.ConfigService.Save();
        RefreshJavaList();
    }

    /// <summary>"↓ 降低优先级"：跟下一条交换 Priority，已经是最后一条时点击无效果。</summary>
    private void MoveJavaDown_Click(object sender, RoutedEventArgs e)
    {
        if (JavaListBox.SelectedItem is not JavaListItem { Entry: { } entry }) return;
        _owner.ConfigService.MoveJavaPriority(entry.Id, moveUp: false);
        _owner.ConfigService.Save();
        RefreshJavaList();
    }

    private static void SelectComboByTag(ComboBox combo, object tagValue)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && Equals(item.Tag?.ToString(), tagValue?.ToString()))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    /// <summary>
    /// 字体分层设置三个下拉框（标题栏/侧边栏/内容区）共用的填充逻辑：第一项固定是
    /// "跟随全局"（Tag=null，对应 AppConfig 里对应字段留空=不覆盖），后面依次是
    /// FontService.GetInstalledFontFamilyNames() 返回的本机全部已安装字体，
    /// 每一项的 Tag 就是字体名字符串本身（保存时直接读 Tag 写回配置，不用额外映射表）。
    /// </summary>
    private void PopulateScopedFontCombo(ComboBox combo, string? currentValue)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "跟随全局", Tag = null });
        foreach (var fontName in FontService.GetInstalledFontFamilyNames())
        {
            combo.Items.Add(new ComboBoxItem { Content = fontName, Tag = fontName });
        }

        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            SelectComboByTag(combo, currentValue!);
        }
        if (combo.SelectedItem == null) combo.SelectedIndex = 0;
    }

    /// <summary>
    /// 按已保存的 HiddenFeatureKeys 把功能隐藏面板里对应的 CheckBox 勾上。
    /// 用递归找可视化树而不是给每个 CheckBox 手动 x:Name，是因为这批 CheckBox
    /// 是 ItemsControl 嵌套 ItemsControl 动态生成的，没法在 XAML 里逐个命名。
    /// </summary>
    private void InitFeatureHideChecks(AppConfig cfg)
    {
        foreach (var checkBox in FindVisualChildren<CheckBox>(FeatureHideList))
        {
            if (checkBox.Tag is string key)
                checkBox.IsChecked = cfg.HiddenFeatureKeys.Contains(key);
        }
    }

    /// <summary>功能隐藏面板的勾选只先留在 UI 中；真正写回 HiddenFeatureKeys 统一放在
    /// PerformSave。这样关闭自动保存时不会在点击“保存设置”之前改动内存配置，也不会因为
    /// 随后某个无关操作恰好触发 ConfigService.Save() 而把未确认的勾选一起写进磁盘。</summary>
    private void FeatureHideCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking || sender is not CheckBox { Tag: string key } checkBox) return;
        if (checkBox.IsChecked == true) _pendingHiddenFeatureKeys.Add(key);
        else _pendingHiddenFeatureKeys.Remove(key);
        // DirtyTracking 的根级 Checked/Unchecked 路由事件会负责标记修改。
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    /// <summary>
    /// 更新"仅高手模式显示"的控件显隐 + 提示文案。原来首页有一份几乎一样的
    /// UpdateHint(bool advanced) 逻辑，现在模式切换入口已经统一搬到这里，首页那份已删除
    /// （见 HomePage.xaml/.xaml.cs），提示文案内容原样保留过来，只是措辞从"主页"语境
    /// 改成了当前所在的"设置"页语境。
    /// </summary>
    private void UpdateAdvancedVisibility()
    {
        var advanced = AdvancedModeCheck.IsChecked == true;
        SimpleJavaPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedJavaPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        DownloadJavaBtn.Content = advanced ? Loc.T("Str_Cs_Download_Java_Using_The_Settings_Above", "按上方设置下载 Java") : Loc.T("Str_Cs_Download_The_Java_Version_Selected_Above", "按上方版本下载 Java");
        AdvancedModeHintText.Text = advanced
            ? "已切换到高手模式：本页会显示 Java 版本、架构与安装方式等高级选项，实例级 JVM 参数请在实例设置中编辑。"
            : "当前是普通模式：启动器只展示必要的选项，Java 会自动探测/下载推荐版本，无需任何手动配置。";
    }

    /// <summary>
    /// 高手模式在设置页内可以立即预览对应控件的显隐，但配置本身仍遵循统一保存行为：
    /// 自动保存关闭时必须点“保存设置”，自动保存开启时由 DirtyTracking 防抖后调用 PerformSave。
    /// </summary>
    private void AdvancedModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
        UpdateAdvancedVisibility();
    }

    /// <summary>见字段声明处注释：挡住初始化赋值触发的事件，避免多余的一次保存。</summary>
    private bool _isInitializingCloseTraySettings;

    private void CloseActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingCloseTraySettings) return;
        // 只保留 UI 选择；DirtyTracking 负责提示/自动保存，真正写 cfg 在 PerformSave。
    }

    /// <summary>「游戏启动成功后启动器窗口」同样遵循设置页统一保存策略。</summary>
    private void PostGameLaunchActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingCloseTraySettings) return;
    }

    private void AutoStartOnBootCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoStartLaunchBehaviorCombo != null)
            AutoStartLaunchBehaviorCombo.IsEnabled = AutoStartOnBootCheck.IsChecked == true;
        if (_isInitializingCloseTraySettings) return;
        // 开机自启动涉及系统启动项，只有 PerformSave 真正保存后才调用 AutoStartService.Apply。
    }

    /// <summary>并发线程数输入框只在"启用多线程下载"勾选时才有意义显示——关闭多线程下载时
    /// 并发数固定视为 1（见 AppConfig.MaxDownloadThreads 注释），显示一个用不上的输入框
    /// 只会让用户误以为关掉多线程后调这个数字还有效果。</summary>
    private void MultiThreadDownloadCheck_Changed(object sender, RoutedEventArgs e)
    {
        ThreadCountPanel.Visibility = MultiThreadDownloadCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 「设置」页里的语言入口：跟首页顶部"🌐 语言"按钮打开的是同一个 LanguageSelectDialog，
    /// 共享同一份切换逻辑（见 HomePage.xaml.cs 的 LanguageEntryButton_Click），两处操作
    /// 结果完全一致，不是两套实现。
    /// </summary>
    private void OpenLanguagePicker_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LanguageSelectDialog(_owner.ConfigService);
        OverlayDialogService.ShowModal(dlg);
    }

    private void ReopenWizard_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new FirstRunWizardWindow(_owner);
        wizard.ShowDialog();
        // 向导跑完可能改了游戏文件夹/语言等设置，重新加载这个页面的显示值，
        // 避免用户看到的还是打开向导之前的旧值。
        SelectComboByTag(GameLanguageCombo, _owner.ConfigService.Config.GameLanguage);
        GameVersionTypeLabelBox.Text = _owner.ConfigService.Config.GameVersionTypeLabel;
        StatusText.Text = Loc.T("Str_Cs_Setup_Is_Complete_And_The_Related_Settin", "新手引导已完成，相关设置已自动刷新。");
    }

    /// <summary>需求：皮肤站可以自动 API 查询，不用手动输入完整 API Root。用户只填皮肤站
    /// 主页地址（或者已经填了完整 API Root 也没关系，探测逻辑会原样识别通过），点这个按钮
    /// 复用登录页"认证服务器登录"同一套探测逻辑（AuthServerAuthService.DetectApiRootAsync：
    /// 依次尝试 {地址}/api/yggdrasil、{地址}/authlib-injector/api 等常见路径，请求根路径看
    /// 返回的 JSON 是否带有 meta/skinDomains/signaturePublickey 这些 authlib-injector 特征
    /// 字段），探测成功直接回填输入框，不需要用户自己去皮肤站后台/文档翻 API 地址。</summary>
    private async void DetectSkinApiRoot_Click(object sender, RoutedEventArgs e)
    {
        var input = SkinApiRootBox.Text?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            MessageBoxDialog.ShowInfo("请先填写皮肤站主页地址（例如 littleskin.cn），再点「自动检测」。", Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        DetectSkinApiRootBtn.IsEnabled = false;
        try
        {
            var authService = new AuthServerAuthService();
            var resolved = await authService.DetectApiRootAsync(input);
            SkinApiRootBox.Text = resolved;
            ToastService.ShowSuccess($"已自动检测到 API 地址：{resolved}");
        }
        catch (AuthStepException ex)
        {
            MessageBoxDialog.ShowWarning(ex.Message, "自动检测失败");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("自动检测 API 地址失败，请检查网络连接，或直接手动填写完整 API Root。",
                ex.ToString(), "自动检测失败");
        }
        finally
        {
            DetectSkinApiRootBtn.IsEnabled = true;
        }
    }

    private async void DownloadJava_Click(object sender, RoutedEventArgs e)
    {
        var advanced = AdvancedModeCheck.IsChecked == true;
        var javaService = new JavaService();

        var progressWin = new ProgressDialog("正在下载 Java 运行时...");
        progressWin.Show();
        try
        {
            string path;
            int? simpleVersion = null;
            if (!advanced)
            {
                // 普通模式：版本号由上方的简化下拉框（8/17/21/25）决定，架构/安装方式仍然走
                // 固定的推荐值——当前系统架构 + 便携安装；但 Java 8 是例外，官方 8 的主流构建
                // 是 32 位的，所以这里单独把 Java 8 的架构写死成 x86，其余版本用系统架构。
                simpleVersion = (SimpleJavaVersionCombo.SelectedItem as ComboBoxItem)?.Tag is string s && int.TryParse(s, out var sv)
                    ? sv
                    : 21;
                var simpleArch = simpleVersion == 8 ? "x86" : (Environment.Is64BitOperatingSystem ? "x64" : "x86");
                path = await javaService.DownloadJavaAsync(
                    new JavaDownloadRequest(simpleVersion.Value, simpleArch, JavaInstallMode.Portable),
                    progressWin.Progress);
            }
            else
            {
                var version = (JavaVersionCombo.SelectedItem as ComboBoxItem)?.Tag is int v ? v : 21;
                var arch = (JavaArchCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "x64";
                var modeTag = (JavaInstallModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Portable";
                var mode = modeTag == "System" ? JavaInstallMode.System : JavaInstallMode.Portable;

                path = await javaService.DownloadJavaAsync(new JavaDownloadRequest(version, arch, mode), progressWin.Progress);
            }

            // 下载完成的 Java 自动登记进 Java 列表，省得用户下载完还要再手动点一次"添加"。
            var downloadedMajor = advanced ? ((JavaVersionCombo.SelectedItem as ComboBoxItem)?.Tag as int?) : simpleVersion;
            var entry = _owner.ConfigService.RegisterJava(path, downloadedMajor, "Downloaded");
            _owner.ConfigService.Save();
            RefreshJavaList();

            StatusText.Text = $"Java 下载完成，已自动加入 Java 列表（{entry.Name}）！";
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Download_Failed_This_Is_Usually_A_Networ", "下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[下载失败] {ex}", "下载失败");
        }
        finally
        {
            progressWin.Close();
        }
    }

    /// <summary>
    /// Java 列表的"刷新（自动探测）"按钮：快速查一遍常见位置(便携版目录/JAVA_HOME/注册表/PATH)，
    /// 几秒内完成，把探测到、且还没登记在列表里的 Java 自动批量加进列表——这是之前空白列表
    /// 唯一能填充内容的方式只有"浏览选择"一个个手动加，用户需要的是免手动点选的快速填充入口。
    /// 已经在列表里的路径会跳过，不会重复添加；跟"全盘扫描"（需要二次确认、可能耗时几分钟、
    /// 遍历整个磁盘）是两个不同粒度的功能，这个按钮不会有任何弹窗确认，点了就直接跑。
    /// </summary>
    private async void RefreshDetectJava_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "探测中...";
        StatusText.Text = Loc.T("Str_Cs_Auto_Detecting_Installed_Java", "正在自动探测本机 Java...");

        try
        {
            var javaService = new JavaService();
            var candidates = await javaService.QuickDetectJavaAsync();

            var cfg = _owner.ConfigService.Config;
            var existingPaths = new HashSet<string>(
                cfg.InstalledJavas.Select(j => j.JavawPath), StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var candidate in candidates)
            {
                if (existingPaths.Contains(candidate.JavawPath)) continue;

                int? major = candidate.Version != null
                    ? JavaService.ParseJavaMajorVersion($"\"{candidate.Version}\"")
                    : null;
                _owner.ConfigService.RegisterJava(candidate.JavawPath, major, "Detected");
                existingPaths.Add(candidate.JavawPath);
                added++;
            }

            if (added > 0)
            {
                _owner.ConfigService.Save();
                RefreshJavaList();
            }

            StatusText.Text = candidates.Count == 0
                ? "没有在常见位置探测到 Java。可以点「全盘扫描查找 Java」做更彻底的搜索，或手动浏览选择。"
                : added > 0
                    ? $"自动探测完成：新增 {added} 个 Java 到列表（共探测到 {candidates.Count} 个）。"
                    : $"自动探测完成：探测到的 {candidates.Count} 个 Java 都已经在列表里了。";
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError(Loc.T("Str_Cs_Auto_Detection_Failed_N", "自动探测失败：\n") + ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = originalContent;
        }
    }

    /// <summary>
    /// 全盘扫描查找 Java：默认不会自动触发，只有用户点了这个按钮才会走到这里；
    /// 点击后先弹出明确的二次确认(说明会遍历所有固定磁盘、可能耗时较久)，
    /// 用户点"是"才真正开始扫描——不同意就直接返回，什么都不做。
    /// </summary>
    private async void ScanDiskForJava_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBoxDialog.ShowConfirm(
            "即将扫描本机所有固定磁盘（不含移动硬盘/U盘/网络盘），查找已安装的 Java (javaw.exe)。\n\n" +
            "这个过程可能需要几分钟，取决于磁盘上的文件数量。默认情况下 XCL2 只会在常见默认路径" +
            "(注册表、JAVA_HOME、PATH、便携版目录)查找 Java，不会做全盘扫描；\n\n" +
            "是否同意开始全盘扫描？",
            "全盘扫描 Java - 需要确认");
        if (!confirm) return;

        var javaService = new JavaService();
        var progressWin = new ProgressDialog("正在全盘扫描 Java，请稍候...");
        progressWin.Show();

        var cts = new System.Threading.CancellationTokenSource();
        var textProgress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo(Loc.T("Str_Cs_Scan_The_Whole_Disk_For_Java", "全盘扫描 Java"), 0, 0, msg)));

        try
        {
            var candidates = await javaService.ScanWholeDiskForJavaAsync(textProgress, cts.Token);
            progressWin.Close();

            if (candidates.Count == 0)
            {
                MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Scan_Finished_No_Javaw_Exe_Was_Found_Any", "扫描完成，没有在本机磁盘上找到任何 javaw.exe。"), "全盘扫描结果");
                return;
            }

            var picker = new JavaCandidatePickerWindow(candidates);
            if (picker.ShowDialog() == true && picker.SelectedPath != null)
            {
                // 选中的这个候选自动登记进 Java 列表；候选自带的版本号（字符串，如 "21.0.5"）
                // 解析成主版本号一并存进去，省得再跑一次外部进程重新探测。
                var picked = candidates.FirstOrDefault(c => c.JavawPath == picker.SelectedPath);
                int? major = picked?.Version != null ? JavaService.ParseJavaMajorVersion($"\"{picked.Version}\"") : null;
                var entry = _owner.ConfigService.RegisterJava(picker.SelectedPath, major, "Scanned");
                _owner.ConfigService.Save();
                RefreshJavaList();

                StatusText.Text = $"已选择扫描到的 Java，并自动加入 Java 列表（{entry.Name}）。";
            }
        }
        catch (OperationCanceledException)
        {
            progressWin.Close();
        }
        catch (Exception ex)
        {
            progressWin.Close();
            MessageBoxDialog.ShowError("全盘扫描失败：\n" + ex.Message);
        }
    }

    /// <summary>浏览选择一个 javaw.exe，实测探测版本号后登记进 Java 列表。</summary>
    private void AddJavaByBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "javaw.exe|javaw.exe|所有文件|*.*", Title = "选择要添加到 Java 列表的 javaw.exe" };
        if (dialog.ShowDialog() != true) return;

        AddJavaPathToList(dialog.FileName);
    }

    /// <summary>实测探测版本号(java -version)后登记进 cfg.InstalledJavas 并保存、刷新界面。</summary>
    private void AddJavaPathToList(string javawPath)
    {
        var javaExe = Path.Combine(Path.GetDirectoryName(javawPath) ?? "", "java.exe");
        int? majorVersion = null;
        if (File.Exists(javaExe))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(javaExe, "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(5000);
                    majorVersion = JavaService.ParseJavaMajorVersion(output);
                }
            }
            catch { /* 探测失败也不阻止添加，只是版本号显示"未知" */ }
        }

        var entry = _owner.ConfigService.RegisterJava(javawPath, majorVersion, "Manual");
        _owner.ConfigService.Save();
        RefreshJavaList();

        var added = JavaListBox.Items.Cast<JavaListItem>().FirstOrDefault(i => i.Entry?.Id == entry.Id);
        if (added != null) JavaListBox.SelectedItem = added;

        StatusText.Text = $"已添加到 Java 列表：{entry.Name}";
    }

    private void RenameJava_Click(object sender, RoutedEventArgs e)
    {
        if (JavaListBox.SelectedItem is not JavaListItem { Entry: { } entry })
        {
            MessageBoxDialog.ShowInfo("请先在列表里选中要重命名的一项。");
            return;
        }

        var renameDialog = new RenameInstanceDialog(entry.Name,
            name => _owner.ConfigService.Config.InstalledJavas.Any(j => j.Id != entry.Id && j.Name == name),
            title: "重命名 Java");
        if (OverlayDialogService.ShowModal(renameDialog) == true)
        {
            entry.Name = renameDialog.NewName;
            _owner.ConfigService.Save();
            RefreshJavaList();
        }
    }

    private void RemoveJava_Click(object sender, RoutedEventArgs e)
    {
        if (JavaListBox.SelectedItem is not JavaListItem { Entry: { } entry })
        {
            MessageBoxDialog.ShowInfo("请先在列表里选中要移除的一项。");
            return;
        }

        var confirm = MessageBoxDialog.ShowConfirm(
            $"确定要从 Java 列表移除「{entry.Name}」吗？\n\n" +
            "注意：这只是从列表里移除这条记录，不会删除实际的 Java 文件；\n" +
            "如果有版本/服务器实例正引用这一条，移除后它们会自动回退到自动探测逻辑。",
            "确认移除");
        if (!confirm) return;

        var cfg = _owner.ConfigService.Config;
        cfg.InstalledJavas.RemoveAll(j => j.Id == entry.Id);
        if (cfg.SelectedJavaId == entry.Id) cfg.SelectedJavaId = null;
        foreach (var key in cfg.VersionJavaIdOverrides.Where(kv => kv.Value == entry.Id).Select(kv => kv.Key).ToList())
            cfg.VersionJavaIdOverrides.Remove(key);

        _owner.ConfigService.Save();
        RefreshJavaList();
        StatusText.Text = "已从 Java 列表移除。";
    }

    /// <summary>「阅读协议」：以只读模式打开 AgreementsWindow，只能浏览《用户协议》《隐私协议》
    /// 《开源协议》全文并前后翻页，不涉及任何表态、不改动任何配置，允许 Esc/点空白处关闭。</summary>
    private void ReadAgreements_Click(object sender, RoutedEventArgs e)
    {
        var dlg = AgreementsWindow.CreateReadOnly(_owner);
        OverlayDialogService.ShowModal(dlg);
        // 只读浏览页里新增了「切换到基本模式」快捷按钮（见 AgreementsWindow 的
        // ApplyModeChrome/GoBasicModeBtn），点了之后 RestrictedMode 可能已经变化，
        // 这里刷新一次门控，让侧边栏置灰状态、右上角「重新阅读协议并同意」按钮
        // 立即反映最新状态，不用等下次启动或切页。
        _owner.ApplyRestrictedModeGating();
    }

    /// <summary>「注销应用（暂时不同意协议）」：主动撤回已同意的协议状态——把
    /// AcceptedAgreementVersion 清零、AgreementsAccepted/BasicAgreementAccepted 复位，
    /// 下次启动时会重新走一遍协议流程（跟"协议版本号落后"触发的场景完全一致）。
    /// 点击后立即退出软件，避免继续停留在一个"配置上已注销、但界面仍按已同意状态运行"的
    /// 中间态。</summary>
    private void Deregister_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBoxDialog.ShowConfirm(
            "注销后，下次启动本软件将需要重新阅读并同意协议才能继续使用。\n\n" +
            "确定要现在注销吗？软件会随即退出。",
            "注销应用");
        if (!confirmed) return;

        var cfg = _owner.ConfigService.Config;
        cfg.AgreementsAccepted = false;
        cfg.AcceptedAgreementVersion = 0;
        cfg.BasicAgreementAccepted = false;
        cfg.RestrictedMode = false;
        _owner.ConfigService.Save();

        Application.Current.Shutdown(0);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => PerformSave();

    /// <summary>一键恢复到默认设置：重置所有可配置项回出厂默认值，并保存配置文件。</summary>
    private void ResetToDefaults_Click(object sender, RoutedEventArgs e)
    {
        // 重置为出厂默认值
        var cfg = _owner.ConfigService.Config;
        
        cfg.AdvancedMode = false;
        cfg.UiSkin = "White";
        cfg.IsDarkMode = false;
        cfg.RegistryFeatureEnabled = true;
        cfg.UseMachineWideRegistry = false;
        cfg.RestrictedMode = false;
        cfg.AgreementsAccepted = false;
        cfg.BasicAgreementAccepted = false;
        cfg.FirstRunWizardCompleted = false;
        cfg.MinMemoryMb = 1024;
        cfg.MaxMemoryMb = 4096;
        cfg.WindowWidth = 854;
        cfg.WindowHeight = 480;
        cfg.Source = DownloadSource.Official;
        cfg.GameLanguage = "zh_cn";
        cfg.GameVersionTypeLabel = "XCL2";
        cfg.EnablePageAnimations = true;
        cfg.LowPerformanceMode = false;
        cfg.AlwaysOnTop = false;
        cfg.EnableWinUi3Design = false;
        cfg.EnableWin11VisualEffects = false;
        cfg.EnableWindowTransparency = false;
        cfg.EnableGlobalWindowTransparency = false;
        cfg.TextOpacityPercent = 100;
        cfg.AutoStartOnBoot = false;
        cfg.AutoStartBehavior = AutoStartLaunchBehavior.ShowWindow;
        cfg.CustomBackgroundFrostPercent = 65;
        cfg.CustomAccentColor = null;
        cfg.PopupUseCustomAppearance = false;
        cfg.PopupOpacityPercent = 92;
        cfg.PopupFrostPercent = 40;
        cfg.PopupTextOpacityPercent = 100;
        cfg.DrawerUseCustomAppearance = false;
        cfg.DrawerOpacityPercent = 92;
        cfg.DrawerFrostPercent = 40;
        cfg.DrawerTextOpacityPercent = 100;
        cfg.MouseWheelSensitivityPercent = ScrollWheelBehavior.DefaultSensitivityPercent;
        cfg.MaxDownloadThreads = 8;
        cfg.DownloadSpeedLimitKBps = 0;
        cfg.SmartBandwidthThrottle = false;
        
        // 保存配置
        _owner.ConfigService.Save();
        
        // 刷新界面反映默认值
        _owner.NavigateToSettings();
        
        // 提示用户
        ToastService.ShowSuccess("已恢复到默认设置");
    }

    /// <summary>供 MainWindow 在"切换页面时有未保存改动"的三选一确认里选了"是"时调用，
    /// 跟点击"保存设置"按钮走的是同一套 PerformSave 逻辑。</summary>
    public void SaveNow() => PerformSave();

    /// <summary>实际的保存逻辑，从原来的 Save_Click 里抽出来，供"保存设置"按钮点击、
    /// 以及编辑追踪的自动保存/气泡"保存"按钮共用同一套逻辑，不用维护两份。</summary>
    #region 设置搜索

    private DispatcherTimer? _searchDebounceTimer;

    /// <summary>
    /// 设置搜索：每敲一个字符防抖 300ms，然后在 SettingsRootPanel 整棵可视化树里查找
    /// TextBlock/CheckBox/Button/RadioButton 的文字内容，命中就给它套一层高亮边框
    /// （不改变原有布局，只是叠加一个 Border 提示），并把第一个匹配项滚动到可视区域。
    /// 不做"隐藏不匹配项"——这个页面控件之间有大量联动（比如高手模式开关影响下面几个
    /// 控件的显隐），贸然按搜索结果隐藏容易跟这些既有的显隐逻辑打架，只做"帮你找到在哪"
    /// 已经能大幅减少手动上下滚动查找的成本。
    /// </summary>
    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SettingsSearchPlaceholder.Visibility = string.IsNullOrEmpty(SettingsSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _searchDebounceTimer?.Stop();
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer!.Stop();
            RunSettingsSearch(SettingsSearchBox.Text?.Trim() ?? "");
        };
        _searchDebounceTimer.Start();
    }

    private void RunSettingsSearch(string keyword)
    {
        ClearSettingsSearchHighlight();

        if (string.IsNullOrEmpty(keyword))
        {
            SettingsSearchResultText.Text = "";
            return;
        }

        var matches = new System.Collections.Generic.List<FrameworkElement>();
        CollectSettingsSearchMatches(SettingsRootPanel, keyword, matches);

        if (matches.Count == 0)
        {
            SettingsSearchResultText.Text = "没有找到匹配的设置项";
            return;
        }

        SettingsSearchResultText.Text = $"找到 {matches.Count} 项";
        foreach (var match in matches)
        {
            HighlightSettingsSearchMatch(match);
        }

        matches[0].BringIntoView();
    }

    /// <summary>递归遍历可视化树，收集文字内容包含关键字（不区分大小写）的元素。</summary>
    private static void CollectSettingsSearchMatches(DependencyObject root, string keyword, System.Collections.Generic.List<FrameworkElement> result)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            var text = child switch
            {
                TextBlock tb => tb.Text,
                CheckBox cb => cb.Content as string,
                RadioButton rb => rb.Content as string,
                Button btn => btn.Content as string,
                _ => null
            };

            if (!string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && child is FrameworkElement fe)
            {
                result.Add(fe);
            }

            CollectSettingsSearchMatches(child, keyword, result);
        }
    }

    /// <summary>用一层半透明强调色 Border 包住命中的元素外面，达到"高亮"效果；
    /// 不直接改元素自身 Background，避免跟控件自带的模板样式互相覆盖。</summary>
    private void HighlightSettingsSearchMatch(FrameworkElement element)
    {
        // 简化实现：直接给元素叠加一个短暂的黄色描边效果，通过 Tag 记录以便后续清除。
        // WPF 里给任意元素"套外框"最稳妥的方式是找它的直接可视化父级里能接受
        // BorderBrush 的容器；这里图简单，改成直接调它自身的 Effect（DropShadowEffect
        // 模拟高亮描边），对 TextBlock/CheckBox/Button 都适用，不需要关心具体是什么控件。
        element.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = System.Windows.Media.Colors.Gold,
            ShadowDepth = 0,
            BlurRadius = 12,
            Opacity = 0.9
        };
        _searchHighlightedElements.Add(element);
    }

    private readonly System.Collections.Generic.List<FrameworkElement> _searchHighlightedElements = new();

    private void ClearSettingsSearchHighlight()
    {
        foreach (var el in _searchHighlightedElements) el.Effect = null;
        _searchHighlightedElements.Clear();
    }

    #endregion

    private void PerformSave()
    {
        // 注意：cfg.JavaPath 这个字段本身没有删除（仍然被 FindJava 当兜底路径使用，
        // MainWindow/FirstRunWizardWindow 等处在自动探测/下载成功后会自动写入它），
        // 但本页已经去掉了对应的独立输入框（见 Java 列表区块的说明），所以这里不再读取
        // 一个不存在的控件去覆盖它——保存设置不应该把这个字段清空或改动，交给别处的
        // 自动探测/下载逻辑维护即可。
        var cfg = _owner.ConfigService.Config;

        // “窗口与托盘 / 界面缩放”等项目过去在 Changed 事件里直接 Save，绕过了页面底部的
        // 保存按钮。现在统一在这里落盘，确保自动保存关闭时所有普通设置都遵循同一套语义。
        cfg.DefaultCloseAction = CloseActionCombo.SelectedIndex switch
        {
            1 => CloseButtonAction.MinimizeToTray,
            2 => CloseButtonAction.Minimize,
            3 => CloseButtonAction.AskEachTime,
            _ => CloseButtonAction.DirectClose
        };
        cfg.PostGameLaunchAction = PostGameLaunchActionCombo.SelectedIndex switch
        {
            1 => Models.PostGameLaunchAction.Minimize,
            2 => Models.PostGameLaunchAction.MinimizeToTray,
            3 => Models.PostGameLaunchAction.Close,
            _ => Models.PostGameLaunchAction.KeepAsIs
        };
        cfg.AutoStartOnBoot = AutoStartOnBootCheck.IsChecked == true;
        cfg.AutoStartBehavior = AutoStartLaunchBehaviorCombo.SelectedIndex switch
        {
            1 => AutoStartLaunchBehavior.Minimize,
            2 => AutoStartLaunchBehavior.MinimizeToTray,
            _ => AutoStartLaunchBehavior.ShowWindow
        };

        cfg.EnableUiZoomShortcut = UiZoomEnabledCheck.IsChecked == true;
        var zoomBindings = new List<string>();
        if (UiZoomWheelCheck.IsChecked == true) zoomBindings.Add(UiZoomShortcutMode.CtrlWheel);
        if (UiZoomArrowCheck.IsChecked == true) zoomBindings.Add(UiZoomShortcutMode.CtrlArrow);
        cfg.UiZoomShortcutBindings = zoomBindings;
        cfg.UiZoomPercent = Math.Clamp((int)Math.Round(UiZoomSlider.Value), UiZoomService.MinPercent, UiZoomService.MaxPercent);
        cfg.MouseWheelSensitivityPercent = ScrollWheelBehavior.ClampSensitivityPercent((int)Math.Round(MouseWheelSensitivitySlider.Value));

        cfg.MinMemoryMb = int.TryParse(MinMemBox.Text, out var min) ? min : cfg.MinMemoryMb;
        cfg.MaxMemoryMb = int.TryParse(MaxMemBox.Text, out var max) ? max : cfg.MaxMemoryMb;
        cfg.WindowWidth = int.TryParse(WidthBox.Text, out var w) ? w : cfg.WindowWidth;
        cfg.WindowHeight = int.TryParse(HeightBox.Text, out var h) ? h : cfg.WindowHeight;
        cfg.Source = SourceCombo.SelectedIndex == 1 ? DownloadSource.Official : DownloadSource.BMCLAPI;
        if ((GameLanguageCombo.SelectedItem as ComboBoxItem)?.Tag is string lang) cfg.GameLanguage = lang;
        cfg.GameVersionTypeLabel = GameVersionTypeLabelBox.Text?.Trim() ?? "";
        cfg.EnablePageAnimations = PageAnimationsCheck.IsChecked == true;
        cfg.LowPerformanceMode = LowPerformanceModeCheck.IsChecked == true;
        cfg.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        cfg.ScheduledInstanceBackupEnabled = ScheduledBackupCheck.IsChecked == true;
        cfg.ScheduledInstanceBackupVersionId = cfg.SelectedVersionId;
        if (int.TryParse(ScheduledBackupIntervalBox.Text, out var backupHours))
            cfg.ScheduledInstanceBackupIntervalHours = Math.Clamp(backupHours, 1, 720);
        if (int.TryParse(ScheduledBackupRetentionBox.Text, out var retention))
            cfg.ScheduledInstanceBackupRetentionCount = Math.Clamp(retention, 1, 100);
        cfg.BackupInstanceOnStartup = BackupOnStartupCheck.IsChecked == true;
        cfg.BackupInstanceOnClose = BackupOnCloseCheck.IsChecked == true;
        if (Enum.TryParse<LifecycleBackupTargetMode>(TagOf(LifecycleBackupTargetModeCombo), out var lifecycleMode))
            cfg.LifecycleBackupTargetMode = lifecycleMode;
        cfg.LifecycleBackupVersionIds = LifecycleBackupVersionIdsBox.Text
            .Split(new[] { '\r', '\n', ',', ';', '；', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.EnableInjectionScan = InjectionScanCheck.IsChecked == true;
        cfg.EnableGameConsoleWindow = GameConsoleWindowCheck.IsChecked == true;
        cfg.ShowModIcons = ShowModIconsCheck.IsChecked == true;
        cfg.ShowServerNetworkGuideOnStart = ShowServerNetworkGuideCheck.IsChecked == true;
        cfg.IsolateVersionsByDefault = IsolateVersionsCheck.IsChecked == true;

        cfg.EnableWin11VisualEffects = Win11EffectsCheck.IsChecked == true;
        cfg.EnableWinUi3Design = WinUi3DesignCheck.IsChecked == true;
        cfg.AppFontFamily = (AppFontCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        cfg.AppFontFamily_TitleBar = (TitleBarFontCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        cfg.AppFontFamily_Sidebar = (SidebarFontCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        cfg.AppFontFamily_Content = (ContentFontCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        cfg.BrightnessPercent = (int)BrightnessSlider.Value;
        var newCustomAccentColor = string.IsNullOrWhiteSpace(CustomAccentColorBox.Text) ? null : CustomAccentColorBox.Text.Trim();
        var customAccentChanged = !string.Equals(cfg.CustomAccentColor, newCustomAccentColor, StringComparison.OrdinalIgnoreCase);
        cfg.CustomAccentColor = newCustomAccentColor;
        cfg.Win11BackdropMaterial = (BackdropMaterialCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Mica";
        cfg.EnableWindowTransparency = WindowTransparencyCheck.IsChecked == true;
        cfg.CustomBackgroundImagePath = string.IsNullOrWhiteSpace(CustomBackgroundImagePathBox.Text) ? null : CustomBackgroundImagePathBox.Text;
        cfg.CustomBackgroundFrostPercent = Math.Clamp((int)CustomBackgroundFrostSlider.Value, 25, 100);
        cfg.WindowOpacityPercent = (int)WindowOpacitySlider.Value;
        // 跟其它设置不同，这两项一保存就应该立刻能在已打开的窗口上看到效果，不用重启/切页——
        // 用户在设置页调滑块本来就是想马上比对效果，见 ThemeService.ApplyWindowTransparency
        // 与 Win11EffectsService.SetEnabled 类注释。
        cfg.EnableGlobalWindowTransparency = GlobalWindowTransparencyCheck.IsChecked == true;
        cfg.GlobalWindowOpacityPercent = (int)GlobalWindowOpacitySlider.Value;
        cfg.TextOpacityPercent = Math.Clamp((int)Math.Round(TextOpacitySlider.Value), 50, 100);

        ThemeService.ApplyWindowTransparency(cfg.EnableWindowTransparency, cfg.WindowOpacityPercent);
        var material = Enum.TryParse<Win11EffectsService.BackdropMaterial>(cfg.Win11BackdropMaterial, out var m)
            ? m : Win11EffectsService.BackdropMaterial.Mica;
        // 先切换 Mica/Acrylic，再最后应用整窗透明；DWM 材质切换可能重建合成属性，
        // 如果顺序反过来会把刚设置的 layered alpha 覆盖掉，表现成“整窗透明无作用”。
        Win11EffectsService.SetEnabled(cfg.EnableWin11VisualEffects, material);
        // WinUI 3 新设计：跟其它"保存后立即生效"的视觉开关一样，不用等重启——圆角部分对
        // Windows 10 静默不生效（见 Win11EffectsService.Apply 里 DWM 属性调用失败即忽略的一贯处理），
        // 字体部分（Segoe UI Variable）在任何 Windows 版本上都能立即生效。之前这个开关只写进
        // 配置文件，从没有代码真正应用过，勾了也看不出任何变化。
        Win11EffectsService.SetWinUi3Enabled(cfg.EnableWinUi3Design);
        ThemeService.ApplyFontFamily(cfg.AppFontFamily, cfg.EnableWinUi3Design);
        FontService.ApplyScopedFonts(_owner, cfg);
        ThemeService.ApplyBrightness(cfg.BrightnessPercent);
        if (!string.IsNullOrWhiteSpace(cfg.CustomBackgroundImagePath) && File.Exists(cfg.CustomBackgroundImagePath))
            _owner.SetCustomBackgroundImage(cfg.CustomBackgroundImagePath);
        else
            _owner.SetCustomBackgroundImage(null);
        ThemeService.ApplyGlobalWindowTransparency(cfg.EnableGlobalWindowTransparency, cfg.GlobalWindowOpacityPercent);
        ThemeService.ApplyTextOpacity(cfg.TextOpacityPercent);

        if (!_transparencyReadabilityWarningShown &&
            ((cfg.EnableWindowTransparency && cfg.WindowOpacityPercent <= 45) ||
             (cfg.EnableGlobalWindowTransparency && cfg.GlobalWindowOpacityPercent <= 65)))
        {
            _transparencyReadabilityWarningShown = true;
            Dispatcher.BeginInvoke(new Action(() => MessageBoxDialog.ShowInfo(
                $"当前配置：面板透明度 {cfg.WindowOpacityPercent}%，整窗透明度 {cfg.GlobalWindowOpacityPercent}%，文字透明度 {cfg.TextOpacityPercent}%。\n\n如果造成有些文字看不清楚，可以在设置里找到“文字透明度设置”进行单独设置。",
                "透明度可读性提示")), DispatcherPriority.Background);
        }

        cfg.PopupUseCustomAppearance = PopupCustomAppearanceCheck.IsChecked == true;
        cfg.PopupOpacityPercent = (int)PopupOpacitySlider.Value;
        cfg.PopupFrostPercent = (int)PopupFrostSlider.Value;
        cfg.PopupTextOpacityPercent = (int)PopupTextOpacitySlider.Value;
        cfg.DrawerUseCustomAppearance = DrawerCustomAppearanceCheck.IsChecked == true;
        cfg.DrawerOpacityPercent = (int)DrawerOpacitySlider.Value;
        cfg.DrawerFrostPercent = (int)DrawerFrostSlider.Value;
        cfg.DrawerTextOpacityPercent = (int)DrawerTextOpacitySlider.Value;
        ThemeService.SetPopupAppearanceConfig(cfg.PopupUseCustomAppearance, cfg.PopupOpacityPercent, cfg.PopupFrostPercent, cfg.PopupTextOpacityPercent);
        ThemeService.SetDrawerAppearanceConfig(cfg.DrawerUseCustomAppearance, cfg.DrawerOpacityPercent, cfg.DrawerFrostPercent, cfg.DrawerTextOpacityPercent);

        cfg.ModpackDropCreatesNewInstance = ModpackDropNewInstanceCheck.IsChecked == true;
        if (Enum.TryParse<DropZipDefault>(TagOf(ZipDropDefaultCombo), out var zipDef))
            cfg.ZipDropDefault = zipDef;
        if (Enum.TryParse<DropJarTarget>(TagOf(ServerJarDropCombo), out var srvJar))
            cfg.ServerPageJarDropTarget = srvJar;
        if (Enum.TryParse<DropJarTarget>(TagOf(DefaultJarDropCombo), out var defJar))
            cfg.DefaultJarDropTarget = defJar;
        cfg.IsolateResourcePacksByDefault = IsolateResourcePacksCheck.IsChecked == true;

        cfg.EnableMultiThreadDownload = MultiThreadDownloadCheck.IsChecked == true;
        if (int.TryParse(ThreadCountBox.Text, out var threads))
            cfg.MaxDownloadThreads = Math.Clamp(threads, 1, 64);
        if (int.TryParse(SpeedLimitBox.Text, out var speedLimit))
            cfg.DownloadSpeedLimitKBps = Math.Max(0, speedLimit);
        cfg.SmartBandwidthThrottle = SmartThrottleCheck.IsChecked == true;

        cfg.LowPerformanceMode = LowPerformanceModeCheck.IsChecked == true;

        cfg.DownloadNotifyMode = DownloadNotifyModeCombo.SelectedIndex;
        cfg.GameVersionNoPopup = GameVersionNoPopupCheck.IsChecked == true;
        cfg.CommunityResourceNoPopup = CommunityResourceNoPopupCheck.IsChecked == true;
        cfg.ModpackNoPopup = ModpackNoPopupCheck.IsChecked == true;

        cfg.SelectedJavaId = (DefaultJavaCombo.SelectedItem as JavaListItem)?.Entry?.Id;

        // 访客模式：这里把勾选状态写回本轮会话的 Config。真正切换临时账户由
        // MainWindow.RefreshGuestModeState 完成；ConfigService.Save 会特意把 GuestModeEnabled
        // 以 false 写入磁盘，所以这个开关只对当前进程有效、重启后始终恢复普通模式。
        // 设置页仍保持“点保存才生效”的交互，避免仅勾选但尚未保存时侧边栏提前切换账户。
        var requestedGuestMode = GuestModeCheck.IsChecked == true;
        var enableGuestModeByRestart = !cfg.GuestModeEnabled && requestedGuestMode;
        var guestModeChanged = cfg.GuestModeEnabled != requestedGuestMode;
        // 开启访客模式时当前进程绝不直接切换；配置仍保持 false，保存完成后重启到一次性的
        // --guest-session。关闭访客模式则可以直接清掉当前会话态。
        if (!enableGuestModeByRestart) cfg.GuestModeEnabled = requestedGuestMode;

        // 配色皮肤：同样只是先写回配置，实际应用画刷的动作跟访客模式共用下面
        // RefreshGuestModeState/ThemeService.ApplyForCurrentState 那一次调用，
        // 不需要在这里单独再调一次 ThemeService，避免访客模式和皮肤同时变化时重复刷新两次。
        var selectedSkinFromUi = (UiSkinCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        // 如果“自定义”只是被打开、用户还没真正选定颜色，就保持原主题不变；这时即使因为
        // 其它设置点击了“保存”，也不能顺手把 Custom 当成已经确认的选择写进配置。
        var skinToSave = _customThemeSelectionPending ? cfg.UiSkin : (selectedSkinFromUi ?? cfg.UiSkin);
        var uiSkinChanged = !string.Equals(cfg.UiSkin, skinToSave, StringComparison.Ordinal);
        cfg.UiSkin = skinToSave;

        var skinApiRoot = SkinApiRootBox.Text?.Trim();
        cfg.SkinApiRoot = string.IsNullOrEmpty(skinApiRoot) ? SkinService.DefaultSkinApiRoot : skinApiRoot;

        // 自动循环的两个切换时间点：只在这里设置；「自动循环」开关本身和「模式设置」
        // （深/浅色）都在首页/主界面按钮上直接切换、立即生效，不需要点这里的保存按钮。
        if ((AutoThemeLightStartHourCombo.SelectedItem as ComboBoxItem)?.Tag is int lightHour)
            cfg.AutoThemeLightStartHour = lightHour;
        if ((AutoThemeDarkStartHourCombo.SelectedItem as ComboBoxItem)?.Tag is int darkHour)
            cfg.AutoThemeDarkStartHour = darkHour;
        // 时间点改了之后，让自动循环下次检查时重新按新计划判定一次，而不是被"上次已经在这个
        // 时间段应用过了"的旧记录挡住、误以为不需要更新。
        cfg.AutoThemeLastAppliedSlotStartHour = null;

        cfg.AdvancedMode = AdvancedModeCheck.IsChecked == true;
        if (cfg.AdvancedMode)
        {
            if ((JavaVersionCombo.SelectedItem as ComboBoxItem)?.Tag is int v) cfg.PreferredJavaMajorVersion = v;
            if ((JavaArchCombo.SelectedItem as ComboBoxItem)?.Tag is string arch) cfg.PreferredJavaArch = arch;
            if ((JavaInstallModeCombo.SelectedItem as ComboBoxItem)?.Tag is string mode) cfg.PreferredJavaInstallMode = mode;
            cfg.EnforceJavaVersionMatch = EnforceJavaVersionMatchCheck.IsChecked == true;
        }
        else if ((SimpleJavaVersionCombo.SelectedItem as ComboBoxItem)?.Tag is string simpleTag && int.TryParse(simpleTag, out var sv))
        {
            // 普通模式下保存的是简化下拉框（8/17/21/25）里选的版本，架构固定走推荐值，
            // 不写回 PreferredJavaArch/PreferredJavaInstallMode，避免覆盖用户之前在
            // 高手模式下设置过的架构/安装方式偏好。
            cfg.PreferredJavaMajorVersion = sv;
        }

        if (int.TryParse(AccountTokenGraceDaysBox.Text, out var graceDays))
            cfg.AccountTokenGracePeriodDays = Math.Max(0, graceDays);
        cfg.UseMachineWideRegistry = UseMachineWideRegistryCheck.IsChecked == true;
        cfg.SettingsAutoSaveWithoutConfirm = SettingsAutoSaveCheck.IsChecked == true;
        cfg.DownloadPopupShowDetailStats = DownloadPopupDetailCheck.IsChecked == true;
        cfg.DownloadPopupSizeDisplayMode = DownloadPopupSizeModeCombo.SelectedIndex;
        cfg.EnableHighPerformanceMode = HighPerformanceModeCheck.IsChecked == true;

        // 功能隐藏从页面内的编辑副本写回，不依赖 ItemsControl 当前是否已生成全部可视容器。
        cfg.HiddenFeatureKeys = _pendingHiddenFeatureKeys.ToList();

        _owner.ConfigService.Save();
        AutoStartService.Apply(cfg.AutoStartOnBoot);
        ScrollWheelBehavior.SetSensitivityPercent(cfg.MouseWheelSensitivityPercent);
        UiZoomService.PreviewPercent(cfg.UiZoomPercent);

        // 下载气泡详情行的显隐/放大是"保存后立即生效"类设置，跟窗口透明度那两项同理，
        // 不需要用户重启或切页才能看到变化。
        _owner.ApplyDownloadPopupDetailMode();
        FrameRateMonitorService.SetEnabled(cfg.EnableHighPerformanceMode);

        // 访客模式开关状态发生变化时，让 MainWindow 立即重新计算"当前应该用哪个账户"
        // （开启时切到临时访客账户，关闭时切回真实保存的账户），并刷新侧边栏显示，
        // 不需要用户重启启动器才能看到效果。
        // 访客模式、皮肤选择，或“自定义”主题颜色任一变化都要重新应用当前主题。
        // 特别是 UiSkin 已经是 Custom 时，仅修改十六进制/RGB 颜色也必须立即刷新，
        // 不能因为 uiSkinChanged=false 就把新颜色只写进 config.json 而界面仍停在旧颜色。
        // 关闭访客模式、或单纯改主题时可在当前进程刷新；开启访客模式必须走下面的进程重启，
        // 不能再在这里临时切账户，否则会违背“一开启访客就重启”的会话隔离约定。
        if ((!enableGuestModeByRestart && guestModeChanged) || uiSkinChanged || customAccentChanged)
            _owner.RefreshGuestModeState();
        // 自动循环的时间点可能刚被改过（上面已经清空了 AutoThemeLastAppliedSlotStartHour），
        // 这里立即按新计划重新校验一次，保证"保存后一秒内看到效果"——如果当前时间刚好落在
        // 新设置的时间段边界两侧、导致该切换的深浅色模式发生变化，会立刻应用，不需要等到
        // 下一次每分钟定时检查。
        _owner.ReevaluateAutoThemeCycle();
        _owner.RefreshSidebar();
        _owner.ApplyFeatureVisibility(); // 功能隐藏勾选可能变了，立即刷新导航栏对应按钮的显隐

        RefreshRegistryStatusText();
        StatusText.Text = "设置已保存。";
        _hasUnsavedChanges = false;
        // 如果用户是通过页面底部“保存设置”或离页/关闭确认保存，而不是点操作卡里的“保存”，
        // 旧的“设置已修改”卡片也应立即消失，避免保存后还挂着一张过期提示。
        ToastService.DismissActionPrompt("settings-dirty");
        _lastSavedUiFingerprint = BuildSettingsUiFingerprint();
        _uiFingerprintReady = true;

        if (enableGuestModeByRestart)
        {
            _owner.RequestGuestModeRestart();
            return;
        }
    }

    /// <summary>刷新"注册表存储"区块下方的状态提示文字：当前 HKLM/HKCU 两支实际是否存在
    /// XCL2 的注册表键，以及当前进程是否具备管理员权限——帮用户理解"为什么我勾了全设备
    /// 但好像没生效"（提权与否是运行时状态，跟这个勾选框本身是两回事）。</summary>
    private void RefreshRegistryStatusText()
    {
        var cfg = _owner.ConfigService.Config;
        if (!cfg.RegistryFeatureEnabled)
        {
            RegistryStatusText.Text = "注册表功能当前已关闭，只使用 config.json。";
            return;
        }

        var (existsInHklm, existsInHkcu) = RegistryConfigService.CheckExistence();
        var isAdmin = RegistryConfigService.IsRunningAsAdministrator();
        var where = existsInHklm ? "HKEY_LOCAL_MACHINE（全设备）" : existsInHkcu ? "HKEY_CURRENT_USER（当前用户）" : "尚未写入";
        RegistryStatusText.Text =
            $"当前生效来源：{where}；当前进程{(isAdmin ? "以管理员身份运行" : "为普通权限")}" +
            (cfg.UseMachineWideRegistry && !isAdmin ? "（已勾选全设备，但这次未提权，本次保存会写入当前用户分支）。" : "。");
    }

    /// <summary>"更新配置文件"：把新版本引入的设置默认值补丁进当前配置，不覆盖用户已改的字段。
    /// 见 ConfigService.PatchDefaults 的白名单/兜底规则。</summary>
    private void UpdateConfigDefaults_Click(object sender, RoutedEventArgs e)
    {
        var patched = _owner.ConfigService.PatchDefaults();
        StatusText.Text = patched > 0 ? $"已补丁 {patched} 项新增的默认设置。" : "配置文件已经是最新，没有需要补丁的项。";
        RefreshRegistryStatusText();
    }

    /// <summary>"导出注册表 (.reg)"：把当前 XCL2 注册表键（不管在 HKLM 还是 HKCU）导出为
    /// 标准 .reg 文件，双击即可在别的电脑上导入同一份注册表内容。</summary>
    private void ExportReg_Click(object sender, RoutedEventArgs e)
    {
        var content = _owner.ConfigService.ExportRegistryFile();
        if (content == null)
        {
            MessageBoxDialog.ShowInfo("当前没有可导出的 XCL2 注册表内容（可能注册表功能已关闭，或还从未写入过）。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出注册表",
            Filter = "注册表文件|*.reg|所有文件|*.*",
            FileName = $"XCL2_{DateTime.Now:yyyyMMdd_HHmmss}.reg"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // .reg 文件本身按微软习惯用 UTF-16 LE + BOM 保存（Windows 注册表编辑器导出的
            // 标准格式，双击导入时才能正确识别中文注释/值），跟项目里其它 .bat 脚本用
            // UTF-8 BOM 是两回事，不要混用编码。
            File.WriteAllText(dialog.FileName, content, System.Text.Encoding.Unicode);
            MessageBoxDialog.ShowSuccess($"注册表已导出到：\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导出失败：{ex.Message}");
        }
    }

    /// <summary>"导出所有配置"：config.json + 注册表镜像字段 + 各实例设置打包成一份归档文件。</summary>
    private void ExportAllConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出所有配置",
            Filter = "XCL2 配置归档|*.xclconfig.json|所有文件|*.*",
            FileName = $"XCL2_配置备份_{DateTime.Now:yyyyMMdd_HHmmss}.xclconfig.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _owner.ConfigService.ExportAllConfig());
            MessageBoxDialog.ShowSuccess($"所有配置已导出到：\n{dialog.FileName}\n\n（不含账户登录凭据，账户需要在新环境重新登录）");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导出失败：{ex.Message}");
        }
    }

    /// <summary>"导入配置"按钮：弹文件选择框，选中后走跟拖拽导入完全相同的
    /// <see cref="ImportConfigArchiveFile"/>。</summary>
    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入配置", Filter = "XCL2 配置归档|*.json;*.xclconfig.json|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        ImportConfigArchiveFile(dialog.FileName);
    }

    private void ImportDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ImportDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            ImportConfigArchiveFile(files[0]);
    }

    /// <summary>真正执行"导入配置"：解析归档文件，确认后整体替换当前配置。
    /// 因为会整体替换 Config（不是逐项合并），执行前先跟用户确认一次——这不属于危险操作
    /// 分类里"要求 xztx127"的那三个（没有清除数据/删除注册表这类不可逆的破坏性），
    /// 只是普通的"要不要覆盖当前设置"确认，用常规的 ShowConfirm 即可。</summary>
    private void ImportConfigArchiveFile(string filePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"无法读取文件：{ex.Message}");
            return;
        }

        var confirmed = MessageBoxDialog.ShowConfirm(
            "导入会用文件里的设置整体替换当前的启动器配置（账户登录状态不受影响）。\n\n确定要导入吗？",
            "导入配置");
        if (!confirmed) return;

        try
        {
            var restoredInstances = _owner.ConfigService.ImportAllConfig(json);
            MessageBoxDialog.ShowSuccess($"配置已导入，另外恢复了 {restoredInstances} 个实例的单独设置。\n部分设置需要重新打开设置页/重启启动器才能完全生效。");
            _owner.RefreshGuestModeState();
            _owner.RefreshSidebar();
            _owner.ApplyFeatureVisibility();
            _owner.ApplyRestrictedModeGating();
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导入失败：{ex.Message}");
        }
    }

    /// <summary>危险操作统一入口：弹 xztx127 二次确认，确认通过才真正执行 <paramref name="action"/>。</summary>
    private void RunDangerousOperation(string title, string message, Action action)
    {
        var dlg = new DangerousConfirmDialog(title, message);
        if (OverlayDialogService.ShowModal(dlg) != true || !dlg.Confirmed) return;
        action();
    }

    private void DisableRegistry_Click(object sender, RoutedEventArgs e)
    {
        RunDangerousOperation(
            "关闭注册表功能",
            "关闭后，启动器只使用 config.json，不再读写注册表。已经写入的注册表项不会被自动删除。",
            () =>
            {
                _owner.ConfigService.DisableRegistryFeature();
                UseMachineWideRegistryCheck.IsEnabled = false;
                RefreshRegistryStatusText();
                StatusText.Text = "注册表功能已关闭。";
            });
    }

    private void DeleteRegistry_Click(object sender, RoutedEventArgs e)
    {
        RunDangerousOperation(
            "删除所有新增的启动器注册表项",
            "将删除 HKEY_LOCAL_MACHINE 和 HKEY_CURRENT_USER 下的 SOFTWARE\\XCL2 键（仅此一个键，不影响其它任何注册表内容）。此操作不可撤销。",
            () =>
            {
                var (hklm, hkcu) = _owner.ConfigService.DeleteAllRegistryEntries();
                RefreshRegistryStatusText();
                StatusText.Text = (hklm || hkcu) ? "注册表项已删除。" : "没有找到可删除的注册表项。";
            });
    }

    private void ClearTraces_Click(object sender, RoutedEventArgs e)
    {
        RunDangerousOperation(
            "清除本机痕迹",
            "将删除 XCL2 的注册表项，以及本机的 xcl2 数据目录（配置、账户、日志、下载缓存的 Java 等全部内容）。" +
            "不会删除任何 .minecraft 游戏目录或其它文件。执行后启动器会立即退出。此操作不可撤销。",
            () =>
            {
                try
                {
                    _owner.ConfigService.ClearAllTraces();
                    Application.Current.Shutdown(0);
                }
                catch (Exception ex)
                {
                    MessageBoxDialog.ShowError($"清除痕迹时出现问题，部分内容可能未能删除：{ex.Message}");
                }
            });
    }

/// <summary>
    /// 打开 AI 助手设置面板（内嵌在窗口内，不再是 Win32 窗口）。
    /// </summary>
    private void AiAssistantSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var settingsPanel = new AiAssistantSettingsPanel(_owner.AiAssistantConfig);
        settingsPanel.Saved += (_, config) =>
        {
            _owner.AiAssistantConfig = config;
            _owner.AiAssistantService.UpdateConfig(_owner.AiAssistantConfig);
            _owner.PersistAiAssistantConfig();
            _owner.UpdateAiFloatingButtonVisibility();
        };
        OverlayDialogService.ShowModal(settingsPanel);
    }

    /// <summary>
    /// "实验性功能"统一入口：第一次打开（cfg.ExperimentalFeaturesUnlocked 还是 false）先弹
    /// ExperimentalGateWindow 强制等待 10 秒确认；确认过一次之后这个标记会持久化保存，
    /// 后续再点直接打开 ExperimentalFeaturesWindow，不需要重复罚站。
    /// 用户在网关窗口点"取消"或者直接关掉窗口（Confirmed 仍为 false）时，什么都不做、
    /// 也不会污染 ExperimentalFeaturesUnlocked，下次点击还是会重新走一遍网关。
    /// </summary>
    private void ExperimentalFeatures_Click(object sender, RoutedEventArgs e)
    {
        _owner.OpenExperimentalFeatures();
    }

    // ===================== 拖拽安装设置 =====================

    /// <summary>按 Tag 选中下拉项。配置存的是枚举名（"Ask"/"Server"/…），
    /// XAML 里每个 ComboBoxItem 的 Tag 就写同样的字符串，两边靠这个对上，
    /// 不依赖下拉项的排列顺序——以后往中间插一项也不会错位。</summary>
    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is System.Windows.Controls.ComboBoxItem item &&
                string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string TagOf(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";

    /// <summary>拖拽相关下拉框的变化处理：跟本页其它设置一样，改动先留在界面上，
    /// 由统一的保存流程写回配置，不在每次选择时立刻落盘。</summary>
    private void DragDropSetting_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 目前不需要即时联动，保留这个处理器是因为 XAML 里绑了 SelectionChanged；
        // 将来若要做"选了『每次询问』就把某些项灰掉"之类的联动，写在这里。
    }


    private void LifecycleBackupTargetModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLifecycleBackupTargetsUi();
    }

    private void UpdateLifecycleBackupTargetsUi()
    {
        if (LifecycleBackupTargetsPanel == null || LifecycleBackupTargetModeCombo == null) return;
        LifecycleBackupTargetsPanel.IsEnabled = TagOf(LifecycleBackupTargetModeCombo) != nameof(LifecycleBackupTargetMode.Single);
        LifecycleBackupTargetsPanel.Opacity = LifecycleBackupTargetsPanel.IsEnabled ? 1.0 : 0.55;
    }

    /// <summary>Win11 视觉效果 / 窗口透明度两个 CheckBox 以及背景材质下拉框共用的处理器。
    /// 先做控件启用状态联动；初始化完成后再做“仅视觉预览”的即时应用，让 Mica/Acrylic 的
    /// 选择当场反映到主窗口。配置本身仍由保存流程落盘，所以取消/回退时可以恢复旧设置。
    /// InitializeComponent 阶段设置初始值也会触发此事件，由 _suppressDirtyTracking 拦住预览。</summary>
    private void VisualEffectsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (WindowOpacitySlider == null) return; // InitializeComponent 尚未跑完时的极早期事件，忽略
        WindowOpacitySlider.IsEnabled = WindowTransparencyCheck.IsChecked == true;
        GlobalWindowOpacitySlider.IsEnabled = GlobalWindowTransparencyCheck.IsChecked == true;
        if (BackdropMaterialPanel != null) BackdropMaterialPanel.IsEnabled = Win11EffectsCheck.IsChecked == true;
        ApplyAquaticLockIfNeeded();

        // 背景材质属于纯视觉预览：用户在下拉框选择 Mica/MicaAlt/Acrylic 后应当立刻同步到
        // 当前主窗口，而不是等到页面底部“保存设置”或重启。初始化控件时会触发同一个事件，
        // 用 _suppressDirtyTracking 挡住，避免构造设置页过程中把半初始化的控件值应用出去。
        if (_suppressDirtyTracking) return;
        var material = Enum.TryParse<Win11EffectsService.BackdropMaterial>(
            (BackdropMaterialCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Mica", out var parsed)
            ? parsed : Win11EffectsService.BackdropMaterial.Mica;
        Win11EffectsService.SetEnabled(Win11EffectsCheck.IsChecked == true, material);
        ThemeService.ApplyWindowTransparency(WindowTransparencyCheck.IsChecked == true, (int)WindowOpacitySlider.Value);
        // ApplyWindowTransparency 会触发一次全局背景刷新；设置页若有尚未保存的磨砂度预览，
        // 这里必须用滑块当前值再覆盖回来，避免切换 Mica/Acrylic/透明开关时磨砂度瞬间跳回旧配置。
        _owner.PreviewCustomBackgroundFrost((int)CustomBackgroundFrostSlider.Value);
    }

    /// <summary>
    /// 自定义背景磨砂度：25% 接近透明/清晰，100% 为最强磨砂。
    /// 拖动时直接预览，但配置落盘仍交给统一的设置保存/自动保存流程。
    /// </summary>
    private void CustomBackgroundFrostSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CustomBackgroundFrostValueText == null) return;
        var percent = Math.Clamp((int)e.NewValue, 25, 100);
        CustomBackgroundFrostValueText.Text = $"{percent}%";
        if (_suppressDirtyTracking) return;
        _owner.PreviewCustomBackgroundFrost(percent);
    }

    /// <summary>透明度滑块拖动时只更新旁边的百分比文字，实际生效同样要等点"保存设置"。</summary>
    private void WindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WindowOpacityValueText == null) return;
        WindowOpacityValueText.Text = $"{(int)e.NewValue}%";
    }

    /// <summary>亮度滑块拖动时立即预览效果（跟面板/整窗透明度滑块的即时预览体验一致），
    /// 真正落盘仍然要等"保存设置"——不预览的话用户没法在保存前先看看这个值合不合适。</summary>
    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValueText == null) return;
        BrightnessValueText.Text = $"{(int)e.NewValue}%";
        if (_suppressDirtyTracking) return;
        ThemeService.ApplyBrightness((int)e.NewValue);
    }

    /// <summary>整窗全局透明度滑块拖动时只更新旁边的百分比文字，同样要等"保存设置"才生效。</summary>
    private void GlobalWindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlobalWindowOpacityValueText == null) return;
        GlobalWindowOpacityValueText.Text = $"{(int)e.NewValue}%";
    }

    /// <summary>弹窗"独立外观"开关：勾选/取消时联动滑块可用状态，并立即预览一次
    /// （不落盘，跟其它外观预览一样，真正生效要等"保存设置"）。</summary>
    private void PopupAppearanceControl_Changed(object sender, RoutedEventArgs e)
    {
        if (PopupOpacitySlider == null) return;
        var useCustom = PopupCustomAppearanceCheck.IsChecked == true;
        PopupOpacitySlider.IsEnabled = useCustom;
        PopupFrostSlider.IsEnabled = useCustom;
        PopupTextOpacitySlider.IsEnabled = useCustom;
        if (_suppressDirtyTracking) return;
        ThemeService.SetPopupAppearanceConfig(useCustom, (int)PopupOpacitySlider.Value, (int)PopupFrostSlider.Value, (int)PopupTextOpacitySlider.Value);
        // 这些控件属于“展开/抽屉式设置”区域，部分 WPF 模板会吞掉根级路由事件。
        // 在实际用户改动路径上显式通知一次，保证保存/回退操作卡一定出现。
        OnSettingsEdited();
    }

    /// <summary>弹窗三个滑块（背景透明度/磨砂度/文字透明度）共用同一个即时预览处理器，
    /// 拖动时立即更新百分比文字并预览效果，真正落盘仍交给"保存设置"。</summary>
    private void PopupAppearanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PopupOpacityValueText == null || PopupFrostValueText == null || PopupTextOpacityValueText == null) return;
        PopupOpacityValueText.Text = $"{(int)PopupOpacitySlider.Value}%";
        PopupFrostValueText.Text = $"{(int)PopupFrostSlider.Value}%";
        PopupTextOpacityValueText.Text = $"{(int)PopupTextOpacitySlider.Value}%";
        if (_suppressDirtyTracking) return;
        ThemeService.SetPopupAppearanceConfig(PopupCustomAppearanceCheck.IsChecked == true, (int)PopupOpacitySlider.Value, (int)PopupFrostSlider.Value, (int)PopupTextOpacitySlider.Value);
        OnSettingsEdited();
    }

    /// <summary>抽屉（AI 助手侧栏）"独立外观"开关，逻辑跟 PopupAppearanceControl_Changed 对称。</summary>
    private void DrawerAppearanceControl_Changed(object sender, RoutedEventArgs e)
    {
        if (DrawerOpacitySlider == null) return;
        var useCustom = DrawerCustomAppearanceCheck.IsChecked == true;
        DrawerOpacitySlider.IsEnabled = useCustom;
        DrawerFrostSlider.IsEnabled = useCustom;
        DrawerTextOpacitySlider.IsEnabled = useCustom;
        if (_suppressDirtyTracking) return;
        ThemeService.SetDrawerAppearanceConfig(useCustom, (int)DrawerOpacitySlider.Value, (int)DrawerFrostSlider.Value, (int)DrawerTextOpacitySlider.Value);
        OnSettingsEdited();
    }

    /// <summary>抽屉三个滑块共用的即时预览处理器，逻辑跟 PopupAppearanceSlider_ValueChanged 对称。</summary>
    private void DrawerAppearanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DrawerOpacityValueText == null || DrawerFrostValueText == null || DrawerTextOpacityValueText == null) return;
        DrawerOpacityValueText.Text = $"{(int)DrawerOpacitySlider.Value}%";
        DrawerFrostValueText.Text = $"{(int)DrawerFrostSlider.Value}%";
        DrawerTextOpacityValueText.Text = $"{(int)DrawerTextOpacitySlider.Value}%";
        if (_suppressDirtyTracking) return;
        ThemeService.SetDrawerAppearanceConfig(DrawerCustomAppearanceCheck.IsChecked == true, (int)DrawerOpacitySlider.Value, (int)DrawerFrostSlider.Value, (int)DrawerTextOpacitySlider.Value);
        OnSettingsEdited();
    }

/// <summary>原来这里会在勾选 Win11 高级特效时强制把色系锁死成"水"（Aquatic），
    /// 用户反馈不希望被强制切换主题——现在改成让所有色系都能正常搭配云母/亚克力材质，
    /// 不再有这条限制，勾选/取消 Win11 特效都不会改动用户选的色系，下拉框也始终可用。
    /// 方法保留（调用点不动），改成空实现，避免把所有调用点都删掉再引入遗漏。</summary>
    private void ApplyAquaticLockIfNeeded()
    {
        if (UiSkinCombo == null) return;
        UiSkinCombo.IsEnabled = true;
    }

    private DispatcherTimer? _accentApplyDebounceTimer;

    // 修复"用户自己取色、设置后没有效果"：以前这里只更新了旁边那个小预览方块的背景，
    // 从来没有调用 ThemeService.ApplyCustomAccent——色板按钮(AccentSwatch_Click)和
    // RGB 滑块的"使用 RGB 颜色"按钮都会立即预览生效，唯独直接在"颜色值"文本框里
    // 输入/粘贴十六进制颜色这条路径不会，用户很容易以为"取色/填色之后没反应"。
    // 现在改成：格式一合法就用短暂防抖（300ms，等用户打完/粘贴完一整段再应用一次，
    // 不会在每敲一个字符时都刷一次全局资源）真正把颜色应用到主题，而不只是预览方块。
    private void CustomAccentColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CustomAccentColorPreview == null) return;
        try
        {
            var text = CustomAccentColorBox.Text.Trim();
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(text)!;
            CustomAccentColorPreview.Background = new System.Windows.Media.SolidColorBrush(color);
            if (!_suppressAccentPickerSync) SetAccentPickerColor(color);

            // InitializeComponent/构造函数给文本框回填已保存颜色时也会触发 TextChanged。
            // 旧代码在这里无条件启动 300ms 定时器，导致“刚切到设置页”就再次 ApplyCustomAccent，
            // 即使当前色系根本不是 Custom，也会把全局按钮颜色突然改掉一次。初始化阶段只更新
            // 预览方块，不允许修改全局主题；并且只有当前确实选择“自定义”色系时才做实时预览。
            if (_suppressDirtyTracking) return;
            var selectedSkin = (UiSkinCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            if (!string.Equals(selectedSkin, ThemeService.SkinCustom, StringComparison.Ordinal)) return;

            // 输入到合法颜色即视为已经做出自定义颜色决定；在此之前仅展开面板不算修改。
            CommitCustomThemeSelection();
            _accentApplyDebounceTimer?.Stop();
            _accentApplyDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _accentApplyDebounceTimer.Tick += (_, _) =>
            {
                _accentApplyDebounceTimer!.Stop();
                ThemeService.ApplyCustomAccent(color);
                OnSettingsEdited();
            };
            _accentApplyDebounceTimer.Start();
        }
        catch
        {
            CustomAccentColorPreview.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void UiSkinCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomThemePanelVisibility();
        if (_suppressDirtyTracking || UiSkinCombo?.SelectedItem is not ComboBoxItem { Tag: string selectedSkin }) return;

        var cfg = _owner.ConfigService.Config;
        if (string.Equals(selectedSkin, ThemeService.SkinCustom, StringComparison.Ordinal))
        {
            // 关键修复：点“自定义”仅展开调色区域。若当前保存的主题本来不是 Custom，
            // 此时不要预览/保存/弹回退气泡；等用户真正点色块、使用 RGB 或输入合法颜色后再提交。
            _customThemeSelectionPending = !string.Equals(cfg.UiSkin, ThemeService.SkinCustom, StringComparison.Ordinal);
            if (!_customThemeSelectionPending && !string.IsNullOrWhiteSpace(cfg.CustomAccentColor))
                ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, ThemeService.SkinCustom, cfg.IsDarkMode, cfg.CustomAccentColor);
            return;
        }

        // 从自定义切回任一预设时应当立即能看到预设配色，不能继续残留 Custom 强调色造成
        // “怎么选预设都没切回去”的错觉。真正落盘仍走本页统一保存/自动保存流程。
        _customThemeSelectionPending = false;
        ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, selectedSkin, cfg.IsDarkMode, cfg.CustomAccentColor);

        // 鼠标打开下拉框后，SelectionChanged 可能在 Popup 仍展开、用户还在浏览候选项时先触发。
        // 预览颜色可以立即做，但此时不能进入自动保存/回退提示，否则气泡会打断尚未结束的选择。
        // 真正由鼠标下拉选择时统一等 HookDirtyTracking 里的 DropDownClosed 再判断；键盘切换等
        // 没有打开下拉 Popup 的交互仍可直接记为编辑。
        if (!UiSkinCombo.IsDropDownOpen)
            OnSettingsEdited();
    }

    private void CommitCustomThemeSelection()
    {
        _customThemeSelectionPending = false;
    }

    private void UpdateCustomThemePanelVisibility()
    {
        if (CustomThemeSettingsPanel == null || UiSkinCombo == null) return;
        var selectedSkin = (UiSkinCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        CustomThemeSettingsPanel.Visibility = string.Equals(selectedSkin, ThemeService.SkinCustom, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
            CommitCustomThemeSelection();
            SetAccentPickerColor(color);
            CustomAccentColorBox.Text = hex;
            ThemeService.ApplyCustomAccent(color);
            OnSettingsEdited();
        }
        catch { }
    }

    private void AccentRgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressAccentPickerSync || AccentPickerPreview == null || AccentRText == null || AccentGText == null || AccentBText == null) return;
        var color = System.Windows.Media.Color.FromRgb((byte)AccentRSlider.Value, (byte)AccentGSlider.Value, (byte)AccentBSlider.Value);
        AccentPickerPreview.Background = new System.Windows.Media.SolidColorBrush(color);
        AccentRText.Text = ((int)AccentRSlider.Value).ToString();
        AccentGText.Text = ((int)AccentGSlider.Value).ToString();
        AccentBText.Text = ((int)AccentBSlider.Value).ToString();
    }

    private void SetAccentPickerColor(System.Windows.Media.Color color)
    {
        if (AccentRSlider == null) return;
        _suppressAccentPickerSync = true;
        try
        {
            AccentRSlider.Value = color.R;
            AccentGSlider.Value = color.G;
            AccentBSlider.Value = color.B;
            AccentRText.Text = color.R.ToString();
            AccentGText.Text = color.G.ToString();
            AccentBText.Text = color.B.ToString();
            AccentPickerPreview.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        finally { _suppressAccentPickerSync = false; }
    }

    private void AccentPickerUse_Click(object sender, RoutedEventArgs e)
    {
        var color = System.Windows.Media.Color.FromRgb((byte)AccentRSlider.Value, (byte)AccentGSlider.Value, (byte)AccentBSlider.Value);
        CommitCustomThemeSelection();
        CustomAccentColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ThemeService.ApplyCustomAccent(color);
        OnSettingsEdited();
        ToastService.ShowSuccess("已预览自定义主题颜色，保存设置后会持久生效");
    }

    private void CustomAccentColorApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = CustomAccentColorBox.Text.Trim();
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(text)!;
            CommitCustomThemeSelection();
            ThemeService.ApplyCustomAccent(color);
            OnSettingsEdited();
            ToastService.ShowSuccess("已预览自定义主题颜色，保存设置后会持久生效");
        }
        catch
        {
            MessageBoxDialog.ShowWarning("颜色格式无效，请填写例如 #4C9AFF。", "输入有误");
        }
    }

    private void ImportBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog() != true) return;

        var cfg = _owner.ConfigService.Config;
        var previousPath = cfg.CustomBackgroundImagePath;
        string? copiedPath = null;

        try
        {
            var dir = Path.Combine(App.DataDir, "backgrounds");
            Directory.CreateDirectory(dir);

            // 不再覆盖固定 custom.png：旧实现的 BitmapImage 默认 CacheOption=OnDemand，
            // WPF 会长期持有该文件句柄，下一次 File.Copy(..., overwrite:true) 就会 IOException。
            // 使用带时间戳的新文件名彻底避开旧版本遗留的文件锁，同时 MainWindow 用 OnLoad 读取，
            // 新版本自身也不会继续锁住图片。
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp") ext = ".png";
            copiedPath = Path.Combine(dir, $"custom-{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}");
            File.Copy(dialog.FileName, copiedPath, overwrite: false);

            // 先真正加载到主窗口，成功后再更新路径框和配置。旧代码不检查返回值，
            // SetCustomBackgroundImage 失败时仍然显示“背景图片已导入”，最终就会出现
            // “路径已经选上，但窗口背景完全没有同步”的假成功状态。
            if (!ApplyBackgroundImage(copiedPath))
                throw new InvalidOperationException("图片无法被 WPF 解码或无法应用到主窗口。");

            // 导入背景属于“立即应用并保存”的操作，所以把当前磨砂度一起落盘，避免
            // 用户刚调好 25~100 的效果，重启后却回到旧磨砂度。
            cfg.CustomBackgroundImagePath = copiedPath;
            cfg.CustomBackgroundFrostPercent = Math.Clamp((int)CustomBackgroundFrostSlider.Value, 25, 100);
            _owner.PreviewCustomBackgroundFrost(cfg.CustomBackgroundFrostPercent);
            _owner.ConfigService.Save();
            SetBackgroundPathTextWithoutDirtyTracking(copiedPath);

            CleanupOldImportedBackgrounds(dir, copiedPath);
            ToastService.ShowSuccess("背景图片已应用并保存");
        }
        catch (Exception ex)
        {
            // 任一步失败都恢复到导入前状态，确保“主窗口显示 / 路径框 / config.json”
            // 始终是同一个背景，不留下半成功状态。BitmapImage 使用 OnLoad，不会锁旧文件。
            cfg.CustomBackgroundImagePath = previousPath;
            if (!string.IsNullOrWhiteSpace(previousPath) && File.Exists(previousPath))
                _owner.SetCustomBackgroundImage(previousPath);
            else
                _owner.SetCustomBackgroundImage(null);
            SetBackgroundPathTextWithoutDirtyTracking(previousPath ?? "");

            if (!string.IsNullOrWhiteSpace(copiedPath) &&
                !string.Equals(copiedPath, previousPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(copiedPath); } catch { }
            }

            MessageBoxDialog.ShowError($"导入背景图片失败：{ex.Message}", "背景图片");
        }
    }

    private void ClearBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;
        var previousPath = cfg.CustomBackgroundImagePath;

        try
        {
            if (!_owner.SetCustomBackgroundImage(null)) return;

            cfg.CustomBackgroundImagePath = null;
            _owner.ConfigService.Save();
            SetBackgroundPathTextWithoutDirtyTracking("");
            ToastService.ShowSuccess("窗口背景已清除");
        }
        catch (Exception ex)
        {
            cfg.CustomBackgroundImagePath = previousPath;
            if (!string.IsNullOrWhiteSpace(previousPath) && File.Exists(previousPath))
                _owner.SetCustomBackgroundImage(previousPath);
            SetBackgroundPathTextWithoutDirtyTracking(previousPath ?? "");
            MessageBoxDialog.ShowError($"清除窗口背景失败：{ex.Message}", "背景图片");
        }
    }

    private bool ApplyBackgroundImage(string path)
    {
        if (_owner.SetCustomBackgroundImage(path)) return true;
        ErrorPresenter.LogTechnicalDetail($"应用背景图片失败或文件不存在：{path}");
        return false;
    }

    private void SetBackgroundPathTextWithoutDirtyTracking(string text)
    {
        var old = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try { CustomBackgroundImagePathBox.Text = text; }
        finally { _suppressDirtyTracking = old; }
    }

    private static void CleanupOldImportedBackgrounds(string dir, string currentPath)
    {
        try
        {
            var oldFiles = new DirectoryInfo(dir).GetFiles("custom-*.*")
                .Where(f => !string.Equals(f.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(4);
            foreach (var file in oldFiles)
            {
                try { file.Delete(); } catch { /* 旧版可能仍锁着文件，留到下次再清理 */ }
            }
        }
        catch { }
    }

    private void WinUi3DesignCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressDirtyTracking && WinUi3DesignCheck.IsChecked == true)
            MessageBoxDialog.ShowInfo("WinUI 3 新设计点击「保存设置」后立即生效，不需要重启启动器：" +
                "窗口圆角在 Windows 11 (22000+) 上生效，Windows 10 上会静默跳过；界面字体切换为 Segoe UI Variable，" +
                "在任意受支持的 Windows 版本上都会立即看到变化。", "提示");
    }

    /// <summary>
    /// 低性能模式开关改变时的处理。
    /// 之前这里勾选低性能模式会连带强制取消页面动画/Win11 特效/窗口透明度/全局透明度等一系列
    /// 跟低性能模式并不是同一件事的独立开关，取消勾选时又反过来强制勾回去——用户如果是想要
    /// "关掉可能拖慢帧率的部分效果、但仍然保留自己特意开启的高性能模式动效/云母材质/整窗透明"
    /// 这种组合（比如显卡本身够强、只是想手动关一部分自己不需要的效果），设置页会毫无提示地
    /// 把这些开关替用户改掉，跟 <see cref="HighPerformanceModeCheck"/> 是否勾选完全不受这里
    /// 干预的设计不一致。现在低性能模式只做它名字描述的那件事——关闭页面切换动画，其余视觉效果
    /// （Win11 特效、窗口/整窗透明度、高性能模式动效）完全由用户自己在各自的开关上决定，这里
    /// 只负责提示，不再替用户做选择、也不再在取消勾选时把用户可能本来就没开的效果强行打开。
    /// </summary>
    /// <summary>鼠标滚轮灵敏度拖动时立即预览，持久化仍交给设置页统一保存流程。</summary>
    private void TextOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TextOpacityValueText == null) return;
        var percent = Math.Clamp((int)Math.Round(e.NewValue), 50, 100);
        TextOpacityValueText.Text = $"{percent}%";
        if (!_suppressDirtyTracking) ThemeService.ApplyTextOpacity(percent);
    }

    private void MouseWheelSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MouseWheelSensitivityValueText == null) return;
        var percent = ScrollWheelBehavior.ClampSensitivityPercent((int)Math.Round(e.NewValue));
        MouseWheelSensitivityValueText.Text = $"{percent}%";
        if (_suppressDirtyTracking) return;
        ScrollWheelBehavior.SetSensitivityPercent(percent);
    }

    /// <summary>界面缩放快捷键总开关只做 UI 联动；配置写回统一由 PerformSave 处理。
    /// 关闭时只禁用下面的绑定和缩放控件，不清空用户已选的绑定组合。</summary>
    private void UiZoomEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
        var enabled = UiZoomEnabledCheck.IsChecked == true;
        UiZoomBindingsPanel.IsEnabled = enabled;
        UiZoomLevelPanel.IsEnabled = enabled;
    }

    /// <summary>"Ctrl+滚轮" / "Ctrl+方向键" 两个绑定方式只修改当前 UI，等待统一保存。</summary>
    private void UiZoomBinding_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
    }

    /// <summary>缩放比例滑块拖动时仍实时预览，但不直接持久化；点击保存或自动保存后才写入配置。</summary>
    private void UiZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressDirtyTracking) return;
        int percent = (int)Math.Round(e.NewValue);
        UiZoomService.PreviewPercent(percent);
        UiZoomPercentText.Text = $"{UiZoomService.CurrentPercent}%";
        // 程序点击“重置为 100%”时 Slider 本身没有键盘焦点/鼠标捕获，根级 RangeBase
        // 追踪会忽略它，所以在这里统一补一次编辑通知；用户拖动时重复通知会被防抖合并。
        OnSettingsEdited();
    }

    private void UiZoomReset_Click(object sender, RoutedEventArgs e)
    {
        UiZoomSlider.Value = UiZoomService.DefaultPercent;
        UiZoomService.PreviewPercent(UiZoomService.DefaultPercent);
        UiZoomPercentText.Text = $"{UiZoomService.CurrentPercent}%";
    }

    private void LowPerformanceModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
        if (LowPerformanceModeCheck.IsChecked == true)
        {
            // 只关闭页面切换动画——这是低性能模式名副其实的核心行为，其它开关（Win11 特效、
            // 窗口透明度、全局透明度、高性能模式动效）是否要一起关，交给用户自己决定。
            if (PageAnimationsCheck != null)
                PageAnimationsCheck.IsChecked = false;
            ToastService.ShowInfo("已启用低性能模式：页面切换动画已关闭。云母/亚克力特效、"
                + "透明度、高性能模式动效等其它开关不受影响，仍按你自己的勾选生效");
        }
        else
        {
            ToastService.ShowInfo("已关闭低性能模式：页面切换动画已恢复默认开启");
            if (PageAnimationsCheck != null)
                PageAnimationsCheck.IsChecked = true;
        }
        // 不在这里写 cfg；否则即使用户还没点保存，内存配置也已经被改掉，随后任何其它
        // ConfigService.Save() 都可能把这次未确认修改带到磁盘。统一由 PerformSave 处理。
    }
}
