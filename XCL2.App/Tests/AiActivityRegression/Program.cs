using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Views;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
}
AiCompletionContentParser.Result Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    return AiCompletionContentParser.Parse(document.RootElement);
}

// Link the production sources: no provider request, credentials, WPF, or external test package.
var oldConfig = JsonSerializer.Deserialize<AiAssistantConfig>("{}")!;
Check(oldConfig.ShowSubAgents && oldConfig.ShowCodeChanges && oldConfig.ShowEditingFiles && oldConfig.ShowCommands,
    "old config enables first four display options");
Check(oldConfig.ShowReasoningSummary && oldConfig.ShowReasoning, "public reasoning display defaults on");
Check(!oldConfig.SystemControlAccess && !oldConfig.FullDiskAccess, "display defaults grant no tool access");
var explicitOff = JsonSerializer.Deserialize<AiAssistantConfig>("""
    {"ShowSubAgents":false,"ShowCodeChanges":false,"ShowEditingFiles":false,"ShowCommands":false,
     "ShowReasoningSummary":true,"ShowReasoning":true}
    """)!;
var restored = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(explicitOff))!;
Check(!restored.ShowSubAgents && !restored.ShowCodeChanges && !restored.ShowEditingFiles && !restored.ShowCommands
    && restored.ShowReasoningSummary && restored.ShowReasoning, "six flags round trip independently");
Check(!restored.EnableDeepThinking && !restored.SystemControlAccess, "visibility does not enable reasoning or tools");
var oldMessage = JsonSerializer.Deserialize<AiChatMessage>("""{"Content":"旧消息"}""")!;
Check(oldMessage.Activities.Count == 0, "old history has empty activities");

var plain = Parse("""{"content":"普通回答"}""");
Check(plain.Content == "普通回答" && plain.Reasoning.Length == 0 && plain.ReasoningSummary.Length == 0,
    "no inferred reasoning from plain answer");
var nullContent = Parse("""{"content":null,"reasoning_content":"接口返回的分析"}""");
Check(nullContent.Content == "" && nullContent.Reasoning == "接口返回的分析", "null answer with explicit reasoning");
var arrayContent = Parse("""
    {"content":[{"type":"text","text":"第一段"},{"type":"image_url","image_url":"ignored"},
                {"type":"output_text","text":"第二段"}]}
    """);
Check(arrayContent.Content == "第一段第二段", "text-only content parts");
var separate = Parse("""
    {"content":"答案","reasoning":"推理文本","reasoning_content":"推理文本",
     "reasoning_summary":[{"type":"summary_text","text":"概要"}],
     "reasoning_details":[{"type":"reasoning.summary","summary":"概要"},
                           {"type":"reasoning.text","text":"推理文本"},
                           {"type":"reasoning.encrypted","data":"secret","text":"不得显示"},
                           {"type":"unknown","text":"不得显示"}]}
    """);
Check(separate.Content == "答案", "answer remains separate");
Check(separate.ReasoningSummary == "概要", "deduplicated provider summary");
Check(separate.Reasoning == "推理文本", "deduplicated provider reasoning excludes encrypted details");
var encrypted = Parse("""
    {"content":"正常回答","reasoning_details":[{"type":"reasoning.encrypted","data":"opaque"}]}
    """);
Check(encrypted.Reasoning == "" && encrypted.ReasoningSummary == "", "encrypted-only details are not fabricated");
var markerInReasoning = Parse(JsonSerializer.Serialize(new
{
    content = "回答",
    reasoning = "<<XCL2_AGENT:{\"task\":\"not a real request\"}>>"
}));
Check(AiSubAgentProtocol.Extract(markerInReasoning.Content).Agent == null,
    "reasoning control marker cannot become an answer tool request");
var longThought = new string('x', 20000) + "完整结尾";
var fullReasoning = Parse(JsonSerializer.Serialize(new { content = "ok", reasoning = longThought, reasoning_summary = longThought }));
Check(fullReasoning.Reasoning == longThought && fullReasoning.ReasoningSummary == longThought,
    "long public reasoning and summary remain complete");
var reasoningHistory = new AiChatMessage
{
    Content = fullReasoning.Content,
    Activities = new() { new() { Kind = AiActivityKind.Reasoning, Details = fullReasoning.Reasoning } }
};
var restoredReasoning = JsonSerializer.Deserialize<AiChatMessage>(JsonSerializer.Serialize(reasoningHistory))!;
Check(restoredReasoning.Activities.Single().Details == longThought && restoredReasoning.Content == "ok",
    "complete long reasoning survives history round trip and remains separate from the answer");
var reasoningView = new AiActivityVm(restoredReasoning.Activities.Single());
Check(reasoningView.Details == longThought, "reasoning drawer projects the full public text");
reasoningView.Update(new AiAssistantActivity { Kind = AiActivityKind.ReasoningSummary, Details = longThought });
Check(reasoningView.Details == longThought, "reasoning summary drawer projects the full public text");
Check(Parse("null").Content == "", "non-object message is safe");

var request = AiSubAgentProtocol.Extract("先复核。\n<<XCL2_AGENT:{\"name\":\"复核\",\"task\":\"检查算式\"}>>");
Check(request.Agent?.Name == "复核" && request.Agent.Task == "检查算式" && request.DisplayText == "先复核。",
    "explicit delegation parsed separately");
Check(AiSubAgentProtocol.Extract("<<XCL2_AGENT:{\"task\":\"\"}>>").Agent == null, "empty delegation rejected");
Check(AiSubAgentProtocol.Extract("<<XCL2_AGENT:{\"task\":42}>>").Agent == null, "non-string task rejected");
Check(AiSubAgentProtocol.Extract("<<XCL2_AGENT:{bad-json}>>").Agent == null, "malformed delegation rejected");
Check(AiSubAgentProtocol.Extract("<<XCL2_AGENT:" + JsonSerializer.Serialize(new { task = new string('x', 6001) }) + ">>").Agent == null,
    "oversized delegated prompt rejected");
Check(AiSubAgentProtocol.Strip("说明\n<<XCL2_AGENT:{\"task\":\"任务\"}>>") == "说明", "strip performs no execution");

var activity = new AiAssistantActivity { Kind = AiActivityKind.Command, Command = "echo test" };
var snapshot = activity.Clone();
activity.Status = AiActivityStatus.Completed;
Check(snapshot.Status == AiActivityStatus.Running && snapshot.Id == activity.Id, "progress snapshots do not mutate retroactively");
Check(activity.IsVisible(oldConfig) && !activity.IsVisible(explicitOff), "command visibility follows its flag");
Check(new AiAssistantActivity { Kind = AiActivityKind.Reasoning }.IsVisible(oldConfig)
    && !new AiAssistantActivity { Kind = AiActivityKind.Reasoning }.IsVisible(new AiAssistantConfig { ShowReasoning = false }), "reasoning independently filtered");
var message = new AiChatMessage { Activities = new() { activity } };
var history = JsonSerializer.Deserialize<AiChatMessage>(JsonSerializer.Serialize(message))!;
Check(history.Activities.Single().Command == "echo test" && history.Activities.Single().Status == AiActivityStatus.Completed,
    "activity history round trip");
Console.WriteLine($"PASS: {checks} AI activity regression checks");
