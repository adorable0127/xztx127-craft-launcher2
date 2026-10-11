using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 本地 Mod 管理：选择文件夹 + 版本后，扫描该版本生效的 mods 目录（跟随"版本隔离"设置，
/// 与 LauncherService.BuildArguments 里 EffectiveGameDir 的计算逻辑保持一致——隔离开启时是
/// versions/&lt;id&gt;/mods，关闭时是 .minecraft 根目录下的 mods，否则管理的目录和实际游戏读取
/// 的目录对不上，会出现"这里删了/禁用了，游戏里还在"的错乱）。
///
/// 同时提供整合包导入导出入口，导出/导入的目标目录同样是这个"生效版本目录"，
/// 而不是 .minecraft 根目录，逻辑与本地 Mod 管理保持一致。
/// </summary>
public partial class ModManagerPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly FolderService _folderService = new();
    private readonly LocalModService _localModService = new();
    private readonly ModpackService _modpackService = new();
    private readonly ModDependencyAnalysisService _dependencyAnalysisService = new();

    private readonly ObservableCollection<GameFolder> _folders = new();
    private IReadOnlyList<GameVersion> _versions = Array.Empty<GameVersion>();
    private IReadOnlyList<LocalModDisplayItem> _mods = Array.Empty<LocalModDisplayItem>();
    private CancellationTokenSource? _versionScanCancellation;
    private CancellationTokenSource? _modScanCancellation;
    private CancellationTokenSource? _dependencyAnalysisCancellation;
    private int _versionScanGeneration;
    private int _modScanGeneration;
    private int _dependencyAnalysisGeneration;
    private bool _updatingSelection;
    private bool _isLoadingVersions;
    private bool _isLoadingMods;

    // 监视当前实例的 mods 文件夹：不管是拖拽导入、下载、还是用户手动往文件夹里丢文件，
    // 只要内容变了就自动刷新列表。防抖是因为复制一个 jar 会连续触发好几次文件事件。
    private FileSystemWatcher? _modsWatcher;
    private string? _watchedModsDir;
    private readonly System.Windows.Threading.DispatcherTimer _watchDebounce;

    public ModManagerPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        _watchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            RefreshMods();
        };
        // 先让页面的轻量框架完成入树。构造函数不扫描磁盘，也不打开任何 JAR。
        Loaded += (_, _) => LoadFolders();
        Unloaded += (_, _) =>
        {
            CancelVersionScan();
            CancelModScan();
            StopWatchingMods();
        };

        FolderCombo.ItemsSource = _folders;
        VersionCombo.ItemsSource = _versions;
        VersionCombo.DisplayMemberPath = nameof(GameVersion.Id);
        ModsListBox.ItemsSource = _mods;
        LoadingHost.Content = LoadingPlaceholderFactory.Create("正在读取版本与模组信息");
    }

    private void LoadFolders()
    {
        if (!IsLoaded) return;
        _updatingSelection = true;
        try
        {
            _folders.Clear();
            foreach (var f in _owner.ConfigService.Config.Folders ?? new List<GameFolder>()) _folders.Add(f);
            FolderCombo.SelectedItem = _folders.FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
                ?? _folders.FirstOrDefault();
        }
        finally { _updatingSelection = false; }
        if (FolderCombo.SelectedItem is GameFolder selected &&
            !string.Equals(_owner.ConfigService.Config.SelectedFolderPath, selected.Path, StringComparison.OrdinalIgnoreCase))
        {
            _owner.ConfigService.Config.SelectedFolderPath = selected.Path;
            _owner.ConfigService.Save();
        }
        RefreshVersions();
    }

    private void RefreshVersions() => _ = RefreshVersionsAsync();

    private async Task RefreshVersionsAsync()
    {
        if (!IsLoaded) return;
        CancelVersionScan();
        CancelModScan();
        StopWatchingMods();
        SetVersions(Array.Empty<GameVersion>(), null);
        SetMods(Array.Empty<LocalModInfo>());

        var folder = FolderCombo.SelectedItem as GameFolder;
        if (folder == null || string.IsNullOrEmpty(folder.Path))
        {
            UpdateEmptyHint();
            return;
        }

        // 路径和配置必须在 UI 线程取快照；后台只处理普通数据，不访问控件/集合。
        var folderPath = folder.Path;
        var selectedId = _owner.ConfigService.Config.SelectedVersionId;
        var generation = _versionScanGeneration;
        var cancellation = new CancellationTokenSource();
        _versionScanCancellation = cancellation;
        _isLoadingVersions = true;
        UpdateEmptyHint();
        try
        {
            var versions = await Task.Run(() => _folderService.ScanVersions(folderPath, cancellation.Token), cancellation.Token);
            if (!IsLoaded || cancellation.IsCancellationRequested || generation != _versionScanGeneration) return;
            SetVersions(versions, selectedId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsLoaded && !cancellation.IsCancellationRequested && generation == _versionScanGeneration)
                ErrorPresenter.ShowFriendlyError("扫描已安装版本失败，可能是这个文件夹不是有效的 .minecraft 目录，或权限不足。",
                    ex.ToString(), "扫描版本失败");
        }
        finally
        {
            if (generation == _versionScanGeneration)
            {
                _versionScanCancellation = null;
                _isLoadingVersions = false;
                UpdateEmptyHint();
            }
            cancellation.Dispose();
        }
        if (IsLoaded && generation == _versionScanGeneration) RefreshMods();
    }

    private void SetVersions(IReadOnlyList<GameVersion> versions, string? selectedId)
    {
        _updatingSelection = true;
        try
        {
            _versions = versions;
            VersionCombo.ItemsSource = _versions;
            VersionCombo.SelectedItem = _versions.FirstOrDefault(v => v.Id == selectedId) ?? _versions.FirstOrDefault();
        }
        finally { _updatingSelection = false; }
    }

    private void SetMods(IEnumerable<LocalModInfo> mods)
    {
        // 一次更换数据源，避免每个模组分别触发集合通知/布局；列表仍使用虚拟化。
        _mods = mods.Select(m => new LocalModDisplayItem(m)).ToArray();
        ModsListBox.ItemsSource = _mods;
    }

    private void CancelVersionScan()
    {
        CancelDependencyAnalysis();
        ++_versionScanGeneration;
        _versionScanCancellation?.Cancel();
        _versionScanCancellation = null;
        _isLoadingVersions = false;
    }

    private void CancelModScan()
    {
        ++_modScanGeneration;
        _modScanCancellation?.Cancel();
        _modScanCancellation = null;
        _isLoadingMods = false;
    }

    private void CancelDependencyAnalysis()
    {
        ++_dependencyAnalysisGeneration;
        _dependencyAnalysisCancellation?.Cancel();
        _dependencyAnalysisCancellation = null;
    }

    /// <summary>
    /// 计算当前选中版本"生效的游戏目录"，跟随版本隔离设置——逻辑必须与
    /// LauncherService.BuildArguments 里的 gameDir 计算保持一致，见类注释。
    /// </summary>
    private string? GetEffectiveGameDir()
    {
        var folder = FolderCombo.SelectedItem as GameFolder;
        var version = VersionCombo.SelectedItem as GameVersion;
        if (folder == null || version == null) return null;

        var cfg = _owner.ConfigService.Config;
        var isolate = cfg.VersionIsolationOverrides.TryGetValue(version.Id, out var overrideValue)
            ? overrideValue
            : cfg.IsolateVersionsByDefault;

        return isolate ? Path.Combine(folder.Path, "versions", version.Id) : folder.Path;
    }

    /// <summary>供主窗口调用：拖拽导入等操作装完文件后，如果用户正停在这个页面，立刻刷新列表。</summary>
    public void RefreshFromExternalChange()
    {
        if (Dispatcher.CheckAccess()) RefreshMods();
        else _ = Dispatcher.BeginInvoke(new Action(RefreshMods));
    }

    /// <summary>实例导入后刷新目录和版本选项，使新实例立即可选。</summary>
    internal void RefreshVersionsFromExternalChange()
    {
        if (Dispatcher.CheckAccess()) LoadFolders();
        else _ = Dispatcher.BeginInvoke(new Action(LoadFolders));
    }

    private void WatchModsDir(string gameDir)
    {
        var modsDir = Path.Combine(gameDir, "mods");
        if (_modsWatcher != null && string.Equals(_watchedModsDir, modsDir, StringComparison.OrdinalIgnoreCase)) return;

        StopWatchingMods();
        try
        {
            if (!Directory.Exists(modsDir)) return;

            var watcher = new FileSystemWatcher(modsDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            void OnChanged() => Dispatcher.BeginInvoke(new Action(() =>
            {
                // 已排队的旧 watcher 事件不能在切走后重新启动计时器。
                if (!IsLoaded || !ReferenceEquals(_modsWatcher, watcher)) return;
                _watchDebounce.Stop();
                _watchDebounce.Start();
            }));
            watcher.Created += (_, _) => OnChanged();
            watcher.Deleted += (_, _) => OnChanged();
            watcher.Changed += (_, _) => OnChanged();
            watcher.Renamed += (_, _) => OnChanged();
            watcher.EnableRaisingEvents = true;

            _modsWatcher = watcher;
            _watchedModsDir = modsDir;
        }
        catch
        {
            // 监视失败（权限/网络盘等）不影响功能，用户仍然可以手动点刷新。
        }
    }

    private void StopWatchingMods()
    {
        _watchDebounce.Stop();
        if (_modsWatcher != null)
        {
            _modsWatcher.EnableRaisingEvents = false;
            _modsWatcher.Dispose();
            _modsWatcher = null;
        }
        _watchedModsDir = null;
    }

    private void RefreshMods() => _ = RefreshModsAsync();

    private async Task RefreshModsAsync()
    {
        if (!IsLoaded || _isLoadingVersions) return;
        // 显式刷新会取消还在排队的"文件变化"刷新，避免同一件事刷两遍。
        _watchDebounce.Stop();
        CancelModScan();
        var gameDir = GetEffectiveGameDir();
        if (gameDir == null)
        {
            StopWatchingMods();
            SetMods(Array.Empty<LocalModInfo>());
            UpdateEmptyHint();
            return;
        }

        var generation = _modScanGeneration;
        var cancellation = new CancellationTokenSource();
        _modScanCancellation = cancellation;
        _isLoadingMods = true;
        UpdateEmptyHint();
        try
        {
            // 先订阅变更，再读快照，避免扫描过程中复制/删除的文件漏掉后续刷新。
            WatchModsDir(gameDir);
            var mods = await Task.Run(() => _localModService.ScanMods(gameDir, cancellation.Token), cancellation.Token);
            if (!IsLoaded || cancellation.IsCancellationRequested || generation != _modScanGeneration) return;
            SetMods(mods);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsLoaded && !cancellation.IsCancellationRequested && generation == _modScanGeneration)
            {
                SetMods(Array.Empty<LocalModInfo>());
                ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Scan_The_Mod_List_A_File_May_Be", "扫描 Mod 列表失败，可能是文件被占用或权限不足，请重试。"),
                    ex.ToString(), "扫描 Mod 列表失败");
            }
        }
        finally
        {
            if (generation == _modScanGeneration)
            {
                _modScanCancellation = null;
                _isLoadingMods = false;
                UpdateEmptyHint();
            }
            cancellation.Dispose();
        }
    }

    private void UpdateEmptyHint()
    {
        var isLoading = _isLoadingVersions || _isLoadingMods;
        LoadingHost.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        VersionCombo.IsEnabled = !_isLoadingVersions;
        var hasSelection = FolderCombo.SelectedItem != null && VersionCombo.SelectedItem != null;
        EmptyHint.Visibility = !isLoading && _mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = !hasSelection
            ? "请先选择一个文件夹和版本。"
            : "这个版本还没有安装任何 Mod，去「下载」页的 Mod 分类搜索安装吧。";
        ModsListBox.Visibility = isLoading || _mods.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection) return;
        if (FolderCombo.SelectedItem is GameFolder folder)
        {
            _owner.ConfigService.Config.SelectedFolderPath = folder.Path;
            _owner.ConfigService.Save();
        }
        RefreshVersions();
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection) return;
        CancelDependencyAnalysis();
        RefreshMods();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshMods();

    /// <summary>
    /// 交接文档需求：前置模组分析。手动点"检查前置模组"按钮触发（不做成每次刷新都自动弹窗——
    /// 用户可能就是想先装个前置再装主 mod，中间状态本来就会"缺前置"，如果每次刷新列表都弹一次
    /// 警告框会很烦人，改成用户主动点了才检查更符合"提示而不是打扰"的分寸）。
    /// </summary>
    private async void AnalyzeDependencies_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GetEffectiveGameDir();
        var selectedVersion = VersionCombo.SelectedItem as GameVersion;
        if (gameDir == null || selectedVersion == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Folder_And_A_Version_Fir", "请先选择一个文件夹和版本。"));
            return;
        }

        // 切换实例后，旧分析既不能弹窗，也不能拿新实例的加载器为旧实例下载前置。
        var selectedId = selectedVersion.Id;
        var mcVersion = selectedVersion.McVersion;
        var modLoader = selectedVersion.ModLoader;
        CancelDependencyAnalysis();
        var generation = _dependencyAnalysisGeneration;
        var cancellation = new CancellationTokenSource();
        _dependencyAnalysisCancellation = cancellation;
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        bool IsCurrentRequest() => IsLoaded && !cancellation.IsCancellationRequested &&
            generation == _dependencyAnalysisGeneration &&
            (VersionCombo.SelectedItem as GameVersion)?.Id == selectedId &&
            string.Equals(GetEffectiveGameDir(), gameDir, StringComparison.OrdinalIgnoreCase);
        try
        {
            ToastService.ShowInfo("正在检查前置模组…");
            var result = await Task.Run(() =>
            {
                var enabledMods = _localModService.ScanMods(gameDir, cancellation.Token)
                    .Where(m => m.IsEnabled).ToList();
                cancellation.Token.ThrowIfCancellationRequested();
                var analysis = _dependencyAnalysisService.Analyze(enabledMods);
                cancellation.Token.ThrowIfCancellationRequested();
                return (EnabledMods: enabledMods, Analysis: analysis);
            }, cancellation.Token);
            if (!IsCurrentRequest()) return;
            if (!result.Analysis.HasMissingDependencies)
            {
                MessageBoxDialog.ShowInfo("没有发现缺失的前置模组。", "检查完成");
                return;
            }

            // 使用请求发起时的游戏版本与加载器；后台工作仅返回普通数据。
            var warnWin = new ModDependencyWarningWindow(result.Analysis, gameDir, result.EnabledMods,
                mcVersion, modLoader);
            warnWin.ModsChanged += (_, _) => RefreshMods();
            warnWin.ShowDialog();
            RefreshMods();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentRequest())
                ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Scan_The_Mod_List_A_File_May_Be", "扫描 Mod 列表失败，可能是文件被占用或权限不足，请重试。"),
                    ex.ToString(), "检查前置模组失败");
        }
        finally
        {
            if (ReferenceEquals(_dependencyAnalysisCancellation, cancellation))
                _dependencyAnalysisCancellation = null;
            cancellation.Dispose();
            if (button != null) button.IsEnabled = true;
        }
    }

    /// <summary>
    /// 一键批量升级：扫描当前实例 mods 文件夹里所有能在 Modrinth 上按哈希识别出来的 mod，
    /// 有更新的列出来给用户确认一次（避免用户明明只想升一个却被连带升级了别的），
    /// 确认后逐个下载替换，全程不影响不认识的/没有更新的文件，也不影响 mods 文件夹里
    /// 已经存在的其它内容（存档/资源包等不在这个文件夹里，天然不受影响）。
    /// </summary>
    private async void BatchUpdateMods_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GetEffectiveGameDir();
        var version = VersionCombo.SelectedItem as GameVersion;
        if (gameDir == null || version == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Folder_And_A_Version_Fir", "请先选择一个文件夹和版本。"));
            return;
        }

        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir))
        {
            MessageBoxDialog.ShowInfo("这个实例还没有 mods 文件夹，没有可检查的模组。");
            return;
        }

        using var batchUpdate = new BatchUpdateService();
        List<UpdateCandidate> candidates;
        try
        {
            // 简单进度提示：检查过程可能要跑几十次网络请求（每个 mod 至少 2 次），
            // 用 Toast 提示一下"正在检查"，避免用户以为按钮没反应。
            ToastService.ShowInfo("正在检查模组更新，可能需要一点时间…");
            candidates = await batchUpdate.CheckAsync(modsDir, "*.jar",
                version.McVersion, version.ModLoader, progress: null, ct: default);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("检查更新失败，请检查网络连接后重试。", ex.ToString(), "检查更新失败");
            return;
        }

        if (candidates.Count == 0)
        {
            MessageBoxDialog.ShowInfo("没有发现可升级的模组（可能都已是最新版本，或者这些 mod 无法通过 Modrinth 识别）。", "检查完成");
            return;
        }

        // 需求：加入"选择模组升级"功能——弹一个可勾选的清单，而不是只有全部升级/全部放弃
        // 两个选项。默认全部勾选，维持"一键全部升级"这个最常见场景的操作习惯，需要跳过
        // 某几个的时候用户自己取消勾选即可。ModUpdateSelectionDialog 关闭时会把用户的勾选
        // 结果写回每个 candidate.Selected，ApplyAsync 内部本来就只处理 Selected=true 的项。
        var selectionDlg = new ModUpdateSelectionDialog(candidates);
        if (selectionDlg.ShowDialog() != true) return;

        var (succeeded, failed) = await batchUpdate.ApplyAsync(modsDir, candidates, progress: null, ct: default);

        RefreshMods();

        if (failed.Count == 0)
        {
            ToastService.ShowSuccess($"已成功升级 {succeeded.Count} 个模组。");
        }
        else
        {
            var failText = string.Join("\n", failed.Select(f => $"「{f.name}」：{f.error}"));
            MessageBoxDialog.ShowWarning(
                $"成功升级 {succeeded.Count} 个，{failed.Count} 个失败：\n\n{failText}",
                "升级完成（部分失败）");
        }
    }

    private void EnableMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LocalModDisplayItem item) return;
        try
        {
            _localModService.Enable(item.Info.FilePath);
            RefreshMods();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("启用这个 Mod 失败，可能是文件正被占用（比如游戏还在运行），请关闭游戏后重试。",
                ex.ToString(), "启用 Mod 失败");
        }
    }

    private void DisableMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LocalModDisplayItem item) return;
        try
        {
            _localModService.Disable(item.Info.FilePath);
            RefreshMods();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("禁用这个 Mod 失败，可能是文件正被占用（比如游戏还在运行），请关闭游戏后重试。",
                ex.ToString(), "禁用 Mod 失败");
        }
    }

    /// <summary>
    /// 删除是不可撤销的破坏性操作，删除前必须弹二次确认——这跟本次任务里「服务端管理」模块
    /// 的破坏性操作要求二次确认是同一个原则，本地 Mod 删除虽然影响范围小得多，但同样不留
    /// "点一下就永久丢失文件"的操作。
    /// </summary>
    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LocalModDisplayItem item) return;

        var confirm = MessageBoxDialog.ShowConfirm($"确定要删除「{item.DisplayName}」吗？此操作无法撤销。",
            "确认删除");
        if (!confirm) return;

        try
        {
            _localModService.Delete(item.Info.FilePath);
            RefreshMods();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("删除这个 Mod 失败，可能是文件正被占用（比如游戏还在运行），请关闭游戏后重试。",
                ex.ToString(), "删除 Mod 失败");
        }
    }

    private async void ExportModpack_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GetEffectiveGameDir();
        var version = VersionCombo.SelectedItem as GameVersion;
        if (gameDir == null || version == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Folder_And_A_Version_Fir", "请先选择一个文件夹和版本。"));
            return;
        }

        // 需求："导出的整合包支持 zip modrinth 的格式"——用 SaveFileDialog 的双过滤项
        // 让用户二选一，FilterIndex 是 1-based(第一项=1)，据此判断用户选了哪种格式，
        // 而不是新开一个额外的格式选择弹窗，操作路径不多绕一步。
        var dialog = new SaveFileDialog
        {
            Title = "导出整合包",
            Filter = "XCL2 整合包 (*.xclpack)|*.xclpack|Modrinth 整合包 (*.mrpack)|*.mrpack",
            FilterIndex = 1,
            FileName = $"{version.Id}.xclpack"
        };
        if (dialog.ShowDialog() != true) return;

        // 用户可能只在 FilterIndex=2 时手动改了文件名但没带扩展名，或者反过来在 FilterIndex=1
        // 时手动打了 .mrpack 扩展名——统一以实际文件名的扩展名为准，比只看 FilterIndex 更可靠。
        var exportAsMrpack = string.Equals(Path.GetExtension(dialog.FileName), ".mrpack", StringComparison.OrdinalIgnoreCase);

        // 修复"导出整合包时界面卡顿"：之前 _modpackService.Export(...) 是同步方法，直接在
        // UI 线程上跑完整个复制+压缩过程，期间界面完全没响应，进度条弹窗虽然弹出来了，
        // 但因为 UI 线程本身被占满，Progress.Report 的回调也排不上队，看起来跟没弹一样。
        // 现在用 Task.Run 把真正的文件复制/压缩工作丢到后台线程，UI 线程只负责接收
        // ProgressDialog.Progress 的回调刷新界面，导出过程中窗口可以正常拖动/看到实时进度。
        var progressWin = new ProgressDialog("正在导出整合包 ...");
        progressWin.Show();
        try
        {
            var manifest = new ModpackManifest
            {
                Name = version.Id,
                McVersion = version.McVersion,
                ModLoader = version.ModLoader,
                ModLoaderVersion = version.ModLoaderVersion
            };
            // ProgressDialog.Progress 本身就是 IProgress<ProgressInfo>，直接传给新版
            // Export/ExportMrpack 重载即可拿到"已复制文件数/总数 + 当前文件名"的真实进度，
            // 不再需要用 IProgress<string> 中转成只有一句阶段文字的假进度。
            await Task.Run(() =>
            {
                if (exportAsMrpack)
                    _modpackService.ExportMrpack(gameDir, dialog.FileName, manifest, progressWin.Progress);
                else
                    _modpackService.Export(gameDir, dialog.FileName, manifest, progressWin.Progress);
            });
            MessageBoxDialog.ShowSuccess($"整合包已导出到：\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("导出整合包失败，可能是磁盘空间不足或目标位置没有写入权限。",
                ex.ToString(), "导出整合包失败");
        }
        finally
        {
            progressWin.Close();
        }
    }

    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GetEffectiveGameDir();
        if (gameDir == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Folder_And_A_Version_Fir", "请先选择一个文件夹和版本。"));
            return;
        }

        // 需求："导出的整合包支持 zip modrinth 的格式"——导入这边要跟导出对称：不仅认
        // XCL2 自己的 .xclpack，也要能直接导入标准 Modrinth .mrpack，以及别的启动器/网站上
        // 下载下来、后缀直接是 .zip 的整合包（比如有些 CurseForge 客户端整合包分享出来就是 .zip）。
        var dialog = new OpenFileDialog
        {
            Title = "导入整合包",
            Filter = "所有支持的整合包 (*.xclpack;*.mrpack;*.zip)|*.xclpack;*.mrpack;*.zip|" +
                     "XCL2 整合包 (*.xclpack)|*.xclpack|Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        // 格式判断：先看扩展名是不是 .mrpack，再兜底用 ModpackService.IsMrpack 探测 zip 包内部
        // 有没有 modrinth.index.json——这样即使用户把 .mrpack 文件手动改名成 .zip，或者反过来
        // 某些来源打包时用了 .zip 后缀但内容其实是标准 mrpack 结构，也能正确识别，不会走错分支
        // 导致"解压出来空空如也"这种误导性失败。
        var isMrpack = string.Equals(Path.GetExtension(dialog.FileName), ".mrpack", StringComparison.OrdinalIgnoreCase)
            || ModpackService.IsMrpack(dialog.FileName);

        var confirmMsg = isMrpack
            ? "这是一个 Modrinth 整合包(.mrpack)。导入会解压其中的附带文件(config/资源包等)，\n" +
              "并按清单逐个下载 mod 到当前选中的版本目录，同名文件会被覆盖。\n" +
              "部分 mod 下载源如果暂时不可用，会跳过并在导入完成后提示，不影响其余内容导入。确定要继续吗？"
            : "导入会把整合包内的 mods/config/resourcepacks/shaderpacks 合并覆盖到当前选中的版本目录，\n" +
              "同名文件会被整合包内容覆盖。确定要继续吗？";
        var confirm = MessageBoxDialog.ShowConfirm(confirmMsg, Loc.T("Str_Cs_Confirm_Import", Loc.T("Str_Cs_Confirm_Import", "确认导入")));
        if (!confirm) return;

        var progressWin = new ProgressDialog("正在导入整合包 ...");
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("导入中", 0, 1, msg)));
            if (isMrpack)
            {
                var result = await _modpackService.ImportMrpackAsync(dialog.FileName, gameDir, progress);
                var failedInfo = result.FailedFiles.Count > 0
                    ? $"\n\n有 {result.FailedFiles.Count} 个文件下载失败，可能是下载源暂时不可用，\n" +
                      $"可以之后去「Mod 管理」页手动补装：\n" + string.Join("\n", result.FailedFiles.Take(10)) +
                      (result.FailedFiles.Count > 10 ? $"\n... 等共 {result.FailedFiles.Count} 个" : "")
                    : "";
                if (failedInfo.Length > 0)
                    MessageBoxDialog.ShowWarning($"整合包已导入。\n整合包名称：{result.Name}{failedInfo}", "成功");
                else
                    MessageBoxDialog.ShowSuccess($"整合包已导入。\n整合包名称：{result.Name}{failedInfo}");
            }
            else
            {
                var manifest = _modpackService.Import(dialog.FileName, gameDir, progress);
                var nameInfo = manifest != null ? $"\n整合包名称：{manifest.Name}" : "";
                MessageBoxDialog.ShowSuccess($"整合包已导入。{nameInfo}");
            }
            RefreshMods();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("导入整合包失败，可能是整合包文件已损坏，或格式不受支持。",
                ex.ToString(), Loc.T("Str_Cs_Modpack_Import_Failed", "导入整合包失败"));
        }
        finally
        {
            progressWin.Close();
        }
    }
}

/// <summary>本地 Mod 列表项的显示包装：根据启用/禁用状态决定"启用"/"禁用"两个按钮谁可见，
/// 避免在一个已启用的 mod 上还显示多余的"启用"按钮造成困惑。</summary>
public class LocalModDisplayItem
{
    public LocalModInfo Info { get; }
    public string DisplayName => Info.DisplayName;
    public string FileName => Info.FileName;
    public string SizeDisplay => Info.SizeDisplay;
    public string StatusLabel => Info.StatusLabel;

    public Visibility EnableButtonVisibility => Info.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DisableButtonVisibility => Info.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    public LocalModDisplayItem(LocalModInfo info) => Info = info;
}
