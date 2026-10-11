using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>推理档位与兼容性降级。仅识别参数错误，鉴权、配额、模型下线等交给接口路由处理。</summary>
public static class AiReasoningPolicy
{
    /// <summary>旧配置的“深度思考”迁移为 High；显式 Off 必须覆盖旧版开关。</summary>
    public static AiReasoningLevel EffectiveLevel(AiAssistantConfig config) =>
        Normalize(config.ReasoningLevel ?? (config.EnableDeepThinking ? AiReasoningLevel.High : AiReasoningLevel.Off));

    /// <summary>极速使用 minimal 推理预算，仅对用户明确标记支持的模型发送；不请求额外付费服务等级。</summary>
    public static AiReasoningLevel ResolveLevel(AiReasoningLevel requested, bool supportsFastMode)
    {
        var level = Normalize(requested);
        return level == AiReasoningLevel.Fast && !supportsFastMode ? AiReasoningLevel.Off : level;
    }

    /// <summary>调用方先 ResolveLevel，再把结果映射为 reasoning_effort；Off 完全省略推理参数。</summary>
    public static string? GetReasoningEffort(AiReasoningLevel level) => level switch
    {
        AiReasoningLevel.Low => "low",
        AiReasoningLevel.Medium => "medium",
        AiReasoningLevel.High => "high",
        AiReasoningLevel.VeryHigh => "xhigh",
        AiReasoningLevel.Max => "max",
        AiReasoningLevel.Fast => "minimal",
        _ => null
    };

    public static string? GetReasoningEffort(AiAssistantConfig config) => GetReasoningEffort(EffectiveLevel(config));

    /// <summary>
    /// 只有 HTTP 400 / 422 且明确拒绝推理参数才降级。优先读取结构化错误对象，避免把回显的
    /// 请求体中包含的 reasoning_effort 与另一个无关参数错误混为一谈。
    /// </summary>
    public static bool IsUnsupportedReasoningError(HttpStatusCode statusCode, string? body)
    {
        if (statusCode != HttpStatusCode.BadRequest && (int)statusCode != 422) return false;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return MatchesError(document.RootElement, 0);
        }
        catch (JsonException)
        {
            return NamesReasoning(body) && DescribesRejection(body);
        }
    }

    private static AiReasoningLevel Normalize(AiReasoningLevel level) =>
        Enum.IsDefined(typeof(AiReasoningLevel), level) ? level : AiReasoningLevel.Off;

    private static bool MatchesError(JsonElement error, int depth)
    {
        if (depth > 8) return false;
        if (error.ValueKind == JsonValueKind.String)
        {
            var text = error.GetString() ?? "";
            return NamesReasoning(text) && DescribesRejection(text);
        }
        if (error.ValueKind == JsonValueKind.Array)
            return error.EnumerateArray().Any(item => MatchesError(item, depth + 1));
        if (error.ValueKind != JsonValueKind.Object) return false;

        var parameter = ReadString(error, "param") ?? ReadString(error, "parameter") ?? ReadString(error, "field");
        if (parameter == null && error.TryGetProperty("loc", out var location)
            && location.ValueKind == JsonValueKind.Array)
            parameter = string.Join(".", location.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()));
        var message = ReadString(error, "message") ?? ReadString(error, "msg") ?? ReadString(error, "error_description") ?? "";
        var code = (ReadString(error, "code") ?? "") + " " + (ReadString(error, "type") ?? "");

        // A provider that explicitly identifies another failing parameter must not trigger this fallback.
        if ((parameter == null ? NamesReasoning(message) : IsReasoningParameter(parameter))
            && (DescribesRejection(message) || DescribesRejection(code))) return true;

        foreach (var child in new[] { "error", "errors", "detail", "details" })
            if (error.TryGetProperty(child, out var value) && MatchesError(value, depth + 1)) return true;
        return false;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static bool IsReasoningParameter(string parameter)
    {
        var value = parameter.Trim().ToLowerInvariant();
        return value is "reasoning" or "reasoning_effort" or "reasoning.effort" or "effort"
            || value.EndsWith(".reasoning_effort", StringComparison.Ordinal)
            || value.EndsWith(".reasoning.effort", StringComparison.Ordinal)
            || value.EndsWith(".reasoning", StringComparison.Ordinal);
    }

    private static bool NamesReasoning(string text)
    {
        var value = text.ToLowerInvariant();
        return new[]
        {
            "reasoning_effort", "reasoning.effort", "\"reasoning\"", "'reasoning'", "`reasoning`",
            "\"effort\"", "'effort'", "`effort`", "reasoning parameter", "reasoning is ",
            "reasoning not ", "reasoning unsupported", "reasoning effort", "推理参数", "推理等级"
        }.Any(value.Contains);
    }

    private static bool DescribesRejection(string text)
    {
        var value = text.ToLowerInvariant();
        return new[]
        {
            "unsupported", "not supported", "does not support", "doesn't support", "unknown",
            "unrecognized", "unrecognised", "invalid", "not allowed", "disallowed", "unexpected",
            "not permitted", "not accepted", "not available", "extra_forbidden", "literal_error",
            "must be", "must not", "should be", "allowed values", "不支持", "无效", "非法", "未识别", "不允许"
        }.Any(value.Contains);
    }
}
