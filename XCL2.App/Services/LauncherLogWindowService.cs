using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>
/// 需求："在选择完整启动器日志的页面，加入一个按钮，可以独立出一个启动器日志，不依附于
/// 咱们的进程。这样的话，就可以在进行启动器操作的时候查看日志"。
///
/// ===== 为什么不能直接用 WPF Window =====
/// "不依附于咱们的进程"是这个需求的核心——如果只是弹一个新的 WPF Window，它仍然跑在
/// XCL2.App.exe 这一个进程里，主进程假死/卡在某个同步操作上（比如网络请求没设超时、
/// 某个第三方库死锁）时，这个"独立日志窗口"会跟主窗口一起失去响应，完全起不到"操作
/// 启动器的同时还能看日志"的作用——恰恰是用户最需要看日志排查问题的时候，日志窗口自己
/// 也卡死了。必须是一个真正独立的操作系统进程。
///
/// ===== 实现方式：复用 GameConsoleWindowService 已验证过的模式 =====
/// 跟 GameConsoleWindowService（游戏控制台独立窗口）用的是同一套思路，本质原因相同：
/// "游戏日志"和"启动器日志"都是启动器进程内部持有的内存/托管状态，没法直接把这些状态
/// "转移"给另一个进程去读。解法是用一个磁盘文件做中转：
///   - 我们进程内定期把 LauncherLogService.GetBufferedText() 的内容同步写入一个临时文件；
///   - 独立弹出的 cmd 窗口用 `cmd /k` 常驻 + `powershell Get-Content -Wait` 持续 tail 这个
///     文件——`Get-Content -Wait` 本身是 PowerShell 自带命令，窗口的存活完全依赖操作系统
///     给它的这个独立进程，不会因为我们主进程卡死/被杀而跟着卡死/消失。
///
/// 跟 GameConsoleWindowService 的差异：LauncherLogService 是纯内存 StringBuilder，没有
/// OutputReceived 这样的"新增一行就推送"事件（游戏进程的 stdout 逐行到达，天然适合事件；
/// 启动器日志是"整段缓冲区"的概念，随时可能被别处一次性 AppendLine 追加，没有现成的
/// 增量事件源）。这里改用一个轻量 DispatcherTimer 定时（跟 LogsPage 里
/// _fullLauncherLogTimer 用的是同一种"没有事件源就轮询"思路）：每次把最新的完整缓冲区
/// 文本重新写入文件——文件很小（内存日志本身也就几十 KB 到几百 KB 量级），全量覆盖写
/// 开销可以忽略，不需要做增量 diff。
/// </summary>
public class LauncherLogWindowService : IDisposable
{
    private Process? _cmdProcess;
    private string? _logFilePath;
    private DispatcherTimer? _syncTimer;
    private string _lastSyncedText = "";

    /// <summary>
    /// 弹出一个独立的 CMD 窗口，持续镜像显示当前会话的完整启动器日志
    /// （LauncherLogService.GetBufferedText()）。
    /// </summary>
    public void Open()
    {
        try
        {
            var logDir = Path.Combine(Path.GetTempPath(), "XCL2-launcher-logs");
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"launcher-log-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            // 先把已有内容一次性写入文件，避免用户打开窗口时前面已经发生的日志漏看。
            var initial = LauncherLogService.GetBufferedText();
            File.WriteAllText(_logFilePath,
                "==== XCL2 启动器完整日志（独立窗口，实时镜像，只读）====" + Environment.NewLine, Encoding.UTF8);
            if (!string.IsNullOrEmpty(initial))
            {
                File.AppendAllText(_logFilePath, initial, Encoding.UTF8);
                _lastSyncedText = initial;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };
            // 跟 GameConsoleWindowService 同样的道理：/k 而不是 /c，保证窗口不会因为
            // 某条命令跑完就自动关闭；powershell -Wait -Tail 持续跟踪文件末尾新增内容。
            var escapedLogPath = _logFilePath.Replace("'", "''");
            psi.Arguments =
                "/k \"chcp 65001>nul & title XCL2 - 启动器日志（独立窗口） & " +
                $"powershell -NoLogo -NoProfile -Command \"& {{ Get-Content -LiteralPath '{escapedLogPath}' -Wait -Tail 2000 -Encoding UTF8 }}\"\"";

            _cmdProcess = Process.Start(psi);

            // 每秒把最新的内存日志同步进文件——跟 LogsPage 里 FullLauncherLogBox 的自动刷新
            // 频率保持一致，用户在两边看到的内容几乎没有感知上的时间差。
            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _syncTimer.Tick += (_, _) => SyncToFile();
            _syncTimer.Start();
        }
        catch
        {
            // 常见原因：找不到 cmd.exe/powershell 或系统限制创建新控制台窗口。静默失败，
            // 不影响启动器主窗口内「完整启动器日志」Tab 本身的查看功能。
            Dispose();
        }
    }

    private void SyncToFile()
    {
        if (_logFilePath == null) return;
        try
        {
            var text = LauncherLogService.GetBufferedText();
            if (text == _lastSyncedText) return; // 没有新内容，跳过这次磁盘 IO

            // 只追加新增的那一部分：LauncherLogService 的缓冲区只会在末尾追加、不会中途改写
            // 历史内容（见其 AppendLine 实现），所以"新文本以旧文本为前缀"这个假设总是成立，
            // 用 StartsWith 校验一下防御性地兜底——万一意外不成立（缓冲区被清空重置等极端
            // 情况），退化为全量重写，保证窗口显示的内容不会跟实际状态脱节。
            if (text.StartsWith(_lastSyncedText, StringComparison.Ordinal))
            {
                var delta = text[_lastSyncedText.Length..];
                File.AppendAllText(_logFilePath, delta, Encoding.UTF8);
            }
            else
            {
                File.WriteAllText(_logFilePath,
                    "==== XCL2 启动器完整日志（独立窗口，实时镜像，只读）====" + Environment.NewLine + text,
                    Encoding.UTF8);
            }
            _lastSyncedText = text;
        }
        catch { /* 文件可能被占用或已被清理，忽略，不影响启动器本身 */ }
    }

    /// <summary>停止同步定时器。独立弹出的 cmd 窗口本身不会被关掉——它是完全独立的进程，
    /// 用户可以（也应该能）在启动器主界面关闭之后继续留着这个日志窗口查看历史内容，
    /// 这正是"不依附于咱们的进程"这个需求的应有之义。停止定时器只是让文件不再继续被
    /// 我们进程写入新内容（比如主窗口关闭时），已经弹出的窗口和文件本身都还在。</summary>
    public void Dispose()
    {
        _syncTimer?.Stop();
        _syncTimer = null;
    }
}
