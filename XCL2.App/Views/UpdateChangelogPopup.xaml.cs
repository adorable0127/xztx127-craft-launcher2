using System;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 更新完成后自动弹出的"这次更新了什么"内嵌变更日志弹窗。见 xaml 头部注释。
/// </summary>
public partial class UpdateChangelogPopup : OverlayDialogControl
{
    private readonly MainWindow _owner;

    public UpdateChangelogPopup(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        SubtitleText.Text = $"XCL 已更新到最新版本，以下是版本 {GitHubReleaseNotesService.CurrentVersionString()} 的更新内容：";
        Loaded += async (_, _) => await LoadNotesAsync();
    }

    private async System.Threading.Tasks.Task LoadNotesAsync()
    {
        try
        {
            var entry = await GitHubReleaseNotesService.FetchCurrentVersionNotesAsync();
            NotesText.Text = entry == null
                ? "GitHub 上暂未找到与当前程序版本一致的 Release 说明，可以稍后在「XCL 更新日志」里手动查看。"
                : entry.Body;
        }
        catch (Exception ex)
        {
            NotesText.Text = "获取更新日志失败（可能是网络问题或 GitHub API 限流）：" + ex.Message +
                              "\n可以稍后在「XCL 更新日志」面板里重试。";
        }
    }

    private void Ack_Click(object sender, RoutedEventArgs e) => Close();

    private void DontShowAgain_Click(object sender, RoutedEventArgs e)
    {
        // 只关掉"自动弹出"这一个开关，「XCL 更新日志」入口本身仍然随时可以手动打开查看。
        _owner.ConfigService.Config.ShowUpdateChangelogPopup = false;
        _owner.ConfigService.Save();
        Close();
    }
}
