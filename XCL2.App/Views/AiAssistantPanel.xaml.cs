using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>主内容区 AI 助手：左侧本地会话历史，右侧聊天；模型路由固定在顶部。</summary>
public partial class AiAssistantPanel : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? SettingsRequested;

    private AiAssistantService? _service;
    private AiAssistantConfig _config = new();
    private AiChatSession _session = new();
    private readonly ObservableCollection<BubbleVm> _bubbles = new();
    private CancellationTokenSource? _sendCts;
    private long _sendGeneration;
    private bool _sending;
    private TaskCompletionSource<bool>? _sendCompletion;
    private bool _overlayClosed;
    internal bool RequireCrashLogPermission { get; set; }
    internal bool SuppressPrewarm { get; set; }
    internal bool AnalysisOnly { get; set; }
    private bool _suppressRoutingSelection;
    private bool _suppressModelSelection;
    private bool _suppressReasoningSelection;
    private bool _suppressWorkModeSelection;
    private bool _suppressHistorySelection;
    private bool _confirmingWebSearch;
    private MainWindow? _owner;
    private readonly System.Windows.Threading.DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _sendStartedUtc;

    public AiAssistantPanel()
    {
        InitializeComponent();
        MessagesItemsControl.ItemsSource = _bubbles;
        _waitTimer.Tick += (_, _) => UpdateWaitHint();
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this) as MainWindow;
        _owner?.UpdateAiFloatingButtonVisibility(false);
        RememberCurrentSession();
        RefreshActivityVisibility();

        // 打开 AI 助手页面时预拉一次内置公共密钥并落盘缓存（已有缓存时内部会直接跳过），
        // 避免后续每次发消息都现拉一次。失败不影响页面正常打开，发消息时会自动兜底重拉。
        if (AiModelIds.GetCatalog(_config).Any(model => model.ProviderId == "builtin"))
            _ = BuiltInAiDefaults.PrefetchAsync();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        // 页面被切走只代表这个控件暂时离开可视树，不代表用户要求停止生成。
        // 取消仍由面板里的“停止”按钮负责；否则发送消息后切到设置/日志等页面，
        // Unloaded 会直接 Cancel 当前请求，表现成 AI 自动停止回复。
        _owner?.UpdateAiFloatingButtonVisibility(true);
    }

    /// <summary>首次接入服务；优先恢复上次真正打开的会话，而不是简单取“最近修改的一条”。</summary>
    public void Attach(AiAssistantService service, AiAssistantConfig config)
    {
        _service = service;
        _service.ConfirmAccessRequest = (title, msg) => MessageBoxDialog.ShowConfirm(msg, title);
        _service.ReviewFileChange = (change, diff) => AiWorkbenchDialogs.Review(Window.GetWindow(this), change, diff);
        // 模型发起 XCL2_ASK 时，在输入框位置弹出提问卡片，等用户作答后再继续。
        _service.AskQuestionAsync = ShowQuestionAsync;
        ApplyConfig(config);

        AiChatSession? saved = null;
        if (!string.IsNullOrWhiteSpace(_config.LastSessionId))
            saved = _service.LoadSession(_config.LastSessionId!);
        _session = saved ?? _service.ListSessions().FirstOrDefault() ?? _service.CreateNewSession();
        _config.LastSessionId = _session.Id;
        RebuildBubbles();
        RefreshHistory();

        // 设置里的"打开 AI 助手时预热系统提示词"开关：每次真正打开这个页面（Attach 只在
        // 页面被导航/创建时调用一次，不含设置保存后的 ApplyConfig 热更新）就在后台悄悄发
        // 一次预热请求，不等待、不展示、不影响页面其它初始化。fire-and-forget 用 _ = 忽略
        // 返回的 Task——WarmUpAsync 内部已经吞掉了所有异常，这里不需要再 catch 一次。
        if (!SuppressPrewarm && _config.PrewarmSystemPromptOnOpen)
            _ = _service.WarmUpAsync();
    }

    /// <summary>
    /// 显示 AI 提问卡片（覆盖输入框）并等待作答。作答期间禁用输入区，避免用户同时在底下打字发送。
    /// 返回 null = 用户点了“忽略”；用户点“停止”会让 ct 取消 → 抛 OperationCanceledException，
    /// 由 GenerateAsync 现有的取消分支处理。
    /// </summary>
    private async Task<AiQuestionAnswer?> ShowQuestionAsync(AiQuestionSet set, CancellationToken ct)
    {
        InputArea.IsEnabled = false;
        try
        {
            ScrollToBottom();
            return await QuestionCard.AskAsync(set, ct);
        }
        finally
        {
            InputArea.IsEnabled = true;
        }
    }

    /// <summary>热更新设置，不重建/切换当前 Session。</summary>
    public void ApplyConfig(AiAssistantConfig config)
    {
        // 设置面板返回的是新对象；如果它来自旧版本没有带 LastSessionId，保住当前会话。
        if (string.IsNullOrWhiteSpace(config.LastSessionId) && !string.IsNullOrWhiteSpace(_session.Id))
            config.LastSessionId = _session.Id;

        _config = config;
        NormalizeConfigModels();
        _service?.UpdateConfig(_config);

        WebSearchCheck.IsChecked = _config.EnableWebSearch;
        RefreshRoutingUi();
        RefreshModelCatalog();
        RefreshReasoningUi();
        RefreshWorkModeUi();
        UpdateRoutingText();
        UpdateConfigHint();
        UpdateTokenBadge();
        RefreshActivityVisibility();
    }

    public async Task SubmitExternalPromptAsync(string text, string sessionTitle = "日志分析", bool isCrashLogContext = false)
    {
        if (_service == null || _sending || string.IsNullOrWhiteSpace(text)) return;
        _session = _service.CreateNewSession(string.IsNullOrWhiteSpace(sessionTitle) ? "日志分析" : sessionTitle);
        RememberCurrentSession();
        RebuildBubbles();
        RefreshHistory();
        InputTextBox.Text = text;
        await SendCurrentInputAsync(isCrashLogContext);
    }

    /// <summary>独立分析浮层真正关闭时取消它自己的请求；普通页面切换仍保持原有后台生成行为。</summary>
    internal async Task StopForOverlayCloseAsync()
    {
        _overlayClosed = true;
        _waitTimer.Stop();
        var completion = _sendCompletion;
        _sendCts?.Cancel();
        if (completion != null) await completion.Task;
    }

    private string ModelDisplayName(string? modelKey)
    {
        var model = AiModelIds.FindInCatalog(_config, modelKey);
        return model == null ? AiModelIds.GetDisplayName(_config, modelKey)
            : string.IsNullOrWhiteSpace(model.ProviderName) ? model.DisplayName : model.ProviderName + " · " + model.DisplayName;
    }

    private void NormalizeConfigModels()
    {
        _config.CustomModels ??= new List<AiModelDefinition>();
        var catalog = AiModelIds.GetCatalog(_config);
        if (catalog.Count == 0) return;

        string Pick(string? value, string builtInFallback)
        {
            var match = AiModelIds.FindInCatalog(_config, value);
            if (match != null) return match.ModelKey;
            var fallback = catalog.FirstOrDefault(m => m.ProviderId == "builtin" && string.Equals(m.Id, builtInFallback, StringComparison.OrdinalIgnoreCase));
            return fallback?.ModelKey ?? catalog[0].ModelKey;
        }

        _config.NormalModelId = Pick(_config.NormalModelId, AiModelIds.Nemotron35Lightning);
        _config.ExpertModelId = Pick(_config.ExpertModelId, AiModelIds.MimoV25);
        _config.SelectedModel = Pick(_config.SelectedModel, AiModelIds.Nemotron35Lightning);
        _config.AutoModelRouting = _config.RoutingMode == AiRoutingMode.Auto;
    }

    private void RefreshRoutingUi()
    {
        _suppressRoutingSelection = true;
        try
        {
            foreach (ComboBoxItem item in RoutingModeCombo.Items)
            {
                if (item.Tag is string tag && Enum.TryParse<AiRoutingMode>(tag, out var mode) && mode == _config.RoutingMode)
                {
                    RoutingModeCombo.SelectedItem = item;
                    return;
                }
            }
            RoutingModeCombo.SelectedIndex = 0;
        }
        finally { _suppressRoutingSelection = false; }
    }

    private void RefreshModelCatalog()
    {
        var catalog = AiModelIds.GetCatalog(_config);
        _suppressModelSelection = true;
        try
        {
            ModelCombo.ItemsSource = catalog;
            ModelCombo.SelectedValue = catalog.Any(m => string.Equals(m.ModelKey, _config.SelectedModel, StringComparison.OrdinalIgnoreCase))
                ? _config.SelectedModel
                : catalog.FirstOrDefault()?.ModelKey;
        }
        finally { _suppressModelSelection = false; }
        UpdateModelSelectionState();
    }

    /// <summary>只有“指定模型”模式允许在聊天页改模型；自动/普通/专家完全由设置页的默认模型决定。</summary>
    private void UpdateModelSelectionState()
    {
        bool manual = _config.RoutingMode == AiRoutingMode.SpecificModel;
        ModelCombo.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ResolvedModelBorder.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        ModelCombo.IsEnabled = manual && !_sending;

        var normal = ModelDisplayName(_config.NormalModelId);
        var expert = ModelDisplayName(_config.ExpertModelId);
        ResolvedModelText.Text = _config.RoutingMode switch
        {
            AiRoutingMode.Normal => $"由设置决定：{normal}",
            AiRoutingMode.Expert => $"由设置决定：{expert}",
            _ => $"自动：普通 {normal} / 专家 {expert}"
        };
    }

    private void RefreshHistory()
    {
        if (_service == null) return;
        var items = _service.ListSessions()
            .Select(s => new AiSessionListItemVm(s))
            .ToList();

        _suppressHistorySelection = true;
        try
        {
            HistoryListBox.ItemsSource = items;
            HistoryListBox.SelectedItem = items.FirstOrDefault(x => x.Session.Id == _session.Id);
        }
        finally { _suppressHistorySelection = false; }
    }

    private void RebuildBubbles()
    {
        RefreshTaskUi();
        _bubbles.Clear();
        foreach (var m in _session.Messages.Where(m => m.Role != AiMessageRole.System))
            _bubbles.Add(BubbleVm.From(m, _config));
        UpdateTokenBadge();
        ScrollToBottom();
    }

    private void UpdateTokenBadge()
    {
        AiRequestUsage[] requests;
        _session.UsageRequests ??= new();
        lock (_session.UsageRequests) requests = _session.UsageRequests.Select(x => x.Clone()).ToArray();
        long total = requests.Sum(x => x.TotalTokens);
        bool estimated = requests.Any(x => x.IsEstimated);
        var current = requests.LastOrDefault(x => x.AffectsContext && x.TotalTokens > 0);
        TokenUsageText.Text = requests.Length == 0 ? "本对话 · 暂无 API 用量" : $"本对话 · {(estimated ? "约 " : "")}{total:N0} token";
        CacheUsageText.Text = current?.CachedInputTokens is long cached
            ? current.HasReportedInput && current.InputTokens > 0
                ? $"当前缓存命中率：{(double)cached / current.InputTokens:P1}（{cached:N0} / {current.InputTokens:N0} 输入 token）"
                : $"缓存命中 {cached:N0} token（等待接口返回有效输入总量后计算命中率）"
            : "当前缓存命中率：供应商未返回缓存数据";
        var priced = requests.Where(x => x.EstimatedCostUsd.HasValue).ToArray();
        string cost = requests.Length == 0 ? "暂无 API 请求" : priced.Length == 0 ? "未配置模型单价"
            : $"${priced.Sum(x => x.EstimatedCostUsd!.Value):0.######} USD" + (priced.Length < requests.Length ? " + 未知部分" : "");
        CostUsageText.Text = "本对话估计成本：" + cost;
        TotalUsageText.Text = $"累计输入 {requests.Sum(x => x.InputTokens):N0} · 输出 {requests.Sum(x => x.OutputTokens):N0} · 合计 {total:N0} token";
        var currentModelKey = current == null ? "" : string.IsNullOrWhiteSpace(current.ModelKey) ? current.Model : current.ModelKey;
        var configuredLimit = current == null ? 0 : AiUsageAccounting.Settings(_config, currentModelKey).ContextWindowTokens;
        var contextLimit = Math.Clamp(configuredLimit > 0 ? configuredLimit : current?.ContextWindowTokens ?? 0, 0, 10_000_000);
        long occupied = current == null ? 0 : current.InputTokens + Math.Max(current.OutputTokens, current.InProgress ? current.ReservedOutputTokens : 0);
        double remaining = contextLimit > 0 ? Math.Clamp(1.0 - (double)occupied / contextLimit, 0, 1) : -1;
        DrawUsageRing(remaining);
        ContextUsageText.Text = current == null ? "当前上下文：发送请求后显示"
            : contextLimit <= 0 ? $"当前上下文约 {occupied:N0} token；请为该模型设置上限以显示剩余比例。"
            : $"当前上下文{(current.IsEstimated ? "约 " : "")}{occupied:N0} / {contextLimit:N0}，剩余 {Math.Max(0, contextLimit - occupied):N0} token"
                + (current.InProgress && current.ReservedOutputTokens > 0 ? "（含预留输出）" : "");
        UsageSummaryText.Text = current == null ? "点击查看用量；可在设置中填写模型上限与单价"
            : (remaining >= 0 ? $"上下文剩余 {remaining:P0} · " : "上下文上限未设置 · ") + cost;
        UsageSourceText.Text = (current == null ? "" : "当前模型："
            + (string.IsNullOrWhiteSpace(current.ProviderName) ? ModelDisplayName(currentModelKey) : current.ProviderName + " · " + current.Model) + "\n")
            + (estimated ? "包含本地估算；供应商未返回完整 usage 或请求中断时，token 与费用可能有偏差。" : "token 采用供应商返回的 usage。")
            + " 费用按你设置的 USD 单价估算；未返回缓存字段或未设置缓存价时按普通输入价计。"
            + (requests.Any(x => x.IsIncomplete) ? " 包含失败或中断请求，可能无法获得完整 usage。" : "")
            + " 累计包含本对话的工具续答、压缩、子任务；不含预热、连接测试及此功能启用前的历史请求。圆环只计算当前主请求。";
    }

    private void DrawUsageRing(double remaining)
    {
        UsageRingLabel.Text = remaining < 0 ? "—" : $"{remaining:P0}";
        UsageRemainingRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            remaining >= 0 && remaining <= 0.1 ? "DangerBrush" : "AccentBrush");
        if (remaining <= 0) { UsageRemainingRing.Data = Geometry.Empty; return; }
        if (remaining >= 0.9999) { UsageRemainingRing.Data = new EllipseGeometry(new Point(20, 20), 17, 17); return; }
        double angle = remaining * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(20, 3), false, false);
            ctx.ArcTo(new Point(20 + Math.Sin(angle) * 17, 20 - Math.Cos(angle) * 17),
                new Size(17, 17), 0, remaining > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        UsageRemainingRing.Data = geometry;
    }

    private void UpdateWaitHint()
    {
        var elapsed = (int)(DateTime.UtcNow - _sendStartedUtc).TotalSeconds;
        bool show = _sending && elapsed >= Math.Max(0, _config.LongWaitHintSeconds);
        LongWaitHintText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            string duration = $"{elapsed / 3600:00}:{elapsed / 60 % 60:00}:{elapsed % 60:00}";
            LongWaitHintText.Text = $"本轮已运行 {duration} · {_config.LongWaitDescription}";
        }
    }

    private void ScrollToBottom() => MessagesScroll.ScrollToEnd();

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendCurrentInputAsync();

    private async void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendCurrentInputAsync();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_sending) return;
        RouteStatusText.Text = "正在停止生成…";
        StopButton.IsEnabled = false;
        _sendCts?.Cancel();
    }

    private void RoutingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRoutingSelection) return;
        if (RoutingModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<AiRoutingMode>(tag, out var mode)) return;

        _config.RoutingMode = mode;
        _config.AutoModelRouting = mode == AiRoutingMode.Auto;
        RefreshReasoningUi();
        UpdateModelSelectionState();
        UpdateRoutingText();
        PersistQuickConfig();
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSelection || _config.RoutingMode != AiRoutingMode.SpecificModel) return;
        if (ModelCombo.SelectedValue is not string modelId || string.IsNullOrWhiteSpace(modelId)) return;

        _config.SelectedModel = modelId;
        _config.AutoModelRouting = false;
        RefreshReasoningUi();
        UpdateRoutingText();
        PersistQuickConfig();
    }

    private void QuickOption_Click(object sender, RoutedEventArgs e)
    {
        var enableWebSearch = WebSearchCheck.IsChecked == true;
        if (_confirmingWebSearch)
        {
            WebSearchCheck.IsChecked = _config.EnableWebSearch;
            return;
        }

        // Click 只来自主动操作；ApplyConfig 设置 IsChecked 不会触发此事件。
        if (enableWebSearch && !_config.EnableWebSearch)
        {
            _confirmingWebSearch = true;
            try
            {
                if (!AiWebSearchConfirmation.ConfirmEnable())
                {
                    WebSearchCheck.IsChecked = false;
                    return;
                }
            }
            finally { _confirmingWebSearch = false; }
        }

        WebSearchCheck.IsChecked = enableWebSearch;
        _config.EnableWebSearch = enableWebSearch;
        PersistQuickConfig();
    }

    private void RefreshWorkModeUi()
    {
        if (!Enum.IsDefined(typeof(AiWorkMode), _config.WorkMode)) _config.WorkMode = AiWorkMode.Plan;
        _suppressWorkModeSelection = true;
        try
        {
            var displayedMode = AnalysisOnly ? AiWorkMode.Plan : _config.WorkMode;
            WorkModeCombo.SelectedItem = WorkModeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, displayedMode.ToString(), StringComparison.Ordinal))
                ?? WorkModeCombo.Items[0];
            WorkModeCombo.IsEnabled = !AnalysisOnly && !_sending;
            WorkModeHintText.Text = AnalysisOnly ? "崩溃分析：只读调查并提供修复建议，不自动修改文件或运行命令。"
                : _config.WorkMode == AiWorkMode.Plan
                ? "Plan：只读调查与规划，不修改文件、不运行命令。切换 Build 后发送消息才开始执行。"
                : "Build：按已有权限执行任务。已有计划可发送“按计划继续”开始执行。";
        }
        finally { _suppressWorkModeSelection = false; }
    }

    private void WorkModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkModeSelection || AnalysisOnly) return;
        if (_sending)
        {
            RefreshWorkModeUi();
            return;
        }
        if (WorkModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<AiWorkMode>(tag, out var mode)) return;

        _config.WorkMode = mode;
        _service?.UpdateConfig(_config);
        RefreshWorkModeUi();
        PersistQuickConfig();
    }

    private AiReasoningLevel SelectedReasoningLevel => _config.ReasoningLevel
        ?? (_config.EnableDeepThinking ? AiReasoningLevel.High : AiReasoningLevel.Off);

    private IEnumerable<string> ActiveModelKeys() => _config.RoutingMode switch
    {
        AiRoutingMode.Normal => new[] { _config.NormalModelId },
        AiRoutingMode.Expert => new[] { _config.ExpertModelId },
        AiRoutingMode.SpecificModel => new[] { _config.SelectedModel },
        _ => new[] { _config.NormalModelId, _config.ExpertModelId }
    };

    private bool ActiveModelsSupportFastMode() => ActiveModelKeys()
        .All(key => AiModelIds.FindInCatalog(_config, key)?.SupportsFastMode == true);

    private void RefreshReasoningUi()
    {
        var level = SelectedReasoningLevel;
        bool supportsFast = ActiveModelsSupportFastMode();
        if (level == AiReasoningLevel.Fast && !supportsFast)
        {
            level = AiReasoningLevel.Off;
            _config.ReasoningLevel = level;
            _config.EnableDeepThinking = false;
            ToastService.ShowWarning("当前模型未确认支持极速模式，推理已切换为 off。自动路由时需普通、专家模型均支持极速。");
            PersistQuickConfig();
        }
        _suppressReasoningSelection = true;
        try
        {
            FastReasoningOption.Visibility = supportsFast ? Visibility.Visible : Visibility.Collapsed;
            FastReasoningOption.IsEnabled = supportsFast;
            ReasoningLevelCombo.SelectedItem = ReasoningLevelCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, level.ToString(), StringComparison.Ordinal));
        }
        finally { _suppressReasoningSelection = false; }
    }

    private void ReasoningLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReasoningSelection || ReasoningLevelCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<AiReasoningLevel>(tag, out var level)) return;
        _config.ReasoningLevel = level;
        _config.EnableDeepThinking = level is not (AiReasoningLevel.Off or AiReasoningLevel.Fast);
        RefreshReasoningUi();
        PersistQuickConfig();
    }

    private void RememberCurrentSession()
    {
        if (string.IsNullOrWhiteSpace(_session.Id)) return;
        _config.LastSessionId = _session.Id;
        PersistQuickConfig();
    }

    private void PersistQuickConfig()
    {
        if (_owner == null) return;
        _owner.AiAssistantConfig = _config;
        _owner.PersistAiAssistantConfig();
    }

    private void UpdateRoutingText()
    {
        var normal = ModelDisplayName(_config.NormalModelId);
        var expert = ModelDisplayName(_config.ExpertModelId);
        var selected = ModelDisplayName(_config.SelectedModel);

        switch (_config.RoutingMode)
        {
            case AiRoutingMode.Normal:
                RouteStatusText.Text = $"普通模式 · {normal}";
                RoutingHintText.Text = "固定使用设置中的普通模型；聊天页不能临时改模型。";
                break;
            case AiRoutingMode.Expert:
                RouteStatusText.Text = $"专家模式 · {expert}";
                RoutingHintText.Text = "固定使用设置中的专家模型；日志、崩溃、注入等分析建议使用此模式。";
                break;
            case AiRoutingMode.SpecificModel:
                RouteStatusText.Text = $"指定模型 · {selected}";
                RoutingHintText.Text = "仅此模式可以在上方直接选择实际模型。";
                break;
            default:
                RouteStatusText.Text = "自动路由 · 准备就绪";
                RoutingHintText.Text = $"自动按问题切换：普通 → {normal}；专家 → {expert}。两个模型都在 AI 设置中配置。";
                break;
        }
        UpdateModelSelectionState();
        UpdateSpecialTermsNotice();
    }

    /// <summary>当前实际会用到的模型（不是全部三个候选，而是这个路由模式下真的会调用的那些）
    /// 里，只要有一个带独立使用条款，就在聊天页顶部提示一下，避免用户没注意到设置页里的提示。</summary>
    private void UpdateSpecialTermsNotice()
    {
        var notices = ActiveModelKeys()
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(key => AiModelIds.FindInCatalog(_config, key)?.Id ?? key)
            .SelectMany(id => new[] { AiModelIds.GetSpecialTermsNotice(id), AiModelIds.GetKnownIssueNotice(id) })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();

        if (notices.Count == 0)
        {
            ChatSpecialTermsNoticeText.Visibility = Visibility.Collapsed;
            ChatSpecialTermsNoticeText.Text = "";
            return;
        }

        ChatSpecialTermsNoticeText.Text = string.Join("\n\n", notices);
        ChatSpecialTermsNoticeText.Visibility = Visibility.Visible;
    }

    private void UpdateConfigHint()
    {
        NoKeyHint.Visibility = AiModelIds.GetCatalog(_config).Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SendCurrentInputAsync(bool isCrashLogContext = false)
    {
        if (_overlayClosed || _sending || _service == null) return;
        var text = InputTextBox.Text.Trim();
        if (text.Length == 0) return;

        isCrashLogContext |= RequireCrashLogPermission;
        if (isCrashLogContext && !_config.AllowCrashLogReading)
        {
            RouteStatusText.Text = "崩溃日志已带入 · 请先在 AI 设置中允许发送日志，再点击发送";
            return; // 保留输入，不在权限关闭时发送、清空或丢弃本次日志。
        }

        // Credentials belong to each source; an empty legacy ApiKey cannot block another
        // configured endpoint or the built-in source in mixed mode.
        if (AiModelIds.GetCatalog(_config).Count == 0)
        {
            NoKeyHint.Visibility = Visibility.Visible;
            return;
        }

        if (AnalysisOnly && _service.GetConfigurationIssue(text, isCrashLogContext) is string configurationIssue)
        {
            RouteStatusText.Text = configurationIssue;
            return; // 初次配置未完成时日志仍留在输入框，设置完成后可以直接发送。
        }

        // 当前模式下会实际用到的模型（普通/专家/指定模型三种是确定的；自动模式要按问题内容
        // 判断走哪个，这里不重复那套打分逻辑，直接跳过拦截——自动模式默认的普通/专家模型
        // 本身就不会是已知有问题的模型）如果是已知有问题（供应商结构性拒绝/常限流）的模型，
        // 弹窗拦截一次，让用户自己决定要不要照发；不是静默失败让用户自己去猜为什么又报错了。
        var activeModelId = _config.RoutingMode switch
        {
            AiRoutingMode.Normal => _config.NormalModelId,
            AiRoutingMode.Expert => _config.ExpertModelId,
            AiRoutingMode.SpecificModel => _config.SelectedModel,
            _ => null
        };
        var knownIssue = AiModelIds.GetKnownIssueNotice(AiModelIds.FindInCatalog(_config, activeModelId)?.Id ?? activeModelId);
        if (!string.IsNullOrWhiteSpace(knownIssue))
        {
            var displayName = ModelDisplayName(activeModelId);
            var proceed = MessageBoxDialog.ShowConfirm(
                $"当前选择的模型“{displayName}”目前已知有问题：\n\n{knownIssue}\n\n大概率会请求失败，仍要继续发送吗？建议先去 AI 设置换一个模型。",
                "模型暂不可用");
            if (!proceed) return;
        }

        ClearInputAndResetUndo();
        await GenerateAsync(text, isCrashLogContext);
    }

    /// <summary>清空待发送框，并把它的 Ctrl+Z 撤销历史一起清掉：撤销只应该能撤回"这一次还没发出去
    /// 的输入"（比如中文输入法一次打出的一个字算一次输入单元），不应该在发送之后还能用 Ctrl+Z
    /// 把上一条已经发出去的消息内容"复活"回来——那样会跟"撤回"按钮的语义混在一起，容易误操作。
    /// WPF TextBox 没有直接的"清空撤销栈"方法，把 UndoLimit 先设成 0 再改回默认值是官方认可的
    /// 清空撤销栈手法（设成 0 会立即丢弃当前撤销栈，改回正常值后新的输入重新开始记录）。</summary>
    private void ClearInputAndResetUndo()
    {
        InputTextBox.Text = "";
        InputTextBox.UndoLimit = 0;
        InputTextBox.UndoLimit = int.MaxValue;
    }

    private async Task GenerateAsync(string text, bool isCrashLogContext)
    {
        if (_overlayClosed || _service == null || _sending) return;
        isCrashLogContext |= RequireCrashLogPermission;
        if (isCrashLogContext && !_config.AllowCrashLogReading)
        {
            RouteStatusText.Text = "请先在 AI 设置中允许发送日志，再继续分析";
            return;
        }

        var service = _service;
        var session = _session;
        var previousAssistant = session.Messages.LastOrDefault(message => message.Role == AiMessageRole.Assistant);
        long generation = ++_sendGeneration;
        _bubbles.Add(BubbleVm.UserBubble(text));
        var pending = BubbleVm.AssistantBubble("", null);
        pending.IsGenerating = true;
        pending.RefreshActivities(_config);
        _bubbles.Add(pending);
        ScrollToBottom();

        _sendCts?.Dispose();
        var sendCts = new CancellationTokenSource();
        _sendCts = sendCts;
        var sendCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendCompletion = sendCompletion;
        var sendToken = sendCts.Token;
        SetSendingState(true);
        // Progress 在 UI 上下文接收快照；生成结束后排队到达的旧通知不能污染下一轮回复。
        var progress = new Progress<AiAssistantActivity>(activity =>
        {
            if (generation != _sendGeneration || !_sending || sendToken.IsCancellationRequested ||
                !ReferenceEquals(session, _session) || !_bubbles.Contains(pending)) return;
            // 离开页面仍允许请求完成，但不再触发隐藏页面的布局；返回时统一刷新快照。
            pending.UpdateActivity(activity, _config, refresh: IsLoaded);
            if (activity.Status == AiActivityStatus.Completed) RefreshTaskUi();
        });

        try
        {
            var reply = await service.SendMessageAsync(
                session, text,
                isCrashLogContext: isCrashLogContext,
                forcedModel: null,
                deepThinking: SelectedReasoningLevel is not (AiReasoningLevel.Off or AiReasoningLevel.Fast),
                webSearch: WebSearchCheck.IsChecked == true,
                ct: sendToken,
                progress: progress,
                textProgress: new Progress<string>(value =>
                {
                    if (generation != _sendGeneration || !_sending || sendToken.IsCancellationRequested || !ReferenceEquals(session, _session)) return;
                    pending.DisplayContent = value;
                }),
                usageProgress: new Progress<AiRequestUsage>(_ =>
                {
                    if (generation == _sendGeneration && ReferenceEquals(session, _session)) UpdateTokenBadge();
                }),
                reasoningLevel: SelectedReasoningLevel);

            if (generation != _sendGeneration || !ReferenceEquals(session, _session)) return;
            RememberCurrentSession();
            RebuildBubbles();
            RefreshHistory();
            RouteStatusText.Text = BuildReplyRouteStatus(reply);
        }
        catch (OperationCanceledException)
        {
            if (generation != _sendGeneration || !ReferenceEquals(session, _session)) return;
            service.SaveSession(session);
            RememberCurrentSession();
            RebuildBubbles();
            RefreshHistory();
            AddFailureBubbleIfMissing(pending, "已停止生成。", previousAssistant);
            RouteStatusText.Text = "生成已停止";
        }
        catch (Exception ex)
        {
            if (generation != _sendGeneration || !ReferenceEquals(session, _session)) return;
            service.SaveSession(session);
            RememberCurrentSession();
            RebuildBubbles();
            RefreshHistory();
            var friendly = FriendlyAiError(ex.Message);
            AddFailureBubbleIfMissing(pending, "出错了：" + friendly, previousAssistant);
            RouteStatusText.Text = ex.Message.Contains("模型 ID", StringComparison.OrdinalIgnoreCase)
                ? "模型配置错误 · 请检查 model ID"
                : "请求失败";
        }
        finally
        {
            sendCompletion.TrySetResult(true);
            if (ReferenceEquals(_sendCompletion, sendCompletion)) _sendCompletion = null;
            if (generation == _sendGeneration)
            {
                ++_sendGeneration;
                pending.IsGenerating = false;
                SetSendingState(false);
                UpdateTokenBadge();
                ScrollToBottom();
                if (ReferenceEquals(_sendCts, sendCts)) _sendCts = null;
            }
            sendCts.Dispose();
        }
    }

    private void RefreshActivityVisibility()
    {
        foreach (var bubble in _bubbles) bubble.RefreshActivities(_config);
    }

    private void AddFailureBubbleIfMissing(BubbleVm pending, string text, AiChatMessage? previousAssistant)
    {
        // 服务会保存带执行记录的中止/错误回复。只为尚未建立该回复的早期失败补显示，避免重复。
        var last = _session.Messages.LastOrDefault();
        if (last?.Role == AiMessageRole.Assistant && !ReferenceEquals(last, previousAssistant)) return;
        pending.DisplayContent = text;
        pending.RefreshActivities(_config);
        _bubbles.Add(pending);
    }

    private void SetSendingState(bool sending)
    {
        _sending = sending;
        RefreshTaskUi();
        if (sending) { _sendStartedUtc = DateTime.UtcNow; _waitTimer.Start(); }
        else _waitTimer.Stop();
        UpdateWaitHint();
        SendButton.IsEnabled = !sending;
        StopButton.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = sending;
        RoutingModeCombo.IsEnabled = !sending;
        ReasoningLevelCombo.IsEnabled = !sending;
        WorkModeCombo.IsEnabled = !sending;
        NewChatButton.IsEnabled = !sending;
        HistoryListBox.IsEnabled = !sending;
        UpdateModelSelectionState();
    }

    private string BuildReplyRouteStatus(AiChatMessage reply)
    {
        var model = reply.ModelDisplayName ?? reply.ModelUsed ?? "未知模型";
        var route = reply.RouteUsed switch
        {
            AiRoutingMode.Expert => "专家",
            AiRoutingMode.SpecificModel => "指定模型",
            _ => "普通"
        };
        return reply.WasAutoRouted ? $"自动 → {route} · {model}" : $"{route} · {model}";
    }

    private static string FriendlyAiError(string errorMsg)
    {
        if (errorMsg.Contains("policy", StringComparison.OrdinalIgnoreCase) ||
            errorMsg.Contains("violation", StringComparison.OrdinalIgnoreCase) ||
            errorMsg.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            errorMsg.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
            errorMsg.Contains("违规", StringComparison.OrdinalIgnoreCase) ||
            errorMsg.Contains("被拦截", StringComparison.OrdinalIgnoreCase) ||
            errorMsg.Contains("封禁", StringComparison.OrdinalIgnoreCase))
            return "可能是发送内容被提供商的安全策略拦截。";
        return errorMsg;
    }

    private void NewChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending) return;

        // 当前已经是一个还没有任何对话内容的新会话（只有系统提示词那条 System 消息）时，
        // 再点"新对话"没有意义，只会在历史列表里堆出一堆空会话——直接提示并返回。
        if (IsCurrentSessionEmpty())
        {
            ToastService.ShowInfo("已经处在新会话当中了");
            InputTextBox.Focus();
            return;
        }

        _session = _service.CreateNewSession();
        RememberCurrentSession();
        RebuildBubbles();
        RefreshHistory();
        UpdateRoutingText();
        InputTextBox.Focus();
    }

    /// <summary>当前会话里没有任何用户/助手消息（只剩系统提示词）就算"新会话"。</summary>
    private bool IsCurrentSessionEmpty()
        => _session.Messages == null || _session.Messages.All(m => m.Role == AiMessageRole.System);

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistorySelection || _service == null || _sending) return;
        if (HistoryListBox.SelectedItem is not AiSessionListItemVm item) return;
        if (item.Session.Id == _session.Id) return;

        _session = _service.LoadSession(item.Session.Id) ?? item.Session;
        RememberCurrentSession();
        RebuildBubbles();
        RefreshHistory();
        UpdateRoutingText();
    }

    private void RenameSession_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending || sender is not Button { Tag: AiChatSession target }) return;
        var dialog = new RenameInstanceDialog(target.Title, _ => false, "重命名对话");
        if (OverlayDialogService.ShowModal(dialog) != true) return;

        target.Title = dialog.NewName.Trim();
        _service.SaveSession(target);
        if (target.Id == _session.Id) _session.Title = target.Title;
        RefreshHistory();
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _sending || sender is not Button { Tag: AiChatSession target }) return;
        if (!MessageBoxDialog.ShowConfirm($"确定删除对话“{target.Title}”吗？\n\n删除后无法恢复。", "删除对话")) return;

        _service.DeleteSession(target.Id);
        if (target.Id == _session.Id)
            _session = _service.ListSessions().FirstOrDefault() ?? _service.CreateNewSession();
        RememberCurrentSession();
        RebuildBubbles();
        RefreshHistory();
        UpdateRoutingText();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BubbleVm vm })
        {
            Clipboard.SetText(vm.DisplayContent);
            ToastService.ShowSuccess("已复制");
        }
    }

    private void UndoMessage_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || sender is not Button { Tag: BubbleVm vm } || vm.OriginalMessage == null || _service == null) return;
        int idx = _session.Messages.IndexOf(vm.OriginalMessage);
        if (idx < 0) return;

        // 需求变更：撤回不再是"直接从对话里删掉这条消息"，而是把这条消息的原文放回待发送框，
        // 让用户可以改一改再重新发送。待发送框如果已经有用户还没发出去的内容，不能悄悄覆盖掉，
        // 弹窗问清楚要怎么处理（替换 / 替换并复制 / 不替换并复制 / 取消）。
        string recalledText = vm.OriginalMessage.Content;
        string existingInput = InputTextBox.Text;

        if (existingInput.Length > 0)
        {
            var dialog = new InputConflictChoiceDialog();
            if (OverlayDialogService.ShowModal(dialog) != true || dialog.Choice == InputConflictChoice.Cancel)
                return;

            switch (dialog.Choice)
            {
                case InputConflictChoice.Replace:
                    InputTextBox.Text = recalledText;
                    break;
                case InputConflictChoice.ReplaceAndCopy:
                    Clipboard.SetText(existingInput);
                    InputTextBox.Text = recalledText;
                    ToastService.ShowSuccess("原内容已复制到剪贴板");
                    break;
                case InputConflictChoice.KeepAndCopy:
                    Clipboard.SetText(recalledText);
                    ToastService.ShowSuccess("撤回内容已复制到剪贴板");
                    return; // 不替换、不移除消息，用户只是想要一份撤回内容的副本。
            }
        }
        else
        {
            InputTextBox.Text = recalledText;
        }

        InputTextBox.CaretIndex = InputTextBox.Text.Length;
        InputTextBox.Focus();

        int count = 1;
        if (idx + 1 < _session.Messages.Count && _session.Messages[idx + 1].Role == AiMessageRole.Assistant)
            count++;
        _session.Messages.RemoveRange(idx, count);
        _service.SaveSession(_session);
        RebuildBubbles();
        RefreshHistory();
    }

    /// <summary>点击“修复”：不再直接删消息重问，而是让这条用户消息原地进入可编辑状态
    /// （气泡内容换成输入框 + “会创建新分支”的提示条），具体保存/取消由下面两个处理器负责。</summary>
    private void FixMessage_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || sender is not Button { Tag: BubbleVm vm } || vm.OriginalMessage == null) return;
        foreach (var b in _bubbles)
            b.IsEditing = false; // 同一时间只允许编辑一条，避免多个输入框同时开着。
        vm.EditingText = vm.OriginalMessage.Content;
        vm.IsEditing = true;
    }

    private void CancelEditMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BubbleVm vm }) vm.IsEditing = false;
    }

    /// <summary>保存“修复”编辑：不修改/覆盖原会话，而是从这条消息之前的历史克隆出一个新会话
    /// （即“分支”），分支里用编辑后的文本替换这条消息并重新请求回复，原对话完整保留不受影响。</summary>
    private async void SaveEditMessage_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || sender is not Button { Tag: BubbleVm vm } || vm.OriginalMessage == null || _service == null) return;
        var newText = (vm.EditingText ?? "").Trim();
        if (newText.Length == 0) return;

        int idx = _session.Messages.IndexOf(vm.OriginalMessage);
        if (idx < 0) { vm.IsEditing = false; return; }

        var branch = new AiChatSession
        {
            Title = BuildBranchTitle(_session.Title),
            IsGuestSession = _session.IsGuestSession,
            Messages = _session.Messages.Take(idx).Select(CloneMessage).ToList()
        };
        _service.SaveSession(branch);

        vm.IsEditing = false;
        _session = branch;
        RememberCurrentSession();
        RebuildBubbles();
        RefreshHistory();
        UpdateRoutingText();

        await GenerateAsync(newText, isCrashLogContext: false);
    }

    private static AiChatMessage CloneMessage(AiChatMessage m) => new()
    {
        Role = m.Role,
        Content = m.Content,
        ModelUsed = m.ModelUsed,
        ModelDisplayName = m.ModelDisplayName,
        RouteUsed = m.RouteUsed,
        WasAutoRouted = m.WasAutoRouted,
        EstimatedTokens = m.EstimatedTokens,
        Activities = (m.Activities ?? new List<AiAssistantActivity>()).Select(activity => activity.Clone()).ToList()
    };

    /// <summary>分支会话标题：延续原标题并加“（分支）”后缀；如果原标题已经带这个后缀
    /// （比如在一个分支上继续修复出第二个分支），不重复叠加。</summary>
    private static string BuildBranchTitle(string? title)
    {
        var baseTitle = string.IsNullOrWhiteSpace(title) ? "新对话" : title!.Trim();
        return baseTitle.EndsWith("（分支）", StringComparison.Ordinal) ? baseTitle : baseTitle + "（分支）";
    }

    private async void RegenerateMessage_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || sender is not Button { Tag: BubbleVm vm } || vm.OriginalMessage == null || _service == null) return;
        int assistantIndex = _session.Messages.IndexOf(vm.OriginalMessage);
        if (assistantIndex <= 0) return;

        for (int i = assistantIndex - 1; i >= 0; i--)
        {
            if (_session.Messages[i].Role != AiMessageRole.User) continue;
            string userText = _session.Messages[i].Content;
            _session.Messages.RemoveRange(i, _session.Messages.Count - i);
            _service.SaveSession(_session);
            RebuildBubbles();
            await GenerateAsync(userText, isCrashLogContext: false);
            return;
        }
    }
}

