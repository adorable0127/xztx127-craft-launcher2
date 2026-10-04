using System.Collections.ObjectModel;
using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>统一游戏进程列表。外部实例参与展示和用户明确请求的关闭，不参与日志/崩溃分析或退出清理。</summary>
public class GameProcessManager
{
    /// <summary>由 XCL2 启动的实例，供日志、崩溃分析与启动器退出清理使用。</summary>
    public ObservableCollection<GameProcessInfo> Processes { get; } = new();
    private IReadOnlyList<GameProcessInfo> _external = Array.Empty<GameProcessInfo>();
    private Task? _refreshTask;
    public event Action? Changed;

    public GameProcessInfo Register(GameProcessInfo info)
    {
        info.Process.Exited += (_, _) =>
        {
            // 不等待 UI 线程，避免关闭游戏时与退出事件相互阻塞。
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
                _ = dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
        };
        Processes.Add(info);
        Changed?.Invoke();
        return info;
    }

    public void PruneExited()
    {
        var dead = Processes.Where(p => p.HasExited).ToList();
        foreach (var d in dead) Processes.Remove(d);
        var external = _external.Where(p => !p.HasExited).ToArray();
        var changed = dead.Count > 0 || external.Length != _external.Count;
        _external = external;
        if (changed) Changed?.Invoke();
    }

    /// <summary>在后台发现外部游戏，返回 UI 线程更新快照；多个界面共用同一次扫描。</summary>
    public Task RefreshAsync()
    {
        if (_refreshTask is { IsCompleted: false }) return _refreshTask;
        return _refreshTask = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        try
        {
            var owned = Processes.Select(p => p.Pid).ToHashSet();
            _external = await Task.Run(() => ExternalGameDiscovery.Discover(owned));
            PruneExited();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // 探测失败时保留已有实例，不清空已知游戏，也不妨碍关闭 XCL2 的游戏。
            ErrorPresenter.LogTechnicalDetail($"[发现外部游戏失败] {ex}");
        }
    }

    public IReadOnlyList<GameProcessInfo> OwnedRunning => Processes.Where(p => !p.HasExited).ToList();
    public IReadOnlyList<GameProcessInfo> Running => OwnedRunning.Concat(_external.Where(p => !p.HasExited))
        .DistinctBy(p => p.Pid).ToList();

    public void CloseSelected(GameProcessInfo info) => info.Close();
    public void CloseAll() { foreach (var p in Running) p.Close(); }
    /// <summary>退出启动器只清理自己的游戏，发现外部实例不代表获得自动关闭它的授权。</summary>
    public void CloseOwned() { foreach (var p in OwnedRunning) p.Close(); }
    public void CloseUnresponsiveMarked()
    {
        foreach (var p in Running.Where(p => p.ManuallyMarkedUnresponsive)) p.ForceKill();
    }

    /// <summary>使用操作时的选择快照，避免关闭确认后才启动的其他游戏。</summary>
    public async Task<IReadOnlyList<GameProcessInfo>> CloseSelectedAsync(IEnumerable<GameProcessInfo> selected, bool force = false)
    {
        var games = selected.Where(p => !p.HasExited).DistinctBy(p => p.Pid).ToArray();
        var results = await Task.WhenAll(games.Select(p => p.CloseAsync(force)));
        PruneExited();
        Changed?.Invoke();
        return games.Where((_, index) => !results[index]).ToArray();
    }

    public Task<IReadOnlyList<GameProcessInfo>> CloseAllAsync() => CloseSelectedAsync(Running);

    public static string DescribeCloseFailures(IReadOnlyList<GameProcessInfo> failures)
        => "以下游戏未能关闭：\n" + string.Join("\n", failures.Select(p =>
            $"{p.VersionId}（PID {p.Pid}）：{p.CloseError ?? "进程仍在运行"}"));
}
