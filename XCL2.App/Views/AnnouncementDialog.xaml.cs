using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>启动公告弹窗，见 AnnouncementDialog.xaml 顶部注释 / AnnouncementService 类注释。</summary>
public partial class AnnouncementDialog : OverlayDialogControl
{
    public AnnouncementDialog(IReadOnlyList<AnnouncementService.Announcement> announcements)
    {
        InitializeComponent();
        AnnouncementList.ItemsSource = announcements;
    }

    /// <summary>点"我知道了"：CloseWith(true) 让调用方(MainWindow)知道这批公告已经被用户
    /// 确认过，可以调用 AnnouncementService.MarkSeen 写入配置。点关闭/Esc/点遮罩则结果是
    /// false/null，调用方不会标记已读，下次启动继续弹出。</summary>
    private void Confirm_Click(object sender, RoutedEventArgs e) => CloseWith(true);
}
