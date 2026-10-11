using System;
using System.Linq;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>OpenAI-compatible usage snapshots. Missing provider fields never become a reported zero.</summary>
internal static class AiUsageAccounting
{
    internal sealed record Usage(long? Input, long? Output, long? Total, long? Cached);

    internal static Usage? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object) return null;
        var input = Read(usage, "prompt_tokens") ?? Read(usage, "input_tokens");
        var output = Read(usage, "completion_tokens") ?? Read(usage, "output_tokens");
        var total = Read(usage, "total_tokens");
        long? cached = Read(usage, "prompt_cache_hit_tokens");
        foreach (var key in new[] { "prompt_tokens_details", "input_tokens_details" })
            if (usage.TryGetProperty(key, out var details) && details.ValueKind == JsonValueKind.Object)
                cached ??= Read(details, "cached_tokens");
        if (input == null && output == null && total == null && cached == null) return null;
        if (cached != null && input != null) cached = Math.Min(cached.Value, input.Value);
        return new(input, output, total, cached);
    }

    private static long? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number) && number >= 0 && number <= 1_000_000_000_000 ? number : null;

    internal static void Apply(AiRequestUsage target, Usage source)
    {
        if (source.Input.HasValue) { target.InputTokens = source.Input.Value; target.HasReportedInput = true; }
        if (source.Output.HasValue) { target.OutputTokens = source.Output.Value; target.HasReportedOutput = true; }
        if (source.Cached.HasValue) target.CachedInputTokens = source.Cached.Value;
        // A provider cache snapshot may precede input usage. Never clip it to a local estimate.
        // Once an actual input count arrives (including corrections), enforce the real upper bound.
        if (target.HasReportedInput && target.CachedInputTokens.HasValue)
            target.CachedInputTokens = Math.Min(target.CachedInputTokens.Value, target.InputTokens);
        if (source.Total.HasValue) { target.TotalTokens = source.Total.Value; target.HasReportedTotal = true; }
        else if (!target.HasReportedTotal) target.TotalTokens = target.InputTokens + target.OutputTokens;
        // Never imply exact counts if the response reports only part of the usage object.
        target.IsEstimated = !(target.HasReportedInput && target.HasReportedOutput);
    }

    /// <summary>Only call immediately after disk deserialization; never pass a retained live session.</summary>
    internal static AiChatSession? NormalizeRestoredSession(AiChatSession? restored)
    {
        if (restored == null) return null;
        restored.UsageRequests ??= new();
        foreach (var request in restored.UsageRequests)
        {
            if (!request.InProgress) continue;
            request.InProgress = false;
            request.IsIncomplete = true;
            request.ReservedOutputTokens = 0;
        }
        return restored;
    }

    internal static AiModelAccountingConfig Settings(AiAssistantConfig config, string model)
    {
        var definition = AiModelIds.FindInCatalog(config, model);
        var key = definition?.ModelKey ?? model;
        var exact = config.ModelAccounting?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        if (exact != null) return exact;
        if (!AiModelIds.TrySplitModelKey(key, out _, out var rawId))
            return config.ModelAccounting?.FirstOrDefault(p => string.Equals(p.Key, model, StringComparison.OrdinalIgnoreCase)).Value ?? new();
        var legacyOwner = AiModelIds.FindInCatalog(config, rawId);
        // 同名第二接口绝不继承第一接口的累计、价格或配额。
        if (legacyOwner != null && string.Equals(legacyOwner.ModelKey, key, StringComparison.OrdinalIgnoreCase))
            return config.ModelAccounting?.FirstOrDefault(p => string.Equals(p.Key, rawId, StringComparison.OrdinalIgnoreCase)).Value?.Clone() ?? new();
        return new();
    }

    /// <summary>本地软配额按预计输入 + 预留输出检查；服务商实际计量仍为最终依据。</summary>
    internal static string? CheckLimits(AiAssistantConfig config, string modelKey, int inputEstimate, int reservedOutput)
    {
        var settings = Settings(config, modelKey);
        var input = Math.Max(0, inputEstimate);
        var output = Math.Max(0, reservedOutput);
        var total = (long)input + output;
        if (settings.ContextWindowTokens > 0 && total > settings.ContextWindowTokens)
            return "该模型的上下文长度不足，请压缩对话或调整该模型的上下文设置。";
        if (settings.MaxOutputTokens > 0 && output > settings.MaxOutputTokens)
            return "本次预留输出超过该模型单独设置的输出长度。";
        if (settings.TokenQuota.HasValue && settings.TokenQuota.Value >= 0
            && (settings.UsedTokens >= settings.TokenQuota.Value || total > settings.TokenQuota.Value - Math.Max(0, settings.UsedTokens)))
            return "该接口的此模型已达到本地 Token 配额，或剩余配额不足本次预计消耗。";
        if (settings.BudgetUsd.HasValue && settings.BudgetUsd.Value >= 0)
        {
            if (settings.SpentUsd >= settings.BudgetUsd.Value) return "该接口的此模型已达到本地美元预算。";
            var estimated = Cost(new AiRequestUsage { InputTokens = input, OutputTokens = output }, settings);
            if (!estimated.HasValue)
                return "该模型设置了美元预算，但输入或输出价格未知；请先填写价格后再请求。";
            if (estimated.Value > settings.BudgetUsd.Value - Math.Max(0, settings.SpentUsd))
                return "该接口的此模型剩余美元预算不足本次预计消耗。";
        }
        return null;
    }

    /// <summary>每次请求完成仅记一次；明确拒绝且没有用量的请求不扣配额。</summary>
    internal static void RecordUsage(AiAssistantConfig config, string modelKey, AiRequestUsage usage)
    {
        if (usage.QuotaRecorded || usage.InProgress) return;
        var hasReportedUsage = usage.HasReportedInput || usage.HasReportedOutput || usage.HasReportedTotal;
        if (usage.WasRejected && !hasReportedUsage && usage.OutputTokens <= 0) return;
        lock (config)
        {
            if (usage.QuotaRecorded) return;
            config.ModelAccounting ??= new();
            var key = AiModelIds.FindInCatalog(config, modelKey)?.ModelKey ?? modelKey;
            var settings = Settings(config, key).Clone();
            var total = Math.Max(0, usage.HasReportedTotal ? usage.TotalTokens : usage.InputTokens + usage.OutputTokens);
            settings.UsedTokens = total > long.MaxValue - Math.Max(0, settings.UsedTokens)
                ? long.MaxValue : Math.Max(0, settings.UsedTokens) + total;
            settings.UsedRequests = settings.UsedRequests == long.MaxValue ? long.MaxValue : Math.Max(0, settings.UsedRequests) + 1;
            // 使用发起请求时捕获价格计算的金额；设置中的价格可能已被用户改动。
            if (usage.EstimatedCostUsd is >= 0)
                settings.SpentUsd = usage.EstimatedCostUsd.Value > decimal.MaxValue - Math.Max(0, settings.SpentUsd)
                    ? decimal.MaxValue : Math.Max(0, settings.SpentUsd) + usage.EstimatedCostUsd.Value;
            settings.UsageIncludesEstimates |= usage.IsEstimated || !usage.EstimatedCostUsd.HasValue;
            config.ModelAccounting[key] = settings;
            usage.QuotaRecorded = true;
        }
    }

    internal static decimal? Cost(AiRequestUsage usage, AiModelAccountingConfig prices)
    {
        if (prices.InputPricePerMillion is not (>= 0 and <= 1_000_000m)
            || prices.OutputPricePerMillion is not (>= 0 and <= 1_000_000m)) return null;
        var cached = Math.Clamp(usage.CachedInputTokens ?? 0, 0, usage.InputTokens);
        var cacheRate = prices.CachedInputPricePerMillion ?? prices.InputPricePerMillion;
        if (cacheRate is not (>= 0 and <= 1_000_000m)) return null;
        // Without cache metadata or a discounted rate, use the ordinary rate as an explicit estimate.
        return ((usage.InputTokens - cached) * prices.InputPricePerMillion.Value
            + cached * cacheRate.Value + usage.OutputTokens * prices.OutputPricePerMillion.Value) / 1_000_000m;
    }
}
