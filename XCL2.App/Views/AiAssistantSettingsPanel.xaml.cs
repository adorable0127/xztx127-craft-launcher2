using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel : UserControl, IOverlayDialog
{
    public event EventHandler<AiAssistantConfig>? Saved;
    public event EventHandler? Cancelled;
    public event EventHandler<bool?>? RequestClose;

    private readonly AiAssistantConfig _currentConfig;
    private readonly ObservableCollection<AiModelDefinition> _customModels = new();
    private bool _loading;
    private string? _lastNormalId;
    private string? _lastExpertId;
    private string? _lastManualId;

    public AiAssistantSettingsPanel(AiAssistantConfig currentConfig)
    {
        InitializeComponent();
        _currentConfig = currentConfig;
        CustomModelsList.ItemsSource = _customModels;
        LoadConfig(currentConfig);
    }

    private void LoadConfig(AiAssistantConfig config)
    {
        _loading = true;
        try
        {
            EnabledCheck.IsChecked = config.Enabled;
            FloatingButtonCheck.IsChecked = config.ShowFloatingButton;
            UseCustomKeyCheck.IsChecked = config.UseCustomApiKey;
            BaseUrlBox.Text = config.BaseUrl;
            ApiKeyBox.Password = config.ApiKey;
            WorkingDirectoryBox.Text = config.WorkingDirectory ?? "";

            _customModels.Clear();
            foreach (var item in (config.CustomModels ?? new List<AiModelDefinition>())
                         .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id)))
            {
                _customModels.Add(new AiModelDefinition
                {
                    Id = item.Id.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Id.Trim() : item.DisplayName.Trim(),
                    ProviderBaseUrl = string.IsNullOrWhiteSpace(item.ProviderBaseUrl) ? null : item.ProviderBaseUrl.Trim(),
                    ProviderApiKey = item.ProviderApiKey,
                    Tier = item.Tier
                });
            }

            var mode = config.RoutingMode;
            if (mode == AiRoutingMode.Auto && !config.AutoModelRouting)
                mode = AiRoutingMode.SpecificModel;
            SelectRoutingMode(mode);

            ThresholdBox.Text = config.CompressionTokenThreshold.ToString();
            AllowCrashLogCheck.IsChecked = config.AllowCrashLogReading;
            ClearGuestHistoryCheck.IsChecked = config.ClearHistoryOnGuestSessionEnd;
            PrewarmOnOpenCheck.IsChecked = config.PrewarmSystemPromptOnOpen;
            EnableDeepThinkingCheck.IsChecked = config.EnableDeepThinking;
            EnableWebSearchCheck.IsChecked = config.EnableWebSearch;
            TokenSaverCheck.IsChecked = config.TokenSaverMode;

            UpdateCustomApiUi();
            RefreshModelCombos(config.NormalModelId, config.ExpertModelId, config.SelectedModel);
            RefreshTokenSaverAvailability();
        }
        finally
        {
            _loading = false;
        }

        // 打开设置页时，如果之前保存的默认模型里有已知有问题的，直接弹一次窗提醒，
        // 不用等用户手动点开下拉框才发现——同一次打开只弹一次，把三个位置的提示合并。
        var loadedIssues = new[] { config.NormalModelId, config.ExpertModelId, config.SelectedModel }
            .Select(id => (id, notice: AiModelIds.GetKnownIssueNotice(id)))
            .Where(x => !string.IsNullOrWhiteSpace(x.notice))
            .GroupBy(x => x.notice)
            .Select(g => g.First())
            .ToList();
        if (loadedIssues.Count > 0)
        {
            var body = string.Join("\n\n", loadedIssues.Select(x =>
                $"“{AiModelIds.GetDisplayName(config, x.id)}”：{x.notice}"));
            MessageBoxDialog.ShowWarning($"当前配置里有模型已知存在问题，建议尽快更换：\n\n{body}", "模型暂不可用");
        }
    }

    private void SelectRoutingMode(AiRoutingMode mode)
    {
        foreach (ComboBoxItem item in DefaultModeCombo.Items)
        {
            if (item.Tag is string tag && Enum.TryParse<AiRoutingMode>(tag, out var parsed) && parsed == mode)
            {
                DefaultModeCombo.SelectedItem = item;
                return;
            }
        }
        DefaultModeCombo.SelectedIndex = 0;
    }

    private AiRoutingMode GetRoutingMode()
    {
        if (DefaultModeCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<AiRoutingMode>(tag, out var mode))
            return mode;
        return AiRoutingMode.Auto;
    }

    private void UseCustomKeyCheck_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var normal = NormalModelCombo.SelectedValue as string;
        var expert = ExpertModelCombo.SelectedValue as string;
        var manual = ManualModelCombo.SelectedValue as string;
        UpdateCustomApiUi();
        RefreshModelCombos(normal, expert, manual);
        RefreshTokenSaverAvailability();
    }

    private void DefaultModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshTokenSaverAvailability();
    }

    private void TokenSaverCheck_CheckedChanged(object sender, RoutedEventArgs e)
    {
        // 只负责响应用户点击；实际是否生效仍以 RefreshTokenSaverAvailability 里算出的
        // "允许打开" 状态为准（勾选框在不允许时本身就是禁用的，点不到）。
    }

    /// <summary>省 Token 模式只在"使用自定义 API key"或"路由模式=自动"时能打开，
    /// 见 AiAssistantConfig.TokenSaverModeAllowed 的注释。这里同步一份到设置页 UI：
    /// 条件不满足时勾选框直接禁用，并把提示文案换成"为什么现在不能开"。</summary>
    private void RefreshTokenSaverAvailability()
    {
        bool allowed = UseCustomKeyCheck.IsChecked == true || GetRoutingMode() == AiRoutingMode.Auto;
        TokenSaverCheck.IsEnabled = allowed;
        TokenSaverHintText.Text = allowed
            ? "上下文压缩更激进，遇到官网/百科/下载地址这类固定问答直接本地回答、不请求 API。"
            : "只在“使用自定义 API key”或“默认路由模式=自动”时可以开启（跟省 token 这个目标同一个方向）；" +
              "手动指定模型/普通档/专家档下不提供这个开关。";
    }

    private void UpdateCustomApiUi()
    {
        bool custom = UseCustomKeyCheck.IsChecked == true;
        BaseUrlBox.IsEnabled = custom;
        ApiKeyBox.IsEnabled = custom;
        CustomModelsEditor.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        BuiltInModelsHintPanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
    }

    private List<AiModelDefinition> CurrentCatalog()
    {
        if (UseCustomKeyCheck.IsChecked == true)
            return _customModels.Select(m => m.Clone()).ToList();

        // 全部内置模型都展示出来（而不是只展示 3 个可用的），这样用户能看到有哪些模型存在；
        // 暂不可用的在显示名上标出来，选中时由 ModelCombo_SelectionChanged 拦截并提示原因。
        return AiModelIds.BuiltIn.Select(m =>
        {
            var clone = m.Clone();
            if (!AiModelIds.IsAvailableByDefault(clone.Id))
                clone.DisplayName += "（暂不可用）";
            return clone;
        }).ToList();
    }

    private void RefreshModelCombos(string? normalId = null, string? expertId = null, string? manualId = null)
    {
        var catalog = CurrentCatalog();
        NormalModelCombo.ItemsSource = catalog;
        ExpertModelCombo.ItemsSource = catalog;
        ManualModelCombo.ItemsSource = catalog;

        string? Pick(string? wanted, string builtInFallback)
        {
            if (!string.IsNullOrWhiteSpace(wanted) && catalog.Any(m => string.Equals(m.Id, wanted, StringComparison.OrdinalIgnoreCase)))
                return catalog.First(m => string.Equals(m.Id, wanted, StringComparison.OrdinalIgnoreCase)).Id;
            if (UseCustomKeyCheck.IsChecked != true && catalog.Any(m => m.Id == builtInFallback))
                return builtInFallback;
            return catalog.FirstOrDefault()?.Id;
        }

        NormalModelCombo.SelectedValue = Pick(normalId, AiModelIds.NemotronLightning);
        ExpertModelCombo.SelectedValue = Pick(expertId, AiModelIds.Nemotron3Ultra);
        ManualModelCombo.SelectedValue = Pick(manualId, AiModelIds.NemotronLightning);

        _lastNormalId = NormalModelCombo.SelectedValue as string;
        _lastExpertId = ExpertModelCombo.SelectedValue as string;
        _lastManualId = ManualModelCombo.SelectedValue as string;

        RefreshSpecialTermsNotice();
    }

    /// <summary>内置模型现在都视为可用，这里只需要记录一下"上一次选中的值"，
    /// 供别处（比如新增/删除自定义模型后重建下拉框）恢复选中项用；同时刷新独立条款提示，
    /// 选中已知有问题的模型时额外弹窗提醒一次（不只是被动展示一条小字提示，容易被忽略）。</summary>
    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ComboBox combo) return;

        var selectedId = combo.SelectedValue as string;

        if (combo == NormalModelCombo) _lastNormalId = selectedId;
        else if (combo == ExpertModelCombo) _lastExpertId = selectedId;
        else if (combo == ManualModelCombo) _lastManualId = selectedId;

        RefreshSpecialTermsNotice();
        WarnIfKnownIssue(selectedId);
    }

    /// <summary>选中的模型如果在 AiModelIds.KnownIssueNotices 里，弹一个警告框说明原因，
    /// 而不是只在下拉框下面放一行小字——小字很容易被忽略，导致用户选了之后发消息才发现
    /// 大概率会失败，还得自己排查一圈才明白是模型本身的问题不是配置错误。</summary>
    private void WarnIfKnownIssue(string? modelId)
    {
        var notice = AiModelIds.GetKnownIssueNotice(modelId);
        if (string.IsNullOrWhiteSpace(notice)) return;

        var displayName = AiModelIds.GetDisplayName(_currentConfig, modelId);
        MessageBoxDialog.ShowWarning($"“{displayName}”目前已知有问题：\n\n{notice}", "模型暂不可用");
    }

    /// <summary>三个模型下拉框里只要有一个选中了带独立条款的模型，或者选中了目前已知有问题
    /// （被供应商限制/常限流）的模型，就把提示条显示出来；多条提示拼在一起（去重），
    /// 避免用户漏看某一个。</summary>
    private void RefreshSpecialTermsNotice()
    {
        if (SpecialTermsNoticeBox == null || SpecialTermsNoticeText == null) return;

        var ids = new[] { NormalModelCombo?.SelectedValue as string, ExpertModelCombo?.SelectedValue as string, ManualModelCombo?.SelectedValue as string };
        var notices = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .SelectMany(id => new[] { AiModelIds.GetSpecialTermsNotice(id), AiModelIds.GetKnownIssueNotice(id) })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();

        if (notices.Count == 0)
        {
            SpecialTermsNoticeBox.Visibility = Visibility.Collapsed;
            SpecialTermsNoticeText.Text = "";
            return;
        }

        SpecialTermsNoticeText.Text = string.Join("\n\n", notices);
        SpecialTermsNoticeBox.Visibility = Visibility.Visible;
    }

    private void AddCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeModelId(CustomModelIdBox.Text, out var id, out var modelIdError))
        {
            MessageBoxDialog.ShowWarning(modelIdError, "AI 模型");
            return;
        }

        var name = CustomModelNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBoxDialog.ShowWarning("请填写模型显示名称。显示名称只用于界面，真正提交给 API 的是左侧模型 ID。", "AI 模型");
            return;
        }

        // 供应商地址/Key 是可选的（多供应商）：都留空表示这个模型沿用上方公共的 Base URL / API Key；
        // 只要填了其中一个就要求两个都填，避免"填了地址没填 Key"这种半成品配置在请求时被悄悄忽略。
        var providerBaseUrlRaw = CustomModelProviderBaseUrlBox.Text?.Trim() ?? "";
        var providerApiKeyRaw = CustomModelProviderApiKeyBox.Password ?? "";
        string? providerBaseUrl = null;
        string? providerApiKey = null;
        if (providerBaseUrlRaw.Length > 0 || providerApiKeyRaw.Length > 0)
        {
            if (!TryNormalizeBaseUrl(providerBaseUrlRaw, out var normalizedProviderUrl))
            {
                MessageBoxDialog.ShowWarning("这个模型单独填写的供应商地址无效。请填写 http/https Base URL，或把两个字段都留空以沿用上方公共接口。", "AI 模型");
                return;
            }
            if (providerApiKeyRaw.Length == 0)
            {
                MessageBoxDialog.ShowWarning("填写了单独的供应商地址，就必须同时填这个模型自己的 API Key（或者两个都留空）。", "AI 模型");
                return;
            }
            providerBaseUrl = normalizedProviderUrl;
            providerApiKey = providerApiKeyRaw;
        }

        var tier = CustomModelTierExpertRadio.IsChecked == true ? AiModelTier.Expert : AiModelTier.Normal;

        var existing = _customModels.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.DisplayName = name;
            existing.ProviderBaseUrl = providerBaseUrl;
            existing.ProviderApiKey = providerApiKey;
            existing.Tier = tier;
            CustomModelsList.Items.Refresh();
        }
        else
        {
            _customModels.Add(new AiModelDefinition
            {
                Id = id,
                DisplayName = name,
                ProviderBaseUrl = providerBaseUrl,
                ProviderApiKey = providerApiKey,
                Tier = tier
            });
        }

        CustomModelIdBox.Clear();
        CustomModelNameBox.Clear();
        CustomModelProviderBaseUrlBox.Clear();
        CustomModelProviderApiKeyBox.Clear();
        CustomModelTierNormalRadio.IsChecked = true;
        RefreshModelCombos(
            NormalModelCombo.SelectedValue as string,
            ExpertModelCombo.SelectedValue as string,
            ManualModelCombo.SelectedValue as string ?? id);
    }

    private static bool TryNormalizeModelId(string? raw, out string normalized, out string error)
    {
        normalized = (raw ?? string.Empty).Trim();
        error = string.Empty;

        // 很多人会从 JSON / 文档里把 "model-id" 连引号一起复制进来；安全地去掉一层成对引号。
        if (normalized.Length >= 2 &&
            ((normalized[0] == '"' && normalized[^1] == '"') ||
             (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            normalized = normalized[1..^1].Trim();
        }

        if (normalized.Length == 0)
        {
            error = "请填写模型 ID。模型 ID 是 API 文档里的 model 值，不是显示名称。";
            return false;
        }
        if (normalized.Length > 200)
        {
            error = "模型 ID 过长，请检查是否误把整段 URL、JSON 或说明文字粘贴到了模型 ID。";
            return false;
        }
        if (normalized.Any(char.IsWhiteSpace))
        {
            error = "模型 ID 中不能包含空格或换行。请填写 API 实际要求的 model 值，例如 openai/gpt-4.1-mini；显示名称可以包含空格。";
            return false;
        }
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var modelUri) &&
            (modelUri.Scheme == Uri.UriSchemeHttp || modelUri.Scheme == Uri.UriSchemeHttps))
        {
            error = "这里要填模型 ID，不是模型接口 URL。接口地址请填写在上方 Base URL。";
            return false;
        }
        if (normalized.IndexOfAny(new[] { '{', '}', '[', ']', '\r', '\n' }) >= 0)
        {
            error = "模型 ID 看起来像 JSON/数组内容。请只填写 API 的 model 字符串。";
            return false;
        }
        return true;
    }

    private static bool TryNormalizeBaseUrl(string? raw, out string normalized)
    {
        normalized = (raw ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        // 用户若直接粘贴了完整 /chat/completions 地址，内部还会再拼一次；这里自动还原成 Base URL。
        const string suffix = "/chat/completions";
        if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^suffix.Length].TrimEnd('/');
        return true;
    }

    private void RemoveCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiModelDefinition model }) return;
        var normal = NormalModelCombo.SelectedValue as string;
        var expert = ExpertModelCombo.SelectedValue as string;
        var manual = ManualModelCombo.SelectedValue as string;
        _customModels.Remove(model);
        RefreshModelCombos(normal, expert, manual);
    }

    private void BrowseWorkingDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 AI 助手工作目录"
        };
        if (Directory.Exists(WorkingDirectoryBox.Text))
            dialog.InitialDirectory = WorkingDirectoryBox.Text;
        if (dialog.ShowDialog() == true)
            WorkingDirectoryBox.Text = dialog.FolderName;
    }

    private void ClearWorkingDirectory_Click(object sender, RoutedEventArgs e)
        => WorkingDirectoryBox.Clear();

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AiAssistantConfig
        {
            Enabled = false,
            UseCustomApiKey = false,
            BaseUrl = "https://openrouter.ai/api/v1",
            ApiKey = "",
            WorkingDirectory = null,
            RoutingMode = AiRoutingMode.Auto,
            AutoModelRouting = true,
            NormalModelId = AiModelIds.NemotronLightning,
            ExpertModelId = AiModelIds.Nemotron3Ultra,
            SelectedModel = AiModelIds.NemotronLightning,
            CompressionTokenThreshold = 18000,
            ShowFloatingButton = false,
            AllowCrashLogReading = false,
            ClearHistoryOnGuestSessionEnd = true,
            PrewarmSystemPromptOnOpen = true,
            EnableDeepThinking = false,
            EnableWebSearch = false,
            PanelWidth = _currentConfig.PanelWidth,
            PanelWasOpen = _currentConfig.PanelWasOpen,
            LastSessionId = _currentConfig.LastSessionId
        };
        LoadConfig(defaults);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(ThresholdBox.Text, out var threshold);
        bool custom = UseCustomKeyCheck.IsChecked == true;

        if (custom)
        {
            if (!TryNormalizeBaseUrl(BaseUrlBox.Text, out var normalizedBaseUrl))
            {
                MessageBoxDialog.ShowWarning("自定义 API 地址无效。请填写 http/https Base URL，例如 https://example.com/v1。", "AI 设置");
                return;
            }
            BaseUrlBox.Text = normalizedBaseUrl;
            if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                MessageBoxDialog.ShowWarning("使用自定义 API 时必须填写 API Key。", "AI 设置");
                return;
            }

            // 如果填的 Base URL 就是 OpenRouter 自己的接口，自动去拉一份当前可用模型列表同步进
            // 自定义模型表——免得用户还要自己一个个手填模型 ID；同步只做“合并”，不会清空用户已经
            // 手填的其它供应商模型。失败（网络/密钥无效）静默跳过，不阻塞保存，交给下面的
            // “至少一个模型”校验去提示用户。
            if (OpenRouterModelSyncService.IsOpenRouterBaseUrl(normalizedBaseUrl))
            {
                SaveButton.IsEnabled = false;
                try
                {
                    var synced = await OpenRouterModelSyncService
                        .TrySyncIntoAsync(normalizedBaseUrl, ApiKeyBox.Password, _customModels);
                    if (synced)
                    {
                        CustomModelsList.Items.Refresh();
                        ToastService.ShowSuccess($"已从 OpenRouter 同步 {_customModels.Count} 个可用模型");
                    }
                }
                finally
                {
                    SaveButton.IsEnabled = true;
                }
            }

            if (_customModels.Count == 0)
            {
                MessageBoxDialog.ShowWarning("使用自定义 API 时至少添加一个模型，并填写模型 ID 与显示名称。", "AI 设置");
                return;
            }
            foreach (var model in _customModels)
            {
                if (!TryNormalizeModelId(model.Id, out var normalizedId, out var modelIdError))
                {
                    MessageBoxDialog.ShowWarning($"模型“{model.DisplayName}”配置有误：{modelIdError}", "AI 设置");
                    return;
                }
                model.Id = normalizedId;
            }
        }

        var catalog = CurrentCatalog();
        if (catalog.Count == 0)
        {
            MessageBoxDialog.ShowWarning("当前没有可用模型。", "AI 设置");
            return;
        }

        string EnsureSelected(ComboBox combo, string fallback)
        {
            if (combo.SelectedValue is string id && catalog.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                return id;
            return catalog.FirstOrDefault(m => m.Id == fallback)?.Id ?? catalog[0].Id;
        }

        var workingDirectory = WorkingDirectoryBox.Text.Trim();
        if (workingDirectory.Length > 0)
        {
            try { workingDirectory = Path.GetFullPath(workingDirectory); }
            catch
            {
                MessageBoxDialog.ShowWarning("工作目录路径无效，请重新选择。", "AI 设置");
                return;
            }
            if (!Directory.Exists(workingDirectory))
            {
                MessageBoxDialog.ShowWarning("工作目录不存在。请先创建目录，或点击“选择目录”选择一个现有目录。", "AI 设置");
                return;
            }
        }

        var mode = GetRoutingMode();
        var result = new AiAssistantConfig
        {
            Enabled = EnabledCheck.IsChecked == true,
            UseCustomApiKey = custom,
            BaseUrl = BaseUrlBox.Text.Trim(),
            ApiKey = ApiKeyBox.Password,
            WorkingDirectory = workingDirectory.Length == 0 ? null : workingDirectory,
            RoutingMode = mode,
            AutoModelRouting = mode == AiRoutingMode.Auto,
            NormalModelId = EnsureSelected(NormalModelCombo, AiModelIds.NemotronLightning),
            ExpertModelId = EnsureSelected(ExpertModelCombo, AiModelIds.Nemotron3Ultra),
            SelectedModel = EnsureSelected(ManualModelCombo, AiModelIds.NemotronLightning),
            CustomModels = _customModels.Select(m => m.Clone()).ToList(),
            CompressionTokenThreshold = threshold >= 2000 ? threshold : 18000,
            ShowFloatingButton = FloatingButtonCheck.IsChecked == true,
            AllowCrashLogReading = AllowCrashLogCheck.IsChecked == true,
            ClearHistoryOnGuestSessionEnd = ClearGuestHistoryCheck.IsChecked == true,
            PrewarmSystemPromptOnOpen = PrewarmOnOpenCheck.IsChecked == true,
            EnableDeepThinking = EnableDeepThinkingCheck.IsChecked == true,
            EnableWebSearch = EnableWebSearchCheck.IsChecked == true,
            TokenSaverMode = TokenSaverCheck.IsEnabled && TokenSaverCheck.IsChecked == true,
            AcceptedTermsVersion = _currentConfig.AcceptedTermsVersion,
            PanelWidth = _currentConfig.PanelWidth,
            PanelWasOpen = _currentConfig.PanelWasOpen,
            LastSessionId = _currentConfig.LastSessionId
        };

        Saved?.Invoke(this, result);
        RequestClose?.Invoke(this, true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, false);
    }

    // ----- 测试模型：不落盘、不影响正常聊天记录，只是拿当前编辑框里的接口配置发一次最小请求 -----

    private void TestNormalModel_Click(object sender, RoutedEventArgs e) =>
        _ = RunModelTestAsync(NormalModelCombo.SelectedValue as string, TestNormalModelButton);

    private void TestExpertModel_Click(object sender, RoutedEventArgs e) =>
        _ = RunModelTestAsync(ExpertModelCombo.SelectedValue as string, TestExpertModelButton);

    private void TestManualModel_Click(object sender, RoutedEventArgs e) =>
        _ = RunModelTestAsync(ManualModelCombo.SelectedValue as string, TestManualModelButton);

    private void TestCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not AiModelDefinition model) return;
        _ = RunModelTestAsync(model.Id, btn);
    }

    /// <summary>用当前设置面板里"还没保存"的接口配置（BaseUrl/Key/自定义模型表/多供应商覆盖）
    /// 拼一份临时 AiAssistantConfig，去实际发一次最小对话请求，用来验证这个模型现在能不能用——
    /// 不依赖已保存的配置，改了 Key 还没点保存也能测；也不会创建/保留任何聊天记录文件。</summary>
    private async Task RunModelTestAsync(string? modelId, Button triggerButton)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            MessageBoxDialog.ShowWarning("请先选择一个模型。", "测试模型");
            return;
        }

        var originalContent = triggerButton.Content;
        triggerButton.IsEnabled = false;
        triggerButton.Content = "测试中…";
        try
        {
            var draftConfig = BuildDraftConfigForTest();
            var testService = new AiAssistantService(draftConfig, App.DataDir);
            var (ok, message) = await testService.TestModelAsync(modelId);

            var displayName = AiModelIds.GetDisplayName(draftConfig, modelId);
            if (ok)
                await MessageBoxDialog.ShowSuccessAsync(message, $"“{displayName}” 测试通过");
            else
                await MessageBoxDialog.ShowErrorAsync(message, $"“{displayName}” 测试失败");
        }
        catch (Exception ex)
        {
            await MessageBoxDialog.ShowErrorAsync(ex.Message, "测试模型");
        }
        finally
        {
            triggerButton.IsEnabled = true;
            triggerButton.Content = originalContent;
        }
    }

    /// <summary>拿当前编辑框里的值（不做 Save_Click 那种严格校验/报错，测试用途尽量"能测就测"）
    /// 拼一份临时配置。CustomModels 里模型 ID 简单 trim 一下，不合法的 ID 交给实际请求去报错即可，
    /// 没必要在这里重复一遍 Save_Click 的校验逻辑。</summary>
    private AiAssistantConfig BuildDraftConfigForTest()
    {
        return new AiAssistantConfig
        {
            Enabled = true,
            UseCustomApiKey = UseCustomKeyCheck.IsChecked == true,
            BaseUrl = BaseUrlBox.Text?.Trim() ?? "",
            ApiKey = ApiKeyBox.Password ?? "",
            CustomModels = _customModels.Select(m => m.Clone()).ToList(),
            RoutingMode = AiRoutingMode.SpecificModel,
            SelectedModel = ManualModelCombo.SelectedValue as string ?? AiModelIds.NemotronLightning
        };
    }
}
