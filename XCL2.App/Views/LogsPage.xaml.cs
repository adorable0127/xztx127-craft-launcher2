using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>模块扫描结果的展示包装（给 ListView 用）。</summary>
public class ModuleRow
{
    public string RiskLabel { get; init; } = "";
    public string FileName { get; init; } = "";
    public string? MatchedRule { get; init; }
    public string? CompanyName { get; init; }
    public string FullPath { get; init; } = "";
}

public partial class LogsPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly CrashAnalyzerService _crashAnalyzer = new();
    private readonly InjectionScanService _injectionScan = new();
    private List<(string path, DateTime modifiedAt)> _crashFiles = new();

    // 游戏日志 Tab：记录当前订阅了 OutputReceived 事件的进程，切换选择时需要先取消订阅旧的。
    private GameProcessInfo? _subscribedProcess;

    // 启动器日志 Tab：文件不会主动推送变化事件，改用轻量轮询定时器实现"自动刷新"。
    private readonly DispatcherTimer _launcherLogTimer;
    private DateTime _launcherLogLastWrite = DateTime.MinValue;

    public LogsPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        // 默认不显示日志面板，符合"小白 0 基础也能用"的定位：日志只是可选的高手工具。
        ShowLogCheck.IsChecked = _owner.ConfigService.Config.ShowLogPanel;
        ApplyVisibility();

        ReloadProcessCombos();
        RefreshLauncherLog_Click(this, new RoutedEventArgs());
        RescanCrash_Click(this, new RoutedEventArgs());

        _owner.ProcessManager.Changed += () => Dispatcher.Invoke(ReloadProcessCombos);

        // 启动器日志文件每 2 秒检查一次是否有更新，有变化才重新读取，避免频繁磁盘 IO。
        _launcherLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _launcherLogTimer.Tick += (_, _) => AutoRefreshLauncherLog();
        _launcherLogTimer.Start();

        Unloaded += (_, _) =>
        {
            _launcherLogTimer.Stop();
            if (_subscribedProcess != null) _subscribedProcess.OutputReceived -= OnGameLogLineReceived;
        };
    }

    private void ApplyVisibility()
    {
        var show = ShowLogCheck.IsChecked == true;
        LogTabs.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HiddenHint.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowLogCheck_Changed(object sender, RoutedEventArgs e)
    {
        _owner.ConfigService.Config.ShowLogPanel = ShowLogCheck.IsChecked == true;
        _owner.ConfigService.Save();
        ApplyVisibility();
    }

    private void ReloadProcessCombos()
    {
        var running = _owner.ProcessManager.Running;
        ProcessCombo.ItemsSource = running;
        ProcessCombo.DisplayMemberPath = nameof(GameProcessInfo.VersionId);
        if (ProcessCombo.SelectedItem == null && running.Count > 0) ProcessCombo.SelectedItem = running[0];

        ScanProcessCombo.ItemsSource = running;
        ScanProcessCombo.DisplayMemberPath = nameof(GameProcessInfo.VersionId);
        if (ScanProcessCombo.SelectedItem == null && running.Count > 0) ScanProcessCombo.SelectedItem = running[0];
    }

    // --- 游戏日志 Tab ---
    // 自动刷新原理：GameProcessInfo 每收到一行新的控制台输出就会触发 OutputReceived 事件，
    // 这里订阅这个事件、直接把新行追加到文本框末尾，不需要用户手动点"刷新"重新加载全部内容。
    private void ProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadGameLog();

    // "刷新"按钮保留：用于手动重新从头加载一次完整缓冲区（比如怀疑漏看了内容时）。
    private void RefreshGameLog_Click(object sender, RoutedEventArgs e) => LoadGameLog();

    private void LoadGameLog()
    {
        if (_subscribedProcess != null)
        {
            _subscribedProcess.OutputReceived -= OnGameLogLineReceived;
            _subscribedProcess = null;
        }

        if (ProcessCombo.SelectedItem is GameProcessInfo info)
        {
            lock (info.OutputBuffer)
                GameLogBox.Text = info.OutputBuffer.Length > 0 ? info.OutputBuffer.ToString() : "(暂无输出)";
            GameLogBox.ScrollToEnd();

            _subscribedProcess = info;
            info.OutputReceived += OnGameLogLineReceived;
        }
        else
        {
            GameLogBox.Text = "当前没有正在运行的游戏进程。启动游戏后这里会实时显示游戏的控制台输出。";
        }
    }

    private void OnGameLogLineReceived(string line)
    {
        Dispatcher.Invoke(() =>
        {
            // 双重确认：事件可能是异步线程触发的，真正写入 UI 前确认订阅关系没有在这期间被切换掉。
            if (!ReferenceEquals(ProcessCombo.SelectedItem, _subscribedProcess)) return;
            GameLogBox.AppendText(line + Environment.NewLine);
            GameLogBox.ScrollToEnd();
        });
    }

    // --- 启动器日志 Tab ---
    // "刷新"按钮保留，供用户手动强制重新读取一次（比如怀疑轮询没生效时）。
    private void RefreshLauncherLog_Click(object sender, RoutedEventArgs e) => LoadLauncherLog(force: true);

    /// <summary>由定时器每 2 秒调用一次：只有文件的最后修改时间变化了才重新读取，避免无意义的磁盘 IO。</summary>
    private void AutoRefreshLauncherLog() => LoadLauncherLog(force: false);

    private void LoadLauncherLog(bool force)
    {
        try
        {
            var crashLog = Path.Combine(App.DataDir, "logs", "crash.log");
            if (!File.Exists(crashLog))
            {
                if (force) LauncherLogBox.Text = "(暂无启动器日志，说明启动器运行正常，没有记录到异常。)";
                return;
            }

            var lastWrite = File.GetLastWriteTimeUtc(crashLog);
            if (!force && lastWrite == _launcherLogLastWrite) return; // 文件没变化，跳过这次刷新

            _launcherLogLastWrite = lastWrite;
            LauncherLogBox.Text = File.ReadAllText(crashLog);
            LauncherLogBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            if (force) LauncherLogBox.Text = "读取启动器日志失败: " + ex.Message;
        }
    }

    // --- 崩溃报告分析 Tab ---
    private void RescanCrash_Click(object sender, RoutedEventArgs e)
    {
        _crashFiles.Clear();
        foreach (var folder in _owner.ConfigService.Config.Folders)
        {
            try { _crashFiles.AddRange(_crashAnalyzer.ListCrashFiles(folder.Path)); }
            catch { /* 忽略单个目录扫描失败 */ }
        }
        _crashFiles = _crashFiles.OrderByDescending(f => f.modifiedAt).ToList();

        CrashFileCombo.ItemsSource = _crashFiles.Select(f => $"{Path.GetFileName(f.path)}  ({f.modifiedAt:yyyy-MM-dd HH:mm})").ToList();
        if (_crashFiles.Count == 0)
        {
            CrashFindingsText.Text = "没有发现崩溃报告文件，说明目前没有检测到游戏崩溃记录。";
            CrashRawBox.Text = "";
        }
        else
        {
            CrashFileCombo.SelectedIndex = 0;
        }
    }

    private void CrashFileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = CrashFileCombo.SelectedIndex;
        if (idx < 0 || idx >= _crashFiles.Count) return;

        var result = _crashAnalyzer.Analyze(_crashFiles[idx].path);
        CrashFindingsText.Text = string.Join("\n\n", result.Findings.Select((f, i) => $"{i + 1}. {f}"));
        CrashRawBox.Text = result.RawText;
    }

    // --- 注入检测 Tab ---
    private void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        if (ScanProcessCombo.SelectedItem is not GameProcessInfo info)
        {
            MessageBox.Show("请先选择一个正在运行的游戏进程。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = _injectionScan.Scan(info.Process);
        ModuleListView.ItemsSource = result.Modules
            .OrderByDescending(m => m.Risk)
            .Select(m => new ModuleRow
            {
                RiskLabel = m.Risk switch
                {
                    ModuleRisk.Suspicious => "⚠ 可疑",
                    ModuleRisk.Unknown => "? 未知",
                    _ => "✓ 可信"
                },
                FileName = m.FileName,
                MatchedRule = m.MatchedRule,
                CompanyName = m.CompanyName,
                FullPath = m.FullPath
            })
            .ToList();

        if (result.HasSuspiciousModule)
            MessageBox.Show("扫描发现可疑模块，请在列表中查看标记为「⚠ 可疑」的条目，建议立即处理。",
                "注入检测", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
