using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Strict provider envelope parsing with source diagnostics; no inferred or repaired tool content.</summary>
internal static class AiProviderJson
{
    internal static IReadOnlyList<JsonElement> ParseFrames(string payload, string source, bool allowSequence, Action<JsonElement>? onFrame = null)
    {
        var documents = new List<JsonElement>();
        var bytes = Encoding.UTF8.GetBytes(payload.TrimStart('\uFEFF'));
        int offset = 0;
        try
        {
            while (offset < bytes.Length)
            {
                while (offset < bytes.Length && bytes[offset] is 9 or 10 or 13 or 32) offset++;
                if (offset == bytes.Length) break;
                if (!allowSequence && documents.Count > 0)
                    throw new InvalidOperationException($"{source} 返回了多个 JSON 值；非流式回答必须只包含一个 JSON 对象。请检查接口格式。");
                var reader = new Utf8JsonReader(bytes.AsSpan(offset), isFinalBlock: true, state: default);
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"{source} 的 JSON 根值必须是对象，实际为 {document.RootElement.ValueKind}。请检查接口兼容性。");
                var frame = document.RootElement.Clone();
                documents.Add(frame);
                offset += checked((int)reader.BytesConsumed);
                onFrame?.Invoke(frame);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{source} 返回了不合法的 JSON（第 {(ex.LineNumber ?? 0) + 1} 行，当前段字节 {ex.BytePositionInLine ?? 0}，已读取 {offset} 字节）。请检查供应商响应、SSE 分帧及多余括号；已收到的内容会保留，不自动重放工具。", ex);
        }
        if (documents.Count == 0) throw new InvalidOperationException($"{source} 返回了空 JSON 响应。请检查接口地址及模型兼容性。");
        return documents;
    }

    internal static JsonElement ParseResponse(string payload) => ParseFrames(payload, "AI 接口", allowSequence: false)[0];

    internal static bool IsCompleteObject(string payload)
    {
        // This is only a publication boundary check. A false result is still strictly decoded
        // at the event boundary/EOF, so malformed data is not discarded or treated as success.
        var bytes = Encoding.UTF8.GetBytes(payload);
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject || !reader.TrySkip()) return false;
            int offset = checked((int)reader.BytesConsumed);
            while (offset < bytes.Length && bytes[offset] is 9 or 10 or 13 or 32) offset++;
            return offset == bytes.Length;
        }
        catch (JsonException) { return false; }
    }
}
