using System.Text;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>
/// 对能确定含义的常见 POSIX 语法做有限转换，不是 Bash 解释器。
/// 引号/注释内文本不会被全局替换；复杂 Linux 脚本交回模型改写。
/// </summary>
public static class WindowsCommandAdapter
{
    public const string ConversionWarning = "已将 Linux 命令自动转换为 Windows 命令；转换后仍可能存在兼容性或其他执行问题，请留意执行结果。";
    public sealed record Result(string Shell, string Command, bool Converted, string? Error = null);
    private sealed record Token(string Raw, string Value, int Start, int Length, bool Operator, bool Literal);

    public static Result Prepare(string? shell, string? command)
    {
        var input = command ?? "";
        var requested = string.IsNullOrWhiteSpace(shell) ? "powershell" : shell.Trim().ToLowerInvariant();
        var linuxShell = requested is "bash" or "sh" or "zsh";
        var target = linuxShell ? "powershell" : requested;
        Result Reject(string why) => new(target, input, false,
            "系统：命令未执行。" + why + " 请改写为 Windows PowerShell 5.1 或 cmd 命令后重试，不要原样重试。");
        if (target is not ("powershell" or "cmd")) return Reject("只支持 PowerShell / cmd。");
        if (string.IsNullOrWhiteSpace(input)) return Reject("run_command 缺少 command。");

        // 先处理完整的 cat here-document，避免把正文里的引号/命令当作脚本解析。
        var newline = input.IndexOf('\n');
        var firstLine = newline >= 0 ? Tokenize(input[..newline].TrimEnd('\r')) : null;
        if (firstLine?.FirstOrDefault()?.Raw == "cat" && firstLine.Any(t => t.Operator && t.Raw == "<<"))
        {
            var header = firstLine;
            if (header is not { Count: 5 } || header[0].Value != "cat")
                return Reject("该 Linux here-document 结构不能可靠转换。");
            Token? path = null, delimiter = null;
            var append = false;
            for (var i = 1; i < header.Count; i += 2)
            {
                if (header[i].Raw == "<<") delimiter = header[i + 1];
                else if (header[i].Raw is ">" or ">>")
                { path = header[i + 1]; append = header[i].Raw == ">>"; }
                else return Reject("该 here-document 包含不支持的重定向。");
            }
            if (path == null || delimiter == null || !SafePath(path)
                || !Regex.IsMatch(delimiter.Value, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                return Reject("here-document 的目标路径或结束标记无效。");
            var end = Regex.Match(input[(newline + 1)..], @"(?m)^" + Regex.Escape(delimiter.Value) + @"\r?$", RegexOptions.CultureInvariant);
            if (!end.Success || !string.IsNullOrWhiteSpace(input[(newline + 1 + end.Index + end.Length)..]))
                return Reject("here-document 未结束或后面还有其他命令。");
            var body = input.Substring(newline + 1, end.Index);
            if (delimiter.Raw == delimiter.Value && body.IndexOfAny(['$', '`', '\\']) >= 0)
                return Reject("未加引号的 here-document 含变量或转义，不能按字面内容转换。");
            var method = append ? "AppendAllText" : "WriteAllText";
            return new("powershell", $"[System.IO.File]::{method}([System.IO.Path]::GetFullPath({Quote(path.Value)}), {Quote(body)}, [System.Text.UTF8Encoding]::new($false))", true);
        }

        var tokens = Tokenize(input);
        if (tokens == null) return Reject("引号或 here-string 没有正确闭合。");
        while (tokens.Count > 0 && tokens[0].Raw == "\n") tokens.RemoveAt(0);
        while (tokens.Count > 0 && tokens[^1].Raw == "\n") tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0) return new(target, input, linuxShell);
        if (tokens.Any(t => t.Operator && t.Raw is "<<" or "<<<"))
            return Reject("不支持该 Linux 输入重定向；写文件请用 write_workspace_file，输入脚本请用 PowerShell 管道。");
        if (target == "powershell" && tokens.Any(t => t.Operator && t.Raw == "<"))
            return Reject("PowerShell 不支持 < 输入重定向，请按内容类型改用 Windows 管道或文件 API。");
        if (target == "powershell" && tokens.Any(t => t.Operator && t.Raw is "&&" or "||"))
            return Reject("Windows PowerShell 5.1 不支持 && / ||；请显式检查 $? 或 $LASTEXITCODE，不能直接替换为分号。");

        var first = tokens[0].Raw;
        var linuxFlags = tokens.Any(t => !t.Operator && t.Raw == t.Value && t.Value is "-la" or "-al" or "-l" or "-a" or "-p" or "-r" or "-R" or "-f" or "-rf" or "-fr" or "-Rrf");
        var known = first is "ls" or "pwd" or "cat" or "mkdir" or "cp" or "mv" or "rm" or "touch" or "export";
        var nativeParameter = tokens.Skip(1).Any(t => Regex.IsMatch(t.Raw, @"^-[A-Za-z]{2,}$") &&
            t.Value is not ("-la" or "-al" or "-rf" or "-fr" or "-Rrf"));
        var simple = tokens.All(t => !t.Operator);
        var convertSimple = known && (linuxShell || target == "cmd" || linuxFlags || first is "touch" or "export"
            || (simple && !nativeParameter && tokens.All(t => t.Literal)));
        if (target == "cmd" && first == "mkdir" && !linuxFlags) convertSimple = false;
        if (convertSimple)
        {
            if (!simple || tokens.Any(t => !t.Literal)) return Reject("此 Linux 命令含管道、变量或复合表达式，不能可靠转换。");
            var args = tokens.Skip(1).ToList();
            var flags = args.Where(t => t.Value.StartsWith('-')).Select(t => t.Value).ToArray();
            var paths = args.Where(t => !t.Value.StartsWith('-')).ToArray();
            bool Flags(params string[] allowed) => flags.All(f => allowed.Contains(f, StringComparer.Ordinal));
            if (paths.Any(t => !SafePath(t))) return Reject("不能猜测 Linux 绝对路径、变量或通配符对应的 Windows 路径。");
            var quoted = string.Join(", ", paths.Select(t => Quote(t.Value)));
            string? converted = first switch
            {
                "pwd" when args.Count == 0 => "Get-Location",
                "ls" when Flags("-a", "-l", "-la", "-al") => "Get-ChildItem" + (paths.Length > 0 ? " -LiteralPath " + quoted : "")
                    + (flags.Any(f => f.Contains('a')) ? " -Force" : ""),
                "cat" when flags.Length == 0 && paths.Length > 0 => "Get-Content -LiteralPath " + quoted,
                "mkdir" when Flags("-p") && paths.Length > 0 => "New-Item -ItemType Directory -Path " + quoted + (flags.Length > 0 ? " -Force" : ""),
                "cp" when Flags("-r", "-R", "-f", "-rf", "-fr") && paths.Length == 2 => "Copy-Item -LiteralPath " + Quote(paths[0].Value)
                    + " -Destination " + Quote(paths[1].Value) + (flags.Any(f => f.Contains('r') || f.Contains('R')) ? " -Recurse" : "")
                    + (flags.Any(f => f.Contains('f')) ? " -Force" : ""),
                "mv" when Flags("-f") && paths.Length == 2 => "Move-Item -LiteralPath " + Quote(paths[0].Value) + " -Destination " + Quote(paths[1].Value)
                    + (flags.Length > 0 ? " -Force" : ""),
                // rm 不带 -r 时不猜测目标是否为目录（Remove-Item 对目录的语义不同）。
                "rm" when Flags("-r", "-R", "-f", "-rf", "-fr") && paths.Length > 0 && flags.Any(f => f.Contains('r') || f.Contains('R'))
                    => "Remove-Item -LiteralPath " + quoted + " -Recurse" + (flags.Any(f => f.Contains('f')) ? " -Force" : ""),
                "export" when args.Count == 1 && Regex.IsMatch(args[0].Value, @"^[A-Za-z_][A-Za-z0-9_]*=")
                    => "$env:" + args[0].Value[..args[0].Value.IndexOf('=')] + " = " + Quote(args[0].Value[(args[0].Value.IndexOf('=') + 1)..]),
                _ => null
            };
            if (converted != null) return new("powershell", converted, true);
            // 已合法的 PowerShell 别名照原样运行；Linux 参数或 Linux shell 必须改写。
            if (linuxShell || target == "cmd" || linuxFlags || flags.Length > 0 || first is "touch" or "export")
                return Reject("该 Linux 命令或参数不在自动转换范围内。");
        }

        // 只转换重定向目标，不能改动字符串、注释、URL 或普通参数中的 /dev/null。
        var edits = new List<(int Start, int Length, string Text)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Value == "/dev/null" && i > 0 && tokens[i - 1].Operator && tokens[i - 1].Raw is ">" or ">>" or "&>" or "&>>")
                edits.Add((token.Start, token.Length, target == "cmd" ? "NUL" : "$null"));
            if (token.Operator && token.Raw is "&>" or "&>>")
            {
                if (target == "cmd") return Reject("cmd 的合并重定向请写为 > 文件 2>&1。");
                edits.Add((token.Start, token.Length, token.Raw == "&>" ? "*>" : "*>>"));
            }
        }
        if (linuxShell || first is "sudo" or "source" || (known && linuxFlags))
            return Reject("该 Linux 脚本无法完整转换为 Windows 命令。");
        var output = new StringBuilder(input);
        foreach (var edit in edits.OrderByDescending(e => e.Start)) output.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        return new(target, output.ToString(), edits.Count > 0);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static bool SafePath(Token token) => token.Literal && !token.Operator && token.Value.Length > 0
        && !token.Value.StartsWith('/') && !token.Value.StartsWith('~') && token.Value.IndexOfAny(['*', '?', '[', ']']) < 0
        && !(token.Raw == token.Value && token.Value.EndsWith('\\') && !Regex.IsMatch(token.Value, @"^[A-Za-z]:\\"));

