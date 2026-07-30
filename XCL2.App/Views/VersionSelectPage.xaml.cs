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
        if (folder == null || string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path)) return;
        try
        {
            foreach (var v in _folderService.ScanVersions(folder.Path)) _installed.Add(v);
        }
        catch (Exception ex)
        {
            MessageBox.Show("扫描已安装版本失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            MessageBox.Show("请先在左侧选择/添加一个 .minecraft 文件夹，再安装新版本。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new InstallClientLoaderWindow(_owner) { Owner = Window.GetWindow(this) };
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
            MessageBox.Show("打开浏览器失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstalledListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstalledListBox.SelectedItem is GameVersion v)
        {
            _owner.ConfigService.Config.SelectedVersionId = v.Id;
            _owner.ConfigService.Save();
            _owner.RefreshSidebar();
            LoadPerVersionSettings(v.Id);
        }
        else
        {
            PerVersionSettingsPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>选中某个已安装版本时，把这个版本已有的"Java 版本覆盖"/"版本隔离覆盖"设置
    /// 加载到面板控件上，方便用户看到当前状态并修改。</summary>
    private void LoadPerVersionSettings(string versionId)
    {
        var cfg = _owner.ConfigService.Config;
        PerVersionTitleText.Text = $"「{versionId}」的单独设置";

        VersionJavaListCombo.Items.Clear();
        VersionJavaListCombo.Items.Add(new JavaListItem { Entry = null }); // "（不指定）"
        foreach (var j in cfg.InstalledJavas) VersionJavaListCombo.Items.Add(new JavaListItem { Entry = j });
        var selectedJavaId = cfg.VersionJavaIdOverrides.TryGetValue(versionId, out var jid) ? jid : null;
        VersionJavaListCombo.SelectedItem = VersionJavaListCombo.Items.Cast<JavaListItem>()
            .FirstOrDefault(i => i.Entry?.Id == selectedJavaId) ?? VersionJavaListCombo.Items[0];

        VersionJavaOverrideBox.Text = cfg.VersionJavaOverrides.TryGetValue(versionId, out var javaOverride) && javaOverride > 0
            ? javaOverride.ToString()
            : "";

        // 临时挂起 Checked/Unchecked 事件处理，避免加载时的赋值触发一次多余的"改动但未保存"提示。
        VersionIsolationOverrideCheck.Checked -= VersionIsolationOverrideCheck_Changed;
        VersionIsolationOverrideCheck.Unchecked -= VersionIsolationOverrideCheck_Changed;
        VersionIsolationOverrideCheck.IsChecked = cfg.VersionIsolationOverrides.TryGetValue(versionId, out var isolate)
            ? isolate
            : cfg.IsolateVersionsByDefault;
        VersionIsolationOverrideCheck.Checked += VersionIsolationOverrideCheck_Changed;
        VersionIsolationOverrideCheck.Unchecked += VersionIsolationOverrideCheck_Changed;

        VersionResourcePackIsolationOverrideCheck.Checked -= VersionResourcePackIsolationOverrideCheck_Changed;
        VersionResourcePackIsolationOverrideCheck.Unchecked -= VersionResourcePackIsolationOverrideCheck_Changed;
        VersionResourcePackIsolationOverrideCheck.IsChecked = cfg.VersionResourcePackIsolationOverrides.TryGetValue(versionId, out var resIsolate)
            ? resIsolate
            : cfg.IsolateResourcePacksByDefault;
        VersionResourcePackIsolationOverrideCheck.Checked += VersionResourcePackIsolationOverrideCheck_Changed;
        VersionResourcePackIsolationOverrideCheck.Unchecked += VersionResourcePackIsolationOverrideCheck_Changed;

        PerVersionSettingsPanel.Visibility = Visibility.Visible;
    }

    private void VersionIsolationOverrideCheck_Changed(object sender, RoutedEventArgs e)
    {
        // 复选框状态变化只更新界面上的选中态，真正写入配置在点击"保存这个版本的设置"时统一处理，
        // 避免每次勾选/取消都触发一次磁盘写入。
    }

    /// <summary>同上：资源包隔离覆盖勾选框状态变化不立即写盘，统一在"保存这个版本的设置"时处理。</summary>
    private void VersionResourcePackIsolationOverrideCheck_Changed(object sender, RoutedEventArgs e)
    {
    }

    /// <summary>把当前面板上的"Java 版本覆盖"/"版本隔离覆盖"写回配置并保存。</summary>
    private void SavePerVersionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledListBox.SelectedItem is not GameVersion v) return;
        var cfg = _owner.ConfigService.Config;

        var selectedJava = (VersionJavaListCombo.SelectedItem as JavaListItem)?.Entry;
        if (selectedJava != null)
            cfg.VersionJavaIdOverrides[v.Id] = selectedJava.Id;
        else
            cfg.VersionJavaIdOverrides.Remove(v.Id); // "（不指定）" = 移除，改走下面的主版本号/自动匹配逻辑

        var javaText = VersionJavaOverrideBox.Text.Trim();
        if (javaText.Length == 0)
        {
            cfg.VersionJavaOverrides.Remove(v.Id); // 留空 = 恢复自动匹配
        }
        else if (int.TryParse(javaText, out var javaMajor) && javaMajor is >= 8 and <= 99)
        {
            cfg.VersionJavaOverrides[v.Id] = javaMajor;
        }
        else
        {
            MessageBox.Show("Java 版本请填一个数字(如 8、17、21、25)，或留空使用自动匹配。",
                "输入有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        cfg.VersionIsolationOverrides[v.Id] = VersionIsolationOverrideCheck.IsChecked == true;
        cfg.VersionResourcePackIsolationOverrides[v.Id] = VersionResourcePackIsolationOverrideCheck.IsChecked == true;

        cfg.SelectedVersionId = v.Id;
        _owner.ConfigService.Save();
        MessageBox.Show($"已保存「{v.Id}」的单独设置。", "已保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

}
