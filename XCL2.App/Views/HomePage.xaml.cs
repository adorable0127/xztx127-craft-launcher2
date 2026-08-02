using System.Windows.Controls;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 首页：磁贴总控台。原本"普通/高手模式"这个状态的唯一读写入口在 SettingsPage
/// （cfg.AdvancedMode，见 AdvancedModeCheck_Changed），现在首页右上角也加了一个同款开关
/// 方便快速切换，两处操作的是同一个配置项、同一份保存逻辑，不是各自独立的状态。
///
/// 「模式设置」（深/浅色）和「自动循环」两个按钮同理：都是直接读写 AppConfig 里对应的
/// 字段，点击立即保存并生效，不需要用户去「设置」页点"保存设置"。
/// </summary>
public partial class HomePage : UserControl
{
    private readonly MainWindow _owner;

    private bool _guestToggleInitializing;
    private bool _modeToggleInitializing;
    private bool _darkModeToggleInitializing;
    private bool _autoThemeToggleInitializing;

    public HomePage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        // 从配置里同步访客模式的当前状态；用 _guestToggleInitializing 避免这一步触发
        // GuestModeToggle_Changed（那里会去改配置+清理会话文件，只有用户手动点击才应该触发）。
        _guestToggleInitializing = true;
        GuestModeToggle.IsChecked = _owner.ConfigService.Config.GuestModeEnabled;
        UpdateGuestModeToggleText();
        _guestToggleInitializing = false;

