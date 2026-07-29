using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>版本选择弹窗：展示某个 Modrinth 项目下所有可下载版本，选中后下载安装到指定目录。</summary>
public partial class ModrinthVersionPickerWindow : Window
{
    private readonly ModrinthService _svc;
    private readonly string _minecraftDir;
    private readonly ModrinthResourceType _type;
    private readonly ObservableCollection<VersionDisplayItem> _versions = new();

    public ModrinthVersionPickerWindow(ModrinthService svc, string minecraftDir, ModrinthResourceType type,
        string projectTitle, List<ModrinthVersion> versions, List<string> saveNames)
    {
        _svc = svc;
        _minecraftDir = minecraftDir;
        _type = type;
        InitializeComponent();

        TitleText.Text = $"「{projectTitle}」的可下载版本";
        VersionListBox.ItemsSource = _versions;
        foreach (var v in versions) _versions.Add(new VersionDisplayItem(v));

        if (type == ModrinthResourceType.DataPack)
        {
            SavePickerPanel.Visibility = Visibility.Visible;
            SaveCombo.ItemsSource = saveNames;
            if (saveNames.Count > 0) SaveCombo.SelectedIndex = 0;
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VersionDisplayItem item) return;

        string? saveName = null;
        if (_type == ModrinthResourceType.DataPack)
        {
            saveName = SaveCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(saveName))
            {
                MessageBox.Show("请先选择要安装到哪个存档（数据包必须放进具体存档才会生效）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var progressWin = new ProgressWindow($"正在下载 {item.Version.Name} ...") { Owner = this };
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("下载中", 0, 1, msg)));
            var path = await _svc.DownloadResourceAsync(_minecraftDir, _type, item.Version, progress, saveName);
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

public class VersionDisplayItem
{
    public ModrinthVersion Version { get; }
    public string Name => string.IsNullOrEmpty(Version.Name) ? Version.VersionNumber : Version.Name;
    public string VersionNumber => Version.VersionNumber;
    public string GameVersionsText => string.Join(", ", Version.GameVersions.Take(6)) + (Version.GameVersions.Count > 6 ? " ..." : "");

    public VersionDisplayItem(ModrinthVersion version) => Version = version;
}
