using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// D 需求：让 AI 助手能在用户明确批准后，读取启动器日志/配置项含义、或修改一个已知的安全设置项。
/// 项目里原来完全没有这套机制，这里是第一版，设计上刻意收得很紧，边界如下（后续要放宽再改）：
///
/// 1) 范围限制——只认白名单，不认路径：
///    - 读日志：不接受 AI 传来的任意文件名/路径，只允许读"当前这次启动器运行会话"的内存日志缓冲
///      （<see cref="LauncherLogService.GetBufferedText"/>），不触碰磁盘上任何其它文件。崩溃文件分析
///      走的是日志页"用户主动选择并授权"的既有流程，不归这里管，两者不混用。
///    - 读/改配置：只认 <see cref="ReadableKeys"/> / <see cref="WritableKeys"/> 里显式登记的字段名，
///      AI 传别的字段名一律拒绝，不做反射式"读者传什么就查什么"。可写字段是可读字段的子集
///      （更敏感的字段——比如访客模式、实验性功能解锁——只读不可写，改动这些的正确入口始终是设置页本身）。
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
        @"<<XCL2_ACCESS:(\{.*?\})>>", RegexOptions.Singleline | RegexOptions.Compiled);

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
        ["EnableInjectionScan"] = new("注入扫描开关", c => c.EnableInjectionScan ? "已开启" : "已关闭"),
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
            if (!int.TryParse(v, out var n) || n < 1 || n > 32)
                return (false, "下载线程数必须是 1~32 之间的整数。", null);
            c.MaxDownloadThreads = n;
            return (true, null, n.ToString());
        }),
        ["EnableInjectionScan"] = new("注入扫描开关", (c, v) =>
        {
            if (!bool.TryParse(v, out var b)) return (false, "取值必须是 true 或 false。", null);
            c.EnableInjectionScan = b;
            return (true, null, b ? "已开启" : "已关闭");
        }),
    };

    public delegate bool ConfirmCallback(string title, string message);

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
                return (displayText, $"系统：已按用户授权把【{writable.Label}】改为 {applied} 并保存。请照此回复用户，确认这项设置已经生效。");
            }

            default:
                // 未知 action：当成没有这个能力，摘掉标记就好，不打断正常回复。
                return (displayText, null);
        }
    }
}
