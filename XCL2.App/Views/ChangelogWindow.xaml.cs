using System;
using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 一个更新日志来源的描述。弹窗只认这个结构，不认识"XCL"或"Minecraft"这两个具体概念——
/// 以后要再加第三个日志入口（比如某个加载器的更新日志），加一个静态属性即可，弹窗不用动。
/// </summary>
public sealed record ChangelogSource(string Title, string Subtitle, string Url, string FailureHint)
{
    /// <summary>XCL 启动器自己的更新日志 = GitHub Releases 页。用 /releases 而不是仓库首页：
    /// 用户点"查看更新日志"想看的是版本列表和每版改了什么，直接落在 Releases 上少一次点击。</summary>
    public static ChangelogSource Xcl { get; } = new(
        "XCL 更新日志",
        "来自 GitHub Releases：每个版本的更新内容、下载包都在这里。",
        "https://github.com/adorable0127/xztx127-craft-launcher2/releases",
        "打不开 GitHub Releases 页。国内网络访问 GitHub 时常不稳定，可以稍后重试，或者点下面的按钮用浏览器打开（浏览器里可能有你已经配置好的代理）。");

    /// <summary>Minecraft 的更新日志 = 中文 Minecraft Wiki 的"版本"条目。
    ///
    /// 为什么用 Wiki 而不是 Mojang 官网的 feedback/patch notes：官网的更新日志接口需要
    /// 登录态、且只覆盖较新的版本；中文 Wiki 的这个页面按正式版/快照完整列出了每个版本，
    /// 有中文说明，还能一路点进具体版本页，对本启动器的中文用户更实用。
    /// 这也跟项目里已有的"在中文 Minecraft Wiki 中查看"右键菜单用的是同一个站点，口径统一。</summary>
    public static ChangelogSource Minecraft { get; } = new(
        "Minecraft 更新日志",
        "来自中文 Minecraft Wiki 的版本列表：正式版与快照的更新内容，可以点进任意版本看详情。",
        "https://zh.minecraft.wiki/w/Java%E7%89%88",
        "打不开中文 Minecraft Wiki。可能是暂时没有网络或者对方站点在维护，可以稍后重试，或者点下面的按钮用浏览器打开。");
}

/// <summary>
/// 更新日志查看弹窗。XCL 和 Minecraft 两个入口各自 new 一个实例、各自弹出，
/// 是两个完全独立的弹窗——详见 xaml 头部注释。区别只在构造时传进来的 ChangelogSource。
///
/// 已从独立 Window 迁移为进程内 Overlay 弹窗（不再弹出新的系统窗口，而是盖在启动器
/// 主窗口上面），并把内容控件从旧的 WebBrowser（IE 内核）换成 WebView2（Edge 内核）——
/// 原因见 xaml 头部注释：旧内核解析不了页面里嵌的现代 JS（哪怕只是一个视频嵌入）就会弹
/// 系统级"脚本错误"对话框，糊在启动器正中间。
/// </summary>
public partial class ChangelogWindow : OverlayDialogControl
{
    private readonly ChangelogSource _source;
    private bool _webViewReady;

    public ChangelogWindow(ChangelogSource source)
    {
        InitializeComponent();
        _source = source;

        TitleText.Text = source.Title;
        SubtitleText.Text = source.Subtitle;
        UrlText.Text = source.Url;
        ErrorText.Text = source.FailureHint;

        Loaded += (_, _) => Navigate();
        RequestClose += (_, _) => DisposeBrowser();
    }

    private async void Navigate()
    {
        ErrorOverlay.Visibility = Visibility.Collapsed;
        if (_source == ChangelogSource.Xcl)
        {
            Browser.Visibility = Visibility.Collapsed;
            ApiNotesPanel.Visibility = Visibility.Visible;
            LatestReleaseNotes.Text = "正在从 GitHub API 获取最新版本日志…";
            CurrentReleaseNotes.Text = "";
            try
            {
                var notes = await GitHubReleaseNotesService.FetchAsync();
                LatestReleaseNotes.Text = notes.Latest;
                CurrentReleaseNotes.Text = notes.Current;
            }
            catch (Exception ex)
            {
                LatestReleaseNotes.Text = "获取失败，请检查网络或 GitHub API 访问限制：" + ex.Message;
                CurrentReleaseNotes.Text = "可通过下方按钮打开 GitHub Releases 发布页面。";
            }
            return;
        }
        ApiNotesPanel.Visibility = Visibility.Collapsed;

        if (!WebView2RuntimeDetector.IsAvailable())
        {
            // 本机没有可用的 WebView2 Runtime：不去尝试初始化一个注定失败的控件，
            // 直接降级成提示 + "用浏览器打开"兜底，跟 Web2BrowserTool/
            // MicrosoftLoginWindow 的降级方式一致。
            Browser.Visibility = Visibility.Collapsed;
            ErrorText.Text = "本机没有安装 WebView2 Runtime，暂时无法在启动器内嵌显示更新日志，可以点下面的按钮用浏览器打开。";
            ErrorOverlay.Visibility = Visibility.Visible;
            return;
        }

        Browser.Visibility = Visibility.Visible;
        ErrorText.Text = _source.FailureHint;

        try
        {
            if (!_webViewReady)
            {
                await Browser.EnsureCoreWebView2Async();
                Browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
                _webViewReady = true;
            }

            Browser.CoreWebView2.Navigate(_source.Url);
        }
        catch
        {
            // 内嵌浏览器控件在某些精简版系统上可能直接不可用。这不是关键功能，
            // 不让它把弹窗连带弄崩——盖上提示层，用户仍然可以用外部浏览器打开。
            ErrorOverlay.Visibility = Visibility.Visible;
        }
    }

    private bool _historyLoaded;

    /// <summary>历史版本更新日志展开：只在第一次展开时真正发一次请求，重复展开/折叠不重复拉取。</summary>
    private async void HistoryExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (_historyLoaded) return;
        _historyLoaded = true;
        try
        {
            var history = await GitHubReleaseNotesService.FetchHistoryAsync();
            HistoryList.ItemsSource = history;
            HistoryStatusText.Text = history.Count == 0 ? "没有找到历史发布记录。" : "";
            HistoryStatusText.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _historyLoaded = false; // 失败允许下次展开重试
            HistoryStatusText.Text = "获取历史版本日志失败，请检查网络：" + ex.Message;
            HistoryStatusText.Visibility = Visibility.Visible;
        }
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) ErrorOverlay.Visibility = Visibility.Visible;
    }

    private void DisposeBrowser()
    {
        if (!_webViewReady) return;
        try { Browser.CoreWebView2.NavigationCompleted -= Browser_NavigationCompleted; } catch { }
        try { Browser.Dispose(); } catch { }
        _webViewReady = false;
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Navigate();

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_source.Url) { UseShellExecute = true });
        }
        catch
        {
            MessageBoxDialog.ShowWarning($"没能调起系统浏览器，你可以手动复制这个地址打开：\n{_source.Url}", _source.Title);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
