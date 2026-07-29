using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 材质包/光影包/数据包文件选择弹窗（CurseForge 来源）：展示某个 CurseForge 项目下所有可下载文件，
/// 选中后下载到对应目录。结构跟 CurseForgeModPickerWindow 一致，只是多了数据包的"选存档"这一步——
/// 这部分照抄 ModrinthVersionPickerWindow 对 DataPack 的处理方式，保持两条来源的交互一致。
/// </summary>
public partial class CurseForgeResourcePickerWindow : Window
{
    private readonly CurseForgeService _svc;
    private readonly string _minecraftDir;
    private readonly CurseForgeResourceKind _kind;
    private readonly ObservableCollection<MapFileDisplayItem> _files = new();

    public CurseForgeResourcePickerWindow(CurseForgeService svc, string minecraftDir, CurseForgeResourceKind kind,
        string resourceName, List<CurseForgeFile> files, List<string> saveNames)
    {
        _svc = svc;
        _minecraftDir = minecraftDir;
        _kind = kind;
        InitializeComponent();

        TitleText.Text = $"「{resourceName}」的可下载文件";
        FileListBox.ItemsSource = _files;
        foreach (var f in files) _files.Add(new MapFileDisplayItem(f));

        if (kind == CurseForgeResourceKind.DataPack)
        {
            SavePickerPanel.Visibility = Visibility.Visible;
            SaveCombo.ItemsSource = saveNames;
            if (saveNames.Count > 0) SaveCombo.SelectedIndex = 0;
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not MapFileDisplayItem item) return;

        string? saveName = null;
        if (_kind == CurseForgeResourceKind.DataPack)
        {
            saveName = SaveCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(saveName))
            {
                MessageBox.Show("请先选择要安装到哪个存档（数据包必须放进具体存档才会生效）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var progressWin = new ProgressWindow($"正在下载 {item.File.FileName} ...") { Owner = this };
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("下载中", 0, 1, msg)));
            var path = await _svc.DownloadResourceAsync(_minecraftDir, _kind, item.File, progress, saveName);
            MessageBox.Show($"下载完成：\n{path}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
