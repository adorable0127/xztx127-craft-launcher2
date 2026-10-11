using System.IO;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Incremental SSE decoding. A partial control marker is never shown as answer text.</summary>
internal static class AiStreamingCompletion
{
    internal static string VisibleText(string text)
    {
        var start = text.IndexOf("<<XCL2_", StringComparison.Ordinal);
        if (start >= 0) return text[..start].TrimEnd();
        const string prefix = "<<XCL2_";
        for (int n = Math.Min(prefix.Length - 1, text.Length); n > 0; n--)
            if (text.EndsWith(prefix[..n], StringComparison.Ordinal)) return text[..^n];
        return text;
    }

    internal static async Task<string> ReadAsync(Stream stream, Action<string>? content,
        Action<string, string>? reasoning, CancellationToken ct, Action<AiUsageAccounting.Usage>? usage = null,
        Action<JsonElement>? providerError = null, Action? responseStarted = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var answer = new StringBuilder();
        var thought = new StringBuilder();
        var summary = new StringBuilder();
        var frame = new StringBuilder();
        bool finished = false;
        bool completionSeen = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastPublish = -32;
        int publishedLength = -1;
        long lastReasoningPublish = -100;
        int publishedThoughtLength = 0;
        int publishedSummaryLength = 0;
        bool receivedOutput = false;
        void PublishAnswer(bool force = false)
        {
            if (answer.Length == 0 || publishedLength == answer.Length) return;
            if (!force && publishedLength >= 0 && clock.ElapsedMilliseconds - lastPublish < 32) return;
            publishedLength = answer.Length; lastPublish = clock.ElapsedMilliseconds;
            content?.Invoke(VisibleText(answer.ToString()));
        }
        void PublishReasoning(bool force = false)
        {
            if (publishedThoughtLength == thought.Length && publishedSummaryLength == summary.Length) return;
            // As the drawer grows, reduce string allocations and layout updates, not its content.
            int publishInterval = (int)Math.Clamp(((long)thought.Length + summary.Length) / 1000, 100, 750);
            if (!force && (publishedThoughtLength > 0 || publishedSummaryLength > 0)
                && clock.ElapsedMilliseconds - lastReasoningPublish < publishInterval) return;
            publishedThoughtLength = thought.Length; publishedSummaryLength = summary.Length;
            lastReasoningPublish = clock.ElapsedMilliseconds;
            reasoning?.Invoke(thought.ToString(), summary.ToString());
        }
        void Dispatch()
        {
            if (frame.Length == 0) return;
            var data = frame.ToString().Trim(); frame.Clear();
            if (data == "[DONE]") { finished = true; return; }
            // Some compatible providers omit the empty line between complete data objects.
            // Decode every complete envelope in order, while retaining real partial output on error.
            AiProviderJson.ParseFrames(data, "AI 流式接口", allowSequence: true, onFrame: DispatchFrame);
        }
        void DispatchFrame(JsonElement root)
        {
            var usageSnapshot = AiUsageAccounting.Parse(root);
            if (usageSnapshot != null) usage?.Invoke(usageSnapshot);
            if (root.TryGetProperty("error", out var error))
            {
                providerError?.Invoke(root);
                throw new IOException("流式接口返回错误：" + error.ToString()[..Math.Min(600, error.ToString().Length)]);
            }
            if (!root.TryGetProperty("choices", out var choices)) return;
            if (choices.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("AI 流式接口的 choices 必须是数组。请检查接口兼容性。");
            if (choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("AI 流式接口的 choice 必须是对象。请检查接口兼容性。");
            var stopReason = choice.TryGetProperty("finish_reason", out var stop) && stop.ValueKind == JsonValueKind.String ? stop.GetString() : null;
            var delta = choice.TryGetProperty("delta", out var streamed) ? streamed
                : choice.TryGetProperty("message", out var message) ? message : default;
            if (delta.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
                throw new InvalidOperationException("AI 流式接口的 delta/message 必须是对象。请检查接口兼容性。");
            var parsed = AiCompletionContentParser.Parse(delta, preserveWhitespace: true);
            if (!receivedOutput && (parsed.Content.Length > 0 || parsed.Reasoning.Length > 0 || parsed.ReasoningSummary.Length > 0
                || HasGeneratedPayload(delta)))
            {
                receivedOutput = true;
                // Signal even a hidden control prefix, before throttled UI publication, so callers
                // cannot retry and replay a partially received response or command.
                responseStarted?.Invoke();
            }
            if (parsed.Content.Length > 0)
            {
                if (answer.Length + parsed.Content.Length > 2_000_000) throw new IOException("回答超过长度上限。");
                answer.Append(parsed.Content);
                PublishAnswer();
            }
            if (parsed.Reasoning.Length > 0 || parsed.ReasoningSummary.Length > 0)
            {
                thought.Append(parsed.Reasoning);
                summary.Append(parsed.ReasoningSummary);
                PublishReasoning();
            }
            if (stopReason is "length" or "content_filter")
                throw new IOException("回答被接口截断，已保留收到的内容；未执行不完整指令。");
            if (stopReason != null) completionSeen = true; // usage may arrive in a later, choices:[] frame
        }
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) { Dispatch(); break; }
                if (line.Length > 2_000_000) throw new IOException("SSE 帧超过长度上限。");
                if (line.Length == 0) { Dispatch(); if (finished) break; continue; }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var value = line[5..]; if (value.StartsWith(' ')) value = value[1..];
                if (value.Trim() == "[DONE]") { Dispatch(); finished = true; break; }
                frame.AppendLine(value);
                if (frame.Length > 2_000_000) throw new IOException("SSE 帧超过长度上限。");
                // Publish a complete JSON object immediately, including providers that omit
                // blank separators. Genuine multiline JSON waits until it is complete.
                if (AiProviderJson.IsCompleteObject(frame.ToString())) Dispatch();
            }
        }
        finally { PublishAnswer(force: true); PublishReasoning(force: true); }
        if (!finished && !completionSeen) throw new IOException("流式连接提前中断，已保留部分回答；未执行不完整指令。");
        return answer.ToString();
    }

    private static bool HasGeneratedPayload(JsonElement delta)
    {
        if (delta.ValueKind != JsonValueKind.Object) return false;
        // Opaque/encrypted reasoning and structured output must also stop automatic replay.
        // We inspect presence only: these fields are never decoded, displayed or executed here.
        foreach (var name in new[] { "content", "reasoning_content", "reasoning", "reasoning_summary",
            "reasoning_details", "tool_calls", "function_call", "refusal", "audio" })
        {
            if (!delta.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
                || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
                || value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any()) return true;
        }
        return false;
    }
}
