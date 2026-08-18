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

    /// <summary>供 MainWindow.SetMainContent 在切页前查询："当前设置页是否有未保存的改动"。
    /// 只有非自动保存模式下才会变成 true——自动保存模式下每次改动都会立即落盘，
    /// 不存在"未保存"这个状态。</summary>
    public bool HasUnsavedChanges => _hasUnsavedChanges;

    public SettingsPage(MainWindow owner)
    {
        _owner = owner; // 统一先于 InitializeComponent 赋值，避免控件初始化时触发的事件访问到未赋值字段
        InitializeComponent();
        var cfg = _owner.ConfigService.Config;

        MinMemBox.Text = cfg.MinMemoryMb.ToString();
        MaxMemBox.Text = cfg.MaxMemoryMb.ToString();
        WidthBox.Text = cfg.WindowWidth.ToString();
        HeightBox.Text = cfg.WindowHeight.ToString();
        SourceCombo.SelectedIndex = cfg.Source == DownloadSource.Official ? 1 : 0;
        SelectComboByTag(GameLanguageCombo, cfg.GameLanguage);
        GameVersionTypeLabelBox.Text = cfg.GameVersionTypeLabel;
        PageAnimationsCheck.IsChecked = cfg.EnablePageAnimations;
        InjectionScanCheck.IsChecked = cfg.EnableInjectionScan;
        GameConsoleWindowCheck.IsChecked = cfg.EnableGameConsoleWindow;
        ShowModIconsCheck.IsChecked = cfg.ShowModIcons;
        ShowServerNetworkGuideCheck.IsChecked = cfg.ShowServerNetworkGuideOnStart;
        IsolateVersionsCheck.IsChecked = cfg.IsolateVersionsByDefault;

        // 外观与视觉效果：Win11 新光效 + 窗口透明度，均默认关闭，见 AppConfig 对应字段注释。
        Win11EffectsCheck.IsChecked = cfg.EnableWin11VisualEffects;
        SelectComboByTag(BackdropMaterialCombo, cfg.Win11BackdropMaterial);
        if (BackdropMaterialCombo.SelectedItem == null) BackdropMaterialCombo.SelectedIndex = 0; // 兜底：旧配置没有这一项时默认选中"云母 Mica"
        BackdropMaterialPanel.IsEnabled = cfg.EnableWin11VisualEffects;
        WindowTransparencyCheck.IsChecked = cfg.EnableWindowTransparency;
        WindowOpacitySlider.Value = cfg.WindowOpacityPercent;
        WindowOpacityValueText.Text = $"{cfg.WindowOpacityPercent}%";
        WindowOpacitySlider.IsEnabled = cfg.EnableWindowTransparency;
        GlobalWindowTransparencyCheck.IsChecked = cfg.EnableGlobalWindowTransparency;
        GlobalWindowOpacitySlider.Value = cfg.GlobalWindowOpacityPercent;
        GlobalWindowOpacityValueText.Text = $"{cfg.GlobalWindowOpacityPercent}%";
        GlobalWindowOpacitySlider.IsEnabled = cfg.EnableGlobalWindowTransparency;

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

        CustomJvmArgsBox.Text = cfg.CustomJvmArgs ?? "";

        AdvancedModeCheck.IsChecked = cfg.AdvancedMode; // 与主页的"普通模式/高手模式"开关共享同一个配置项，两边保持同步
        UpdateAdvancedVisibility();

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

        AccountTokenGraceDaysBox.Text = cfg.AccountTokenGracePeriodDays.ToString();
        UseMachineWideRegistryCheck.IsChecked = cfg.UseMachineWideRegistry;
        RefreshRegistryStatusText();

        // 功能隐藏：绑定分组数据源，再按已保存的 HiddenFeatureKeys 逐个勾选。
        // 用 Loaded 事件而不是构造函数里直接遍历，是因为此时 ItemsControl 的
        // 容器（每个 CheckBox）还没真正生成，直接找子控件会全部落空。
        FeatureHideList.ItemsSource = FeatureVisibilityService.Groups;
        Loaded += (_, _) => InitFeatureHideChecks(cfg);

        RefreshJavaList();

        // 需求：启动器在启动时(这里指打开设置页时)自动刷新一次 Java 列表，不需要用户每次都手动点
        // "刷新（自动探测）"按钮才能发现新装的 Java。复用同一份 QuickDetectJavaAsync 逻辑
        // （已经在读取候选时把 AppData 目录也纳入扫描范围，见 JavaService 的改动），
        // 静默合并新探测到的候选，不弹确认框、不因为探测失败而报错打扰用户——
        // 这只是锦上添花的自动填充，失败了大不了跟以前一样，用户还能手动点按钮。
        _ = AutoDetectJavaOnLoadAsync();

        // "是否可以直接保存，无需点击保存设置"：见 AppConfig.SettingsAutoSaveWithoutConfirm 注释。
        SettingsAutoSaveCheck.IsChecked = cfg.SettingsAutoSaveWithoutConfirm;

        // 所有加载初始值的代码到这里结束，之后任何控件值变化都应该视为"用户真的动了一下"，
        // 从这里开始挂编辑追踪、并放开 _suppressDirtyTracking。
        HookDirtyTracking();
        _suppressDirtyTracking = false;
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
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => OnSettingsEdited()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => OnSettingsEdited()));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((sender, e) =>
        {
            // 关键修复：ComboBox 下拉箭头在内部也是一个 ToggleButton，展开/收起下拉列表
            // （包括仅仅点开看一眼、不选任何新项）都会触发 Checked/Unchecked 并冒泡到这里，
            // 被误判成"用户改了一个设置"从而弹出确认/自动保存气泡。真正的设置项永远是
            // CheckBox/RadioButton，不会是 ComboBox 内部结构，这里按事件源类型过滤掉。
            if (e.OriginalSource is not System.Windows.Controls.CheckBox and not System.Windows.Controls.RadioButton) return;
            OnSettingsEdited();
        }));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((sender, e) =>
        {
            if (e.OriginalSource is not System.Windows.Controls.CheckBox and not System.Windows.Controls.RadioButton) return;
            OnSettingsEdited();
        }));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((sender, e) =>
        {
            // 关键修复："一动页面就弹提示"的真正原因：本页内容放在 ScrollViewer 里，
            // 它内部的滚动条本身也是一个 RangeBase，滚动页面时会不停触发 ValueChanged
            // 并冒泡到这里，被误判成"用户改了一个设置"。只有真正的设置控件（Slider）
            // 才应该算作编辑，滚动条（ScrollBar）产生的事件必须过滤掉。
            if (e.OriginalSource is System.Windows.Controls.Primitives.ScrollBar) return;
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

        _editDebounceTimer?.Stop();
        _editDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _editDebounceTimer.Tick += (_, _) =>
        {
            _editDebounceTimer!.Stop();
            HandleDebouncedEdit();
        };
        _editDebounceTimer.Start();
    }

    private void HandleDebouncedEdit()
    {
        var cfg = _owner.ConfigService.Config;

        if (cfg.SettingsAutoSaveWithoutConfirm)
        {
            // 自动保存分支：先把保存前的完整配置快照下来（供"回退"按钮用），
            // 再走跟点击"保存设置"按钮完全相同的落盘逻辑，最后弹气泡告知结果。
            _preAutoSaveSnapshotJson = System.Text.Json.JsonSerializer.Serialize(cfg);
            PerformSave();
            _hasUnsavedChanges = false;

            ToastService.ShowActionPrompt(
                "设置已保存", "回退", RollbackAutoSave,
                hint: "点击回退可撤销这一次自动保存",
                autoDismissSeconds: 2, key: "settings-autosave");
        }
        else
        {
            _hasUnsavedChanges = true;

            ToastService.ShowActionPrompt(
                "设置已修改，是否保存？", "保存", () => PerformSave(),
                "撤销", DiscardChanges,
                autoDismissSeconds: 2, key: "settings-dirty");
        }
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
        ApplyAllVisualEffectsFromConfig();
        _owner.NavigateToSettings();
        ToastService.ShowInfo("已回退到上一次自动保存之前的设置。");
    }

    /// <summary>"撤销"按钮（非自动保存分支）：这次编辑还没落盘，cfg 里的字段仍然是改动前的值，
    /// 只需要把设置页整页重新打开一次，界面控件就会重新从 cfg 读到没被改动过的旧值，
    /// 不需要额外维护一份"逐控件原始值"的映射。</summary>
    private void DiscardChanges()
    {
        _hasUnsavedChanges = false;
        _owner.NavigateToSettings();
    }

    /// <summary>回退自动保存之后，跟 Save_Click 结尾同样需要重新应用一遍视觉相关的效果
    /// （配色/透明度/Win11 特效），避免"配置文件已经回退了，但当前已打开窗口的画面
    /// 还停留在回退前的样子"这种不同步。</summary>
    private void ApplyAllVisualEffectsFromConfig()
    {
        var cfg = _owner.ConfigService.Config;
        ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, cfg.UiSkin, cfg.IsDarkMode);
        ThemeService.ApplyWindowTransparency(cfg.EnableWindowTransparency, cfg.WindowOpacityPercent);
        ThemeService.ApplyGlobalWindowTransparency(cfg.EnableGlobalWindowTransparency, cfg.GlobalWindowOpacityPercent);
        var material = Enum.TryParse<Win11EffectsService.BackdropMaterial>(cfg.Win11BackdropMaterial, out var m)
            ? m : Win11EffectsService.BackdropMaterial.Mica;
        Win11EffectsService.SetEnabled(cfg.EnableWin11VisualEffects, material);
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
                RefreshJavaList();
                StatusText.Text = $"已自动探测到 {added} 个新 Java 并加入列表。";
            }
        }
        catch { /* 静默失败：这是打开设置页时的自动锦上添花操作，不应该弹窗打扰用户 */ }
    }

    /// <summary>供 MainWindow.ScanJavaInBackgroundAsync 在启动时静默扫描完成后调用：
    /// 如果用户当前正好停留在「设置」页，让新登记的 Java 立刻反映到列表框里，
    /// 不需要用户手动切出去再切回来才能看到。RefreshJavaList 本身保持 private，
    /// 只加这一层公开转发，避免把内部刷新细节暴露给外部随意调用。</summary>
    public void RefreshJavaListPublic() => RefreshJavaList();

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

    /// <summary>功能隐藏面板里任意一个 CheckBox 勾选状态变化时，同步写回配置的
    /// HiddenFeatureKeys 列表。不在这里立即保存到磁盘——跟页面其它设置一样，
    /// 统一等用户点"保存设置"（Save_Click）才落盘，避免每点一下就触发一次 IO。</summary>
    private void FeatureHideCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key } checkBox) return;
        var cfg = _owner.ConfigService.Config;
        var isHidden = checkBox.IsChecked == true;
        if (isHidden && !cfg.HiddenFeatureKeys.Contains(key)) cfg.HiddenFeatureKeys.Add(key);
        else if (!isHidden) cfg.HiddenFeatureKeys.Remove(key);
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
        CustomJvmArgsPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        DownloadJavaBtn.Content = advanced ? Loc.T("Str_Cs_Download_Java_Using_The_Settings_Above", "按上方设置下载 Java") : Loc.T("Str_Cs_Download_The_Java_Version_Selected_Above", "按上方版本下载 Java");
        AdvancedModeHintText.Text = advanced
            ? "已切换到高手模式：本页会显示 Java 版本/架构/安装方式、自定义启动参数等高级选项，左侧「日志」页也建议勾选显示日志面板。"
            : "当前是普通模式：启动器只展示必要的选项，Java 会自动探测/下载推荐版本，无需任何手动配置。";
    }

    /// <summary>
    /// 勾选/取消勾选立即写回 cfg.AdvancedMode 并保存——这是现在全局唯一的模式切换入口
    /// （原来首页也有一份单独的开关，写回逻辑重复了一份，现在合并到这一处，首页改成纯展示磁贴，
    /// 不再持有任何模式状态）。构造函数里第一次设置 AdvancedModeCheck.IsChecked（用来回填
    /// 已有配置）也会触发这个事件——这里直接把"当前 checkbox 状态"写回配置本身是幂等操作，
    /// 构造阶段触发一次不会产生任何实际变化，不需要额外加抑制标志位。
    /// </summary>
    private void AdvancedModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        _owner.ConfigService.Config.AdvancedMode = AdvancedModeCheck.IsChecked == true;
        _owner.ConfigService.Save();
        UpdateAdvancedVisibility();
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

    /// <summary>供 MainWindow 在"切换页面时有未保存改动"的三选一确认里选了"是"时调用，
    /// 跟点击"保存设置"按钮走的是同一套 PerformSave 逻辑。</summary>
    public void SaveNow() => PerformSave();

    /// <summary>实际的保存逻辑，从原来的 Save_Click 里抽出来，供"保存设置"按钮点击、
    /// 以及编辑追踪的自动保存/气泡"保存"按钮共用同一套逻辑，不用维护两份。</summary>
    private void PerformSave()
    {
        // 注意：cfg.JavaPath 这个字段本身没有删除（仍然被 FindJava 当兜底路径使用，
        // MainWindow/FirstRunWizardWindow 等处在自动探测/下载成功后会自动写入它），
        // 但本页已经去掉了对应的独立输入框（见 Java 列表区块的说明），所以这里不再读取
        // 一个不存在的控件去覆盖它——保存设置不应该把这个字段清空或改动，交给别处的
        // 自动探测/下载逻辑维护即可。
        var cfg = _owner.ConfigService.Config;
        cfg.MinMemoryMb = int.TryParse(MinMemBox.Text, out var min) ? min : cfg.MinMemoryMb;
        cfg.MaxMemoryMb = int.TryParse(MaxMemBox.Text, out var max) ? max : cfg.MaxMemoryMb;
        cfg.WindowWidth = int.TryParse(WidthBox.Text, out var w) ? w : cfg.WindowWidth;
        cfg.WindowHeight = int.TryParse(HeightBox.Text, out var h) ? h : cfg.WindowHeight;
        cfg.Source = SourceCombo.SelectedIndex == 1 ? DownloadSource.Official : DownloadSource.BMCLAPI;
        if ((GameLanguageCombo.SelectedItem as ComboBoxItem)?.Tag is string lang) cfg.GameLanguage = lang;
        cfg.GameVersionTypeLabel = GameVersionTypeLabelBox.Text?.Trim() ?? "";
        cfg.EnablePageAnimations = PageAnimationsCheck.IsChecked == true;
        cfg.EnableInjectionScan = InjectionScanCheck.IsChecked == true;
        cfg.EnableGameConsoleWindow = GameConsoleWindowCheck.IsChecked == true;
        cfg.ShowModIcons = ShowModIconsCheck.IsChecked == true;
        cfg.ShowServerNetworkGuideOnStart = ShowServerNetworkGuideCheck.IsChecked == true;
        cfg.IsolateVersionsByDefault = IsolateVersionsCheck.IsChecked == true;

        cfg.EnableWin11VisualEffects = Win11EffectsCheck.IsChecked == true;
        cfg.Win11BackdropMaterial = (BackdropMaterialCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Mica";
        cfg.EnableWindowTransparency = WindowTransparencyCheck.IsChecked == true;
        cfg.WindowOpacityPercent = (int)WindowOpacitySlider.Value;
        // 跟其它设置不同，这两项一保存就应该立刻能在已打开的窗口上看到效果，不用重启/切页——
        // 用户在设置页调滑块本来就是想马上比对效果，见 ThemeService.ApplyWindowTransparency
        // 与 Win11EffectsService.SetEnabled 类注释。
        cfg.EnableGlobalWindowTransparency = GlobalWindowTransparencyCheck.IsChecked == true;
        cfg.GlobalWindowOpacityPercent = (int)GlobalWindowOpacitySlider.Value;

        ThemeService.ApplyWindowTransparency(cfg.EnableWindowTransparency, cfg.WindowOpacityPercent);
        ThemeService.ApplyGlobalWindowTransparency(cfg.EnableGlobalWindowTransparency, cfg.GlobalWindowOpacityPercent);
        var material = Enum.TryParse<Win11EffectsService.BackdropMaterial>(cfg.Win11BackdropMaterial, out var m)
            ? m : Win11EffectsService.BackdropMaterial.Mica;
        Win11EffectsService.SetEnabled(cfg.EnableWin11VisualEffects, material);

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

        cfg.SelectedJavaId = (DefaultJavaCombo.SelectedItem as JavaListItem)?.Entry?.Id;

        // 访客模式：勾选状态直接写回配置。实际"用临时账户替换真实账户"的逻辑在 MainWindow 里
        // （见 MainWindow.RefreshGuestModeState），这里只负责持久化这个开关本身，跟其它设置项
        // 一样要点"保存设置"才生效——避免访客模式这种影响账户行为的开关也搞成"勾选就立即生效"，
        // 那样用户在这个页面里勾选的瞬间还没保存，SidebarText 却已经变了，容易造成"状态不一致"的困惑。
        var guestModeChanged = cfg.GuestModeEnabled != (GuestModeCheck.IsChecked == true);
        cfg.GuestModeEnabled = GuestModeCheck.IsChecked == true;

        // 配色皮肤：同样只是先写回配置，实际应用画刷的动作跟访客模式共用下面
        // RefreshGuestModeState/ThemeService.ApplyForCurrentState 那一次调用，
        // 不需要在这里单独再调一次 ThemeService，避免访客模式和皮肤同时变化时重复刷新两次。
        var uiSkinChanged = cfg.UiSkin != ((UiSkinCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? cfg.UiSkin);
        if ((UiSkinCombo.SelectedItem as ComboBoxItem)?.Tag is string selectedSkin)
            cfg.UiSkin = selectedSkin;

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

        // 自定义 JVM 参数：保存前先做一次跟启动时同样的切分校验，
        // 提前把"引号没闭合"这类明显错误拦在设置页，而不是等到真正启动游戏那一刻才发现。
        var customJvmArgsRaw = CustomJvmArgsBox.Text?.Trim();
        if (string.IsNullOrEmpty(customJvmArgsRaw))
        {
            cfg.CustomJvmArgs = null;
        }
        else
        {
            try
            {
                LauncherService.SplitArgsRespectingQuotes(customJvmArgsRaw);
                cfg.CustomJvmArgs = customJvmArgsRaw;
            }
            catch (Exception ex)
            {
                MessageBoxDialog.ShowWarning(
                    $"自定义 Java 启动参数格式有误，未保存这一项（其余设置已正常保存）：\n{ex.Message}",
                    "自定义启动参数格式错误");
            }
        }

        if (int.TryParse(AccountTokenGraceDaysBox.Text, out var graceDays))
            cfg.AccountTokenGracePeriodDays = Math.Max(0, graceDays);
        cfg.UseMachineWideRegistry = UseMachineWideRegistryCheck.IsChecked == true;
        cfg.SettingsAutoSaveWithoutConfirm = SettingsAutoSaveCheck.IsChecked == true;

        _owner.ConfigService.Save();

        // 访客模式开关状态发生变化时，让 MainWindow 立即重新计算"当前应该用哪个账户"
        // （开启时切到临时访客账户，关闭时切回真实保存的账户），并刷新侧边栏显示，
        // 不需要用户重启启动器才能看到效果。
        // 访客模式开关变化 或 皮肤选择变化，任一发生都需要重算当前应该显示的配色——
        // RefreshGuestModeState 内部已经会调用 ThemeService.ApplyForCurrentState，
        // 两个条件合并只调一次，避免访客模式没变但只改了皮肤时画面没反应。
        if (guestModeChanged || uiSkinChanged) _owner.RefreshGuestModeState();
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
    /// "进入实验性功能"入口：第一次点击（cfg.ExperimentalFeaturesUnlocked 还是 false）会先弹
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

    /// <summary>Win11 视觉效果 / 窗口透明度两个 CheckBox 共用的处理器：这里只做纯界面联动
    /// （窗口透明度关闭时把下面的透明度滑块一并禁用，避免用户以为拖了滑块但其实没生效），
    /// 不在这里直接落盘/应用效果——跟本页其它设置一样，改动先留在界面上，统一交给
    /// "保存设置"按钮（Save_Click）落盘并立即应用。InitializeComponent 阶段设置初始
    /// IsChecked 也会触发这个事件，但此时 WindowOpacitySlider 已经在 XAML 里声明好，
    /// 直接读取不会有空引用问题。</summary>
    private void VisualEffectsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (WindowOpacitySlider == null) return; // InitializeComponent 尚未跑完时的极早期事件，忽略
        WindowOpacitySlider.IsEnabled = WindowTransparencyCheck.IsChecked == true;
        GlobalWindowOpacitySlider.IsEnabled = GlobalWindowTransparencyCheck.IsChecked == true;
        if (BackdropMaterialPanel != null) BackdropMaterialPanel.IsEnabled = Win11EffectsCheck.IsChecked == true;
        ApplyAquaticLockIfNeeded();
    }

    /// <summary>透明度滑块拖动时只更新旁边的百分比文字，实际生效同样要等点"保存设置"。</summary>
    private void WindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WindowOpacityValueText == null) return;
        WindowOpacityValueText.Text = $"{(int)e.NewValue}%";
    }

    /// <summary>整窗全局透明度滑块拖动时只更新旁边的百分比文字，同样要等"保存设置"才生效。</summary>
    private void GlobalWindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlobalWindowOpacityValueText == null) return;
        GlobalWindowOpacityValueText.Text = $"{(int)e.NewValue}%";
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

}


