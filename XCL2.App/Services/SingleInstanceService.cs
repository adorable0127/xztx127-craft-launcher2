using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace XCL2.App.Services;

/// <summary>
/// 多开检测：需求是"再打开一个 xcl2 时，提示已有实例在运行，让用户四选一"：
///   1. 保留此实例——新旧两个实例都继续跑，互不干涉；
///   2. 关闭此实例——新打开的这个直接退出，原实例不受影响；
///   3. 关闭此实例且拉起原来的实例——新打开的这个退出，同时把原实例的窗口切到前台；
///   4. 打开此实例并关闭原有实例——新打开的这个继续启动，原实例被要求退出。
///
/// 实现方式：不用 Mutex（Mutex 只能回答"有没有已经在跑的实例"，答不了"怎么联系到它"），
/// 改用本机命名管道（Named Pipe）：
///   - 第一个启动的实例作为"服务端"，常驻一个后台线程 WaitForConnection 循环监听。
///   - 后面每次再启动，新实例先尝试用极短超时去"连接"这个管道名：
///       · 连接失败 = 没有已有实例在监听，本实例自己就是第一个，转去当服务端。
///       · 连接成功 = 已经有实例在跑，弹窗询问用户，再决定要不要往这条已经建立的连接上
///         写一条指令（"ACTIVATE"/"CLOSE"），服务端那边收到后负责具体执行。
/// 这个方案不需要额外的第三方依赖，.NET 自带的 System.IO.Pipes 就够用，且天然只在本机
/// 生效（不监听网络端口），不会有被局域网内其它机器连接的安全顾虑。
/// </summary>
public static class SingleInstanceService
{
    // 带一个版本后缀：以后如果协议改了（比如指令格式变化），换个新管道名就能避免新旧版本
    // exe 之间互相误连接、读出一堆解析不了的数据。
    private const string PipeName = "XCL2_App_SingleInstance_Pipe_v1";
    private const int ProbeConnectTimeoutMs = 300;

    public enum ConflictChoice
    {
        /// <summary>保留此实例：两个实例都继续运行。</summary>
        KeepBothRunning,
        /// <summary>关闭此实例：新实例退出，原实例不受影响。</summary>
        CloseNewInstance,
        /// <summary>关闭此实例且拉起原来的实例：新实例退出，同时激活原实例窗口。</summary>
        CloseNewAndActivateOld,
        /// <summary>打开此实例并关闭原有实例：新实例继续启动，原实例被要求退出。</summary>
        CloseOldKeepNewInstance,
    }

    /// <summary>
    /// 在 RunStartupSequence 里、创建 MainWindow 之前调用。
    /// </summary>
    /// <param name="askUser">检测到已有实例在运行时才会被调用，用来弹窗询问用户四选一，
    /// 必须在 UI 线程同步返回结果（内部用 Window.ShowDialog() 实现，天然满足这一点）。</param>
    /// <returns>true：本实例应该继续往下走正常启动流程；false：本实例应该立刻退出，
    /// 不再执行任何后续初始化（不建日志 session、不建 MainWindow）。</returns>
    public static bool HandleStartup(Func<ConflictChoice> askUser)
    {
        NamedPipeClientStream? probe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        try
        {
            probe.Connect(ProbeConnectTimeoutMs);
        }
        catch
        {
            // 连接不上：没有实例在监听，本实例就是"第一个"，自己当服务端，正常继续启动。
            probe.Dispose();
            StartServer();
            return true;
        }

        // 连接成功，说明已经有一个实例在跑——用 using 保证不管用户选哪个分支，这条探测用的
        // 连接最终都会被正确释放（对面的 server.WaitForConnection() 才能继续接下一个连接）。
        using (probe)
        {
            var choice = askUser();
            switch (choice)
            {
                case ConflictChoice.KeepBothRunning:
                    // 不发任何指令，什么也不做：两个实例各自独立运行，互不干涉。
                    // 本实例不再尝试抢当服务端——旧实例已经占着这个管道名，抢不到也没必要抢，
                    // 以后第三次启动时探测到的仍然是旧实例，这符合"只要有实例在跑就该弹窗提醒"
                    // 的预期，不算问题。
                    return true;

                case ConflictChoice.CloseNewInstance:
                    return false;

                case ConflictChoice.CloseNewAndActivateOld:
                    TrySendCommand(probe, "ACTIVATE");
                    return false;

                case ConflictChoice.CloseOldKeepNewInstance:
                    TrySendCommand(probe, "CLOSE");
                    // 旧实例收到 CLOSE 到它真正退出、释放管道名之间有一小段异步延迟（要等它的
                    // UI 线程调度到、执行 Shutdown、NamedPipeServerStream 被 Dispose），
                    // 这里不死等，交给 StartServer() 内部的重试机制（见其注释）。
                    StartServer();
                    return true;

                default:
                    return true;
            }
        }
    }

