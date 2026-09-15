using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>启动公告弹窗，见 AnnouncementDialog.xaml 顶部注释 / AnnouncementService 类注释。</summary>
public partial class AnnouncementDialog : OverlayDialogControl
{
    /// <summary>某条公告的行内操作按钮被点击（目前只有简洁模式公告用到）。参数是
    /// AnnouncementService.Announcement.ActionKey。不在这里直接处理具体业务——
    /// AnnouncementDialog 不该知道"简洁模式"是什么，交给 MainWindow 订阅处理。</summary>
    public event Action<string>? ActionInvoked;

    public AnnouncementDialog(IReadOnlyList<AnnouncementService.Announcement> announcements)
    {
        InitializeComponent();
        AnnouncementList.ItemsSource = announcements;
    }

    /// <summary>点"我知道了"：CloseWith(true) 让调用方(MainWindow)知道这批公告已经被用户
    /// 确认过，可以调用 AnnouncementService.MarkSeen 写入配置。点关闭/Esc/点遮罩则结果是
    /// false/null，调用方不会标记已读，下次启动继续弹出。</summary>
    private void Confirm_Click(object sender, RoutedEventArgs e) => CloseWith(true);

    /// <summary>公告卡片里的行内操作按钮：只转发事件，不关闭弹窗——用户可能还有别的公告
    /// 没看完，点了"立即开启简洁模式"之后应该还能继续往下读，最后自己点"我知道了"关闭。</summary>
    private void AnnouncementAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string actionKey })
            ActionInvoked?.Invoke(actionKey);
    }
}
