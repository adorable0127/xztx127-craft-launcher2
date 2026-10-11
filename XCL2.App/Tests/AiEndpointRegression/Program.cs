using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
}
var config = new AiAssistantConfig
{
    UseCustomApiKey = true, BaseUrl = "https://one.example/v1", ApiKey = "first-test-only",
    ApiKeys = new() { "second-test-only", "first-test-only" },
    ApiEndpoints = new()
    {
        new() { Id = "second", Name = "第二接口", BaseUrl = "https://two.example/v1", ApiKeys = new() { "third-test-only" } },
        new() { Id = "disabled", Name = "停用接口", BaseUrl = "https://disabled.example/v1", Enabled = false, ApiKeys = new() { "never" } }
    },
    CustomModels = new()
    {
        new() { Id = "same-model", DisplayName = "主模型", Tier = AiModelTier.Expert },
        new() { Id = "same-model", DisplayName = "同名第二接口", ProviderId = "second", Tier = AiModelTier.Normal },
        new() { Id = "peer-model", DisplayName = "同等级模型", ProviderId = "second", Tier = AiModelTier.Expert },
        new() { Id = "different-tier", ProviderId = "second", Tier = AiModelTier.Normal },
        new() { Id = "same-model", ProviderId = "disabled", Tier = AiModelTier.Expert }
    },
    SelectedModel = "same-model", NormalModelId = "same-model", ExpertModelId = "same-model",
    EnableDeepThinking = true,
    ModelAccounting = new() { ["same-model"] = new() { InputPricePerMillion = 2, OutputPricePerMillion = 4, UsedTokens = 100, SpentUsd = 0.25m } }
};
var primaryKey = AiModelIds.BuildModelKey("custom", "same-model");
var secondaryKey = AiModelIds.BuildModelKey("second", "same-model");
var catalog = AiModelIds.GetCatalog(config);
Check(catalog.Count(m => m.Id == "same-model") == 2, "same ID in different enabled providers retained");
Check(catalog.Select(m => m.ModelKey).Distinct().Count() == catalog.Count, "keys unique per provider model");
Check(AiModelIds.FindInCatalog(config, secondaryKey)?.ProviderId == "second", "precise selection finds second provider");
Check(AiModelIds.FindInCatalog(config, "same-model")?.ProviderId == "custom", "legacy selection preserves main custom provider");
Check(AiModelIds.FindInCatalog(config, AiModelIds.BuildModelKey("unknown", "same-model")) == null, "unknown composite identity never raw-falls-back");
var disabledFailover = AiEndpointRouting.GetCandidates(config, primaryKey);
Check(disabledFailover.Count == 1, "endpoint failover off never crosses providers");
Check(disabledFailover[0].ApiKeys.SequenceEqual(new[] { "first-test-only", "second-test-only" }), "key order primary plus backups without duplicates");
config.EnableApiKeyFailover = false;
Check(AiEndpointRouting.GetCandidates(config, primaryKey)[0].ApiKeys.Count == 1, "key failover switch respected");
config.EnableApiKeyFailover = true;
config.EnableEndpointFailover = true;
var fallback = AiEndpointRouting.GetCandidates(config, primaryKey);
Check(fallback.Count == 3 && fallback[1].ModelKey == secondaryKey && fallback[2].ModelId == "peer-model", "same model then same tier only");
Check(fallback[1].ApiKeys.Single() == "third-test-only", "provider credentials never cross endpoints");
Check(BuiltInAiDefaults.Fetches == 0, "constructing custom candidates never fetches built-in credentials");
config.MixBuiltInApi = true;
var mixed = AiModelIds.GetCatalog(config);
Check(mixed.Any(m => m.ProviderId == "builtin") && mixed.Any(m => m.ProviderId == "second"), "mixed mode includes built-in and custom");
AiEndpointRouting.GetCandidates(config, primaryKey);
Check(BuiltInAiDefaults.Fetches == 0, "even mixed fallback creation is lazy for built-in keys");
var builtin = AiEndpointRouting.GetCandidates(config, AiModelIds.BuildModelKey("builtin", AiModelIds.NemotronLightning))[0];
Check((await AiEndpointRouting.ResolveApiKeysAsync(builtin)).Single() == "builtin-test-only" && BuiltInAiDefaults.Fetches == 1, "selected built-in resolves its own key lazily");
AiEndpointRouting.NormalizeConfig(config);
Check(config.SelectedModel == primaryKey && config.ReasoningLevel == AiReasoningLevel.High, "legacy selection and reasoning migrate");
Check(!config.ModelAccounting.ContainsKey("same-model") && config.ModelAccounting.ContainsKey(primaryKey), "raw accounting migrated once and removed");
Check(AiUsageAccounting.Settings(config, secondaryKey).UsedTokens == 0 && AiUsageAccounting.Settings(config, secondaryKey).InputPricePerMillion == null, "second endpoint does not inherit raw accounting");
config.UseCustomApiKey = false;
AiEndpointRouting.NormalizeConfig(config);
Check(AiUsageAccounting.Settings(config, secondaryKey).SpentUsd == 0, "mode switch cannot replay old raw accounting");
config.UseCustomApiKey = true;
config.ModelAccounting[secondaryKey] = new() { InputPricePerMillion = 10, OutputPricePerMillion = 20, TokenQuota = 200, BudgetUsd = 1, MaxOutputTokens = 100 };
Check(AiUsageAccounting.CheckLimits(config, secondaryKey, 100, 101) != null, "per-model output limit checked");
Check(AiUsageAccounting.CheckLimits(config, secondaryKey, 190, 20) != null, "local token quota considers input and reserved output");
Check(AiUsageAccounting.CheckLimits(config, secondaryKey, 100, 50) == null, "within limits accepted");
var usage = new AiRequestUsage { Model = "same-model", ModelKey = secondaryKey, InProgress = false,
    InputTokens = 100, OutputTokens = 20, TotalTokens = 120, EstimatedCostUsd = 0.002m, IsEstimated = true };
