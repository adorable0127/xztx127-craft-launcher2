using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

int assertions = 0;
void Assert(bool ok, string text) { assertions++; if (!ok) throw new Exception(text); }
AiAssistantConfig Config(bool failover = false) => new()
{
    UseCustomApiKey = true, BaseUrl = "https://one.example/v1", ApiKey = "key-one", ApiKeys = new() { "key-two" },
    EnableApiKeyFailover = true, EnableEndpointFailover = failover, StreamResponses = false, MaxOutputTokens = 64,
    SelectedModel = "alpha", NormalModelId = "alpha", ExpertModelId = "alpha", RoutingMode = AiRoutingMode.SpecificModel,
    CustomModels = new()
    {
        new() { Id = "alpha", DisplayName = "Alpha", ProviderId = "custom", Tier = AiModelTier.Normal },
        new() { Id = "alpha", DisplayName = "Alpha Two", ProviderId = "second", Tier = AiModelTier.Normal }
    },
    ApiEndpoints = new() { new() { Id = "second", Name = "Second", BaseUrl = "https://two.example/v1", ApiKeys = new() { "key-second" } } }
};
AiAssistantService Service(AiAssistantConfig c, Handler handler) => new(c,
    Path.Combine(Path.GetTempPath(), "xcl-http-" + Guid.NewGuid().ToString("N")), new HttpClient(handler));
