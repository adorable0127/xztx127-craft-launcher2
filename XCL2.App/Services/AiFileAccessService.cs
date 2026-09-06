using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// D 需求：让 AI 助手能在用户明确批准后，读取启动器日志/配置项含义、或修改一个已知的安全设置项。
/// 项目里原来完全没有这套机制，这里是第一版，设计上刻意收得很紧，边界如下（后续要放宽再改）：
///
/// 1) 范围限制——启动器数据只认白名单；工作目录只认经过边界校验的相对路径：
///    - 读日志：不接受 AI 传来的任意日志路径，只允许读"当前这次启动器运行会话"的内存日志缓冲
///      （<see cref="LauncherLogService.GetBufferedText"/>）。崩溃文件分析继续走日志页用户主动选择/授权的既有流程。
///    - 读/改配置：只认 <see cref="ReadableKeys"/> / <see cref="WritableKeys"/> 里显式登记的字段名，
///      AI 传别的字段名一律拒绝，不做反射式访问。访客模式、实验功能等敏感项保持只读。
///    - 工作目录：仅在用户预先配置的目录内允许 list/read/write；AI 只能传相对路径，解析后再次检查
///      完整路径是否仍位于工作目录内，因此绝对路径和 ../ 越界都会被拒绝。
///
/// 2) 二次确认——每次都问，不做"本次会话内长期授权"：
///    这是刻意的取舍：读日志/查配置本身风险很低，但如果做成"问一次、后面都放行"，用户很容易在
///    没留意的情况下让后续消息里的改写请求也被自动放行。每次都弹一次确认，用户看到的永远是
///    "这次具体要做什么"，而不是一个笼统的开关。
///
/// 3) 写操作二次校验：即使字段在白名单里，值也要过 <see cref="WritableKeys"/> 里登记的校验器
///    （类型、范围）才会真正写盘，校验失败直接拒绝，不做"尽量修正成合法值"这种自作主张的行为。
/// </summary>
public static class AiFileAccessService
{
    // AI 回复末尾用这个标记发起请求，不会展示给用户；<see cref="ProcessReply"/> 负责摘掉它。
    // 格式故意用 JSON，一是取值时不用自己写微型 DSL 解析器，二是"看得懂内容"，方便审计/调试。
    private static readonly Regex MarkerRegex = new(
        @"<<XCL2_ACCESS:(\{.*\})>>\s*$", RegexOptions.Singleline | RegexOptions.Compiled);

    private sealed record ReadableKey(string Label, Func<AppConfig, string> Get);
    private sealed record WritableKey(string Label, Func<AppConfig, string, (bool Ok, string? Error, string? AppliedDisplay)> TryApply);

