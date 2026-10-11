using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>一次"从接口拉取模型列表"的结果。Success=false 时 Error 是可直接展示给用户的原因。</summary>
public sealed record AiModelFetchResult(bool Success, string Error, List<AiModelDefinition> Models, int SkippedNonChat)
{
    public int? StatusCode { get; init; }
}

/// <summary>
/// 通用的 OpenAI 兼容接口模型列表拉取：请求 {BaseUrl}/models，兼容常见的几种返回形态
/// （{"data":[...]} / {"models":[...]} / 直接一个数组；元素可以是对象或纯字符串）。
/// 与 OpenRouterModelSyncService 的区别：那个只在 Base URL 是 openrouter.ai 且点"保存"时自动触发；
/// 这个由设置页里的"自动拉取全部模型"按钮主动调用，适用于任何供应商，包括单个模型自己填的独立供应商。
/// </summary>
public static class AiModelListFetcher
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher");
        return client;
    }

    // 明显不是对话模型的名字特征（向量/重排/语音/绘图/审核），拉取时默认跳过——这些模型走不了 /chat/completions。
    private static readonly string[] NonChatMarkers =
    {
        "embed", "rerank", "moderation", "whisper", "tts", "dall-e", "dalle", "stable-diffusion", "sdxl",
        "midjourney", "imagen", "transcribe", "text-to-speech", "speech-to-text", "content-safety"
    };

    // 名字里带这些词的更像轻量/快速模型 → 默认归"普通"。
    private static readonly string[] LightMarkers =
    {
        "mini", "nano", "flash", "lite", "haiku", "small", "tiny", "instant", "turbo"
    };

    // 个位数参数量（1b～9b）的小模型也归普通；前面不能是数字/小数点，避免把 32b、72b 误当成 2b。
    private static readonly Regex SmallSizePattern = new(
        @"(^|[^0-9.])[1-9]b([^a-z0-9]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExpertPattern = new(
        @"(opus|sonnet|ultra|reason|think|(^|[^a-z0-9])(pro|max|large|o1|o3|o4|r1)([^a-z0-9]|$)|(^|[^0-9])(32|70|72|120|235|405|480|550|671)b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>按模型名粗略猜一个分组：带"mini/flash/lite…"归普通，带"pro/ultra/reasoner/大参数量…"归专家，
    /// 其余归普通。这只是个初始猜测，设置页里可以随时手动把任意模型改成普通/专家。</summary>
    public static AiModelTier GuessTier(string modelId)
    {
        var id = modelId ?? "";
        if (LightMarkers.Any(m => id.Contains(m, StringComparison.OrdinalIgnoreCase)) || SmallSizePattern.IsMatch(id))
            return AiModelTier.Normal;
        return ExpertPattern.IsMatch(id) ? AiModelTier.Expert : AiModelTier.Normal;
    }

    /// <summary>由用户填的 Base URL 得到 /models 的完整地址：去掉末尾斜杠，粘贴了完整
    /// /chat/completions 地址时还原，已经以 /models 结尾就原样使用。</summary>
    public static string BuildModelsUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        const string chat = "/chat/completions";
        if (url.EndsWith(chat, StringComparison.OrdinalIgnoreCase))
            url = url[..^chat.Length].TrimEnd('/');
        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            return url;
        return url + "/models";
    }

    /// <summary>从指定接口拉取模型；每条结果保留接口身份，第二接口同名模型不会覆盖第一接口。</summary>
    public static async Task<AiModelFetchResult> FetchAsync(AiApiEndpoint endpoint,
        bool enableKeyFailover = true, CancellationToken ct = default)
    {
        if (!endpoint.Enabled) return new(false, "该接口已禁用，请先启用后再拉取。", new(), 0);
        var keys = AiEndpointRouting.CleanKeys(endpoint.ApiKeys);
        if (keys.Count == 0) keys = new[] { "" }; // 兼容本地免密接口。
        if (!enableKeyFailover) keys = keys.Take(1).ToArray();
        AiModelFetchResult result = new(false, "没有可用接口。", new(), 0);
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            result = await FetchAsync(endpoint.BaseUrl, key, ct).ConfigureAwait(false);
            if (result.Success)
            {
                foreach (var model in result.Models)
                {
                    model.ProviderId = endpoint.Id;
                    model.ProviderName = endpoint.Name;
                }
                return result;
            }
            if (result.StatusCode is not (401 or 402 or 403 or 429)) break;
        }
        return result;
    }

    public static async Task<AiModelFetchResult> FetchAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        var empty = new List<AiModelDefinition>();
        try
        {
            var url = BuildModelsUrl(baseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var safeBody = string.IsNullOrWhiteSpace(apiKey) ? body : body.Replace(apiKey.Trim(), "[已隐藏密钥]", StringComparison.Ordinal);
                var snippet = safeBody.Length > 200 ? safeBody[..200] + "…" : safeBody;
                var hint = (int)response.StatusCode switch
                {
                    401 or 403 => "API Key 无效或没有权限。",
                    404 => "该接口没有提供模型列表（/models）。请确认接口地址（通常以 /v1 结尾），或手动添加模型。",
                    429 => "请求太频繁或额度不足，请稍后再试。",
                    _ => ""
                };
                return new(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}。{hint}\n{snippet}".Trim(), empty, 0) { StatusCode = (int)response.StatusCode };
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                return new(false, "接口返回的内容不是 JSON，请检查接口地址是否正确（通常需要以 /v1 结尾，而不是网站首页）。", empty, 0);
            }

            using (doc)
            {
                var list = new List<AiModelDefinition>();
                int skipped = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in EnumerateItems(doc.RootElement))
                {
                    var (id, name, isChat) = ReadItem(item);
                    if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                    if (!isChat || NonChatMarkers.Any(m => id.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped++;
                        continue;
                    }
                    list.Add(new AiModelDefinition
                    {
                        Id = id,
                        DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
                        Tier = GuessTier(id)
                    });
                }

                if (list.Count == 0 && skipped == 0)
                    return new(false, "接口返回成功，但没有解析到任何模型。该供应商的模型列表格式可能不是 OpenAI 兼容格式，请手动添加。", empty, 0);

                return new(true, "", list, skipped);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "请求超时（30 秒）。请检查网络或接口地址是否可访问。", empty, 0);
        }
        catch (OperationCanceledException)
        {
            return new(false, "已取消。", empty, 0);
        }
        catch (Exception ex)
        {
            // 供应商可能在异常/响应中回显鉴权信息；不记录原始异常。
            var message = string.IsNullOrWhiteSpace(apiKey) ? ex.Message : ex.Message.Replace(apiKey.Trim(), "[已隐藏密钥]", StringComparison.Ordinal);
            return new(false, "拉取失败：" + message, empty, 0);
        }
    }

    private static IEnumerable<JsonElement> EnumerateItems(JsonElement root)
    {
        JsonElement array = default;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) array = data;
            else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array) array = models;
        }

        if (array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
            yield return item;
    }

    private static (string? id, string? name, bool isChat) ReadItem(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
            return (item.GetString()?.Trim(), null, true);
        if (item.ValueKind != JsonValueKind.Object)
            return (null, null, false);

        string? Str(string key) =>
            item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

        var id = Str("id") ?? Str("model") ?? Str("name");
        var name = Str("display_name") ?? Str("displayName") ?? Str("name");
        // 名字和 id 一样，或者是 Gemini 风格的 "models/xxx" 内部名，就不当作显示名。
        if (name != null && (string.Equals(name, id, StringComparison.OrdinalIgnoreCase) || name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)))
            name = null;

        // OpenRouter 等会给出输出模态；明确不包含 text 的（纯生图/生音频）不是对话模型。
        bool isChat = true;
        if (item.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object &&
            arch.TryGetProperty("output_modalities", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            isChat = outputs.EnumerateArray().Any(o =>
                o.ValueKind == JsonValueKind.String && string.Equals(o.GetString(), "text", StringComparison.OrdinalIgnoreCase));
        }

        return (id, name, isChat);
    }
}