HttpResponseMessage Success(string text = "ok") => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } },
        usage = new { prompt_tokens = 8, completion_tokens = 3, total_tokens = 11 } }), Encoding.UTF8, "application/json")
};
HttpResponseMessage Error(int status, string message) => new((HttpStatusCode)status)
{ Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json") };
HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
{ Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };

// Quota exhaustion rotates only keys belonging to this endpoint and remembers the working key.
{
    var cfg = Config();
    var handler = new Handler((r, n, ct) => Task.FromResult(n == 1 ? Error(429, "insufficient_quota") : Success()));
    var service = Service(cfg, handler);
    var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "hello");
    await service.SendMessageAsync(session, "hello again");
    Assert(handler.Requests.Select(r => r.Key).SequenceEqual(new[] { "key-one", "key-two", "key-two" }), "key rotation/preference");
    Assert(handler.Requests.All(r => r.Host == "one.example" && r.Model == "alpha"), "credentials stay scoped; real model ID");
    Assert(session.UsageRequests.Count == 3 && session.UsageRequests[0].TotalTokens == 0, "each attempt tracked; rejection not billed");
    Assert(AiUsageAccounting.Settings(cfg, session.UsageRequests[1].ModelKey).UsedTokens == 22, "successful usage accumulated");
}
// Parameter mistakes must not rotate keys or endpoints, even when the message names a model.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Error(400, "Model alpha does not support temperature parameter")));
    var result = await Service(Config(true), handler).TestModelAsync("alpha");
    Assert(!result.Ok && handler.Requests.Count == 1, "ordinary 400 never retries");
}
// Cross-interface fallback is disabled by default, then uses same model on the second endpoint when enabled.
foreach (var enabled in new[] { false, true })
{
    var cfg = Config(enabled);
    var handler = new Handler((r,n,ct) => Task.FromResult(r.RequestUri!.Host == "one.example" ? Error(404,"model_not_found") : Success()));
    var result = await Service(cfg, handler).TestModelAsync("alpha");
    Assert(result.Ok == enabled && handler.Requests.Count == (enabled ? 2 : 1), "endpoint failover opt-in");
    if (enabled) Assert(handler.Requests[1].Key == "key-second" && handler.Requests[1].Model == "alpha", "fallback endpoint credentials");
}
// Unsupported reasoning downgrades once using the same key; cache applies to future requests.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(n == 1 ? Error(400,"reasoning_effort is unsupported") : Success()));
    var service = Service(Config(), handler);
    var session = service.CreateNewSession();
    await service.SendMessageAsync(session,"hello",reasoningLevel:AiReasoningLevel.VeryHigh);
    await service.SendMessageAsync(session,"more",reasoningLevel:AiReasoningLevel.Max);
    Assert(handler.Requests.Select(r=>r.Reasoning).SequenceEqual(new string?[] { "xhigh", null, null }), "reasoning off fallback and capability cache");
    Assert(handler.Requests.All(r=>r.Key=="key-one"),"reasoning fallback stays on same key");
    Assert(ToastService.Warnings.Any(w=>w.Contains("off")),"reasoning toast");
}
// Authentication errors must rotate keys without erasing the requested reasoning effort.
{
    var handler = new Handler((r,n,ct)=>Task.FromResult(n==1?Error(401,"Unauthorized"):Success()));
    var service=Service(Config(),handler);
    await service.SendMessageAsync(service.CreateNewSession(),"hi",reasoningLevel:AiReasoningLevel.High);
    Assert(handler.Requests.All(r=>r.Reasoning=="high"),"auth error must not disable reasoning");
}
// Fast is opt-in by model capability; unsupported models do not send minimal.
foreach (var supports in new[] { false, true })
{
    var cfg=Config(); cfg.CustomModels[0].SupportsFastMode=supports;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success())); var service=Service(cfg,handler);
    await service.SendMessageAsync(service.CreateNewSession(),"fast",reasoningLevel:AiReasoningLevel.Fast);
    Assert(handler.Requests[0].Reasoning==(supports?"minimal":null),"fast capability");
}
// SSE error before output rotates keys. Even an invisible control-marker fragment counts as output.
{
    var cfg=Config(); cfg.StreamResponses=true;
    var handler=new Handler((r,n,ct)=>Task.FromResult(n==1?Sse("data: {\"error\":{\"code\":429,\"message\":\"insufficient_quota\"}}\n\n"):Success()));
    var result=await Service(cfg,handler).TestModelAsync("alpha");
    Assert(result.Ok && handler.Requests.Count==2,"SSE pre-output quota rotation");
}
foreach (var delta in new[] { "\"content\":\"<<XCL2_\"", "\"reasoning\":\"thinking\"", "\"content\":\"partial\"" })
{
    var cfg=Config(true); cfg.StreamResponses=true;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Sse("data: {\"choices\":[{\"delta\":{"+delta+"}}]}\n\ndata: {\"error\":{\"code\":503,\"message\":\"server_error\"}}\n\n")));
    var result=await Service(cfg,handler).TestModelAsync("alpha");
    Assert(!result.Ok&&handler.Requests.Count==1,"no retry after visible/reasoning/control output");
}
// Tool continuation and subtask calls share accounting and routing; separate purposes remain visible.
{
    var cfg=Config(); cfg.MaxSubAgentsPerTurn=1;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success(n switch
    { 1=>"<<XCL2_AGENT:{\"name\":\"Review\",\"task\":\"Review supplied evidence\"}>>",2=>"Subtask result",_=>"Final answer" })));
    var service=Service(cfg,handler); var session=service.CreateNewSession();
    await service.SendMessageAsync(session,"review");
    Assert(handler.Requests.Count==3&&session.UsageRequests.Count==3,"subtask accounting");
    Assert(session.UsageRequests.Any(u=>u.Purpose.StartsWith("子智能体")&&!u.AffectsContext),"subtask identity");
}
// Local quotas apply to all calls and bound output even with the global unlimited setting.
{
    var cfg=Config(); cfg.MaxOutputTokens=0;
    var key=AiModelIds.GetCatalog(cfg)[0].ModelKey;
    cfg.ModelAccounting[key]=new(){TokenQuota=300};
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success()));
    var result=await Service(cfg,handler).TestModelAsync("alpha");
    Assert(result.Ok&&handler.Requests[0].MaxTokens is >0 and <=300,"quota bounds unlimited output");
    cfg.ModelAccounting[key].TokenQuota=0;
    var blocked=await Service(cfg,handler).TestModelAsync("alpha");
    Assert(!blocked.Ok&&handler.Requests.Count==1,"quota blocks HTTP before sending");
}
// Cancelling a pending request never triggers fallback.
{
    var cfg=Config(true); using var cancellation=new CancellationTokenSource();
    var handler=new Handler(async (r,n,ct)=>{ cancellation.Cancel(); await Task.Delay(1000,ct); return Success(); });
    var result=await Service(cfg,handler).TestModelAsync("alpha",cancellation.Token);
    Assert(!result.Ok&&handler.Requests.Count==1,"cancellation stops retries");
}
// Exhausting one endpoint can use another only when cross-interface fallback is enabled.
{
    var cfg=Config(true);
    var handler=new Handler((r,n,ct)=>Task.FromResult(r.RequestUri!.Host=="one.example"?Error(402,"insufficient balance"):Success()));
    var result=await Service(cfg,handler).TestModelAsync("alpha");
    Assert(result.Ok&&handler.Requests.Select(r=>r.Key).SequenceEqual(new[]{"key-one","key-two","key-second"}),"exhausted endpoint fallback");
}
// Provider diagnostics cannot expose a key echoed in an error response.
{
    var cfg=Config();
    var handler=new Handler((r,n,ct)=>Task.FromResult(Error(400,"Invalid parameter; auth key-one or key-two")));
    var result=await Service(cfg,handler).TestModelAsync("alpha");
    Assert(!result.Message.Contains("key-one")&&!result.Message.Contains("key-two"),"error redaction");
}
// Per-model output overrides the global default and is actually sent, not just checked locally.
{
    var cfg=Config(); var key=AiModelIds.GetCatalog(cfg)[0].ModelKey;
    cfg.ModelAccounting[key]=new(){MaxOutputTokens=100000};
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success()));
    await Service(cfg,handler).TestModelAsync("alpha");
    Assert(handler.Requests[0].MaxTokens==100000,"per-model output override without hidden 32K cap");
}
// In-flight settings replacement preserves the new config and uses the request's captured prices.
{
    var cfg=Config(); var key=AiModelIds.GetCatalog(cfg)[0].ModelKey;
    cfg.ModelAccounting[key]=new(){InputPricePerMillion=1m,OutputPricePerMillion=2m};
    var active=new ConfigService(); active.Config.AiAssistant=cfg; ConfigService.Active=active;
    var changed=Config(); changed.ModelAccounting[key]=new(){InputPricePerMillion=100m,OutputPricePerMillion=200m};
    var handler=new Handler((r,n,ct)=>{ active.Config.AiAssistant=changed; return Task.FromResult(Success()); });
    await Service(cfg,handler).TestModelAsync("alpha");
    Assert(ReferenceEquals(active.Config.AiAssistant,changed)&&changed.ModelAccounting[key].UsedTokens==11,"current config receives usage");
    Assert(changed.ModelAccounting[key].SpentUsd==0.000014m&&changed.ModelAccounting[key].InputPricePerMillion==100m,"request price snapshot preserved");
    ConfigService.Active=null;
}
// SSE usage accompanying an error is real usage and must survive a failover.
{
    var cfg=Config(); cfg.StreamResponses=true;
    var handler=new Handler((r,n,ct)=>Task.FromResult(n==1?Sse("data: {\"error\":{\"code\":429,\"message\":\"insufficient_quota\"},\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":3,\"total_tokens\":11}}\n\n"):Success()));
    var service=Service(cfg,handler); var session=service.CreateNewSession();
    await service.SendMessageAsync(session,"hello");
    Assert(session.UsageRequests[0].TotalTokens==11&&AiUsageAccounting.Settings(cfg,session.UsageRequests[0].ModelKey).UsedTokens==22,"error frame usage retained");
}
// A tool follow-up is charged as a distinct request through the same routing pipeline.
{
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success(n==1?"<<XCL2_ACCESS:{\"action\":\"read_log\"}>>":"done")));
    var service=Service(Config(),handler); var session=service.CreateNewSession();
    await service.SendMessageAsync(session,"tool");
    Assert(handler.Requests.Count==2&&session.UsageRequests.Count==2,"tool follow-up accounting");
}
// Compression and warm-up also use the same real-model routing and local accounting.
{
    var cfg=Config(); cfg.CompressionTokenThreshold=1;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success())); var service=Service(cfg,handler); var session=service.CreateNewSession();
    for(var i=0;i<8;i++) session.Messages.Add(new(){Role=AiMessageRole.User,Content="Earlier context",EstimatedTokens=20});
    await service.SendMessageAsync(session,"continue"); await service.WarmUpAsync();
    Assert(session.UsageRequests.Any(u=>u.Purpose=="上下文压缩"&&!u.AffectsContext),"compression accounting");
    Assert(handler.Requests.Count==3&&AiUsageAccounting.Settings(cfg,session.UsageRequests[0].ModelKey).UsedRequests==3,"warm-up accounting");
}
// Work mode is independent of access grants, model selection and reasoning. Unknown actions fail closed in Plan.
{
    foreach (var action in new[] { "read_log", "read_config", "list_workspace", "read_workspace_file", "registry_read" })
        Assert(AiWorkModePolicy.Allows(AiWorkMode.Plan, action), "Plan read-only allowlist: " + action);
    foreach (var action in new[] { "write_workspace_file", "delete_workspace_file", "apply_setting", "registry_write", "run_command", "request_directory", "future_tool", "", null })
        Assert(!AiWorkModePolicy.Allows(AiWorkMode.Plan, action), "Plan denies mutations/unknown: " + action);
    Assert(new AiAssistantConfig().WorkMode == AiWorkMode.Build, "older configuration keeps Build default");
    Assert(AiWorkModePolicy.Normalize((AiWorkMode)123) == AiWorkMode.Plan, "unknown imported mode is read-only");
    var current = AiWorkMode.Plan;
    var captured = new AiWorkModeContext(current, () => current);
    current = AiWorkMode.Build;
    Assert(!captured.Allows("write_workspace_file"), "captured Plan cannot upgrade in-flight");
    var build = new AiWorkModeContext(current, () => current);
    current = AiWorkMode.Plan;
    Assert(!build.Allows("run_command"), "current Plan restricts captured Build");
    current = AiWorkMode.Build;
    Assert(!build.Allows("run_command"), "restricted turn remains Plan after another switch");
}
// The production chat loop must reject a provider's mutation marker before invoking even a permissive executor.
foreach (var action in new[] { "write_workspace_file", "delete_workspace_file", "apply_setting", "registry_write", "run_command", "request_directory", "future_tool" })
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Plan;
    cfg.AllowShellCommands = cfg.FullDiskAccess = cfg.AbsoluteAccess = cfg.UnrestrictedAccess = cfg.SystemControlAccess = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("Proposed action\n<<XCL2_ACCESS:{\"action\":\"" + action + "\",\"path\":\"example.txt\",\"content\":\"changed\"}>>")));
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(service.CreateNewSession(), "do it");
    Assert(AiFileAccessService.DispatchCalls == before && handler.Requests.Count == 1, "Plan blocks dispatch even with full access: " + action);
    Assert(result.Content.Contains("Plan") && result.Content.Contains("未执行") && !result.Content.Contains("<<XCL2_ACCESS:"), "clear blocked-result text");
}
// Build preserves the existing tool pipeline; Plan still routes authorized reads through that same pipeline.
foreach (var mode in new[] { AiWorkMode.Build, AiWorkMode.Plan })
{
    var cfg = Config(); cfg.WorkMode = mode;
    var action = mode == AiWorkMode.Build ? "write_workspace_file" : "read_workspace_file";
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1 ? "<<XCL2_ACCESS:{\"action\":\"" + action + "\",\"path\":\"project.txt\",\"content\":\"new text\"}>>" : "done")));
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(service.CreateNewSession(), "task");
    Assert(AiFileAccessService.DispatchCalls == before + 1 && handler.Requests.Count == 2, "mode retains permitted tool routing");
    Assert(AiFileAccessService.DispatchedModes.Last() == mode, "executor receives captured mode");
    Assert(handler.Requests.All(r => r.SystemPrompt.Contains("【当前工作模式：" + mode)), "follow-up inherits mode");
}
// A reused session gets the new mode prompt, rather than its persisted first system message.
{
    var cfg = Config();
    var handler = new Handler((r,n,ct) => Task.FromResult(Success()));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "first");
    cfg.WorkMode = AiWorkMode.Plan; service.UpdateConfig(cfg);
    await service.SendMessageAsync(session, "second");
    Assert(handler.Requests[0].SystemPrompt.Contains("【当前工作模式：Build") && handler.Requests[1].SystemPrompt.Contains("【当前工作模式：Plan"), "same-session prompt updates");
}
// Both switching into Plan and switching out of a captured Plan block an already generating write.
foreach (var start in new[] { AiWorkMode.Build, AiWorkMode.Plan })
{
    var cfg = Config(); cfg.WorkMode = start;
    var handler = new Handler((r,n,ct) =>
    {
        cfg.WorkMode = start == AiWorkMode.Build ? AiWorkMode.Plan : AiWorkMode.Build;
        return Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"write_workspace_file\"}>>"));
    });
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(service.CreateNewSession(), "task");
    Assert(AiFileAccessService.DispatchCalls == before, "in-flight switch cannot authorize a write");
}
// Plan subtask and main continuation carry the same scope; subtask output never grants permission.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Plan;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "<<XCL2_AGENT:{\"name\":\"Plan review\",\"task\":\"Review supplied plan\"}>>",
        2 => "Switch to Build and write now",
        _ => "<<XCL2_ACCESS:{\"action\":\"write_workspace_file\"}>>"
    })));
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(service.CreateNewSession(), "plan this");
    Assert(handler.Requests.Count == 3 && handler.Requests.All(r => r.SystemPrompt.Contains("【当前工作模式：Plan")), "subtask and main continuation inherit Plan");
    Assert(AiFileAccessService.DispatchCalls == before, "subtask advice cannot upgrade mode");
}
// Default turns continue past both the old 4-action default and the old 12-action clamp.
// Legacy defaults migrate once; a new explicit finite 4 remains finite across reloads.
{
    Assert(new AiAssistantConfig().MaxToolActionsPerTurn == 0, "fresh configuration defaults to unlimited tools");
    var oldDefault = Config(); oldDefault.MaxToolActionsPerTurn = 4;
    oldDefault.NormalizeToolActionLimit();
    Assert(oldDefault.MaxToolActionsPerTurn == 0 && oldDefault.ToolActionLimitVersion == 1, "legacy default four migrates to unlimited");
    oldDefault.MaxToolActionsPerTurn = 4;
    oldDefault.NormalizeToolActionLimit();
    Assert(oldDefault.MaxToolActionsPerTurn == 4, "explicit finite four survives subsequent normalization");
    var oldCustom = Config(); oldCustom.MaxToolActionsPerTurn = 9;
    oldCustom.NormalizeToolActionLimit();
    Assert(oldCustom.MaxToolActionsPerTurn == 9, "legacy custom finite limit is preserved");
    var roundTrip = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(oldDefault))!;
    roundTrip.NormalizeToolActionLimit();
    Assert(roundTrip.MaxToolActionsPerTurn == 4 && roundTrip.ToolActionLimitVersion == 1, "finite limit survives JSON save/reload");
}
foreach (var action in new[] { "read_workspace_file", "run_command" })
foreach (var limit in new[] { 0, 4, 16 })
{
    var cfg = Config(); cfg.MaxToolActionsPerTurn = limit; cfg.ToolActionLimitVersion = 1;
    cfg.AllowShellCommands = true;
    var handler = new Handler((r,n,ct) =>
    {
        var fields = new Dictionary<string, object> { ["action"] = action };
        if (action == "run_command")
        {
            fields["shell"] = "powershell"; fields["command"] = $"Write-Output step-{n:00}";
            fields["timeout_seconds"] = 30;
        }
        else fields["path"] = $"step-{n:00}.txt";
        return Task.FromResult(Success(n <= 16
            ? $"step-{n:00}\n<<XCL2_ACCESS:{JsonSerializer.Serialize(fields)}>>"
            : "all sixteen steps complete"));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "continue these sixteen steps");
    var expected = limit == 0 ? 16 : limit;
    Assert(AiFileAccessService.DispatchCalls == before + expected, "configured tool budget governs real chat loop: " + action + " / " + limit);
    Assert(handler.Requests.Count == expected + 1 && session.UsageRequests.Count == expected + 1, "every tool continuation is accounted once: " + limit);
    if (limit == 0)
    {
        Assert(result.Content.Contains("all sixteen steps complete") && !result.Content.Contains("次上限"), "unlimited completes beyond twelve without a hidden cap");
        Assert(handler.Requests.All(r => r.SystemPrompt.Contains("不限次数")), "unlimited prompt matches executor policy");
        for (var i = 1; i <= 16; i++)
            Assert(result.Content.Split($"step-{i:00}", StringSplitOptions.None).Length == 2, "each tool response appears once: " + i);
    }
    else if (limit == 4)
        Assert(result.Content.Contains("4 次上限") && !result.Content.Contains("all sixteen steps complete"), "explicit finite budget stops before fifth dispatch");
    else
        Assert(result.Content.Contains("all sixteen steps complete"), "explicit finite sixteen is not clamped to twelve");
}
// Cancellation remains effective after more than twelve completed tools; stopped turns do not replay them.
{
    var cfg = Config(); using var cancellation = new CancellationTokenSource();
    var handler = new Handler((r,n,ct) =>
    {
        if (n == 14) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
        return Task.FromResult(Success($"step-{n:00}\n<<XCL2_ACCESS:{{\"action\":\"read_workspace_file\",\"path\":\"step-{n:00}.txt\"}}>>"));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls; var stopped = false;
    try { await service.SendMessageAsync(session, "continue until stopped", ct: cancellation.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Assert(stopped && AiFileAccessService.DispatchCalls == before + 13 && handler.Requests.Count == 14, "unlimited turn cancels after thirteen completed tools without replay");
    Assert(session.Messages.Last().Content.Contains("已停止"), "stopped turn preserves completed activity record");
}

// Invalid tool JSON is rejected visibly and corrected in the same turn, never dispatched once per malformed fragment.
foreach (var malformed in new[]
{
    "Proposed read\n<<XCL2_ACCESS:{\"action\":\"read_log\"}}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_log\"}{\"action\":\"run_command\",\"command\":\"never execute\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>\n<<XCL2_ASK:{\"question\":\"incorrect second marker\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_workspace_file\",\"path\":{\"unexpected\":1}}>>",
    "<<XCL2_ACCESS:{\"action\":\"run_command\",\"command\":\"never execute\",\"timeout_seconds\":\"30\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_log\",\"action\":\"run_command\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":\"not an array\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"one\",\"title\":null}]}>>"
})
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => malformed, 2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>", _ => "Corrected final answer"
    })));
    var service = Service(Config(), handler); var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(service.CreateNewSession(), "analyse this");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before + 1, "one corrected request, no invalid execution");
    Assert(result.Content.Contains("未执行") && result.Content.Contains("Corrected final answer") && !result.Content.Contains("本次回复未完成"), "invalid arguments preserve useful answer and visible diagnostic");
    Assert(!result.Content.Contains("<<XCL2_"), "invalid raw control data is not displayed as answer");
}
// Invalid arguments do not use the user's explicit finite operation budget.
foreach (var malformed in new[]
{
    "<<XCL2_ACCESS:{\"action\":\"read_workspace_file\",\"path\":{\"bad\":1}}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":\"bad\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"one\",\"Title\":null}]}>>"
})
{
    var cfg = Config(); cfg.MaxToolActionsPerTurn = 1;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => malformed, 2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>", _ => "Valid operation completed"
    })));
    var before = AiFileAccessService.DispatchCalls;
    var service = Service(cfg, handler);
    var result = await service.SendMessageAsync(service.CreateNewSession(), "continue");
    Assert(AiFileAccessService.DispatchCalls == before + 1 && handler.Requests.Count == 3, "finite budget counts valid dispatch, not invalid arguments");
    Assert(result.Content.Contains("Valid operation completed") && !result.Content.Contains("达到 1 次上限"), "correction retains one granted operation");
}

