using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace XCL2.App.Views;

/// <summary>
/// "服务器启动后如何开放外网访问"教程窗口：介绍内网穿透/路由器端口映射/云服务器中转三种
/// 常见方法，附带常用工具的官网链接。用户诉求原话是"介绍内网穿透、路由等操作方法"，
/// 三种方法覆盖了从"零配置但依赖第三方/有限速"到"自己掌控但需要公网IP和路由器权限"再到
/// "花钱但最稳定"的完整梯度，不只讲一种，让不同网络环境的用户都能找到适合自己的路径。
///
/// 由 ServerManagerPage.StartInstance 在启动成功后弹出（非模态阻塞主流程的必经步骤，
/// 用户可以直接关掉不影响正常使用），是否弹出受 AppConfig.ShowServerNetworkGuideOnStart
/// 控制，窗口内有"不再提示"勾选框直接写回这个配置。
/// </summary>
public partial class ServerNetworkGuideWindow : OverlayDialogControl
{
    /// <summary>关闭窗口时是否勾选了"不再提示"，调用方据此更新 AppConfig 并保存。</summary>
    public bool DontShowAgain { get; private set; }

    public ServerNetworkGuideWindow()
    {
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 极少数环境下默认浏览器关联异常，静默失败即可，不弹错误打断用户看教程的流程，
            // 链接文本本身就是可读的网址，用户可以自己手动复制去浏览器打开。
        }
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DontShowAgain = DontShowAgainCheck.IsChecked == true;
        CloseWith(null);
    }
}
