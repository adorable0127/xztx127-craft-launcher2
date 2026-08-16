using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    public VersionSelectPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

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
        _installed.Clear();
        var folder = FolderListBox.SelectedItem as GameFolder;
        if (folder == null || string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path))
        {
            ShowHiddenInstancesBtn.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var cfg = _owner.ConfigService.Config;
            foreach (var v in _folderService.ScanVersions(folder.Path))
            {
                // "从列表中删除"的实例：文件还在磁盘上，只是不出现在这个列表里。
                if (InstanceDeletionService.IsHidden(cfg, folder.Path, v.Id)) continue;
                _installed.Add(v);
            }
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning("扫描已安装版本失败：\n" + ex.Message, Loc.T("Str_Cs_Error", "错误"));
        }

        RefreshHiddenInstancesButtonVisibility(folder.Path);
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

    /// <summary>打开系统默认浏览器访问中文 Minecraft Wiki 对给定关键词的搜索结果。
    /// 用 Process.Start 的 UseShellExecute 方式打开 URL（不直接拼 Process.Start(url) 是因为
    /// .NET 默认不会把 url 当可执行文件处理，需要显式声明走 Shell 关联程序）。</summary>
    internal static void OpenMinecraftWiki(string keyword)
    {
        try
        {
            var url = "https://zh.minecraft.wiki/?search=" + Uri.EscapeDataString(keyword);
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