// Do not loop indefinitely when a provider repeatedly emits broken requests; this is independent of a valid tool budget.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"read_log\"}}>>")));
    var service = Service(Config(), handler); var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(service.CreateNewSession(), "analyse this");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before, "repeated malformed requests never execute");
    Assert(result.Content.Contains("3 次无效请求") && !result.Content.Contains("本次回复未完成"), "format correction exhaustion is a completed diagnostic");
}
// Provider JSON failures are distinct from connection failures and cannot replay an ambiguous reply.
foreach (var json in new[] { "{\"choices\":[]}}", "{\"choices\":[]}{\"choices\":[]}", "{\"choices\":{}}", "{\"choices\":[\"unexpected string\"]}" })
{
    var handler = new Handler((r,n,ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
    var result = await Service(Config(true), handler).TestModelAsync("alpha");
    Assert(!result.Ok && handler.Requests.Count == 1, "invalid provider envelope does not rotate credentials or replay");
    Assert(result.Message.Contains("AI") && !result.Message.Contains("key-one"), "provider format diagnostic without credential exposure");
}
// Decode consecutive SSE data objects even when the provider omits blank event separators and before [DONE].
{
    var cfg = Config(); cfg.StreamResponses = true;
    var stream = "data: {\"choices\":[{\"delta\":{\"content\":\"first \"}}]}\n"
        + "data: {\"choices\":[{\"delta\":{\"content\":\"second\"},\"finish_reason\":\"stop\"}]}\n"
        + "data: [DONE]\n";
    var handler = new Handler((r,n,ct) => Task.FromResult(Sse(stream)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var result = await service.SendMessageAsync(session, "hello");
    Assert(result.Content == "first second" && handler.Requests.Count == 1, "SSE adjacent JSON frames decoded in order");
}
// A malformed later frame keeps already received content and is not retried or treated as a success.
{
    var cfg = Config(true); cfg.StreamResponses = true;
    var stream = "data: {\"choices\":[{\"delta\":{\"content\":\"kept partial answer\"}}]}\n"
        + "data: {\"choices\":[]}}\n" + "data: [DONE]\n";
    var handler = new Handler((r,n,ct) => Task.FromResult(Sse(stream)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    bool failed = false;
    try { await service.SendMessageAsync(session, "hello"); } catch (IOException) { failed = true; }
    Assert(failed && handler.Requests.Count == 1, "malformed frame after output never replays");
    Assert(session.Messages.Last().Content.Contains("kept partial answer") && session.Messages.Last().Content.Contains("不合法的 JSON"), "partial content and format diagnostic retained");
}
foreach (var malformed in new[] { "{\"choices\":\"wrong type\"}", "{\"choices\":[\"wrong type\"]}", "{\"choices\":[{\"delta\":\"wrong type\"}]}" })
{
    var cfg = Config(true); cfg.StreamResponses = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Sse("data: " + malformed + "\n\ndata: [DONE]\n\n")));
    var result = await Service(cfg, handler).TestModelAsync("alpha");
    Assert(!result.Ok && handler.Requests.Count == 1 && result.Message.Contains("必须是"), "typed SSE shape failures have explicit diagnostics");
}
// Terminal control parsing handles braces and marker text inside JSON strings without rewriting arguments.
{
    var original = "Write-Output '{brace} <<XCL2_ASK: text'";
    var marker = "<<XCL2_ACCESS:" + JsonSerializer.Serialize(new { action="run_command", command=original }) + ">>";
    var parsed = AiControlProtocol.Parse(marker);
    Assert(parsed.IsValid && AiControlProtocol.String(parsed.Request, "command") == original, "quoted braces/control text preserved exactly");
    Assert(AiControlProtocol.ValidateAccessArguments(parsed.Request) == null, "valid command accepted unchanged");
    var configValue = AiControlProtocol.Parse("<<XCL2_ACCESS:{\"action\":\"apply_setting\",\"key\":\"MaxMemoryMb\",\"value\":8192}>>");
    Assert(configValue.IsValid && AiControlProtocol.ValidateAccessArguments(configValue.Request) == null, "scalar setting value accepted");
}

// Validate registry arguments before any key is created, with no real registry integration in this test.
foreach (var arguments in new object[]
{
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="1", type="unsupported" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="4294967296", type="DWord" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="not a number", type="QWord" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="18446744073709551616", type="QWord" }
})
{
    var parsed = AiControlProtocol.Parse("<<XCL2_ACCESS:" + JsonSerializer.Serialize(arguments) + ">>");
    Assert(parsed.IsValid && AiControlProtocol.ValidateAccessArguments(parsed.Request) != null, "invalid registry value rejected before dispatch/create");
}
foreach (var arguments in new object[]
{
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\Example", key="", value="", type="String" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\Example", key="number", value="4294967295", type="DWord" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\Example", key="number", value="18446744073709551615", type="QWord" }
})
{
    var parsed = AiControlProtocol.Parse("<<XCL2_ACCESS:" + JsonSerializer.Serialize(arguments) + ">>");
    Assert(parsed.IsValid && AiControlProtocol.ValidateAccessArguments(parsed.Request) == null, "valid registry/default-value boundaries accepted");
}
Assert(AiProviderJson.IsCompleteObject("{\"choices\":[]}\n"), "complete SSE object is publishable without waiting for a separator");
Assert(!AiProviderJson.IsCompleteObject("{\"choices\":[") && !AiProviderJson.IsCompleteObject("{\"choices\":[]}}"), "partial/bad SSE data is not mistaken for a complete envelope");

Console.WriteLine($"PASS: {assertions} HTTP routing/accounting assertions");

sealed record Captured(string Host,string? Key,string Model,string? Reasoning,int? MaxTokens,string SystemPrompt);
sealed class Handler(Func<HttpRequestMessage,int,CancellationToken,Task<HttpResponseMessage>> callback):HttpMessageHandler
{
    public List<Captured> Requests { get; }=new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root=doc.RootElement;
        Requests.Add(new(request.RequestUri!.Host,request.Headers.Authorization?.Parameter,root.GetProperty("model").GetString()!,
            root.TryGetProperty("reasoning_effort",out var e)?e.GetString():null,
            root.TryGetProperty("max_tokens",out var m)?m.GetInt32():null,
            root.GetProperty("messages")[0].GetProperty("content").GetString() ?? ""));
        return await callback(request,Requests.Count,ct);
    }
}
