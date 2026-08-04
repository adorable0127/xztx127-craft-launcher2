using System.Windows;
using System.Windows.Input;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 运行中服务器的控制台交互窗口：对应清单里"服务端控制台交互"——这里实现的是三选项中的
/// "内嵌控制台面板（可直接输入命令）"。独立 CMD 窗口选项和"两者都要"的可选开关，
/// 留待后续根据用户对这版内嵌面板的反馈再决定是否要补充（用户当时没有最终确认这三选一，
/// 先实现"内嵌面板"这个默认最常用、大多数同类启动器采用的方式）。
/// </summary>
public partial class ServerConsoleWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly ServerInstance _instance;
    private ServerProcessInfo? _processInfo;

    public ServerConsoleWindow(MainWindow owner, ServerInstance instance)
    {
        _owner = owner;
        _instance = instance;
        InitializeComponent();

        TitleText.Text = $"服务器控制台 - {instance.DisplayName}";
        AttachToProcess();

        // 同上：Window.Closed → IOverlayDialog.RequestClose
        RequestClose += (_, _) => Detach();
    }

    private void AttachToProcess()
    {
        _processInfo = _owner.ServerProcessManager.GetRunning(_instance.Id);
        if (_processInfo == null)
        {
            OutputText.Text = Loc.T("Str_Cs_The_Server_Isn_T_Running", "（服务器当前没有在运行）");
            StopBtn.IsEnabled = false;
            CommandBox.IsEnabled = false;
            return;
        }

        // 打开窗口时，先把已经产生的历史输出一次性展示出来，再订阅后续实时输出，
        // 避免用户打开控制台时看到的是空白，误以为服务器没有任何日志。
        lock (_processInfo.OutputBuffer)
        {
            OutputText.Text = _processInfo.OutputBuffer.ToString();
        }
        ScrollToEnd();

        _processInfo.OutputReceived += OnOutputReceived;
    }

    private void OnOutputReceived(string line)
    {
        Dispatcher.Invoke(() =>
        {
            OutputText.AppendText(line + "\n");
            ScrollToEnd();

            if (_processInfo?.HasExited == true)
            {
                StopBtn.IsEnabled = false;
                CommandBox.IsEnabled = false;
                OutputText.AppendText("\n[服务器进程已退出]\n");
                ScrollToEnd();
            }
        });
    }

    private void ScrollToEnd() => OutputScroll.ScrollToEnd();

    private void Detach()
    {
        if (_processInfo != null) _processInfo.OutputReceived -= OnOutputReceived;
    }

    private async void SendCommand_Click(object sender, RoutedEventArgs e) => await SendCurrentCommandAsync();

    private async void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendCurrentCommandAsync();
    }

    private async Task SendCurrentCommandAsync()
    {
        var cmd = CommandBox.Text.Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        CommandBox.Clear();

        try
        {
            await _owner.ServerProcessManager.SendCommandAsync(_instance.Id, cmd);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Send_The_Command_The_Server_Pro", "发送命令失败，可能是服务器进程已经停止响应。"),
                ex.ToString(), Loc.T("Str_Cs_Couldn_T_Send_The_Command", "发送命令失败"));
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBoxDialog.ShowConfirm($"确定要停止服务器「{_instance.DisplayName}」吗？\n会先尝试正常关服（保存世界），最长等待60秒后如仍未退出将强制结束。", "确认停止");
        if (!confirm) return;   // ShowConfirm 返回 bool（true=用户点了"是"），不再是 MessageBoxResult

        StopBtn.IsEnabled = false;
        CommandBox.IsEnabled = false;
        OutputText.AppendText("\n[正在发送 stop 命令，等待服务器保存并退出...]\n");
        ScrollToEnd();

        try
        {
            await _owner.ServerProcessManager.StopAsync(_instance.Id);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Something_Went_Wrong_Stopping_The_Server", "停止服务器时出错，如果服务器进程还在运行，可以尝试从「进程管理」里手动结束它。"),
                ex.ToString(), Loc.T("Str_Cs_Couldn_T_Stop_The_Server", "停止服务器失败"));
        }
    }
}
