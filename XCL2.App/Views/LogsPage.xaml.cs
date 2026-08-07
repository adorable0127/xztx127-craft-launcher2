using System.IO;
using System.Text;
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

    // 需求：游戏崩溃退出后，这个 Tab 不应该瞬间"空屏"——用户往往就是想在崩溃那一刻
    // 立刻看最后几行输出来定位问题，日志一闪而空对排错完全没有帮助。
    // 用户手动点"清空本视图内的日志"按钮之前，都不应该清空。
    //
    // 之前的实现完全没有这个考虑：ReloadProcessCombos 每次进程列表变化（包括进程退出后
    // 从"运行中列表"移除）都会重新绑定 ProcessCombo.ItemsSource，一旦当前选中的进程不在
    // 新列表里了，SelectionChanged 触发 LoadGameLog()，走到 "else" 分支直接把
    // GameLogBox.Text 整个替换成"当前没有正在运行的游戏进程"提示语——玩家刚看到的崩溃输出
    // 瞬间被这句提示覆盖掉，等于自己伸手把最有用的信息删了。
    //
    // 修复方式：额外记一份"这个进程退出前最后订阅到的完整日志文本"快照 (_lastKnownLogSnapshot)，
    // 在 LoadGameLog() 因为找不到匹配进程要显示占位提示时，如果这份快照非空，
    // 优先继续展示快照内容（并追加一行明显的分隔提示，说明"进程已结束，以下为退出前的日志"），
    // 而不是清空。只有用户主动点了"清空本视图内的日志"按钮，或者选中了一个新的、
    // 确实在运行的进程时，才会真正替换/清空这份快照。
    private string? _lastKnownLogSnapshot;
    private string? _lastKnownLogVersionId;

    // 启动器日志 Tab：文件不会主动推送变化事件，改用轻量轮询定时器实现"自动刷新"。
    private readonly DispatcherTimer _launcherLogTimer;
    private DateTime _launcherLogLastWrite = DateTime.MinValue;

    // 完整启动器日志 Tab：内容来自纯内存的 LauncherLogService.Buffer，没有文件可供比对修改时间，
    // 所以直接每秒无条件读一次（内存操作，开销可忽略），而不是像上面文件轮询那样先判断有没有变化。
    private readonly DispatcherTimer _fullLauncherLogTimer;

    // 独立启动器日志窗口：见 LauncherLogWindowService 类头注释。只在用户点了"独立窗口"按钮
    // 之后才会创建；可以重复点击复用同一个实例（Open() 内部每次都会弹一个新窗口+新同步定时器，
    // 这里只是持有引用方便页面卸载时统一 Dispose 掉同步定时器）。
    private LauncherLogWindowService? _launcherLogWindowService;

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
        LoadFullLauncherLog();

        _owner.ProcessManager.Changed += () => Dispatcher.Invoke(ReloadProcessCombos);

        // 启动器日志文件每 2 秒检查一次是否有更新，有变化才重新读取，避免频繁磁盘 IO。
        _launcherLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _launcherLogTimer.Tick += (_, _) => AutoRefreshLauncherLog();
        _launcherLogTimer.Start();

        // 完整启动器日志每秒自动刷新一次。纯内存读取，没有磁盘 IO，
        // 但如果用户觉得费性能/费内存，可以用 Tab 里的"刷新"按钮手动模式代替
        // （后续计划在设置页加一个开关，允许关掉这个自动定时器、隐藏刷新按钮改成纯手动）。
        _fullLauncherLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullLauncherLogTimer.Tick += (_, _) => AutoRefreshFullLauncherLog();
        _fullLauncherLogTimer.Start();

        Unloaded += (_, _) =>
        {
            _launcherLogTimer.Stop();
            _fullLauncherLogTimer.Stop();
            if (_subscribedProcess != null) _subscribedProcess.OutputReceived -= OnGameLogLineReceived;
            // 只停掉我们进程内的同步定时器；已经弹出的独立日志窗口本身不受影响，继续独立存在
            // （见 LauncherLogWindowService.Dispose 的注释：这正是"不依附于咱们的进程"的应有之义）。
            _launcherLogWindowService?.Dispose();
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
                GameLogBox.Text = info.OutputBuffer.Length > 0 ? info.OutputBuffer.ToString() : Loc.T("Str_Cs_No_Output_Yet", "(暂无输出)");
            GameLogBox.ScrollToEnd();

            _subscribedProcess = info;
            info.OutputReceived += OnGameLogLineReceived;

            // 记下这份快照：万一这个进程接下来崩溃/退出、从"运行中列表"里消失，
            // 下次 LoadGameLog 找不到匹配进程时还能回退展示这份内容，而不是清空。
            lock (info.OutputBuffer) _lastKnownLogSnapshot = info.OutputBuffer.ToString();
            _lastKnownLogVersionId = info.VersionId;
        }
        else if (!string.IsNullOrEmpty(_lastKnownLogSnapshot))
        {
            // 找不到匹配的运行中进程（通常是游戏刚崩溃/退出，进程从列表里被移除了），
            // 但我们手上还留着它退出前的日志快照——继续展示这份内容，不清空，
            // 并且在末尾加一行明显的提示，说明这是"进程已结束时的最后日志"而不是实时输出，
            // 避免用户误以为游戏还在运行。
            GameLogBox.Text = _lastKnownLogSnapshot.TrimEnd('\r', '\n') + Environment.NewLine +
                Environment.NewLine +
                $"==== [{_lastKnownLogVersionId}] 进程已退出，以上为退出前的最后日志（点击上方\"清空本视图内的日志\"可清空此视图） ====";
            GameLogBox.ScrollToEnd();
        }
        else
        {
            GameLogBox.Text = Loc.T("Str_Cs_No_Game_Process_Is_Running_Once_You_Laun", "当前没有正在运行的游戏进程。启动游戏后这里会实时显示游戏的控制台输出。");
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
            // 快照跟着实时输出同步更新，保证进程真退出的那一刻快照是完整的（含最后一行）。
            if (_subscribedProcess != null)
                lock (_subscribedProcess.OutputBuffer) _lastKnownLogSnapshot = _subscribedProcess.OutputBuffer.ToString();
        });
    }

    /// <summary>"清空本视图内的日志"按钮：只清空这个 Tab 当前显示的文本和内部快照，
    /// 不影响 GameProcessInfo.OutputBuffer（进程管理等其它地方还依赖它），也不删磁盘上的
    /// latest.log/crash.log。清空后如果选中的进程仍在运行，会继续正常实时追加新日志——
    /// 只是清掉了"清空这一刻之前"的历史内容。</summary>
    private void ClearGameLogView_Click(object sender, RoutedEventArgs e)
    {
        GameLogBox.Text = Loc.T("Str_Cs_No_Output_Yet", "(暂无输出)");
        _lastKnownLogSnapshot = null;
        _lastKnownLogVersionId = null;
    }

    // --- 启动器日志 Tab ---
    // "刷新"按钮保留，供用户手动强制重新读取一次（比如怀疑轮询没生效时）。
    private void RefreshLauncherLog_Click(object sender, RoutedEventArgs e) => LoadLauncherLog(force: true);

    // --- 完整启动器日志 Tab ---
    // 显示的是"当前会话"的内存日志（LauncherLogService.Buffer），不是磁盘上的历史 .log/crash.log 文件，
    // 所以每次刷新都是纯内存读取，没有磁盘 IO，可以放心每秒自动刷新一次。
    private void RefreshFullLauncherLog_Click(object sender, RoutedEventArgs e) => LoadFullLauncherLog();

    /// <summary>需求："加入一个按钮，可以独立出一个启动器日志，不依附于咱们的进程"。
    /// 见 LauncherLogWindowService 类头注释：弹出一个真正独立的 cmd 进程持续 tail 日志文件，
    /// 不是同进程内的第二个 WPF 窗口。每次点击都弹一个新窗口——用户可能想同时开好几个
    /// （比如一个专门盯着看，一个截图存档），不做"只能开一个"的限制，跟大多数支持多开
    /// 日志窗口的工具（比如各类 tail 工具）的习惯一致。</summary>
    private void OpenLauncherLogInIndependentWindow_Click(object sender, RoutedEventArgs e)
    {
        // 复用同一个字段持有"最近一次"打开的实例，页面 Unloaded 时统一 Dispose 掉同步定时器；
        // 旧实例（如果还在）先 Dispose 一次，避免两个同步定时器同时写同一批新增内容造成浪费
        // ——不影响旧窗口本身的显示，旧窗口对应的文件已经写好的内容还在，只是不再收到新的
        // 增量更新（用户如果还想继续看最新日志，重新点一次这个按钮弹新窗口即可）。
        _launcherLogWindowService?.Dispose();
        _launcherLogWindowService = new LauncherLogWindowService();
        _launcherLogWindowService.Open();
    }

    /// <summary>由 _fullLauncherLogTimer 每秒调用一次。</summary>
    private void AutoRefreshFullLauncherLog() => LoadFullLauncherLog();

    private void LoadFullLauncherLog()
    {
        var text = LauncherLogService.GetBufferedText();
        var newText = string.IsNullOrWhiteSpace(text)
            ? Loc.T("Str_Cs_No_Session_Log_Yet", "(当前会话暂无内存日志)")
            : text;

        // 内容没变化就不重新赋值，避免每秒都重置一次滚动条位置，打断用户正在往上翻看历史的操作。
        if (newText == FullLauncherLogBox.Text) return;

        // 用户手动往上滚动查看历史时，不要把它拽回底部；只有本来就停在底部（或刚打开还没滚动过）才继续跟随最新内容。
        var wasAtBottom = FullLauncherLogBox.VerticalOffset >= FullLauncherLogBox.ExtentHeight - FullLauncherLogBox.ViewportHeight - 1;

        FullLauncherLogBox.Text = newText;
        if (wasAtBottom) FullLauncherLogBox.ScrollToEnd();
    }

    /// <summary>由定时器每 2 秒调用一次：只有文件的最后修改时间变化了才重新读取，避免无意义的磁盘 IO。</summary>
    private void AutoRefreshLauncherLog() => LoadLauncherLog(force: false);

    private void LoadLauncherLog(bool force)
    {
        try
        {
            var crashLog = Path.Combine(App.DataDir, "logs", "crash.log");
            if (!File.Exists(crashLog))
            {
                if (force) LauncherLogBox.Text = Loc.T("Str_Cs_No_Launcher_Log_Yet_Which_Means_The_Laun", "(暂无启动器日志，说明启动器运行正常，没有记录到异常。)");
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
            if (force) LauncherLogBox.Text = Loc.T("Str_Cs_Couldn_T_Read_The_Launcher_Log", "读取启动器日志失败: ") + ex.Message;
        }
    }

    private static string BuildAllLauncherLogsText()
    {
        var sb = new StringBuilder();
        var logDir = Path.Combine(App.DataDir, "logs");

        sb.AppendLine("===== 当前会话内存日志 =====");
        var buffered = LauncherLogService.GetBufferedText();
        sb.AppendLine(string.IsNullOrWhiteSpace(buffered) ? "(当前会话暂无内存日志)" : buffered.TrimEnd());
        sb.AppendLine();

        if (!Directory.Exists(logDir))
        {
            sb.AppendLine("===== 磁盘日志 =====");
            sb.AppendLine("(日志目录不存在)");
            return sb.ToString();
        }

        var files = Directory.EnumerateFiles(logDir, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), "crash.log", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderBy(f => string.Equals(f.Name, "crash.log", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        if (files.Count == 0)
        {
            sb.AppendLine("===== 磁盘日志 =====");
            sb.AppendLine("(没有找到 .log 或 crash.log 文件)");
            return sb.ToString();
        }

        foreach (var file in files)
        {
            sb.AppendLine($"===== {file.Name}  修改时间: {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}  大小: {file.Length} 字节 =====");
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                sb.AppendLine(reader.ReadToEnd().TrimEnd());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(读取失败: {ex.Message})");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // --- 崩溃报告分析 Tab ---
    private void RescanCrash_Click(object sender, RoutedEventArgs e)
    {
        _crashFiles.Clear();
        foreach (var folder in _owner.ConfigService.Config.Folders)
        {
            // .minecraft 根目录（版本隔离关闭时崩溃报告在这里）
            try { _crashFiles.AddRange(_crashAnalyzer.ListCrashFiles(folder.Path)); }
            catch { /* 忽略单个目录扫描失败 */ }

            // ===== 关键补充：逐个版本目录也要扫 =====
            // 版本隔离是**默认开启**的，开启时游戏的工作目录是 versions/<版本名>/，
            // crash-reports 和 logs 都写在那里面，而不是 .minecraft 根目录。
            // 之前只扫根目录，等于对绝大多数用户的崩溃记录完全失明——
            // 表现就是"游戏明明崩了，崩溃分析却说没有发现崩溃报告"。
            try
            {
                var versionsDir = Path.Combine(folder.Path, "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var versionDir in Directory.GetDirectories(versionsDir))
                    {
                        try { _crashFiles.AddRange(_crashAnalyzer.ListCrashFiles(versionDir)); }
                        catch { /* 单个版本目录扫描失败不影响其它 */ }
                    }
                }
            }
            catch { /* 忽略 */ }
        }

        // 同一个文件可能被根目录和版本目录两条路径扫到（隔离关闭时），按全路径去重。
        _crashFiles = _crashFiles
            .GroupBy(f => Path.GetFullPath(f.path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(f => f.modifiedAt)
            .ToList();

        // 现在会扫到多个版本目录下的同名文件（每个实例都有自己的 latest.log），
        // 只显示文件名的话完全分不清是哪个版本的，所以把上一级目录名一起带上。
        CrashFileCombo.ItemsSource = _crashFiles.Select(f =>
        {
            var owner = TryDescribeOwningInstance(f.path);
            return owner == null
                ? $"{Path.GetFileName(f.path)}  ({f.modifiedAt:yyyy-MM-dd HH:mm})"
                : $"[{owner}] {Path.GetFileName(f.path)}  ({f.modifiedAt:yyyy-MM-dd HH:mm})";
        }).ToList();
        if (_crashFiles.Count == 0)
        {
            CrashFindingsText.Text = Loc.T("Str_Cs_No_Crash_Reports_Found_So_No_Game_Crashe", "没有发现崩溃报告文件，说明目前没有检测到游戏崩溃记录。");
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

        // 带上置信度标记：让用户一眼分清"日志里明说的原因"和"启发式猜测"。
        // 不加区分的话，一条泛泛的"可能是内存不足"跟一条确定的"缺少前置 mod X"
        // 看起来分量一样，用户会去试错误的方向。
        CrashFindingsText.Text = result.RankedFindings.Count > 0
            ? string.Join("\n\n", result.RankedFindings.Select((f, i) =>
            {
                var tag = f.Confidence switch
                {
                    CrashConfidence.Certain => "【基本确定】",
                    CrashConfidence.Likely => "【很可能】",
                    _ => "【推测】",
                };
                return $"{i + 1}. {tag} {f.Text}";
            }))
            : string.Join("\n\n", result.Findings.Select((f, i) => $"{i + 1}. {f}"));

        CrashRawBox.Text = result.RawText;
    }

    /// <summary>从崩溃文件路径反推它属于哪个游戏实例（versions/&lt;名字&gt;/... → 名字）。
    /// 扫不出来就返回 null，界面退回只显示文件名。</summary>
    private static string? TryDescribeOwningInstance(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(dir))
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent != null &&
                    string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
                dir = parent;
            }
        }
        catch { }
        return null;
    }

    // --- 注入检测 Tab ---
    private void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        if (ScanProcessCombo.SelectedItem is not GameProcessInfo info)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Select_A_Running_Game_Process_Fir", "请先选择一个正在运行的游戏进程。"), Loc.T("Str_Status_Tip", "提示"));
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
            MessageBoxDialog.ShowWarning(Loc.T("Str_Cs_Suspicious_Modules_Were_Found_Check_The_", "扫描发现可疑模块，请在列表中查看标记为「⚠ 可疑」的条目，建议立即处理。"), Loc.T("Str_Cs_Injection_Scan", "注入检测"));
    }
}
