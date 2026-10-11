using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
}
AiUsageAccounting.Usage? Parse(string json)
{
    using var doc = JsonDocument.Parse(json);
    return AiUsageAccounting.Parse(doc.RootElement);
}
var snapshot = Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":100,"total_tokens":1100,"prompt_tokens_details":{"cached_tokens":750}}}""")!;
Check(snapshot.Input == 1000 && snapshot.Output == 100 && snapshot.Cached == 750, "OpenAI usage and cached tokens");
Check(Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":100}}""")!.Cached == null, "missing cache remains unknown");
Check(Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":100,"prompt_tokens_details":{"cached_tokens":0}}}""")!.Cached == 0, "reported zero cache preserved");
Check(Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":100,"prompt_cache_hit_tokens":400}}""")!.Cached == 400, "alternate cache metadata");
Check(Parse("""{"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":200}}}""")!.Cached == 100, "cache bounded by input");
Check(Parse("""{"usage":{"prompt_tokens":-1,"completion_tokens":"100","total_tokens":9223372036854775807}}""") == null, "invalid and extreme usage rejected");
Check(Parse("""{"choices":[],"usage":null}""") == null, "null usage ignored");
var request = new AiRequestUsage { InputTokens = 1500, OutputTokens = 80, TotalTokens = 1580 };
AiUsageAccounting.Apply(request, snapshot);
Check(!request.IsEstimated && request.TotalTokens == 1100 && request.InputTokens == 1000, "reported usage replaces estimates");
AiUsageAccounting.Apply(request, snapshot);
Check(request.TotalTokens == 1100, "repeated cumulative frame is not double counted");
var partial = new AiRequestUsage { InputTokens = 150, OutputTokens = 10, TotalTokens = 160 };
AiUsageAccounting.Apply(partial, new(100, null, null, null));
AiUsageAccounting.Apply(partial, new(null, 12, 112, 40));
Check(!partial.IsEstimated && partial.TotalTokens == 112 && partial.OutputTokens == 12, "split provider usage retains field provenance");
var delayedInput = new AiRequestUsage { InputTokens = 500, TotalTokens = 500 };
AiUsageAccounting.Apply(delayedInput, new(null, null, null, 1000));
Check(delayedInput.CachedInputTokens == 1000 && !delayedInput.HasReportedInput, "cache-only frame is not clipped to estimated input");
AiUsageAccounting.Apply(delayedInput, new(2000, null, null, null));
Check(delayedInput.CachedInputTokens == 1000 && delayedInput.HasReportedInput, "later actual input preserves earlier cache snapshot");
AiUsageAccounting.Apply(delayedInput, new(800, null, null, null));
Check(delayedInput.CachedInputTokens == 800, "smaller actual input correction bounds cache snapshot");
var rates = new AiModelAccountingConfig { InputPricePerMillion = 2m, CachedInputPricePerMillion = 0.5m, OutputPricePerMillion = 8m };
Check(AiUsageAccounting.Cost(request, rates) == 0.001675m, "cached and output price arithmetic");
Check(AiUsageAccounting.Cost(request, new()) == null, "unknown price is not free");
Check(AiUsageAccounting.Cost(request, new() { InputPricePerMillion = 0, OutputPricePerMillion = 0 }) == 0, "explicit zero price is free");
Check(AiUsageAccounting.Cost(request, new() { InputPricePerMillion = decimal.MaxValue, OutputPricePerMillion = 0 }) == null, "untrusted imported prices cannot overflow");
var config = new AiAssistantConfig { RequestTimeoutSeconds = 86400, LongWaitHintSeconds = 3600, LongWaitDescription = "继续等待", ModelAccounting = new() { ["test-model"] = rates } };
var restored = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(config))!;
Check(restored.RequestTimeoutSeconds == 86400 && restored.LongWaitDescription == "继续等待" && AiUsageAccounting.Settings(restored, "TEST-MODEL").OutputPricePerMillion == 8m, "accounting and long wait settings round trip");
var session = new AiChatSession { UsageRequests = new() { request } };
Check(JsonSerializer.Deserialize<AiChatSession>(JsonSerializer.Serialize(session))!.UsageRequests.Single().TotalTokens == 1100, "session usage survives reopening");
Check(JsonSerializer.Deserialize<AiChatSession>("{}")!.UsageRequests.Count == 0, "old sessions remain compatible");
var liveSession = new AiChatSession { UsageRequests = new() { new() { InProgress = true, ReservedOutputTokens = 4000, InputTokens = 1000 } } };
var reopened = AiUsageAccounting.NormalizeRestoredSession(JsonSerializer.Deserialize<AiChatSession>(JsonSerializer.Serialize(liveSession)))!;
Check(!reopened.UsageRequests[0].InProgress && reopened.UsageRequests[0].IsIncomplete && reopened.UsageRequests[0].ReservedOutputTokens == 0,
    "reopened interrupted snapshots do not reserve output indefinitely");
Check(liveSession.UsageRequests[0].InProgress && liveSession.UsageRequests[0].ReservedOutputTokens == 4000, "restored normalization does not mutate retained live session");
var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n"
    + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
    + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":100,\"total_tokens\":1100,\"prompt_tokens_details\":{\"cached_tokens\":750}}}\n\n"
    + "data: [DONE]\n\n";
using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
AiUsageAccounting.Usage? streamedUsage = null;
var answer = await AiStreamingCompletion.ReadAsync(stream, null, null, CancellationToken.None, usage => streamedUsage = usage);
Check(answer == "Hi", "stream answer retained");
Check(streamedUsage?.Cached == 750 && streamedUsage?.Total == 1100, "usage after finish_reason is consumed before DONE");
Console.WriteLine($"PASS: {checks} AI usage regression checks");
