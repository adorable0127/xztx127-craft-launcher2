using System.IO;
using System.Linq;
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
        RunInlineAnalysis();
    }

    /// <summary>需求：把「日志分析」直接嵌进这个崩溃弹窗，不用再点"查看日志"跳去日志页找。
    /// 复用 LogsPage 崩溃报告分析 Tab 同一套 CrashAnalyzerService，只是这里自动定位到
    /// _processInfo.GameDir 下最新的一份崩溃文件/hs_err 日志/latest.log 来分析，不需要用户
    /// 自己在下拉框里挑。processInfo 为 null（极端情况下没能拿到进程对象）或者这个目录下
    /// 确实没有任何崩溃文件时，整块分析区域保持折叠不显示，不留一句空话唬人。</summary>
    private void RunInlineAnalysis()
    {
        if (_processInfo == null || string.IsNullOrEmpty(_processInfo.GameDir) || !Directory.Exists(_processInfo.GameDir))
            return;

        try
        {
            var analyzer = new CrashAnalyzerService();
            var files = analyzer.ListCrashFiles(_processInfo.GameDir);
            if (files.Count == 0) return;

            // ListCrashFiles 已经按修改时间倒序，第一个就是离这次崩溃最近的一份。
            var latest = files[0];
            var result = analyzer.Analyze(latest.path);
            if (result.RankedFindings.Count == 0 && result.Findings.Count == 0) return;

            var lines = result.RankedFindings.Count > 0
                ? result.RankedFindings.Select((f, i) =>
                {
                    var tag = f.Confidence switch
                    {
                        CrashConfidence.Certain => "【基本确定】",
                        CrashConfidence.Likely => "【很可能】",
                        _ => "【推测】",
                    };
                    return $"{i + 1}. {tag} {f.Text}";
                })
                : result.Findings.Select((f, i) => $"{i + 1}. {f}");

            AnalysisText.Text = string.Join("\n\n", lines);
            AnalysisSourceText.Text = $"分析依据：{Path.GetFileName(latest.path)}（{latest.modifiedAt:yyyy-MM-dd HH:mm}）。" +
                "这是启发式自动分析，仅供定位问题参考，不保证 100% 准确；完整原文可点下方「查看完整日志」。";
            AnalysisSection.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // 自动分析失败不应该影响崩溃弹窗本身的展示（弹窗的首要任务是告知"游戏崩了"），
            // 静默记日志即可，用户仍然可以用"查看完整日志"/"导出完整日志"两个按钮兜底。
            ErrorPresenter.LogFallback("崩溃弹窗内嵌分析失败", ex);
        }
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
