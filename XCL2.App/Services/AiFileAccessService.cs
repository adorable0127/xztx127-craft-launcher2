using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Win32;
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

    // ===== 多目录 / 完全访问 =====

    /// <summary>当前已批准且仍存在的目录（主目录在前）。</summary>
    public static List<string> GetApprovedRoots()
    {
        var result = new List<string>();
        var ai = ConfigService.Active?.Config.AiAssistant;
        if (ai == null) return result;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(ai.WorkingDirectory)) candidates.Add(ai.WorkingDirectory!);
        if (ai.WorkingDirectories != null) candidates.AddRange(ai.WorkingDirectories);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                var full = Path.GetFullPath(c.Trim());
                if (Directory.Exists(full) && !result.Contains(full, StringComparer.OrdinalIgnoreCase))
                    result.Add(full);
            }
            catch { /* 无效路径直接忽略 */ }
        }
        return result;
    }

    public static bool IsFullAccess()
        => ConfigService.Active?.Config.AiAssistant?.FullDiskAccess == true;

    private static bool IsInside(string root, string candidate)
    {
        var rootWithSlash = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>系统关键目录：即使完全访问，也不允许 AI 写入（读取仍需用户逐次确认）。</summary>
    private static bool IsProtectedForWrite(string fullPath)
    {
        if (IsUnrestrictedAccess()) return false; // 完全绝对访问：不保护任何路径
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        return protectedRoots.Any(r => !string.IsNullOrWhiteSpace(r) && IsInside(Path.GetFullPath(r), fullPath));
    }

    private static bool TryResolveWorkspacePath(string? inputPath, bool allowRoot, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        var roots = GetApprovedRoots();
        var full = IsFullAccess();
        if (roots.Count == 0 && !full)
        {
            error = "用户尚未在 AI 设置中批准任何目录，文件工具处于关闭状态。可以用 request_directory 向用户申请一个目录。";
            return false;
        }

        var raw = (inputPath ?? "").Trim();
        if (raw.Length == 0)
        {
            if (!allowRoot) { error = "没有提供路径。"; return false; }
            if (roots.Count == 0) { error = "没有主目录，请提供绝对路径。"; return false; }
            fullPath = roots[0];
            return true;
        }

        try
        {
            var candidate = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(roots.Count > 0 ? roots[0] : Environment.CurrentDirectory, raw));

            if (!full && !roots.Any(r => IsInside(r, candidate)))
            {
                error = "路径不在已批准的目录内，已拒绝。可以用 request_directory 向用户申请该目录。";
                return false;
            }
            fullPath = candidate;
            return true;
        }
        catch
        {
            error = "路径无效。";
            return false;
        }
    }

    /// <summary>绝对访问是否生效：必须同时开启完全访问。</summary>
    public static bool IsAbsoluteAccess()
    {
        var ai = ConfigService.Active?.Config.AiAssistant;
        return ai != null && ai.FullDiskAccess && ai.AbsoluteAccess;
    }

    /// <summary>彻底完全访问是否生效：必须同时开启完全访问、绝对访问、完全绝对访问。生效后 AI 可改注册表、执行系统命令。</summary>
    public static bool IsSystemControlAccess()
    {
        var ai = ConfigService.Active?.Config.AiAssistant;
        return ai != null && ai.FullDiskAccess && ai.AbsoluteAccess && ai.UnrestrictedAccess && ai.SystemControlAccess;
    }

    /// <summary>完全绝对访问是否生效：必须同时开启完全访问和绝对访问。生效后不再有任何受保护路径。</summary>
    public static bool IsUnrestrictedAccess()
    {
        var ai = ConfigService.Active?.Config.AiAssistant;
        return ai != null && ai.FullDiskAccess && ai.AbsoluteAccess && ai.UnrestrictedAccess;
    }

    /// <summary>启动器自己的配置/账户/密钥所在位置（含 API 密钥、登录令牌）：
    /// 即使是绝对访问，也不允许静默读写，仍然走弹窗确认。</summary>
    private static bool IsLauncherPrivatePath(string fullPath)
    {
        try
        {
            var privateRoots = new List<string> { App.DataDir, AppContext.BaseDirectory + "XCL2" };
            var cfg = ConfigService.Active;
            if (cfg != null)
            {
                foreach (var p in new[] { cfg.ConfigPath, cfg.MirrorConfigPath, cfg.LegacyMirrorConfigPath, cfg.LegacyConfigPath })
                {
                    var dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrWhiteSpace(dir)) privateRoots.Add(dir);
                }
            }
            return privateRoots.Any(r => !string.IsNullOrWhiteSpace(r) && IsInside(Path.GetFullPath(r), fullPath));
        }
        catch { return true; /* 判断不了就按敏感处理，退回弹窗确认 */ }
    }

    private static void AuditLog(string message)
    {
        try { LauncherLogService.AppendLine("[AI 绝对访问] " + message); } catch { }
    }

    private static void AuditSystemLog(string message)
    {
        try { LauncherLogService.AppendLine("[AI 彻底完全访问] " + message); } catch { }
    }

    private static void AuditFullAccessLog(string message)
    {
        try { LauncherLogService.AppendLine("[AI 完全访问] " + message); } catch { }
    }

    /// <summary>文件类操作的统一确认入口：绝对访问下静默通过并记日志；否则弹窗。</summary>
    private static bool ConfirmFileAction(ConfirmCallback confirm, string title, string message, string logAction, string targetPath, bool isWrite = false)
    {
        if (IsAbsoluteAccess() && (IsUnrestrictedAccess() || !IsLauncherPrivatePath(targetPath)))
        {
            AuditLog($"{logAction}：{targetPath}（未弹窗，{(IsUnrestrictedAccess() ? "完全绝对访问" : "绝对访问")}已授权）");
            return true;
        }
        // 完全访问（未开启绝对访问）：读取/列出这类低风险操作不再弹窗，仅记日志；
        // 写入仍逐次确认。启动器自己的配置/账户目录（含密钥）读取仍然弹窗。
        if (!isWrite && IsFullAccess() && !IsLauncherPrivatePath(targetPath))
        {
            AuditFullAccessLog($"{logAction}：{targetPath}（未弹窗，完全访问下读取免确认）");
            return true;
        }
        return confirm(title, message);
    }

    private static bool LooksBinary(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var buf = new byte[Math.Min(4096, (int)Math.Min(fs.Length, 4096))];
            var n = fs.Read(buf, 0, buf.Length);
            for (var i = 0; i < n; i++) if (buf[i] == 0) return true;
        }
        catch { }
        return false;
    }

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

            case "request_directory":
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                    return (displayText, "系统：request_directory 需要提供一个绝对路径，未执行。");
                string dirFull;
                try { dirFull = Path.GetFullPath(path.Trim()); }
                catch { return (displayText, "系统：目录路径无效，未执行。"); }
                if (!Directory.Exists(dirFull))
                    return (displayText, "系统：该目录不存在，未执行。请让用户确认路径。");
                var aiCfg = ConfigService.Active?.Config.AiAssistant;
                if (aiCfg == null)
                    return (displayText, "系统：当前无法访问启动器配置（未初始化），未执行。");
                // 防止绕过"完全访问"的两次确认：磁盘根目录、用户主目录、系统关键目录
                // 这类"等同于整台电脑"的范围，只能由用户在设置里开启完全访问来授予。
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var tooBroad = string.Equals(dirFull.TrimEnd('\\', '/'), Path.GetPathRoot(dirFull)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(userProfile) && string.Equals(dirFull.TrimEnd('\\', '/'), Path.GetFullPath(userProfile).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    || IsProtectedForWrite(dirFull);
                if (tooBroad && !IsFullAccess())
                    return (displayText, "系统：该目录范围过大（磁盘根目录、用户主目录或系统目录），不能通过申请授予。请申请更具体的子目录，或请用户自己在 AI 设置里开启「完全访问」。");

                if (GetApprovedRoots().Any(r => IsInside(r, dirFull)))
                    return (displayText, $"系统：该目录已经在已批准范围内：{dirFull}，无需再次申请，可直接使用。");

                var reason = string.IsNullOrWhiteSpace(content) ? "（AI 未说明原因）" : content.Trim();
                if (!confirm("AI 请求使用目录",
                        $"AI 助手申请把下面这个目录加入可访问范围：\n{dirFull}\n\n理由：{reason}\n\n同意后，AI 之后可以在这个目录内发起列出/读取/写入请求（每次具体操作仍会再弹窗确认）。你可以随时在 AI 设置里移除。\n\n是否同意？"))
                    return (displayText, "用户拒绝了这个目录的使用申请，未做任何改动。请不要再次申请同一个目录，也不要猜测里面的内容。");

                aiCfg.WorkingDirectories ??= new List<string>();
                aiCfg.WorkingDirectories.Add(dirFull);
                ConfigService.Active!.Save();
                return (displayText, $"系统：用户已批准目录 {dirFull}。现在可以在其中发起 list_workspace / read_workspace_file / write_workspace_file 请求（传绝对路径），每次操作仍会单独确认。");
            }

            case "list_workspace":
            {
                var roots = GetApprovedRoots();
                var rawPath = (path ?? "").Trim();

                // 不带路径：多目录/完全访问时先告诉 AI 有哪些入口，而不是只列主目录。
                if (rawPath.Length == 0 && (roots.Count > 1 || (roots.Count == 0 && IsFullAccess())))
                {
                    if (!ConfirmFileAction(confirm, "AI 请求访问目录", roots.Count > 1
                            ? "AI 助手想查看已批准的目录列表。\n\n是否同意？"
                            : "AI 助手想查看这台电脑的磁盘列表。\n\n是否同意？",
                            "查看目录入口列表", roots.Count > 1 ? "(已批准目录)" : "(磁盘列表)"))
                        return (displayText, "用户拒绝了这次请求。请不要假设有哪些目录。");
                    string[] roots2 = roots.Count > 1
                        ? roots.ToArray()
                        : DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name).ToArray();
                    return (displayText, "系统：用户已同意。可用入口：\n" + string.Join("\n", roots2.Select(r => "[目录] " + r)) +
                        "\n请用绝对路径继续。");
                }

                if (!TryResolveWorkspacePath(path, allowRoot: true, out var folder, out var pathError))
                    return (displayText, $"系统：无法列出目录：{pathError}");
                if (!Directory.Exists(folder))
                    return (displayText, "系统：请求列出的路径不是目录或已经不存在。");
                if (!ConfirmFileAction(confirm, "AI 请求访问目录", $"AI 助手想列出目录中的文件：\n{folder}\n\n是否同意？", "列出目录", folder))
                    return (displayText, "用户拒绝了列出目录的请求。请不要假设目录中有哪些文件。");

                string[] entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(folder)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Take(200)
                        .Select(x => (Directory.Exists(x) ? "[目录] " : "[文件] ") + x)
                        .ToArray();
                }
                catch (Exception ex) { return (displayText, $"系统：用户已同意，但读取目录失败：{ex.Message}"); }
                var text = entries.Length == 0 ? "(目录为空)" : string.Join("\n", entries);
                return (displayText, $"系统：用户已同意。目录内容（最多 200 项）：\n{text}\n请据此继续；仍只能访问已批准的范围{(IsFullAccess() ? "（当前为完全访问模式）" : "")}。");
            }

            case "read_workspace_file":
            {
                if (!TryResolveWorkspacePath(path, allowRoot: false, out var file, out var pathError))
                    return (displayText, $"系统：无法读取文件：{pathError}");
                if (!File.Exists(file))
                    return (displayText, "系统：请求读取的文件不存在。");
                if (!ConfirmFileAction(confirm, "AI 请求读取文件", $"AI 助手想读取：\n{file}\n\n是否同意？", "读取文件", file))
                    return (displayText, "用户拒绝了读取该文件的请求。请不要猜测文件内容。");
                if (LooksBinary(file))
                    return (displayText, "系统：该文件看起来是二进制文件，不提供读取。请只处理文本文件。");
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex)
                {
                    if (IsAbsoluteAccess()) AuditLog($"读取失败：{file}（{ex.Message}）");
                    return (displayText, $"系统：读取文件失败：{ex.Message}");
                }
                if (IsAbsoluteAccess()) AuditLog($"读取完成：{file}（{text.Length} 字符）");
                if (text.Length > MaxWorkspaceReadChars)
                    text = text[..MaxWorkspaceReadChars] + $"\n……（文件过长，仅提供前 {MaxWorkspaceReadChars} 字符）";
                return (displayText, $"系统：用户已同意读取文件 {path}。内容如下：\n```text\n{text}\n```\n请基于内容继续回答。若要修改，必须另行发起写入请求并再次获得确认。");
            }

            case "write_workspace_file":
            {
                if (!TryResolveWorkspacePath(path, allowRoot: false, out var file, out var pathError))
                    return (displayText, $"系统：无法写入文件：{pathError}");
                if (content == null)
                    return (displayText, "系统：写入请求缺少 content，未执行。");
                if (content.Length > MaxWorkspaceWriteChars)
                    return (displayText, $"系统：单次写入超过 {MaxWorkspaceWriteChars} 字符上限，未执行。请缩小修改范围。");

                if (IsProtectedForWrite(file))
                {
                    if (IsAbsoluteAccess()) AuditLog($"已拒绝写入系统目录：{file}");
                }
                if (IsProtectedForWrite(file))
                    return (displayText, "系统：该位置属于系统关键目录（Windows / Program Files / ProgramData），即使在完全访问模式下也不允许写入（需要用户在设置里开启最高级别的「完全绝对访问」才可修改），未执行。");

                var existed = File.Exists(file);
                var verb = existed ? "覆盖" : "创建";
                if (!ConfirmFileAction(confirm, "AI 请求写入文件", $"AI 助手想{verb}文件：\n{file}\n\n内容长度：{content.Length} 字符。是否同意？", $"{verb}文件（{content.Length} 字符）", file, isWrite: true))
                    return (displayText, $"用户拒绝了{verb}文件的请求，磁盘内容保持不变。");
                try
                {
                    var dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch (Exception ex)
                {
                    if (IsAbsoluteAccess()) AuditLog($"{verb}失败：{file}（{ex.Message}）");
                    return (displayText, $"系统：用户同意了，但写入失败：{ex.Message}");
                }
                if (IsAbsoluteAccess()) AuditLog($"{verb}完成：{file}（{content.Length} 字符）");
                return (displayText, $"系统：已按用户授权{verb}工作目录文件：{path}。请确认操作完成，并说明修改的内容。不要声称执行了编译/测试，除非用户另外提供了实际结果。");
            }

            case "registry_read":
            case "registry_write":
            case "run_command":
            {
                if (!IsSystemControlAccess())
                    return (displayText, "系统：注册表与系统命令能力未开启，未执行。需要用户在 AI 设置里依次开启「完全访问 → 绝对访问 → 完全绝对访问 → 彻底完全访问」。请告诉用户这一点，不要尝试用别的方式绕过。");
                try
                {
                    return action switch
                    {
                        "registry_read" => (displayText, RegistryRead(path, key ?? value)),
                        "registry_write" => (displayText, RegistryWrite(path, key, value, root.TryGetProperty("type", out var tp) ? tp.GetString() : null)),
                        _ => (displayText, RunSystemCommand(
                            root.TryGetProperty("shell", out var sh) ? sh.GetString() : null,
                            root.TryGetProperty("command", out var cm) ? cm.GetString() : content,
                            root.TryGetProperty("timeout_seconds", out var to) && to.ValueKind == JsonValueKind.Number ? to.GetInt32() : 30))
                    };
                }
                catch (Exception ex)
                {
                    AuditSystemLog($"{action} 失败：{ex.Message}");
                    return (displayText, $"系统：{action} 执行失败：{ex.Message}");
                }
            }

            default:
                // 未知 action：当成没有这个能力，摘掉标记就好，不打断正常回复。
                return (displayText, null);
        }
    }

    // ===== 彻底完全访问：注册表 / 系统命令 =====

    private const int MaxCommandOutputChars = 8000;

    private static bool TryOpenRegistryKey(string? fullPath, bool writable, bool create, out RegistryKey? key, out string? subPath, out string error)
    {
        key = null; subPath = null; error = "";
        if (string.IsNullOrWhiteSpace(fullPath)) { error = "没有提供注册表路径。"; return false; }
        var parts = fullPath.Trim().Trim('\\').Split('\\', 2);
        var hiveName = parts[0].ToUpperInvariant();
        subPath = parts.Length > 1 ? parts[1] : "";
        Microsoft.Win32.RegistryHive? hive = hiveName switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Microsoft.Win32.RegistryHive.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Microsoft.Win32.RegistryHive.LocalMachine,
            "HKEY_CLASSES_ROOT" or "HKCR" => Microsoft.Win32.RegistryHive.ClassesRoot,
            "HKEY_USERS" or "HKU" => Microsoft.Win32.RegistryHive.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => Microsoft.Win32.RegistryHive.CurrentConfig,
            _ => null
        };
        if (hive == null) { error = "注册表路径必须以 HKEY_CURRENT_USER / HKEY_LOCAL_MACHINE / HKEY_CLASSES_ROOT / HKEY_USERS / HKEY_CURRENT_CONFIG（或 HKCU/HKLM/HKCR/HKU/HKCC）开头。"; return false; }
        using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
        key = subPath.Length == 0 ? baseKey.OpenSubKey("", writable)
            : create ? baseKey.CreateSubKey(subPath, writable) : baseKey.OpenSubKey(subPath, writable);
        if (key == null) { error = "注册表项不存在或无权访问。"; return false; }
        return true;
    }

    private static string RegistryRead(string? path, string? valueName)
    {
        if (!TryOpenRegistryKey(path, false, false, out var key, out _, out var error))
            return $"系统：读取注册表失败：{error}";
        using (key)
        {
            if (!string.IsNullOrEmpty(valueName))
            {
                var v = key!.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (v == null) return $"系统：注册表值不存在：{path} -> {valueName}";
                var kind = key.GetValueKind(valueName);
                AuditSystemLog($"读取注册表值：{path} -> {valueName}");
                return $"系统：{path} -> {valueName}（{kind}）= {FormatRegistryValue(v)}";
            }
            var names = key!.GetValueNames().Take(100).Select(n => (n.Length == 0 ? "(默认)" : n) + $"（{key.GetValueKind(n)}）");
            var subs = key.GetSubKeyNames().Take(100).Select(n => "[子项] " + n);
            AuditSystemLog($"列出注册表项：{path}");
            return $"系统：{path}\n值：\n{string.Join("\n", names)}\n子项：\n{string.Join("\n", subs)}\n（各最多 100 项）";
        }
    }

    private static string FormatRegistryValue(object v) => v switch
    {
        byte[] b => BitConverter.ToString(b.Take(256).ToArray()) + (b.Length > 256 ? "…" : ""),
        string[] arr => string.Join(" | ", arr),
        _ => v.ToString() ?? ""
    };

    private static string RegistryWrite(string? path, string? valueName, string? value, string? type)
    {
        if (valueName == null) return "系统：registry_write 缺少值名称（key 字段，默认值请传空字符串）。";
        if (!TryOpenRegistryKey(path, true, true, out var key, out _, out var error))
            return $"系统：写入注册表失败：{error}（修改 HKEY_LOCAL_MACHINE 通常需要以管理员身份运行启动器）";
        using (key)
        {
            var text = value ?? "";
            switch ((type ?? "String").ToLowerInvariant())
            {
                case "dword": key!.SetValue(valueName, unchecked((int)uint.Parse(text)), RegistryValueKind.DWord); break;
                case "qword": key!.SetValue(valueName, unchecked((long)ulong.Parse(text)), RegistryValueKind.QWord); break;
                case "expandstring": key!.SetValue(valueName, text, RegistryValueKind.ExpandString); break;
                case "multistring": key!.SetValue(valueName, text.Split('\n'), RegistryValueKind.MultiString); break;
                case "string": key!.SetValue(valueName, text, RegistryValueKind.String); break;
                default: return "系统：type 只能是 String / ExpandString / MultiString / DWord / QWord。";
            }
        }
        AuditSystemLog($"写入注册表：{path} -> {valueName} = {value}（{type ?? "String"}）");
        return $"系统：已写入注册表 {path} -> {valueName}。请向用户说明修改了什么、为什么，以及如何还原。";
    }

    private static string RunSystemCommand(string? shell, string? command, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command)) return "系统：run_command 缺少 command。";
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 120);
        var useCmd = string.Equals(shell, "cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = useCmd ? "cmd.exe" : "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        if (useCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
        else
        {
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(command);
        }
        AuditSystemLog($"执行命令（{(useCmd ? "cmd" : "powershell")}，超时 {timeoutSeconds}s）：{command}");

        // 在后台线程执行并在 UI 线程上泵消息等待，避免长命令冻结启动器界面。
        string result = "";
        Task task = Task.Run(() => { result = ExecuteProcess(psi, timeoutSeconds); });
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && dispatcher.CheckAccess())
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        else task.Wait();
        return result;
    }

    private static string ExecuteProcess(ProcessStartInfo psi, int timeoutSeconds)
    {
        using var proc = Process.Start(psi);
        if (proc == null) return "系统：无法启动进程。";
        proc.StandardInput.Close();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            AuditSystemLog($"命令超时被终止（{timeoutSeconds}s）");
            return $"系统：命令执行超过 {timeoutSeconds} 秒，已强制结束。";
        }
        proc.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        var output = (stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n[stderr]\n" + stderr)).Trim();
        if (output.Length > MaxCommandOutputChars) output = output[..MaxCommandOutputChars] + "\n……（输出过长已截断）";
        AuditSystemLog($"命令结束，退出码 {proc.ExitCode}");
        return $"系统：命令已执行，退出码 {proc.ExitCode}。输出：\n```\n{(output.Length == 0 ? "(无输出)" : output)}\n```\n请如实向用户说明结果，不要声称做了没有做的事。";
    }
}
