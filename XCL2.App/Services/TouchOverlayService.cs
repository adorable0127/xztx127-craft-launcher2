using System.Windows;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>单个游戏进程上触屏悬浮层的状态。</summary>
public enum TouchOverlayState
{
    /// <summary>未开启。</summary>
    Disabled,
    /// <summary>正在等待游戏窗口出现 / 正在创建悬浮层。</summary>
    Starting,
    /// <summary>悬浮层已显示。</summary>
    Enabled,
    /// <summary>开启失败（见错误文本）。</summary>
    Failed
}

/// <summary>GetStatus 的返回值。</summary>
public readonly record struct TouchOverlayStatus(TouchOverlayState State, string? Error);

/// <summary>
/// 触屏模式的接入口：负责在游戏窗口真正出现之后，把"套娃层"(<see cref="TouchOverlayWindow"/>)
/// 贴上去，并在游戏进程退出时自动收掉。
///
/// 为什么要轮询等窗口：Process.Start 返回的时候 java 进程刚起来，游戏窗口还不存在
/// （要等 JVM 起来、资源加载完，慢的机器上几十秒都有可能），MainWindowHandle 这时是 0。
/// 直接建悬浮层会贴到一个不存在的窗口上。这里最多等 3 分钟，每 500ms 探一次，
/// 拿到有效句柄再建层；期间游戏要是崩了/被关了就直接放弃，不留任何残留窗口。
///
/// 这是纯附加功能：任何一步失败都只记日志，绝不能影响游戏本身的启动和运行。
/// </summary>
public class TouchOverlayService
{
    private sealed class Entry
    {
        public required GameProcessInfo Info;
        public TouchOverlayState State = TouchOverlayState.Starting;
        public string? Error;
        public CancellationTokenSource Cts = new();
        public ITouchOverlayWindow? Overlay;
    }

    private static readonly List<ITouchOverlayWindow> Active = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<int, Entry> Entries = new();

