using System;
using System.Collections.Generic;
using System.Linq;

namespace XCL2.App.Models;

/// <summary>AI 模型条目。Id 是实际发给 OpenAI 兼容接口的 model；DisplayName 只用于界面展示。</summary>
public class AiModelDefinition
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;

    public AiModelDefinition Clone() => new() { Id = Id, DisplayName = DisplayName };
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

/// <summary>内置 OpenCode Zen 接口目前提供的预设模型。</summary>
public static class AiModelIds
{
    public const string Hy3 = "hy3-free";
    public const string MimoV25 = "mimo-v2.5-free";
    public const string MuseSpark12 = "muse-spark-1.2-free";
    public const string Nemotron3Ultra = "nemotron-3-ultra-free";
    public const string Nemotron35Lightning = "nemotron-3.5-lightning-free";

    public static readonly IReadOnlyList<AiModelDefinition> BuiltIn = new List<AiModelDefinition>
    {
        new() { Id = Hy3, DisplayName = "Hy3 Free" },
        new() { Id = MimoV25, DisplayName = "MiMo V2.5 Free" },
        new() { Id = MuseSpark12, DisplayName = "Muse Spark 1.2 Free" },
        new() { Id = Nemotron3Ultra, DisplayName = "Nemotron 3 Ultra Free" },
        new() { Id = Nemotron35Lightning, DisplayName = "Nemotron 3.5 Lightning Free" }
    };

    // 保留旧代码兼容入口。
    public static readonly string[] Available = BuiltIn.Select(m => m.Id).ToArray();

    /// <summary>当前默认 API key 下实际可用的模型。原因：供应商侧的 API 问题，Hy3 Free 和
    /// Muse Spark 1.2 Free 暂时无法访问，只有这 3 个稳定可用。这个限制只影响"未使用自定义
    /// API key"的场景；自定义 API key 的模型表不受影响。恢复可用后，把对应模型 ID 加回本列表即可。</summary>
    public static readonly IReadOnlyList<string> DefaultAvailable = new[]
    {
        MimoV25, Nemotron3Ultra, Nemotron35Lightning
    };

    public static bool IsAvailableByDefault(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) &&
        DefaultAvailable.Any(id => string.Equals(id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>因供应商 API 问题暂不可用的模型被选中时，展示给用户的提示原话（注意措辞，勿改）。</summary>
    public const string UnavailableModelMessage =
        "当前因模型提供商的 API 问题，这些模型暂时无法访问，请谅解，如果可以使用会第一时间通知您";

    public static List<AiModelDefinition> GetCatalog(AiAssistantConfig config)
    {
        if (!config.UseCustomApiKey)
            return BuiltIn.Where(m => IsAvailableByDefault(m.Id)).Select(m => m.Clone()).ToList();

        return (config.CustomModels ?? new List<AiModelDefinition>())
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var item = g.First();
                var id = item.Id.Trim();
                return new AiModelDefinition
                {
                    Id = id,
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? id : item.DisplayName.Trim()
                };
            })
            .ToList();
    }

    public static string GetDisplayName(AiAssistantConfig config, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return "未知模型";
        var match = GetCatalog(config).FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.DisplayName;

        var builtIn = BuiltIn.FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        return builtIn?.DisplayName ?? modelId;
    }
}

/// <summary>AI 助手全局配置，持久化在 AppConfig.AiAssistant。</summary>
public class AiAssistantConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://opencode.ai/zen/v1";
    public string ApiKey { get; set; } = "";
    public bool UseCustomApiKey { get; set; } = false;

    /// <summary>默认路由模式：自动 / 普通 / 专家 / 指定模型。默认改成 SpecificModel（"指定模型·
    /// 始终使用手动模型"）——实测目前接口下只有 MiMo V2.5 Free 和 Nemotron 3 Ultra Free
    /// 这两个模型稳定可用，其余预设模型经常请求失败，与其让新用户第一次用就撞上路由到
    /// 不可用模型报错，不如默认直接锁定手动模式，普通/专家档和"指定模型"默认值都固定指向
    /// 这两个目前验证可用的模型（见下面三个字段）。</summary>
    public AiRoutingMode RoutingMode { get; set; } = AiRoutingMode.SpecificModel;

    /// <summary>
    /// 旧版兼容字段。新代码以 RoutingMode 为准；保存时同步成 RoutingMode==Auto。
    /// 老配置里 AutoModelRouting=false 会在 ConfigService.Load 时迁移为 SpecificModel。
    /// </summary>
    public bool AutoModelRouting { get; set; } = true;

    /// <summary>"指定模型"模式下实际使用的模型 ID。默认 Nemotron 3 Ultra Free（见 RoutingMode 注释：
    /// 实测目前只有这个和 MiMo V2.5 Free 稳定可用）。</summary>
    public string SelectedModel { get; set; } = AiModelIds.Nemotron3Ultra;

    /// <summary>普通档默认模型：功能入口、设置位置、常规使用说明等。默认 MiMo V2.5 Free。</summary>
    public string NormalModelId { get; set; } = AiModelIds.MimoV25;

    /// <summary>专家档默认模型：日志、崩溃、注入、堆栈、复杂故障排查等。默认 Nemotron 3 Ultra Free。</summary>
    public string ExpertModelId { get; set; } = AiModelIds.Nemotron3Ultra;

    /// <summary>
    /// 自定义 API 的模型表。使用自定义 API 时不会猜供应商有哪些模型，必须由用户自己填写
    /// Model ID + 显示名称，面板/普通档/专家档的模型选择都只从这里取。
    /// </summary>
    public List<AiModelDefinition> CustomModels { get; set; } = new();

    public bool EnableDeepThinking { get; set; } = false;
    public bool EnableWebSearch { get; set; } = false;
    public int CompressionTokenThreshold { get; set; } = 18000;

    /// <summary>"省 Token 模式"。只在"用自己的 API key"或"自动路由"下能打开——自定义 key 是因为
    /// 用户自己承担调用成本，省 token 直接省钱；自动路由是因为它本来就会按问题复杂度换模型，
    /// 跟"少花 token"这个目标同一个方向，开着也不冲突。手动指定模型/普通/专家档这几个固定档位
    /// 模式跟"省不省 token"没什么关系，不提供这个开关（设置页会把勾选框禁用掉）。
    /// 效果两块：1) 上下文压缩更激进（阈值更低、摘要更短、保留的原始消息更少）；
    /// 2) 遇到 AiQuickAnswers 里能直接答的极简单问题（"MC 官网是什么"这类）不请求 API，
    /// 本地直接回答。</summary>
    public bool TokenSaverMode { get; set; } = false;

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
}

public class AiChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public bool IsGuestSession { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<AiChatMessage> Messages { get; set; } = new();
    public string? SummaryOfOlderMessages { get; set; }
    public int SummarizedUpToIndex { get; set; } = -1;
}
