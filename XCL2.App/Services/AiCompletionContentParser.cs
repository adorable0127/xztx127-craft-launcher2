using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Only reads text explicitly returned by the provider. Reasoning never becomes answer/tool text.</summary>
internal static class AiCompletionContentParser
{
    internal sealed record Result(string Content, string ReasoningSummary, string Reasoning);

    internal static Result Parse(JsonElement message, bool preserveWhitespace = false)
    {
        if (message.ValueKind != JsonValueKind.Object) return new("", "", "");
        var content = message.TryGetProperty("content", out var value) ? ReadText(value) : "";
        var summary = new List<string>();
        var reasoning = new List<string>();
        AddField(message, "reasoning_summary", summary, preserveWhitespace);
        AddField(message, "reasoning_content", reasoning, preserveWhitespace);
        AddField(message, "reasoning", reasoning, preserveWhitespace);
        if (message.TryGetProperty("reasoning_details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object) continue;
                var type = detail.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String
                    ? kind.GetString() : null;
                // Explicit allow-list: encrypted/signature/redacted data is neither decoded nor displayed.
                if (type == "reasoning.summary") AddField(detail, "summary", summary, preserveWhitespace);
                else if (type == "reasoning.text") AddField(detail, "text", reasoning, preserveWhitespace);
            }
        }
        return new(content, JoinDistinct(summary), JoinDistinct(reasoning));
    }

    private static void AddField(JsonElement source, string property, List<string> target, bool preserveWhitespace)
    {
        if (!source.TryGetProperty(property, out var value)) return;
        var text = ReadText(value);
        if (preserveWhitespace ? text.Length > 0 : !string.IsNullOrWhiteSpace(text)) target.Add(preserveWhitespace ? text : text.Trim());
    }

    private static string ReadText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var part in value.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) { text.Append(part.GetString()); continue; }
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() is not ("text" or "output_text" or "summary_text")) continue;
            if (!part.TryGetProperty("text", out var partText)) continue;
            if (partText.ValueKind == JsonValueKind.String) text.Append(partText.GetString());
            else if (partText.ValueKind == JsonValueKind.Object && partText.TryGetProperty("value", out var plain)
                && plain.ValueKind == JsonValueKind.String) text.Append(plain.GetString());
        }
        return text.ToString();
    }

    private static string JoinDistinct(List<string> parts)
    {
        // Public reasoning belongs in its own scrollable drawer. Preserve it in full for history
        // and display; never turn an encrypted or otherwise unreturned field into reasoning.
        return string.Join("\n\n", parts.Distinct(StringComparer.Ordinal));
    }
}
