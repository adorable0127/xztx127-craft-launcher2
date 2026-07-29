using System.Diagnostics;
using System.Windows;

namespace XCL2.App.Views;

public partial class DeviceCodeWindow : Window
{
    private readonly string _verificationUri;
    private readonly string _userCode;
    public bool CancelRequested { get; private set; }
    private readonly CancellationTokenSource? _cts;

    public DeviceCodeWindow(string verificationUri, string userCode, CancellationTokenSource? cts = null)
    {
        InitializeComponent();
        _verificationUri = verificationUri;
        _userCode = userCode;
        _cts = cts;

        CodeText.Text = userCode;
        Loaded += (_, _) =>
        {
            // 弹窗一出现就自动复制代码到剪贴板，用户直接粘贴即可
            TryCopyToClipboard(userCode);
            StatusText.Text = "代码已自动复制到剪贴板，请粘贴到浏览器页面";
        };
    }

    public void SetStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

    private void TryCopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* 剪贴板可能被其他程序占用，忽略 */ }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        TryCopyToClipboard(_userCode);
        StatusText.Text = "已复制！请粘贴到浏览器页面";
    }

    private void ReopenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_verificationUri) { UseShellExecute = true }); }
        catch { /* 忽略 */ }
        TryCopyToClipboard(_userCode);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested = true;
        _cts?.Cancel();
        Close();
    }
}