public sealed class AiSessionListItemVm
{
    public AiChatSession Session { get; }
    public string Title => string.IsNullOrWhiteSpace(Session.Title) ? "新对话" : Session.Title;
    public string UpdatedText => Session.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public AiSessionListItemVm(AiChatSession session) => Session = session;
}

/// <summary>聊天气泡显示模型。实现 INotifyPropertyChanged 是因为“修复”需要在气泡原地
/// 切换编辑态（IsEditing）——ObservableCollection 只在增删条目时通知界面，单条目内部属性
/// 变化必须自己发通知，否则点“修复”后气泡不会变成输入框。</summary>
public partial class BubbleVm : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    private string _displayContent = "";
    public string DisplayContent { get => _displayContent; set { _displayContent = value; Raise(nameof(DisplayContent)); } }
    private bool _isGenerating;
    public bool IsGenerating { get => _isGenerating; set { _isGenerating = value; Raise(nameof(IsGenerating)); Raise(nameof(ReasoningVisibility)); } }
    public string SubLabel { get; set; } = "";
    public Visibility SubLabelVisibility { get; set; } = Visibility.Collapsed;
    public HorizontalAlignment BubbleAlignment { get; set; }
    public Brush BubbleBrush { get; set; } = Brushes.Transparent;
    public bool IsUserMessage => BubbleAlignment == HorizontalAlignment.Right;
    public bool IsAssistantMessage => BubbleAlignment == HorizontalAlignment.Left;
    public AiChatMessage? OriginalMessage { get; set; }

    private bool _isEditing;
    /// <summary>是否处于“修复”编辑态。只对用户消息气泡有意义，但不额外限制——
    /// XAML 侧“修复”按钮本身就只在 IsUserMessage 时可见/可点。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            Raise(nameof(IsEditing));
            Raise(nameof(IsNotEditing));
        }
    }

    /// <summary>给只需要“非编辑态”的绑定用（正文只读视图、操作按钮栏），避免在 XAML 里
    /// 写取反转换器。</summary>
    public bool IsNotEditing => !IsEditing;

    private string _editingText = "";
    /// <summary>编辑框里的临时文本，跟 DisplayContent 分开：取消编辑时不应该污染原文本，
    /// 只有点“保存”才会真正拿这个值去创建分支。</summary>
    public string EditingText
    {
        get => _editingText;
        set
        {
            if (_editingText == value) return;
            _editingText = value;
            Raise(nameof(EditingText));
        }
    }

    public static BubbleVm From(AiChatMessage m, AiAssistantConfig? config = null)
    {
        bool isUser = m.Role == AiMessageRole.User;
        var bubble = new BubbleVm
        {
            DisplayContent = m.Content,
            BubbleAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BubbleBrush = isUser
                ? new SolidColorBrush(Color.FromArgb(0x2D, 0x18, 0x68, 0xE8))
                : new SolidColorBrush(Color.FromArgb(0x16, 0x80, 0x90, 0xA0)),
            SubLabel = isUser ? "" : BuildModelLabel(m),
            SubLabelVisibility = !isUser && !string.IsNullOrWhiteSpace(m.ModelUsed) ? Visibility.Visible : Visibility.Collapsed,
            OriginalMessage = m
        };
        bubble.LoadActivities(m.Activities, config ?? new AiAssistantConfig());
        return bubble;
    }

    public static BubbleVm UserBubble(string text) => new()
    {
        DisplayContent = text,
        BubbleAlignment = HorizontalAlignment.Right,
        BubbleBrush = new SolidColorBrush(Color.FromArgb(0x2D, 0x18, 0x68, 0xE8))
    };

    public static BubbleVm AssistantBubble(string text, string? model) => new()
    {
        DisplayContent = text,
        BubbleAlignment = HorizontalAlignment.Left,
        BubbleBrush = new SolidColorBrush(Color.FromArgb(0x16, 0x80, 0x90, 0xA0)),
        SubLabel = model ?? "",
        SubLabelVisibility = string.IsNullOrWhiteSpace(model) ? Visibility.Collapsed : Visibility.Visible
    };

    private static string BuildModelLabel(AiChatMessage m)
    {
        var display = m.ModelDisplayName;
        if (string.IsNullOrWhiteSpace(display))
        {
            var builtIn = AiModelIds.BuiltIn.FirstOrDefault(x =>
                string.Equals(x.Id, m.ModelUsed, StringComparison.OrdinalIgnoreCase));
            display = builtIn?.DisplayName ?? m.ModelUsed ?? "";
        }

        var route = m.RouteUsed switch
        {
            AiRoutingMode.Expert => "专家",
            AiRoutingMode.SpecificModel => "指定",
            _ => "普通"
        };
        return m.WasAutoRouted ? $"自动→{route} · {display}" : $"{route} · {display}";
    }
}
