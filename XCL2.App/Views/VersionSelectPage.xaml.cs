using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class VersionSelectPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly FolderService _folderService = new();
    private readonly ObservableCollection<GameFolder> _folders = new();
    private readonly ObservableCollection<GameVersion> _installed = new();

    /// <summary>不完整实例仍保留在列表中，单独记录以显示修复提示。</summary>
    private List<GameVersion> _incompleteVersions = new();
    private readonly DispatcherTimer _watchDebounce;
    private readonly DispatcherTimer _rescanTimer;
    private FileSystemWatcher? _versionsWatcher;
    private string? _watchedFolderPath;
    private bool _refreshingItems;
    private bool _automaticRefreshRunning;
    private long _refreshGeneration;

    public VersionSelectPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watchDebounce.Tick += async (_, _) =>
        {
            _watchDebounce.Stop();
            await RefreshVersionsAutomaticallyAsync();
        };
        // 文件监视可能在网络盘、目录刚创建或大量复制时漏通知，定期扫描作为补偿。
        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _rescanTimer.Tick += (_, _) =>
        {
            WatchSelectedFolder();
            QueueAutomaticRefresh();
        };
        Loaded += (_, _) =>
        {
            WatchSelectedFolder();
            _rescanTimer.Start();
            QueueAutomaticRefresh();
        };
        Unloaded += (_, _) =>
        {
            ++_refreshGeneration;
            _watchDebounce.Stop();
            _rescanTimer.Stop();
            StopWatchingVersions();
        };

        FolderListBox.ItemsSource = _folders;
        InstalledListBox.ItemsSource = _installed;

        LoadFolders();
        RefreshInstalledVersions(); // 修复：进入页面时选中框已有默认值，但 SelectionChanged 未必触发，这里主动刷新一次
    }

    private void LoadFolders()
    {
        _folders.Clear();
        foreach (var f in _owner.ConfigService.Config.Folders ?? new List<GameFolder>()) _folders.Add(f);

        var selected = _folders.FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath) ?? _folders.FirstOrDefault();
        if (selected != null) FolderListBox.SelectedItem = selected;
    }

    private void RefreshInstalledVersions()
    {
        ++_refreshGeneration;
        var folder = FolderListBox.SelectedItem as GameFolder;
        if (folder == null || string.IsNullOrEmpty(folder.Path))
        {
            _installed.Clear();
            _incompleteVersions.Clear();
            ShowHiddenInstancesBtn.Visibility = Visibility.Collapsed;
            RefreshIncompleteVersionsButtonVisibility();
            return;
        }
        try
        {
            ApplyScannedVersions(folder.Path, _folderService.ScanVersions(folder.Path));
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning("扫描已安装版本失败：\n" + ex.Message, Loc.T("Str_Cs_Error", "错误"));
        }

    }

    private void ApplyScannedVersions(string folderPath, List<GameVersion> versions)
    {
        var cfg = _owner.ConfigService.Config;
        var visible = versions.Where(v => !InstanceDeletionService.IsHidden(cfg, folderPath, v.Id)).ToList();
        _incompleteVersions = visible.Where(v => !v.IsInstalled).ToList();
        var selectedId = cfg.SelectedVersionId ?? (InstalledListBox.SelectedItem as GameVersion)?.Id;
        var unchanged = _installed.Count == visible.Count && _installed.Zip(visible).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.McVersion == pair.Second.McVersion &&
            pair.First.ModLoader == pair.Second.ModLoader && pair.First.ModLoaderVersion == pair.Second.ModLoaderVersion &&
            pair.First.FolderPath == pair.Second.FolderPath && pair.First.IsInstalled == pair.Second.IsInstalled);

        _refreshingItems = true;
        try
        {
            if (!unchanged)
            {
                _installed.Clear();
                foreach (var version in visible) _installed.Add(version);
            }
            InstalledListBox.SelectedItem = _installed.FirstOrDefault(v =>
                string.Equals(v.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _refreshingItems = false; }

        RefreshHiddenInstancesButtonVisibility(folderPath);
        RefreshIncompleteVersionsButtonVisibility();
    }

    /// <summary>导入完成后就地刷新，保留页面和当前实例选择。</summary>
    public void RefreshFromExternalChange()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshFromExternalChange));
            return;
        }
        var cfg = _owner.ConfigService.Config;
        if (!_folders.SequenceEqual(cfg.Folders)) LoadFolders();
        var selected = _folders.FirstOrDefault(f =>
            string.Equals(f.Path, cfg.SelectedFolderPath, StringComparison.OrdinalIgnoreCase)) ?? _folders.FirstOrDefault();
        if (!ReferenceEquals(FolderListBox.SelectedItem, selected)) FolderListBox.SelectedItem = selected;
        else RefreshInstalledVersions();
    }

    private void QueueAutomaticRefresh()
    {
        if (!IsLoaded) return;
        _watchDebounce.Stop();
        _watchDebounce.Start();
    }

    private async Task RefreshVersionsAutomaticallyAsync()
    {
        if (!IsLoaded || _automaticRefreshRunning || FolderListBox.SelectedItem is not GameFolder folder) return;
        _automaticRefreshRunning = true;
        var generation = ++_refreshGeneration;
        var folderPath = folder.Path;
        try
        {
            var versions = await Task.Run(() => _folderService.ScanVersions(folderPath));
            if (IsLoaded && generation == _refreshGeneration && FolderListBox.SelectedItem is GameFolder current &&
                string.Equals(current.Path, folderPath, StringComparison.OrdinalIgnoreCase))
                ApplyScannedVersions(folderPath, versions);
        }
        catch { /* 复制中的文件/临时目录访问失败不弹窗，后续通知及定时扫描会重试。 */ }
        finally { _automaticRefreshRunning = false; }
    }

    private void WatchSelectedFolder()
    {
        var folderPath = (FolderListBox.SelectedItem as GameFolder)?.Path;
        if (_versionsWatcher != null && Directory.Exists(folderPath) &&
            string.Equals(folderPath, _watchedFolderPath, StringComparison.OrdinalIgnoreCase)) return;
        StopWatchingVersions();
        if (!IsLoaded || string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

        FileSystemWatcher? watcher = null;
        try
        {
            // 监视根目录，使尚不存在的 versions 目录被创建后也能自动发现。
            watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            void OnChanged(string path)
            {
                var parts = Path.GetRelativePath(folderPath, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length == 0 || !string.Equals(parts[0], "versions", StringComparison.OrdinalIgnoreCase)) return;
                if (parts.Length > 2 && (parts.Length != 3 ||
                    !(string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase)))) return;
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_versionsWatcher, watcher)) QueueAutomaticRefresh();
                }));
            }
            watcher.Created += (_, e) => OnChanged(e.FullPath);
            watcher.Deleted += (_, e) => OnChanged(e.FullPath);
            watcher.Changed += (_, e) => OnChanged(e.FullPath);
            watcher.Renamed += (_, e) => { OnChanged(e.OldFullPath); OnChanged(e.FullPath); };
            watcher.Error += (_, _) =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(_versionsWatcher, watcher)) return;
                    StopWatchingVersions();
                    QueueAutomaticRefresh();
                }));
            };
            _versionsWatcher = watcher;
            _watchedFolderPath = folderPath;
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher?.Dispose();
            _versionsWatcher = null;
            _watchedFolderPath = null;
            // 监视不可用时仍由后台定时扫描自动刷新。
        }
    }

    private void StopWatchingVersions()
    {
        _versionsWatcher?.Dispose();
        _versionsWatcher = null;
        _watchedFolderPath = null;
    }

    /// <summary>刷新"未完成的安装 (N)"提示按钮的可见性/文字，逻辑跟旁边的
    /// ShowHiddenInstancesBtn 是同一套模式：没有未完成的版本时整个按钮收起来，
    /// 不占大多数正常用户的界面空间。</summary>
    private void RefreshIncompleteVersionsButtonVisibility()
    {
        if (_incompleteVersions.Count == 0)
        {
            IncompleteVersionsBtn.Visibility = Visibility.Collapsed;
            return;
        }
        IncompleteVersionsBtn.Visibility = Visibility.Visible;
        IncompleteVersionsBtn.Content = $"未完成的安装 ({_incompleteVersions.Count})...";
    }

    /// <summary>列出需要修复的实例；它们已在主列表中显示。</summary>
    private void IncompleteVersions_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderListBox.SelectedItem as GameFolder;
        if (folder == null || _incompleteVersions.Count == 0) return;

        // 每个未完成的实例后面带一个"安装"按钮：只补全文件，不启动游戏。
        var dlg = new IncompleteInstallsDialog(_incompleteVersions.ToList(),
            versionId => _owner.EnqueueInstanceRepair(folder, versionId, RefreshInstalledVersions));
        dlg.ShowDialog();
        if (dlg.AnyInstalled) RefreshInstalledVersions();
    }

    /// <summary>当前文件夹下只要有一个隐藏实例，就显示"已隐藏的实例..."入口，否则收起来，
    /// 避免大多数从没用过这个功能的用户平时也要看到一个大概率用不上的按钮。</summary>
    private void RefreshHiddenInstancesButtonVisibility(string folderPath)
    {
        var cfg = _owner.ConfigService.Config;
        var prefix = InstanceDeletionService.BuildKey(folderPath, "");
        var hasHidden = cfg.HiddenInstanceKeys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        ShowHiddenInstancesBtn.Visibility = hasHidden ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FolderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderListBox.SelectedItem is GameFolder folder)
        {
            _owner.ConfigService.Config.SelectedFolderPath = folder.Path;
            _owner.ConfigService.Save();
            RefreshInstalledVersions();
        }
        else RefreshInstalledVersions();
        WatchSelectedFolder();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 .minecraft 文件夹" };
        if (dialog.ShowDialog() == true)
        {
            var folder = _folderService.AddFolder(_owner.ConfigService.Config, dialog.FolderName);
            _owner.ConfigService.Save();
            _folders.Add(folder);
            FolderListBox.SelectedItem = folder;
        }
    }

    /// <summary>"导入整合包..."按钮：跟拖拽整合包进窗口是同一条路，只是不用拖，直接弹文件选择框。
    /// 复用 MainWindow.ImportDroppedModpackAsync——装成新实例还是装进已有实例、进度弹窗、
    /// 失败提示，这些都跟拖拽安装完全一致，不用在这里再写一遍。</summary>
    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择整合包文件",
            Filter = "整合包文件 (*.zip;*.mrpack)|*.zip;*.mrpack|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        await _owner.ImportDroppedModpackAsync(dialog.FileName);
    }

    /// <summary>"导入实例文件夹..."按钮：把一个现成的、已经有 json/jar 的版本文件夹整个拷进
    /// 当前选中的 .minecraft 的 versions/ 下——不解析清单、不下载任何东西，纯文件拷贝，
    /// 装的就是一个已经能跑的现成实例。装完直接选中，右侧列表就能用⚙️菜单设置/启动。</summary>
    private async void ImportInstanceFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderListBox.SelectedItem is not GameFolder folder)
        {
            MessageBoxDialog.ShowInfo("请先在左侧选择/添加一个 .minecraft 文件夹，再导入实例。", "提示");
            return;
        }

        var pickDialog = new OpenFolderDialog { Title = "选择要导入的版本文件夹" };
        if (pickDialog.ShowDialog() != true) return;
        var sourceFolder = pickDialog.FolderName;

        if (Directory.GetFiles(sourceFolder, "*.json", SearchOption.TopDirectoryOnly).Length == 0)
        {
            MessageBoxDialog.ShowInfo("所选文件夹里没有找到版本 json 文件，这可能不是一个完整的版本文件夹。", "无法导入");
            return;
        }

        var suggested = Path.GetFileName(sourceFolder.TrimEnd('\\', '/'));
        var existingNames = new HashSet<string>(
            Directory.Exists(Path.Combine(folder.Path, "versions"))
                ? Directory.GetDirectories(Path.Combine(folder.Path, "versions")).Select(d => Path.GetFileName(d) ?? string.Empty)
                : Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var nameDialog = new RenameInstanceDialog(suggested, n => existingNames.Contains(n), "导入实例文件夹");
        if (OverlayDialogService.ShowModal(nameDialog) != true) return;

        try
        {
            await _folderService.ImportVersionFolderAsync(folder.Path, sourceFolder, nameDialog.NewName);
            _owner.NotifyInstanceImported(folder.Path, nameDialog.NewName);
            ToastService.ShowSuccess($"已导入实例「{nameDialog.NewName}」");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("导入实例文件夹失败，请确认源文件夹完整且磁盘空间足够。",
                ex.ToString(), "导入失败");
        }
    }

    /// <summary>"导入实例文件..."（小号链接入口）：跟上面"导入实例文件夹"是同一件事，只是源头
    /// 换成打包好的 .zip，不用用户先自己解压出来。</summary>
    private async void ImportInstanceFile_Click(object sender, RoutedEventArgs e)
    {
        if (FolderListBox.SelectedItem is not GameFolder folder)
        {
            MessageBoxDialog.ShowInfo("请先在左侧选择/添加一个 .minecraft 文件夹，再导入实例。", "提示");
            return;
        }

        var pickDialog = new OpenFileDialog { Title = "选择要导入的实例文件", Filter = "实例压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*" };
        if (pickDialog.ShowDialog() != true) return;

        var suggested = Path.GetFileNameWithoutExtension(pickDialog.FileName);
        var existingNames = new HashSet<string>(
            Directory.Exists(Path.Combine(folder.Path, "versions"))
                ? Directory.GetDirectories(Path.Combine(folder.Path, "versions")).Select(d => Path.GetFileName(d) ?? string.Empty)
                : Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var nameDialog = new RenameInstanceDialog(suggested, n => existingNames.Contains(n), "导入实例文件");
        if (OverlayDialogService.ShowModal(nameDialog) != true) return;

        var pd = new ProgressDialog($"正在导入实例文件「{Path.GetFileName(pickDialog.FileName)}」...");
        pd.Show();
        try
        {
            await _folderService.ImportVersionZipAsync(folder.Path, pickDialog.FileName, nameDialog.NewName);
            _owner.NotifyInstanceImported(folder.Path, nameDialog.NewName);
            ToastService.ShowSuccess($"已导入实例「{nameDialog.NewName}」");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : "导入实例文件失败，可能是文件损坏或磁盘空间不足。",
                ex.ToString(), "导入失败");
        }
        finally
        {
            pd.Close();
        }
    }

    /// <summary>
    /// "安装新版本"入口：打开 InstallClientLoaderWindow 让用户选加载器/MC版本/构建版本，
    /// 装到"当前选中的文件夹"下。装完之后不需要手动把新版本加进任何列表——
    /// FolderService.ScanVersions 是直接扫描 versions/ 目录的，这里只需要重新扫一次
    /// (RefreshInstalledVersions)，新装好的版本文件夹自然就会出现在列表里。
    /// </summary>
    private void InstallNewVersion_Click(object sender, RoutedEventArgs e)
    {
        if (FolderListBox.SelectedItem is not GameFolder)
        {
            MessageBoxDialog.ShowInfo("请先在左侧选择/添加一个 .minecraft 文件夹，再安装新版本。",
                "提示");
            return;
        }

        var window = new InstallClientLoaderWindow(_owner);
        if (window.ShowDialog() == true)
        {
            RefreshInstalledVersions();
            if (window.InstalledVersionId != null)
            {
                var installed = _installed.FirstOrDefault(v => v.Id == window.InstalledVersionId);
                if (installed != null) InstalledListBox.SelectedItem = installed;
            }
        }
    }

    /// <summary>
    /// 右键菜单"在中文 Minecraft Wiki 中查看"：跳转到 zh.minecraft.wiki 对应版本号的搜索/条目页。
    /// Wiki 站内条目路径不完全规律(比如 "Java版 1.20.1" 这种命名规则可能随版本命名规则变化)，
    /// 这里统一走 Special:Search，命中时 MediaWiki 会自动跳到唯一匹配的条目，没命中也能看到
    /// 搜索结果页而不是 404，比强行拼一个可能拼错的条目直链更稳妥。
    /// </summary>
    private void ViewOnWiki_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameVersion v }) return;
        OpenMinecraftWiki(v.Id);
    }

    /// <summary>
    /// 判断一个版本号是否是"正式版"命名格式（只由数字和点号组成，如 "1.20.4"、"26.3"）。
    /// 中文 Minecraft Wiki 给正式版开的条目统一是 "Java版&lt;版本号&gt;" 这个固定格式
    /// （例如 https://zh.minecraft.wiki/w/Java%E7%89%8826.3 ，对应"Java版26.3"），
    /// 可以直接拼直链；快照/预发布/候选版/愚人节版本命名不规律，没有这个规律，仍然走搜索兜底。
    /// </summary>
    private static bool LooksLikeReleaseId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        foreach (var c in id)
        {
            if (!char.IsDigit(c) && c != '.') return false;
        }
        return true;
    }

    /// <summary>打开系统默认浏览器访问中文 Minecraft Wiki 对给定版本号的页面：
    /// 正式版（纯数字+点号，如 "26.3"）直接拼 "Java版&lt;版本号&gt;" 条目直链；
    /// 其余命名不规律的版本（快照/预发布/候选版/愚人节版本等）退回 Special:Search，
    /// 命中时 MediaWiki 会自动跳到唯一匹配的条目，没命中也能看到搜索结果页而不是 404，
    /// 比强行拼一个可能拼错的条目直链更稳妥。
    /// 用 Process.Start 的 UseShellExecute 方式打开 URL（不直接拼 Process.Start(url) 是因为
    /// .NET 默认不会把 url 当可执行文件处理，需要显式声明走 Shell 关联程序）。</summary>
    internal static void OpenMinecraftWiki(string keyword)
    {
        try
        {
            var url = LooksLikeReleaseId(keyword)
                ? "https://zh.minecraft.wiki/w/" + Uri.EscapeDataString("Java版" + keyword)
                : "https://zh.minecraft.wiki/?search=" + Uri.EscapeDataString(keyword);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning("打开浏览器失败：\n" + ex.Message, Loc.T("Str_Cs_Error", "错误"));
        }
    }

    /// <summary>拿到 .minecraft 根目录路径：优先用当前选中的 GameFolder（左侧列表选中项），
    /// 是当前页面里唯一的"当前文件夹"来源。</summary>
    private string? GetSelectedFolderPath() => (FolderListBox.SelectedItem as GameFolder)?.Path;

    /// <summary>右键菜单"重命名..."：本质是把 versions/&lt;旧id&gt; 目录改名，
    /// 详见 InstanceDeletionService.Rename 的注释。</summary>
    private void RenameInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameVersion v }) return;
        RenameInstance(v);
    }

    private void RenameInstance(GameVersion v)
    {
        var folderPath = GetSelectedFolderPath();
        if (string.IsNullOrEmpty(folderPath)) return;

        var dlg = new RenameInstanceDialog(
            v.Id,
            isNameTaken: candidate => candidate != v.Id &&
                _installed.Any(other => other.Id != v.Id && string.Equals(other.Id, candidate, StringComparison.OrdinalIgnoreCase)),
            title: "重命名实例");

        if (OverlayDialogService.ShowModal(dlg) != true) return;

        try
        {
            InstanceDeletionService.Rename(_owner.ConfigService.Config, folderPath, v.Id, dlg.NewName);
            _owner.ConfigService.Save();
            RefreshInstalledVersions();
            var renamed = _installed.FirstOrDefault(i => string.Equals(i.Id, dlg.NewName, StringComparison.OrdinalIgnoreCase));
            if (renamed != null) InstalledListBox.SelectedItem = renamed;
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError("重命名失败：\n" + ex.Message);
        }
    }

    /// <summary>⚙️ 菜单"补全文件"：只补全缺失文件，不启动游戏。仅对不完整的实例显示。</summary>
    private void RepairInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameVersion v }) return;
        if (FolderListBox.SelectedItem is not GameFolder folder) return;

        // 走统一下载队列（右上角下载抽屉），不再弹独占的模态进度框。
        _owner.EnqueueInstanceRepair(folder, v.Id, RefreshInstalledVersions);
    }

    /// <summary>⚙️ 二级菜单 / 右键菜单"删除..."：先弹选择方式的弹窗(DeleteInstanceChoiceDialog)，
    /// 用户选"从电脑中删除"时再追加一层 xztx127 确认，通过了才真正物理删除。</summary>
    private void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameVersion v }) return;
        DeleteInstance(v);
    }

    private void DeleteInstance(GameVersion v)
    {
        var folderPath = GetSelectedFolderPath();
        if (string.IsNullOrEmpty(folderPath)) return;

        var choiceDlg = new DeleteInstanceChoiceDialog(v.Id);
        if (OverlayDialogService.ShowModal(choiceDlg) != true || choiceDlg.Choice == null) return;

        var cfg = _owner.ConfigService.Config;

        if (choiceDlg.Choice == DeleteInstanceChoiceDialog.DeleteChoice.RemoveFromList)
        {
            InstanceDeletionService.HideFromList(cfg, folderPath, v.Id);
            cfg.SelectedVersionId = null;
            _owner.ConfigService.Save();
            _owner.RefreshSidebar();
            RefreshInstalledVersions();
            return;
        }

        if (choiceDlg.Choice == DeleteInstanceChoiceDialog.DeleteChoice.DeleteToRecycleBin)
        {
            // 回收站可撤销，不需要再追加 xztx127 确认这道门槛。
            try
            {
                InstanceDeletionService.DeleteToRecycleBin(cfg, folderPath, v.Id);
                _owner.ConfigService.Save();
                _owner.RefreshSidebar();
                RefreshInstalledVersions();
            }
            catch (Exception ex)
            {
                MessageBoxDialog.ShowError("删除失败：\n" + ex.Message);
            }
            return;
        }

        // 从电脑中删除：物理删除，需要再过一道 xztx127 确认码，不可撤销。
        var confirmDlg = new DangerousConfirmDialog(
            "从电脑中删除实例",
            $"将彻底删除「{v.Id}」这个实例目录下的所有文件（存档、mod、资源包、日志等），此操作不可撤销。");
        if (OverlayDialogService.ShowModal(confirmDlg) != true || !confirmDlg.Confirmed) return;

        try
        {
            InstanceDeletionService.DeletePermanently(cfg, folderPath, v.Id);
            _owner.ConfigService.Save();
            _owner.RefreshSidebar();
            RefreshInstalledVersions();
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError("删除失败：\n" + ex.Message);
        }
    }

    /// <summary>"已隐藏的实例..."入口：列出当前文件夹下所有被"从列表中删除"的实例 id，
    /// 逐条支持"取消隐藏"重新出现在主列表。用最简单的方式实现——复用 MessageBoxDialog
    /// 之外没有专门做一个列表弹窗的必要，这里手写一个轻量 Overlay 内容更直观。</summary>
    private void ShowHiddenInstances_Click(object sender, RoutedEventArgs e)
    {
        var folderPath = GetSelectedFolderPath();
        if (string.IsNullOrEmpty(folderPath)) return;

        var cfg = _owner.ConfigService.Config;
        var prefix = InstanceDeletionService.BuildKey(folderPath, "");
        var hiddenIds = cfg.HiddenInstanceKeys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Substring(prefix.Length))
            .ToList();

        if (hiddenIds.Count == 0)
        {
            RefreshHiddenInstancesButtonVisibility(folderPath);
            return;
        }

        var dlg = new HiddenInstancesDialog(hiddenIds);
        if (OverlayDialogService.ShowModal(dlg) != true) return;

        foreach (var id in dlg.UnhiddenIds)
            InstanceDeletionService.UnhideFromList(cfg, folderPath, id);

        if (dlg.UnhiddenIds.Count > 0)
        {
            _owner.ConfigService.Save();
            RefreshInstalledVersions();
        }
    }

    /// <summary>实例条目右侧⚙️图标：点击不再直接进设置，而是弹出它的二级菜单
    /// （版本设置/重命名/删除），设置入口从菜单里进。</summary>
    private void OpenInstanceSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.ContextMenu == null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.IsOpen = true;
    }

    /// <summary>⚙️ 二级菜单"版本设置..."：弹出这个实例的独立设置弹窗(InstanceSettingsDialog)。</summary>
    private void OpenInstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameVersion v }) return;
        var folderPath = GetSelectedFolderPath();
        if (string.IsNullOrEmpty(folderPath)) return;

        var dlg = new InstanceSettingsDialog(_owner, folderPath, v);
        if (OverlayDialogService.ShowModal(dlg) != true) return;

        _owner.RefreshSidebar();
    }

    private void InstalledListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingItems) return;
        if (InstalledListBox.SelectedItem is GameVersion v)
        {
            _owner.ConfigService.Config.SelectedVersionId = v.Id;
            _owner.ConfigService.Save();
            _owner.RefreshSidebar();
        }
    }

    // ============================================================
    // 游戏文件夹：设置菜单（打开/重命名/设为默认/删除）
    // ============================================================

    /// <summary>文件夹条目右侧⚙️图标：点击弹出它的二级菜单。</summary>
    private void FolderSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.ContextMenu == null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.IsOpen = true;
    }

    /// <summary>菜单"打开文件夹"：用资源管理器打开这个 .minecraft 目录。</summary>
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameFolder f }) return;
        if (string.IsNullOrEmpty(f.Path) || !Directory.Exists(f.Path))
        {
            MessageBoxDialog.ShowWarning("文件夹路径不存在：\n" + f.Path, Loc.T("Str_Cs_Error", "错误"));
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", f.Path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBoxDialog.ShowError($"打开文件夹失败：{ex.Message}"); }
    }

    /// <summary>菜单"重命名文件夹..."：只改列表里的显示名（GameFolder.Name），不改磁盘目录名。</summary>
    private void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameFolder f }) return;
        var dlg = new RenameInstanceDialog(
            f.Name,
            candidate => _folders.Any(other => !ReferenceEquals(other, f) && string.Equals(other.Name, candidate, StringComparison.OrdinalIgnoreCase)),
            title: "重命名文件夹");
        if (OverlayDialogService.ShowModal(dlg) != true) return;
        f.Name = dlg.NewName;
        _owner.ConfigService.Save();
        FolderListBox.Items.Refresh();   // GameFolder 没有属性变更通知，手动刷新让显示名跟上来
    }

    /// <summary>菜单"设为默认文件夹"：把目标文件夹的 IsDefault 置 true，其余全部置 false。</summary>
    private void SetDefaultFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameFolder f }) return;
        if (f.IsDefault) return;
        foreach (var other in _folders) other.IsDefault = ReferenceEquals(other, f);
        _owner.ConfigService.Save();
        FolderListBox.Items.Refresh();   // 刷新"（默认）"角标
    }

    /// <summary>菜单"删除文件夹（从列表中移除）"：只从配置列表移除，不删磁盘文件。</summary>
    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameFolder f }) return;
        if (!MessageBoxDialog.ShowConfirm(
            $"确定要将文件夹「{f.Name}」从列表中移除吗？\n（不会删除磁盘上的任何文件，之后仍可通过「添加已有文件夹」重新加入。）",
            "移除文件夹")) return;

        RemoveFolderFromLists(f);
        if (FolderListBox.SelectedItem == null)
            FolderListBox.SelectedItem = _folders.FirstOrDefault();
    }

    /// <summary>菜单"直接删除文件夹（从电脑中删除）"：物理删除整个目录，必须输入 xztx127 确认。</summary>
    private void DeleteFolderFromDisk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameFolder f }) return;
        if (!Directory.Exists(f.Path))
        {
            MessageBoxDialog.ShowWarning("文件夹路径不存在，无法从电脑中删除：\n" + f.Path, Loc.T("Str_Cs_Error", "错误"));
            return;
        }

        var confirmDlg = new DangerousConfirmDialog(
            "从电脑中删除文件夹",
            $"将彻底删除「{f.Name}」文件夹下的所有内容（存档、mod、资源包、版本文件等）：\n{f.Path}\n\n此操作不可撤销！");
        if (OverlayDialogService.ShowModal(confirmDlg) != true || !confirmDlg.Confirmed) return;

        try
        {
            Directory.Delete(f.Path, recursive: true);
            RemoveFolderFromLists(f);
            if (FolderListBox.SelectedItem == null)
                FolderListBox.SelectedItem = _folders.FirstOrDefault();
            MessageBoxDialog.ShowInfo($"已从电脑中删除文件夹：\n{f.Path}", "删除完成");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError("删除文件夹失败：\n" + ex.Message);
        }
    }

    /// <summary>把文件夹从配置列表和当前列表里移除；如果移除的是当前选中的文件夹，
    /// 顺便清掉 SelectedFolderPath/SelectedVersionId 并刷新侧边栏与版本列表。</summary>
    private void RemoveFolderFromLists(GameFolder f)
    {
        var cfg = _owner.ConfigService.Config;
        cfg.Folders.Remove(f);
        _folders.Remove(f);
        if (cfg.SelectedFolderPath == f.Path)
        {
            cfg.SelectedFolderPath = null;
            cfg.SelectedVersionId = null;
        }
        _owner.ConfigService.Save();
        _owner.RefreshSidebar();
        RefreshInstalledVersions();
    }

}
