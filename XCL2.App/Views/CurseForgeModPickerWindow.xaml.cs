using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Mod 文件选择弹窗：展示某个 CurseForge Mod 下所有可下载文件，选中后下载到 mods/。
/// 跟 CurseForgeMapPickerWindow 结构一致，区别只在下载目标是 mods/ 而不是解压到 saves/。</summary>
public partial class CurseForgeModPickerWindow : Window
{
    private readonly CurseForgeService _svc;
    private readonly string _minecraftDir;
    private readonly ObservableCollection<MapFileDisplayItem> _files = new();

    public CurseForgeModPickerWindow(CurseForgeService svc, string minecraftDir, string modName, List<CurseForgeFile> files)
    {
        _svc = svc;
        _minecraftDir = minecraftDir;
        InitializeComponent();

        TitleText.Text = $"「{modName}」的可下载文件";
        FileListBox.ItemsSource = _files;
        foreach (var f in files) _files.Add(new MapFileDisplayItem(f));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not MapFileDisplayItem item) return;

        var progressWin = new ProgressWindow($"正在下载 {item.File.FileName} ...") { Owner = this };
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("下载中", 0, 1, msg)));
            var path = await _svc.DownloadModAsync(_minecraftDir, item.File, progress);
            MessageBox.Show($"Mod 已安装到：\n{path}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
}
