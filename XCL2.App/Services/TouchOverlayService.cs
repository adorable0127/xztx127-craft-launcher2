using System.Threading;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

public enum TouchOverlayState
{
    Disabled,
    Starting,
    Enabled,
    Failed
}

/// <summary>按游戏实例管理触屏悬浮层。会话和状态通知均在 UI 线程上处理。</summary>
public class TouchOverlayService
{
    private sealed class OverlaySession
    {
        public GameProcessInfo Info { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TouchOverlayWindow? Window { get; set; }
        public EventHandler? ExitHandler { get; set; }
        public EventHandler? ClosedHandler { get; set; }
        public TouchOverlayState State { get; set; } = TouchOverlayState.Starting;
        public string? Error { get; set; }

        public OverlaySession(GameProcessInfo info) => Info = info;
    }

    // 使用实例对象作为键，同一版本启动多次也能独立开关。
    private static readonly Dictionary<GameProcessInfo, OverlaySession> Sessions = new();

    public static event Action? Changed;

    public static (TouchOverlayState State, string? Error) GetStatus(GameProcessInfo info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => GetStatus(info));

        if (info.HasExited || !Sessions.TryGetValue(info, out var session))
            return (TouchOverlayState.Disabled, null);
        return (session.State, session.Error);
    }

    /// <summary>可在游戏启动时或运行中开启；已有悬浮层/等待任务时不会重复创建。</summary>
    public static void Attach(GameProcessInfo info, AppConfig cfg)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Attach(info, cfg));
            return;
        }

        if (info.HasExited) return;
        if (Sessions.TryGetValue(info, out var existing))
        {
            if (existing.State is TouchOverlayState.Starting or TouchOverlayState.Enabled) return;
            StopSession(existing);
        }

        var session = new OverlaySession(info);
        Sessions.Add(info, session);
        session.ExitHandler = (_, _) =>
        {
            // 不阻塞进程退出线程，避免主线程等待游戏退出时互相等待。
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            try { dispatcher.BeginInvoke(new Action(() => StopSession(session))); }
            catch (InvalidOperationException) { /* 启动器正在退出 */ }
        };
        info.Process.Exited += session.ExitHandler;
        _ = AttachWhenReadyAsync(session, cfg.TouchOverlayButtonScale,
            cfg.TouchOverlayOpacityPercent, cfg.TouchOverlayLookSensitivity);
        Changed?.Invoke();
    }

    private static bool IsCurrent(OverlaySession session)
        => Sessions.TryGetValue(session.Info, out var current) && ReferenceEquals(current, session);

    private static async Task AttachWhenReadyAsync(OverlaySession session,
        double buttonScale, double opacityPercent, double sensitivity)
    {
        var token = session.Cancellation.Token;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            IntPtr hwnd = IntPtr.Zero;
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(session) || session.Info.HasExited) return;

                hwnd = await Task.Run(() =>
                {
                    try
                    {
                        session.Info.Process.Refresh();
                        return session.Info.Process.MainWindowHandle;
                    }
                    catch { return IntPtr.Zero; }
                }, token);

                token.ThrowIfCancellationRequested();
                if (!IsCurrent(session) || session.Info.HasExited) return;
                if (hwnd != IntPtr.Zero) break;
                await Task.Delay(500, token);
            }

            if (hwnd == IntPtr.Zero)
            {
                FailSession(session, "等待游戏窗口超时，请在游戏窗口出现后重新开启。");
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(session) || session.Info.HasExited) return;

            var overlay = new TouchOverlayWindow(hwnd, buttonScale, opacityPercent, sensitivity);
            session.Window = overlay;
            session.ClosedHandler = (_, _) => StopSession(session, closeWindow: false);
            overlay.Closed += session.ClosedHandler;
            overlay.Show();

            // Loaded/Closed 可能在 Show 中触发；已关闭的窗口不能再标记为开启。
            if (!IsCurrent(session)) return;
            if (session.Info.HasExited) { StopSession(session); return; }
            session.State = TouchOverlayState.Enabled;
            Changed?.Invoke();
            LauncherLogService.AppendLine($"[触屏模式] 已为版本 {session.Info.VersionId} (PID {session.Info.Pid}) 开启触摸板。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 用户关闭/游戏退出后，旧的等待任务不能重新创建悬浮层。
        }
        catch (Exception ex)
        {
            FailSession(session, "触摸板开启失败，可重试或在日志中查看原因。");
            ErrorPresenter.LogTechnicalDetail($"[触屏悬浮层加载失败，不影响游戏运行]\n{ex}");
        }
        finally
        {
            if (IsCurrent(session) && session.State == TouchOverlayState.Starting)
                StopSession(session);
        }
    }

    private static void FailSession(OverlaySession session, string message)
    {
        if (!IsCurrent(session)) return;
        if (session.Info.HasExited) { StopSession(session); return; }
        session.State = TouchOverlayState.Failed;
        session.Error = message;
        ReleaseWindow(session, closeWindow: true);
        LauncherLogService.AppendLine($"[触屏模式] {session.Info.VersionId} (PID {session.Info.Pid})：{message}");
        Changed?.Invoke();
    }

    private static void ReleaseWindow(OverlaySession session, bool closeWindow)
    {
        var overlay = session.Window;
        session.Window = null;
        if (overlay == null) return;
        if (session.ClosedHandler != null) overlay.Closed -= session.ClosedHandler;
        session.ClosedHandler = null;
        try
        {
            if (closeWindow) overlay.ShutdownOverlay();
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"[触屏悬浮层关闭失败]\n{ex}");
        }
    }

    /// <summary>只关闭所选实例的触摸板（或取消等待），游戏继续运行。</summary>
    public static void Detach(GameProcessInfo info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Detach(info));
            return;
        }
        if (Sessions.TryGetValue(info, out var session)) StopSession(session);
    }

    private static void StopSession(OverlaySession session, bool closeWindow = true)
    {
        if (!IsCurrent(session)) return;
        Sessions.Remove(session.Info);
        session.Cancellation.Cancel();
        if (session.ExitHandler != null)
            session.Info.Process.Exited -= session.ExitHandler;
        ReleaseWindow(session, closeWindow);
        session.Cancellation.Dispose();
        Changed?.Invoke();
    }

    /// <summary>等待开启也算活动会话，退出启动器时一并提示和清理。</summary>
    public static bool HasActive
    {
        get
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                return dispatcher.Invoke(() => HasActive);
            return Sessions.Values.Any(s => !s.Info.HasExited &&
                (s.State is TouchOverlayState.Starting or TouchOverlayState.Enabled));
        }
    }

    public static void CloseAll()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                dispatcher.Invoke(CloseAll);
            return;
        }
        foreach (var session in Sessions.Values.ToList()) StopSession(session);
    }
}
