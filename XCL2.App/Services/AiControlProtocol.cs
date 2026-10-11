using System.Globalization;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Parses one explicit terminal control block. Invalid JSON never becomes an executable request.</summary>
internal static class AiControlProtocol
{
    internal sealed record Result(string? Kind, string DisplayText, JsonElement Request, string? Error)
    {
        internal bool HasMarker => Kind != null;
        internal bool IsValid => HasMarker && Error == null;
    }

    internal static Result Parse(string? reply)
    {
        var text = reply ?? "";
        int start = -1;
        string? kind = null;
        foreach (var candidate in new[] { "ACCESS", "ASK", "AGENT" })
        {
            int index = text.IndexOf("<<XCL2_" + candidate + ":", StringComparison.Ordinal);
            if (index >= 0 && (start < 0 || index < start)) { start = index; kind = candidate; }
        }
        if (start < 0) return new(null, text, default, null);
        var display = text[..start].TrimEnd();
        var block = text[start..].TrimEnd();
        if (!block.EndsWith(">>", StringComparison.Ordinal))
            return new(kind, display, default, "控制标记必须完整并位于回复末尾，未执行。请重新生成一个完整标记。");
        var prefixLength = ("<<XCL2_" + kind + ":").Length;
        var payload = block[prefixLength..^2];
        if (payload.Length > 1_600_000) return new(kind, display, default, "工具请求超过长度上限，未执行。");
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new(kind, display, default, "控制标记中的 JSON 必须是一个对象，未执行。");
            if (HasDuplicateProperties(doc.RootElement))
                return new(kind, display, default, "JSON 含重复字段，无法确定实际参数，未执行。请每个字段只提供一次。");
            var request = doc.RootElement.Clone();
            string? error = kind switch
            {
                "ACCESS" when string.IsNullOrWhiteSpace(String(request, "action")) => "工具 action 必须是非空字符串，未执行。",
                "AGENT" when string.IsNullOrWhiteSpace(String(request, "task")) || String(request, "task")!.Length > 6000 => "子任务 task 必须是 1 至 6000 字符的字符串，未执行。",
                "ASK" when !HasQuestion(request) => "提问必须包含非空 question，或包含问题对象的 questions 数组，未显示提问卡片。",
                _ => null
            };
            return new(kind, display, request, error);
        }
        catch (JsonException ex)
        {
            // Do not remove braces, pick the first of several commands, or guess invalid arguments.
            // The caller can feed this precise diagnostic back to the model without ending the turn.
            return new(kind, display, default, $"控制标记 JSON 不合法（第 {(ex.LineNumber ?? 0) + 1} 行，字节 {ex.BytePositionInLine ?? 0}）。只允许一个 JSON 对象及一种控制标记；检查多余括号、逗号和转义字符。未执行。");
        }
    }

    internal static string Strip(string? reply, string kind)
    {
        var parsed = Parse(reply);
        return parsed.Kind == kind ? parsed.DisplayText : reply ?? "";
    }

    internal static string? ValidateAccessArguments(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object) return "工具参数必须是 JSON 对象。";
        foreach (var name in new[] { "action", "key", "path", "content", "shell", "command", "type", "sha256", "old_text", "new_text", "glob", "query", "next_action", "notes" })
            if (request.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return $"参数 {name} 必须是字符串，实际为 {value.ValueKind}。";
        if (request.TryGetProperty("value", out var setting) && setting.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            return "参数 value 必须是字符串、数值或布尔值。";
        foreach (var name in new[] { "timeout_seconds", "start_line", "line_count", "limit" })
            if (request.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
                return $"参数 {name} 必须是 32 位整数。";
        foreach (var name in new[] { "regex", "case_sensitive" })
            if (request.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return $"参数 {name} 必须是布尔值 true 或 false。";
        var action = String(request, "action");
        if (action is not ("apply_setting" or "read_config" or "request_directory" or "read_workspace_file"
            or "write_workspace_file" or "delete_workspace_file" or "read_file_range" or "registry_read" or "registry_write"
            or "patch_workspace_file" or "search_project" or "list_project_files" or "read_log" or "list_workspace" or "update_task" or "run_command"))
            return "未知工具 action：" + action + "。请使用已公开的工具名称。";
        string[] required = action switch
        {
            "apply_setting" => new[] { "key" },
            "read_config" => new[] { "key" },
            "request_directory" or "read_workspace_file" or "write_workspace_file" or "delete_workspace_file" or "read_file_range" or "registry_read" or "registry_write" => new[] { "path" },
            "patch_workspace_file" => new[] { "path", "sha256", "old_text" },
            "search_project" => new[] { "path", "query" },
            "list_project_files" => new[] { "path" },
            "read_log" or "list_workspace" or "update_task" or "run_command" => Array.Empty<string>(),
            _ => new[] { "action" }
        };
        foreach (var name in required)
            if (string.IsNullOrWhiteSpace(String(request, name))) return $"参数 {name} 缺失或为空。";
        if (action == "write_workspace_file" && !HasString(request, "content")) return "write_workspace_file 缺少字符串 content。";
        if (action == "patch_workspace_file" && !HasString(request, "new_text")) return "patch_workspace_file 缺少字符串 new_text。";
        if (action == "run_command" && string.IsNullOrWhiteSpace(String(request, "command")) && string.IsNullOrWhiteSpace(String(request, "content")))
            return "run_command 缺少 command。";
        if (action == "apply_setting" && (!request.TryGetProperty("value", out var settingValue) || settingValue.ValueKind == JsonValueKind.Null))
            return "apply_setting 缺少 value。";
        if (action == "registry_write")
        {
            if (!HasString(request, "key")) return "registry_write 缺少字符串 key；默认值请明确传空字符串。";
            var text = Scalar(request, "value");
            if (text == null) return "registry_write 缺少 value。";
            var type = (String(request, "type") ?? "String").ToLowerInvariant();
            if (type is not ("string" or "expandstring" or "multistring" or "dword" or "qword"))
                return "registry_write 的 type 只能是 String / ExpandString / MultiString / DWord / QWord。";
            if (type == "dword" && !uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return "DWord 的 value 必须是 0 至 4294967295 的整数。";
            if (type == "qword" && !ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return "QWord 的 value 必须是 0 至 18446744073709551615 的整数。";
        }
        if (action == "update_task") return ValidateTaskArguments(request);
        return null;
    }

    private static string? ValidateTaskArguments(JsonElement request)
    {
        if (String(request, "notes")?.Length > 20000 || String(request, "next_action")?.Length > 10000)
            return "任务 notes 或 next_action 超过长度上限。";
        if (!request.TryGetProperty("steps", out var steps)) return null;
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() > 100)
            return "steps 必须是最多 100 项的步骤对象数组。";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int inProgress = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) return "每个步骤必须是 JSON 对象。";
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in step.EnumerateObject())
            {
                var name = property.Name.ToLowerInvariant();
                if (name is not ("id" or "title" or "status" or "evidence")) continue;
                if (!fields.Add(name)) return "步骤含重复字段：" + property.Name + "。";
                if (property.Value.ValueKind != JsonValueKind.String) return "步骤 " + property.Name + " 必须是字符串。";
                var text = property.Value.GetString()!;
                if (name == "id" && (string.IsNullOrWhiteSpace(text) || text.Length > 128 || !ids.Add(text)))
                    return "步骤 ID 必须唯一、非空且不超过 128 字符。";
                if (name == "status")
                {
                    if (text is not ("pending" or "in_progress" or "completed" or "blocked")) return "任务步骤状态无效。";
                    if (text == "in_progress" && ++inProgress > 1) return "同时最多一个步骤处于 in_progress。";
                }
                if (name == "title" && text.Length > 1000 || name == "evidence" && text.Length > 4000)
                    return "任务步骤 title 或 evidence 超过长度上限。";
            }
        }
        return null;
    }

    internal static string? String(JsonElement request, string name)
        => request.ValueKind == JsonValueKind.Object && request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string? Scalar(JsonElement request, string name)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.GetRawText() : null;
    }

    private static bool HasString(JsonElement request, string name)
        => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    private static bool HasQuestion(JsonElement request)
    {
        bool Valid(JsonElement question) => question.ValueKind == JsonValueKind.Object
            && !string.IsNullOrWhiteSpace(String(question, "question") ?? String(question, "text") ?? String(question, "title"));
        return request.TryGetProperty("questions", out var questions)
            ? questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0 && questions.EnumerateArray().All(Valid)
            : Valid(request);
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
}
