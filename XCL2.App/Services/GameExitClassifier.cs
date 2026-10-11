using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

public enum GameExitKind
{
    Normal,
    UserRequested,
    PossiblyManualClose,
    Crash
}

/// <param name="JvmIssues">本次退出里识别出的 JVM 启动参数错误（见 <see cref="JvmArgumentDiagnostics"/>）；没有则为 null。
/// 有值时 Kind 一定是 Crash：JVM 都没起来，游戏代码根本没跑。</param>
public sealed record GameExitAssessment(GameExitKind Kind, int ExitCode, string Evidence, bool LogLooksTruncated,
    IReadOnlyList<JvmArgIssue>? JvmIssues = null);

/// <summary>
/// 对 Minecraft Java 进程退出做最保守的分类。核心目标不是“尽量多报崩溃”，而是避免把
/// 正常退出、启动器主动关闭、以及明显像是外部强制终止后留下半截日志的情况误写成崩溃。
/// </summary>
public static class GameExitClassifier
{
    /// <summary>硬证据：出现就基本可以确定是真的崩溃/JVM 致命错误，正常运行的日志里不会有。</summary>
    private static readonly string[] HardCrashMarkers =
    {
        "Crash report saved to",
        "A fatal error has been detected by the Java Runtime Environment",
        "EXCEPTION_ACCESS_VIOLATION",
        "OutOfMemoryError",
        "Could not create the Java Virtual Machine",
        "Could not reserve enough space for object heap"
    };

    /// <summary>软证据：这些字样在<b>正常运行</b>的日志里也经常出现（Mod 的告警栈、可选原生库加载失败、
    /// 无害的 GLFW/OpenGL 提示……），单独出现不能说明这次退出是崩溃。只有它们出现在日志<b>末尾</b>、
    /// 且没有正常关闭的收尾记录时，才当作崩溃证据。</summary>
    private static readonly string[] SoftCrashMarkers =
    {
        "Exception in thread",
        "Caused by:",
        "/FATAL]",
        " FATAL ",
        "GLFW error",
        "OpenGL error",
        "org.lwjgl.LWJGLException",
        "java.lang.UnsatisfiedLinkError"
    };

    /// <summary>软证据只看日志末尾这么多字符：真正的崩溃异常栈总是紧挨着日志结尾，
    /// 而游戏中途打印的、后来被正常运行淹没的告警离结尾很远。</summary>
    private const int SoftEvidenceTailChars = 6000;

    /// <summary>被外部/控制台终止时 Windows 给出的退出码（Ctrl+C / 关闭控制台 / 会话注销等），
    /// 不是游戏自己崩溃：0xC000013A STATUS_CONTROL_C_EXIT、Unix 风格的 130(SIGINT)/143(SIGTERM)。</summary>
    private static readonly int[] ExternalTerminationExitCodes = { unchecked((int)0xC000013A), 130, 143 };

    private static readonly string[] NormalShutdownMarkers =
    {
        "/INFO]: Stopping!",            // 1.14+ 是 [Render thread/INFO]，1.13 及更早是 [Client thread/INFO]
        "Stopping server",
        "Saving worlds",
        "Saving players",
        "Sound engine shut down",
        "Client shutdown"
    };

