using System.Diagnostics;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>只将网页地址交给系统默认浏览器，不启动任意协议或命令。</summary>
public static class ExternalLinkService
{
    public static void Open(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            MessageBoxDialog.ShowWarning("请输入有效的 http / https 网页地址。", "打开链接");
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBoxDialog.ShowWarning("无法打开链接：" + ex.Message, "打开链接"); }
    }
}
