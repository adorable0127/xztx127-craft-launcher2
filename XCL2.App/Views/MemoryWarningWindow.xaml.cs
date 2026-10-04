using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>列表行的展示包装：把 GameProcessInfo 转成 UI 好绑定的文本行，附带该进程实际占用的物理内存。</summary>
public class MemoryWarningProcessRow
{
    public GameProcessInfo Info { get; }

    public string TitleLine => $"{Info.VersionId}  (PID {Info.Pid})";
    public string SubLine
    {
        get
        {
            var memMb = TryGetWorkingSetMb(Info);
            var memText = memMb.HasValue ? $"{memMb.Value:N0} MB" : "未知";
            return $"来源: {Info.SourceLabel}  |  占用内存: {memText}  |  {GameWindowStatus.ResponseLabel(Info)}";
        }
    }

    public MemoryWarningProcessRow(GameProcessInfo info) => Info = info;

    private static double? TryGetWorkingSetMb(GameProcessInfo info)
    {
        try
        {
            if (info.HasExited) return null;
            info.Process.Refresh();
            return info.Process.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch
        {
            // 进程可能在读取的瞬间已经退出，或者没有权限查询，静默返回未知即可，
            // 不应该因为拿不到内存数值就让整个预警窗口崩掉。
            return null;
        }
    }
}

/// <summary>
/// 系统可用内存过低时弹出的预警窗口：展示当前内存状态 + 正在运行的游戏进程列表，
/// 让用户选择"关闭所选进程 / 立即关闭全部游戏 / 忽略本次提醒"。
///
/// 设计上刻意不做"自动帮用户关掉游戏"：内存告急不代表游戏一定要被强制杀掉——
/// 玩家可能只是想赶紧保存一下再退出，直接自动 Kill 反而可能造成存档损坏/进度丢失。
/// 这里的职责只是"尽早、显眼地提醒用户"，具体关不关、关哪个，由用户自己决定。
/// </summary>
public partial class MemoryWarningWindow : OverlayDialogControl
{
    private readonly GameProcessManager _processManager;
    private readonly MemoryWatchdogService _watchdog;
    private bool _busy;

    public MemoryWarningWindow(GameProcessManager processManager, MemoryWatchdogService watchdog,
        MemoryWatchdogService.LowMemoryEventArgs args)
    {
        _processManager = processManager;
        _watchdog = watchdog;
        InitializeComponent();

        DetailText.Text =
            $"当前系统可用内存仅剩 {args.AvailPhysMb:N0} MB（总内存 {args.TotalPhysMb:N0} MB，" +
            $"已用 {args.MemoryLoadPercent}%）。";

        Loaded += (_, _) => _processManager.Changed += OnProcessesChanged;
        Unloaded += (_, _) => _processManager.Changed -= OnProcessesChanged;
        RefreshList(selectAll: true);
    }

    private void OnProcessesChanged() => RefreshList();

    private void RefreshList(bool selectAll = false)
    {
        var selected = ProcessListBox.SelectedItems.Cast<MemoryWarningProcessRow>().Select(r => r.Info).ToHashSet();
        var rows = _processManager.Running.Select(p => new MemoryWarningProcessRow(p)).ToList();
        ProcessListBox.ItemsSource = rows;
        foreach (var row in rows.Where(r => selectAll || selected.Contains(r.Info)))
            ProcessListBox.SelectedItems.Add(row);
    }

    private async void CloseSelected_Click(object sender, RoutedEventArgs e)
    {
        var games = ProcessListBox.SelectedItems.Cast<MemoryWarningProcessRow>().Select(r => r.Info).ToArray();
        if (games.Length == 0)
        {
            MessageBoxDialog.ShowInfo("请先选择需要关闭的游戏。", "提示");
            return;
        }
        await CloseGamesAsync(games);
    }

    private async void CloseAll_Click(object sender, RoutedEventArgs e)
        => await CloseGamesAsync(_processManager.Running);

    private async Task CloseGamesAsync(IEnumerable<GameProcessInfo> games)
    {
        if (_busy) return;
        _busy = true;
        CloseSelectedBtn.IsEnabled = CloseAllBtn.IsEnabled = IgnoreBtn.IsEnabled = ProcessListBox.IsEnabled = false;
        try
        {
            var failures = await _processManager.CloseSelectedAsync(games);
            if (failures.Count > 0)
                MessageBoxDialog.ShowWarning(GameProcessManager.DescribeCloseFailures(failures), "关闭失败");
            if (_processManager.Running.Count == 0) CloseWith(null);
            else RefreshList();
        }
        finally
        {
            _busy = false;
            CloseSelectedBtn.IsEnabled = CloseAllBtn.IsEnabled = IgnoreBtn.IsEnabled = ProcessListBox.IsEnabled = true;
        }
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _watchdog.SuppressUntilRecovered();
        CloseWith(null);
    }
}