    /// <param name="exitCodeKnown">是否真的读到了进程退出码。读不到（句柄已失效等）时调用方传 false：
    /// 此时拿到的退出码只是占位值，不能把它当成"非 0 退出"去判崩溃。</param>
    public static GameExitAssessment Assess(GameProcessInfo info, int exitCode, bool exitCodeKnown = true)
    {
        if (info.UserRequestedClose)
            return new(GameExitKind.UserRequested, exitCode, "", false);

        // Java/Minecraft 正常从菜单退出时最可靠的信号就是 0。无论日志内容长短，都不能再写成“崩溃”。
        if (exitCodeKnown && exitCode == 0)
            return new(GameExitKind.Normal, exitCode, "", false);

        var output = info.GetOutputSnapshot();

        // JVM 启动参数错误：只看进程自己的控制台输出——这类错误发生在游戏代码运行之前，
        // 磁盘上的 latest.log 只可能是上一局遗留的，拿它来判断会张冠李戴。
        // 命中时直接定为崩溃并带上具体原因，不再走下面“半截日志=可能手动关闭”之类的推断。
        var jvmIssues = JvmArgumentDiagnostics.Detect(output);
        if (jvmIssues.Count > 0)
            return new(GameExitKind.Crash, exitCode, output, false, jvmIssues);

        // latest.log 只在"是这次运行写的"时才算证据：游戏没来得及写日志就退出时，
        // 磁盘上留着的是<b>上一局</b>的 latest.log（里面完全可能有旧的异常栈），拿它判这一局会误报崩溃。
        var latest = TryReadLatestLog(info.GameDir, info.StartedAt, out var latestEndsMidLine);
        var evidence = string.Join("\n", new[] { output, latest ?? "" }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var hasCrashArtifact = HasRecentCrashArtifact(info.GameDir, info.StartedAt);
        var hasHardCrashEvidence = hasCrashArtifact || ContainsAny(evidence, HardCrashMarkers);

        // 日志/控制台末尾是否有正常关闭的收尾记录（Stopping! / Saving worlds …）。
        var tail = evidence.Length > SoftEvidenceTailChars ? evidence[^SoftEvidenceTailChars..] : evidence;
        var hasNormalShutdown = ContainsAny(tail, NormalShutdownMarkers);

        // 有明确的正常关服/保存世界收尾，而且没有硬崩溃证据时，按正常退出处理——
        // 即使日志里别处有 Mod 的 Caused by / GLFW 提示这类软字样，也不能因此叫“崩溃”。
        // 这覆盖少数 Mod/Wrapper 会在正常退出时返回非 0 的情况。
        if (!hasHardCrashEvidence && hasNormalShutdown)
            return new(GameExitKind.Normal, exitCode, evidence, false);

        // 软证据：只看末尾，并且 OpenGL 诊断也只在没有正常收尾时才算。
        var hasSoftCrashEvidence = ContainsAny(tail, SoftCrashMarkers)
            || (!hasNormalShutdown && OpenGlTroubleshooter.IsOpenGlFailure(tail));
        var hasStrongCrashEvidence = hasHardCrashEvidence || hasSoftCrashEvidence;

        // 被 Ctrl+C / 关闭控制台 / 会话注销等外部方式终止：不是游戏自己崩溃。
        if (exitCodeKnown && !hasStrongCrashEvidence && ExternalTerminationExitCodes.Contains(exitCode))
            return new(GameExitKind.Normal, exitCode, evidence, false);

        // 没读到真实退出码、又没有任何崩溃证据：不能凭一个占位值判崩溃。
        if (!exitCodeKnown && !hasStrongCrashEvidence)
            return new(GameExitKind.Normal, exitCode, evidence, false);

        // latest.log 最后一行没有换行，通常意味着进程在日志框架来得及收尾前被外部结束。
        // 如果同时没有 crash-report/hs_err/FATAL/异常栈等明确证据，就不要武断地叫“游戏崩溃”。
        if (latestEndsMidLine && !hasStrongCrashEvidence)
            return new(GameExitKind.PossiblyManualClose, exitCode, evidence, true);

        return new(GameExitKind.Crash, exitCode, evidence, latestEndsMidLine);
    }

    private static bool ContainsAny(string text, string[] markers)
        => !string.IsNullOrEmpty(text) &&
           markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static string? TryReadLatestLog(string gameDir, DateTime startedAt, out bool endsMidLine)
    {
        endsMidLine = false;
        try
        {
            var path = Path.Combine(gameDir, "logs", "latest.log");
            if (!File.Exists(path)) return null;

            // 比这次启动更早（留出几秒时钟/文件系统误差）就是上一局遗留的日志，不属于这次运行。
            if (File.GetLastWriteTime(path) < startedAt.AddSeconds(-10)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
            var text = sr.ReadToEnd();
            if (text.Length > 0)
                endsMidLine = text[^1] != '\n' && text[^1] != '\r';
            return text;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasRecentCrashArtifact(string gameDir, DateTime startedAt)
    {
        try
        {
            var threshold = startedAt.AddSeconds(-3);
            var crashDir = Path.Combine(gameDir, "crash-reports");
            if (Directory.Exists(crashDir) && Directory.EnumerateFiles(crashDir, "*.txt")
                    .Any(f => File.GetLastWriteTime(f) >= threshold))
                return true;

            if (Directory.Exists(gameDir) && Directory.EnumerateFiles(gameDir, "hs_err_pid*.log")
                    .Any(f => File.GetLastWriteTime(f) >= threshold))
                return true;
        }
        catch { }
        return false;
    }
}
