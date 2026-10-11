using System.Net;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
}

var legacy = JsonSerializer.Deserialize<AiAssistantConfig>("{\"EnableDeepThinking\":true}")!;
Check(AiReasoningPolicy.EffectiveLevel(legacy) == AiReasoningLevel.High, "legacy deep thinking migrates to high");
legacy.ReasoningLevel = AiReasoningLevel.Off;
Check(AiReasoningPolicy.EffectiveLevel(legacy) == AiReasoningLevel.Off, "explicit off overrides legacy toggle");
Check(AiReasoningPolicy.EffectiveLevel(new()) == AiReasoningLevel.Off, "default does not request reasoning");
foreach (var (level, effort) in new[]
{
    (AiReasoningLevel.Low, "low"), (AiReasoningLevel.Medium, "medium"),
    (AiReasoningLevel.High, "high"), (AiReasoningLevel.VeryHigh, "xhigh"), (AiReasoningLevel.Max, "max")
})
    Check(AiReasoningPolicy.GetReasoningEffort(level) == effort, "effort mapping: " + level);
Check(AiReasoningPolicy.GetReasoningEffort(AiReasoningLevel.Off) == null, "off omits reasoning field");
Check(AiReasoningPolicy.ResolveLevel(AiReasoningLevel.Fast, false) == AiReasoningLevel.Off, "unconfirmed fast capability is blocked");
Check(AiReasoningPolicy.GetReasoningEffort(AiReasoningPolicy.ResolveLevel(AiReasoningLevel.Fast, true)) == "minimal", "confirmed fast uses minimal effort");
Check(AiReasoningPolicy.ResolveLevel((AiReasoningLevel)999, true) == AiReasoningLevel.Off, "unknown imported level safely turns off");

var badRequest = HttpStatusCode.BadRequest;
const string unsupported = """{"error":{"message":"Unsupported parameter: reasoning_effort","param":"reasoning_effort","code":"unsupported_parameter"}}""";
Check(AiReasoningPolicy.IsUnsupportedReasoningError(badRequest, unsupported), "structured unsupported reasoning parameter");
Check(AiReasoningPolicy.IsUnsupportedReasoningError(badRequest,
    """{"error":{"message":"Invalid value 'max': reasoning_effort must be one of low, medium, high","param":"reasoning_effort"}}"""), "unsupported effort value falls back");
Check(AiReasoningPolicy.IsUnsupportedReasoningError((HttpStatusCode)422,
    """{"detail":[{"loc":["body","reasoning_effort"],"msg":"Extra inputs are not permitted","type":"extra_forbidden"}]}"""), "FastAPI field validation identifies reasoning");
Check(AiReasoningPolicy.IsUnsupportedReasoningError(badRequest, "This model does not support reasoning_effort."), "plain text provider error");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError(HttpStatusCode.Unauthorized, unsupported), "401 never masquerades as reasoning incompatibility");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError((HttpStatusCode)429, unsupported), "429 remains key/quota routing error");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError(HttpStatusCode.InternalServerError, unsupported), "500 remains provider failure");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError(badRequest,
    """{"error":{"message":"Invalid max_tokens","param":"max_tokens","code":"invalid_request_error"},"request":{"reasoning_effort":"max"}}"""), "echoed request does not contaminate unrelated error");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError(badRequest,
    """{"error":{"message":"Unsupported model reasoning-v1","param":"model","code":"invalid_request_error"}}"""), "model name mentioning reasoning is unrelated");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError(badRequest, """{"error":{"message":"Invalid request","code":"invalid_request_error"}}"""), "generic invalid request does not downgrade");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError(badRequest,
    """{"error":{"message":"service_tier priority is unsupported","param":"service_tier"}}"""), "paid priority service is not a reasoning mode");
Check(!AiReasoningPolicy.IsUnsupportedReasoningError(badRequest, null), "empty error does not downgrade");
Console.WriteLine($"PASS: {checks} AI reasoning policy regression checks");
