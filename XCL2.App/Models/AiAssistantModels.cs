using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace XCL2.App.Models;

/// <summary>AI 模型条目。Id 是实际发给 OpenAI 兼容接口的 model；DisplayName 只用于界面展示。
/// ProviderBaseUrl/ProviderApiKey 用于"多供应商"：留空表示沿用上方公共的 Base URL / API Key
/// （内置模型固定沿用内置接口；自定义模型表里的条目沿用 config.BaseUrl/ApiKey）。填了的话，
/// 这一条模型请求时改用它自己的接口地址和 Key —— 这样用户可以在同一个模型表里，同时混用
/// 多个不同供应商（不同 Base URL + 不同 API Key）的模型，不需要在设置里反复切换。</summary>
public class AiModelDefinition
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public bool SupportsFastMode { get; set; }
    /// <summary>配置、选择与计费使用接口 + 模型身份；请求体仍发送 Id。</summary>
    [JsonIgnore]
    public string ModelKey => AiModelIds.BuildModelKey(AiModelIds.GetProviderId(this), Id);
    public string? ProviderBaseUrl { get; set; }
    public string? ProviderApiKey { get; set; }

    /// <summary>该模型所属分类：普通（轻量、响应快，适合日常问答/功能入口讲解）或
    /// 专家（能力更强，适合复杂问题/日志排障/复杂编程），仅用于界面分组展示。</summary>
    public AiModelTier Tier { get; set; } = AiModelTier.Normal;

    public bool HasOwnProvider =>
        !string.IsNullOrWhiteSpace(ProviderBaseUrl) && !string.IsNullOrWhiteSpace(ProviderApiKey);

    /// <summary>仅供界面展示："独立供应商：https://..."；没有单独供应商时为空字符串
    /// （而不是 null），这样直接绑定到 TextBlock.Text 也不会报绑定错误，空文本自然不占视觉重量。</summary>
    public string ProviderSummary => HasOwnProvider ? $"独立供应商：{ProviderBaseUrl}" : "";

    /// <summary>Tier 的中文展示文本，供界面直接绑定，避免枚举 ToString() 显示英文。</summary>
    public string TierLabel => Tier == AiModelTier.Expert ? "专家" : "普通";

    /// <summary>"切换分组"按钮上的文字：当前是专家就显示"改为普通"，反之亦然。</summary>
    public string TierSwitchLabel => Tier == AiModelTier.Expert ? "改为普通" : "改为专家";

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;

    public AiModelDefinition Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ProviderId = ProviderId,
        ProviderName = ProviderName,
        SupportsFastMode = SupportsFastMode,
        ProviderBaseUrl = ProviderBaseUrl,
        ProviderApiKey = ProviderApiKey,
        Tier = Tier
    };
}

/// <summary>模型分类：普通档（轻量/快）还是专家档（更强/更慢）。只是分组标签，
/// 跟 AiRoutingMode 里的 Normal/Expert 路由档位是两回事——路由档位决定"这次对话用哪个模型"，
/// 这个 Tier 只决定"这个模型该出现在设置页的普通分组还是专家分组里"。</summary>
public enum AiModelTier
{
    Normal = 0,
    Expert = 1
}

/// <summary>
/// AI 路由模式。普通/专家只是“该用哪一个默认模型”的语义档位，不是模型本身。
/// SpecificModel 才表示用户绕过路由，直接指定一个实际模型 ID。
/// </summary>
public enum AiRoutingMode
{
    Auto = 0,
    Normal = 1,
    Expert = 2,
    SpecificModel = 3
}

/// <summary>内置接口（OpenRouter）目前提供的预设免费模型，按"普通/专家"两档分组。
/// 模型 ID 使用 OpenRouter 上的真实 model 值（含大小写/连字符），不是随手拼的；OpenRouter 上的
/// 免费模型会不定期调整（下线、改名、限流），如果某个内置模型出现 400/404，通常就是供应商那边
/// 把这个免费端点调整了，去 https://openrouter.ai/models?max_price=0 核对一下最新的模型 ID 即可，
/// 不代表启动器这边配置有问题。
/// 注意：Embedding / Rerank / Content-Safety 这几类模型不是对话模型（不能走 /chat/completions
/// 拿到正常回复），所以不放进下面的普通/专家模型表——即使 OpenRouter 免费列表里有它们。</summary>
public static class AiModelIds
{
    // 普通档：轻量、响应快，适合日常问答、功能入口讲解这类简单问题。
    public const string NemotronLightning = "nvidia/nemotron-3.5-lightning:free";
    public const string Lfm25 = "liquid/lfm-2.5-2.6b:free";
    public const string Gemma4_26B = "google/gemma-4-26b-a4b-it:free";
    public const string NexN25Mini = "nex-agi/nex-n2.5-mini:free";
    public const string LingFlashSante = "inclusionai/ling-3.0-flash-sante:free";
    public const string LingFlashFin = "inclusionai/ling-3.0-flash-fin:free";

