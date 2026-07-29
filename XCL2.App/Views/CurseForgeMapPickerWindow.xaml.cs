using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>地图文件选择弹窗：展示某个 CurseForge 地图下所有可下载文件，选中后下载并解压到 saves/。</summary>
public partial class CurseForgeMapPickerWindow : Window
{
    private readonly CurseForgeService _svc;
    private readonly string _minecraftDir;
    private readonly ObservableCollection<MapFileDisplayItem> _files = new();

    public CurseForgeMapPickerWindow(CurseForgeService svc, string minecraftDir, string mapName, List<CurseForgeFile> files)
    {
        _svc = svc;
        _minecraftDir = minecraftDir;
        InitializeComponent();

        TitleText.Text = $"「{mapName}」的可下载文件";
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
            var path = await _svc.DownloadMapAsync(_minecraftDir, item.File, progress);
            MessageBox.Show($"地图已安装到：\n{path}\n\n启动游戏后应该能在存档列表看到。", "成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
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

public class MapFileDisplayItem
{
    public CurseForgeFile File { get; }
    public string DisplayName => string.IsNullOrEmpty(File.DisplayName) ? File.FileName : File.DisplayName;
    public string FileName => File.FileName;
    public string GameVersionsText => string.Join(", ", File.GameVersions.Take(6)) + (File.GameVersions.Count > 6 ? " ..." : "");

    public MapFileDisplayItem(CurseForgeFile file) => File = file;
}
