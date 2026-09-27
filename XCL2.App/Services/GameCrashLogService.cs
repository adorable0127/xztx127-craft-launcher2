using System.IO;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>只保存已判定为异常退出的游戏日志，普通/主动/疑似手动退出不生成崩溃记录。</summary>
public static class GameCrashLogService
{
    public static string GetSnapshotDirectory(string gameDir)
        => Path.Combine(gameDir, "logs", "xcl2-crashes");

    public static string? Capture(GameProcessInfo info, GameExitAssessment assessment)
    {
        if (assessment.Kind != GameExitKind.Crash || info.UserRequestedClose) return null;
        try
        {
            var directory = GetSnapshotDirectory(info.GameDir);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"exit-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{info.Pid}-{Guid.NewGuid():N}.log");
            var text = new StringBuilder();
            text.AppendLine("XCL2 非人为异常退出日志快照");
            text.AppendLine($"游戏版本：{info.VersionId}");
            text.AppendLine($"启动时间：{info.StartedAt:O}");
            text.AppendLine($"退出时间：{DateTime.Now:O}");
            text.AppendLine($"退出码：{assessment.ExitCode}");
            text.AppendLine("用户主动关闭：否");
            text.AppendLine("---------- 本次退出的控制台和游戏日志证据 ----------");
            text.AppendLine(string.IsNullOrWhiteSpace(assessment.Evidence)
                ? info.GetOutputSnapshot() : assessment.Evidence);
            // 写完才使用可扫描的 .log 后缀，避免页面看到半份报告。
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path);
            return path;
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[游戏日志] 保存异常退出快照失败：" + ex.Message);
            return null;
        }
    }
}
