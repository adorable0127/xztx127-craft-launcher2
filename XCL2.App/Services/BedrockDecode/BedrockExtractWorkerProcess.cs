using System.Diagnostics;
using System.Globalization;
using System.IO;
using XCL2.App.Services;

namespace XCL2.App.BedrockDecode;

/// <summary>
/// 修复"解压基岩版 MSIXVC 包时整个启动器直接崩溃、没有任何输出、界面卡死"。
///
/// 根因（排查记录，供以后维护参考）：
/// MsiXVDStream/Extensions 里用 Marshal.PtrToStructure/Marshal.StructureToPtr 把文件里的
/// 原始字节直接反序列化成 MsiXVDHeader 等 C# 结构体（结构体里混了 byte[]/char[]/自定义
/// 结构体数组等非托管数组字段，配合 Pack=1）。这套手法一旦遇到跟预期不完全一致的包体
/// （版本差异、损坏、非标准来源），有极小概率触发 AccessViolationException —— 这是
/// "损坏进程状态"级别的异常（Corrupted State Exception），CLR 默认既不会被
/// try/catch(Exception) 捕获，也不会被 AppDomain.UnhandledException 捕获（哪怕两者都已经
/// 在 App.xaml.cs 里注册），进程会被 Windows 直接判定为崩溃并当场终止，不会有任何
/// .NET 异常堆栈写进日志——用户看到的现象就是"没有任何输出，界面卡死，直接没了"。
/// 这也是为什么 crash.log／会话日志都是空的：LauncherLogService 的日志只在进程正常退出
/// 或走到 DispatcherUnhandledException 处理器时才落盘，AccessViolationException 两边都
/// 到不了。
///
/// 修复方式：不改动解压逻辑本身（BedrockLauncher.Core 移植代码，按要求不动），而是把这一步
/// 隔离到独立子进程里跑。子进程如果被这类无法捕获的原生异常整个杀掉，父进程（主界面所在
/// 进程）只会看到子进程以非 0 退出码结束，可以正常捕获并提示"解压失败"，不会连累主界面
/// 一起消失。跟 App.xaml.cs 里 TryRelaunchForWin7WriteXorExecuteFix 复用自身 exe 重新拉起
/// 一个进程是同一个思路，这里只是换成"拉起子进程跑完一段任务就退出"而不是"整个替换当前
/// 进程"。
/// </summary>
public static class BedrockExtractWorkerProcess
{
    /// <summary>命令行参数里用来识别"以子进程 worker 模式启动"的标记，App.xaml.cs 的
    /// OnStartup 最开始就要检查这个，检查到就直接跑 worker 逻辑、不创建任何窗口。</summary>
    public const string WorkerArgMarker = "--bedrock-extract-worker";

    private const string ProgressLinePrefix = "PROGRESS\t";
    private const string OkLinePrefix = "OK\t";
    private const string ErrLinePrefix = "ERR\t";

