using System.Windows;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 游戏崩溃提示弹窗（见 CrashReportDialog.xaml 顶部注释）。
///
/// 用法：<c>CrashReportDialog.Show(mainWindow, summary, processInfo)</c>——processInfo 传崩溃/
/// 提前退出的那个游戏进程记录，"查看日志"和"导出完整日志"都要靠它拿到"游戏崩溃前的输出"
/// 和游戏工作目录（用来找 logs/latest.log）。传 null 也能弹（比如没能拿到进程对象的极端情况），
/// 这时导出的日志里对应两段会显示"没有可用的游戏进程输出"。
/// </summary>
public partial class CrashReportDialog : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly GameProcessInfo? _processInfo;

    private CrashReportDialog(MainWindow owner, string message, GameProcessInfo? processInfo)
    {
        InitializeComponent();
        _owner = owner;
        _processInfo = processInfo;
        MessageText.Text = message;
    }

    /// <summary>弹出崩溃提示弹窗，非阻塞（跟游戏进程无关的其它操作不需要等用户处理完这个弹窗）。</summary>
    public static void Show(MainWindow owner, string message, GameProcessInfo? processInfo)
    {
        var dlg = new CrashReportDialog(owner, message, processInfo);
        OverlayDialogService.ShowNonModal(dlg);
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        // "查看日志"：直接跳转到日志页——里面的"游戏日志"/"启动器日志"/"崩溃报告分析"三个
        // 标签已经能分别看到需求要求的三类内容，不需要在这个弹窗里再重复实现一遍只读预览。
        Close();
        _owner.NavigateToLogs();
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出完整日志",
            Filter = "文本文件|*.txt|所有文件|*.*",
            FileName = $"XCL2_崩溃日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            CrashLogExportService.ExportTo(dialog.FileName, _processInfo);
            MessageBoxDialog.ShowSuccess($"完整日志已导出到：\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导出失败：{ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