    // 专家档：参数规模更大/推理更强，适合复杂编程、日志与崩溃分析等排障场景。
    public const string Nemotron3Ultra = "nvidia/nemotron-3-ultra-550b-a55b:free";
    public const string Nemotron3Super = "nvidia/nemotron-3-super-120b-a12b:free";
    public const string NemotronNanoOmni = "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free";
    public const string Gemma4_31B = "google/gemma-4-31b-it:free";
    public const string InklingSmall = "thinkingmachines/inkling-small:free";
    public const string Inkling = "thinkingmachines/inkling:free";
    public const string LagunaS21 = "poolside/laguna-s-2.1:free";
    public const string NexN25Pro = "nex-agi/nex-n2.5-pro:free";
    public const string NorthMiniCode = "cohere/north-mini-code:free";

    // 以下几个是 Embedding / Rerank / Content-Safety 模型，不能走 /chat/completions 对话，
    // 不出现在 BuiltIn 模型表里；只在这里留个常量方便代码里按需引用/判断。
    public const string NemotronContentSafety = "nvidia/nemotron-3.5-content-safety:free";
    public const string NemotronEmbed1B = "nvidia/nemotron-3-embed-1b:free";
    public const string LlamaNemotronEmbedVl1BV2 = "nvidia/llama-nemotron-embed-vl-1b-v2:free";
    public const string LlamaNemotronRerankVl1BV2 = "nvidia/llama-nemotron-rerank-vl-1b-v2:free";
    public const string LfmEmbedding350M = "liquid/lfm-2.5-embedding-350m:free";

    // 保留旧字段名兼容旧配置的反序列化引用位置（值指向新的普通/专家默认模型）。
    public const string Nemotron35Lightning = NemotronLightning;
    public const string MimoV25 = Nemotron3Ultra;

