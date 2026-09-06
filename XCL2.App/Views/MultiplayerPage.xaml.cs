using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 「联机」页：保留陶瓦联机(Terracotta)入口。
/// 红石联机的启动器按钮入口已移除；如果用户需要相应 Mod，可直接在下载中心按名称搜索。
/// 陶瓦联机已经内置在启动器里(EmbeddedResource)，本页面只负责确保内置文件已释放到本地、
/// 一键拉起，以及允许用户手动覆盖成其他版本，不重新实现第三方联机协议。
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

}
