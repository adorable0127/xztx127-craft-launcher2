using System.Windows.Controls;
using System.Windows;

namespace XCL2.App.Views;

/// <summary>
/// 首页：磁贴总控台。不再持有"普通/高手模式"这个状态——那是全局配置项
/// （cfg.AdvancedMode），唯一的读写入口已经搬到 SettingsPage（见 SettingsPage.xaml.cs 的
/// AdvancedModeCheck_Changed），首页只是把常用功能做成磁贴入口，点哪个就跳到哪个页面/
/// 触发哪个动作，本身不存储/不修改任何配置。
/// </summary>
public partial class HomePage : UserControl
{
    private readonly MainWindow _owner;

    private bool _guestToggleInitializing;

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
    }

    private void UpdateGuestModeToggleText()
    {
        GuestModeToggleText.Text = GuestModeToggle.IsChecked == true ? "访客模式：已开启" : "访客模式";
    }

    /// <summary>
    /// 首页右上角访客模式开关：原来只能在「设置」页勾选/保存才生效，现在挪到首页后
    /// 点击立即生效——直接复用 MainWindow.RefreshGuestModeState 里"创建/清空临时账户 +
    /// 刷新侧边栏"的同一套逻辑，跟设置页保存时触发的是同一个方法，行为完全一致。
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
}