AiUsageAccounting.RecordUsage(config, secondaryKey, usage);
AiUsageAccounting.RecordUsage(config, secondaryKey, usage);
Check(AiUsageAccounting.Settings(config, secondaryKey).UsedTokens == 120 && AiUsageAccounting.Settings(config, secondaryKey).UsedRequests == 1, "usage recorded exactly once");
Check(AiUsageAccounting.Settings(config, secondaryKey).SpentUsd == 0.002m && AiUsageAccounting.Settings(config, secondaryKey).UsageIncludesEstimates, "snapshot cost preserved and estimates labeled");
Check(AiUsageAccounting.Settings(config, primaryKey).UsedTokens == 100 && AiUsageAccounting.Settings(config, primaryKey).SpentUsd == 0.25m, "other provider accounting untouched");
AiUsageAccounting.RecordUsage(config, secondaryKey, new() { InProgress = false, WasRejected = true, InputTokens = 999, TotalTokens = 999 });
Check(AiUsageAccounting.Settings(config, secondaryKey).UsedRequests == 1, "explicit rejected request does not consume quota");
Check(AiUsageAccounting.CheckLimits(config, secondaryKey, 60, 30) != null, "subsequent request respects remaining quota");
var legacy = new AiModelDefinition { Id = "abc", ProviderBaseUrl = "https://legacy.example/v1", ProviderApiKey = "secret-A" };
var originalKey = legacy.ModelKey;
legacy.ProviderApiKey = "secret-B";
Check(legacy.ModelKey == originalKey && !legacy.ModelKey.Contains("secret"), "rotating legacy secret does not change identity");
Check(AiModelIds.LegacyProviderId("https://user:password@legacy.example/v1?api_key=a") == AiModelIds.LegacyProviderId("https://legacy.example/v1?api_key=b"), "URL credentials do not affect identity");
var restored = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(config))!;
Check(AiModelIds.FindInCatalog(restored, secondaryKey)?.Id == "same-model" && restored.ApiEndpoints[0].ApiKeys.Single() == "third-test-only", "endpoint and model identities survive config round trip");
var oldDefault = new AiAssistantConfig();
AiEndpointRouting.NormalizeConfig(oldDefault);
Check(!oldDefault.EnableEndpointFailover && !oldDefault.MixBuiltInApi && oldDefault.ReasoningLevel == AiReasoningLevel.Off, "migration does not silently enable optional modes");
Console.WriteLine($"PASS: {checks} endpoint and quota regression checks");
