using System.ComponentModel;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public class ProcessRow : INotifyPropertyChanged
{
    public GameProcessInfo Info { get; }
    public Visibility MarkVisibility { get; }
    public string TitleLine => $"{Info.VersionId}  (PID {Info.Pid})";
    public string SubLine => $"来源: {Info.SourceLabel}  |  {GameWindowStatus.ResponseLabel(Info)}" +
        (Info.ManuallyMarkedUnresponsive ? "  |  已标记为无响应" : "");
    public bool IsMarkedUnresponsive
    {
        get => Info.ManuallyMarkedUnresponsive;
        set
        {
            Info.ManuallyMarkedUnresponsive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMarkedUnresponsive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubLine)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public ProcessRow(GameProcessInfo info, bool showMarkCheckbox)
    {
        Info = info;
        MarkVisibility = showMarkCheckbox ? Visibility.Visible : Visibility.Collapsed;
    }
}

public partial class ProcessManagerWindow : OverlayDialogControl
{
    public enum Mode { SelectToClose, MarkUnresponsive }
    private readonly GameProcessManager _manager;
    private readonly Mode _mode;
    private bool _busy;

    public ProcessManagerWindow(GameProcessManager manager, Mode mode)
    {
        _manager = manager;
        _mode = mode;
        InitializeComponent();
        if (mode == Mode.SelectToClose)
        {
            TitleText.Text = "关闭所选的游戏";
            HintText.Text = "选择需要关闭的游戏（包括其他启动器启动的实例），再点击关闭所选。";
            ActionButton.Content = "关闭所选";
        }
        else
        {
            TitleText.Text = "关闭未响应的游戏";
            HintText.Text = "包含其他启动器启动的游戏。勾选确认无响应的实例后强制结束；系统响应状态仅供参考。";
            ActionButton.Content = "强制结束已勾选的游戏";
        }
        Loaded += (_, _) => _manager.Changed += Reload;
        Unloaded += (_, _) => _manager.Changed -= Reload;
        Reload();
    }

    private void Reload()
    {
        var selected = ProcessListBox.SelectedItems.Cast<ProcessRow>().Select(r => r.Info).ToHashSet();
        var rows = _manager.Running.Select(p => new ProcessRow(p, _mode == Mode.MarkUnresponsive)).ToList();
        ProcessListBox.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains(r.Info))) ProcessListBox.SelectedItems.Add(row);
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var rows = ProcessListBox.ItemsSource as IEnumerable<ProcessRow> ?? Array.Empty<ProcessRow>();
        var games = _mode == Mode.SelectToClose
            ? ProcessListBox.SelectedItems.Cast<ProcessRow>().Select(r => r.Info).ToList()
            : rows.Where(r => r.IsMarkedUnresponsive).Select(r => r.Info).ToList();
        if (games.Count == 0)
        {
            MessageBoxDialog.ShowInfo(_mode == Mode.SelectToClose
                ? "请先在列表中选择游戏。" : "请先勾选至少一个确认无响应的游戏。", Loc.T("Str_Status_Tip", "提示"));
            return;
        }
        _busy = true;
        ActionButton.IsEnabled = ProcessListBox.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            var failures = await _manager.CloseSelectedAsync(games, force: _mode == Mode.MarkUnresponsive);
            if (failures.Count == 0) CloseWith(null);
            else
            {
                MessageBoxDialog.ShowWarning(GameProcessManager.DescribeCloseFailures(failures), "关闭失败");
                Reload();
            }
        }
        finally
        {
            _busy = false;
            ActionButton.IsEnabled = ProcessListBox.IsEnabled = CancelButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_busy) CloseWith(null); }
}
