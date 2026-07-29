using System.Collections.ObjectModel;
using System.IO;
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
            ErrorPresenter.ShowFriendlyError("扫描 Mod 列表失败，可能是文件被占用或权限不足，请重试。",
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
            MessageBox.Show("请先选择一个文件夹和版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<LocalModInfo> enabledMods;
        try
        {
            enabledMods = _localModService.ScanMods(gameDir).Where(m => m.IsEnabled).ToList();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("扫描 Mod 列表失败，可能是文件被占用或权限不足，请重试。",
                ex.ToString(), "检查前置模组失败");
            return;
        }

        var analysis = _dependencyAnalysisService.Analyze(enabledMods);
        if (!analysis.HasMissingDependencies)
        {
            MessageBox.Show("没有发现缺失的前置模组。", "检查完成", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var warnWin = new ModDependencyWarningWindow(analysis, gameDir, enabledMods) { Owner = Window.GetWindow(this) };
        warnWin.ShowDialog();
        RefreshMods(); // 用户可能在弹窗里下载了前置或删除了 mod，刷新列表让状态保持一致
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

        var confirm = MessageBox.Show($"确定要删除「{item.DisplayName}」吗？此操作无法撤销。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

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

    private void ExportModpack_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GetEffectiveGameDir();
        var version = VersionCombo.SelectedItem as GameVersion;
        if (gameDir == null || version == null)
        {
            MessageBox.Show("请先选择一个文件夹和版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出整合包",
            Filter = "XCL2 整合包 (*.xclpack)|*.xclpack",
            FileName = $"{version.Id}.xclpack"
        };
        if (dialog.ShowDialog() != true) return;

        var progressWin = new ProgressWindow("正在导出整合包 ...") { Owner = Window.GetWindow(this) };
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
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("导出中", 0, 1, msg)));
            _modpackService.Export(gameDir, dialog.FileName, manifest, progress);
            MessageBox.Show($"整合包已导出到：\n{dialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GetEffectiveGameDir();
        if (gameDir == null)
        {
            MessageBox.Show("请先选择一个文件夹和版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog { Title = "导入整合包", Filter = "XCL2 整合包 (*.xclpack)|*.xclpack|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;

        var confirm = MessageBox.Show(
            "导入会把整合包内的 mods/config/resourcepacks/shaderpacks 合并覆盖到当前选中的版本目录，\n" +
            "同名文件会被整合包内容覆盖。确定要继续吗？",
            "确认导入", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var progressWin = new ProgressWindow("正在导入整合包 ...") { Owner = Window.GetWindow(this) };
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("导入中", 0, 1, msg)));
            var manifest = _modpackService.Import(dialog.FileName, gameDir, progress);
            var nameInfo = manifest != null ? $"\n整合包名称：{manifest.Name}" : "";
            MessageBox.Show($"整合包已导入。{nameInfo}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshMods();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("导入整合包失败，可能是整合包文件已损坏，或格式不受支持。",
                ex.ToString(), "导入整合包失败");
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
