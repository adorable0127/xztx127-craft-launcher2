using System.IO;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 需求："游戏崩溃的时候，可以选择查看日志和导出完整日志。完整日志包括启动器的日志、
/// 游戏崩溃前的输出和游戏日志"。
///
/// 这里的"完整日志"是三段内容拼接成一份文本，方便用户一次性发给帮忙排查问题的人，
/// 不用东找一个文件西找一个文件：
/// 1. 启动器日志——本次启动器会话运行期间的时间线摘要，来自 <see cref="LauncherLogService"/>
///    的内存缓冲（这时候还没到关闭时刻，文件可能还没落盘，所以直接读缓冲区而不是读文件）。
/// 2. 游戏崩溃前的输出——崩溃的这个游戏进程自己的 stdout/stderr 滚动缓冲
///    （<see cref="GameProcessInfo.OutputBuffer"/>），也就是"游戏日志"Tab 里能看到的内容。
/// 3. 游戏日志——游戏工作目录下 logs/latest.log（Minecraft 自己写盘的日志文件，
///    内容跟第 2 点的控制台输出有重叠但格式不同，两者都留着方便交叉核对）。
/// </summary>
public static class CrashLogExportService
{
    /// <summary>组装完整的合并日志文本，供"查看日志"预览和"导出完整日志"保存共用同一份内容。</summary>
    public static string BuildCombinedLog(GameProcessInfo? processInfo)
    {
        var sb = new StringBuilder();

        sb.AppendLine("========================================");
        sb.AppendLine("XCL2 崩溃完整日志导出");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("========================================");
        sb.AppendLine();

        sb.AppendLine("---------- 一、启动器日志（本次会话） ----------");
        var launcherLog = LauncherLogService.GetBufferedText();
        sb.AppendLine(string.IsNullOrWhiteSpace(launcherLog) ? "(空)" : launcherLog);
        sb.AppendLine();

        sb.AppendLine("---------- 二、游戏崩溃前的控制台输出 ----------");
        if (processInfo != null)
        {
            string gameOutput;
            lock (processInfo.OutputBuffer) gameOutput = processInfo.OutputBuffer.ToString();
            sb.AppendLine(string.IsNullOrWhiteSpace(gameOutput) ? "(没有捕获到任何输出)" : gameOutput);
        }
        else
        {
            sb.AppendLine("(没有可用的游戏进程输出)");
        }
        sb.AppendLine();

        sb.AppendLine("---------- 三、游戏日志文件（logs/latest.log） ----------");
        var gameLogText = TryReadGameLatestLog(processInfo);
        sb.AppendLine(gameLogText ?? "(没有找到游戏的 logs/latest.log 文件)");

        return sb.ToString();
    }

    /// <summary>从游戏进程记录的工作目录里找 logs/latest.log 并读取全文。找不到就返回 null。</summary>
    private static string? TryReadGameLatestLog(GameProcessInfo? processInfo)
    {
        if (processInfo == null) return null;
        try
        {
            var logPath = Path.Combine(processInfo.GameDir, "logs", "latest.log");
            if (File.Exists(logPath))
                return File.ReadAllText(logPath);
        }
        catch { /* 读取失败就当作没有，不阻断其余内容的展示/导出 */ }
        return null;
    }

    /// <summary>把合并日志保存到指定路径，静默失败时抛出异常交给调用方处理（调用方通常会弹友好错误提示）。</summary>
    public static void ExportTo(string filePath, GameProcessInfo? processInfo)
    {
        File.WriteAllText(filePath, BuildCombinedLog(processInfo));
    }
}
