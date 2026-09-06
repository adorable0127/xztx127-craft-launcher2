using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel : UserControl, IOverlayDialog
{
    public event EventHandler<AiAssistantConfig>? Saved;
    public event EventHandler? Cancelled;
    public event EventHandler<bool?>? RequestClose;

    private readonly AiAssistantConfig _currentConfig;
    private readonly ObservableCollection<AiModelDefinition> _customModels = new();
    private bool _loading;
    private bool _guardingModelSelection;
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
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Id.Trim() : item.DisplayName.Trim()
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

        NormalModelCombo.SelectedValue = Pick(normalId, AiModelIds.Nemotron35Lightning);
        ExpertModelCombo.SelectedValue = Pick(expertId, AiModelIds.MimoV25);
        ManualModelCombo.SelectedValue = Pick(manualId, AiModelIds.Nemotron35Lightning);

        _lastNormalId = NormalModelCombo.SelectedValue as string;
        _lastExpertId = ExpertModelCombo.SelectedValue as string;
        _lastManualId = ManualModelCombo.SelectedValue as string;
    }

    /// <summary>默认 API key 下，Hy3 Free / Muse Spark 1.2 Free 暂时不可用（见 AiModelIds.
    /// DefaultAvailable 注释）。下拉框仍展示全部内置模型，方便用户知道有哪些模型，但选中
    /// 不可用的模型时要提示原因并把选择还原，不允许真的保存一个不可用模型。</summary>
    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _guardingModelSelection) return;
        if (sender is not ComboBox combo) return;

        string? lastId = combo == NormalModelCombo ? _lastNormalId
            : combo == ExpertModelCombo ? _lastExpertId
            : _lastManualId;

        var selectedId = combo.SelectedValue as string;

        if (UseCustomKeyCheck.IsChecked != true && !AiModelIds.IsAvailableByDefault(selectedId))
        {
            _guardingModelSelection = true;
            try { combo.SelectedValue = lastId; }
            finally { _guardingModelSelection = false; }

            MessageBoxDialog.ShowWarning(AiModelIds.UnavailableModelMessage, "模型暂时不可用");
            return;
        }

        if (combo == NormalModelCombo) _lastNormalId = selectedId;
        else if (combo == ExpertModelCombo) _lastExpertId = selectedId;
        else if (combo == ManualModelCombo) _lastManualId = selectedId;
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

        var existing = _customModels.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.DisplayName = name;
            CustomModelsList.Items.Refresh();
        }
        else
        {
            _customModels.Add(new AiModelDefinition { Id = id, DisplayName = name });
        }

        CustomModelIdBox.Clear();
        CustomModelNameBox.Clear();
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
            BaseUrl = "https://opencode.ai/zen/v1",
            ApiKey = "",
            WorkingDirectory = null,
            RoutingMode = AiRoutingMode.Auto,
            AutoModelRouting = true,
            NormalModelId = AiModelIds.Nemotron35Lightning,
            ExpertModelId = AiModelIds.MimoV25,
            SelectedModel = AiModelIds.Nemotron35Lightning,
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

    private void Save_Click(object sender, RoutedEventArgs e)
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
            NormalModelId = EnsureSelected(NormalModelCombo, AiModelIds.Nemotron35Lightning),
            ExpertModelId = EnsureSelected(ExpertModelCombo, AiModelIds.MimoV25),
            SelectedModel = EnsureSelected(ManualModelCombo, AiModelIds.Nemotron35Lightning),
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
}
