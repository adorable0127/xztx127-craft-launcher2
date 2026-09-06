using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>
/// 百宝箱「Web2」工具：把指定网址（默认 xztx127.dpdns.org/xztx127gjx）用内嵌 WebView2 打开，
/// 或者一键交给系统默认浏览器打开。跟「种子结构查询」工具是同一套模式（只在用户主动点开时
/// 才创建 WebView2 实例），这里额外支持用户自己改地址框，不写死只能打开一个网址。
/// </summary>
public partial class Web2BrowserTool : UserControl
{
    private WebView2? _browser;
    private bool _initializing;

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
        if (!WebView2RuntimeDetector.IsAvailable())
        {
            Web2StatusText.Text = "未检测到 WebView2 Runtime，可以点击“在浏览器中打开”继续访问。";
            MessageBoxDialog.ShowWarning("本机没有可用的 WebView2 Runtime，无法在启动器内嵌打开网页。\n\n你仍然可以点击“在浏览器中打开”使用系统浏览器访问。", "WebView2 不可用");
            return;
        }

        if (_initializing) return;
        _initializing = true;
        OpenEmbeddedButton.IsEnabled = false;
        Web2StatusText.Text = "正在启动 WebView2 并加载页面…";
        BrowserCard.Visibility = Visibility.Visible;

        try
        {
            if (_browser == null)
            {
                _browser = new WebView2
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    MinHeight = 560
                };
                BrowserHost.Children.Add(_browser);
                await _browser.EnsureCoreWebView2Async();
                _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            }

            _browser.CoreWebView2.Navigate(ResolveUrl());
        }
        catch (Exception ex)
        {
            Web2StatusText.Text = "内嵌打开失败，可以改用“在浏览器中打开”。";
            ErrorPresenter.LogTechnicalDetail($"Web2 工具 WebView2 初始化失败：{ex}");
            MessageBoxDialog.ShowError($"页面加载失败：{ex.Message}", "Web2");
        }
        finally
        {
            _initializing = false;
            OpenEmbeddedButton.IsEnabled = true;
        }
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Web2StatusText.Text = e.IsSuccess
            ? "页面已加载。"
            : $"页面加载失败（WebErrorStatus: {e.WebErrorStatus}），可尝试“在浏览器中打开”。";
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
        if (_browser == null) return;
        try { _browser.Dispose(); } catch { }
        _browser = null;
        BrowserHost.Children.Clear();
    }
}
