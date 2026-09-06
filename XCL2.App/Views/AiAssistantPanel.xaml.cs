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
    private bool _sending;
    private bool _suppressRoutingSelection;
    private bool _suppressModelSelection;
    private bool _suppressHistorySelection;
    private MainWindow? _owner;

    public AiAssistantPanel()
    {
        InitializeComponent();
        MessagesItemsControl.ItemsSource = _bubbles;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this) as MainWindow;
        _owner?.UpdateAiFloatingButtonVisibility(false);
        RememberCurrentSession();
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
        if (_config.PrewarmSystemPromptOnOpen)
            _ = _service.WarmUpAsync();
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

        DeepThinkingCheck.IsChecked = _config.EnableDeepThinking;
        WebSearchCheck.IsChecked = _config.EnableWebSearch;
        RefreshRoutingUi();
        RefreshModelCatalog();
        UpdateRoutingText();
        UpdateConfigHint();
        UpdateTokenBadge();
    }

    public async Task SubmitExternalPromptAsync(string text, string sessionTitle = "日志分析", bool isCrashLogContext = false)
    {
        if (_service == null || string.IsNullOrWhiteSpace(text)) return;
        _session = _service.CreateNewSession(string.IsNullOrWhiteSpace(sessionTitle) ? "日志分析" : sessionTitle);
        RememberCurrentSession();
        RebuildBubbles();
        RefreshHistory();
        InputTextBox.Text = text;
        await SendCurrentInputAsync(isCrashLogContext);
    }

    private void NormalizeConfigModels()
    {
        _config.CustomModels ??= new List<AiModelDefinition>();
        var catalog = AiModelIds.GetCatalog(_config);
        if (catalog.Count == 0) return;

        string Pick(string? value, string builtInFallback)
        {
            var match = catalog.FirstOrDefault(m => string.Equals(m.Id, value, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Id;
            if (!_config.UseCustomApiKey)
            {
                var fallback = catalog.FirstOrDefault(m => string.Equals(m.Id, builtInFallback, StringComparison.OrdinalIgnoreCase));
                if (fallback != null) return fallback.Id;
            }
            return catalog[0].Id;
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
            ModelCombo.SelectedValue = catalog.Any(m => string.Equals(m.Id, _config.SelectedModel, StringComparison.OrdinalIgnoreCase))
                ? _config.SelectedModel
                : catalog.FirstOrDefault()?.Id;
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

        var normal = AiModelIds.GetDisplayName(_config, _config.NormalModelId);
        var expert = AiModelIds.GetDisplayName(_config, _config.ExpertModelId);
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
        _bubbles.Clear();
        foreach (var m in _session.Messages.Where(m => m.Role != AiMessageRole.System))
            _bubbles.Add(BubbleVm.From(m));
        UpdateTokenBadge();
        ScrollToBottom();
    }

    private void UpdateTokenBadge()
    {
        int total = _session.Messages.Sum(m => m.EstimatedTokens);
        TokenUsageText.Text = $"{total} / {_config.CompressionTokenThreshold} tok";
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
        UpdateRoutingText();
        PersistQuickConfig();
    }

    private void QuickOption_Click(object sender, RoutedEventArgs e)
    {
        _config.EnableDeepThinking = DeepThinkingCheck.IsChecked == true;
        _config.EnableWebSearch = WebSearchCheck.IsChecked == true;
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
        var normal = AiModelIds.GetDisplayName(_config, _config.NormalModelId);
        var expert = AiModelIds.GetDisplayName(_config, _config.ExpertModelId);
        var selected = AiModelIds.GetDisplayName(_config, _config.SelectedModel);

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
    }

    private void UpdateConfigHint()
    {
        bool customMissing = _config.UseCustomApiKey &&
            (string.IsNullOrWhiteSpace(_config.ApiKey) || AiModelIds.GetCatalog(_config).Count == 0);
        NoKeyHint.Visibility = customMissing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SendCurrentInputAsync(bool isCrashLogContext = false)
    {
        if (_sending || _service == null) return;
        var text = InputTextBox.Text.Trim();
        if (text.Length == 0) return;

        if (_config.UseCustomApiKey &&
            (string.IsNullOrWhiteSpace(_config.ApiKey) || AiModelIds.GetCatalog(_config).Count == 0))
        {
            NoKeyHint.Visibility = Visibility.Visible;
            return;
        }

        InputTextBox.Text = "";
        await GenerateAsync(text, isCrashLogContext);
    }

    private async Task GenerateAsync(string text, bool isCrashLogContext)
    {
        if (_service == null || _sending) return;

        _bubbles.Add(BubbleVm.UserBubble(text));
        _bubbles.Add(BubbleVm.AssistantBubble("正在生成回答…", null));
        ScrollToBottom();

        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        SetSendingState(true);

        try
        {
            var reply = await _service.SendMessageAsync(
                _session, text,
                isCrashLogContext: isCrashLogContext,
                forcedModel: null,
                deepThinking: DeepThinkingCheck.IsChecked == true,
                webSearch: WebSearchCheck.IsChecked == true,
                ct: _sendCts.Token);

            RememberCurrentSession();
            RebuildBubbles();
            RefreshHistory();
            RouteStatusText.Text = BuildReplyRouteStatus(reply);
        }
        catch (OperationCanceledException)
        {
            _service.SaveSession(_session);
            RememberCurrentSession();
            RebuildBubbles();
            RefreshHistory();
            _bubbles.Add(BubbleVm.AssistantBubble("已停止生成。", null));
            RouteStatusText.Text = "生成已停止";
        }
        catch (Exception ex)
        {
            _service.SaveSession(_session);
            RememberCurrentSession();
            RebuildBubbles();
            RefreshHistory();
            var friendly = FriendlyAiError(ex.Message);
            _bubbles.Add(BubbleVm.AssistantBubble("出错了：" + friendly, null));
            RouteStatusText.Text = ex.Message.Contains("模型 ID", StringComparison.OrdinalIgnoreCase)
                ? "模型配置错误 · 请检查 model ID"
                : "请求失败";
        }
        finally
        {
            SetSendingState(false);
            UpdateTokenBadge();
            ScrollToBottom();
            _sendCts?.Dispose();
            _sendCts = null;
        }
    }

    private void SetSendingState(bool sending)
    {
        _sending = sending;
        SendButton.IsEnabled = !sending;
        StopButton.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = sending;
        RoutingModeCombo.IsEnabled = !sending;
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
        _session = _service.CreateNewSession();
        RememberCurrentSession();
        RebuildBubbles();
        RefreshHistory();
        UpdateRoutingText();
        InputTextBox.Focus();
    }

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

        int count = 1;
        if (idx + 1 < _session.Messages.Count && _session.Messages[idx + 1].Role == AiMessageRole.Assistant)
            count++;
        _session.Messages.RemoveRange(idx, count);
        _service.SaveSession(_session);
        RebuildBubbles();
        RefreshHistory();
    }

    private async void FixMessage_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || sender is not Button { Tag: BubbleVm vm } || vm.OriginalMessage == null || _service == null) return;
        int idx = _session.Messages.IndexOf(vm.OriginalMessage);
        if (idx < 0) return;

        string originalText = vm.OriginalMessage.Content;
        int removeCount = 1;
        if (idx + 1 < _session.Messages.Count && _session.Messages[idx + 1].Role == AiMessageRole.Assistant)
            removeCount++;
        _session.Messages.RemoveRange(idx, removeCount);
        _service.SaveSession(_session);
        RebuildBubbles();
        await GenerateAsync($"请修复/改进以下内容：\n{originalText}", isCrashLogContext: false);
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

/// <summary>聊天气泡显示模型。</summary>
public class BubbleVm
{
    public string DisplayContent { get; set; } = "";
    public string SubLabel { get; set; } = "";
    public Visibility SubLabelVisibility { get; set; } = Visibility.Collapsed;
    public HorizontalAlignment BubbleAlignment { get; set; }
    public Brush BubbleBrush { get; set; } = Brushes.Transparent;
    public bool IsUserMessage => BubbleAlignment == HorizontalAlignment.Right;
    public bool IsAssistantMessage => BubbleAlignment == HorizontalAlignment.Left;
    public AiChatMessage? OriginalMessage { get; set; }

    public static BubbleVm From(AiChatMessage m)
    {
        bool isUser = m.Role == AiMessageRole.User;
        return new BubbleVm
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
