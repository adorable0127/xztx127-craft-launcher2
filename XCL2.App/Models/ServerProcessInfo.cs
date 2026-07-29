using System.Diagnostics;
using System.Text;

namespace XCL2.App.Models;

/// <summary>
/// 一个正在运行的服务器进程的运行时信息。与客户端 GameProcessInfo 的关键区别：
/// 服务端是纯控制台程序，没有窗口可关——"关服"不能像客户端那样 CloseMainWindow()，
/// 正常关服方式是往 stdin 写入 "stop" 命令，让服务端自己完成保存世界等收尾工作再退出，
/// 强制 Kill 是没有收到 stop 后超时的兜底手段，而不是首选方式。
/// </summary>
public class ServerProcessInfo
{
    public Process Process { get; }
    public string InstanceId { get; }
    public string DisplayName { get; }
    public DateTime StartedAt { get; } = DateTime.Now;

    public StringBuilder OutputBuffer { get; } = new();
    public event Action<string>? OutputReceived;

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

    public ServerProcessInfo(Process process, string instanceId, string displayName)
    {
        Process = process;
        InstanceId = instanceId;
        DisplayName = displayName;

        process.OutputDataReceived += (_, e) => AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(e.Data);
    }

    private void AppendLine(string? line)
    {
        if (line == null) return;
        lock (OutputBuffer)
        {
            OutputBuffer.AppendLine(line);
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

    /// <summary>
    /// 往服务端 stdin 写入一行命令（例如 "say hello"、"list"、"stop"）。
    /// 这是控制台交互的核心机制——服务端把 stdin 当作控制台输入源，和真的在
    /// run.bat 窗口里手敲命令是等价的。
    /// </summary>
    public async Task SendCommandAsync(string command)
    {
        if (HasExited) throw new InvalidOperationException("服务器已经停止运行，无法发送命令。");
        try
        {
            await Process.StandardInput.WriteLineAsync(command);
            await Process.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("发送命令失败，可能服务器进程刚好退出。", ex);
        }
    }

    /// <summary>
    /// 正常关服：发送 "stop" 命令，等待服务端自行保存世界并退出。
    /// 超时(默认60秒，世界大的服务器保存可能较慢)后才升级为强制结束，
    /// 避免用户等待中误以为卡死而重复点击导致数据没保存完整就被杀掉。
    /// </summary>
    public async Task<bool> StopGracefullyAsync(TimeSpan? timeout = null)
    {
        if (HasExited) return true;
        try
        {
            await SendCommandAsync("stop");
        }
        catch
        {
            // stdin 已经不可写（进程可能刚好在这一瞬间退出），直接判断退出状态
            return HasExited;
        }

        var waitTask = Process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout ?? TimeSpan.FromSeconds(60)));
        return completed == waitTask;
    }

    /// <summary>强制结束进程树（用于"服务端没有响应 stop 命令"的兜底场景）。</summary>
    public void ForceKill()
    {
        try
        {
            if (!HasExited) Process.Kill(entireProcessTree: true);
        }
        catch { /* 忽略：进程可能已经退出 */ }
    }
}
