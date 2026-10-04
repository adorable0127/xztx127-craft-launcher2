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
    /// <summary>按真实版本解析的触控分支；null 表示外部进程或版本信息未知。</summary>
    public bool? UseOriginalTouchInput { get; set; }
    /// <summary>本次启动时的选择，仅用于记录；当前是否允许接管由 TouchModeEnabled 决定。</summary>
    public bool TouchModeRequestedAtLaunch { get; set; }
    private volatile bool _touchModeEnabled;
    /// <summary>当前实例明确启用且尚未关闭触屏模式；键鼠实例、外部发现实例默认不接管。</summary>
    public bool TouchModeEnabled { get => _touchModeEnabled; set => _touchModeEnabled = value; }
    public bool IsExternal { get; init; }
    public string SourceLabel => IsExternal ? "其他启动器 / 外部启动" : "XCL2";
    public string TouchInputRoute { get; set; } = "键鼠模式：未接管输入";
    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>外部启动的游戏（其他启动器）已探测到的原生游戏窗口句柄；启动器自己启动的进程为 <see cref="IntPtr.Zero"/>，
    /// 此时由 MainWindowHandle 轮询得到。</summary>
    public IntPtr TouchWindowHandle { get; set; }

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
    public string? CloseError { get; private set; }

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
        CloseError = null;
        try
        {
            if (HasExited) return;
            Process.Refresh();
            if (!Process.CloseMainWindow() || !Process.WaitForExit(3000))
                Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { if (!HasExited) CloseError = ex.Message; }
    }

    /// <summary>强制结束进程树（用于"未响应"场景，CloseMainWindow 大概率无效，直接 Kill）。</summary>
    public void ForceKill()
    {
        UserRequestedClose = true;
        CloseError = null;
        try
        {
            if (!HasExited) Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { if (!HasExited) CloseError = ex.Message; }
    }

    /// <summary>关闭等待在后台执行，多个游戏并行关闭，不阻塞启动器的界面线程。</summary>
    public Task<bool> CloseAsync(bool force = false) => Task.Run(() =>
    {
        if (force) ForceKill(); else Close();
        try { if (!HasExited) Process.WaitForExit(2000); }
        catch (Exception ex) { if (!HasExited) CloseError = ex.Message; }
        if (!HasExited) CloseError ??= "进程仍在运行，可能没有结束它的权限。";
        return HasExited;
    });
}
