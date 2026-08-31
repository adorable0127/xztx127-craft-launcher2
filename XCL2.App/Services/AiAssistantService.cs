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

    // Auto 路由复杂度判定。原来是"命中任一关键词就走专家档"的粗暴规则，问题是：
    // 1) "日志"、"分析"、"排查"这类词日常问法里也很常见（比如"日志在哪"），单独命中就跳专家档，
    //    结果简单问题被分去复杂模型；
    // 2) 反过来，真正复杂的问题（比如贴了一大段没提关键词的报错文本）命中不到词，又被分去简单模型。
    // 改成打分制：强信号（明显是堆栈/异常/贴日志）单独命中即可判专家档；普通关键词只算弱/中权重，
    // 需要和别的信号叠加到阈值才判专家档；再叠加"消息长度"和"疑似粘贴的多行日志"这两个结构信号，
    // 弥补关键词覆盖不到的情况。仍然是启发式，不是真正的语义分类，但比单关键词命中准得多。

    // 强信号：只要出现就足以直接判专家档（这些词/模式基本只出现在真正的错误场景里）。
    private static readonly string[] StrongComplexTriggers = new[]
    {
        "UnsupportedClassVersion", "OutOfMemory", "stacktrace", "堆栈", "NullReferenceException",
        "NoClassDefFoundError", "at java.", "at net.minecraft", "Exception in thread"
    };

    // 中权重：明确指向"出问题了"，但本身不代表内容复杂。
    private const int MediumWeight = 2;
    private static readonly string[] MediumComplexTriggers = new[]
    {
        "崩溃", "crash", "报错", "异常", "闪退", "白屏", "卡死", "无响应",
        "冲突", "依赖", "连不上", "联机失败", "下载失败", "安装失败", "打不开", "损坏"
    };

    // 弱权重：日常问法（"日志在哪"）和真正排障（贴一段日志分析）都会用到这些词，
    // 单独出现不足以判专家档，需要和别的信号叠加。
    private const int WeakWeight = 1;
    private static readonly string[] WeakComplexTriggers = new[]
    {
        "日志", "log", "分析", "排查"
    };

    private const int ExpertScoreThreshold = 3;

    /// <summary>给一段用户输入打"复杂度分"，>= ExpertScoreThreshold 时 Auto 模式判专家档。</summary>
    private static int EstimateComplexityScore(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return 0;

        if (StrongComplexTriggers.Any(k => userText.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return ExpertScoreThreshold;

        int score = 0;
        score += MediumComplexTriggers.Count(k => userText.Contains(k, StringComparison.OrdinalIgnoreCase)) * MediumWeight;
        score += WeakComplexTriggers.Count(k => userText.Contains(k, StringComparison.OrdinalIgnoreCase)) * WeakWeight;

        // 结构信号：疑似粘贴了一段日志/报错（多行、偏长），哪怕一个关键词都没命中也该判复杂。
        var lineCount = userText.Count(c => c == '\n') + 1;
        if (lineCount >= 4) score += 2;
        if (userText.Length >= 150) score += 1;

        return score;
    }

    /// <summary>不对用户显示的系统提示词。内容即 XCL2 助手的角色设定、功能知识库和能力边界。</summary>
    public string SystemPrompt { get; set; } = DefaultSystemPrompt.Text;

    /// <summary>
    /// UI 层（AiAssistantPanel）注入的确认框回调：AI 请求读取日志/查看或修改配置时，
    /// 弹出对应确认框并返回用户的选择。不赋值时视为"没有可以问用户的界面"，一律当作拒绝，
    /// 绝不允许在没有用户确认的情况下静默执行——这条不能因为回调没接好就被绕过。
    /// </summary>
    public AiFileAccessService.ConfirmCallback? ConfirmAccessRequest { get; set; }

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

        // 省 Token 模式：命中本地问答表就直接答，不请求 API。
        if (_config.EffectiveTokenSaverMode && !isCrashLogContext)
        {
            var quickAnswer = AiQuickAnswers.TryAnswer(userText);
            if (quickAnswer != null)
            {
                var quickMsg = new AiChatMessage
                {
                    Role = AiMessageRole.Assistant,
                    Content = quickAnswer,
                    ModelUsed = null,
                    ModelDisplayName = "本地直答 · 省 Token",
                    RouteUsed = AiRoutingMode.SpecificModel,
                    WasAutoRouted = false,
                    EstimatedTokens = 0
                };
                session.Messages.Add(quickMsg);
                if (session.Title == "新对话" && session.Messages.Count <= 3)
                    session.Title = userText.Length > 16 ? userText[..16] + "…" : userText;
                SaveSession(session);
                return quickMsg;
            }
        }

        // 会话总量估算，超阈值先压缩再发请求。省 Token 模式下阈值打对折、保留的原始消息更少，
        // 压缩更激进——见 CompressSessionAsync 里 keepRecent 的注释。
        var effectiveThreshold = _config.EffectiveTokenSaverMode
            ? Math.Max(2000, _config.CompressionTokenThreshold / 2)
            : _config.CompressionTokenThreshold;
        if (EstimateSessionTokens(session) > effectiveThreshold)
        {
            await CompressSessionAsync(session, ct);
        }

        var route = ChooseModel(userText, isCrashLogContext, forcedModel);
        var payloadMessages = BuildContextMessages(session);

        var rawReply = await CallChatCompletionAsync(baseUrl, apiKey, route.ModelId, payloadMessages, deepThinking, webSearch, ct);

        // D 需求：AI 可能在回复末尾附带一个"访问请求标记"（见 AiFileAccessService），
        // 需要弹确认框、按用户选择执行或拒绝，再把结果喂回去让模型给出最终回复。
        // ConfirmAccessRequest 由 UI 层（AiAssistantPanel）赋值；没赋值时（比如预热请求）
        // 默认一律拒绝，不能在没有界面可以问用户的情况下静默执行任何操作。
        var (displayText, followUp) = AiFileAccessService.ProcessReply(
            rawReply, ConfirmAccessRequest ?? ((_, _) => false));

        string replyText;
        if (followUp == null)
        {
            replyText = displayText;
        }
        else
        {
            var secondRoundMessages = payloadMessages.ToList();
            secondRoundMessages.Add(("assistant", displayText));
            secondRoundMessages.Add(("user", followUp));
            var secondReply = await CallChatCompletionAsync(baseUrl, apiKey, route.ModelId, secondRoundMessages,
                deepThinking, webSearch, ct);
            replyText = string.IsNullOrWhiteSpace(displayText) ? secondReply : $"{displayText}\n\n{secondReply}";
        }

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
    /// 设置里"打开 AI 助手时预热系统提示词"开关对应的实现：只发一次"系统提示词 + 极简用户
    /// 消息"的请求，刻意不落盘到任何会话文件、也不把回复内容返回给调用方展示——目的只是
    /// 提前把这次连接建立、鉴权、路由选择这些前置开销花掉，让用户真正开始聊天时的第一条
    /// 消息不用再等这些东西，观感上"回复变快了"。
    /// 任何失败（没配置 Key、网络不通、接口报错……）都静默吞掉：这是锦上添花的体验优化，
    /// 不能因为它失败就在用户还没开始聊天之前弹出一个莫名其妙的错误提示。</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            var (baseUrl, apiKey) = AiCredentialResolver.Resolve(_config);
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            var route = ChooseModel(userText: "", forceComplex: false, forcedModel: null);
            var messages = new List<(string role, string content)>
            {
                ("system", SystemPrompt),
                ("user", "(预热请求，无需回复正文，收到即可)")
            };
            await CallChatCompletionAsync(baseUrl, apiKey, route.ModelId, messages,
                deepThinking: false, webSearch: false, ct: ct);
        }
        catch
        {
            // 预热本来就是"能省则省"的优化，不应该以任何形式打断或影响正常聊天流程。
        }
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

        var expert = forceComplex || EstimateComplexityScore(userText) >= ExpertScoreThreshold;
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
    /// 上下文压缩：把 [0, Messages.Count-1) 里还没被摘要覆盖、且不含最近几条的部分
    /// 丢给模型总结成一段简短摘要，写入 SummaryOfOlderMessages，最近几条原样保留，
    /// 从而把下一次请求的上下文体积打下来。省 Token 模式下保留的原始消息更少（4 条而不是 6 条）、
    /// 摘要要求更短（100 字而不是 200 字），压得更狠一些。
    /// </summary>
    private async Task CompressSessionAsync(AiChatSession session, CancellationToken ct)
    {
        var tokenSaver = _config.EffectiveTokenSaverMode;
        var keepRecent = tokenSaver ? 4 : 6;
        var summaryWordLimit = tokenSaver ? 100 : 200;

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
            ("system", $"你是一个对话压缩器。把用户给的对话记录压缩成不超过 {summaryWordLimit} 字的中文摘要，" +
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
