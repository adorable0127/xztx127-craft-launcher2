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

    public static List<AiModelDefinition> GetCatalog(AiAssistantConfig config)
    {
        if (!config.UseCustomApiKey)
            return BuiltIn.Select(m => m.Clone()).ToList();

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
    public bool Enabled { get; set; } = false;
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

    /// <summary>"指定模型"模式下实际使用的模型 ID。默认 MiMo V2.5 Free（见 RoutingMode 注释：
    /// 实测目前只有这个和 Nemotron 3 Ultra Free 稳定可用）。</summary>
    public string SelectedModel { get; set; } = AiModelIds.MimoV25;

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
    public double PanelWidth { get; set; } = 380;
    public bool ClearHistoryOnGuestSessionEnd { get; set; } = true;
    public bool ShowFloatingButton { get; set; } = false;
    public bool PanelWasOpen { get; set; } = false;

    /// <summary>是否允许把用户主动提交的崩溃/日志内容发给 AI 分析。默认改为 true——这是用户
    /// 主动点"发给 AI 分析"才会触发的场景（不是后台自动收集），默认打开能让首次使用时
    /// 这个核心排障功能开箱即用，不用先跑一趟设置页才能用。</summary>
    public bool AllowCrashLogReading { get; set; } = true;

    /// <summary>最后一次打开的会话 ID，用于重启/重新进入 AI 页面后继续原对话。</summary>
    public string? LastSessionId { get; set; }
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
