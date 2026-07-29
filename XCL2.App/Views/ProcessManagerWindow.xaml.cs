using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>列表行的展示包装：把 GameProcessInfo 转成 UI 好绑定的文本行 + 是否显示"标记无响应"勾选框。</summary>
public class ProcessRow
{
    public GameProcessInfo Info { get; }
    public Visibility MarkVisibility { get; }

    public string TitleLine => $"{Info.VersionId}  (PID {Info.Pid})";
    public string SubLine => $"账户: {Info.AccountLabel}  |  启动于: {Info.StartedAt:HH:mm:ss}" +
                              (Info.ManuallyMarkedUnresponsive ? "  |  已标记为无响应" : "");

    public ProcessRow(GameProcessInfo info, bool showMarkCheckbox)
    {
        Info = info;
        MarkVisibility = showMarkCheckbox ? Visibility.Visible : Visibility.Collapsed;
    }
}

public partial class ProcessManagerWindow : Window
{
    public enum Mode { SelectToClose, MarkUnresponsive }

    private readonly GameProcessManager _manager;
    private readonly Mode _mode;

    public ProcessManagerWindow(GameProcessManager manager, Mode mode)
    {
        _manager = manager;
        _mode = mode;
        InitializeComponent();

        if (mode == Mode.SelectToClose)
        {
            TitleText.Text = "关闭所选的游戏";
            HintText.Text = "选中一个正在运行的游戏，点击下方按钮关闭它。";
            ActionButton.Content = "关闭所选";
        }
        else
        {
            TitleText.Text = "关闭未响应的游戏";
            HintText.Text = "请先勾选确认无响应（卡死/打不开）的游戏，再点击下方按钮强制结束。" +
                             "未勾选的游戏不会被关闭，避免误杀正在正常游玩的进程。";
            ActionButton.Content = "强制结束已勾选的游戏";
        }

        Reload();
    }

    private void Reload()
    {
        ProcessListBox.ItemsSource = _manager.Running
            .Select(p => new ProcessRow(p, showMarkCheckbox: _mode == Mode.MarkUnresponsive))
            .ToList();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.SelectToClose)
        {
            if (ProcessListBox.SelectedItem is not ProcessRow row)
            {
                MessageBox.Show("请先在列表中选择一个游戏。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _manager.CloseSelected(row.Info);
        }
        else
        {
            var rows = ProcessListBox.ItemsSource as System.Collections.Generic.IEnumerable<ProcessRow>;
            var anyMarked = rows?.Any(r => r.Info.ManuallyMarkedUnresponsive) == true;
            if (!anyMarked)
            {
                MessageBox.Show("请先勾选至少一个确认无响应的游戏，再执行强制结束。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _manager.CloseUnresponsiveMarked();
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
