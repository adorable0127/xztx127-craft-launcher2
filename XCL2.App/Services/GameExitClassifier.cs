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

public sealed record GameExitAssessment(GameExitKind Kind, int ExitCode, string Evidence, bool LogLooksTruncated);

/// <summary>
/// 对 Minecraft Java 进程退出做最保守的分类。核心目标不是“尽量多报崩溃”，而是避免把
/// 正常退出、启动器主动关闭、以及明显像是外部强制终止后留下半截日志的情况误写成崩溃。
/// </summary>
public static class GameExitClassifier
{
    private static readonly string[] StrongCrashMarkers =
    {
        "Exception in thread",
        "/FATAL]",
        " FATAL ",
        "Crash report saved to",
        "A fatal error has been detected by the Java Runtime Environment",
        "EXCEPTION_ACCESS_VIOLATION",
        "OutOfMemoryError",
        "Could not create the Java Virtual Machine",
        "Could not reserve enough space for object heap",
        "org.lwjgl.LWJGLException",
        "java.lang.UnsatisfiedLinkError",
        "Incompatible mod set!",
        "Incompatible mods found!",
        "Mod resolution encountered an incompatible mod set",
        "net.fabricmc.loader.impl.FormattedException",
        "Failed to start minecraft",
        "Missing or unsupported mandatory dependencies",
        "Error: Could not find or load main class",
        "Error: Unable to access jarfile"
    };

    private static readonly string[] NormalShutdownMarkers =
    {
        "[Render thread/INFO]: Stopping!",
        "Stopping server",
        "Saving worlds",
        "Saving players",
        "Sound engine shut down",
        "Client shutdown"
    };

    public static GameExitAssessment Assess(GameProcessInfo info, int exitCode)
    {
        if (info.UserRequestedClose)
            return new(GameExitKind.UserRequested, exitCode, "", false);

        // Java/Minecraft 正常从菜单退出时最可靠的信号就是 0。无论日志内容长短，都不能再写成“崩溃”。
        if (exitCode == 0)
            return new(GameExitKind.Normal, exitCode, "", false);

        // Windows 控制台被关闭 / Ctrl+C 的终止码不是游戏崩溃。
        if (exitCode == unchecked((int)0xC000013A))
            return new(GameExitKind.PossiblyManualClose, exitCode, "", false);

        var output = info.GetOutputSnapshot();
        var latest = TryReadLatestLog(info.GameDir, info.StartedAt, out var latestEndsMidLine);
        var evidence = string.Join("\n", new[] { output, latest ?? "" }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var hasCrashArtifact = HasRecentCrashArtifact(info.GameDir, info.StartedAt);
        var hasStrongCrashEvidence = hasCrashArtifact || OpenGlTroubleshooter.IsOpenGlFailure(evidence) || StrongCrashMarkers.Any(m =>
            evidence.Contains(m, StringComparison.OrdinalIgnoreCase));

        // 有明确的正常关服/保存世界收尾，而且没有任何强崩溃证据时，也按正常退出处理。
        // 这覆盖少数 Mod/Wrapper 会在正常退出时返回非 0 的情况。
        if (!hasStrongCrashEvidence && NormalShutdownMarkers.Any(m =>
                evidence.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return new(GameExitKind.Normal, exitCode, evidence, false);

        // 没有 crash-report/hs_err/FATAL 等明确证据时，不仅半行日志需要保守处理：
        // 外部结束进程也可能留下完整换行，单凭非零退出码不能认定为游戏自身崩溃。
        if (!hasStrongCrashEvidence)
            return new(GameExitKind.PossiblyManualClose, exitCode, evidence, latestEndsMidLine);

        return new(GameExitKind.Crash, exitCode, evidence, latestEndsMidLine);
    }

    private static string? TryReadLatestLog(string gameDir, DateTime startedAt, out bool endsMidLine)
    {
        endsMidLine = false;
        try
        {
            var path = Path.Combine(gameDir, "logs", "latest.log");
            if (!File.Exists(path)) return null;
            if (File.GetLastWriteTime(path) < startedAt.AddSeconds(-3)) return null;

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
