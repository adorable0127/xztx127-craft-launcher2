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
    private readonly DateTime _incidentObservedAt = DateTime.Now;
    private CancellationTokenSource? _aiLogReadCts;
    private bool _closed;

    private CrashReportDialog(MainWindow owner, string message, GameProcessInfo? processInfo, bool suppressAutomaticAnalysis = false, string? title = null)
    {
        InitializeComponent();
        _owner = owner;
        _processInfo = processInfo;
        RequestClose += (_, _) =>
        {
            _closed = true;
            _aiLogReadCts?.Cancel();
        };
        MessageText.Text = message;
        if (!string.IsNullOrWhiteSpace(title)) TitleText.Text = title;
        if (!suppressAutomaticAnalysis && processInfo != null)
        {
            var evidence = OpenGlTroubleshooter.ReadCurrentIncidentEvidence(processInfo);
            if (OpenGlTroubleshooter.IsOpenGlFailure(evidence))
            {
                TitleText.Text = "OpenGL 初始化失败";
                MessageText.Text = "游戏没有成功创建所需的图形环境。已识别本次日志的 OpenGL 错误，请优先尝试下方修复步骤，无需直接提交崩溃报告。";
                OpenGlRepairMessage.Text = OpenGlTroubleshooter.RepairSteps;
                var logPath = OpenGlTroubleshooter.CaptureIncident(processInfo, evidence);
                OpenGlEvidencePath.Text = logPath == null ? "保存诊断日志失败，可通过下方按钮导出完整日志。" : "本次 OpenGL 诊断日志：" + logPath;
                OpenGlRepairSection.Visibility = Visibility.Visible;
            }
        }
        if (!suppressAutomaticAnalysis) RunInlineAnalysis();
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
            var files = CrashAiLogService.GetCurrentIncidentFiles(_processInfo, _incidentObservedAt);
            if (files.Count == 0) return;

            // 只分析本次启动期间写入的文件；优先崩溃报告/JVM 报告，再使用 latest.log。
            var latest = files[0];
            var result = analyzer.Analyze(latest);
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
            AnalysisSourceText.Text = $"分析依据：{Path.GetFileName(latest)}（{result.ModifiedAt:yyyy-MM-dd HH:mm}）。" +
                "这是启发式自动分析，仅供定位问题参考，不保证 100% 准确；完整原文可点下方「在记事本中查看日志」或「导出完整日志」。";
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

    /// <summary>JVM 启动参数错误专用弹窗：Java 虚拟机都没起来，游戏代码根本没跑过，所以没有崩溃报告，
    /// 也不该再去分析磁盘上可能是上一局遗留的 crash-reports/latest.log（会张冠李戴）。
    /// 因此关闭自动分析，直接把 <see cref="JvmArgumentDiagnostics"/> 的结论显示在分析区域。</summary>
    public static void ShowJvmArgumentError(MainWindow owner, GameProcessInfo processInfo, int exitCode,
        IReadOnlyList<JvmArgIssue> issues)
    {
        var message = $"游戏没能启动：Java 虚拟机（JVM）的启动参数有问题（退出码 {exitCode}）。\n\n" +
                      "这类错误发生在游戏代码运行之前，所以不会有崩溃报告，也不需要提交崩溃报告。按下面的结论修改后重新启动即可。";
        var dlg = new CrashReportDialog(owner, message, processInfo, suppressAutomaticAnalysis: true, title: "JVM 参数错误");
        dlg.ShowJvmIssues(issues);
        OverlayDialogService.ShowNonModal(dlg);
    }

    private void ShowJvmIssues(IReadOnlyList<JvmArgIssue> issues)
    {
        AnalysisText.Text = JvmArgumentDiagnostics.FormatForDialog(issues);
        AnalysisSourceText.Text = "分析依据：本次游戏进程的控制台输出（JVM 启动阶段的报错）。" +
            "自定义 JVM 参数在「实例设置 / 设置」里修改，清空后即恢复默认。完整输出可点下方「在记事本中查看日志」。";
        AnalysisSection.Visibility = Visibility.Visible;
    }

    /// <summary>日志只留下半截、又没有明确异常证据时，不把它包装成“游戏崩溃”。
    /// 正文严格只显示用户要求的一行，同时关闭 OpenGL/自动崩溃分析，避免下面又冒出一堆
    /// 与“可能是手动关闭”互相矛盾的诊断结论。</summary>
    public static void ShowPossibleManualClose(MainWindow owner, GameProcessInfo processInfo)
    {
        var dlg = new CrashReportDialog(owner, "可能是用户手动关闭了游戏", processInfo,
            suppressAutomaticAnalysis: true, title: "游戏已退出");
        dlg.AnalyzeCrashWithAiButton.Visibility = Visibility.Collapsed;
        OverlayDialogService.ShowNonModal(dlg);
    }

    /// <summary>"补全 DirectX"：从微软官方下载并静默安装 DirectX 最终用户运行库，不是显卡驱动。</summary>
    private void InstallDirectX_Click(object sender, RoutedEventArgs e)
        => DirectXRuntimeInstallService.StartWithConfirmation();

    private void OpenDirectXPage_Click(object sender, RoutedEventArgs e)
        => ExternalLinkService.Open(DirectXRuntimeInstallService.OfficialPage);

    private void InstallOpenGlDriver_Click(object sender, RoutedEventArgs e)
        => WindowsDisplayDriverInstallService.StartWithConfirmation();

    private async void AnalyzeCrashWithAi_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _aiLogReadCts != null) return;
        var readCts = new CancellationTokenSource();
        _aiLogReadCts = readCts;
        AnalyzeCrashWithAiButton.IsEnabled = false;
        try
        {
            var prompt = await _owner.PrepareCrashAiPromptAsync(_processInfo, _incidentObservedAt, MessageText.Text, readCts.Token);
            if (_closed || readCts.IsCancellationRequested) return;
            // 先收起崩溃提示，再显示覆盖在原页面上的助手；关闭助手不会重新弹出崩溃提示。
            Close();
            var title = _processInfo == null ? "当前崩溃日志分析" : _processInfo.VersionId + " · 崩溃日志分析";
            await _owner.ShowCrashAnalysisOverlayAsync(prompt, title);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("准备崩溃 AI 分析失败", ex);
            MessageBoxDialog.ShowError("打开崩溃日志分析失败：" + ex.Message, "崩溃日志分析");
        }
        finally
        {
            if (ReferenceEquals(_aiLogReadCts, readCts)) _aiLogReadCts = null;
            readCts.Dispose();
            if (!_closed) AnalyzeCrashWithAiButton.IsEnabled = true;
        }
    }

    /// <summary>"在记事本中查看日志"：跟"查看完整日志"（其实是跳转到启动器内部的日志页）
    /// 分开——那个按钮名字容易让人以为点了会拉起系统记事本，其实只是启动器自己的一个页面，
    /// 存在误导。这里把 CrashLogExportService 组装的同一份合并日志（启动器日志+游戏崩溃前
    /// 输出+游戏日志文件）写到临时文件，再真正用系统记事本（notepad.exe）打开它，做到
    /// 名副其实：点了就是打开记事本，可以直接搜索/复制/另存/发给别人。
    ///
    /// 用临时文件而不是直接找一个已有的日志文件打开，是因为"合并日志"本身就是三段内容
    /// 拼接出来的新文本，磁盘上并不存在这样一份现成文件——跟"导出完整日志"用的是同一个
    /// BuildCombinedLog，只是这里不用用户手选保存位置，直接扔进临时目录即开即用。
    /// 临时文件不做清理：内容量小、不敏感到需要即用即删的程度，且 Windows 会按自己的策略
    /// 定期清理 %TEMP%，不需要这里额外操心生命周期。</summary>
    private void OpenInNotepad_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"XCL2_崩溃日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(tempPath, CrashLogExportService.BuildCombinedLog(_processInfo));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{tempPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // 极少数情况下（比如系统记事本被彻底删除/替换、临时目录写不进去）打不开，
            // 退化成友好错误提示，不影响弹窗其它按钮继续可用。
            MessageBoxDialog.ShowError($"用记事本打开日志失败：{ex.Message}");
        }
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
