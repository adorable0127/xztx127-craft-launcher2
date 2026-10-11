using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private IEmbeddedWebView? _browser;
    private bool _initializing;
    private bool _dependencyBlocked;
    private int _lifetimeVersion;

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

        if (_initializing || _dependencyBlocked) return;
        var lifetimeVersion = _lifetimeVersion;
        _initializing = true;
        OpenMapButton.IsEnabled = false;
        SeedQueryStatusText.Text = "正在启动 WebView2 并加载种子地图…";

        try
        {
            if (!EmbeddedWebView.TryCheckComponents(out var detail))
            {
                BlockEmbeddedBrowser(detail);
                return;
            }
            if (!WebView2RuntimeDetector.IsAvailable())
            {
                SeedQueryStatusText.Text = "未检测到 WebView2 Runtime，可以点击“用浏览器打开”继续查询。";
                ToastService.ShowWarning("内嵌地图需要 Microsoft WebView2 Runtime；也可点击“用浏览器打开”。");
                return;
            }

            BrowserCard.Visibility = Visibility.Visible;
            if (_browser == null)
            {
                _browser = EmbeddedWebView.Create(Browser_NavigationCompleted);
                BrowserHost.Children.Add(_browser.View);
                await _browser.InitializeAsync();
            }

            if (lifetimeVersion != _lifetimeVersion) return;
            _browser?.Navigate(BuildSeedMapUrl());
        }
        catch (Exception) when (lifetimeVersion != _lifetimeVersion)
        {
            // 初始化未完成时切走页面是正常生命周期；Unloaded 已负责释放。
        }
        catch (Exception ex)
        {
            ReleaseBrowser();
            if (EmbeddedWebView.IsComponentLoadFailure(ex))
                BlockEmbeddedBrowser(ex.ToString());
            else
            {
                SeedQueryStatusText.Text = "内嵌地图启动失败，可以重试或改用“用浏览器打开”。";
                ErrorPresenter.LogTechnicalDetail($"种子结构查询 WebView2 初始化失败：{ex}");
                ToastService.ShowWarning($"种子地图加载失败：{ex.Message}");
            }
        }
        finally
        {
            if (lifetimeVersion == _lifetimeVersion)
            {
                _initializing = false;
                OpenMapButton.IsEnabled = !_dependencyBlocked;
            }
        }
    }

    private void Browser_NavigationCompleted(bool isSuccess, string errorStatus)
    {
        if (!IsLoaded) return;
        SeedQueryStatusText.Text = isSuccess
            ? "种子地图已加载。可在地图左侧过滤村庄、要塞、远古城市、林地府邸、下界堡垒等结构。"
            : $"地图加载失败（WebErrorStatus: {errorStatus}），可尝试“用浏览器打开”。";
    }

    private void BlockEmbeddedBrowser(string? detail)
    {
        _dependencyBlocked = true;
        BrowserCard.Visibility = Visibility.Collapsed;
        SeedQueryStatusText.Text = EmbeddedWebView.MissingComponentsMessage;
        ErrorPresenter.LogTechnicalDetail($"种子结构查询 WebView2 组件不可用：{detail}");
        ToastService.ShowWarning("WebView2 程序组件缺失，请重新解压完整发布包。仍可用系统浏览器查询。");
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
        _lifetimeVersion++;
        _initializing = false;
        OpenMapButton.IsEnabled = !_dependencyBlocked;
        ReleaseBrowser();
    }

    private void ReleaseBrowser()
    {
        // 此方法和字段均不得出现 WebView2 SDK 类型，缺 DLL 时 Unloaded 也必须安全。
        var browser = _browser;
        _browser = null;
        try { browser?.Dispose(); }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail($"种子地图浏览器释放失败：{ex}"); }
        BrowserHost.Children.Clear();
        BrowserCard.Visibility = Visibility.Collapsed;
    }
}