        RefreshModeToggle();
        RefreshThemeToggles();
    }

    /// <summary>
    /// 从配置里重新读取 cfg.AdvancedMode 并同步到 ModeToggle 的勾选状态/文案，不触发保存。
    /// 除了构造函数第一次调用，还需要在"从设置页切回首页"时调用一次——用户可能刚在设置页
    /// 切换过模式，首页这份按钮的显示不应该是切页面之前的旧状态。见 MainWindow.ShowHome。
    /// </summary>
    public void RefreshModeToggle()
    {
        _modeToggleInitializing = true;
        ModeToggle.IsChecked = _owner.ConfigService.Config.AdvancedMode;
        UpdateModeToggleText();
        _modeToggleInitializing = false;
        RefreshTileOrder();
    }

    /// <summary>
    /// 需求：普通模式下把"启动游戏"和"一键开始游戏"这两个磁贴的位置互换（专家模式不用管，
    /// 保持原来的顺序）——普通模式的用户大多是新手，更需要"一键开始游戏"这种全自动向导，
    /// 换到磁贴总控台第一格（左上角，最先看到、最方便点到的位置）；专家模式用户通常已经
    /// 自己配置好版本/账户，"启动游戏"这个直接启动的磁贴放在第一格更符合专家模式的使用习惯，
    /// 所以专家模式下维持 XAML 里写死的原始顺序不动。
    ///
    /// UniformGrid 按 Children 集合顺序自动填格子，交换顺序只需要把这两个 Button 在
    /// Children 里的索引对调；但两者的 Margin 是跟\"当前所在格子的行列位置\"绑定的
    /// （比如第一格是 Margin="0,0,10,10"，第四格是 Margin="0,0,10,0"，右边距/下边距取决于
    /// 是不是最后一列/最后一行），所以调整索引的同时必须把 Margin 也交换一次，否则两个磁贴
    /// 会带着"错的格子"该有的间距挪到"对的格子"里，视觉上出现多余/缺失的间隙。
    /// 这个方法是幂等的：可以在同一个 HomePage 实例上被多次调用（比如反复来回切模式），
    /// 每次都先用 IndexOf 现查两者当前的实际索引，不依赖"只能交换一次"的假设。
    /// </summary>
    private void RefreshTileOrder()
    {
        var advanced = _owner.ConfigService.Config.AdvancedMode;

        var launchIndex = TileGrid.Children.IndexOf(LaunchTile);
        var quickStartIndex = TileGrid.Children.IndexOf(QuickStartTile);
        if (launchIndex < 0 || quickStartIndex < 0) return;

        // 普通模式：一键开始游戏在前（索引更小）；专家模式：启动游戏在前。
        // 如果当前顺序已经符合目标模式的要求，不用再交换（避免同一模式下重复调用时
        // 把已经交换好的顺序又换回去）。
        var alreadyCorrectOrder = advanced ? launchIndex < quickStartIndex : quickStartIndex < launchIndex;
        if (alreadyCorrectOrder) return;

        var launchMargin = LaunchTile.Margin;
        var quickStartMargin = QuickStartTile.Margin;

        TileGrid.Children.RemoveAt(Math.Max(launchIndex, quickStartIndex));
        TileGrid.Children.RemoveAt(Math.Min(launchIndex, quickStartIndex));

        var firstSlotIndex = Math.Min(launchIndex, quickStartIndex);
        var (first, second) = advanced ? (LaunchTile, QuickStartTile) : (QuickStartTile, LaunchTile);
        TileGrid.Children.Insert(firstSlotIndex, first);
        TileGrid.Children.Insert(firstSlotIndex + 1, second);

        // Margin 跟着各自新占的格子走：原来在前一格的 Margin 现在给挪到前面的那个磁贴用，
        // 原来在后一格的 Margin 给挪到后面的那个磁贴用。
        var frontMargin = launchIndex < quickStartIndex ? launchMargin : quickStartMargin;
        var backMargin = launchIndex < quickStartIndex ? quickStartMargin : launchMargin;
        first.Margin = frontMargin;
        second.Margin = backMargin;
    }

    /// <summary>
    /// 从配置里重新读取 IsDarkMode / AutoThemeCycleEnabled 并同步到「模式设置」/「自动循环」
    /// 两个按钮的勾选状态/文案，不触发保存、不重新应用配色（配色本身由调用方在改配置的同一
    /// 时刻已经调用过 ThemeService.ApplyForCurrentState，这里只是让按钮显示跟上最新状态）。
    /// 除了构造函数，MainWindow 的自动循环定时检查在真正切换了 IsDarkMode 之后也会调用一次，
    /// 保证首页按钮文案跟自动切换的结果保持同步，不会出现"实际已经变成深色模式，按钮却还
    /// 显示浅色"的不一致。
    /// </summary>
    public void RefreshThemeToggles()
    {
        var cfg = _owner.ConfigService.Config;

        _darkModeToggleInitializing = true;
        DarkModeToggle.IsChecked = cfg.IsDarkMode;
        UpdateDarkModeToggleText();
        _darkModeToggleInitializing = false;

        _autoThemeToggleInitializing = true;
        AutoThemeCycleToggle.IsChecked = cfg.AutoThemeCycleEnabled;
        UpdateAutoThemeCycleToggleText();
        _autoThemeToggleInitializing = false;
    }

    private void UpdateModeToggleText()
    {
        ModeToggleText.Text = ModeToggle.IsChecked == true ? "专家模式" : "普通模式";
    }

    /// <summary>
    /// 首页右上角"普通模式/专家模式"开关：点击立即写回 cfg.AdvancedMode 并保存，
    /// 跟「设置」页 AdvancedModeCheck_Changed 是同一个配置项、同一份保存动作——不管从
    /// 哪一边切换，两边下次显示都会是最新状态（这一边不需要额外触发设置页控件显隐刷新，
    /// 那部分逻辑只在设置页自己打开时才需要跑）。
    /// </summary>
    private void ModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_modeToggleInitializing) return;
        _owner.ConfigService.Config.AdvancedMode = ModeToggle.IsChecked == true;
        _owner.ConfigService.Save();
        UpdateModeToggleText();
    }

    private void UpdateDarkModeToggleText()
    {
        var isDark = DarkModeToggle.IsChecked == true;
        DarkModeToggleText.Text = isDark ? "深色模式" : "浅色模式";
        DarkModeToggleIcon.Text = isDark ? "\uD83C\uDF19" : "\u2600"; // 🌙 / ☀
    }

    /// <summary>
    /// 「模式设置」按钮：切换深色/浅色，只影响 cfg.IsDarkMode，不影响 cfg.UiSkin 选的色系
    /// （比如色系选了蓝色，这里点击只是在"蓝色系-浅"和"蓝色系-深"之间切）。点击立即写回
    /// 配置、保存，并调用 ThemeService 重新应用——保存后一秒内必须看到界面刷新，这里直接
    /// 同步调用 Apply，不存在异步延迟。
    ///
    /// 这是用户"手动"点的操作，即使当前「自动循环」是开启状态，也允许临时覆盖显示——
    /// 只是不会关闭自动循环本身，到下一个自动切换时间点，MainWindow 的定时检查还是会
    /// 按计划把 IsDarkMode 重新覆盖回去（见 MainWindow.ReevaluateAutoThemeCycle）。
    /// </summary>
    private void DarkModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_darkModeToggleInitializing) return;
        var cfg = _owner.ConfigService.Config;
        cfg.IsDarkMode = DarkModeToggle.IsChecked == true;
        _owner.ConfigService.Save();
        UpdateDarkModeToggleText();

        ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, cfg.UiSkin, cfg.IsDarkMode);
    }

    private void UpdateAutoThemeCycleToggleText()
    {
        AutoThemeCycleToggleText.Text = AutoThemeCycleToggle.IsChecked == true ? "自动循环：已开启" : "自动循环";
    }

    /// <summary>
    /// 「自动循环」开关：开启后由 MainWindow 的每分钟定时检查根据当前系统时间自动决定
    /// cfg.IsDarkMode；关闭后完全交还给用户手动控制，不再有任何自动覆盖。
    /// 点击立即写回配置、保存；刚打开的瞬间立即校验一次当前时间点应该是哪个模式并应用，
    /// 不需要等到下一次定时检查才生效（保存后一秒内必须看到界面刷新）。
    /// </summary>
    private void AutoThemeCycleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_autoThemeToggleInitializing) return;
        var cfg = _owner.ConfigService.Config;
        cfg.AutoThemeCycleEnabled = AutoThemeCycleToggle.IsChecked == true;
        // 开关状态一变化，"上次自动切换的时间段"记录就作废了：不管是刚打开（需要立即按当前
        // 时间校正一次）还是刚关闭（下次重新打开时不该沿用很久以前的旧记录），都清空。
        cfg.AutoThemeLastAppliedSlotStartHour = null;
        _owner.ConfigService.Save();
        UpdateAutoThemeCycleToggleText();

        _owner.ReevaluateAutoThemeCycle();
    }

    private void UpdateGuestModeToggleText()
    {
        GuestModeToggleText.Text = GuestModeToggle.IsChecked == true ? "访客模式：已开启" : "访客模式";
    }

    /// <summary>
    /// 首页右上角访客模式开关：原来只能在「设置」页勾选/保存才生效，现在挪到首页后
    /// 点击立即生效——直接复用 MainWindow.RefreshGuestModeState 里"创建/清空临时账户 +
    /// 刷新侧边栏"的同一套逻辑，跟设置页保存时触发的是同一个方法，行为完全一致。
    /// 访客模式不影响界面配色（见 ThemeService 类注释：一切以用户当前的色系/明暗选择为准），
    /// 这里不需要额外调用 ThemeService。
    /// </summary>
    private void GuestModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_guestToggleInitializing) return;
        _owner.ConfigService.Config.GuestModeEnabled = GuestModeToggle.IsChecked == true;
        _owner.ConfigService.Save();
        _owner.RefreshGuestModeState();
        UpdateGuestModeToggleText();
    }

    // 磁贴点击：全部转发到 MainWindow 已有的公开导航方法/事件处理方法，不重复实现导航逻辑。
    // "启动游戏"磁贴直接调用 MainWindow.Launch_Click（已改成 public，见 MainWindow.xaml.cs），
    // 复用同一套防手滑冷却状态，跟左下角"启动游戏"按钮完全一致的行为。

    private void TileLaunch_Click(object sender, RoutedEventArgs e) => _owner.Launch_Click(sender, e);

    private void TileAccounts_Click(object sender, RoutedEventArgs e) => _owner.NavigateToAccounts();

    private void TileDownload_Click(object sender, RoutedEventArgs e) => _owner.NavigateToDownloadCenter();

    /// <summary>
    /// 首页磁贴改动：原来这个位置是"Mod 管理"，现在换成"一键开始游戏"（打开
    /// QuickStartWizardWindow，账户/文件夹/版本/加载器/Mod/资源包一步到位）——按用户要求，
    /// 首页一等入口位优先给"一键启动"，Mod 管理仍然可以从「下载中心」进入，不是被删除，
    /// 只是不再单独占首页磁贴位。
    /// </summary>
    private void TileQuickStart_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new QuickStartWizardWindow(_owner) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
    }

    private void TileServerManager_Click(object sender, RoutedEventArgs e) => _owner.NavigateToServerManager();

    private void TileSettings_Click(object sender, RoutedEventArgs e) => _owner.NavigateToSettings();

    /// <summary>
    /// 顶部「🌐 语言」按钮：打开独立的语言选择弹窗（LanguageSelectDialog）。选择后弹窗内部
    /// 自己完成保存+应用+关闭，这里不需要处理返回值或做任何后续刷新——语言切换后的界面
    /// 刷新由 LocalizationService.Apply 里复用的 ThemeService 窗口刷新逻辑统一处理，
    /// 首页这里不用额外调用什么方法。
    /// </summary>
    private void LanguageEntryButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LanguageSelectDialog(_owner.ConfigService);
        OverlayDialogService.ShowModal(dlg);
    }
}
