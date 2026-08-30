using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 服务端进程的启动/停止/注册表管理。对应清单里"一键开服"和"一键关服"。
///
/// 与 GameProcessManager(客户端) 的关键差异：
/// - 客户端关闭用 CloseMainWindow()（游戏有窗口）；服务端没有窗口，正常关服要往 stdin 发 "stop"
///   命令，让服务端自己保存世界，见 ServerProcessInfo.StopGracefullyAsync。
/// - 需要 RedirectStandardInput=true，才能做后续的控制台命令注入。
/// - 支持 CPU 使用率硬限制(Job Object)，客户端启动没有这个需求。
/// </summary>
public class ServerProcessManager
{
    public ObservableCollection<ServerProcessInfo> Processes { get; } = new();
    public event Action? Changed;

    // 记录每个实例对应的 Job Object 句柄，进程退出时释放，避免句柄泄漏
    private readonly Dictionary<string, IntPtr> _jobHandles = new();

    /// <summary>
    /// 一键开服：拼装 java 命令行（或直接跑 run.bat/run.sh 脚本）并启动进程。
    /// 同一个实例不允许重复启动——先检查 Processes 里是否已有该实例且仍在运行。
    /// </summary>
    public ServerProcessInfo Start(ServerInstance instance)
    {
        if (Processes.Any(p => p.InstanceId == instance.Id && !p.HasExited))
            throw new InvalidOperationException($"服务器「{instance.DisplayName}」已经在运行中，不能重复启动。");

        if (!Directory.Exists(instance.Directory))
            throw new DirectoryNotFoundException($"服务器目录不存在：{instance.Directory}");

        var launchTargetPath = Path.Combine(instance.Directory, instance.LaunchTarget);
        if (!File.Exists(launchTargetPath))
            throw new FileNotFoundException($"找不到启动文件：{launchTargetPath}\n可能核心还没有下载完成，或者文件被移动/删除了。");

        ProcessStartInfo psi;
        if (instance.LaunchTargetIsScript)
        {
            // Forge/NeoForge 生成的 run.bat/run.sh：脚本内部已经包含了正确的 JVM 参数（含内存设置的
            // user_jvm_args.txt 机制），直接执行脚本，不再额外拼 -Xmx 等参数，避免和脚本内部设置冲突。
            psi = new ProcessStartInfo
            {
                FileName = launchTargetPath,
                WorkingDirectory = instance.Directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8
            };

            // Forge/NeoForge 新版把内存参数放在 user_jvm_args.txt 里，这里同步写入，
            // 保证"开服向导里设置的内存上限"对脚本启动方式同样生效，而不是只对直接 java -jar 方式生效。
            TryWriteUserJvmArgsFile(instance);
        }
        else
        {
            // 注意：instance.JavaId(Java 列表选择) 在"编辑服务器 Java"保存时已经同步写回了
            // instance.JavaPath(见 ServerManagerPage.SaveServerJava_Click)，这里只需要用
            // JavaPath 就是当时选中列表条目的实际路径，不需要在这里再解析一次 JavaId。
            var javaPath = instance.JavaPath;
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
                throw new FileNotFoundException("没有配置有效的 Java 路径，无法启动服务端。请先在创建/编辑服务器时指定 Java。");

            psi = new ProcessStartInfo
            {
                FileName = javaPath,
                WorkingDirectory = instance.Directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add($"-Xms{instance.MinMemoryMb}M");
            psi.ArgumentList.Add($"-Xmx{instance.MaxMemoryMb}M");

            if (!string.IsNullOrWhiteSpace(instance.ExtraJvmArgs))
            {
                foreach (var arg in instance.ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    psi.ArgumentList.Add(arg);
            }

            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(launchTargetPath);
            psi.ArgumentList.Add("nogui"); // 控制台面板本身就是图形界面的替代，不需要服务端再弹一层自己的伪GUI
        }

        // 首次开服时如果 eula.txt 不存在或未同意，Mojang 服务端会直接打印提示后退出。
        // 提前处理好，避免用户第一次点"一键开服"就迷惑于"为什么点了立刻自己退出了"。
        EnsureEulaAccepted(instance.Directory);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        // CPU 限制：仅 Windows 生效，失败也不影响进程已经启动这个事实（见 JobObjectCpuLimiter 类注释）
        if (instance.CpuLimitPercent is { } cpuPercent)
        {
            var job = JobObjectCpuLimiter.TryLimitCpu(process, cpuPercent);
            if (job != IntPtr.Zero) _jobHandles[instance.Id] = job;
        }

        var info = new ServerProcessInfo(process, instance.Id, instance.DisplayName);
        info.BeginReadOutput();

        process.Exited += (_, _) =>
        {
            if (_jobHandles.TryGetValue(instance.Id, out var job))
            {
                JobObjectCpuLimiter.Release(job);
                _jobHandles.Remove(instance.Id);
            }
            Application.Current?.Dispatcher.Invoke(() => Changed?.Invoke());
        };

        Processes.Add(info);
        Changed?.Invoke();
        return info;
    }

    /// <summary>一键关服：正常方式（发送 stop 命令等待优雅退出），超时后强制结束。</summary>
    public async Task StopAsync(string instanceId, TimeSpan? timeout = null)
    {
        var info = Processes.FirstOrDefault(p => p.InstanceId == instanceId && !p.HasExited);
        if (info == null) return;

        var stoppedGracefully = await info.StopGracefullyAsync(timeout);
        if (!stoppedGracefully)
        {
            // stop 命令超时未响应：可能是服务端卡死或世界过大保存较慢，强制结束作为最后手段。
            // 这里选择强杀而不是无限等待，因为用户点了"关服"就是明确的停止意图，
            // 界面上应该给出确定性的结果，而不是无限期挂起等待一个可能永远不会退出的进程。
            info.ForceKill();
        }
    }

    /// <summary>发送任意控制台命令到指定运行中的服务器（对应"服务端控制台交互"功能）。</summary>
    public Task SendCommandAsync(string instanceId, string command)
    {
        var info = Processes.FirstOrDefault(p => p.InstanceId == instanceId && !p.HasExited);
        if (info == null) throw new InvalidOperationException("这个服务器当前没有在运行。");
        return info.SendCommandAsync(command);
    }

    public ServerProcessInfo? GetRunning(string instanceId)
        => Processes.FirstOrDefault(p => p.InstanceId == instanceId && !p.HasExited);

    public bool IsRunning(string instanceId) => GetRunning(instanceId) != null;

    public void PruneExited()
    {
        var dead = Processes.Where(p => p.HasExited).ToList();
        foreach (var d in dead) Processes.Remove(d);
        if (dead.Count > 0) Changed?.Invoke();
    }

    private static void EnsureEulaAccepted(string serverDir)
    {
        var eulaPath = Path.Combine(serverDir, "eula.txt");
        var content = File.Exists(eulaPath) ? File.ReadAllText(eulaPath) : "";
        if (content.Contains("eula=true")) return;

        // 静默写入同意——用户点击"一键开服"本身就是明确希望这个服务端跑起来的意图表达，
        // 这里不重复弹一次法律条款确认框；如果产品后续需要更严格的合规展示，
        // 应该在"创建服务器"向导的那一步展示一次 EULA 链接并要求勾选，而不是每次开服都问。
        File.WriteAllText(eulaPath, "eula=true\n");
    }

    private static void TryWriteUserJvmArgsFile(ServerInstance instance)
    {
        try
        {
            var path = Path.Combine(instance.Directory, "user_jvm_args.txt");
            var sb = new StringBuilder();
            sb.AppendLine("# 由 XCL2 自动写入，覆盖此文件里的内存参数会在下次「一键开服」时被重新覆盖");
            sb.AppendLine($"-Xms{instance.MinMemoryMb}M");
            sb.AppendLine($"-Xmx{instance.MaxMemoryMb}M");
            if (!string.IsNullOrWhiteSpace(instance.ExtraJvmArgs))
                sb.AppendLine(instance.ExtraJvmArgs);
            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // user_jvm_args.txt 不是所有 Forge/NeoForge 版本都有（旧版本机制不同），
            // 写入失败不阻塞启动流程，脚本会用自己内部的默认内存设置继续跑。
        }
    }
}
