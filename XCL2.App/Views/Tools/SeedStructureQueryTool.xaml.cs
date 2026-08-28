using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>
/// 百宝箱「种子结构查询」。真正的结构算法和地图由 Chunkbase Seed Map 提供；
/// 启动器只负责把 seed / 版本 / 维度带进内嵌 WebView2，并且只在用户主动打开时创建浏览器，
/// 避免打开百宝箱就平白加载 Edge/WebView2 运行时。
/// </summary>
public partial class SeedStructureQueryTool : UserControl
{
    private const string SeedMapBaseUrl = "https://www.chunkbase.com/apps/seed-map";
    private WebView2? _browser;
    private bool _initializing;

    public SeedStructureQueryTool()
    {
        InitializeComponent();
    }

    private string BuildSeedMapUrl()
    {
        var seed = SeedBox.Text.Trim();
        var platform = (VersionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "java_1_21";
        var dimension = (DimensionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "overworld";
        return $"{SeedMapBaseUrl}#seed={Uri.EscapeDataString(seed)}&platform={Uri.EscapeDataString(platform)}&dimension={Uri.EscapeDataString(dimension)}&x=0&z=0&zoom=0.5";
    }

    private async void OpenMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeedBox.Text))
        {
            MessageBoxDialog.ShowWarning("请先输入世界种子。数字种子和字符串种子都可以。", "种子结构查询");
            SeedBox.Focus();
            return;
        }

        if (!WebView2RuntimeDetector.IsAvailable())
        {
            SeedQueryStatusText.Text = "未检测到 WebView2 Runtime，可以点击“用浏览器打开”继续查询。";
            MessageBoxDialog.ShowWarning("本机没有可用的 WebView2 Runtime，无法在启动器内嵌地图。\n\n你仍然可以点击“用浏览器打开”使用系统浏览器查询。", "WebView2 不可用");
            return;
        }

        if (_initializing) return;
        _initializing = true;
        OpenMapButton.IsEnabled = false;
        SeedQueryStatusText.Text = "正在启动 WebView2 并加载种子地图…";
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
                _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            }

            _browser.CoreWebView2.Navigate(BuildSeedMapUrl());
        }
        catch (Exception ex)
        {
            SeedQueryStatusText.Text = "内嵌地图启动失败，可以改用“用浏览器打开”。";
            ErrorPresenter.LogTechnicalDetail($"种子结构查询 WebView2 初始化失败：{ex}");
            MessageBoxDialog.ShowError($"种子地图加载失败：{ex.Message}", "种子结构查询");
        }
        finally
        {
            _initializing = false;
            OpenMapButton.IsEnabled = true;
        }
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        SeedQueryStatusText.Text = e.IsSuccess
            ? "种子地图已加载。可在地图左侧过滤村庄、要塞、远古城市、林地府邸、下界堡垒等结构。"
            : $"地图加载失败（WebErrorStatus: {e.WebErrorStatus}），可尝试“用浏览器打开”。";
    }

    private void CopySeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeedBox.Text)) return;
        Clipboard.SetText(SeedBox.Text.Trim());
        ToastService.ShowSuccess("种子已复制");
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeedBox.Text))
        {
            MessageBoxDialog.ShowWarning("请先输入世界种子。", "种子结构查询");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(BuildSeedMapUrl()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"打开浏览器失败：{ex.Message}", "种子结构查询");
        }
    }

    private void SeedStructureQueryTool_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_browser == null) return;
        try { _browser.Dispose(); } catch { }
        _browser = null;
        BrowserHost.Children.Clear();
    }
}