    private static void TrySendCommand(NamedPipeClientStream client, string command)
    {
        try
        {
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
        }
        catch
        {
            // 这一瞬间旧实例可能碰巧已经退出、管道已经断开，发不出去就算了——不能因为
            // "通知旧实例"这件事失败，就阻塞或搞崩新实例自己的启动流程。
        }
    }

    /// <summary>
    /// 后台线程常驻的管道服务端：每次 WaitForConnection 处理完一条连接（读到一行指令，或者
    /// 对方只是探测一下就断开、读不到任何内容）后 Disconnect 复用同一个流对象继续等下一次
    /// 连接，不需要每次都重新 new 一个 NamedPipeServerStream。
    /// </summary>
    private static void StartServer()
    {
        var thread = new Thread(() =>
        {
            NamedPipeServerStream? server = null;
            // 重试原因见 HandleStartup 里 CloseOldKeepNewInstance 分支的注释：管道名的释放
            // 是异步的。最多重试 10 次、每次间隔 200ms（约 2 秒），比死等更稳，也比立刻放弃
            // 更宽容——不至于因为旧实例退出慢了半拍，新实例就彻底当不成服务端。
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    break;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
            // 抢不到管道名也不应该拖垮主程序其它功能：安静放弃即可，代价只是"以后新开的实例
            // 探测不到这个进程，会各自变成新的服务端"，多开检测退化但不影响启动器本身可用性。
            if (server == null) return;

            while (true)
            {
                try
                {
                    server.WaitForConnection();
                    string? command = null;
                    try
                    {
                        using var reader = new StreamReader(server, leaveOpen: true);
                        command = reader.ReadLine();
                    }
                    catch { /* 对方只是探测一下就断开连接（没写任何内容），忽略即可 */ }

                    if (!string.IsNullOrEmpty(command))
                        DispatchCommand(command);

                    server.Disconnect();
                }
                catch
                {
                    // 管道服务端自身出问题（概率很低，比如系统资源耗尽）：安静退出这个循环，
                    // 不影响主程序其它功能——大不了以后的多开检测失效。
                    break;
                }
            }
        })
        { IsBackground = true, Name = "XCL2-SingleInstance-Pipe-Server" };
        thread.Start();
    }

    private static void DispatchCommand(string command)
    {
        var app = Application.Current;
        if (app == null) return;
        // 收到指令时正处在后台线程里，UI 相关操作（切窗口前台/关闭应用）必须丢回 UI 线程。
        app.Dispatcher.BeginInvoke(() =>
        {
            switch (command)
            {
                case "ACTIVATE":
                    var win = app.MainWindow;
                    if (win == null) return;
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    win.Show();
                    win.Activate();
                    // Windows 的"前台窗口锁定"机制下，后台进程调用 Activate() 不一定总能真的
                    // 把窗口切到最前——假开关一下 Topmost 是社区常见的绕过写法，能覆盖绝大多数
                    // 场景，不是必须但能明显提高成功率。
                    win.Topmost = true;
                    win.Topmost = false;
                    break;
                case "CLOSE":
                    app.Shutdown();
                    break;
            }
        });
    }
}