    public static readonly IReadOnlyList<AiModelDefinition> BuiltIn = new List<AiModelDefinition>
    {
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = NemotronLightning, DisplayName = "Nemotron 3.5 Lightning（普通·免费）", Tier = AiModelTier.Normal },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = Lfm25, DisplayName = "LFM2.5-2.6B（普通·免费）", Tier = AiModelTier.Normal },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = Gemma4_26B, DisplayName = "Gemma 4 26B A4B（普通·免费·常限流）", Tier = AiModelTier.Normal },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = NexN25Mini, DisplayName = "Nex-N2.5-Mini（普通·免费）", Tier = AiModelTier.Normal },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = LingFlashSante, DisplayName = "Ling 3.0 Flash Sante（普通·医疗领域·免费）", Tier = AiModelTier.Normal },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = LingFlashFin, DisplayName = "Ling 3.0 Flash Fin（普通·金融领域·免费）", Tier = AiModelTier.Normal },

        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = Nemotron3Ultra, DisplayName = "Nemotron 3 Ultra（专家·免费）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = Nemotron3Super, DisplayName = "Nemotron 3 Super（专家·免费）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = NemotronNanoOmni, DisplayName = "Nemotron 3 Nano Omni（专家·多模态推理·免费）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = Gemma4_31B, DisplayName = "Gemma 4 31B（专家·免费）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = InklingSmall, DisplayName = "Inkling Small（专家·免费·暂不可用）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = Inkling, DisplayName = "Inkling（专家·免费·暂不可用）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = LagunaS21, DisplayName = "Laguna S 2.1（专家·免费）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = NexN25Pro, DisplayName = "Nex-N2.5-Pro（专家·免费）", Tier = AiModelTier.Expert },
        new() { ProviderId = BuiltInProviderId, ProviderName = "内置 API", Id = NorthMiniCode, DisplayName = "North Mini Code（专家·免费）", Tier = AiModelTier.Expert }
    };

    /// <summary>实测发现某些内置模型目前有问题（供应商侧限制/限流），不是配置错误，用户没必要
    /// 反复排查。Key 是模型 ID，Value 是给用户看的简要说明。跟 SpecialTermsNotices 不是一回事：
    /// 那个是"能用，但有条款要注意"；这个是"选了大概率会请求失败"。
    /// - Inkling / Inkling Small：供应商把这个免费端点限定为只给 Agent 类工具（编程/生产力应用）
    ///   调用，普通对话请求会被直接拒绝（HTTP 403），是结构性限制，不是网络或 Key 的问题。
    /// - Gemma 4 26B A4B：近期实测经常被限流（HTTP 429），可能是免费额度紧张，不代表模型下线，
    ///   换个时间或换个模型通常就好了。
    /// 供应商恢复正常后，把对应条目从这个字典删掉、DisplayName 里的"暂不可用/常限流"去掉即可。</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownIssueNotices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [InklingSmall] = "供应商限制：此免费端点只允许 Agent 类工具（编程/生产力应用）调用，普通对话请求会被直接拒绝（HTTP 403），暂不建议选用，建议换成 Nemotron 3 Ultra 等其它专家模型。",
            [Inkling] = "供应商限制：此免费端点只允许 Agent 类工具（编程/生产力应用）调用，普通对话请求会被直接拒绝（HTTP 403），暂不建议选用，建议换成 Nemotron 3 Ultra 等其它专家模型。",
            [Gemma4_26B] = "实测近期经常被供应商限流（HTTP 429），可能是免费额度紧张，不代表模型已下线；如果频繁失败，建议先换成 Nemotron 3.5 Lightning 等其它普通模型。",
            [Gemma4_31B] = "实测近期经常请求失败/被供应商限流，暂不建议选用，建议换成 Nemotron 3 Ultra 等其它专家模型。"
        };

    public static bool HasKnownIssue(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && KnownIssueNotices.ContainsKey(modelId.Trim());

    public static string? GetKnownIssueNotice(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        return KnownIssueNotices.TryGetValue(modelId.Trim(), out var notice) ? notice : null;
    }

    /// <summary>部分厂商对自己的免费端点有独立的使用条款/数据处理说明（不同于 OpenRouter 通用条款），
    /// 选中这些模型时要在界面上提示用户。Key 是模型 ID，Value 是给用户看的简要提示文案。</summary>
    public static readonly IReadOnlyDictionary<string, string> SpecialTermsNotices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [InklingSmall] =
                "Thinking Machines 提示：此免费端点仅供 Agent 类工具使用；请勿上传任何机密信息或个人隐私内容（如人脸照片/声音）。" +
                "你的使用记录（含提示词与输出）会被用于改进 Thinking Machines 的模型与服务，使用即代表同意其免费研究 API 条款。",
            [Inkling] =
                "Thinking Machines 提示：此免费端点仅供 Agent 类工具使用；请勿上传任何机密信息或个人隐私内容（如人脸照片/声音）。" +
                "你的使用记录（含提示词与输出）会被用于改进 Thinking Machines 的模型与服务，使用即代表同意其免费研究 API 条款。",
            [NemotronContentSafety] =
                "NVIDIA 提示：请勿上传任何机密信息；如上传图片，NVIDIA 及其服务商可能将其用于提供本演示服务，" +
                "使用记录会被记录并可能用于改进 NVIDIA 产品与服务，使用即代表同意 NVIDIA API 试用条款。",
            [Nemotron3Ultra] =
                "NVIDIA 提示：请勿上传任何机密信息；如上传图片，NVIDIA 及其服务商可能将其用于提供本演示服务，" +
                "使用记录会被记录并可能用于改进 NVIDIA 产品与服务，使用即代表同意 NVIDIA API 试用条款。",
            [Nemotron3Super] =
                "NVIDIA 提示：请勿上传任何机密信息；如上传图片，NVIDIA 及其服务商可能将其用于提供本演示服务，" +
                "使用记录会被记录并可能用于改进 NVIDIA 产品与服务，使用即代表同意 NVIDIA API 试用条款。",
            [NemotronEmbed1B] =
                "NVIDIA 提示：请勿上传任何机密信息；使用记录会被记录并可能用于改进 NVIDIA 产品与服务，使用即代表同意 NVIDIA API 试用条款。",
            [LlamaNemotronEmbedVl1BV2] =
                "NVIDIA 提示：请勿上传任何机密信息；如上传图片，NVIDIA 及其服务商可能将其用于提供本演示服务，使用即代表同意 NVIDIA API 试用条款。",
            [LlamaNemotronRerankVl1BV2] =
                "NVIDIA 提示：请勿上传任何机密信息；如上传图片，NVIDIA 及其服务商可能将其用于提供本演示服务，使用即代表同意 NVIDIA API 试用条款。",
            [LfmEmbedding350M] =
                "Liquid AI 提示：请勿上传任何机密信息。你成功的请求与生成的向量可能被 Liquid AI 保留并用于训练其模型。"
        };

    /// <summary>某个模型是否有需要额外提示用户的独立使用条款。</summary>
    public static bool HasSpecialTerms(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && SpecialTermsNotices.ContainsKey(modelId.Trim());

    /// <summary>取某个模型的独立条款提示文案；没有则返回 null。</summary>
    public static string? GetSpecialTermsNotice(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        return SpecialTermsNotices.TryGetValue(modelId.Trim(), out var notice) ? notice : null;
    }

    // 保留旧代码兼容入口。
    public static readonly string[] Available = BuiltIn.Select(m => m.Id).ToArray();

    /// <summary>内置模型目前都视为可用；不再维护"个别模型暂时不可用"的白名单——OpenRouter
    /// 侧模型下线/限流时直接看请求报错即可，不需要客户端提前猜哪些能用。</summary>
    public static bool IsAvailableByDefault(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) &&
        BuiltIn.Any(m => string.Equals(m.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));

    public const string BuiltInProviderId = "builtin";
    public const string CustomProviderId = "custom";

    public static string BuildModelKey(string providerId, string modelId) =>
        Uri.EscapeDataString((providerId ?? "").Trim()) + "::" + Uri.EscapeDataString((modelId ?? "").Trim());

    public static bool TrySplitModelKey(string? modelKey, out string providerId, out string modelId)
    {
        providerId = modelId = "";
        var separator = modelKey?.IndexOf("::", StringComparison.Ordinal) ?? -1;
        if (separator < 1) return false;
        try
        {
            providerId = Uri.UnescapeDataString(modelKey![..separator]);
            modelId = Uri.UnescapeDataString(modelKey[(separator + 2)..]);
            return modelId.Length > 0;
        }
        catch (UriFormatException) { return false; }
    }

    /// <summary>旧版模型的独立接口也有稳定身份；密钥、查询参数及 URL 用户信息不参与身份。</summary>
    public static string LegacyProviderId(string baseUrl)
    {
        var identity = (baseUrl ?? "").Trim().TrimEnd('/');
        if (Uri.TryCreate(identity, UriKind.Absolute, out var uri))
            identity = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/');
        else
            identity = identity.Split('?', '#')[0];
        return "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
    }

    public static string GetProviderId(AiModelDefinition model)
    {
        var id = model.ProviderId?.Trim();
        if (string.Equals(id, BuiltInProviderId, StringComparison.OrdinalIgnoreCase)) return BuiltInProviderId;
        if (string.Equals(id, CustomProviderId, StringComparison.OrdinalIgnoreCase)) return CustomProviderId;
        return !string.IsNullOrWhiteSpace(id) ? id
            : !string.IsNullOrWhiteSpace(model.ProviderBaseUrl) ? LegacyProviderId(model.ProviderBaseUrl) : CustomProviderId;
    }

    public static List<AiModelDefinition> GetCatalog(AiAssistantConfig config)
    {
        var result = new List<AiModelDefinition>();
        if (!config.UseCustomApiKey || config.MixBuiltInApi)
            foreach (var model in BuiltIn)
            {
                var clone = model.Clone();
                clone.ProviderId = BuiltInProviderId;
                clone.ProviderName = "内置 API";
                clone.Tier = ResolveBuiltInTier(config, clone.Id, clone.Tier);
                clone.DisplayName = AdjustDisplayNameForTier(clone.DisplayName, clone.Tier);
                result.Add(clone);
            }

        if (config.UseCustomApiKey || config.MixBuiltInApi)
            foreach (var item in config.CustomModels ?? new List<AiModelDefinition>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id)) continue;
                var clone = item.Clone();
                clone.Id = item.Id.Trim();
                clone.ProviderId = GetProviderId(item);
                // 命名接口必须仍然存在且启用；不把失效配置回落到另一个接口的密钥。
                if (clone.ProviderId != CustomProviderId && clone.ProviderId != BuiltInProviderId
                    && !clone.ProviderId.StartsWith("legacy-", StringComparison.Ordinal))
                {
                    var endpoint = config.ApiEndpoints?.FirstOrDefault(e => e != null
                        && string.Equals(e.Id, clone.ProviderId, StringComparison.OrdinalIgnoreCase));
                    if (endpoint == null || !endpoint.Enabled) continue;
                    clone.ProviderName = string.IsNullOrWhiteSpace(endpoint.Name) ? "自定义接口" : endpoint.Name;
                }
                else if (clone.ProviderId == BuiltInProviderId)
                {
                    // 自定义模型表不能通过填写保留身份混入内置凭据。
                    continue;
                }
                else if (string.IsNullOrWhiteSpace(clone.ProviderName))
                    clone.ProviderName = clone.ProviderId == CustomProviderId ? "自定义主接口" : "独立接口";
                clone.DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? clone.Id : item.DisplayName.Trim();
                result.Add(clone);
            }

        return result.GroupBy(m => m.ModelKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>内置模型的显示名里写死了"（普通·…）/（专家·…）"这样的分组字样；用户把它调到另一组之后，
    /// 同步把括号里的分组字样换掉，避免出现"分组是专家、名字却写着普通"的矛盾。</summary>
    public static string AdjustDisplayNameForTier(string displayName, AiModelTier tier)
    {
        if (string.IsNullOrEmpty(displayName)) return displayName;
        return tier == AiModelTier.Expert
            ? displayName.Replace("（普通·", "（专家·")
            : displayName.Replace("（专家·", "（普通·");
    }

    /// <summary>内置模型的实际分组：用户在设置页里手动调整过（ModelTierOverrides）就用用户的，
    /// 否则沿用代码里写死的默认分组。</summary>
    public static AiModelTier ResolveBuiltInTier(AiAssistantConfig config, string? modelId, AiModelTier fallback)
    {
        if (string.IsNullOrWhiteSpace(modelId) || config.ModelTierOverrides == null) return fallback;
        foreach (var pair in config.ModelTierOverrides)
        {
            if (string.Equals(pair.Key, modelId.Trim(), StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return fallback;
    }

    public static string GetDisplayName(AiAssistantConfig config, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return "未知模型";
        var match = FindInCatalog(config, modelId);
        if (match != null) return match.DisplayName;
        var rawId = TrySplitModelKey(modelId, out _, out var id) ? id : modelId;
        return BuiltIn.FirstOrDefault(m => string.Equals(m.Id, rawId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? rawId;
    }

    /// <summary>新配置按接口 + 模型查找；旧配置的真实模型 ID 仍可读取。</summary>
    public static AiModelDefinition? FindInCatalog(AiAssistantConfig config, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var catalog = GetCatalog(config);
        var value = modelId.Trim();
        var exact = catalog.FirstOrDefault(m => string.Equals(m.ModelKey, value, StringComparison.OrdinalIgnoreCase));
        if (exact != null || TrySplitModelKey(value, out _, out _)) return exact;
        // 旧自定义配置优先保留自定义模型，不因开启混合而换成同名内置模型。
        return catalog.Where(m => string.Equals(m.Id, value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => config.UseCustomApiKey ? m.ProviderId == BuiltInProviderId : m.ProviderId != BuiltInProviderId)
            .FirstOrDefault();
    }
}

/// <summary>AI 助手全局配置，持久化在 AppConfig.AiAssistant。</summary>
public enum AiReasoningLevel { Off, Low, Medium, High, VeryHigh, Max, Fast }

/// <summary>Build 保持已有授权执行行为；Plan 只分析规划及使用已授权的只读工具。</summary>
public enum AiWorkMode { Build = 0, Plan = 1 }

public sealed class AiApiEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "自定义接口";
    public string BaseUrl { get; set; } = "";
    public List<string> ApiKeys { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public AiApiEndpoint Clone() => new() { Id = Id, Name = Name, BaseUrl = BaseUrl,
        ApiKeys = ApiKeys?.ToList() ?? new(), Enabled = Enabled };
    public override string ToString() => Name;
}

public class AiAssistantConfig
{
    public bool Enabled { get; set; } = true;
    public AiWorkMode WorkMode { get; set; } = AiWorkMode.Build;
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = "";
    public bool UseCustomApiKey { get; set; } = false;
    public List<string> ApiKeys { get; set; } = new();
    public List<AiApiEndpoint> ApiEndpoints { get; set; } = new();
    public bool MixBuiltInApi { get; set; }
    public bool EnableApiKeyFailover { get; set; } = true;
    public bool EnableEndpointFailover { get; set; }
    public AiReasoningLevel? ReasoningLevel { get; set; }

    /// <summary>默认路由模式：自动 / 普通 / 专家 / 指定模型。默认 Auto——普通问题走轻量模型、
    /// 复杂/排障问题走专家模型，不需要用户手动选。</summary>
    public AiRoutingMode RoutingMode { get; set; } = AiRoutingMode.Auto;

    /// <summary>
    /// 旧版兼容字段。新代码以 RoutingMode 为准；保存时同步成 RoutingMode==Auto。
    /// 老配置里 AutoModelRouting=false 会在 ConfigService.Load 时迁移为 SpecificModel。
    /// </summary>
    public bool AutoModelRouting { get; set; } = true;

    /// <summary>"指定模型"模式下实际使用的模型 ID。默认 Nemotron 3.5 Lightning（普通档）。</summary>
    public string SelectedModel { get; set; } = AiModelIds.NemotronLightning;

    /// <summary>普通档默认模型：功能入口、设置位置、常规使用说明等。默认 Nemotron 3.5 Lightning。</summary>
    public string NormalModelId { get; set; } = AiModelIds.NemotronLightning;

    /// <summary>专家档默认模型：日志、崩溃、注入、堆栈、复杂故障排查等。默认 Nemotron 3 Ultra。</summary>
    public string ExpertModelId { get; set; } = AiModelIds.Nemotron3Ultra;

    /// <summary>
    /// 自定义 API 的模型表。使用自定义 API 时不会猜供应商有哪些模型，必须由用户自己填写
    /// Model ID + 显示名称，面板/普通档/专家档的模型选择都只从这里取。
    /// 每个条目还可以单独填 ProviderBaseUrl/ProviderApiKey（多供应商）：留空则沿用下面这一份
    /// 公共 BaseUrl/ApiKey；填了就用自己的，这样可以在同一张表里混用多个不同供应商的模型。
    /// </summary>
    public List<AiModelDefinition> CustomModels { get; set; } = new();

    /// <summary>用户对"内置模型"分组（普通/专家）的手动调整：Key 是模型 ID，Value 是调整后的分组。
    /// 内置模型的默认分组写死在 AiModelIds.BuiltIn 里，改不了代码，所以单独记一份覆盖表；
    /// 没出现在这里的内置模型沿用默认分组。自定义模型的分组直接存在各自的 Tier 字段里，不走这张表。</summary>
    public Dictionary<string, AiModelTier> ModelTierOverrides { get; set; } = new();

    public bool StreamResponses { get; set; } = true;
    public bool RequestUsageMetadata { get; set; } = true;
    /// <summary>单次请求（含重试）的最长等待秒数，0 表示不限时；最长可设 24 小时。</summary>
    public int RequestTimeoutSeconds { get; set; } = 180;
    public int LongWaitHintSeconds { get; set; } = 45;
    public string LongWaitDescription { get; set; } = "模型仍在处理，请稍候；你可以随时停止生成。";
    /// <summary>按实际模型 ID 保存上下文容量与每百万 token 单价。留空表示未知，不猜测模型价格。</summary>
    public Dictionary<string, AiModelAccountingConfig> ModelAccounting { get; set; } = new();
    public bool SendTemperature { get; set; } = true;
    public double Temperature { get; set; } = 0.6;
    public int MaxOutputTokens { get; set; } = 0;
    /// <summary>每轮工具/命令操作上限，0 表示不限次数；仍遵守工作模式、既有授权和取消/超时。</summary>
    public int MaxToolActionsPerTurn { get; set; } = 0;
    /// <summary>次数策略迁移版本；旧版默认 4 次在首次加载时升级为不限次数。</summary>
    public int ToolActionLimitVersion { get; set; }

    internal void NormalizeToolActionLimit()
    {
        if (ToolActionLimitVersion < 1 && MaxToolActionsPerTurn == 4)
            MaxToolActionsPerTurn = 0;
        MaxToolActionsPerTurn = Math.Max(0, MaxToolActionsPerTurn);
        ToolActionLimitVersion = 1;
    }
    public int MaxSubAgentsPerTurn { get; set; } = 2;
    /// <summary>允许 Shell；普通模式逐次确认，完全绝对访问生效后免确认。最高权限开启后默认启用。</summary>
    public bool AllowShellCommands { get; set; } = false;
    public bool EnableDeepThinking { get; set; } = false;
    public bool EnableWebSearch { get; set; } = false;

    // 仅控制过程记录的显示，不授予工具权限，也不改变深度思考模式。
    public bool ShowSubAgents { get; set; } = true;
    public bool ShowCodeChanges { get; set; } = true;
    public bool ShowEditingFiles { get; set; } = true;
    public bool ShowCommands { get; set; } = true;
    public bool ShowReasoningSummary { get; set; } = true;
    public bool ShowReasoning { get; set; } = true;
    public int CompressionTokenThreshold { get; set; } = 18000;

    /// <summary>"省 Token 模式"。只在"用自己的 API key"或"自动路由"下能打开——自定义 key 是因为
    /// 用户自己承担调用成本，省 token 直接省钱；自动路由是因为它本来就会按问题复杂度换模型，
    /// 跟"少花 token"这个目标同一个方向，开着也不冲突。手动指定模型/普通/专家档这几个固定档位
    /// 模式跟"省不省 token"没什么关系，不提供这个开关（设置页会把勾选框禁用掉）。
    /// 效果两块：1) 上下文压缩更激进（阈值更低、摘要更短、保留的原始消息更少）；
    /// 2) 遇到 AiQuickAnswers 里能直接答的极简单问题（"MC 官网是什么"这类）不请求 API，
    /// 本地直接回答。
    /// 默认开启：不少模型供应商（尤其是免费/内置那些）对每日请求数做了限制，默认少发点请求
    /// 更不容易被限流打断正常使用；条件不满足时 EffectiveTokenSaverMode 会自动不生效，
    /// 不需要用户先手动关掉再手动开。</summary>
    public bool TokenSaverMode { get; set; } = true;

    /// <summary>省 Token 模式的开关是否允许打开（见 TokenSaverMode 注释里的条件）；
    /// UI 和 Service 两边都要判断这个条件，抽成方法避免两处写重复逻辑。</summary>
    public bool TokenSaverModeAllowed => UseCustomApiKey || RoutingMode == AiRoutingMode.Auto;

    /// <summary>实际生效的省 Token 模式：开关开着，但如果当前不满足条件（比如从自动切回指定模型），
    /// 也不生效——不需要额外弹提示，只是安静地不生效，等条件满足了自动又生效。</summary>
    public bool EffectiveTokenSaverMode => TokenSaverMode && TokenSaverModeAllowed;

    /// <summary>用户已同意的《AI 助手使用条款》版本号。小于 AiTermsDialog.CurrentVersion 时，
    /// 打开 AI 助手页面要先弹条款让用户同意，同意后写回这个字段。条款内容以后如果有实质性修改，
    /// 把 CurrentVersion 加 1，老用户会被要求重新确认一次；纯措辞调整不需要动版本号。</summary>
    public int AcceptedTermsVersion { get; set; } = 0;
    public double PanelWidth { get; set; } = 380;
    public bool ClearHistoryOnGuestSessionEnd { get; set; } = true;
    public bool ShowFloatingButton { get; set; } = false;

    /// <summary>AI 轻量编程工作目录。为空时文件工具完全不可用；AI 只能访问这个目录及其子目录，
    /// 每次读取/写入仍需用户逐次确认，不能通过 ../ 或绝对路径越界。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>额外批准给 AI 的目录（与 WorkingDirectory 合并后即为全部已批准目录；
    /// WorkingDirectory 仍作为"主目录"，相对路径以它为基准）。AI 通过 request_directory 申请、
    /// 用户在弹窗里同意后也会追加到这里。</summary>
    public List<string> WorkingDirectories { get; set; } = new();

    /// <summary>完全访问模式：开启后 AI 可（在每次请求仍需用户确认的前提下）访问电脑上任意路径。
    /// 开启时设置页会连续弹出两次系统提示，两次都确认才生效。</summary>
    public bool FullDiskAccess { get; set; } = false;

    /// <summary>绝对访问（仅在 FullDiskAccess 同时开启时生效）：AI 的文件列出/读取/写入不再弹窗确认，
    /// 静默执行，并把每一次操作写进启动器日志。启动器自身的配置/账户数据目录仍需逐次确认。</summary>
    public bool AbsoluteAccess { get; set; } = false;

    /// <summary>完全绝对访问（仅在 FullDiskAccess 与 AbsoluteAccess 同时开启时生效）：取消所有路径保护——
    /// 应用安装目录、Windows 系统目录、ProgramData 以及启动器自己的配置/账户目录都可以被 AI 静默读写。
    /// 已开启的 Shell 能力也免逐次确认。开启时设置页会连续弹出两次红色警告，两次都确认才生效。</summary>
    public bool UnrestrictedAccess { get; set; } = false;

    /// <summary>彻底完全访问（仅在完全访问、绝对访问、完全绝对访问都开启时生效）：AI 还可以修改注册表、
    /// 执行任意系统命令（PowerShell / cmd）。开启时设置页会连续弹出三次红色警告。</summary>
    public bool SystemControlAccess { get; set; } = false;
    public bool PanelWasOpen { get; set; } = false;

    /// <summary>是否允许把用户主动提交的崩溃/日志内容发给 AI 分析。默认改为 true——这是用户
    /// 主动点"发给 AI 分析"才会触发的场景（不是后台自动收集），默认打开能让首次使用时
    /// 这个核心排障功能开箱即用，不用先跑一趟设置页才能用。</summary>
    public bool AllowCrashLogReading { get; set; } = true;

    /// <summary>最后一次打开的会话 ID，用于重启/重新进入 AI 页面后继续原对话。</summary>
    public string? LastSessionId { get; set; }

    /// <summary>打开 AI 助手页面时，先在后台悄悄发一次"系统提示词预热请求"（不落盘到任何
    /// 会话、也不把回复展示给用户），提前把鉴权/路由/连接这些开销花掉，让用户真正发第一条
    /// 消息时能更快拿到回复。默认打开——这是纯粹的体验优化，失败了也静默忽略，不影响正常
    /// 使用；只有明确不想产生这次多余请求（比如按量计费的自定义 API）的用户才需要去设置里
    /// 关掉。</summary>
    public bool PrewarmSystemPromptOnOpen { get; set; } = true;

    /// <summary>人格预设 ID（见 AiPersonas：default / doubao / claude / companion）。
    /// 只改变语气，不改变知识库和安全边界。</summary>
    public string PersonaId { get; set; } = "default";

    /// <summary>用户自定义的系统提示词补充内容，会追加在人格设定之后；留空表示不使用。</summary>
    public string CustomSystemPrompt { get; set; } = "";
}

public enum AiMessageRole
{
    System,
    User,
    Assistant
}

public class AiChatMessage
{
    public AiMessageRole Role { get; set; } = AiMessageRole.User;
    public string Content { get; set; } = "";
    public string? ModelUsed { get; set; }
    public string? ModelDisplayName { get; set; }
    public AiRoutingMode RouteUsed { get; set; } = AiRoutingMode.Normal;
    public bool WasAutoRouted { get; set; }
    public int EstimatedTokens { get; set; }
    public List<AiAssistantActivity> Activities { get; set; } = new();
}

public class AiChatSession
{
    public string? TaskId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public bool IsGuestSession { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<AiChatMessage> Messages { get; set; } = new();
    /// <summary>该会话实际触发的 API 请求；包含压缩与子任务，撤回消息也不抹去已发生的消耗。</summary>
    public List<AiRequestUsage> UsageRequests { get; set; } = new();
    public string? SummaryOfOlderMessages { get; set; }
    public int SummarizedUpToIndex { get; set; } = -1;
}

/// <summary>金额统一为 USD / 百万 token。未知与免费（0）严格区分。</summary>
public class AiModelAccountingConfig
{
    public int ContextWindowTokens { get; set; }
    public int MaxOutputTokens { get; set; }
    public long? TokenQuota { get; set; }
    public decimal? BudgetUsd { get; set; }
    public long UsedTokens { get; set; }
    public decimal SpentUsd { get; set; }
    public long UsedRequests { get; set; }
    /// <summary>本地累计是否包含未由服务商返回的估算值。</summary>
    public bool UsageIncludesEstimates { get; set; }
    public decimal? InputPricePerMillion { get; set; }
    public decimal? CachedInputPricePerMillion { get; set; }
    public decimal? OutputPricePerMillion { get; set; }
    public AiModelAccountingConfig Clone() => (AiModelAccountingConfig)MemberwiseClone();
}

public class AiRequestUsage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Model { get; set; } = "";
    public string ModelKey { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    /// <summary>明确的 API 拒绝且没有 usage 或生成内容时不扣本地配额。</summary>
    public bool WasRejected { get; set; }
    public bool QuotaRecorded { get; set; }
    public string Purpose { get; set; } = "对话";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public bool IsEstimated { get; set; } = true;
    public bool HasReportedInput { get; set; }
    public bool HasReportedOutput { get; set; }
    public bool HasReportedTotal { get; set; }
    public bool InProgress { get; set; } = true;
    public bool IsIncomplete { get; set; }
    public bool AffectsContext { get; set; } = true;
    public int ContextWindowTokens { get; set; }
    public int ReservedOutputTokens { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public AiRequestUsage Clone() => (AiRequestUsage)MemberwiseClone();
}
