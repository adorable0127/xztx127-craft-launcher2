using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private readonly ObservableCollection<GameVersion> _versions = new();
    private readonly ObservableCollection<LocalModDisplayItem> _mods = new();

    public ModManagerPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        FolderCombo.ItemsSource = _folders;
        VersionCombo.ItemsSource = _versions;
        VersionCombo.DisplayMemberPath = nameof(GameVersion.Id);
        ModsListBox.ItemsSource = _mods;

        LoadFolders();
    }

    private void LoadFolders()
    {
        _folders.Clear();
        foreach (var f in _owner.ConfigService.Config.Folders ?? new List<GameFolder>()) _folders.Add(f);

        var selected = _folders.FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)
            ?? _folders.FirstOrDefault();
        if (selected != null)
            FolderCombo.SelectedItem = selected;
        else
            RefreshVersions(); // 没有任何文件夹时也要跑一遍刷新，确保空态提示正确显示
    }

    private void RefreshVersions()
    {
        _versions.Clear();
        _mods.Clear();

        var folder = FolderCombo.SelectedItem as GameFolder;
        if (folder == null || string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path))
        {
            UpdateEmptyHint();
            return;
        }

        try
        {
            foreach (var v in _folderService.ScanVersions(folder.Path)) _versions.Add(v);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("扫描已安装版本失败，可能是这个文件夹不是有效的 .minecraft 目录，或权限不足。",
                ex.ToString(), "扫描版本失败");
        }

        var selectedId = _owner.ConfigService.Config.SelectedVersionId;
        var selectedVersion = _versions.FirstOrDefault(v => v.Id == selectedId) ?? _versions.FirstOrDefault();
        if (selectedVersion != null)
            VersionCombo.SelectedItem = selectedVersion;
        else
            UpdateEmptyHint();
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

    private void RefreshMods()
    {
        _mods.Clear();
        var gameDir = GetEffectiveGameDir();
        if (gameDir == null)
        {
            UpdateEmptyHint();
            return;
        }

        try
        {
            foreach (var m in _localModService.ScanMods(gameDir)) _mods.Add(new LocalModDisplayItem(m));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Scan_The_Mod_List_A_File_May_Be", "扫描 Mod 列表失败，可能是文件被占用或权限不足，请重试。"),
                ex.ToString(), "扫描 Mod 列表失败");
        }

        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        var hasSelection = FolderCombo.SelectedItem != null && VersionCombo.SelectedItem != null;
        EmptyHint.Visibility = _mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = !hasSelection
            ? "请先选择一个文件夹和版本。"
            : "这个版本还没有安装任何 Mod，去「下载」页的 Mod 分类搜索安装吧。";
        ModsListBox.Visibility = _mods.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderCombo.SelectedItem is GameFolder folder)
        {
            _owner.ConfigService.Config.SelectedFolderPath = folder.Path;
            _owner.ConfigService.Save();
        }
        RefreshVersions();
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshMods();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshMods();

    /// <summary>
    /// 交接文档需求：前置模组分析。手动点"检查前置模组"按钮触发（不做成每次刷新都自动弹窗——
    /// 用户可能就是想先装个前置再装主 mod，中间状态本来就会"缺前置"，如果每次刷新列表都弹一次
    /// 警告框会很烦人，改成用户主动点了才检查更符合"提示而不是打扰"的分寸）。
    /// </summary>
    private void AnalyzeDependencies_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GetEffectiveGameDir();
        if (gameDir == null)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Folder_And_A_Version_Fir", "请先选择一个文件夹和版本。"));
            return;
        }

        List<LocalModInfo> enabledMods;
        try
        {
            enabledMods = _localModService.ScanMods(gameDir).Where(m => m.IsEnabled).ToList();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Scan_The_Mod_List_A_File_May_Be", "扫描 Mod 列表失败，可能是文件被占用或权限不足，请重试。"),
                ex.ToString(), "检查前置模组失败");
            return;
        }

        var analysis = _dependencyAnalysisService.Analyze(enabledMods);
        if (!analysis.HasMissingDependencies)
        {
            MessageBoxDialog.ShowInfo("没有发现缺失的前置模组。", "检查完成");
            return;
        }

        var warnWin = new ModDependencyWarningWindow(analysis, gameDir, enabledMods);
        warnWin.ShowDialog();
        RefreshMods(); // 用户可能在弹窗里下载了前置或删除了 mod，刷新列表让状态保持一致
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

        var listText = string.Join("\n", candidates.Select(c =>
            $"「{c.DisplayName}」：{(string.IsNullOrEmpty(c.CurrentVersionName) ? "当前版本" : c.CurrentVersionName)} → {c.NewVersionName}"));
        var confirm = MessageBoxDialog.ShowConfirm(
            $"发现 {candidates.Count} 个模组有更新：\n\n{listText}\n\n是否全部升级？",
            "一键批量升级");
        if (!confirm) return;

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