    /// <summary>任一进程的状态变化时触发（可能在后台线程触发）。</summary>
    public static event Action? Changed;

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* 订阅方异常不影响主流程 */ }
    }

    /// <summary>查询某个游戏进程当前的触屏悬浮层状态。</summary>
    public static TouchOverlayStatus GetStatus(GameProcessInfo info)
    {
        lock (Gate)
            return Entries.TryGetValue(info.Pid, out var e)
                ? new TouchOverlayStatus(e.State, e.Error)
                : new TouchOverlayStatus(TouchOverlayState.Disabled, null);
    }

    /// <summary>关闭某个进程上的悬浮层，或取消尚在等待中的开启。幂等。</summary>
    public static void Detach(GameProcessInfo info)
    {
        // 先撤销接管许可，输入入口和异步加载任务即刻失效。
        info.TouchModeEnabled = false;
        info.TouchInputRoute = "键鼠模式：未接管输入";
        Entry? e;
        lock (Gate)
        {
            Entries.Remove(info.Pid, out e);
        }
        if (e == null) { RaiseChanged(); return; }
        try { e.Cts.Cancel(); } catch { }
        var overlay = e.Overlay;
        if (overlay != null)
        {
            try { overlay.ShutdownOverlay(); } catch { /* 尽力而为 */ }
            lock (Gate) Active.Remove(overlay);
        }
        RaiseChanged();
    }

    /// <summary>用户在管理面板明确切换当前实例到触屏模式，包括外部启动的游戏。</summary>
    public static void Enable(GameProcessInfo info, AppConfig cfg)
    {
        if (info.HasExited) return;
        info.TouchModeEnabled = true;
        Attach(info, cfg);
    }

    /// <summary>给一个刚启动的游戏进程挂上触屏悬浮层（异步等待窗口出现，不阻塞调用方）。</summary>
    public static void Attach(GameProcessInfo info, AppConfig cfg)
    {
        // 键鼠模式不建立接管会话、不加载窗口、不安装钩子。
        if (!info.TouchModeEnabled || info.HasExited) return;
        var entry = new Entry { Info = info };
        lock (Gate)
        {
            if (Entries.TryGetValue(info.Pid, out var old) &&
                old.State is TouchOverlayState.Starting or TouchOverlayState.Enabled) return;
            Entries[info.Pid] = entry;
        }
        RaiseChanged();
        var ct = entry.Cts.Token;

        void Fail(string msg)
        {
            lock (Gate)
            {
                if (!ct.IsCancellationRequested && Entries.TryGetValue(info.Pid, out var cur) && ReferenceEquals(cur, entry))
                { entry.State = TouchOverlayState.Failed; entry.Error = msg; }
            }
            RaiseChanged();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                IntPtr hwnd = IntPtr.Zero;

                while (DateTime.UtcNow < deadline)
                {
                    if (ct.IsCancellationRequested || !info.TouchModeEnabled) return;
                    if (info.HasExited)
                    {
                        lock (Gate) { if (Entries.TryGetValue(info.Pid, out var cur) && ReferenceEquals(cur, entry)) Entries.Remove(info.Pid); }
                        RaiseChanged();
                        return; // 游戏已经退出/崩了，不用贴了
                    }

                    try
                    {
                        hwnd = TouchGameWindow.Find(info.Pid, info.TouchWindowHandle);
                    }
                    catch { hwnd = IntPtr.Zero; }

                    if (hwnd != IntPtr.Zero) break;
                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
                }

                if (hwnd == IntPtr.Zero)
                {
                    LauncherLogService.AppendLine("[触屏模式] 等待游戏窗口超时，未能加载触屏悬浮层。");
                    Fail("等待游戏窗口超时");
                    return;
                }

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested || !info.TouchModeEnabled || info.HasExited) return;
                    lock (Gate)
                        if (!Entries.TryGetValue(info.Pid, out var current) || !ReferenceEquals(current, entry)) return;
                    bool CanTakeOver() => info.TouchModeEnabled && !ct.IsCancellationRequested && !info.HasExited;
                    var originalInput = info.UseOriginalTouchInput ?? TouchVersionPolicy.FromVersion(info.VersionId)
                        ?? (TouchGameWindow.Detect(hwnd) == TouchGameBackend.Sdl);
                    info.TouchInputRoute = originalInput ? "触控路径：26.3 T4+" : "触控路径：26.3 T4 前兼容";
                    ITouchOverlayWindow overlay = originalInput
                        ? new OriginalTouchOverlayWindow(hwnd, cfg.TouchOverlayButtonScale,
                            cfg.TouchOverlayOpacityPercent, cfg.TouchOverlayLookSensitivity, info.Pid, CanTakeOver)
                        : new TouchOverlayWindow(hwnd, cfg.TouchOverlayButtonScale,
                            cfg.TouchOverlayOpacityPercent, cfg.TouchOverlayLookSensitivity, info.Pid, CanTakeOver);
                    LauncherLogService.AppendLine($"[触屏模式] 版本={info.VersionId}，路径={(originalInput ? "26.3 T4+ 第二份源码实现" : "26.3 T4 前 GLFW/LWJGL 兼容实现")}");
                    lock (Gate)
                    {
                        Active.Add(overlay);
                        entry.Overlay = overlay;
                    }
                    // 先订阅清理，再显示。游戏可能在 Loaded 回调中退出或撤销接管。
                    overlay.Closed += (_, _) =>
                    {
                        lock (Gate)
                        {
                            Active.Remove(overlay);
                            if (Entries.TryGetValue(info.Pid, out var cur) && ReferenceEquals(cur, entry))
                            {
                                Entries.Remove(info.Pid);
                                info.TouchModeEnabled = false;
                                info.TouchInputRoute = "键鼠模式：未接管输入";
                            }
                        }
                        RaiseChanged();
                    };
                    EventHandler onExited = (_, _) =>
                    {
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.HasShutdownStarted)
                            _ = dispatcher.BeginInvoke(new Action(() => overlay.ShutdownOverlay()));
                    };
                    info.Process.Exited += onExited;
                    overlay.Closed += (_, _) => info.Process.Exited -= onExited;
                    if (!CanTakeOver()) { overlay.ShutdownOverlay(); return; }
                    try { overlay.Show(); }
                    catch { overlay.ShutdownOverlay(); throw; }
                    if (!CanTakeOver()) { overlay.ShutdownOverlay(); return; }
                    lock (Gate)
                    {
                        if (!Entries.TryGetValue(info.Pid, out var cur) || !ReferenceEquals(cur, entry)) return;
                        entry.State = TouchOverlayState.Enabled;
                    }
                    LauncherLogService.AppendLine($"[触屏模式] 已为版本 {info.VersionId} 加载触屏悬浮层。");
                    RaiseChanged();
                });
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogTechnicalDetail($"[触屏悬浮层加载失败，不影响游戏运行]\n{ex}");
                Fail(ex.Message);
            }
        });
    }

    /// <summary>当前是否有至少一个触屏悬浮层正在运行——关闭启动器前用来判断要不要弹出
    /// 触屏模式专属的关闭确认，见 MainWindow.MainWindow_Closing。</summary>
    public static bool HasActive { get { lock (Gate) return Entries.Values.Any(e =>
        e.State is TouchOverlayState.Starting or TouchOverlayState.Enabled); } }

    /// <summary>关闭当前所有悬浮层（启动器退出时调用，避免留下孤儿置顶窗口）。</summary>
    public static void CloseAll()
    {
        List<ITouchOverlayWindow> list;
        List<Entry> entries;
        lock (Gate)
        {
            list = Active.ToList();
            entries = Entries.Values.ToList();
            Active.Clear();
            Entries.Clear();
        }
        foreach (var e in entries)
        {
            e.Info.TouchModeEnabled = false;
            e.Info.TouchInputRoute = "键鼠模式：未接管输入";
            try { e.Cts.Cancel(); } catch { }
        }
        foreach (var w in list)
        {
            try { w.ShutdownOverlay(); } catch { /* 尽力而为 */ }
        }
        RaiseChanged();
    }
}
