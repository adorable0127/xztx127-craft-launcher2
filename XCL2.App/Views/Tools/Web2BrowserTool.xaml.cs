using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>
/// 百宝箱「Web2」工具：把指定网址（默认 xztx127.dpdns.org/xztx127gjx）用内嵌 WebView2 打开，
/// 或者一键交给系统默认浏览器打开。跟「种子结构查询」工具是同一套模式（只在用户主动点开时
/// 才创建 WebView2 实例），这里额外支持用户自己改地址框，不写死只能打开一个网址。
/// </summary>
public partial class Web2BrowserTool : UserControl
{
    private IEmbeddedWebView? _browser;
    private bool _initializing;
    private bool _dependencyBlocked;
    private int _lifetimeVersion;

    public Web2BrowserTool()
    {
        InitializeComponent();
    }

    private string ResolveUrl()
    {
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "https://xztx127.dpdns.org/xztx127gjx";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }

    private async void OpenEmbeddedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing || _dependencyBlocked) return;
        var lifetimeVersion = _lifetimeVersion;
        _initializing = true;
        OpenEmbeddedButton.IsEnabled = false;
        Web2StatusText.Text = "正在启动 WebView2 并加载页面…";

        try
        {
            if (!EmbeddedWebView.TryCheckComponents(out var detail))
            {
                BlockEmbeddedBrowser(detail);
                return;
            }
            if (!WebView2RuntimeDetector.IsAvailable())
            {
                Web2StatusText.Text = "未检测到 WebView2 Runtime，可以点击“在浏览器中打开”继续访问。";
                ToastService.ShowWarning("内嵌网页需要 Microsoft WebView2 Runtime；也可点击“在浏览器中打开”。");
                return;
            }

            BrowserCard.Visibility = Visibility.Visible;
            if (_browser == null)
            {
                _browser = EmbeddedWebView.Create(Browser_NavigationCompleted, enableDevTools: true);
                BrowserHost.Children.Add(_browser.View);
                await _browser.InitializeAsync();
            }

            if (lifetimeVersion != _lifetimeVersion) return;
            _browser?.Navigate(ResolveUrl());
        }
        catch (Exception) when (lifetimeVersion != _lifetimeVersion)
        {
            // 页面已卸载，由 Unloaded 释放，不对正常页面切换弹错误。
        }
        catch (Exception ex)
        {
            ReleaseBrowser();
            if (EmbeddedWebView.IsComponentLoadFailure(ex))
                BlockEmbeddedBrowser(ex.ToString());
            else
            {
                Web2StatusText.Text = "内嵌打开失败，可以重试或改用“在浏览器中打开”。";
                ErrorPresenter.LogTechnicalDetail($"Web2 工具 WebView2 初始化失败：{ex}");
                ToastService.ShowWarning($"页面加载失败：{ex.Message}");
            }
        }
        finally
        {
            if (lifetimeVersion == _lifetimeVersion)
            {
                _initializing = false;
                OpenEmbeddedButton.IsEnabled = !_dependencyBlocked;
            }
        }
    }

    private void Browser_NavigationCompleted(bool isSuccess, string errorStatus)
    {
        if (!IsLoaded) return;
        Web2StatusText.Text = isSuccess
            ? "页面已加载。"
            : $"页面加载失败（WebErrorStatus: {errorStatus}），可尝试“在浏览器中打开”。";
    }

    private void BlockEmbeddedBrowser(string? detail)
    {
        _dependencyBlocked = true;
        BrowserCard.Visibility = Visibility.Collapsed;
        Web2StatusText.Text = EmbeddedWebView.MissingComponentsMessage;
        ErrorPresenter.LogTechnicalDetail($"Web2 工具 WebView2 组件不可用：{detail}");
        ToastService.ShowWarning("WebView2 程序组件缺失，请重新解压完整发布包。仍可使用系统浏览器。");
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ResolveUrl()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"打开浏览器失败：{ex.Message}", "Web2");
        }
    }

    private void Web2BrowserTool_Unloaded(object sender, RoutedEventArgs e)
    {
        _lifetimeVersion++;
        _initializing = false;
        OpenEmbeddedButton.IsEnabled = !_dependencyBlocked;
        ReleaseBrowser();
    }

    private void ReleaseBrowser()
    {
        var browser = _browser;
        _browser = null;
        try { browser?.Dispose(); }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail($"Web2 浏览器释放失败：{ex}"); }
        BrowserHost.Children.Clear();
        BrowserCard.Visibility = Visibility.Collapsed;
    }
}