    // 只读：字段含义查询。都是设置页里本来就有、用户自己也能看到的字段，不涉及账户/隐私数据。
    private static readonly Dictionary<string, ReadableKey> ReadableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MinMemoryMb"] = new("最小内存分配 (MB)", c => c.MinMemoryMb.ToString()),
        ["MaxMemoryMb"] = new("最大内存分配 (MB)", c => c.MaxMemoryMb.ToString()),
        ["Source"] = new("下载源", c => c.Source == DownloadSource.BMCLAPI ? "BMCLAPI 镜像源" : "官方源 (Mojang)"),
        ["EnableMultiThreadDownload"] = new("多线程下载开关", c => c.EnableMultiThreadDownload ? "已开启" : "已关闭"),
        ["MaxDownloadThreads"] = new("下载线程数上限", c => c.MaxDownloadThreads.ToString()),
        ["DownloadSpeedLimitKBps"] = new("下载限速 (KB/s，0=不限速)", c => c.DownloadSpeedLimitKBps.ToString()),
        ["SmartBandwidthThrottle"] = new("智能带宽调节", c => c.SmartBandwidthThrottle ? "已开启" : "已关闭"),
        ["EnableInjectionScan"] = new("注入扫描开关", c => c.EnableInjectionScan ? "已开启" : "已关闭"),
        ["EnablePageAnimations"] = new("页面动画", c => c.EnablePageAnimations ? "已开启" : "已关闭"),
        ["SettingsAutoSaveWithoutConfirm"] = new("设置自动保存", c => c.SettingsAutoSaveWithoutConfirm ? "已开启" : "已关闭"),
        ["MouseWheelSensitivityPercent"] = new("鼠标滚轮灵敏度 (%)", c => c.MouseWheelSensitivityPercent.ToString()),
        ["TextOpacityPercent"] = new("文字透明度 (%)", c => c.TextOpacityPercent.ToString()),
        ["EnforceJavaVersionMatch"] = new("强制使用匹配 Java", c => c.EnforceJavaVersionMatch ? "已开启" : "已关闭"),
        ["GameLanguage"] = new("游戏内语言", c => c.GameLanguage),
        ["LauncherLanguage"] = new("启动器界面语言", c => c.LauncherLanguage),
        // 下面两个只读不可写：改动入口只留在设置页本身，AI 不能替用户开关。
        ["GuestModeEnabled"] = new("访客模式", c => c.GuestModeEnabled ? "已开启" : "已关闭"),
        ["ExperimentalFeaturesUnlocked"] = new("实验性功能解锁", c => c.ExperimentalFeaturesUnlocked ? "已开启" : "已关闭"),
    };

    // 可写：readable 的子集，且都带类型/范围校验。
    private static readonly Dictionary<string, WritableKey> WritableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MinMemoryMb"] = new("最小内存分配", (c, v) =>
        {
            if (!int.TryParse(v, out var mb) || mb < 256 || mb > 65536)
                return (false, "内存值必须是 256~65536 之间的整数 (MB)。", null);
            if (mb > c.MaxMemoryMb)
                return (false, $"最小内存不能大于当前最大内存 ({c.MaxMemoryMb} MB)。", null);
            c.MinMemoryMb = mb;
            return (true, null, $"{mb} MB");
        }),
        ["MaxMemoryMb"] = new("最大内存分配", (c, v) =>
        {
            if (!int.TryParse(v, out var mb) || mb < 256 || mb > 65536)
                return (false, "内存值必须是 256~65536 之间的整数 (MB)。", null);
            if (mb < c.MinMemoryMb)
                return (false, $"最大内存不能小于当前最小内存 ({c.MinMemoryMb} MB)。", null);
            c.MaxMemoryMb = mb;
            return (true, null, $"{mb} MB");
        }),
        ["Source"] = new("下载源", (c, v) =>
        {
            if (string.Equals(v, "BMCLAPI", StringComparison.OrdinalIgnoreCase) || v.Contains("镜像"))
            {
                c.Source = DownloadSource.BMCLAPI;
                return (true, null, "BMCLAPI 镜像源");
            }
            if (string.Equals(v, "Official", StringComparison.OrdinalIgnoreCase) || v.Contains("官方"))
            {
                c.Source = DownloadSource.Official;
                return (true, null, "官方源 (Mojang)");
            }
            return (false, "下载源取值只能是 Official 或 BMCLAPI。", null);
        }),
        ["EnableMultiThreadDownload"] = new("多线程下载开关", (c, v) =>
        {
            if (!bool.TryParse(v, out var b)) return (false, "取值必须是 true 或 false。", null);
            c.EnableMultiThreadDownload = b;
            return (true, null, b ? "已开启" : "已关闭");
        }),
        ["MaxDownloadThreads"] = new("下载线程数上限", (c, v) =>
        {
            if (!int.TryParse(v, out var n) || n < 1 || n > 64)
                return (false, "下载线程数必须是 1~64 之间的整数。", null);
            c.MaxDownloadThreads = n;
            return (true, null, n.ToString());
        }),
        ["DownloadSpeedLimitKBps"] = new("下载限速", (c, v) =>
        {
            if (!int.TryParse(v, out var n) || n < 0 || n > 10_000_000)
                return (false, "下载限速必须是 0~10000000 之间的整数 (KB/s)，0 表示不限速。", null);
            c.DownloadSpeedLimitKBps = n;
            return (true, null, n == 0 ? "不限速" : $"{n} KB/s");
        }),
        ["SmartBandwidthThrottle"] = new("智能带宽调节", (c, v) =>
        {
            if (!bool.TryParse(v, out var b)) return (false, "取值必须是 true 或 false。", null);
            c.SmartBandwidthThrottle = b;
            return (true, null, b ? "已开启" : "已关闭");
        }),
        ["EnablePageAnimations"] = new("页面动画", (c, v) =>
        {
            if (!bool.TryParse(v, out var b)) return (false, "取值必须是 true 或 false。", null);
            c.EnablePageAnimations = b;
            return (true, null, b ? "已开启" : "已关闭");
        }),
        ["SettingsAutoSaveWithoutConfirm"] = new("设置自动保存", (c, v) =>
        {
            if (!bool.TryParse(v, out var b)) return (false, "取值必须是 true 或 false。", null);
            c.SettingsAutoSaveWithoutConfirm = b;
            return (true, null, b ? "已开启" : "已关闭");
        }),
        ["MouseWheelSensitivityPercent"] = new("鼠标滚轮灵敏度", (c, v) =>
        {
            if (!int.TryParse(v, out var n) || n < ScrollWheelBehavior.MinSensitivityPercent || n > ScrollWheelBehavior.MaxSensitivityPercent)
                return (false, $"滚轮灵敏度必须是 {ScrollWheelBehavior.MinSensitivityPercent}~{ScrollWheelBehavior.MaxSensitivityPercent} 之间的整数百分比。", null);
            c.MouseWheelSensitivityPercent = n;
            return (true, null, $"{n}%");
        }),
        ["TextOpacityPercent"] = new("文字透明度", (c, v) =>
        {
            if (!int.TryParse(v, out var n) || n < 50 || n > 100)
                return (false, "文字透明度必须是 50~100 之间的整数百分比。", null);
            c.TextOpacityPercent = n;
            return (true, null, $"{n}%");
        }),
        ["EnforceJavaVersionMatch"] = new("强制使用匹配 Java", (c, v) =>
        {
            if (!bool.TryParse(v, out var b)) return (false, "取值必须是 true 或 false。", null);
            c.EnforceJavaVersionMatch = b;
            return (true, null, b ? "已开启" : "已关闭");
        }),
        ["EnableInjectionScan"] = new("注入扫描开关", (c, v) =>
        {
            if (!bool.TryParse(v, out var b)) return (false, "取值必须是 true 或 false。", null);
            c.EnableInjectionScan = b;
            return (true, null, b ? "已开启" : "已关闭");
        }),
    };

    public delegate bool ConfirmCallback(string title, string message);

    private const int MaxWorkspaceReadChars = 40000;
    private const int MaxWorkspaceWriteChars = 200000;

    private static bool TryGetWorkspaceRoot(out string root, out string error)
    {
        root = "";
        error = "";
        var configured = ConfigService.Active?.Config.AiAssistant?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            error = "用户尚未在 AI 设置中指定工作目录，文件工具处于关闭状态。";
            return false;
        }
        try
        {
            root = Path.GetFullPath(configured);
            if (!Directory.Exists(root))
            {
                error = "设置的 AI 工作目录已经不存在，请用户重新选择目录。";
                return false;
            }
            return true;
        }
        catch
        {
            error = "AI 工作目录路径无效，请用户重新选择目录。";
            return false;
        }
    }

    private static bool TryResolveWorkspacePath(string? relativePath, bool allowRoot, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (!TryGetWorkspaceRoot(out var root, out error)) return false;

        var rel = (relativePath ?? "").Trim();
        if (rel.Length == 0 && allowRoot)
        {
            fullPath = root;
            return true;
        }
        if (rel.Length == 0)
        {
            error = "没有提供相对路径。";
            return false;
        }
        if (Path.IsPathRooted(rel))
        {
            error = "只允许使用相对于工作目录的路径，不能使用绝对路径。";
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(root, rel));
            var rootWithSlash = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                error = "路径试图越过 AI 工作目录边界，已拒绝。";
                return false;
            }
            fullPath = candidate;
            return true;
        }
        catch
        {
            error = "相对路径无效。";
            return false;
        }
    }

    private static string WorkspaceRelative(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    /// <summary>
    /// 处理一次模型回复：如果末尾带访问请求标记，摘掉标记、弹确认框、按结果执行或拒绝，
    /// 返回"展示给用户的正文"和"要不要再补一轮请求把结果喂回给模型"。
    /// confirm 由调用方（UI 层）传入，方便在非 WPF 环境（比如单元测试）里替换成假实现。
    /// </summary>
    public static (string DisplayText, string? FollowUpForModel) ProcessReply(string replyText, ConfirmCallback confirm)
    {
        var match = MarkerRegex.Match(replyText ?? "");
        if (!match.Success)
            return (replyText ?? "", null);

        var displayText = MarkerRegex.Replace(replyText!, "").TrimEnd();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            root = doc.RootElement.Clone();
        }
        catch
        {
            // 标记格式不对：当成没请求，只是把标记摘掉，不阻断正常回复。
            return (displayText, null);
        }

        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
        var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
        var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;

        switch (action)
        {
            case "read_log":
            {
                const string msg = "AI 助手想读取【本次启动器运行期间的启动器日志】（不是游戏日志，也不是崩溃报告）来帮你排查问题。\n\n是否同意？";
                if (!confirm("AI 请求访问数据", msg))
                    return (displayText, "用户拒绝了读取启动器日志的请求，未执行任何操作。请在不依赖日志内容的前提下继续回答，或直接说明缺少这部分信息就无法进一步判断。");

                var log = LauncherLogService.GetBufferedText();
                if (string.IsNullOrWhiteSpace(log)) log = "(本次会话日志目前是空的)";
                // 日志可能很长，只把最后一段喂回去，避免一次占满上下文。
                const int maxChars = 4000;
                if (log.Length > maxChars) log = "……（前面部分已省略）\n" + log[^maxChars..];
                return (displayText, $"系统：用户已同意，以下是本次启动器运行日志（可能已截断到最后 {maxChars} 字）：\n```\n{log}\n```\n请基于以上内容继续之前的回答。");
            }

            case "read_config":
            {
                if (key == null || !ReadableKeys.TryGetValue(key, out var readable))
                    return (displayText, $"系统：请求读取的配置项 \"{key}\" 不在允许查询的范围内，未执行。请只依据你已有的知识回答，或说明这项需要用户自己去设置页确认。");

                var msg = $"AI 助手想查看当前的【{readable.Label}】设置值。\n\n是否同意？";
                if (!confirm("AI 请求访问数据", msg))
                    return (displayText, $"用户拒绝了查看【{readable.Label}】当前值的请求，未执行任何操作。请继续回答，但不要假设这个值是多少。");

                var cfg = ConfigService.Active?.Config;
                if (cfg == null)
                    return (displayText, "系统：当前无法访问启动器配置（未初始化），未执行。");

                var currentValue = readable.Get(cfg);
                return (displayText, $"系统：用户已同意，【{readable.Label}】当前值为：{currentValue}。请基于这个值继续之前的回答。");
            }

            case "apply_setting":
            {
                if (key == null || !WritableKeys.TryGetValue(key, out var writable))
                    return (displayText, $"系统：请求修改的设置项 \"{key}\" 不在 AI 可代为修改的范围内，未执行。请告诉用户需要自己到设置页里改这一项。");

                var msg = $"AI 助手想把【{writable.Label}】改成：{value}\n\n是否同意？（同意后立即生效并保存）";
                if (!confirm("AI 请求修改设置", msg))
                    return (displayText, $"用户拒绝了修改【{writable.Label}】的请求，未执行任何操作。设置保持原样，请照此回复用户。");

                var cfg = ConfigService.Active;
                if (cfg == null)
                    return (displayText, "系统：当前无法访问启动器配置（未初始化），未执行。");

                var (ok, error, applied) = writable.TryApply(cfg.Config, value ?? "");
                if (!ok)
                    return (displayText, $"系统：用户同意了，但这个值不合法，没有修改成功（原因：{error}）。请告诉用户具体原因，可以请他提供一个合法值再试一次，或引导去设置页手动改。");

                cfg.Save();
                // 只对有明确、无副作用的全局视觉/输入设置做即时预览；其余设置下次相关流程读取配置时生效。
                if (string.Equals(key, "MouseWheelSensitivityPercent", StringComparison.OrdinalIgnoreCase))
                    ScrollWheelBehavior.SetSensitivityPercent(cfg.Config.MouseWheelSensitivityPercent);
                else if (string.Equals(key, "TextOpacityPercent", StringComparison.OrdinalIgnoreCase))
                    ThemeService.ApplyTextOpacity(cfg.Config.TextOpacityPercent);

                return (displayText, $"系统：已按用户授权把【{writable.Label}】改为 {applied} 并保存。请照此回复用户，确认这项设置已经生效。");
            }

            case "list_workspace":
            {
                if (!TryResolveWorkspacePath(path, allowRoot: true, out var folder, out var pathError))
                    return (displayText, $"系统：无法列出工作目录：{pathError}");
                if (!Directory.Exists(folder))
                    return (displayText, "系统：请求列出的路径不是目录或已经不存在。");
                if (!confirm("AI 请求访问工作目录", $"AI 助手想列出工作目录中的文件：\n{folder}\n\n是否同意？"))
                    return (displayText, "用户拒绝了列出工作目录的请求。请不要假设目录中有哪些文件。");

                var workspaceRoot = Path.GetFullPath(ConfigService.Active!.Config.AiAssistant!.WorkingDirectory!);
                var entries = Directory.EnumerateFileSystemEntries(folder)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(200)
                    .Select(x => (Directory.Exists(x) ? "[目录] " : "[文件] ") + WorkspaceRelative(workspaceRoot, x))
                    .ToArray();
                var text = entries.Length == 0 ? "(目录为空)" : string.Join("\n", entries);
                return (displayText, $"系统：用户已同意。工作目录内容（最多 200 项）：\n{text}\n请据此继续；仍不能访问工作目录以外的路径。");
            }

            case "read_workspace_file":
            {
                if (!TryResolveWorkspacePath(path, allowRoot: false, out var file, out var pathError))
                    return (displayText, $"系统：无法读取工作目录文件：{pathError}");
                if (!File.Exists(file))
                    return (displayText, "系统：请求读取的文件不存在。");
                if (!confirm("AI 请求读取工作目录文件", $"AI 助手想读取：\n{file}\n\n是否同意？"))
                    return (displayText, "用户拒绝了读取该工作目录文件的请求。请不要猜测文件内容。");
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) { return (displayText, $"系统：读取文件失败：{ex.Message}"); }
                if (text.Length > MaxWorkspaceReadChars)
                    text = text[..MaxWorkspaceReadChars] + $"\n……（文件过长，仅提供前 {MaxWorkspaceReadChars} 字符）";
                return (displayText, $"系统：用户已同意读取文件 {path}。内容如下：\n```text\n{text}\n```\n请基于内容继续回答。若要修改，必须另行发起写入请求并再次获得确认。");
            }

            case "write_workspace_file":
            {
                if (!TryResolveWorkspacePath(path, allowRoot: false, out var file, out var pathError))
                    return (displayText, $"系统：无法写入工作目录文件：{pathError}");
                if (content == null)
                    return (displayText, "系统：写入请求缺少 content，未执行。");
                if (content.Length > MaxWorkspaceWriteChars)
                    return (displayText, $"系统：单次写入超过 {MaxWorkspaceWriteChars} 字符上限，未执行。请缩小修改范围。");

                var existed = File.Exists(file);
                var verb = existed ? "覆盖" : "创建";
                if (!confirm("AI 请求写入工作目录", $"AI 助手想{verb}文件：\n{file}\n\n内容长度：{content.Length} 字符。是否同意？"))
                    return (displayText, $"用户拒绝了{verb}文件的请求，磁盘内容保持不变。");
                try
                {
                    var dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch (Exception ex)
                {
                    return (displayText, $"系统：用户同意了，但写入失败：{ex.Message}");
                }
                return (displayText, $"系统：已按用户授权{verb}工作目录文件：{path}。请确认操作完成，并说明修改的内容。不要声称执行了编译/测试，除非用户另外提供了实际结果。");
            }

            default:
                // 未知 action：当成没有这个能力，摘掉标记就好，不打断正常回复。
                return (displayText, null);
        }
    }
}
