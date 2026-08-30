using System.Collections.ObjectModel;
using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 全局游戏进程注册表：每次"启动游戏"都会往这里加一条记录，
/// 供主页的"关闭所选的游戏 / 一键关闭游戏 / 关闭未响应的游戏"三个按钮，以及日志面板、崩溃分析使用。
/// 用 ObservableCollection 让 UI 能直接绑定列表变化。
/// </summary>
public class GameProcessManager
{
    public ObservableCollection<GameProcessInfo> Processes { get; } = new();

    public event Action? Changed;

    public GameProcessInfo Register(GameProcessInfo info)
    {
        // Process.Exited 在 EnableRaisingEvents=true 时会在进程退出后触发，
        // 这里回到 UI 线程移除记录，保证列表始终反映"仍在运行"的进程。
        info.Process.Exited += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Changed?.Invoke();
            });
        };
        Processes.Add(info);
        Changed?.Invoke();
        return info;
    }

    /// <summary>清理已经退出的记录（供 UI 定时刷新调用，避免"僵尸"条目一直停留在列表里）。</summary>
    public void PruneExited()
    {
        var dead = Processes.Where(p => p.HasExited).ToList();
        foreach (var d in dead) Processes.Remove(d);
        if (dead.Count > 0) Changed?.Invoke();
    }

    public IReadOnlyList<GameProcessInfo> Running => Processes.Where(p => !p.HasExited).ToList();

    /// <summary>关闭所选的游戏：只结束指定的一个进程。</summary>
    public void CloseSelected(GameProcessInfo info) => info.Close();

    /// <summary>一键关闭游戏：结束所有正在运行的游戏进程。</summary>
    public void CloseAll()
    {
        foreach (var p in Running) p.Close();
    }

    /// <summary>
    /// 关闭未响应的游戏：只对用户手动勾选/标记为"无响应"的进程强制结束，
    /// 不会自作主张判断游戏是否卡死——避免误杀正常运行中的游戏。
    /// </summary>
    public void CloseUnresponsiveMarked()
    {
        foreach (var p in Running.Where(p => p.ManuallyMarkedUnresponsive))
            p.ForceKill();
    }
}
