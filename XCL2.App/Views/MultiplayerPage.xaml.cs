using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 「联机」页：陶瓦联机(Terracotta) + 红石联机 两个国内主流联机方案的入口。
/// 具体集成方式见 TerracottaService 类注释——这两个联机方案的核心逻辑分别在
/// 独立的第三方可执行程序、和游戏内 Mod 里。陶瓦联机已经内置在启动器里(EmbeddedResource)，
/// 不再需要"检测本机是否安装/引导用户去官网下载"这一步，本页面只负责"确保内置文件已释放到本地
/// + 一键拉起 + (可选)让用户手动覆盖成其他版本 + 红石联机一键搜索安装"这层启动器该做的事，
/// 不假装重新实现了它们的联机协议。
/// </summary>
public partial class MultiplayerPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly TerracottaService _terracotta = new();

    public MultiplayerPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        RefreshTerracottaStatus();
    }

    /// <summary>
    /// 陶瓦联机已经内置在启动器里，不再需要"检测本机是否安装"这一步——这里只区分
    /// 两种状态给用户看：用的是内置版本，还是用户之前手动覆盖过的自定义路径。
    /// </summary>
    private void RefreshTerracottaStatus()
    {
        var overridePath = _owner.ConfigService.Config.TerracottaExecutablePath;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            TerracottaStatusText.Text = $"✅ 当前使用手动指定的版本：{overridePath}（点「恢复使用内置版本」可以改回启动器自带的版本）";
        }
        else
        {
            TerracottaStatusText.Text = "✅ 已内置陶瓦联机(0.4.2)，无需下载，点击下方按钮即可直接启动。";
        }
        TerracottaLaunchBtn.IsEnabled = true;
    }

    /// <summary>一键拉起陶瓦联机窗口：建房/加入房间/房间码全部在它自己的界面里完成。
    /// 首次调用时会自动把内置的可执行文件释放到本地数据目录，之后直接复用。</summary>
    private void TerracottaLaunch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _terracotta.Launch(_owner.ConfigService.Config.TerracottaExecutablePath);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("启动陶瓦联机失败，请确认文件没有损坏、且是对应平台(Windows)的可执行文件。",
                $"[启动陶瓦联机失败] {ex}", "启动失败");
        }
    }

    /// <summary>手动选择一个陶瓦联机可执行文件覆盖内置版本：适合以后官方出了新版本、
    /// 内置版本还没来得及更新时，用户自己下载新版本临时替换。</summary>
    private void TerracottaBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择陶瓦联机(Terracotta)可执行文件（用于覆盖内置版本，非必需）",
            Filter = "可执行文件|*.exe|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        _owner.ConfigService.Config.TerracottaExecutablePath = dialog.FileName;
        _owner.ConfigService.Save();
        RefreshTerracottaStatus();
    }

    /// <summary>清除手动覆盖路径，恢复使用内置版本。</summary>
    private void TerracottaResetToBuiltin_Click(object sender, RoutedEventArgs e)
    {
        _owner.ConfigService.Config.TerracottaExecutablePath = null;
        _owner.ConfigService.Save();
        RefreshTerracottaStatus();
    }

    /// <summary>跳转到下载中心的「Mod」分类，并预填搜索关键词"红石联机"——复用现成的
    /// Modrinth 综合搜索 + 一键安装逻辑，不重新写一套下载流程。</summary>
    private void SearchRedstoneMod_Click(object sender, RoutedEventArgs e)
    {
        _owner.NavigateToDownloadCenterWithModSearch("红石联机");
    }
}
