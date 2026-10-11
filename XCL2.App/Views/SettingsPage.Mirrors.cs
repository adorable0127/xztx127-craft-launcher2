using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private void OpenMirrorSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MirrorSettingsDialog(_owner.ConfigService.Config.DownloadMirrors);
        if (OverlayDialogService.ShowModal(dialog) != true) return;
        _owner.ConfigService.Config.DownloadMirrors = dialog.Result;
        _owner.ConfigService.Save();
        DownloadEndpoints.ResetHealth();
        ToastService.ShowSuccess("下载镜像设置已保存，将应用于接下来的下载。");
    }
}
