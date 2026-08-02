using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        // 自动循环的两个小时下拉框：0~23 全部可选，内容同样在这里现填。
        for (var hour = 0; hour <= 23; hour++)
        {
            AutoThemeLightStartHourCombo.Items.Add(new ComboBoxItem { Content = $"{hour:00}:00", Tag = hour });
            AutoThemeDarkStartHourCombo.Items.Add(new ComboBoxItem { Content = $"{hour:00}:00", Tag = hour });
        }
        SelectComboByTag(AutoThemeLightStartHourCombo, cfg.AutoThemeLightStartHour);
        SelectComboByTag(AutoThemeDarkStartHourCombo, cfg.AutoThemeDarkStartHour);

        SkinApiRootBox.Text = cfg.SkinApiRoot;

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
        DownloadJavaBtn.Content = advanced ? "按上方设置下载 Java" : "按上方版本下载 Java";
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
        var wizard = new FirstRunWizardWindow(_owner) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        // 向导跑完可能改了游戏文件夹/语言等设置，重新加载这个页面的显示值，
        // 避免用户看到的还是打开向导之前的旧值。
        SelectComboByTag(GameLanguageCombo, _owner.ConfigService.Config.GameLanguage);
        GameVersionTypeLabelBox.Text = _owner.ConfigService.Config.GameVersionTypeLabel;
        StatusText.Text = "新手引导已完成，相关设置已自动刷新。";
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
            ErrorPresenter.ShowFriendlyError("下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[下载失败] {ex}", "下载失败");
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
        StatusText.Text = "正在自动探测本机 Java...";

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
            MessageBoxDialog.ShowError("自动探测失败：\n" + ex.Message);
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
        var textProgress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("全盘扫描 Java", 0, 0, msg)));

        try
        {
            var candidates = await javaService.ScanWholeDiskForJavaAsync(textProgress, cts.Token);
            progressWin.Close();

            if (candidates.Count == 0)
            {
                MessageBoxDialog.ShowInfo("扫描完成，没有在本机磁盘上找到任何 javaw.exe。", "全盘扫描结果");
                return;
            }

            var picker = new JavaCandidatePickerWindow(candidates) { Owner = Window.GetWindow(this) };
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

    private void Save_Click(object sender, RoutedEventArgs e)
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

        StatusText.Text = "设置已保存。";
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
}


