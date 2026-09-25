using System.Diagnostics;
using System.Text;
using System.Threading;

namespace XCL2.App.Models;

/// <summary>
/// 一个正在运行（或刚结束）的游戏进程的运行时信息。
/// 由 LauncherService 在启动游戏时创建，供"进程管理"面板展示、关闭、以及日志/崩溃分析读取。
/// </summary>
public class GameProcessInfo
{
    public Process Process { get; }
    public string VersionId { get; }
    public string AccountLabel { get; }
    public string GameDir { get; }
    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>游戏 Java 进程的 stdout+stderr 实时输出，滚动缓冲，供日志面板"游戏日志"Tab 展示。</summary>
    public StringBuilder OutputBuffer { get; } = new();

    public event Action<string>? OutputReceived;

    private readonly TaskCompletionSource<bool> _stdoutClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stderrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exitNotificationClaimed;

    /// <summary>用户是否已手动标记这个进程为"无响应"，标记后才允许使用"关闭未响应的游戏"按钮。</summary>
    public bool ManuallyMarkedUnresponsive { get; set; }

    /// <summary>是否是启动器/用户主动请求关闭的（点了"关闭游戏"按钮，或"关闭未响应的游戏"）。
    /// 用来区分"用户主动结束游戏"和"游戏自己意外退出/崩溃"——只有后者才需要弹崩溃提示，
    /// 前者是用户自己的操作，不应该被当成崩溃打扰用户。见 <see cref="Close"/>/<see cref="ForceKill"/>。</summary>
    public bool UserRequestedClose { get; private set; }

    /// <summary>同一个进程的退出只能交给一处 UI 处理。启动阶段和 Exited 事件都可能几乎同时观察到退出，
    /// 用这个原子标记保证最多只弹一次退出/崩溃提示。</summary>
    public bool TryClaimExitNotification()
        => Interlocked.Exchange(ref _exitNotificationClaimed, 1) == 0;

    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch { return true; }
        }
    }

    public int Pid
    {
        get
        {
            try { return Process.Id; }
            catch { return -1; }
        }
    }

    public GameProcessInfo(Process process, string versionId, string accountLabel, string gameDir)
    {
        Process = process;
        VersionId = versionId;
        AccountLabel = accountLabel;
        GameDir = gameDir;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) { _stdoutClosed.TrySetResult(true); return; }
            AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { _stderrClosed.TrySetResult(true); return; }
            AppendLine(e.Data);
        };
    }

    private void AppendLine(string? line)
    {
        if (line == null) return;
        lock (OutputBuffer)
        {
            OutputBuffer.AppendLine(line);
            // 简单的滚动裁剪，避免长时间运行内存无限增长
            if (OutputBuffer.Length > 500_000)
                OutputBuffer.Remove(0, OutputBuffer.Length - 400_000);
        }
        OutputReceived?.Invoke(line);
    }

    /// <summary>等待重定向的 stdout/stderr 把进程退出前已经写进管道的数据尽量读完。
    /// Process.Exited 可能先于最后几条异步 DataReceived 到达；不等这一小段时间会把“日志还没读完”
    /// 当成“日志只有一半”，甚至错过真正的异常行。</summary>
    public async Task WaitForOutputDrainAsync(TimeSpan timeout)
    {
        try
        {
            var allClosed = Task.WhenAll(_stdoutClosed.Task, _stderrClosed.Task);
            await Task.WhenAny(allClosed, Task.Delay(timeout));
        }
        catch { /* 退出判定不能因为日志收尾失败而中断 */ }
    }

    public string GetOutputSnapshot()
    {
        lock (OutputBuffer) return OutputBuffer.ToString();
    }

    public void BeginReadOutput()
    {
        try
        {
            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
        }
        catch { /* 进程可能已退出 */ }
    }

    /// <summary>正常请求关闭（先尝试优雅关闭主窗口消息，超时后强制结束）。</summary>
    public void Close()
    {
        UserRequestedClose = true;
        try
        {
            if (HasExited) return;
            if (!Process.CloseMainWindow() || !Process.WaitForExit(3000))
                Process.Kill(entireProcessTree: true);
        }
        catch { /* 忽略：进程可能已经退出 */ }
    }

    /// <summary>强制结束进程树（用于"未响应"场景，CloseMainWindow 大概率无效，直接 Kill）。</summary>
    public void ForceKill()
    {
        UserRequestedClose = true;
        try
        {
            if (!HasExited) Process.Kill(entireProcessTree: true);
        }
        catch { /* 忽略：进程可能已经退出 */ }
    }
}
