using System.Windows;
using Microsoft.Web.WebView2.Core;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 内嵌浏览器登录窗口：直接展示微软登录页，用户在里面输入账号密码即可，
/// 不需要复制/粘贴任何验证码。原理与 mc-launcher（Electron 版）里
/// loginMicrosoftInteractive 打开的弹窗完全一致：监测网页跳转到
/// <see cref="MicrosoftAuthService.NativeClientRedirectUri"/> 时，
/// 从跳转后的 URL 上取出 ?code= 参数即为授权码。
/// </summary>
public partial class MicrosoftLoginWindow : OverlayDialogControl
{
    private readonly string _redirectUri;
    private readonly TaskCompletionSource<string> _codeTcs = new();
    private bool _settled;

    public MicrosoftLoginWindow(string authorizeUrl, string redirectUri = MicrosoftAuthService.NativeClientRedirectUri)
    {
        InitializeComponent();
        _redirectUri = redirectUri;
        Loaded += async (_, _) => await InitializeAsync(authorizeUrl);
        RequestClose += OnRequestClose;
    }

    /// <summary>等待用户完成登录并拿到授权码；窗口被手动关闭则视为取消。</summary>
    public Task<string> WaitForCodeAsync() => _codeTcs.Task;

    private async Task InitializeAsync(string authorizeUrl)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.NavigationStarting += (_, e) => CheckUrl(e.Uri);
            Browser.CoreWebView2.SourceChanged += (_, _) => CheckUrl(Browser.CoreWebView2.Source);
            Browser.CoreWebView2.Navigate(authorizeUrl);
        }
        catch (Exception ex)
        {
            // 常见原因：机器上没有装 WebView2 Runtime。降级提示用户改用浏览器登录，
            // 而不是让整个窗口空白卡死、看起来毫无反应。
            Browser.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
            FallbackText.Text = Loc.T("Str_Cs_The_Embedded_Sign_In_Component_Failed_To", "内嵌登录组件初始化失败：") + ex.Message +
                Loc.T("Str_Cs_Please_Use_Browser_Login_Instead", "\n\n请改用「浏览器登录」按钮，或安装 WebView2 运行时后重试。");
            Settle(null, ex);
        }
    }

    private void CheckUrl(string url)
    {
        if (_settled || string.IsNullOrEmpty(url) || !url.StartsWith(_redirectUri, StringComparison.OrdinalIgnoreCase))
            return;

        var query = ParseQuery(new Uri(url).Query);
        if (query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
        {
            query.TryGetValue("error_description", out var desc);
            Settle(null, new AuthStepException("获取登录令牌", desc ?? error));
            return;
        }

        if (query.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code)) Settle(code, null);
    }

    /// <summary>极简 query string 解析：net8.0-windows 的 WPF 项目默认不带 System.Web.HttpUtility，
    /// 没必要为了这一个用途额外引入 ASP.NET 相关依赖。</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx >= 0 ? pair[..idx] : pair;
            var value = idx >= 0 ? pair[(idx + 1)..] : "";
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return result;
    }

    private void Settle(string? code, Exception? ex)
    {
        if (_settled) return;
        _settled = true;
        if (ex != null) _codeTcs.TrySetException(ex);
        else _codeTcs.TrySetResult(code!);
        Dispatcher.Invoke(Close);
    }

    // 迁移后修复：OverlayDialogControl 继承自 UserControl，没有 Window.OnClosed 这个虚方法
    // 可以重写（这也是 error CS0115 的原因——"没有找到适合的方法来重写"）。
    // Overlay 弹窗对应"我要关了"这件事的钩子是 IOverlayDialog.RequestClose 事件，
    // 之前的 SkinSelectWindow 等其它迁移弹窗已经在用这个模式（见 IOverlayDialog.cs 顶部注释），
    // 这里补上同样的写法：构造函数里订阅一次，逻辑跟原来 OnClosed 里的完全一致。
    private void OnRequestClose(object? sender, bool? result)
    {
        // 用户直接关掉弹窗而没有走到重定向：视为取消登录，而不是让上层一直卡着等待。
        if (!_settled) _codeTcs.TrySetCanceled();
    }
}
