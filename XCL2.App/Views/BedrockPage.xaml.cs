using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 「基岩版启动」侧边栏独立页。整合了原来分散在两个地方的基岩版相关功能：
///   1) 「百宝箱」页原来的"基岩版"Tab —— 内容管理（世界/资源包/行为包/附加包导入）
///      和专用服务端下载/启动，这部分是正式功能，直接把 UI 和事件处理代码原样搬过来；
///   2) 「实验性功能」弹窗里原来的"启动基岩版"按钮 —— 检测本机是否装了 Microsoft Store
///      版 Minecraft for Windows，装了就唤起（BedrockLaunchService），这部分逻辑现在也
///      收进这个页面的"基岩版客户端"区块（BedrockLaunchBtn/BedrockDetect_Click）。
///
/// 新增功能：
///   3) 基岩版客户端多版本下载 —— 通过 mc-w10-versiondb 获取版本列表，支持从 Microsoft Store
///      FE3 API 获取下载直链，下载解压完成后自动启动游戏（下载安装启动一条龙）。
/// </summary>
public partial class BedrockPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly BedrockContentService _bedrockService = new();
    private readonly BedrockClientDownloadService _clientDownloadService = new();

    // 客户端版本列表（从 mc-w10-versiondb 获取）
    private List<BedrockClientDownloadService.BedrockVersionInfo> _clientVersions = new();

    public BedrockPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        // 系统版本检测放在最前面、其他任何初始化之前——基岩版官方就不支持 Win10 以下系统，
        // 不支持的话后面所有检测/网络请求（版本列表、UWP 依赖检测等）都是白做，
        // 而且会让用户以为"卡住了"而不是"这系统压根用不了这个功能"。
        if (!BedrockLaunchService.IsOsSupported)
        {
            IsEnabled = false;
            BedrockStatusText.Text = BedrockLaunchService.UnsupportedOsMessage;
            BedrockClientStatusText.Text = BedrockLaunchService.UnsupportedOsMessage;
            MessageBoxDialog.ShowInfo(BedrockLaunchService.UnsupportedOsMessage,
                Loc.T("Str_Cs_Bedrock_Os_Unsupported", "系统不支持"));
            return;
        }

        // 基岩版检测是异步的（要跑一次 PowerShell），不阻塞页面构造。
        RefreshBedrockStatusAsync();
        InitBedrockClientSection();
        InitBedrockServerSection();
    }

    // ============================================================
    // 基岩版客户端：检测 + 唤起 + 下载 + 启动
    // ============================================================

    /// <summary>检测基岩版是否已安装并更新界面状态。构造时和用户点"重新检测"时都会调。</summary>
    private async void RefreshBedrockStatusAsync()
    {
        try
        {
            BedrockStatusText.Text = Loc.T("Str_Cs_Detecting_Ellipsis", "正在检测...");
            var installed = await BedrockLaunchService.IsInstalledAsync();
            BedrockLaunchBtn.IsEnabled = installed;
            BedrockStatusText.Text = installed
                ? Loc.T("Str_Cs_Bedrock_Detected", "已检测到 Minecraft for Windows（基岩版）。")
                : Loc.T("Str_Cs_Bedrock_Not_Detected_2",
                    "没有检测到基岩版。请从 Microsoft Store 安装并至少启动一次，之后再回来这里。");
        }
        catch
        {
            BedrockStatusText.Text = Loc.T("Str_Cs_Bedrock_Detect_Failed",
                "检测失败（可能是 PowerShell 被禁用）。可以直接点「启动基岩版」试试。");
            BedrockLaunchBtn.IsEnabled = true;
        }
    }

    private void BedrockDetect_Click(object sender, RoutedEventArgs e) => RefreshBedrockStatusAsync();

    /// <summary>
    /// 唤起本机已装的 Minecraft for Windows。没检测到就提示用户去应用商店安装。
    /// </summary>
    private async void BedrockLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (!BedrockLaunchBtn.IsEnabled)
        {
            var installed = await BedrockLaunchService.IsInstalledAsync();
            if (!installed)
            {
                await MessageBoxDialog.ShowInfoAsync(
                    Loc.T("Str_Cs_Bedrock_Not_Detected_3",
                        "没有检测到已安装的「Minecraft for Windows」（基岩版）。\n\n" +
                        "这是完全独立于 Java 版的另一个游戏（不同引擎、不同 Mod 生态），需要先在 Microsoft Store 里搜索" +
                        "「Minecraft」单独安装。"),
                    Loc.T("Str_Cs_Bedrock_Edition_Not_Detected", "未检测到基岩版"));
                return;
            }
        }

        try { BedrockLaunchService.Launch(); }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                Loc.T("Str_Cs_Couldn_T_Start_Bedrock_2", "唤起基岩版失败，可能它没有正确安装。"),
                ex.ToString(), Loc.T("Str_Cs_Couldn_T_Start_Bedrock_Edition", "启动基岩版失败"));
        }
    }

    private void BedrockOpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = BedrockContentService.ComMojangDir;
        if (!Directory.Exists(dir))
        {
            MessageBoxDialog.ShowInfo(
                Loc.T("Str_Cs_No_Data_Folder_Yet_Body",
                    "基岩版的数据目录还不存在。基岩版**首次启动之后**才会创建这个目录，" +
                    "请先启动一次基岩版再来。"),
                Loc.T("Str_Cs_No_Data_Folder_Yet", "还没有数据目录"));
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBoxDialog.ShowError($"打开文件夹失败：{ex.Message}"); }
    }

    // ============================================================
    // 基岩版客户端：下载 + 多版本管理
    // ============================================================

    /// <summary>当前在"已下载客户端"列表里选中的目录，Launch 按钮用这个。</summary>
    private string? _selectedBedrockClientDir;

    private void InitBedrockClientSection()
    {
        var cfg = _owner.ConfigService.Config;
        BedrockClientDefaultDirBox.Text = string.IsNullOrWhiteSpace(cfg.BedrockClientDefaultDownloadDir)
            ? Loc.T("Str_Ui_Not_Set_Will_Ask_Every_Time", "未设置（每次下载都会询问）")
            : cfg.BedrockClientDefaultDownloadDir;

        RefreshBedrockClientInstanceList();

        // 页面加载时自动刷新一次版本列表
        _ = LoadClientVersionsAsync();
    }

    private BedrockClientDownloadService.BedrockClientChannel GetSelectedClientChannel()
        => BedrockClientChannelCombo.SelectedIndex == 1
            ? BedrockClientDownloadService.BedrockClientChannel.Preview
            : BedrockClientDownloadService.BedrockClientChannel.Stable;

    private async Task LoadClientVersionsAsync()
    {
        // 之前的问题：下面这四行 UI 状态重置写在 try 之外——如果这个方法执行的时候
        // 页面已经被切走/关掉（这是个 fire-and-forget 调用 `_ = LoadClientVersionsAsync()`，
        // 没人 await 它，用户完全可能在它跑完之前就导航到别的页面），这几个命名控件的引用
        // 可能已经不可用，直接访问会抛 NullReferenceException。因为没被 await/catch，
        // 这个异常会变成"未观察的后台 Task 异常"，在终结器线程上被重新抛出，导致进程崩溃
        // ——日志里的 `AggregateException...NullReferenceException...LoadClientVersionsAsync
        // 第156行` 就是这么来的。现在把所有访问控件的代码都收进 try，统一兜底。
        try
        {
            BedrockClientVersionCombo.IsEnabled = false;
            BedrockClientDownloadBtn.IsEnabled = false;
            BedrockClientVersionCombo.ItemsSource = null;
            BedrockClientStatusText.Text = Loc.T("Str_Cs_Loading_Version_List", "正在加载版本列表...");

            var channel = GetSelectedClientChannel();
            _clientVersions = await _clientDownloadService.GetVersionListAsync(channel);
            if (_clientVersions.Count == 0)
            {
                BedrockClientStatusText.Text = Loc.T("Str_Cs_No_Versions_Available", "无法获取版本列表，请检查网络后点击「刷新列表」重试。");
            }
            else
            {
                BedrockClientVersionCombo.ItemsSource = _clientVersions;
                BedrockClientVersionCombo.DisplayMemberPath = "Name";
                BedrockClientVersionCombo.SelectedIndex = 0;
                BedrockClientDownloadBtn.IsEnabled = true;
                BedrockClientStatusText.Text = $"该渠道已加载 {_clientVersions.Count} 个版本，可直接在下拉框里选择要下载的版本。";
            }

            BedrockClientVersionCombo.IsEnabled = _clientVersions.Count > 0;   // 多版本：列表可用时允许自由选择
        }
        catch (Exception ex) when (IsBenignPageGoneException(ex))
        {
            // 页面已经被切走/关掉，控件不可用：静默忽略，不是真正的错误，
            // 不需要打扰用户也不需要记崩溃日志。
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("加载基岩版客户端版本列表失败", ex);
            try { BedrockClientStatusText.Text = Loc.T("Str_Cs_Version_List_Load_Failed", "版本列表加载失败，请检查网络后点击「刷新列表」重试。"); }
            catch { /* 页面可能已经没了，忽略 */ }
        }
    }

    /// <summary>
    /// 判断异常是不是"页面/窗口已经被拆掉，控件引用失效"这种良性情况（不是真正的 bug，
    /// 不需要记录/上报）。目前主要覆盖 NullReferenceException（控件字段为空）——
    /// WPF 没有一个统一的"页面已卸载"异常类型，只能按经验判断。
    /// </summary>
    private static bool IsBenignPageGoneException(Exception ex) => ex is NullReferenceException;

    private void BedrockClientChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 渠道切换时自动刷新版本列表
        _ = LoadClientVersionsAsync();
    }

    private void BedrockClientRefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        // 强制从网络刷新
        BedrockClientVersionCombo.IsEnabled = false;
        BedrockClientVersionCombo.ItemsSource = new[] { Loc.T("Str_Cs_Loading_Version_List", "正在加载版本列表...") };

        _ = Task.Run(async () =>
        {
            try
            {
                var channel = GetSelectedClientChannel();
                var versions = await _clientDownloadService.RefreshVersionListAsync(channel);

                await Dispatcher.InvokeAsync(() =>
                {
                    _clientVersions = versions;
                    if (versions.Count == 0)
                    {
                        BedrockClientVersionCombo.ItemsSource = new[] { Loc.T("Str_Cs_No_Versions_Available", "无法获取版本列表") };
                        BedrockClientDownloadBtn.IsEnabled = false;
                        BedrockClientStatusText.Text = Loc.T("Str_Cs_No_Versions_Available", "无法获取版本列表，请检查网络后点击「刷新列表」重试。");
                    }
                    else
                    {
                        BedrockClientVersionCombo.ItemsSource = versions;
                        BedrockClientVersionCombo.DisplayMemberPath = "Name";
                        BedrockClientVersionCombo.SelectedIndex = 0;
                        BedrockClientDownloadBtn.IsEnabled = true;
                        BedrockClientStatusText.Text = $"该渠道已加载 {versions.Count} 个版本，可直接在下拉框里选择要下载的版本。";
                    }
                    BedrockClientVersionCombo.IsEnabled = versions.Count > 0;   // 多版本：允许自由选择
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ErrorPresenter.LogFallback("刷新基岩版客户端版本列表失败", ex);
                    BedrockClientVersionCombo.ItemsSource = new[] { Loc.T("Str_Cs_Version_List_Load_Failed", "版本列表加载失败") };
                    BedrockClientDownloadBtn.IsEnabled = false;
                    BedrockClientStatusText.Text = Loc.T("Str_Cs_Version_List_Load_Failed", "版本列表加载失败，请检查网络后点击「刷新列表」重试。");
                    BedrockClientVersionCombo.IsEnabled = false;
                });
            }
        });
    }

    private void RefreshBedrockClientInstanceList()
    {
        var cfg = _owner.ConfigService.Config;
        BedrockClientInstanceList.ItemsSource = null;
        BedrockClientInstanceList.ItemsSource = cfg.BedrockClients
            .OrderByDescending(r => r.InstalledAtUtc)
            .Select(r => new
            {
                r.Id,
                r.Directory,
                Label = $"{r.DisplayName} — {r.Directory}",
            })
            .ToList();
        BedrockClientInstanceList.DisplayMemberPath = "Label";
        BedrockClientInstanceList.SelectedValuePath = "Directory";

        if (cfg.BedrockClients.Count > 0)
        {
            BedrockClientInstanceList.SelectedIndex = 0;
        }
        else
        {
            _selectedBedrockClientDir = null;
            BedrockClientLaunchDownloadedBtn.IsEnabled = false;
        }
    }

    private void BedrockClientInstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedBedrockClientDir = BedrockClientInstanceList.SelectedValue as string;
        BedrockClientLaunchDownloadedBtn.IsEnabled = !string.IsNullOrEmpty(_selectedBedrockClientDir);
    }

    private void BedrockClientBrowseDefaultDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("Str_Cs_Choose_Bedrock_Client_Default_Dir", "选择基岩版客户端的默认下载文件夹") };
        if (picker.ShowDialog() != true) return;

        var cfg = _owner.ConfigService.Config;
        cfg.BedrockClientDefaultDownloadDir = picker.FolderName;
        _owner.ConfigService.Save();
        BedrockClientDefaultDirBox.Text = picker.FolderName;
    }

    private void BedrockClientClearDefaultDir_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;
        cfg.BedrockClientDefaultDownloadDir = null;
        _owner.ConfigService.Save();
        BedrockClientDefaultDirBox.Text = Loc.T("Str_Ui_Not_Set_Will_Ask_Every_Time", "未设置（每次下载都会询问）");
    }


    private async void BedrockClientDownload_Click(object sender, RoutedEventArgs e)
    {
        // 多版本：使用下拉框里选中的版本；没选中（列表未加载）时回退到列表第一项（最新版）。
        var selectedVersion = BedrockClientVersionCombo.SelectedItem as BedrockClientDownloadService.BedrockVersionInfo
            ?? _clientVersions.FirstOrDefault();
        if (selectedVersion == null)
        {
            MessageBoxDialog.ShowInfo(
                Loc.T("Str_Cs_No_Version_Selected_Body", "版本列表还没有加载出来，请先点击「刷新列表」获取最新版本信息后再下载。"),
                Loc.T("Str_Cs_No_Version_Selected", "未获取到版本信息"));
            return;
        }

        var cfg = _owner.ConfigService.Config;

        // 在弹"选文件夹"之前先按版本号查已记录的实例：不管当年装在哪个目录，
        // 只要这个版本已经装过且 exe 还在，就不应该再走一遍下载流程。
        // 以前的问题是：没设默认目录时每次点下载都要手选文件夹，两次选了不同文件夹
        // 就会被当成"全新安装"重新下载一遍——哪怕本机其实已经有这个版本了。
        var existingRecord = cfg.BedrockClients.FirstOrDefault(r =>
            string.Equals(r.Version, selectedVersion.Name, StringComparison.OrdinalIgnoreCase)
            && BedrockClientDownloadService.FindClientExe(r.Directory) != null);

        if (existingRecord != null)
        {
            if (MessageBoxDialog.ShowConfirm(
                    $"基岩版客户端 {selectedVersion.Name} 已经安装过了：\n{existingRecord.Directory}\n\n无需重复下载，直接启动它吗？",
                    Loc.T("Str_Cs_Download_Complete", "已安装")))
            {
                try
                {
                    await BedrockClientDownloadService.LaunchClientAsync(existingRecord.Directory);
                    BedrockClientStatusText.Text = $"已启动已安装的基岩版客户端 {selectedVersion.Name}：{existingRecord.Directory}";
                }
                catch (Exception launchEx)
                {
                    ErrorPresenter.LogFallback("启动已安装的基岩版客户端失败", launchEx);
                }
            }
            return;
        }

        // 确定下载目录
        string baseDir;
        if (!string.IsNullOrWhiteSpace(cfg.BedrockClientDefaultDownloadDir))
        {
            baseDir = cfg.BedrockClientDefaultDownloadDir!;
        }
        else
        {
            var picker = new OpenFolderDialog { Title = Loc.T("Str_Cs_Choose_Bedrock_Client_Install_Dir", "选择基岩版客户端的安装位置") };
            if (picker.ShowDialog() != true) return;
            baseDir = picker.FolderName;
        }

        BedrockClientDownloadBtn.IsEnabled = false;
        var pd = new ProgressDialog(Loc.T("Str_Cs_Downloading_Bedrock_Client", "正在下载基岩版客户端 ..."));
        pd.Show();
        try
        {
            // 先装运行库前置（VC++ / UWP 框架包，缺了客户端闪退）：一次性做完，
            // 这样游戏包下载完成之后直接解压启动，不会再有"下载完又下载一次"的错觉。
            await BedrockContentService.EnsureSupportLibrariesInstalledAsync(pd.Progress);

            var finalDir = Path.Combine(baseDir, $"bedrock-client-{selectedVersion.Name}");

            // 已装过同版本（按目录判断，作为上面按版本号判断的兜底）：不再重复下载
            if (Directory.Exists(finalDir) && BedrockClientDownloadService.FindClientExe(finalDir) != null)
            {
                if (MessageBoxDialog.ShowConfirm(
                        $"基岩版客户端 {selectedVersion.Name} 已经安装过了：\n{finalDir}\n\n无需重复下载，直接启动它吗？",
                        Loc.T("Str_Cs_Download_Complete", "已安装")))
                {
                    try
                    {
                        await BedrockClientDownloadService.LaunchClientAsync(finalDir);
                        BedrockClientStatusText.Text = $"已启动已安装的基岩版客户端 {selectedVersion.Name}：{finalDir}";
                    }
                    catch (Exception launchEx)
                    {
                        ErrorPresenter.LogFallback("启动已安装的基岩版客户端失败", launchEx);
                    }
                }
                return;
            }

            var extractDir = await _clientDownloadService.DownloadClientAsync(
                selectedVersion, finalDir, pd.Progress);

            var record = cfg.BedrockClients.FirstOrDefault(r => r.Directory.Equals(finalDir, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                record = new BedrockClientRecord
                {
                    Directory = finalDir,
                    Version = selectedVersion.Name,
                    DisplayName = selectedVersion.Name,
                    OriginalFileName = $"Minecraft-{selectedVersion.Name}.appx",
                };
                cfg.BedrockClients.Add(record);
            }
            else
            {
                record.Version = selectedVersion.Name;
                record.InstalledAtUtc = DateTime.UtcNow;
            }
            _owner.ConfigService.Save();
            RefreshBedrockClientInstanceList();

            // 一条龙：下载+解压完成后直接自动启动游戏，不用再手动点「启动已下载客户端」，
            // 也不需要自己去安装/注册任何东西（官方包解压出来就是完整可运行的游戏）。
            // 启动失败不阻断流程：记录已保存，用户仍可点「启动已下载客户端」手动重试。
            string launchNote;
            try
            {
                await BedrockClientDownloadService.LaunchClientAsync(finalDir, pd.Progress);
                launchNote = Loc.T("Str_Cs_Bedrock_Client_Auto_Launched", "游戏已自动启动。");
                BedrockClientStatusText.Text = $"已下载并启动基岩版客户端 {selectedVersion.Name}：{finalDir}";
            }
            catch (Exception launchEx)
            {
                ErrorPresenter.LogFallback("自动启动已下载的基岩版客户端失败", launchEx);
                launchNote = Loc.T("Str_Cs_Bedrock_Client_Auto_Launch_Failed", "游戏自动启动失败，可点上面的「启动已下载客户端」手动重试。");
                BedrockClientStatusText.Text = $"已下载基岩版客户端 {selectedVersion.Name} 到：{finalDir}（自动启动失败）";
            }

            MessageBoxDialog.ShowSuccess(
                $"基岩版客户端 {selectedVersion.Name} 已下载并解压到：\n{finalDir}\n\n{launchNote}",
                Loc.T("Str_Cs_Download_Complete", "下载完成"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : Loc.T("Str_Cs_Bedrock_Client_Download_Failed", "下载基岩版客户端失败，可能是网络问题或该版本暂无下载链接。"),
                ex.ToString(), Loc.T("Str_Cs_Download_Failed", "下载失败"));
        }
        finally
        {
            pd.Close();
            BedrockClientDownloadBtn.IsEnabled = true;
        }
    }

    private async void BedrockClientLaunchDownloaded_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedBedrockClientDir))
        {
            MessageBoxDialog.ShowInfo(
                Loc.T("Str_Cs_No_Client_Instance_Selected_Body", "请先在下面的列表里选中一个已下载的基岩版客户端实例。"),
                Loc.T("Str_Cs_No_Client_Instance_Selected", "还没有选中实例"));
            return;
        }

        try
        {
            await BedrockClientDownloadService.LaunchClientAsync(_selectedBedrockClientDir);
            BedrockClientStatusText.Text = $"已启动：{_selectedBedrockClientDir}";
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : Loc.T("Str_Cs_Bedrock_Client_Launch_Failed", "启动基岩版客户端失败。"),
                ex.ToString(), Loc.T("Str_Cs_Couldn_T_Start_Bedrock_Edition", "启动失败"));
        }
    }

    // ============================================================
    // 内容导入
    // ============================================================

    private async void BedrockImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Str_Cs_Choose_Bedrock_Content_To_Import", "选择要导入的基岩版内容"),
            Filter = "基岩版内容|*.mcworld;*.mcpack;*.mcaddon;*.mctemplate|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        if (!BedrockContentService.IsBedrockDataPresent)
        {
            MessageBoxDialog.ShowInfo(
                Loc.T("Str_Cs_Bedrock_Not_Installed_Import_Body",
                    "这台电脑上还没有安装基岩版，或者基岩版从未启动过（首次启动才会创建数据目录），无法导入。"),
                Loc.T("Str_Cs_Bedrock_Edition_Isn_T_Installed", "还没有安装基岩版"));
            return;
        }

        var pd = new ProgressDialog(Loc.T("Str_Cs_Importing_Bedrock_Content", "正在导入基岩版内容 ..."));
        pd.Show();
        try
        {
            var files = dlg.FileNames.ToList();
            var r = await Task.Run(() => _bedrockService.ImportMany(files,
                new Progress<string>(msg => pd.Progress.Report(new ProgressInfo(msg, 0, 1, "")))));

            var lines = new List<string>();
            if (r.Installed.Count > 0) lines.Add($"成功导入 {r.Installed.Count} 项：\n" + string.Join("\n", r.Installed));
            if (r.Failed.Count > 0) lines.Add($"\n未能导入 {r.Failed.Count} 项：\n" + string.Join("\n", r.Failed));
            MessageBoxDialog.ShowInfo(string.Join("\n", lines) + "\n\n重启基岩版后生效。", Loc.T("Str_Cs_Import_Complete_2", "导入完成"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : Loc.T("Str_Cs_Couldn_T_Import_The_Bedrock_Content", "导入基岩版内容失败。"),
                ex.ToString(), Loc.T("Str_Cs_Import_Failed", "导入失败"));
        }
        finally { pd.Close(); }
    }

    // ============================================================
    // 基岩版专用服务端
    // ============================================================

    /// <summary>当前在"已安装实例"列表里选中的目录，Launch 按钮用这个；
    /// 下载成功后自动选中新装的那个。</summary>
    private string? _selectedBedrockServerDir;

    private void InitBedrockServerSection()
    {
        var cfg = _owner.ConfigService.Config;
        BedrockServerDefaultDirBox.Text = string.IsNullOrWhiteSpace(cfg.BedrockServerDefaultDownloadDir)
            ? Loc.T("Str_Ui_Not_Set_Will_Ask_Every_Time", "未设置（每次下载都会询问）")
            : cfg.BedrockServerDefaultDownloadDir;

        RefreshBedrockServerInstanceList();

        // 页面加载时自动拉一次版本列表（拉不到也能用"最新版"逻辑下载，不阻塞）
        _ = LoadServerVersionsAsync();
    }

    private BdsChannel GetSelectedServerChannel()
        => BedrockServerChannelCombo.SelectedIndex == 1 ? BdsChannel.Preview : BdsChannel.Stable;

    private async Task LoadServerVersionsAsync()
    {
        BedrockServerVersionCombo.IsEnabled = false;
        BedrockServerRefreshVersionsBtn.IsEnabled = false;
        BedrockServerVersionCombo.ItemsSource = null;

        try
        {
            var channel = GetSelectedServerChannel();
            var versions = await _bedrockService.GetDedicatedServerVersionsAsync(channel);
            if (versions.Count > 0)
            {
                // 多版本：列表展示可选的正式版/预览版，用户可在下拉框里挑具体版本下载。
                BedrockServerVersionCombo.ItemsSource = versions;
                BedrockServerVersionCombo.SelectedIndex = 0;
                BedrockServerStatusText.Text = $"该渠道已加载 {versions.Count} 个服务端版本，可直接在下拉框里选择要下载的版本。";
            }
            else
            {
                BedrockServerStatusText.Text = "服务端版本列表获取失败，将下载该渠道的最新版。";
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("加载基岩版服务端版本列表失败", ex);
            BedrockServerStatusText.Text = "服务端版本列表获取失败，将下载该渠道的最新版。";
        }
        finally
        {
            BedrockServerVersionCombo.IsEnabled = true;   // 多版本：允许自由选择
            BedrockServerRefreshVersionsBtn.IsEnabled = true;
        }
    }

    private async void BedrockServerChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 渠道切换时重新拉版本列表
        if (BedrockServerVersionCombo != null)
            _ = LoadServerVersionsAsync();
    }

    private async void BedrockServerRefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadServerVersionsAsync();
    }

    private void RefreshBedrockServerInstanceList()
    {
        var cfg = _owner.ConfigService.Config;
        BedrockServerInstanceList.ItemsSource = null;
        BedrockServerInstanceList.ItemsSource = cfg.BedrockServers
            .OrderByDescending(r => r.InstalledAtUtc)
            .Select(r => new
            {
                r.Id,
                r.Directory,
                Label = $"{r.DisplayName} — {r.Directory}",
            })
            .ToList();
        BedrockServerInstanceList.DisplayMemberPath = "Label";
        BedrockServerInstanceList.SelectedValuePath = "Directory";

        // 默认选中最近装的那个（列表已按时间倒序），方便刚下载完直接点启动。
        if (cfg.BedrockServers.Count > 0)
        {
            BedrockServerInstanceList.SelectedIndex = 0;
        }
        else
        {
            _selectedBedrockServerDir = null;
            BedrockServerLaunchBtn.IsEnabled = false;
        }
    }

    private void BedrockServerInstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedBedrockServerDir = BedrockServerInstanceList.SelectedValue as string;
        BedrockServerLaunchBtn.IsEnabled = !string.IsNullOrEmpty(_selectedBedrockServerDir);
    }

    private void BedrockServerBrowseDefaultDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("Str_Cs_Choose_Bedrock_Server_Default_Dir", "选择基岩版服务端的默认下载文件夹") };
        if (picker.ShowDialog() != true) return;

        var cfg = _owner.ConfigService.Config;
        cfg.BedrockServerDefaultDownloadDir = picker.FolderName;
        _owner.ConfigService.Save();
        BedrockServerDefaultDirBox.Text = picker.FolderName;
    }

    private void BedrockServerClearDefaultDir_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;
        cfg.BedrockServerDefaultDownloadDir = null;
        _owner.ConfigService.Save();
        BedrockServerDefaultDirBox.Text = Loc.T("Str_Ui_Not_Set_Will_Ask_Every_Time", "未设置（每次下载都会询问）");
    }

    private async void BedrockServerDownload_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;

        // 设置了默认文件夹就直接用；没设置才弹选择框（跟旧行为一致，不强迫用户先去设置默认值）。
        // 注意：这里下载到的是"默认文件夹本身"，不同版本/渠道各自建子目录，避免互相覆盖文件。
        string baseDir;
        if (!string.IsNullOrWhiteSpace(cfg.BedrockServerDefaultDownloadDir))
        {
            baseDir = cfg.BedrockServerDefaultDownloadDir!;
        }
        else
        {
            var picker = new OpenFolderDialog { Title = Loc.T("Str_Cs_Choose_Bedrock_Server_Install_Dir", "选择基岩版服务端的安装位置") };
            if (picker.ShowDialog() != true) return;
            baseDir = picker.FolderName;
        }

        var channel = GetSelectedServerChannel();

        // 多版本：使用下拉框里选中的版本；版本列表没加载出来时 SelectedItem 为 null，回退到该渠道最新版。
        string? selectedVersion = BedrockServerVersionCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selectedVersion)) selectedVersion = null;

        // 版本列表已加载时，能提前判断"这个版本是否已经装过"——装过就不再重复下载。
        var channelTag = channel == BdsChannel.Preview ? "preview" : "stable";
        var knownVersion = selectedVersion;
        if (!string.IsNullOrWhiteSpace(knownVersion))
        {
            var existingDir = Path.Combine(baseDir, $"bedrock-server-{knownVersion}-{channelTag}");
            if (Directory.Exists(existingDir) && File.Exists(Path.Combine(existingDir, "bedrock_server.exe")))
            {
                if (MessageBoxDialog.ShowConfirm(
                        $"基岩版服务端 {knownVersion} 已经安装过了：\n{existingDir}\n\n无需重复下载，直接启动它吗？",
                        Loc.T("Str_Cs_Download_Complete", "已安装")))
                {
                    try
                    {
                        await BedrockContentService.LaunchDedicatedServerAsync(existingDir);
                        BedrockServerStatusText.Text = $"已启动已安装的服务端 {knownVersion}：{existingDir}";
                    }
                    catch (Exception launchEx)
                    {
                        ErrorPresenter.LogFallback("启动已安装的基岩版服务端失败", launchEx);
                    }
                }
                return;
            }
        }

        BedrockServerDownloadBtn.IsEnabled = false;
        var pd = new ProgressDialog(Loc.T("Str_Cs_Downloading_Bedrock_Server", "正在下载基岩版服务端 ..."));
        pd.Show();
        try
        {
            // 子目录用"渠道-版本号"命名（下载前还不知道版本号，所以先下到 baseDir 下的临时
            // 渠道目录，拿到版本号后再改名成最终目录），保证同一个默认文件夹下装多个版本/
            // 反复重装不会互相覆盖 world/server.properties。
            var stagingDir = Path.Combine(baseDir, $"bedrock-server-{channelTag}-{Guid.NewGuid():N}".Substring(0, 40));

            var version = await _bedrockService.DownloadDedicatedServerAsync(stagingDir, channel, selectedVersion, pd.Progress);

            var finalDir = Path.Combine(baseDir, $"bedrock-server-{version}-{channelTag}");
            if (Directory.Exists(finalDir))
            {
                // 已经装过同一个版本/渠道：新下载的直接覆盖进旧目录（跟 DownloadDedicatedServerAsync
                // 内部本来就有的"保护 server.properties/allowlist 等配置文件"逻辑配合，相当于"更新"）。
                foreach (var f in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(stagingDir, f);
                    var dest = Path.Combine(finalDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f, dest, overwrite: true);
                }
                Directory.Delete(stagingDir, recursive: true);
            }
            else
            {
                Directory.Move(stagingDir, finalDir);
            }

            var record = cfg.BedrockServers.FirstOrDefault(r => r.Directory.Equals(finalDir, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                record = new BedrockServerRecord
                {
                    Directory = finalDir,
                    Version = version,
                    Channel = channel,
                    DisplayName = $"{version} ({(channel == BdsChannel.Preview ? "预览版" : "正式版")})",
                };
                cfg.BedrockServers.Add(record);
            }
            else
            {
                record.Version = version;
                record.InstalledAtUtc = DateTime.UtcNow;
            }
            _owner.ConfigService.Save();
            RefreshBedrockServerInstanceList();

            BedrockServerStatusText.Text = $"已安装基岩版服务端 {version} 到：{finalDir}";
            MessageBoxDialog.ShowSuccess(
                $"基岩版服务端 {version} 已下载并解压到：\n{finalDir}\n\n" +
                "可以直接点上面的「启动服务端」，也可以自己运行里面的 bedrock_server.exe。" +
                "首次运行会生成 server.properties，改完记得重启服务端。",
                Loc.T("Str_Cs_Download_Complete", "下载完成"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : Loc.T("Str_Cs_Bedrock_Server_Download_Failed", "下载基岩版服务端失败，可能是网络问题。"),
                ex.ToString(), Loc.T("Str_Cs_Download_Failed", "下载失败"));
        }
        finally
        {
            pd.Close();
            BedrockServerDownloadBtn.IsEnabled = true;
        }
    }

    private async void BedrockServerLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedBedrockServerDir))
        {
            MessageBoxDialog.ShowInfo(
                Loc.T("Str_Cs_No_Instance_Selected_Body", "请先在下面的列表里选中一个已安装的基岩版服务端实例。"),
                Loc.T("Str_Cs_No_Instance_Selected", "还没有选中实例"));
            return;
        }

        try
        {
            await BedrockContentService.LaunchDedicatedServerAsync(_selectedBedrockServerDir);
            BedrockServerStatusText.Text = $"已启动：{_selectedBedrockServerDir}（控制台窗口已单独弹出）";
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : Loc.T("Str_Cs_Bedrock_Server_Launch_Failed", "启动基岩版服务端失败。"),
                ex.ToString(), Loc.T("Str_Cs_Couldn_T_Start_Bedrock_Edition", "启动失败"));
        }
    }
}
