using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>覆盖在当前页面上的完整 AI 助手；独立请求回调不会抢走普通 AI 页的会话。</summary>
internal sealed class AiCrashAnalysisDialog : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly AiAssistantPanel _panel;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _prompt;
    private readonly string _sessionTitle;
    private bool _started;
    private bool _disposed;

    internal AiCrashAnalysisDialog(MainWindow owner, string prompt, string sessionTitle)
    {
        _owner = owner;
        _prompt = prompt;
        _sessionTitle = sessionTitle;
        _panel = new AiAssistantPanel { RequireCrashLogPermission = true, SuppressPrewarm = true, AnalysisOnly = true };
        _panel.Attach(new AiAssistantService(owner.AiAssistantConfig, App.DataDir, _http) { ForceReadOnly = true }, owner.AiAssistantConfig);
        _panel.CloseRequested += (_, _) => Close();
        _panel.SettingsRequested += (_, _) => ShowSettings();

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock
        {
            Text = "当前崩溃日志分析 · 收起后仍保留当前页面",
            Margin = new Thickness(16, 10, 16, 10), FontSize = 12, TextWrapping = TextWrapping.Wrap
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        content.Children.Add(heading);
        Grid.SetRow(_panel, 1);
        content.Children.Add(_panel);
        Content = content;
        UpdateSize();
        owner.SizeChanged += Owner_SizeChanged;
        owner.Closed += Owner_Closed;
        Loaded += Dialog_Loaded;
    }

    private void UpdateSize()
    {
        Width = Math.Max(300, Math.Min(1050, _owner.ActualWidth - 72));
        Height = Math.Max(260, Math.Min(760, _owner.ActualHeight - 100));
    }

    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSize();

    private async void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed) return;
        _started = true;
        try { await _panel.SubmitExternalPromptAsync(_prompt, _sessionTitle, isCrashLogContext: true); }
        catch (Exception ex)
        {
            // 配置/磁盘等请求开始前的异常也必须可见，不能让 async void 异常结束启动器。
            ErrorPresenter.LogFallback("打开崩溃 AI 分析失败", ex);
            if (!_disposed) MessageBoxDialog.ShowError("准备 AI 分析失败：" + ex.Message, "崩溃日志分析");
        }
    }

    private void ShowSettings()
    {
        var settings = new AiAssistantSettingsPanel(_owner.AiAssistantConfig);
        settings.Saved += (_, config) =>
        {
            _owner.AiAssistantConfig = config;
            _owner.AiAssistantService.UpdateConfig(config);
            _owner.PersistAiAssistantConfig();
            _panel.ApplyConfig(config);
        };
        OverlayDialogService.ShowModal(settings);
    }

    private void Owner_Closed(object? sender, EventArgs e)
    {
        // 主窗口关闭时也立即取消网络请求；普通导航中的 AI 面板不受本浮层生命周期影响。
        _ = DisposeAsync();
    }

    internal async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.SizeChanged -= Owner_SizeChanged;
        _owner.Closed -= Owner_Closed;
        Loaded -= Dialog_Loaded;
        try { await _panel.StopForOverlayCloseAsync(); }
        finally { _http.Dispose(); }
    }
}
