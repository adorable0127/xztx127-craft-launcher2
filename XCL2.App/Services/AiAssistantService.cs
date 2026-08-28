using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// XCL2 内置 AI 助手服务：
/// - 走标准 OpenAI /chat/completions 格式，兼容 OpenCode Zen（或任何同格式的中转/自建服务）。
/// - 按问题复杂度在两档模型间自动路由：简单问题（问入口/用法）用 Nemotron 3.5 Lightning Free，
///   复杂问题（崩溃分析等）用 MiMo V2.5 Free；两者都是免费档，路由的目的是"该快的快、该准的准"，
///   不是省钱。
/// - "快速模式"：请求体里不开思考/推理参数（不传 reasoning/thinking 相关字段，
///   不要求模型输出思考过程），System Prompt 里也明确要求直接给结论。
/// - 聊天记录按 Session 落盘到 xcl2/ai_chat/*.json，纯本地，不上传除用户自己配置的 API 端点外的任何地方。
/// - 单个 Session 上下文估算超过 CompressionTokenThreshold（默认 18000）时自动压缩：
///   调用一次 API 把较早的消息总结成一段摘要，替换掉原始上下文，后续请求只带"摘要 + 最近消息"。
/// </summary>
public class AiAssistantService
{
    private readonly HttpClient _http;
    private readonly string _storageDir;
    private AiAssistantConfig _config;

    // 是否是"技术性/需要深入分析"的问题 —— 命中任一关键词就走 ComplexModel。
    // 简单启发式，不是 NLP 分类，但对这个场景（入口问答 vs 崩溃分析）区分度已经够用。
    private static readonly string[] ComplexTriggers = new[]
    {
        "崩溃", "crash", "报错", "异常", "闪退", "白屏", "卡死", "无响应",
        "日志", "log", "分析", "排查", "冲突", "依赖", "堆栈", "stacktrace",
        "内存不足", "OutOfMemory", "UnsupportedClassVersion", "连不上", "联机失败",
        "下载失败", "安装失败", "打不开", "损坏"
    };

    /// <summary>不对用户显示的系统提示词。内容即 XCL2 助手的角色设定、功能知识库和能力边界。</summary>
    public string SystemPrompt { get; set; } = DefaultSystemPrompt.Text;