    // 保留原文坐标，字符串支持 PowerShell 双单引号与反引号，Windows 路径里的反斜杠不当作转义。
    private static List<Token>? Tokenize(string text)
    {
        var result = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] is '\r' or '\n') { result.Add(new("\n", "\n", i++, 1, true, false)); continue; }
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text.AsSpan(i).StartsWith("<#"))
            {
                var depth = 1; i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (text.AsSpan(i).StartsWith("<#")) { depth++; i += 2; }
                    else if (text.AsSpan(i).StartsWith("#>")) { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) return null;
                continue;
            }
            if (text[i] == '#') { while (i < text.Length && text[i] != '\n') i++; continue; }
            var start = i;
            if (text[i] == '@' && i + 2 < text.Length && text[i + 1] is '\'' or '"' && text[i + 2] is '\r' or '\n')
            {
                var end = Regex.Match(text[(i + 2)..], "(?m)^" + Regex.Escape(text[i + 1] + "@") + @"(?=$|[\s;|&),])");
                if (!end.Success) return null;
                i += 2 + end.Index + end.Length;
                result.Add(new(text[start..i], text[start..i], start, i - start, false, false));
                continue;
            }
            if ("<>|&;(){}".Contains(text[i]))
            {
                i++;
                if (i < text.Length && ((text[i] == text[start] && "<>|&".Contains(text[start])) || (text[start] == '&' && text[i] == '>'))) i++;
                if (i < text.Length && ((text[start..i] == "&>" && text[i] == '>') || (text[start..i] == "<<" && text[i] == '<'))) i++;
                result.Add(new(text[start..i], text[start..i], start, i - start, true, false));
                continue;
            }
            var value = new StringBuilder();
            var literal = true;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && !"<>|&;(){}".Contains(text[i]))
            {
                if (text[i] is '\'' or '"')
                {
                    var quote = text[i++];
                    var closed = false;
                    while (i < text.Length)
                    {
                        var c = text[i++];
                        if (c == quote)
                        {
                            if (quote == '\'' && i < text.Length && text[i] == '\'') { value.Append('\''); i++; continue; }
                            closed = true; break;
                        }
                        if (quote == '"' && c is '$' or '`') literal = false;
                        if (quote == '"' && c == '`' && i < text.Length) { value.Append(c).Append(text[i++]); continue; }
                        value.Append(c);
                    }
                    if (!closed) return null;
                }
                else
                {
                    var c = text[i++];
                    if (c is '$' or '`') literal = false;
                    if (c == '`' && i < text.Length) value.Append(c).Append(text[i++]);
                    else value.Append(c);
                }
            }
            result.Add(new(text[start..i], value.ToString(), start, i - start, false, literal));
        }
        return result;
    }
}
