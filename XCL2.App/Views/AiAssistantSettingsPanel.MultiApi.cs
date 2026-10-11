using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel
{
    private readonly ObservableCollection<AiApiEndpoint> _endpoints = new();
    private readonly List<string> _defaultExtraKeys = new();
    private readonly HashSet<string> _resetUsageModelKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingEndpoint;
    private bool _fetchingModels;

    // Never bind real key strings into a visible list or tooltip.
    private sealed record MaskedKey(int Index, string Label);

    private void LoadMultiApiSettings(AiAssistantConfig config)
    {
        MixBuiltInApiCheck.IsChecked = config.MixBuiltInApi;
        ApiKeyFailoverCheck.IsChecked = config.EnableApiKeyFailover;
        EndpointFailoverCheck.IsChecked = config.EnableEndpointFailover;
        _defaultExtraKeys.Clear();
        _defaultExtraKeys.AddRange((config.ApiKeys ?? new()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal));
        _endpoints.Clear();
        foreach (var endpoint in config.ApiEndpoints ?? new())
            if (endpoint != null) _endpoints.Add(endpoint.Clone());
        EndpointsCombo.ItemsSource = _endpoints;
        EndpointsCombo.SelectedItem = _endpoints.FirstOrDefault();
        ShowEndpointEditor();
        RefreshEndpointChoices();
        RefreshMaskedKeys();
        _resetUsageModelKeys.Clear();
    }

    private void RefreshEndpointChoices()
    {
        if (ModelEndpointCombo == null) return;
        var selected = ModelEndpointCombo.SelectedValue as string;
        var choices = new List<AiApiEndpoint> { new() { Id = "custom", Name = "公共自定义接口", Enabled = true } };
        choices.AddRange(_endpoints.Where(e => e.Enabled).Select(e => e.Clone()));
        ModelEndpointCombo.ItemsSource = choices;
        ModelEndpointCombo.SelectedValue = choices.Any(e => e.Id == selected) ? selected : choices[0].Id;
    }

    private void RefreshMaskedKeys()
    {
        DefaultKeysList.ItemsSource = _defaultExtraKeys.Select((_, i) => new MaskedKey(i, $"备用密钥 {i + 1} · ••••••••")).ToList();
        var endpoint = EndpointsCombo.SelectedItem as AiApiEndpoint;
        EndpointKeysList.ItemsSource = (endpoint?.ApiKeys ?? new()).Select((_, i) => new MaskedKey(i, $"密钥 {i + 1} · ••••••••")).ToList();
    }

    private void AddDefaultKey_Click(object sender, RoutedEventArgs e)
    {
        var key = DefaultExtraKeyBox.Password.Trim();
        if (key.Length == 0) return;
        if (!_defaultExtraKeys.Contains(key, StringComparer.Ordinal)) _defaultExtraKeys.Add(key);
        DefaultExtraKeyBox.Clear();
        RefreshMaskedKeys();
    }

    private void RemoveDefaultKey_Click(object sender, RoutedEventArgs e)
    {
        if (DefaultKeysList.SelectedItem is not MaskedKey row) return;
        if (row.Index >= 0 && row.Index < _defaultExtraKeys.Count) _defaultExtraKeys.RemoveAt(row.Index);
        RefreshMaskedKeys();
    }

    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = new AiApiEndpoint { Id = Guid.NewGuid().ToString("N"), Name = $"接口 {_endpoints.Count + 2}", Enabled = true };
        _endpoints.Add(endpoint);
        EndpointsCombo.SelectedItem = endpoint;
        RefreshEndpointChoices();
        EndpointNameBox.Focus();
        EndpointNameBox.SelectAll();
    }

    private void RemoveEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        var linked = _customModels.Where(m => string.Equals(m.ProviderId, endpoint.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (linked.Count > 0 && !MessageBoxDialog.ShowConfirm($"删除“{endpoint.Name}”及其 {linked.Count} 个模型？本次编辑可用“取消”放弃。", "删除接口")) return;
        foreach (var model in linked) _customModels.Remove(model);
        _endpoints.Remove(endpoint);
        EndpointsCombo.SelectedItem = _endpoints.FirstOrDefault();
        ShowEndpointEditor();
        RefreshEndpointChoices();
        RefreshCatalogPreservingSelection();
    }

    private void EndpointsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowEndpointEditor();

    private void ShowEndpointEditor()
    {
        if (EndpointEditor == null) return;
        _loadingEndpoint = true;
        try
        {
            var endpoint = EndpointsCombo.SelectedItem as AiApiEndpoint;
            EndpointEditor.IsEnabled = endpoint != null;
            EndpointNameBox.Text = endpoint?.Name ?? "";
            EndpointUrlBox.Text = endpoint?.BaseUrl ?? "";
            EndpointEnabledCheck.IsChecked = endpoint?.Enabled == true;
            EndpointNewKeyBox.Clear();
            RefreshMaskedKeys();
        }
        finally { _loadingEndpoint = false; }
    }

    private void EndpointFields_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _loadingEndpoint || EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        endpoint.Name = EndpointNameBox.Text.Trim();
        endpoint.BaseUrl = EndpointUrlBox.Text.Trim();
    }

    private void EndpointName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingEndpoint) return;
        EndpointsCombo.Items.Refresh();
        RefreshEndpointChoices();
        RefreshCatalogPreservingSelection();
    }

    private void EndpointEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingEndpoint || EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        endpoint.Enabled = EndpointEnabledCheck.IsChecked == true;
        RefreshEndpointChoices();
        RefreshCatalogPreservingSelection();
    }

    private void AddEndpointKey_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint) return;
        var key = EndpointNewKeyBox.Password.Trim();
        if (key.Length == 0) return;
        endpoint.ApiKeys ??= new();
        if (!endpoint.ApiKeys.Contains(key, StringComparer.Ordinal)) endpoint.ApiKeys.Add(key);
        EndpointNewKeyBox.Clear();
        RefreshMaskedKeys();
    }

    private void RemoveEndpointKey_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointsCombo.SelectedItem is not AiApiEndpoint endpoint || EndpointKeysList.SelectedItem is not MaskedKey row) return;
        if (row.Index >= 0 && row.Index < endpoint.ApiKeys.Count) endpoint.ApiKeys.RemoveAt(row.Index);
        RefreshMaskedKeys();
    }

    private void CommitPendingKeys()
    {
        AddDefaultKey_Click(this, new RoutedEventArgs());
        AddEndpointKey_Click(this, new RoutedEventArgs());
    }

    private List<AiApiEndpoint> FetchableEndpoints()
    {
        var list = _endpoints.Where(e => e.Enabled).Select(e => e.Clone()).ToList();
        // An empty legacy public interface should not prevent named-only configurations.
        var defaultKeys = new[] { ApiKeyBox.Password }.Concat(_defaultExtraKeys)
            .Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
        if (defaultKeys.Count > 0 || list.Count == 0 || _customModels.Any(m => AiModelIds.GetProviderId(m) == "custom"))
            list.Insert(0, new AiApiEndpoint { Id = "custom", Name = "公共自定义接口", BaseUrl = BaseUrlBox.Text.Trim(), ApiKeys = defaultKeys, Enabled = true });
        return list;
    }

    private bool ValidateApiConfiguration()
    {
        CommitPendingKeys();
        foreach (var endpoint in _endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Id) || endpoint.Id is "custom" or "builtin")
            {
                MessageBoxDialog.ShowWarning("命名接口的标识无效，请删除后重新添加该接口。", "AI 设置");
                return false;
            }
            if (endpoint.Enabled && (!TryNormalizeBaseUrl(endpoint.BaseUrl, out _) || string.IsNullOrWhiteSpace(endpoint.Name)))
            {
                MessageBoxDialog.ShowWarning($"接口“{endpoint.Name}”需要名称和有效的 http/https Base URL。", "AI 设置");
                return false;
            }
        }
        if (_endpoints.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            MessageBoxDialog.ShowWarning("接口标识重复，请删除重复接口后重新添加。", "AI 设置");
            return false;
        }
        var usesCommon = _customModels.Any(m => AiModelIds.GetProviderId(m) == "custom");
        if (usesCommon && !TryNormalizeBaseUrl(BaseUrlBox.Text, out _))
        {
            MessageBoxDialog.ShowWarning("公共自定义接口地址无效，请填写有效的 http/https Base URL。", "AI 设置");
            return false;
        }
        if (TryNormalizeBaseUrl(BaseUrlBox.Text, out var baseUrl)) BaseUrlBox.Text = baseUrl;
        foreach (var endpoint in _endpoints.Where(e => e.Enabled))
            if (TryNormalizeBaseUrl(endpoint.BaseUrl, out var normalized)) endpoint.BaseUrl = normalized;
        return true;
    }

    private async Task FetchEndpointModelsAsync(bool allEndpoints)
    {
        if (_fetchingModels) return;
        CommitPendingKeys();
        var endpoints = FetchableEndpoints();
        if (!allEndpoints)
        {
            var selectedId = ModelEndpointCombo.SelectedValue as string ?? "custom";
            if (selectedId == "custom" && endpoints.All(e => e.Id != "custom"))
                endpoints.Insert(0, new AiApiEndpoint { Id = "custom", Name = "公共自定义接口", BaseUrl = BaseUrlBox.Text.Trim(), ApiKeys = new(), Enabled = true });
            endpoints = endpoints.Where(e => e.Id == selectedId).ToList();
            // Old standalone-provider fields remain usable for imported configurations.
            var legacyUrl = CustomModelProviderBaseUrlBox.Text.Trim();
            if (legacyUrl.Length > 0 || CustomModelProviderApiKeyBox.Password.Length > 0)
            {
                var legacy = new AiModelDefinition { Id = "endpoint", ProviderBaseUrl = legacyUrl };
                endpoints = new() { new AiApiEndpoint { Id = AiModelIds.GetProviderId(legacy), Name = "独立接口", BaseUrl = legacyUrl,
                    ApiKeys = new() { CustomModelProviderApiKeyBox.Password }, Enabled = true } };
            }
        }
        if (endpoints.Count == 0) { ToastService.ShowWarning("请先选择或启用一个接口。"); return; }
        _fetchingModels = true;
        FetchModelsButton.IsEnabled = FetchAllModelsButton.IsEnabled = false;
        var report = new List<string>();
        int totalAdded = 0, failures = 0;
        try
        {
            foreach (var endpoint in endpoints)
            {
                FetchModelsStatusText.Text = $"正在拉取：{endpoint.Name}…";
                if (!TryNormalizeBaseUrl(endpoint.BaseUrl, out var baseUrl))
                { report.Add($"{endpoint.Name}：接口地址无效"); failures++; continue; }
                var keys = (endpoint.ApiKeys ?? new()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
                if (keys.Count == 0) keys.Add(""); // Local compatible servers may have no authentication.
                var requestEndpoint = endpoint.Clone();
                requestEndpoint.BaseUrl = baseUrl;
                requestEndpoint.ApiKeys = keys;
                var result = await AiModelListFetcher.FetchAsync(requestEndpoint, ApiKeyFailoverCheck.IsChecked == true);
                if (result?.Success != true)
                {
                    var error = result?.Error ?? "未返回结果";
                    foreach (var key in keys.Where(k => k.Length > 0)) error = error.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);
                    report.Add($"{endpoint.Name}：失败，{error}"); failures++; continue;
                }
                int added = 0, existed = 0;
                foreach (var fetched in result.Models)
                {
                    if (!TryNormalizeModelId(fetched.Id, out var id, out _)) continue;
                    var model = fetched.Clone();
                    model.Id = id;
                    model.ProviderId = endpoint.Id;
                    model.ProviderName = endpoint.Name;
                    if (endpoint.Id != "custom" && !_endpoints.Any(e => e.Id == endpoint.Id))
                    {
                        // Empty IDs retain the legacy Base URL based provider identity.
                        model.ProviderId = string.Empty;
                        model.ProviderBaseUrl = baseUrl;
                        model.ProviderApiKey = keys[0];
                    }
                    if (_customModels.Any(m => string.Equals(m.ModelKey, model.ModelKey, StringComparison.OrdinalIgnoreCase))) { existed++; continue; }
                    _customModels.Add(model);
                    added++;
                }
                totalAdded += added;
                report.Add($"{endpoint.Name}：新增 {added}，已有 {existed}，跳过非对话模型 {result.SkippedNonChat}");
            }
            RefreshCatalogPreservingSelection();
            FetchModelsStatusText.Text = string.Join("\n", report);
            if (failures > 0) ToastService.ShowWarning($"已新增 {totalAdded} 个模型；{failures} 个接口拉取失败，成功结果已保留，请查看各接口结果。");
            else ToastService.ShowSuccess($"已完成 {endpoints.Count} 个接口拉取，新增 {totalAdded} 个模型。");
        }
        finally
        {
            _fetchingModels = false;
            FetchModelsButton.IsEnabled = FetchAllModelsButton.IsEnabled = true;
        }
    }

    private void EditCustomModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AiModelDefinition model }) return;
        CustomModelIdBox.Text = model.Id;
        CustomModelNameBox.Text = model.DisplayName;
        CustomModelProviderBaseUrlBox.Text = model.ProviderBaseUrl ?? "";
        CustomModelProviderApiKeyBox.Password = model.ProviderApiKey ?? "";
        ModelEndpointCombo.SelectedValue = model.ProviderId ?? "custom";
        CustomModelTierNormalRadio.IsChecked = model.Tier == AiModelTier.Normal;
        CustomModelTierExpertRadio.IsChecked = model.Tier == AiModelTier.Expert;
    }

    private void RefreshCatalogPreservingSelection()
    {
        if (_loading) return;
        var normal = NormalModelCombo.SelectedValue as string;
        var expert = ExpertModelCombo.SelectedValue as string;
        var manual = ManualModelCombo.SelectedValue as string;
        SaveAccountingModel();
        _loading = true;
        try { RefreshModelCombos(normal, expert, manual); }
        finally { _loading = false; }
    }

    private static long? ReadQuota(string? text) => long.TryParse(text, out var value) && value >= 0 ? value : null;

    private void ResetModelUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_accountingModel == null) return;
        SaveAccountingModel();
        var accounting = _accounting[_accountingModel];
        accounting.UsedTokens = 0;
        accounting.SpentUsd = 0;
        accounting.UsedRequests = 0;
        accounting.UsageIncludesEstimates = false;
        _resetUsageModelKeys.Add(_accountingModel);
        ShowAccountingModel();
        ToastService.ShowSuccess("本模型累计用量将在保存设置后重置。");
    }

    private void ModelFastSupported_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _refreshingAccounting || AccountingModelCombo.SelectedItem is not AiModelDefinition selected) return;
        var model = _customModels.FirstOrDefault(m => m.ModelKey == selected.ModelKey);
        if (model == null) return;
        model.SupportsFastMode = ModelFastSupportedCheck.IsChecked == true;
        RefreshCatalogPreservingSelection();
    }

    private void SelectReasoningLevel(AiReasoningLevel level)
    {
        ReasoningLevelCombo.SelectedItem = ReasoningLevelCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, level.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? ReasoningLevelCombo.Items[0];
    }

    private AiReasoningLevel GetReasoningLevel()
    {
        if (ReasoningLevelCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<AiReasoningLevel>(tag, out var level))
            return level == AiReasoningLevel.Fast && FastReasoningItem.IsEnabled != true ? AiReasoningLevel.Off : level;
        return AiReasoningLevel.Off;
    }

    private void RefreshReasoningAvailability()
    {
        if (FastReasoningItem == null) return;
        IEnumerable<AiModelDefinition?> selected = GetRoutingMode() switch
        {
            AiRoutingMode.Normal => new[] { NormalModelCombo.SelectedItem as AiModelDefinition },
            AiRoutingMode.Expert => new[] { ExpertModelCombo.SelectedItem as AiModelDefinition },
            AiRoutingMode.SpecificModel => new[] { ManualModelCombo.SelectedItem as AiModelDefinition },
            _ => new[] { NormalModelCombo.SelectedItem as AiModelDefinition, ExpertModelCombo.SelectedItem as AiModelDefinition }
        };
        var available = selected.All(m => m?.SupportsFastMode == true);
        FastReasoningItem.IsEnabled = available;
        FastReasoningHintText.Text = available ? "当前路由所选模型已标记为支持极速。"
            : "极速仅在当前路由所选模型均明确支持时可选；可在每模型设置中确认供应商能力。";
        if (!available && ReferenceEquals(ReasoningLevelCombo.SelectedItem, FastReasoningItem))
        {
            SelectReasoningLevel(AiReasoningLevel.Off);
            ToastService.ShowWarning("当前路由所选模型未全部声明支持极速，已自动改为 off 模式。");
        }
    }
}