    public AiAssistantService(AiAssistantConfig config, string xcl2DataDir)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _storageDir = Path.Combine(xcl2DataDir, "ai_chat");
        Directory.CreateDirectory(_storageDir);
    }

    public void UpdateConfig(AiAssistantConfig config) => _config = config;

    // ============================================================
    // 会话管理（本地存储）
    // ============================================================

    private string SessionPath(string sessionId) => Path.Combine(_storageDir, sessionId + ".json");

    public AiChatSession CreateNewSession(string title = "新对话", bool isGuestSession = false)
    {
        var session = new AiChatSession { Title = title, IsGuestSession = isGuestSession };
        session.Messages.Add(new AiChatMessage
        {
            Role = AiMessageRole.System,
            Content = SystemPrompt,
            EstimatedTokens = AiTokenEstimator.Estimate(SystemPrompt)
        });
        SaveSession(session);
        return session;
    }

    /// <summary>退出/删除访客账户时调用：按 ClearHistoryOnGuestSessionEnd 开关，删掉所有
    /// IsGuestSession=true 的本地会话文件，不影响正式账号下的历史对话。</summary>
    public void ClearGuestSessionsIfConfigured()
    {
        if (!_config.ClearHistoryOnGuestSessionEnd) return;
        foreach (var s in ListSessions().Where(s => s.IsGuestSession))
            DeleteSession(s.Id);
    }

    public void SaveSession(AiChatSession session)
    {
        session.UpdatedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SessionPath(session.Id), json, Encoding.UTF8);
    }

    public AiChatSession? LoadSession(string sessionId)
    {
        var path = SessionPath(sessionId);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<AiChatSession>(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>列出本地所有会话（按更新时间倒序），用于侧边栏"历史对话"列表。</summary>
    public List<AiChatSession> ListSessions()
    {
        var list = new List<AiChatSession>();
        if (!Directory.Exists(_storageDir)) return list;
        foreach (var file in Directory.GetFiles(_storageDir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<AiChatSession>(File.ReadAllText(file, Encoding.UTF8));
                if (s != null) list.Add(s);
            }
            catch { /* 忽略单个坏文件，不影响其它会话加载 */ }
        }
        return list.OrderByDescending(s => s.UpdatedUtc).ToList();
    }

    public void DeleteSession(string sessionId)
    {
        var path = SessionPath(sessionId);
        if (File.Exists(path)) File.Delete(path);
    }

    // ============================================================
    // 发送消息
    // ============================================================

    /// <summary>
    /// 发送一条用户消息并获取回复。
    /// isCrashLogContext: 调用方（日志页"授权分析崩溃文件"入口）可以显式传 true 强制走复杂档模型，
    /// 不依赖关键词命中。
    /// deepThinking: 是否启用深度思考（推理模式）
    /// webSearch: 是否启用联网搜索
    /// </summary>
    public async Task<AiChatMessage> SendMessageAsync(
        AiChatSession session,
        string userText,
        bool isCrashLogContext = false,
        string? forcedModel = null,
        bool deepThinking = false,
        bool webSearch = false,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = AiCredentialResolver.Resolve(_config);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("尚未配置 API Key（内置 Key 未设置，且未勾选使用自定义 Key）。");
        if (isCrashLogContext && !_config.AllowCrashLogReading)
            throw new InvalidOperationException("尚未在设置里打开“允许把日志/崩溃内容发给 AI 分析”开关。");

        var userMsg = new AiChatMessage
        {
            Role = AiMessageRole.User,
            Content = userText,
            EstimatedTokens = AiTokenEstimator.Estimate(userText)
        };
        session.Messages.Add(userMsg);

        // 会话总量估算，超阈值先压缩再发请求
        if (EstimateSessionTokens(session) > _config.CompressionTokenThreshold)
        {
            await CompressSessionAsync(session, ct);
        }

        var route = ChooseModel(userText, isCrashLogContext, forcedModel);
        var payloadMessages = BuildContextMessages(session);

        var replyText = await CallChatCompletionAsync(baseUrl, apiKey, route.ModelId, payloadMessages, deepThinking, webSearch, ct);

        var assistantMsg = new AiChatMessage
        {
            Role = AiMessageRole.Assistant,
            Content = replyText,
            ModelUsed = route.ModelId,
            ModelDisplayName = AiModelIds.GetDisplayName(_config, route.ModelId),
            RouteUsed = route.RouteUsed,
            WasAutoRouted = route.WasAutoRouted,
            EstimatedTokens = AiTokenEstimator.Estimate(replyText)
        };
        session.Messages.Add(assistantMsg);

        if (session.Title == "新对话" && session.Messages.Count <= 3)
            session.Title = userText.Length > 16 ? userText[..16] + "…" : userText;

        SaveSession(session);
        return assistantMsg;
    }

    /// <summary>
    /// 普通/专家是“路由档位”，不是模型名。Auto 根据问题类型选择普通档或专家档；
    /// Normal/Expert 固定走设置中对应的默认模型；SpecificModel 直接使用用户指定的实际模型。
    /// 自定义 API 下模型必须来自用户自己填写的模型表，不猜供应商模型 ID。
    /// </summary>
    private (string ModelId, AiRoutingMode RouteUsed, bool WasAutoRouted) ChooseModel(
        string userText, bool forceComplex, string? forcedModel)
    {
        if (!string.IsNullOrWhiteSpace(forcedModel))
            return (ResolveConfiguredModel(forcedModel, AiModelIds.Nemotron35Lightning), AiRoutingMode.SpecificModel, false);

        var mode = _config.RoutingMode;
        // 兼容旧配置：旧版只有 AutoModelRouting=false + SelectedModel。
        if (mode == AiRoutingMode.Auto && !_config.AutoModelRouting)
            mode = AiRoutingMode.SpecificModel;

        if (mode == AiRoutingMode.SpecificModel)
            return (ResolveConfiguredModel(_config.SelectedModel, AiModelIds.Nemotron35Lightning), AiRoutingMode.SpecificModel, false);
        if (mode == AiRoutingMode.Normal)
            return (ResolveConfiguredModel(_config.NormalModelId, AiModelIds.Nemotron35Lightning), AiRoutingMode.Normal, false);
        if (mode == AiRoutingMode.Expert)
            return (ResolveConfiguredModel(_config.ExpertModelId, AiModelIds.MimoV25), AiRoutingMode.Expert, false);

        var expert = forceComplex || ComplexTriggers.Any(k => userText.Contains(k, StringComparison.OrdinalIgnoreCase));
        return expert
            ? (ResolveConfiguredModel(_config.ExpertModelId, AiModelIds.MimoV25), AiRoutingMode.Expert, true)
            : (ResolveConfiguredModel(_config.NormalModelId, AiModelIds.Nemotron35Lightning), AiRoutingMode.Normal, true);
    }

    private string ResolveConfiguredModel(string? requestedModelId, string builtInFallback)
    {
        var catalog = AiModelIds.GetCatalog(_config);
        if (catalog.Count == 0)
        {
            if (_config.UseCustomApiKey)
                throw new InvalidOperationException("自定义 API 尚未配置任何模型。请到 AI 设置中添加模型 ID 和显示名称。");
            return builtInFallback;
        }

        if (!string.IsNullOrWhiteSpace(requestedModelId))
        {
            var exact = catalog.FirstOrDefault(m =>
                string.Equals(m.Id, requestedModelId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.Id;
        }

        if (!_config.UseCustomApiKey)
        {
            var fallback = catalog.FirstOrDefault(m =>
                string.Equals(m.Id, builtInFallback, StringComparison.OrdinalIgnoreCase));
            if (fallback != null) return fallback.Id;
        }

        return catalog[0].Id;
    }

    private int EstimateSessionTokens(AiChatSession session)
    {
        var start = session.SummarizedUpToIndex + 1;
        int total = session.SummaryOfOlderMessages != null
            ? AiTokenEstimator.Estimate(session.SummaryOfOlderMessages)
            : 0;
        for (int i = start; i < session.Messages.Count; i++)
            total += session.Messages[i].EstimatedTokens;
        return total;
    }

    /// <summary>拼装实际发给 API 的上下文：System Prompt + （可选）历史摘要 + 未被摘要覆盖的原始消息。</summary>
    private List<(string role, string content)> BuildContextMessages(AiChatSession session)
    {
        var result = new List<(string, string)> { ("system", SystemPrompt) };

        if (!string.IsNullOrEmpty(session.SummaryOfOlderMessages))
            result.Add(("system", "以下是本次对话更早内容的摘要，回答时可参考：\n" + session.SummaryOfOlderMessages));

        int start = session.SummarizedUpToIndex + 1;
        for (int i = start; i < session.Messages.Count; i++)
        {
            var m = session.Messages[i];
            if (m.Role == AiMessageRole.System) continue; // system prompt 已经单独加过一次
            result.Add((m.Role == AiMessageRole.User ? "user" : "assistant", m.Content));
        }
        return result;
    }

    /// <summary>
    /// 上下文压缩：把 [0, Messages.Count-1) 里还没被摘要覆盖、且不含最近 6 条的部分
    /// 丢给模型总结成一段简短摘要，写入 SummaryOfOlderMessages，最近 6 条原样保留，
    /// 从而把下一次请求的上下文体积打下来。
    /// </summary>
    private async Task CompressSessionAsync(AiChatSession session, CancellationToken ct)
    {
        const int keepRecent = 6;
        int start = session.SummarizedUpToIndex + 1;
        int end = Math.Max(start, session.Messages.Count - keepRecent); // 不含 end
        if (end <= start) return; // 没有足够旧消息可压

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(session.SummaryOfOlderMessages))
            sb.AppendLine("【此前摘要】\n" + session.SummaryOfOlderMessages);
        for (int i = start; i < end; i++)
        {
            var m = session.Messages[i];
            if (m.Role == AiMessageRole.System) continue;
            sb.AppendLine((m.Role == AiMessageRole.User ? "用户：" : "助手：") + m.Content);
        }

        var summarizeRequest = new List<(string role, string content)>
        {
            ("system", "你是一个对话压缩器。把用户给的对话记录压缩成不超过 200 字的中文摘要，" +
                       "只保留跟用户问题/结论相关的关键信息，不要用第一人称，不要评论，不要加任何前后缀说明。"),
            ("user", sb.ToString())
        };

        try
        {
            var (baseUrl, apiKey) = AiCredentialResolver.Resolve(_config);
            var summaryModel = ChooseModel("对话上下文摘要", forceComplex: false, forcedModel: null).ModelId;
            var summary = await CallChatCompletionAsync(baseUrl, apiKey, summaryModel, summarizeRequest, ct: ct);
            session.SummaryOfOlderMessages = summary.Trim();
            session.SummarizedUpToIndex = end - 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 压缩失败不阻塞正常对话，下次达到阈值再重试即可。
        }
    }

    // ============================================================
    // HTTP 调用（OpenAI 兼容 /chat/completions，非流式，快速模式不开思考）
    // ============================================================

private async Task<string> CallChatCompletionAsync(
        string baseUrl, string apiKey, string model, List<(string role, string content)> messages, bool deepThinking = false, bool webSearch = false, CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        var body = new ChatCompletionRequest
        {
            Model = model,
            Messages = messages.Select(m => new ChatMessageDto { Role = m.role, Content = m.content }).ToList(),
            Stream = false,
            ReasoningEffort = deepThinking ? "high" : null,
            Temperature = 0.6,
            Tools = webSearch ? new List<object> { new { type = "web_search" } } : null
        };

        // 仅对超时、429、5xx 这类瞬时错误重试。模型 ID 写错造成的 400 属于配置错误，
        // 以前会毫无意义地连续请求 3 次，看起来像“AI 卡住”，现在立即返回可读提示。
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
                    Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                var respText = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var detail = ExtractProviderError(respText);

                    if ((status == 400 || status == 404) && LooksLikeModelError(detail))
                    {
                        throw new AiClientRequestException(
                            $"模型 ID“{model}”被接口拒绝（HTTP {status}）。请打开 AI 设置检查该模型的“模型 ID（请求用）”；" +
                            $"显示名称不会发送给 API。{AppendProviderDetail(detail)}");
                    }

                    if (status == 400)
                    {
                        throw new AiClientRequestException(
                            $"AI 接口拒绝了请求（HTTP 400）。当前模型 ID：{model}。" +
                            $"如果你使用自定义 API，请确认模型 ID 与提供商文档完全一致，并检查该模型是否支持当前请求参数。" +
                            AppendProviderDetail(detail));
                    }
                    if (status == 401)
                        throw new AiClientRequestException("AI API Key 无效或已失效（HTTP 401）。请到 AI 设置重新填写 API Key。" + AppendProviderDetail(detail));
                    if (status == 403)
                        throw new AiClientRequestException("AI 接口拒绝访问（HTTP 403）。请检查 API Key 权限、模型权限或提供商策略。" + AppendProviderDetail(detail));
                    if (status == 404)
                        throw new AiClientRequestException($"AI 接口地址或模型不存在（HTTP 404）。当前请求地址：{url}；模型：{model}。" + AppendProviderDetail(detail));

                    // 408/429 和 5xx 可能是暂时性问题，可以重试；其它 4xx 都不应重复轰炸接口。
                    if (status >= 400 && status < 500 && status != 408 && status != 429)
                        throw new AiClientRequestException($"AI 接口调用失败（HTTP {status}）。" + AppendProviderDetail(detail));

                    throw new InvalidOperationException($"AI 接口暂时不可用（HTTP {status}）。" + AppendProviderDetail(detail));
                }

                using var doc = JsonDocument.Parse(respText);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
                return content ?? "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AiClientRequestException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct); // 2s, 4s
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new InvalidOperationException($"AI 调用失败（已重试 3 次）：{lastError?.Message}");
    }

    private static bool LooksLikeModelError(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var markers = new[]
        {
            "model", "invalid_model", "model_not_found", "unknown model", "unsupported model",
            "does not exist", "not found", "模型", "不存在的模型", "不支持的模型"
        };
        return markers.Any(m => detail.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractProviderError(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return LimitErrorText(error.GetString());
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                        return LimitErrorText(msg.GetString());
                    if (error.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        return LimitErrorText(detail.GetString());
                }
            }
            if (root.TryGetProperty("message", out var topMessage) && topMessage.ValueKind == JsonValueKind.String)
                return LimitErrorText(topMessage.GetString());
            if (root.TryGetProperty("detail", out var topDetail) && topDetail.ValueKind == JsonValueKind.String)
                return LimitErrorText(topDetail.GetString());
        }
        catch
        {
            // 非 JSON 错误页直接按纯文本截断。
        }
        return LimitErrorText(responseText);
    }

    private static string LimitErrorText(string? text)
    {
        var cleaned = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= 600 ? cleaned : cleaned[..600] + "…";
    }

    private static string AppendProviderDetail(string detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : $" 服务端提示：{detail}";

    private sealed class AiClientRequestException : InvalidOperationException
    {
        public AiClientRequestException(string message) : base(message) { }
    }

    private class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<ChatMessageDto> Messages { get; set; } = new();
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; set; }
        [JsonPropertyName("tools")] public List<object>? Tools { get; set; }
    }

    private class ChatMessageDto
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }
}

/// <summary>
/// 粗略 token 估算：不依赖任何 tokenizer 库（题目要求"不用依赖"）。
/// 中文按 1 字≈1.7 token、英文/数字按 1 词≈1.3 token 的经验系数估算，足够用于压缩阈值判断，
/// 不追求跟真实计费 token 数完全一致。
/// </summary>
public static class AiTokenEstimator
{
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int cjk = 0, other = 0;
        bool inWord = false;
        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) { cjk++; inWord = false; }
            else if (char.IsWhiteSpace(ch)) { inWord = false; }
            else if (!inWord) { other++; inWord = true; }
        }
        return (int)Math.Ceiling(cjk * 1.7 + other * 1.3);
    }
}
