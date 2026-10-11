using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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

    /// <summary>用户对内置模型分组（普通/专家）的手动调整，Key=模型 ID。保存时写回 AiAssistantConfig.ModelTierOverrides。</summary>
    private readonly Dictionary<string, AiModelTier> _tierOverrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private readonly Dictionary<string, AiModelAccountingConfig> _accounting = new(StringComparer.OrdinalIgnoreCase);
    private string? _accountingModel;
    private bool _refreshingAccounting;
    private string? _lastNormalId;
    private string? _lastExpertId;
    private string? _lastManualId;

    public AiAssistantSettingsPanel(AiAssistantConfig currentConfig)
    {
        InitializeComponent();
        _currentConfig = currentConfig;
        CustomModelsList.ItemsSource = _customModels;
        ExtraDirectoriesList.ItemsSource = _extraDirs;
        PersonaPresetList.ItemsSource = AiPersonas.Presets;
        LoadConfig(currentConfig);
    }

    private readonly ObservableCollection<string> _extraDirs = new();

    private void LoadConfig(AiAssistantConfig config)
    {
        _loading = true;
        try
        {
            _accountingModel = null;
            _accounting.Clear();
            foreach (var pair in config.ModelAccounting ?? new Dictionary<string, AiModelAccountingConfig>())
                if (pair.Value != null && !string.IsNullOrWhiteSpace(pair.Key)) _accounting[pair.Key] = pair.Value.Clone();
            EnabledCheck.IsChecked = config.Enabled;
            FloatingButtonCheck.IsChecked = config.ShowFloatingButton;
            SelectWorkMode(config.WorkMode);
            UseCustomKeyCheck.IsChecked = config.UseCustomApiKey;
            BaseUrlBox.Text = config.BaseUrl;
            ApiKeyBox.Password = config.ApiKey;
            LoadMultiApiSettings(config);
            WorkingDirectoryBox.Text = config.WorkingDirectory ?? "";
            _extraDirs.Clear();
            foreach (var d in config.WorkingDirectories ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(d)) _extraDirs.Add(d);
            FullAccessCheck.IsChecked = config.FullDiskAccess;
            AbsoluteAccessCheck.IsChecked = config.FullDiskAccess && config.AbsoluteAccess;
            AbsoluteAccessCheck.IsEnabled = config.FullDiskAccess;
            UnrestrictedAccessCheck.IsChecked = config.FullDiskAccess && config.AbsoluteAccess && config.UnrestrictedAccess;
            UnrestrictedAccessCheck.IsEnabled = config.FullDiskAccess && config.AbsoluteAccess;
            SystemControlAccessCheck.IsChecked = config.FullDiskAccess && config.AbsoluteAccess && config.UnrestrictedAccess && config.SystemControlAccess;
            SystemControlAccessCheck.IsEnabled = config.FullDiskAccess && config.AbsoluteAccess && config.UnrestrictedAccess;

            _tierOverrides.Clear();
            foreach (var pair in config.ModelTierOverrides ?? new Dictionary<string, AiModelTier>())
                if (!string.IsNullOrWhiteSpace(pair.Key)) _tierOverrides[pair.Key.Trim()] = pair.Value;

            _customModels.Clear();
            foreach (var item in (config.CustomModels ?? new List<AiModelDefinition>())
                         .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id)))
            {
                var cloned = item.Clone();
                cloned.Id = item.Id.Trim();
                cloned.DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? cloned.Id : item.DisplayName.Trim();
                _customModels.Add(cloned);
            }

            var mode = config.RoutingMode;
            if (mode == AiRoutingMode.Auto && !config.AutoModelRouting)
                mode = AiRoutingMode.SpecificModel;
            SelectRoutingMode(mode);

            ThresholdBox.Text = config.CompressionTokenThreshold.ToString();
            AllowCrashLogCheck.IsChecked = config.AllowCrashLogReading;
            ClearGuestHistoryCheck.IsChecked = config.ClearHistoryOnGuestSessionEnd;
            PrewarmOnOpenCheck.IsChecked = config.PrewarmSystemPromptOnOpen;
            PersonaCombo.ItemsSource = AiPersonas.All;
            PersonaCombo.SelectedValue = AiPersonas.Get(config.PersonaId).Id;
            CustomSystemPromptBox.Text = config.CustomSystemPrompt ?? "";
            UpdatePersonaHint();
            ShowSubAgentsCheck.IsChecked = config.ShowSubAgents;
            ShowCodeChangesCheck.IsChecked = config.ShowCodeChanges;
            ShowEditingFilesCheck.IsChecked = config.ShowEditingFiles;
            ShowCommandsCheck.IsChecked = config.ShowCommands;
            ShowReasoningSummaryCheck.IsChecked = config.ShowReasoningSummary;
            ShowReasoningCheck.IsChecked = config.ShowReasoning;
            StreamResponsesCheck.IsChecked = config.StreamResponses;
            RequestUsageMetadataCheck.IsChecked = config.RequestUsageMetadata;
            SendTemperatureCheck.IsChecked = config.SendTemperature;
            RequestTimeoutBox.Text = config.RequestTimeoutSeconds.ToString();
            LongWaitHintSecondsBox.Text = config.LongWaitHintSeconds.ToString();
            LongWaitDescriptionBox.Text = config.LongWaitDescription;
            TemperatureBox.Text = config.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
            MaxOutputTokensBox.Text = config.MaxOutputTokens.ToString();
            MaxToolActionsBox.Text = config.MaxToolActionsPerTurn.ToString();
            MaxSubAgentsBox.Text = config.MaxSubAgentsPerTurn.ToString();
            AllowShellCommandsCheck.IsChecked = AiShellPolicy.CanRun(config);
            SelectReasoningLevel(config.ReasoningLevel ?? (config.EnableDeepThinking ? AiReasoningLevel.High : AiReasoningLevel.Off));
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

    private void SelectWorkMode(AiWorkMode mode)
    {
        var selectedMode = Enum.IsDefined(mode) ? mode : AiWorkMode.Plan;
        WorkModeCombo.SelectedItem = WorkModeCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selectedMode.ToString(), StringComparison.Ordinal))
            ?? WorkModeCombo.Items[1];
    }

    private AiWorkMode GetWorkMode()
    {
        if (WorkModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<AiWorkMode>(tag, out var mode) && Enum.IsDefined(mode))
            return mode;
        return AiWorkMode.Plan;
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
        RefreshReasoningAvailability();
    }

    private void PersonaCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdatePersonaHint();

    private void UpdatePersonaHint()
    {
        if (PersonaHintText == null) return;
        var persona = AiPersonas.Get(PersonaCombo.SelectedValue as string);
        PersonaHintText.Text = persona.Description;
        // 只有选「其他」才显示自己的提示词输入区；固定控件尺寸，不改变窗口比例（外层 ScrollViewer 已限高）。
        if (CustomPromptPanel != null)
            CustomPromptPanel.Visibility = persona.Id == AiPersonas.Custom ? Visibility.Visible : Visibility.Collapsed;
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
        bool allowed = UseCustomKeyCheck.IsChecked == true || MixBuiltInApiCheck.IsChecked == true || GetRoutingMode() == AiRoutingMode.Auto;
        TokenSaverCheck.IsEnabled = allowed;
        TokenSaverHintText.Text = allowed
            ? "上下文压缩更激进，遇到官网/百科/下载地址这类固定问答直接本地回答、不请求 API。"
            : "只在“使用自定义 API key”或“默认路由模式=自动”时可以开启（跟省 token 这个目标同一个方向）；" +
              "手动指定模型/普通档/专家档下不提供这个开关。";
    }

    private void UpdateCustomApiUi()
    {
        bool custom = UseCustomKeyCheck.IsChecked == true || MixBuiltInApiCheck.IsChecked == true;
        BaseUrlBox.IsEnabled = custom;
        ApiKeyBox.IsEnabled = custom;
        CustomModelsEditor.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        BuiltInModelsHintPanel.Visibility = !custom || MixBuiltInApiCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<AiModelDefinition> CurrentCatalog()
    {
        return AiModelIds.GetCatalog(new AiAssistantConfig
        {
            UseCustomApiKey = UseCustomKeyCheck.IsChecked == true,
            MixBuiltInApi = MixBuiltInApiCheck.IsChecked == true,
            ApiEndpoints = _endpoints.Select(e => e.Clone()).ToList(),
            CustomModels = _customModels.Select(m => m.Clone()).ToList(),
            ModelTierOverrides = new Dictionary<string, AiModelTier>(_tierOverrides, StringComparer.OrdinalIgnoreCase)
        });
    }

    private void RefreshModelCombos(string? normalId = null, string? expertId = null, string? manualId = null)
    {
        var wasLoading = _loading;
        if (!wasLoading) SaveAccountingModel();
        _loading = true;
        try
        {
            var catalog = CurrentCatalog();
            RefreshAccountingModels(catalog);

            // 普通/专家下拉框列出全部模型，但把对应分组的模型排在前面（OrderBy 是稳定排序，组内保持原顺序）；
            // 这样把某个模型调到另一个分组后，它在两个下拉框里的位置会跟着变。
            List<AiModelDefinition> OrderedFor(AiModelTier tier) =>
                catalog.OrderBy(m => m.Tier == tier ? 0 : 1).ToList();

            NormalModelCombo.ItemsSource = OrderedFor(AiModelTier.Normal);
            ExpertModelCombo.ItemsSource = OrderedFor(AiModelTier.Expert);
            ManualModelCombo.ItemsSource = catalog;
            ModelTierList.ItemsSource = catalog;

            string? Pick(string? wanted, string builtInFallback)
            {
                var match = catalog.FirstOrDefault(m => string.Equals(m.ModelKey, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? catalog.FirstOrDefault(m => string.Equals(m.Id, wanted, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.ModelKey;
                return catalog.FirstOrDefault(m => m.Id == builtInFallback)?.ModelKey ?? catalog.FirstOrDefault()?.ModelKey;
            }

            NormalModelCombo.SelectedValue = Pick(normalId, AiModelIds.NemotronLightning);
            ExpertModelCombo.SelectedValue = Pick(expertId, AiModelIds.Nemotron3Ultra);
            ManualModelCombo.SelectedValue = Pick(manualId, AiModelIds.NemotronLightning);

            _lastNormalId = NormalModelCombo.SelectedValue as string;
            _lastExpertId = ExpertModelCombo.SelectedValue as string;
            _lastManualId = ManualModelCombo.SelectedValue as string;

            RefreshSpecialTermsNotice();
            RefreshReasoningAvailability();
        }
        finally { _loading = wasLoading; }
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
        WarnIfKnownIssue((combo.SelectedItem as AiModelDefinition)?.Id);
        RefreshReasoningAvailability();
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

        var ids = new[] { (NormalModelCombo?.SelectedItem as AiModelDefinition)?.Id, (ExpertModelCombo?.SelectedItem as AiModelDefinition)?.Id, (ManualModelCombo?.SelectedItem as AiModelDefinition)?.Id };
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

        var endpoint = ModelEndpointCombo.SelectedItem as AiApiEndpoint;
        var providerId = providerBaseUrl != null ? string.Empty : endpoint?.Id ?? "custom";
        var candidate = new AiModelDefinition { Id = id, ProviderId = providerId, ProviderBaseUrl = providerBaseUrl };
        var existing = _customModels.FirstOrDefault(m => string.Equals(m.ModelKey, candidate.ModelKey, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.DisplayName = name;
            existing.ProviderId = providerId;
            existing.ProviderName = providerBaseUrl != null ? "独立接口" : endpoint?.Name ?? "公共自定义接口";
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
                ProviderId = providerId,
                ProviderName = providerBaseUrl != null ? "独立接口" : endpoint?.Name ?? "公共自定义接口",
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
            ManualModelCombo.SelectedValue as string ?? candidate.ModelKey);
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

    /// <summary>把某个模型在"普通"和"专家"之间切换：自定义模型直接改自己的 Tier；
    /// 内置模型的默认分组写死在代码里，所以只记一份覆盖表（改回默认分组时把覆盖项删掉）。</summary>
    private void ToggleModelTier_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiModelDefinition model }) return;

        var normal = NormalModelCombo.SelectedValue as string;
        var expert = ExpertModelCombo.SelectedValue as string;
        var manual = ManualModelCombo.SelectedValue as string;
        var newTier = model.Tier == AiModelTier.Expert ? AiModelTier.Normal : AiModelTier.Expert;

        var target = _customModels.FirstOrDefault(m => string.Equals(m.ModelKey, model.ModelKey, StringComparison.OrdinalIgnoreCase));
        if (target != null)
        {
            target.Tier = newTier;
            CustomModelsList.Items.Refresh();
        }
        else
        {
            var builtIn = AiModelIds.BuiltIn.FirstOrDefault(m => string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase));
            if (builtIn == null) return;
            if (builtIn.Tier == newTier) _tierOverrides.Remove(builtIn.Id);
            else _tierOverrides[builtIn.Id] = newTier;
        }

        // 重建下拉框只是刷新分组顺序，不是用户重新选择了模型——临时挂上 _loading，
        // 避免 SelectionChanged 对"已知有问题的模型"重复弹警告。
        var wasLoading = _loading;
        _loading = true;
        try { RefreshModelCombos(normal, expert, manual); }
        finally { _loading = wasLoading; }
    }

    /// <summary>点右侧的预设：把对应人格的提示词填进"我的提示词"输入框，之后可以随便改。</summary>
    private void PersonaPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string presetId }) return;
        var text = AiPersonas.GetPresetText(presetId);
        if (string.IsNullOrWhiteSpace(text)) return;

        var existing = CustomSystemPromptBox.Text?.Trim() ?? "";
        if (existing.Length > 0 && !string.Equals(existing, text, StringComparison.Ordinal))
        {
            if (!MessageBoxDialog.ShowConfirm("输入框里已经有内容了，填入预设会覆盖它。确定要覆盖吗？", "填入预设"))
                return;
        }

        CustomSystemPromptBox.Text = text;
        CustomSystemPromptBox.Focus();
        CustomSystemPromptBox.CaretIndex = text.Length;
        ToastService.ShowSuccess($"已填入“{AiPersonas.Get(presetId).DisplayName}”预设，可以直接修改");
    }

    /// <summary>从供应商接口自动拉取全部模型并合并进自定义模型表。
    /// 下方"供应商地址 + 供应商 API Key"两栏都填了 → 用它们拉取，拉到的模型自动归到这个供应商（多供应商）；
    /// 都没填 → 用上方公共的接口地址和 API Key。已经在表里的模型一律不覆盖（保留用户改过的名称/分组/供应商）。</summary>
    private async void FetchModels_Click(object sender, RoutedEventArgs e)
        => await FetchEndpointModelsAsync(allEndpoints: false);

    private async void FetchAllModels_Click(object sender, RoutedEventArgs e)
        => await FetchEndpointModelsAsync(allEndpoints: true);

    private void RemoveCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiModelDefinition model }) return;
        var normal = NormalModelCombo.SelectedValue as string;
        var expert = ExpertModelCombo.SelectedValue as string;
        var manual = ManualModelCombo.SelectedValue as string;
        _customModels.Remove(model);
        RefreshModelCombos(normal, expert, manual);
    }

    private static int ReadTimeout(string text) => int.TryParse(text, out var value)
        ? value <= 0 ? 0 : Math.Clamp(value, 15, 86400) : 180;

    private static decimal? ReadPrice(string text) => decimal.TryParse(text,
        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
        && value >= 0 && value <= 1_000_000m ? value : null;

    private void SaveAccountingModel()
    {
        if (string.IsNullOrWhiteSpace(_accountingModel)) return;
        var settings = _accounting.TryGetValue(_accountingModel, out var existing) ? existing.Clone() : new AiModelAccountingConfig();
        settings.ContextWindowTokens = ReadInteger(ContextWindowBox.Text, 0, 0, 10_000_000);
        settings.MaxOutputTokens = ReadInteger(ModelOutputTokensBox.Text, 0, 0, 10_000_000);
        settings.TokenQuota = ReadQuota(ModelTokenQuotaBox.Text);
        settings.BudgetUsd = ReadPrice(ModelBudgetBox.Text);
        settings.InputPricePerMillion = ReadPrice(InputPriceBox.Text);
        settings.CachedInputPricePerMillion = ReadPrice(CachedInputPriceBox.Text);
        settings.OutputPricePerMillion = ReadPrice(OutputPriceBox.Text);
        _accounting[_accountingModel] = settings;
    }

    private Dictionary<string, AiModelAccountingConfig> SnapshotAccounting()
    {
        SaveAccountingModel();
        var snapshot = _accounting.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var live = ConfigService.Active?.Config.AiAssistant ?? _currentConfig;
        lock (live)
        {
            foreach (var entry in live.ModelAccounting ?? new Dictionary<string, AiModelAccountingConfig>())
            {
                if (_resetUsageModelKeys.Contains(entry.Key)) continue;
                if (!snapshot.TryGetValue(entry.Key, out var target))
                    snapshot[entry.Key] = target = new AiModelAccountingConfig();
                target.UsedTokens = Math.Max(target.UsedTokens, entry.Value.UsedTokens);
                target.UsedRequests = Math.Max(target.UsedRequests, entry.Value.UsedRequests);
                target.SpentUsd = Math.Max(target.SpentUsd, entry.Value.SpentUsd);
                target.UsageIncludesEstimates |= entry.Value.UsageIncludesEstimates;
            }
        }
        return snapshot;
    }

    private void ShowAccountingModel()
    {
        _accountingModel = AccountingModelCombo.SelectedValue as string;
        var model = AccountingModelCombo.SelectedItem as AiModelDefinition;
        var settings = _accountingModel != null && _accounting.TryGetValue(_accountingModel, out var found)
            ? found : model != null && _accounting.TryGetValue(model.Id, out var legacy) ? legacy : new AiModelAccountingConfig();
        if (_accountingModel != null && !_accounting.ContainsKey(_accountingModel)) _accounting[_accountingModel] = settings.Clone();
        ContextWindowBox.Text = settings.ContextWindowTokens.ToString();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        InputPriceBox.Text = settings.InputPricePerMillion?.ToString(culture) ?? "";
        CachedInputPriceBox.Text = settings.CachedInputPricePerMillion?.ToString(culture) ?? "";
        OutputPriceBox.Text = settings.OutputPricePerMillion?.ToString(culture) ?? "";
        ModelOutputTokensBox.Text = settings.MaxOutputTokens > 0 ? settings.MaxOutputTokens.ToString() : "";
        ModelTokenQuotaBox.Text = settings.TokenQuota?.ToString(culture) ?? "";
        ModelBudgetBox.Text = settings.BudgetUsd?.ToString(culture) ?? "";
        ModelUsageText.Text = $"本地累计：{settings.UsedRequests:N0} 次请求 · {settings.UsedTokens:N0} token{(settings.UsageIncludesEstimates ? "（含估算）" : "")} · 估算费用 ${settings.SpentUsd:0.######}";
        var wasRefreshing = _refreshingAccounting;
        _refreshingAccounting = true;
        try
        {
            ModelFastSupportedCheck.IsEnabled = model != null && _customModels.Any(m => m.ModelKey == model.ModelKey);
            ModelFastSupportedCheck.IsChecked = model?.SupportsFastMode == true;
        }
        finally { _refreshingAccounting = wasRefreshing; }
    }

    private void RefreshAccountingModels(List<AiModelDefinition> catalog)
    {
        if (!_loading) SaveAccountingModel();
        var selected = _accountingModel;
        _refreshingAccounting = true;
        try
        {
            AccountingModelCombo.ItemsSource = catalog;
            AccountingModelCombo.SelectedValue = catalog.FirstOrDefault(m => m.ModelKey == selected || m.Id == selected)?.ModelKey ?? catalog.FirstOrDefault()?.ModelKey;
            ShowAccountingModel();
        }
        finally { _refreshingAccounting = false; }
    }

    private void AccountingModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingAccounting || _loading) return;
        SaveAccountingModel();
        ShowAccountingModel();
    }

    private static int ReadInteger(string text, int fallback, int min, int max)
        => int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

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

    private void AddExtraDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "添加 AI 可访问的目录" };
        if (dialog.ShowDialog() != true) return;
        var dir = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (_extraDirs.Contains(dir, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(dir, WorkingDirectoryBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            return;
        _extraDirs.Add(dir);
    }

    private void RemoveExtraDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ExtraDirectoriesList.SelectedItem is string selected) _extraDirs.Remove(selected);
    }

    /// <summary>开启完全访问：连续两次系统提示，任何一次取消都撤回勾选。</summary>
    private void FullAccessCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var first = MessageBoxDialog.ShowConfirm(
            "你正在开启【完全访问】。\n\n开启后，AI 助手可以请求读取、列出甚至写入这台电脑上任意位置的文件，" +
            "包括你的文档、桌面、下载目录等个人文件。\n\n文件内容会发送给 AI 服务（使用自定义 API 时发送到你配置的服务商）。" +
            "请只在你信任当前所用模型与服务的情况下开启。\n\n是否继续？",
            "系统提示 1/2：完全访问");
        if (!first) { RevertFullAccessCheck(); return; }

        var second = MessageBoxDialog.ShowDangerConfirm(
            "最后确认（2/2）：\n\n· AI 可能误解指令，错误地覆盖或修改文件，且无法撤销；\n" +
            "· 读取、列出文件不再逐次询问（会记入启动器日志）；每次写入仍会弹窗询问你，请认真看清路径再同意；\n" +
            "· 系统目录（Windows、Program Files、ProgramData）始终禁止写入；\n" +
            "· 你可以随时回到这里取消勾选以关闭完全访问。\n\n确定要开启完全访问吗？",
            "系统提示 2/2：确认开启完全访问", "取消", "确认开启");
        if (!second) { RevertFullAccessCheck(); return; }

        AbsoluteAccessCheck.IsEnabled = true;
    }

    private void FullAccessCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        // 关闭完全访问时，绝对访问一并关闭并禁用。
        AbsoluteAccessCheck.IsChecked = false;
        AbsoluteAccessCheck.IsEnabled = false;
        UnrestrictedAccessCheck.IsChecked = false;
        UnrestrictedAccessCheck.IsEnabled = false;
        SystemControlAccessCheck.IsChecked = false;
        SystemControlAccessCheck.IsEnabled = false;
    }

    /// <summary>开启绝对访问：连续两次系统提示，任何一次取消都撤回勾选。</summary>
    private void AbsoluteAccessCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var first = MessageBoxDialog.ShowConfirm(
            "你正在开启【绝对访问】。\n\n开启后，AI 助手对电脑文件的列出、读取和写入将【不再弹出任何确认】，直接静默执行。\n\n" +
            "这意味着：AI 一旦误解指令、或被文件里夹带的恶意文字诱导，就可能在你毫不知情的情况下覆盖、改坏你的文件，" +
            "或把私人文件内容发给 AI 服务。\n\n是否继续？",
            "系统提示 1/2：绝对访问");
        if (!first) { RevertAbsoluteAccessCheck(); return; }

        var second = MessageBoxDialog.ShowConfirm(
            "最后确认（2/2）：\n\n· 每一次操作都会记入启动器日志（前缀「[AI 绝对访问]」），事后可以在日志页查看；\n" +
            "· 系统目录始终禁止写入；启动器自己的配置/账户目录仍会弹窗确认；\n" +
            "· 重要文件请先自己备份；\n· 可随时取消勾选恢复为逐次确认。\n\n确定要开启绝对访问吗？",
            "系统提示 2/2：确认开启绝对访问");
        if (!second) { RevertAbsoluteAccessCheck(); return; }

        UnrestrictedAccessCheck.IsEnabled = true;
    }

    private void AbsoluteAccessCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        // 关闭绝对访问时，完全绝对访问一并关闭并禁用。
        UnrestrictedAccessCheck.IsChecked = false;
        UnrestrictedAccessCheck.IsEnabled = false;
        SystemControlAccessCheck.IsChecked = false;
        SystemControlAccessCheck.IsEnabled = false;
    }

    /// <summary>开启完全绝对访问：连续两次红色警告，任何一次取消都撤回勾选。</summary>
    private void UnrestrictedAccessCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var first = MessageBoxDialog.ShowDangerConfirm(
            "你正在开启【完全绝对访问】——这是最高风险级别。\n\n若已允许 Shell 命令，也不再逐次弹出 Shell 确认窗口；仍记录执行日志并可停止。\n\n开启后，AI 助手可以不经任何确认，直接读取、修改、覆盖这台电脑上的【所有文件】，" +
            "包括：\n· 应用安装文件（Program Files、ProgramData 及其他软件的安装目录）；\n· Windows 系统文件（Windows、System32 等）；\n" +
            "· 启动器自己的配置与账户数据（含 API 密钥、登录令牌）。\n\n" +
            "AI 一旦误解指令、或被文件里夹带的恶意文字诱导，就可能破坏系统或软件、导致电脑无法启动，或把敏感内容发给 AI 服务，且很可能无法恢复。\n\n是否继续？",
            "危险操作 1/2：完全绝对访问", "取消", "我了解，继续");
        if (!first) { RevertUnrestrictedAccessCheck(); return; }

        var second = MessageBoxDialog.ShowDangerConfirm(
            "最后确认（2/2）——此操作极度危险：\n\n· 所有路径保护将被取消，AI 的任何读写都不会再有弹窗；\n" +
            "· 修改或删除系统文件、程序文件可能让 Windows 或软件无法运行，需要重装系统才能恢复；\n" +
            "· 每一次操作仍会记入启动器日志（前缀「[AI 绝对访问]」），但记录不能撤销已造成的破坏；\n" +
            "· 请务必先备份重要数据，并只在你完全信任当前模型与服务的情况下使用；\n· 可随时取消勾选恢复保护。\n\n确定要开启完全绝对访问吗？",
            "危险操作 2/2：确认开启完全绝对访问", "取消", "确认开启");
        if (!second) { RevertUnrestrictedAccessCheck(); return; }

        SystemControlAccessCheck.IsEnabled = true;
    }

    private void UnrestrictedAccessCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        // 关闭完全绝对访问时，彻底完全访问一并关闭并禁用。
        SystemControlAccessCheck.IsChecked = false;
        SystemControlAccessCheck.IsEnabled = false;
    }

    /// <summary>开启彻底完全访问：连续三次红色警告，任何一次取消都撤回勾选。</summary>
    private void SystemControlAccessCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var first = MessageBoxDialog.ShowDangerConfirm(
            "你正在开启【彻底完全访问】——这是最高危的级别。\n\n开启后，AI 助手可以：\n· 直接修改 Windows 注册表（包括开机启动项、系统与软件配置）；\n" +
            "· 通过 PowerShell / cmd 执行任意命令：删除文件与目录、结束进程、修改系统设置、安装或卸载软件、改动网络与防火墙、创建或删除用户等；\n" +
            "· 以上全部不再有任何弹窗确认。\n\n是否继续？",
            "危险操作 1/3：彻底完全访问", "取消", "我了解，继续");
        if (!first) { RevertSystemControlAccessCheck(); return; }

        var second = MessageBoxDialog.ShowDangerConfirm(
            "第二次警告（2/3）：\n\n· AI 一旦误解指令，或被网页、文件、日志里夹带的恶意文字诱导，就可能执行破坏性命令；\n" +
            "· 错误的注册表或系统命令可能让 Windows 无法启动、数据永久丢失，甚至需要重装系统；\n" +
            "· 启动器无法替你判断某条命令是否安全，也无法撤销已经执行的命令；\n· 如果你不是非常清楚自己在做什么，请不要开启。\n\n仍要继续吗？",
            "危险操作 2/3：彻底完全访问", "取消", "继续");
        if (!second) { RevertSystemControlAccessCheck(); return; }

        var third = MessageBoxDialog.ShowDangerConfirm(
            "最后确认（3/3）——此操作极度危险：\n\n· 每一次注册表修改和命令执行都会记入启动器日志（前缀「[AI 彻底完全访问]」），但记录不能撤销已造成的破坏；\n" +
            "· 请务必先备份重要数据、创建系统还原点；\n· 只在你完全信任当前模型与服务时使用；\n· 可随时取消勾选立即关闭该能力。\n\n确定要开启彻底完全访问吗？",
            "危险操作 3/3：确认开启彻底完全访问", "取消", "确认开启");
        if (!third) { RevertSystemControlAccessCheck(); return; }

        // All three highest-access confirmations succeeded. This capability is already
        // granted by that access level; reflect it in the visible and persisted Shell switch.
        AllowShellCommandsCheck.IsChecked = true;
    }

    private void RevertSystemControlAccessCheck()
    {
        _loading = true;
        try { SystemControlAccessCheck.IsChecked = false; }
        finally { _loading = false; }
    }

    private void RevertUnrestrictedAccessCheck()
    {
        _loading = true;
        try { UnrestrictedAccessCheck.IsChecked = false; }
        finally { _loading = false; }
    }

    private void RevertAbsoluteAccessCheck()
    {
        _loading = true;
        try { AbsoluteAccessCheck.IsChecked = false; }
        finally { _loading = false; }
    }

    private void RevertFullAccessCheck()
    {
        _loading = true;
        try { FullAccessCheck.IsChecked = false; }
        finally { _loading = false; }
    }

    private void ClearWorkingDirectory_Click(object sender, RoutedEventArgs e)
        => WorkingDirectoryBox.Clear();

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AiAssistantConfig
        {
            Enabled = true,
            WorkMode = AiWorkMode.Build,
            UseCustomApiKey = false,
            BaseUrl = "https://openrouter.ai/api/v1",
            ApiKey = "",
            WorkingDirectory = null,
            WorkingDirectories = new List<string>(),
            FullDiskAccess = false,
            AbsoluteAccess = false,
            UnrestrictedAccess = false,
            SystemControlAccess = false,
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
            PersonaId = AiPersonas.Default,
            CustomSystemPrompt = "",
            ShowSubAgents = true,
            ShowCodeChanges = true,
            ShowEditingFiles = true,
            ShowCommands = true,
            ShowReasoningSummary = true,
            ShowReasoning = true,
            EnableDeepThinking = false,
            EnableWebSearch = false,
            TokenSaverMode = true,
            PanelWidth = _currentConfig.PanelWidth,
            PanelWasOpen = _currentConfig.PanelWasOpen,
            LastSessionId = _currentConfig.LastSessionId
        };
        LoadConfig(defaults);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(ThresholdBox.Text, out var threshold);
        bool custom = UseCustomKeyCheck.IsChecked == true;

        CommitPendingKeys();
        if (custom || MixBuiltInApiCheck.IsChecked == true)
        {
            if (!ValidateApiConfiguration()) return;
            if (_customModels.Count == 0 && MixBuiltInApiCheck.IsChecked != true)
            {
                MessageBoxDialog.ShowWarning("请为已启用接口拉取或添加至少一个模型。", "AI 设置");
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
            if (combo.SelectedValue is string id && catalog.Any(m => string.Equals(m.ModelKey, id, StringComparison.OrdinalIgnoreCase)))
                return id;
            return catalog.FirstOrDefault(m => m.Id == fallback)?.ModelKey ?? catalog[0].ModelKey;
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
            WorkMode = GetWorkMode(),
            UseCustomApiKey = custom,
            BaseUrl = BaseUrlBox.Text.Trim(),
            ApiKey = ApiKeyBox.Password,
            ApiKeys = _defaultExtraKeys.ToList(),
            ApiEndpoints = _endpoints.Select(e => e.Clone()).ToList(),
            MixBuiltInApi = MixBuiltInApiCheck.IsChecked == true,
            EnableApiKeyFailover = ApiKeyFailoverCheck.IsChecked == true,
            EnableEndpointFailover = EndpointFailoverCheck.IsChecked == true,
            ReasoningLevel = GetReasoningLevel(),
            WorkingDirectory = workingDirectory.Length == 0 ? null : workingDirectory,
            WorkingDirectories = _extraDirs.ToList(),
            FullDiskAccess = FullAccessCheck.IsChecked == true,
            AbsoluteAccess = FullAccessCheck.IsChecked == true && AbsoluteAccessCheck.IsChecked == true,
            UnrestrictedAccess = FullAccessCheck.IsChecked == true && AbsoluteAccessCheck.IsChecked == true && UnrestrictedAccessCheck.IsChecked == true,
            SystemControlAccess = FullAccessCheck.IsChecked == true && AbsoluteAccessCheck.IsChecked == true && UnrestrictedAccessCheck.IsChecked == true && SystemControlAccessCheck.IsChecked == true,
            RoutingMode = mode,
            AutoModelRouting = mode == AiRoutingMode.Auto,
            NormalModelId = EnsureSelected(NormalModelCombo, AiModelIds.NemotronLightning),
            ExpertModelId = EnsureSelected(ExpertModelCombo, AiModelIds.Nemotron3Ultra),
            SelectedModel = EnsureSelected(ManualModelCombo, AiModelIds.NemotronLightning),
            CustomModels = _customModels.Select(m => m.Clone()).ToList(),
            ModelTierOverrides = new Dictionary<string, AiModelTier>(_tierOverrides, StringComparer.OrdinalIgnoreCase),
            CompressionTokenThreshold = threshold >= 2000 ? threshold : 18000,
            ShowFloatingButton = FloatingButtonCheck.IsChecked == true,
            AllowCrashLogReading = AllowCrashLogCheck.IsChecked == true,
            ClearHistoryOnGuestSessionEnd = ClearGuestHistoryCheck.IsChecked == true,
            PrewarmSystemPromptOnOpen = PrewarmOnOpenCheck.IsChecked == true,
            PersonaId = PersonaCombo.SelectedValue as string ?? AiPersonas.Default,
            CustomSystemPrompt = CustomSystemPromptBox.Text?.Trim() ?? "",
            ShowSubAgents = ShowSubAgentsCheck.IsChecked == true,
            ShowCodeChanges = ShowCodeChangesCheck.IsChecked == true,
            ShowEditingFiles = ShowEditingFilesCheck.IsChecked == true,
            ShowCommands = ShowCommandsCheck.IsChecked == true,
            ShowReasoningSummary = ShowReasoningSummaryCheck.IsChecked == true,
            ShowReasoning = ShowReasoningCheck.IsChecked == true,
            StreamResponses = StreamResponsesCheck.IsChecked == true,
            RequestUsageMetadata = RequestUsageMetadataCheck.IsChecked == true,
            SendTemperature = SendTemperatureCheck.IsChecked == true,
            RequestTimeoutSeconds = ReadTimeout(RequestTimeoutBox.Text),
            LongWaitHintSeconds = ReadInteger(LongWaitHintSecondsBox.Text, 45, 0, 86400),
            LongWaitDescription = LongWaitDescriptionBox.Text ?? "",
            ModelAccounting = SnapshotAccounting(),
            Temperature = double.TryParse(TemperatureBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature) && double.IsFinite(temperature) ? Math.Clamp(temperature, 0, 2) : 0.6,
            MaxOutputTokens = ReadInteger(MaxOutputTokensBox.Text, 0, 0, 32768),
            MaxToolActionsPerTurn = ReadInteger(MaxToolActionsBox.Text, 0, 0, int.MaxValue),
            ToolActionLimitVersion = 1,
            MaxSubAgentsPerTurn = ReadInteger(MaxSubAgentsBox.Text, 2, 0, 4),
            AllowShellCommands = AllowShellCommandsCheck.IsChecked == true,
            EnableDeepThinking = GetReasoningLevel() is not (AiReasoningLevel.Off or AiReasoningLevel.Fast),
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
        _ = RunModelTestAsync(model.ModelKey, btn);
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
        CommitPendingKeys();
        return new AiAssistantConfig
        {
            Enabled = true,
            WorkMode = GetWorkMode(),
            UseCustomApiKey = UseCustomKeyCheck.IsChecked == true,
            BaseUrl = BaseUrlBox.Text?.Trim() ?? "",
            ApiKey = ApiKeyBox.Password ?? "",
            ApiKeys = _defaultExtraKeys.ToList(),
            ApiEndpoints = _endpoints.Select(e => e.Clone()).ToList(),
            MixBuiltInApi = MixBuiltInApiCheck.IsChecked == true,
            EnableApiKeyFailover = ApiKeyFailoverCheck.IsChecked == true,
            EnableEndpointFailover = EndpointFailoverCheck.IsChecked == true,
            ReasoningLevel = GetReasoningLevel(),
            CustomModels = _customModels.Select(m => m.Clone()).ToList(),
            ShowSubAgents = ShowSubAgentsCheck.IsChecked == true,
            ShowCodeChanges = ShowCodeChangesCheck.IsChecked == true,
            ShowEditingFiles = ShowEditingFilesCheck.IsChecked == true,
            ShowCommands = ShowCommandsCheck.IsChecked == true,
            ShowReasoningSummary = ShowReasoningSummaryCheck.IsChecked == true,
            ShowReasoning = ShowReasoningCheck.IsChecked == true,
            StreamResponses = StreamResponsesCheck.IsChecked == true,
            RequestUsageMetadata = RequestUsageMetadataCheck.IsChecked == true,
            SendTemperature = SendTemperatureCheck.IsChecked == true,
            RequestTimeoutSeconds = ReadTimeout(RequestTimeoutBox.Text),
            LongWaitHintSeconds = ReadInteger(LongWaitHintSecondsBox.Text, 45, 0, 86400),
            LongWaitDescription = LongWaitDescriptionBox.Text ?? "",
            ModelAccounting = SnapshotAccounting(),
            Temperature = double.TryParse(TemperatureBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature) && double.IsFinite(temperature) ? Math.Clamp(temperature, 0, 2) : 0.6,
            MaxOutputTokens = ReadInteger(MaxOutputTokensBox.Text, 0, 0, 32768),
            MaxToolActionsPerTurn = ReadInteger(MaxToolActionsBox.Text, 0, 0, int.MaxValue),
            ToolActionLimitVersion = 1,
            MaxSubAgentsPerTurn = ReadInteger(MaxSubAgentsBox.Text, 2, 0, 4),
            AllowShellCommands = AllowShellCommandsCheck.IsChecked == true,
            RoutingMode = AiRoutingMode.SpecificModel,
            SelectedModel = ManualModelCombo.SelectedValue as string ?? AiModelIds.NemotronLightning
        };
    }

    /// <summary>把当前编辑框里的完整状态（不做校验）整理成一份 AiAssistantConfig，用作导入 JSON 时
    /// 的"基底"——导入的文件里没提到的字段（比如"不保留密钥"那种导出）保持编辑框里原样，不会被
    /// 意外清空成默认值。跟 BuildDraftConfigForTest 的区别是这里字段更全，专给导入用。</summary>
    private AiAssistantConfig CaptureUiAsConfig()
    {
        CommitPendingKeys();
        int.TryParse(ThresholdBox.Text, out var threshold);
        return new AiAssistantConfig
        {
            Enabled = EnabledCheck.IsChecked == true,
            WorkMode = GetWorkMode(),
            ShowFloatingButton = FloatingButtonCheck.IsChecked == true,
            UseCustomApiKey = UseCustomKeyCheck.IsChecked == true,
            BaseUrl = BaseUrlBox.Text?.Trim() ?? "",
            ApiKey = ApiKeyBox.Password ?? "",
            ApiKeys = _defaultExtraKeys.ToList(),
            ApiEndpoints = _endpoints.Select(e => e.Clone()).ToList(),
            MixBuiltInApi = MixBuiltInApiCheck.IsChecked == true,
            EnableApiKeyFailover = ApiKeyFailoverCheck.IsChecked == true,
            EnableEndpointFailover = EndpointFailoverCheck.IsChecked == true,
            ReasoningLevel = GetReasoningLevel(),
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text) ? null : WorkingDirectoryBox.Text.Trim(),
            WorkingDirectories = _extraDirs.ToList(),
            FullDiskAccess = FullAccessCheck.IsChecked == true,
            AbsoluteAccess = FullAccessCheck.IsChecked == true && AbsoluteAccessCheck.IsChecked == true,
            UnrestrictedAccess = FullAccessCheck.IsChecked == true && AbsoluteAccessCheck.IsChecked == true && UnrestrictedAccessCheck.IsChecked == true,
            SystemControlAccess = FullAccessCheck.IsChecked == true && AbsoluteAccessCheck.IsChecked == true && UnrestrictedAccessCheck.IsChecked == true && SystemControlAccessCheck.IsChecked == true,
            CustomModels = _customModels.Select(m => m.Clone()).ToList(),
            ModelTierOverrides = new Dictionary<string, AiModelTier>(_tierOverrides, StringComparer.OrdinalIgnoreCase),
            RoutingMode = GetRoutingMode(),
            AutoModelRouting = GetRoutingMode() == AiRoutingMode.Auto,
            NormalModelId = NormalModelCombo.SelectedValue as string ?? AiModelIds.NemotronLightning,
            ExpertModelId = ExpertModelCombo.SelectedValue as string ?? AiModelIds.Nemotron3Ultra,
            SelectedModel = ManualModelCombo.SelectedValue as string ?? AiModelIds.NemotronLightning,
            CompressionTokenThreshold = threshold >= 2000 ? threshold : 18000,
            AllowCrashLogReading = AllowCrashLogCheck.IsChecked == true,
            ClearHistoryOnGuestSessionEnd = ClearGuestHistoryCheck.IsChecked == true,
            PrewarmSystemPromptOnOpen = PrewarmOnOpenCheck.IsChecked == true,
            PersonaId = PersonaCombo.SelectedValue as string ?? AiPersonas.Default,
            CustomSystemPrompt = CustomSystemPromptBox.Text?.Trim() ?? "",
            ShowSubAgents = ShowSubAgentsCheck.IsChecked == true,
            ShowCodeChanges = ShowCodeChangesCheck.IsChecked == true,
            ShowEditingFiles = ShowEditingFilesCheck.IsChecked == true,
            ShowCommands = ShowCommandsCheck.IsChecked == true,
            ShowReasoningSummary = ShowReasoningSummaryCheck.IsChecked == true,
            ShowReasoning = ShowReasoningCheck.IsChecked == true,
            StreamResponses = StreamResponsesCheck.IsChecked == true,
            RequestUsageMetadata = RequestUsageMetadataCheck.IsChecked == true,
            SendTemperature = SendTemperatureCheck.IsChecked == true,
            RequestTimeoutSeconds = ReadTimeout(RequestTimeoutBox.Text),
            LongWaitHintSeconds = ReadInteger(LongWaitHintSecondsBox.Text, 45, 0, 86400),
            LongWaitDescription = LongWaitDescriptionBox.Text ?? "",
            ModelAccounting = SnapshotAccounting(),
            Temperature = double.TryParse(TemperatureBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature) && double.IsFinite(temperature) ? Math.Clamp(temperature, 0, 2) : 0.6,
            MaxOutputTokens = ReadInteger(MaxOutputTokensBox.Text, 0, 0, 32768),
            MaxToolActionsPerTurn = ReadInteger(MaxToolActionsBox.Text, 0, 0, int.MaxValue),
            ToolActionLimitVersion = 1,
            MaxSubAgentsPerTurn = ReadInteger(MaxSubAgentsBox.Text, 2, 0, 4),
            AllowShellCommands = AllowShellCommandsCheck.IsChecked == true,
            EnableDeepThinking = GetReasoningLevel() is not (AiReasoningLevel.Off or AiReasoningLevel.Fast),
            EnableWebSearch = EnableWebSearchCheck.IsChecked == true,
            TokenSaverMode = TokenSaverCheck.IsEnabled && TokenSaverCheck.IsChecked == true,
            AcceptedTermsVersion = _currentConfig.AcceptedTermsVersion,
            PanelWidth = _currentConfig.PanelWidth,
            PanelWasOpen = _currentConfig.PanelWasOpen,
            LastSessionId = _currentConfig.LastSessionId
        };
    }

    // ----- 导入配置 JSON：读之前"导出配置 JSON"生成的文件，覆盖到当前编辑框里；不是"保存" -----

    /// <summary>导入一份之前用"导出配置 JSON"生成的文件。因为导出时可以选"不保留密钥/接口/模型表"，
    /// 这里按 JSON 里实际存在的字段来更新——文件里没有的字段（比如朋友发来的"可分发"版本本来就没有
    /// 密钥/BaseUrl/模型表）保持编辑框里当前的值不动，不会把已经填好的密钥/自定义模型表冲掉。
    /// 导入之后还是停在设置页里，需要用户自己点"保存"才会真正生效——跟直接改了几个控件的值等价，
    /// 给用户留一次检查/反悔的机会。</summary>
    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 AI 助手配置",
            Filter = "JSON 文件|*.json|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var config = CaptureUiAsConfig();
            bool hadCredentials = false;

            string? GetStr(string key) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool? GetBool(string key) =>
                root.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
            int? GetInt(string key) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

            if (GetBool("启用AI助手") is bool enabled) config.Enabled = enabled;
            if (GetBool("显示悬浮按钮") is bool floating) config.ShowFloatingButton = floating;
            // Missing mode keeps the current selection; malformed values choose the read-only mode.
            // WorkMode never changes the file, Shell or system-control permission switches.
            if (root.TryGetProperty("默认工作模式", out var workModeElement))
                config.WorkMode = workModeElement.ValueKind == JsonValueKind.String
                    && Enum.TryParse<AiWorkMode>(workModeElement.GetString(), true, out var workMode)
                    && Enum.IsDefined(workMode) ? workMode : AiWorkMode.Plan;
            if (GetStr("默认路由模式") is string modeStr && Enum.TryParse<AiRoutingMode>(modeStr, out var mode))
            {
                config.RoutingMode = mode;
                config.AutoModelRouting = mode == AiRoutingMode.Auto;
            }
            if (GetInt("上下文压缩阈值") is int threshold) config.CompressionTokenThreshold = threshold;
            if (GetBool("显示AI调用的子智能体") is bool showSubAgents) config.ShowSubAgents = showSubAgents;
            if (GetBool("显示修改的代码") is bool showCodeChanges) config.ShowCodeChanges = showCodeChanges;
            if (GetBool("显示正在修改的文件") is bool showEditingFiles) config.ShowEditingFiles = showEditingFiles;
            if (GetBool("显示执行的命令") is bool showCommands) config.ShowCommands = showCommands;
            if (GetBool("显示思考过程摘要") is bool showReasoningSummary) config.ShowReasoningSummary = showReasoningSummary;
            if (GetBool("显示思考过程") is bool showReasoning) config.ShowReasoning = showReasoning;
            if (GetBool("流式输出") is bool stream) config.StreamResponses = stream;
            if (GetBool("发送温度") is bool sendTemperature) config.SendTemperature = sendTemperature;
            if (GetBool("请求流式用量") is bool requestUsage) config.RequestUsageMetadata = requestUsage;
            if (GetStr("请求超时") is string timeout) config.RequestTimeoutSeconds = ReadTimeout(timeout);
            if (GetStr("等待提示时间") is string hintSeconds) config.LongWaitHintSeconds = ReadInteger(hintSeconds, 45, 0, 86400);
            if (GetStr("等待提示描述") is string hint) config.LongWaitDescription = hint.Length <= 2000 ? hint : hint[..2000];
            if (root.TryGetProperty("模型用量设置", out var accounting) && accounting.ValueKind == JsonValueKind.Object)
                config.ModelAccounting = JsonSerializer.Deserialize<Dictionary<string, AiModelAccountingConfig>>(accounting.GetRawText()) ?? new();
            if (GetStr("温度") is string temp && double.TryParse(temp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature) && double.IsFinite(temperature)) config.Temperature = Math.Clamp(temperature, 0, 2);
            if (GetStr("输出上限") is string output) config.MaxOutputTokens = ReadInteger(output, 0, 0, 32768);
            if (GetStr("工具上限") is string tools)
            {
                config.MaxToolActionsPerTurn = ReadInteger(tools, 0, 0, int.MaxValue);
                config.ToolActionLimitVersion = 1; // Imported finite limits are intentional.
            }
            if (GetStr("子任务上限") is string agents) config.MaxSubAgentsPerTurn = ReadInteger(agents, 2, 0, 4);
            // Importing a configuration never silently enables execution authority.
            if (GetBool("Shell") == false) config.AllowShellCommands = false;
            if (GetBool("默认深度思考") is bool deepThinking) config.EnableDeepThinking = deepThinking;
            if (GetStr("推理等级") is string reasoning && Enum.TryParse<AiReasoningLevel>(reasoning, true, out var level) && Enum.IsDefined(level)) config.ReasoningLevel = level;
            else if (GetBool("默认深度思考") is bool legacyDeep) config.ReasoningLevel = legacyDeep ? AiReasoningLevel.High : AiReasoningLevel.Off;
            if (GetBool("混合内置接口") is bool mixed) config.MixBuiltInApi = mixed;
            if (GetBool("密钥自动切换") is bool keyFailover) config.EnableApiKeyFailover = keyFailover;
            if (GetBool("跨接口自动切换") is bool endpointFailover) config.EnableEndpointFailover = endpointFailover;
            if (GetBool("默认联网搜索") is bool webSearch) config.EnableWebSearch = webSearch;
            if (GetBool("允许崩溃日志分析") is bool crashLog) config.AllowCrashLogReading = crashLog;
            if (GetBool("访客会话结束清空历史") is bool clearGuest) config.ClearHistoryOnGuestSessionEnd = clearGuest;
            if (GetBool("打开页面预热提示词") is bool prewarm) config.PrewarmSystemPromptOnOpen = prewarm;
            if (GetStr("AI人格") is string personaId) config.PersonaId = AiPersonas.Get(personaId).Id;
            if (GetStr("自定义系统提示词") is string customPrompt) config.CustomSystemPrompt = customPrompt;
            if (GetBool("省Token模式") is bool tokenSaver) config.TokenSaverMode = tokenSaver;
            if (root.TryGetProperty("内置模型分组调整", out var tierMapEl) && tierMapEl.ValueKind == JsonValueKind.Object)
            {
                var importedTiers = new Dictionary<string, AiModelTier>(StringComparer.OrdinalIgnoreCase);
                foreach (var tierMapProp in tierMapEl.EnumerateObject())
                    if (Enum.TryParse<AiModelTier>(tierMapProp.Value.ToString(), true, out var parsedTier))
                        importedTiers[tierMapProp.Name] = parsedTier;
                config.ModelTierOverrides = importedTiers;
            }

            // 密钥/接口地址/模型表这几项只有导出时选了"保留"才会出现在文件里；出现才覆盖，
            // 没出现（"可分发"版本）就保留编辑框里已经填好的那份，不做任何改动。
            if (GetBool("使用自定义API") is bool useCustom) { config.UseCustomApiKey = useCustom; hadCredentials = true; }
            if (GetStr("接口地址BaseUrl") is string baseUrl) { config.BaseUrl = baseUrl; hadCredentials = true; }
            if (GetStr("API密钥") is string apiKey) { config.ApiKey = apiKey; hadCredentials = true; }
            if (root.TryGetProperty("备用API密钥", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
            { config.ApiKeys = JsonSerializer.Deserialize<List<string>>(keysEl.GetRawText()) ?? new(); hadCredentials = true; }
            if (root.TryGetProperty("API接口列表", out var endpointsEl) && endpointsEl.ValueKind == JsonValueKind.Array)
            { config.ApiEndpoints = JsonSerializer.Deserialize<List<AiApiEndpoint>>(endpointsEl.GetRawText()) ?? new(); hadCredentials = true; }
            if (GetStr("普通模型ID") is string normalId) { config.NormalModelId = normalId; hadCredentials = true; }
            if (GetStr("专家模型ID") is string expertId) { config.ExpertModelId = expertId; hadCredentials = true; }
            if (GetStr("指定模型ID") is string manualId) { config.SelectedModel = manualId; hadCredentials = true; }
            if (root.TryGetProperty("自定义模型表", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
            {
                var imported = new List<AiModelDefinition>();
                foreach (var item in modelsEl.EnumerateArray())
                {
                    var id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    imported.Add(new AiModelDefinition
                    {
                        Id = id.Trim(),
                        ProviderId = item.TryGetProperty("ProviderId", out var piEl) && piEl.ValueKind == JsonValueKind.String ? piEl.GetString() ?? string.Empty : string.Empty,
                        ProviderName = item.TryGetProperty("ProviderName", out var pnEl) && pnEl.ValueKind == JsonValueKind.String ? pnEl.GetString() ?? "" : "",
                        SupportsFastMode = item.TryGetProperty("SupportsFastMode", out var fastEl) && fastEl.ValueKind == JsonValueKind.True,
                        DisplayName = item.TryGetProperty("DisplayName", out var nameEl) ? nameEl.GetString() ?? id.Trim() : id.Trim(),
                        ProviderBaseUrl = item.TryGetProperty("ProviderBaseUrl", out var pbEl) && pbEl.ValueKind == JsonValueKind.String ? pbEl.GetString() : null,
                        ProviderApiKey = item.TryGetProperty("ProviderApiKey", out var pkEl) && pkEl.ValueKind == JsonValueKind.String ? pkEl.GetString() : null,
                        Tier = item.TryGetProperty("Tier", out var tierEl) && Enum.TryParse<AiModelTier>(tierEl.ToString(), true, out var t) ? t : AiModelTier.Normal
                    });
                }
                config.CustomModels = imported;
                hadCredentials = true;
            }

            LoadConfig(config);

            ToastService.ShowSuccess(hadCredentials
                ? "已导入配置（含模型/密钥/接口信息），检查无误后记得点“保存”。"
                : "已导入配置（不含模型/密钥/接口信息，沿用当前已填写的那份），检查无误后记得点“保存”。");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导入失败：{ex.Message}\n\n请确认选择的是本设置页“导出配置 JSON”生成的文件。", "导入配置");
        }
    }

    // ----- 导出配置 JSON：仅用于调试/分享排查，不是"保存"，不校验、不落地到正式配置文件 -----

    /// <summary>导出当前编辑框里的设置为一份 JSON 文件，方便调试时发给别人排查（比如群里帮忙看设置）。
    /// 先问清楚"要不要连模型/密钥/供应商接口信息一起导出"——因为很多人（包括我自己）导出这个文件
    /// 就是为了让别人看路由/能力这些普通设置，压根不想把自己电脑上已经填好的 API Key 和自定义模型表
    /// 也一起发出去。选"保留"时，导出的文件里会带明文密钥，明确提示不要拿去分发；选"不保留"时，
    /// 密钥/接口地址/自定义模型表这些字段直接从 JSON 里去掉，可以放心把文件发给别人。</summary>
    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBoxDialog.ShowThreeChoice(
            "导出的配置里是否包含当前填写的模型列表、API 密钥、供应商接口地址等信息？\n\n" +
            "选“保留”：这些信息会以明文形式写入导出的 JSON 文件，请不要把这份文件分发给别人。\n" +
            "选“不保留”：导出的文件不含密钥/接口地址/自定义模型表，可以放心分享给别人用于排查问题。",
            "导出 AI 助手配置",
            "取消",
            "不保留（可分发）",
            "保留（仅自用，勿分发）");

        bool includeCredentials;
        switch (choice)
        {
            case XclMessageResult.Yes:
                includeCredentials = true;
                break;
            case XclMessageResult.No:
                includeCredentials = false;
                break;
            default:
                return; // 取消
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 AI 助手配置",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            FileName = $"XCL2_AI设置_{(includeCredentials ? "含密钥_请勿分发" : "可分发")}_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var export = BuildExportPayload(includeCredentials);
            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);

            if (includeCredentials)
                MessageBoxDialog.ShowWarning($"配置已导出到：\n{dialog.FileName}\n\n这份文件包含明文 API 密钥，请不要发给别人或上传到公开地方。", "导出成功");
            else
                MessageBoxDialog.ShowSuccess($"配置已导出到：\n{dialog.FileName}\n\n已去除密钥/接口地址/自定义模型表，可以放心分享。", "导出成功");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导出失败：{ex.Message}");
        }
    }

    /// <summary>把当前编辑框里的值整理成一份用于导出的匿名对象。之所以不直接序列化
    /// AiAssistantConfig 本体，是因为那个类里还混了一堆跟"分享排查用的设置快照"无关的运行时状态
    /// （LastSessionId、PanelWidth、AcceptedTermsVersion……），这里只挑跟"AI 行为/路由"相关、
    /// 值得给别人看的字段，順便按 includeCredentials 决定要不要带上密钥类信息。</summary>
    private object BuildExportPayload(bool includeCredentials)
    {
        CommitPendingKeys();
        int.TryParse(ThresholdBox.Text, out var threshold);

        var payload = new Dictionary<string, object?>
        {
            ["_说明"] = includeCredentials
                ? "此文件包含明文 API 密钥/接口地址，请勿分发给他人或上传到公开渠道。"
                : "此文件已去除密钥/接口地址/自定义模型表，可安全分享给他人用于排查设置问题。",
            ["启用AI助手"] = EnabledCheck.IsChecked == true,
            ["显示悬浮按钮"] = FloatingButtonCheck.IsChecked == true,
            ["默认工作模式"] = GetWorkMode().ToString(),
            ["使用自定义API"] = UseCustomKeyCheck.IsChecked == true,
            ["默认路由模式"] = GetRoutingMode().ToString(),
            ["上下文压缩阈值"] = threshold,
            ["显示AI调用的子智能体"] = ShowSubAgentsCheck.IsChecked == true,
            ["显示修改的代码"] = ShowCodeChangesCheck.IsChecked == true,
            ["显示正在修改的文件"] = ShowEditingFilesCheck.IsChecked == true,
            ["显示执行的命令"] = ShowCommandsCheck.IsChecked == true,
            ["显示思考过程摘要"] = ShowReasoningSummaryCheck.IsChecked == true,
            ["显示思考过程"] = ShowReasoningCheck.IsChecked == true,
            ["流式输出"] = StreamResponsesCheck.IsChecked == true,
            ["请求流式用量"] = RequestUsageMetadataCheck.IsChecked == true,
            ["发送温度"] = SendTemperatureCheck.IsChecked == true,
            ["请求超时"] = RequestTimeoutBox.Text,
            ["等待提示时间"] = LongWaitHintSecondsBox.Text,
            ["等待提示描述"] = LongWaitDescriptionBox.Text,
            ["模型用量设置"] = SnapshotAccounting(),
            ["温度"] = TemperatureBox.Text,
            ["输出上限"] = MaxOutputTokensBox.Text,
            ["工具上限"] = MaxToolActionsBox.Text,
            ["子任务上限"] = MaxSubAgentsBox.Text,
            ["Shell"] = AllowShellCommandsCheck.IsChecked == true,
            ["默认深度思考"] = GetReasoningLevel() is not (AiReasoningLevel.Off or AiReasoningLevel.Fast),
            ["推理等级"] = GetReasoningLevel().ToString(),
            ["混合内置接口"] = MixBuiltInApiCheck.IsChecked == true,
            ["密钥自动切换"] = ApiKeyFailoverCheck.IsChecked == true,
            ["跨接口自动切换"] = EndpointFailoverCheck.IsChecked == true,
            ["默认联网搜索"] = EnableWebSearchCheck.IsChecked == true,
            ["允许崩溃日志分析"] = AllowCrashLogCheck.IsChecked == true,
            ["访客会话结束清空历史"] = ClearGuestHistoryCheck.IsChecked == true,
            ["打开页面预热提示词"] = PrewarmOnOpenCheck.IsChecked == true,
            ["AI人格"] = PersonaCombo.SelectedValue as string ?? AiPersonas.Default,
            ["自定义系统提示词"] = CustomSystemPromptBox.Text?.Trim() ?? "",
            ["省Token模式"] = TokenSaverCheck.IsEnabled && TokenSaverCheck.IsChecked == true,
            ["内置模型分组调整"] = _tierOverrides.ToDictionary(p => p.Key, p => p.Value.ToString()),
        };

        if (includeCredentials)
        {
            payload["接口地址BaseUrl"] = BaseUrlBox.Text?.Trim() ?? "";
            payload["API密钥"] = ApiKeyBox.Password ?? "";
            payload["备用API密钥"] = _defaultExtraKeys.ToList();
            payload["API接口列表"] = _endpoints.Select(e => e.Clone()).ToList();
            payload["普通模型ID"] = NormalModelCombo.SelectedValue as string;
            payload["专家模型ID"] = ExpertModelCombo.SelectedValue as string;
            payload["指定模型ID"] = ManualModelCombo.SelectedValue as string;
            payload["自定义模型表"] = _customModels.Select(m => new
            {
                m.Id,
                m.DisplayName,
                m.ProviderId,
                m.ProviderName,
                m.SupportsFastMode,
                m.Tier,
                m.ProviderBaseUrl,
                m.ProviderApiKey
            }).ToList();
        }

        return payload;
    }
}
