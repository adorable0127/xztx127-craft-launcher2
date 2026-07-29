using System.Diagnostics;
using System.Text;

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

    /// <summary>用户是否已手动标记这个进程为"无响应"，标记后才允许使用"关闭未响应的游戏"按钮。</summary>
    public bool ManuallyMarkedUnresponsive { get; set; }

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

        process.OutputDataReceived += (_, e) => AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(e.Data);
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
        try
        {
            if (!HasExited) Process.Kill(entireProcessTree: true);
        }
        catch { /* 忽略：进程可能已经退出 */ }
    }
}
