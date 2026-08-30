using System.Diagnostics;
using System.IO;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 负责在游戏启动时额外弹出一个独立的 CMD 窗口，把游戏进程的实时控制台输出
/// (stdout+stderr) 镜像打印到这个窗口里，方便习惯用命令行看日志的用户直接查看，
/// 不需要打开启动器内的「日志」面板。
///
/// === 之前版本的 bug："只显示一秒就消失" ===
/// 之前用 cmd.exe /c "... & findstr /r ."，把 findstr 的标准输入重定向到我们手里写。
/// 问题在于：
///   1) `/c` 执行完"一整条命令"后 cmd 就会退出——只要 findstr 在还没收到任何数据前
///      读到 stdin 的 EOF（例如 Attach() 里第一次真正 Flush 之前出现哪怕一瞬间的管道
///      句柄状态异常/时序问题），"这一整条命令"就算执行完了，窗口立刻关闭，
///      表现出来就是"闪一下就没了"。
///   2) 全靠我们进程内手动 Flush 维持 findstr 存活，只要中途有一次异常没写成功，
///      没有任何兜底，窗口同样会静默关闭。
///
/// === 新实现思路 ===
/// 不再依赖"手动喂 stdin、子进程被动等 EOF 才决定要不要退出"这种脆弱语义，改成两点：
///   - 用 `cmd /k` 而不是 `/c`：`/k` 执行完命令后保留交互式 shell 常驻，不会因为
///     某条命令执行完就退出窗口，从根子上排除"一闪而过"的可能。
///   - 用一个独立生命周期的日志文件做中转：游戏输出被追加写入临时文件，cmd 窗口里跑
///     `powershell Get-Content -Wait` 持续 tail 这个文件，窗口的存活完全不依赖我们进程内
///     托管对象（Process/StreamWriter）的生命周期，只要文件还在就会一直刷新显示。
/// </summary>
public class GameConsoleWindowService
{
    private Process? _cmdProcess;
    private string? _logFilePath;
    private readonly object _fileLock = new();

    /// <summary>为指定的游戏进程打开一个独立 CMD 窗口，并开始实时镜像它的控制台输出。</summary>
    public void Attach(GameProcessInfo info)
    {
        try
        {
            var logDir = Path.Combine(Path.GetTempPath(), "XCL2-console-logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"console-{info.Process.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            // 先把已有的历史输出一次性写入文件，避免用户打开晚了漏看前面的日志。
            string existing;
            lock (info.OutputBuffer) existing = info.OutputBuffer.ToString();
            File.WriteAllText(_logFilePath, "==== 游戏控制台输出 (只读镜像) ====" + Environment.NewLine, Encoding.UTF8);
            if (!string.IsNullOrEmpty(existing))
                AppendToFile(existing);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardInput = false, // 关键改动：不再重定向标准输入，窗口拥有独立的
                                                // 控制台会话，不受我们进程内对象生命周期影响。
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };
            // 用 /k 而不是 /c：/k 执行完命令后保留交互式 cmd 会话，窗口不会自动关闭。
            // powershell -Wait -Tail 持续跟踪文件末尾新增内容，效果等价于 tail -f。
            var title = EscapeForCmdLine(info.VersionId);
            var escapedLogPath = _logFilePath.Replace("'", "''");
            psi.Arguments =
                $"/k \"chcp 65001>nul & title XCL2 - {title} & " +
                $"powershell -NoLogo -NoProfile -Command \"& {{ Get-Content -LiteralPath '{escapedLogPath}' -Wait -Tail 500 -Encoding UTF8 }}\"\"";

            _cmdProcess = Process.Start(psi);

            info.OutputReceived += OnLineReceived;

            info.Process.Exited += (_, _) =>
            {
                AppendToFile("[XCL2] 游戏进程已退出，这个窗口可以关闭了。");
                info.OutputReceived -= OnLineReceived;
            };
        }
        catch
        {
            // 常见原因：找不到 cmd.exe/powershell 或系统限制创建新控制台窗口。静默失败，
            // 不影响游戏本身启动，用户依然可以用启动器内的日志面板查看输出。
        }
    }

    private void OnLineReceived(string line) => AppendToFile(line);

    /// <summary>把一行文本追加写入日志文件，cmd 窗口里的 powershell tail 会自动捕获到新增内容。
    /// 用文件而不是管道做中转，窗口的存活完全独立于我们进程内对象的生命周期。</summary>
    private void AppendToFile(string text)
    {
        if (_logFilePath == null) return;
        try
        {
            lock (_fileLock)
                File.AppendAllText(_logFilePath, text + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 文件可能被占用或已被清理，忽略，不影响游戏本身 */ }
    }

    /// <summary>标题里可能包含用户自定义的版本号文本，对命令行里的 " 和 % 做最基本转义。</summary>
    private static string EscapeForCmdLine(string s) => s.Replace("\"", "").Replace("%", "%%");
}