    /// <summary>
    /// 父进程（主界面）这边调用：拉起自身 exe 的子进程模式，等它跑完解压。
    /// 子进程整个挂掉（含 AccessViolationException 这类拿不到管理异常信息的原生崩溃）时，
    /// 这里会用退出码/是否读到 OK 行来判定失败，抛出普通的 InvalidOperationException——
    /// 调用方（BedrockClientDownloadService.DownloadClientAsync）原有的 try/catch
    /// 完全不用改，效果上跟"解压函数自己抛了个异常"完全一样，只是这次真的能被接住。
    /// </summary>
    public static async Task RunAsync(string packagePath, string extractDir,
        BedrockClientDownloadService.BedrockClientChannel channel,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("无法定位启动器自身可执行文件，解压子进程启动失败。");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(WorkerArgMarker);
        psi.ArgumentList.Add(packagePath);
        psi.ArgumentList.Add(extractDir);
        psi.ArgumentList.Add(channel.ToString());

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stderrBuffer = new System.Text.StringBuilder();
        string? okLine = null;
        string? errLine = null;

        proc.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            if (args.Data.StartsWith(ProgressLinePrefix, StringComparison.Ordinal))
            {
                // 格式：PROGRESS\t<done>\t<total>\t<fileName>
                var parts = args.Data.Substring(ProgressLinePrefix.Length).Split('\t');
                if (parts.Length >= 3
                    && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
                    && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                {
                    progress?.Report(new ProgressInfo("正在解压 MSIXVC 安装包", (int)done, (int)total, parts[2]));
                }
            }
            else if (args.Data.StartsWith(OkLinePrefix, StringComparison.Ordinal))
            {
                okLine = args.Data;
            }
            else if (args.Data.StartsWith(ErrLinePrefix, StringComparison.Ordinal))
            {
                errLine = args.Data.Substring(ErrLinePrefix.Length);
            }
        };
        proc.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data)) stderrBuffer.AppendLine(args.Data);
        };

        LauncherLogService.AppendLine($"[Bedrock下载] 启动独立解压子进程：{packagePath} -> {extractDir}");

        if (!proc.Start())
            throw new InvalidOperationException("解压子进程启动失败。");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            throw;
        }

        if (errLine != null)
        {
            throw new InvalidOperationException($"解压失败：{errLine}");
        }

        if (okLine == null || proc.ExitCode != 0)
        {
            // 子进程没有正常输出 OK 行就退出了：绝大多数是被 Windows 直接判定原生崩溃
            // 杀掉（比如上面注释说的 AccessViolationException），退出码通常是
            // 0xC0000005 之类的 NTSTATUS 值，转成有符号 int 后会是个很大的负数，
            // 这里不特判具体数值，统一按"子进程异常终止"处理，提示里带上退出码方便反馈。
            var detail = stderrBuffer.Length > 0 ? $"：{stderrBuffer}" : "";
            throw new InvalidOperationException(
                $"解压过程异常终止（退出码 {proc.ExitCode}，可能是安装包本身有问题或与当前解压逻辑不兼容）{detail}");
        }

        LauncherLogService.AppendLine($"[Bedrock下载] 独立解压子进程正常完成：{extractDir}");
    }

    /// <summary>
    /// 子进程这边调用：真正执行解压（复用 GdkPackageExtractor.ExtractAsync，逻辑完全不变），
    /// 通过 stdout 按行输出进度/结果给父进程读取。在 App.xaml.cs 的 OnStartup 最开始，
    /// 检测到命令行第一个参数是 WorkerArgMarker 时调用本方法并直接退出进程，不创建任何窗口、
    /// 不加载配置/主题这些跟这次任务无关的东西，保持子进程尽量"轻"、只做一件事。
    /// </summary>
    public static async Task<int> RunAsWorkerAsync(string[] args)
    {
        // args[0] 是 WorkerArgMarker 本身，真正参数从 args[1] 开始。
        if (args.Length < 4)
        {
            Console.WriteLine($"{ErrLinePrefix}参数不足");
            return 1;
        }

        var packagePath = args[1];
        var extractDir = args[2];
        if (!Enum.TryParse<BedrockClientDownloadService.BedrockClientChannel>(args[3], out var channel))
            channel = BedrockClientDownloadService.BedrockClientChannel.Stable;

        try
        {
            var progress = new Progress<ProgressInfo>(info =>
            {
                Console.WriteLine($"{ProgressLinePrefix}{info.Done}\t{info.Total}\t{info.CurrentFile}");
            });

            await GdkPackageExtractor.ExtractAsync(packagePath, extractDir, channel, progress);

            Console.WriteLine($"{OkLinePrefix}done");
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            // 这里能接住的都是"正常的托管异常"（文件损坏之类走得到 catch 的场景），
            // 单独用一个进程跑的意义在于防住那些连这个 catch 都到不了、把整个进程
            // 直接带走的原生崩溃（AccessViolationException 等）——那类情况这个 catch
            // 根本不会执行，子进程会直接非正常退出，父进程靠退出码判定，见 RunAsync。
            Console.WriteLine($"{ErrLinePrefix}{ex.Message}");
            Console.Out.Flush();
            return 1;
        }
    }
}
