using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 超级"一键开始游戏"向导：把"创建/登录账户 -&gt; 选版本+加载器 -&gt; 选 Mod -&gt; 选资源包/光影包
/// -&gt; 下载+启动"这一整条新手最容易卡住的路径串成一个向导，串完自动调用
/// <see cref="MainWindow.Launch_Click"/> 直接进游戏。也支持"导入整合包"这条捷径，跳过选版本/
/// 选 Mod/选资源包，直接进入最后一步。
///
/// 步骤切换框架照抄 FirstRunWizardWindow.GoToStep 的写法；版本/加载器三级联动查询逻辑照抄
/// InstallClientLoaderWindow 的 LoaderType_Checked/LoadMcVersionsAsync/McVersionCombo_SelectionChanged；
/// 时序陷阱（XAML 默认选中的 RadioButton 在 InitializeComponent() 阶段就会触发 Checked 事件）用
/// InstallClientLoaderWindow 的 _initialized 字段方案（比 FirstRunWizardWindow 的
/// _suppressModeEvent 写法更简单，这里统一用这一种）。
///
/// 本轮（Round 12）跟用户确认过的四个设计决策，写死在下面对应位置，不再是"待定"：
/// 1) 步骤5下载编排失败策略：版本/加载器/Java 这些核心步骤失败就中止整个流程并弹窗提醒；
///    Mod/资源包这类次要步骤单条失败就跳过、继续走完，不阻断整体（跟 Fabric API 失败处理一致）。
/// 2) 游戏文件夹选择：向导没有单独一步，直接把文件夹选择控件放进步骤 2（版本+加载器）里。
/// 3) 账户校验：步骤 1 支持直接在向导内登录微软账户或创建离线账户（不需要跳转到账户管理页），
///    登录/创建后会保存进 ConfigService.Accounts，账户管理页也能看到；"下一步"离开步骤 1 前
///    要求必须已经选中一个账户，属于强校验。
/// 4) 入口：DownloadCenterPage 顶部加一个按钮（见该文件改动），不在 HomePage 加。
/// </summary>
public partial class QuickStartWizardWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly JavaService _javaService = new();
    private readonly ModrinthService _modrinth = new();
    private readonly CurseForgeKeyService _curseForgeKeyService = new();
    private CurseForgeService? _curseForge;
    private ClientLoaderInstallService? _loaderService;
    private readonly ModpackService _modpackService = new();

    private ModSearchService? _modSearch;
    private ModSearchService GetModSearch() => _modSearch ??= new ModSearchService(_modrinth, GetCurseForge());
    private CurseForgeService GetCurseForge() => _curseForge ??= new CurseForgeService(_curseForgeKeyService);

    /// <summary>0 = 选择来源页；1-5 = 原有下载向导五步。0 不计入步骤指示条（那条只表示 1-5）。</summary>
    private int _step = 0;
    private const int TotalSteps = 5;

    private readonly FolderService _folderService = new();

    /// <summary>"使用现有实例"模式下选中的 GameFolder + 版本，直接跳到 Java 检测+启动。</summary>
    private bool _useExistingInstance;
    private string? _existingInstanceFolderPath;
    private string? _existingInstanceVersionId;

    /// <summary>是否已经跑完构造函数里的 InitializeComponent()，跟 InstallClientLoaderWindow 同样的
    /// 时序陷阱：XAML 里 AccountModeOffline/LoaderVanilla/ResTypeResourcePack 都写了
    /// IsChecked="True"，会在 InitializeComponent() 阶段提前触发 Checked 事件，此时自动生成的
    /// 字段还没连接完，直接读会 NullReferenceException。所有相关事件处理器开头都要
    /// "if (!_initialized) return;"。</summary>
    private bool _initialized;

    private ServerCoreType _selectedLoaderType = ServerCoreType.Vanilla;
    private readonly ObservableCollection<string> _mcVersions = new();
    private readonly ObservableCollection<ServerCoreBuild> _buildVersions = new();

    private ModrinthResourceType _selectedResourceType = ModrinthResourceType.ResourcePack;

    private readonly ObservableCollection<SelectableModItem> _modResults = new();
    private readonly ObservableCollection<SelectableResourceItem> _resourceResults = new();

    /// <summary>步骤3"已选清单"：点进 ModDetailPage（AddToWizardList 模式）选中具体版本后加入这里，
    /// 步骤5下载时按这里锁定的 InlineVersionEntry 下载，不再自动匹配"最新版"。</summary>
    private readonly ObservableCollection<WizardModSelection> _modSelections = new();

    /// <summary>整页详情显示/收起，同 DownloadCenterPage.ShowDetail/HideDetail 的写法。</summary>
    private void ShowDetail(ModDetailPage page)
    {
        DetailHost.Content = page;
        DetailHost.Visibility = Visibility.Visible;
    }

    private void HideDetail()
    {
        DetailHost.Visibility = Visibility.Collapsed;
        DetailHost.Content = null;
    }

    /// <summary>导入整合包成功后会跳过"选版本/选Mod/选资源包"，标记这个状态，步骤 5 的下载编排
    /// 需要区分"走正常流程装的版本/Mod/资源包"和"整合包已经把这些都装好了，只需要装 Java + 启动"。</summary>
    private bool _importedModpack;
    private string? _importedVersionId;

    public QuickStartWizardWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        var cfg = _owner.ConfigService.Config;
        var existingDefault = cfg.Folders.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath)
            ?? cfg.Folders.FirstOrDefault(f => f.IsDefault) ?? cfg.Folders.FirstOrDefault();
        FolderPathBox.Text = existingDefault?.Path ?? Path.Combine(AppContext.BaseDirectory, ".minecraft");

        McVersionCombo.ItemsSource = _mcVersions;
        BuildVersionCombo.ItemsSource = _buildVersions;
        BuildVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
        ModResultsList.ItemsSource = _modResults;
        ResourceResultsList.ItemsSource = _resourceResults;
        ModSelectedList.ItemsSource = _modSelections;

        UpdateAccountStatusText();
        RefreshExistingAccountCombo();
        RefreshExistingFolderCombo();
        RefreshSimpleModeAvailability();

        _initialized = true;
        _ = LoadMcVersionsAsync();
    }

    // ========== 步骤 0：选择来源 ==========

    /// <summary>
    /// "傻瓜式启动游戏"要求用户已经导入过至少一个安装文件夹（cfg.Folders 里至少一个文件夹
    /// 底下能扫到已装好的版本），否则这个选项禁用并提示原因，避免用户选了之后卡在"文件夹是空的"
    /// 这种更困惑的状态。
    /// </summary>
    private void RefreshSimpleModeAvailability()
    {
        var cfg = _owner.ConfigService.Config;
        var hasImportedFolder = cfg.Folders.Any(f => Directory.Exists(f.Path) && _folderService.ScanVersions(f.Path).Any(v => v.IsInstalled));

        SourceModeSimple.IsEnabled = hasImportedFolder;
        SimpleModeHintText.Text = hasImportedFolder
            ? "已经导入过安装文件夹，可以直接选一个装好的版本快速启动，不用再走一遍下载流程。"
            : "需要先导入一个安装文件夹（用「一键下载游戏」装过版本，或者在下面选「使用现有实例」导入一次）才能使用这个选项。";
    }

    private void RefreshExistingFolderCombo()
    {
        var cfg = _owner.ConfigService.Config;
        ExistingFolderCombo.ItemsSource = cfg.Folders.Where(f => Directory.Exists(f.Path)).ToList();
        if (ExistingFolderCombo.Items.Count > 0) ExistingFolderCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// "傻瓜式启动"专用：只列出 cfg.Folders 里已经导入过、且能扫到已装好版本的文件夹——
    /// 跟"使用现有实例"不同，这里不提供"浏览其它位置"，因为这个选项的前提就是
    /// "用户之前已经用这个启动器导入过安装文件夹"，不是"随便指一个 .minecraft"。
    /// </summary>
    private void RefreshSimpleFolderCombo()
    {
        var cfg = _owner.ConfigService.Config;
        var foldersWithVersions = cfg.Folders
            .Where(f => Directory.Exists(f.Path) && _folderService.ScanVersions(f.Path).Any(v => v.IsInstalled))
            .ToList();
        SimpleFolderCombo.ItemsSource = foldersWithVersions;
        if (foldersWithVersions.Count > 0) SimpleFolderCombo.SelectedIndex = 0;
        else
        {
            SimpleVersionCombo.ItemsSource = null;
            SimpleModeStatusText.Text = "还没有任何已导入的安装文件夹，请先用「一键下载游戏」装一个版本，或者用「使用现有实例」导入一次。";
        }
    }

    private void SimpleFolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (SimpleFolderCombo.SelectedItem is not GameFolder folder) return;

        var versions = _folderService.ScanVersions(folder.Path).Where(v => v.IsInstalled).ToList();
        SimpleVersionCombo.ItemsSource = versions;
        SimpleVersionCombo.DisplayMemberPath = nameof(GameVersion.SubTitle);
        if (versions.Count > 0)
        {
            SimpleVersionCombo.SelectedIndex = 0;
            SimpleModeStatusText.Text = $"找到 {versions.Count} 个已装好的版本，选一个然后点「下一步」，选好账户就能直接启动。";
        }
        else
        {
            SimpleModeStatusText.Text = "这个文件夹里暂时没有已装好的版本。";
        }
    }

    private void RefreshExistingAccountCombo()
    {
        var accounts = _owner.ConfigService.Accounts;
        ExistingAccountCombo.ItemsSource = accounts;
        var selected = _owner.ConfigService.GetSelectedAccount();
        ExistingAccountPanel.Visibility = accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewAccountPanel.Visibility = accounts.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (selected != null) ExistingAccountCombo.SelectedItem = selected;
        else if (ExistingAccountCombo.Items.Count > 0) ExistingAccountCombo.SelectedIndex = 0;
    }

    private void ExistingAccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (ExistingAccountCombo.SelectedItem is Account acc) _owner.ConfigService.SelectAccount(acc.Id);
        UpdateAccountStatusText();
    }

    private void ShowNewAccountPanel_Click(object sender, RoutedEventArgs e)
    {
        ExistingAccountPanel.Visibility = Visibility.Collapsed;
        NewAccountPanel.Visibility = Visibility.Visible;
    }

    private void SourceMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        SimpleModePanel.Visibility = SourceModeSimple.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ExistingInstancePanel.Visibility = SourceModeExisting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ModpackImportPanel.Visibility = SourceModeModpack.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SourceModeSimple.IsChecked == true) RefreshSimpleFolderCombo();
    }

    private void BrowseExistingFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 .minecraft 文件夹位置" };

        // 修复编译错误 CS1503：见 CreateServerWindow.xaml.cs 同类注释——
        // ShowDialog 要 Window，改用 Window.GetWindow(this) 找到宿主 MainWindow。
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = dialog.FolderName;
        var versions = _folderService.ScanVersions(path).Where(v => v.IsInstalled).ToList();
        if (versions.Count == 0)
        {
            ExistingInstanceStatusText.Text = "这个文件夹里没有找到任何已装好的版本，请确认选的是正确的 .minecraft 文件夹。";
            ExistingVersionCombo.ItemsSource = null;
            return;
        }

        _existingInstanceFolderPath = path;
        ExistingVersionCombo.ItemsSource = versions;
        ExistingVersionCombo.DisplayMemberPath = nameof(GameVersion.SubTitle);
        ExistingVersionCombo.SelectedIndex = 0;
        ExistingInstanceStatusText.Text = $"找到 {versions.Count} 个已装好的版本，选一个然后点「下一步」即可自动匹配 Java 并启动。";
    }

    private void ExistingFolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (ExistingFolderCombo.SelectedItem is not GameFolder folder) return;

        var versions = _folderService.ScanVersions(folder.Path).Where(v => v.IsInstalled).ToList();
        _existingInstanceFolderPath = folder.Path;
        ExistingVersionCombo.ItemsSource = versions;
        ExistingVersionCombo.DisplayMemberPath = nameof(GameVersion.SubTitle);
        if (versions.Count > 0)
        {
            ExistingVersionCombo.SelectedIndex = 0;
            ExistingInstanceStatusText.Text = $"找到 {versions.Count} 个已装好的版本，选一个然后点「下一步」即可自动匹配 Java 并启动。";
        }
        else
        {
            ExistingInstanceStatusText.Text = "这个文件夹里没有找到任何已装好的版本，可以点「浏览其它位置...」换一个文件夹。";
        }
    }

    // ========== 步骤 1：账户 ==========

    private void UpdateAccountStatusText()
    {
        var existing = _owner.ConfigService.GetSelectedAccount();
        AccountStatusText.Text = existing == null
            ? "当前还没有任何账户，请先创建离线账户或登录微软账户。"
            : $"当前已选中账户：{existing.DisplayLabel}，可以直接点「下一步」，或者切换成另一种账户类型重新登录/创建。";
    }

    private void AccountMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var useMicrosoft = AccountModeMicrosoft.IsChecked == true;
        OfflineAccountPanel.Visibility = useMicrosoft ? Visibility.Collapsed : Visibility.Visible;
        MicrosoftAccountPanel.Visibility = useMicrosoft ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(OfflineNameBox.Text) ? "Player" : OfflineNameBox.Text.Trim();
        var account = OfflineAuthService.CreateOfflineAccount(name);
        _owner.ConfigService.AddOrUpdateAccount(account);
        // 需求变更：创建账户之后不再自动选中，需要用户自己在"已保存的账户"下拉框/账户管理页里选用。
        RefreshExistingAccountCombo();
        UpdateAccountStatusText();
        MessageBoxDialog.ShowInfo($"离线账户「{name}」创建成功，请在上方选择要使用的账户。", "完成");
    }

    /// <summary>
    /// 直接在向导内完成微软登录，不跳转到账户管理页——照抄 LoginPage.AddMicrosoft_Click 的写法
    /// （设备码弹窗 + 轮询），登录成功后同样调用 ConfigService.AddOrUpdateAccount/SelectAccount，
    /// 账户管理页读的是同一份 ConfigService.Accounts，自然也能看到这个新账户。
    /// </summary>
    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        MicrosoftLoginBtn.IsEnabled = false;
        AccountStatusText.Text = Loc.T("Str_Cs_Preparing_To_Sign_In_Please_Wait", "正在准备登录，请稍候...");

        MicrosoftAuthService auth;
        try
        {
            auth = new MicrosoftAuthService();
        }
        catch (AuthStepException ex)
        {
            AccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
            MicrosoftLoginBtn.IsEnabled = true;
            return;
        }

        var cts = new CancellationTokenSource();
        DeviceCodeWindow? popup = null;

        auth.UserCodeReady += (uri, code) =>
        {
            Dispatcher.Invoke(() =>
            {
                popup = new DeviceCodeWindow(uri, code, cts) ;
                popup.Show();
            });
        };
        auth.StatusChanged += status => Dispatcher.Invoke(() => popup?.SetStatus(status));

        try
        {
            var account = await auth.LoginInteractiveAsync(cts.Token);
            popup?.Dispatcher.Invoke(() => popup.Close());

            if (account == null)
            {
                AccountStatusText.Text = Loc.T("Str_Cs_Microsoft_Sign_In_Failed_Or_Was_Cancelle", "微软账户登录失败或已取消，请重试。");
                return;
            }
            _owner.ConfigService.AddOrUpdateAccount(account);
            // 需求变更：登录成功之后不再自动选中，需要用户自己选用。
            RefreshExistingAccountCombo();
            UpdateAccountStatusText();
        }
        catch (OperationCanceledException)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            AccountStatusText.Text = Loc.T("Str_Cs_Sign_In_Cancelled", "登录已取消。");
        }
        catch (AuthStepException ex)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            AccountStatusText.Text = $"登录在「{ex.Step}」这一步失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            popup?.Dispatcher.Invoke(() => popup.Close());
            AccountStatusText.Text = "登录出错：" + ex.Message;
        }
        finally
        {
            MicrosoftLoginBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 导入整合包：复用 ModpackService.Import（照抄 ModManagerPage.ImportModpack_Click 的调用方式）。
    /// 现在挂在步骤 0（选择来源页），此时还没走到账户步骤，所以不再要求"先有账户"；导入成功后
    /// 自动跳到步骤 1 去选账户，账户选完直接到步骤 5（GoToStep 逻辑里 _importedModpack 会跳过 2/3/4）。
    /// 支持 .xclpack / .mrpack（Modrinth）/ .zip 三种格式，具体解析交给 ModpackService.Import。
    /// </summary>
    private async void ImportModpackShortcut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包文件",
            Filter = "整合包 (*.xclpack;*.mrpack;*.zip)|*.xclpack;*.mrpack;*.zip|所有文件 (*.*)|*.*"
        };

        // 修复编译错误 CS1503：见 CreateServerWindow.xaml.cs 同类注释——
        // ShowDialog 要 Window，改用 Window.GetWindow(this) 找到宿主 MainWindow。
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var cfgFolders = _owner.ConfigService.Config.Folders;
        var defaultFolder = cfgFolders.FirstOrDefault(f => f.IsDefault) ?? cfgFolders.FirstOrDefault();
        var folderPath = defaultFolder?.Path ?? Path.Combine(AppContext.BaseDirectory, ".minecraft");
        try { Directory.CreateDirectory(folderPath); }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("无法创建游戏文件夹，可能是路径没有写入权限，或者路径包含非法字符。",
                $"[导入整合包] 创建游戏文件夹失败：{ex}", "出了点问题");
            return;
        }

        // 目标版本目录：整合包内部固定版本名的场景在 ModManagerPage 里也是这么处理的，
        // 由 Import/ImportMrpackAsync 自己在 targetVersionDir 下展开。
        var tempVersionId = "modpack-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var targetVersionDir = Path.Combine(folderPath, "versions", tempVersionId);

        ImportModpackBtn.IsEnabled = false;
        try
        {
            string packName;

            // .mrpack 是 Modrinth 官方整合包格式，内部结构跟 .xclpack/普通 .zip 完全不同
            // （只有下载清单，没有打包 mod jar 本体），需要单独走 ImportMrpackAsync 边解析边下载。
            if (ModpackService.IsMrpack(dialog.FileName))
            {
                var progress = new Progress<string>(msg => ModpackImportStatusText.Text = msg);
                var result = await _modpackService.ImportMrpackAsync(dialog.FileName, targetVersionDir, progress);
                packName = result.Name;
                if (result.FailedFiles.Count > 0)
                {
                    ErrorPresenter.ShowFriendlyError(
                        $"整合包「{result.Name}」大部分内容已导入成功，但有 {result.FailedFiles.Count} 个文件下载失败" +
                        "（可能是对应的 Mod 已在源站下架），可以之后在「Mod 管理」页手动补装。",
                        $"[导入 mrpack] 下载失败的文件：{string.Join(", ", result.FailedFiles)}", "部分导入失败");
                }
            }
            else
            {
                var manifest = _modpackService.Import(dialog.FileName, targetVersionDir);
                if (manifest == null)
                {
                    ErrorPresenter.ShowFriendlyError("整合包导入失败，文件格式不正确或者文件已损坏。",
                        $"[导入整合包] Import 返回 null，文件：{dialog.FileName}", "出了点问题");
                    return;
                }
                packName = manifest.Name;
            }

            _importedModpack = true;
            _importedVersionId = tempVersionId;

            var existing = _owner.ConfigService.Config.Folders.FirstOrDefault(f => f.Path == folderPath);
            if (existing == null)
            {
                existing = new GameFolder { Name = "整合包文件夹", Path = folderPath, IsDefault = _owner.ConfigService.Config.Folders.Count == 0 };
                _owner.ConfigService.Config.Folders.Add(existing);
            }
            _owner.ConfigService.Config.SelectedFolderPath = existing.Path;
            _owner.ConfigService.Save();
            FolderPathBox.Text = existing.Path;

            var label = string.IsNullOrWhiteSpace(packName) ? "整合包" : $"整合包「{packName}」";
            MessageBoxDialog.ShowInfo($"{label}导入成功！接下来选一个账户就能启动游戏了。", "完成");
            GoToStep(1);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("整合包导入失败，可能是文件损坏，网络问题（Modrinth 整合包需要联网下载文件），或磁盘空间/权限有问题。",
                $"[导入整合包] 导入异常，文件：{dialog.FileName}\n{ex}", "出了点问题");
        }
        finally
        {
            ImportModpackBtn.IsEnabled = true;
        }
    }

    // ========== 步骤 2：文件夹 + 版本 + 加载器 ==========

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏文件夹",
            InitialDirectory = Directory.Exists(FolderPathBox.Text) ? FolderPathBox.Text : AppContext.BaseDirectory
        };

        // 修复编译错误 CS1503：见 CreateServerWindow.xaml.cs 同类注释——
        // ShowDialog 要 Window，改用 Window.GetWindow(this) 找到宿主 MainWindow。
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderPathBox.Text = dialog.FolderName;
    }

    private ClientLoaderInstallService GetLoaderService()
        => _loaderService ??= new ClientLoaderInstallService(_owner.ConfigService.Config);

    private async void LoaderType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ServerCoreType>(tag, out var loaderType)) return;
        _selectedLoaderType = loaderType;

        BuildVersionPanel.Visibility = loaderType is ServerCoreType.Vanilla or ServerCoreType.NeoForge
            ? Visibility.Collapsed : Visibility.Visible;
        BuildVersionLabel.Text = loaderType switch
        {
            ServerCoreType.Fabric => "Loader 版本",
            ServerCoreType.Forge => "安装器版本",
            _ => "构建版本"
        };
        InstallFabricApiCheck.Visibility = loaderType == ServerCoreType.Fabric ? Visibility.Visible : Visibility.Collapsed;

        await LoadMcVersionsAsync();
    }

    /// <summary>
    /// 拉取当前选中加载器对应的 MC 版本列表。原版走 DownloadService.CreateFromConfig(...).
    /// GetVersionManifestAsync() 拿完整版本清单（这是 InstallClientLoaderWindow 里原来没有的分支，
    /// 因为那个窗口只处理加载器，不处理"原版"这个选项）；只保留 release 类型，新手向导不需要
    /// 展示快照版本，避免选到不稳定版本导致后续 Mod 兼容性问题。
    /// </summary>
    private async Task LoadMcVersionsAsync()
    {
        _mcVersions.Clear();
        _buildVersions.Clear();
        McVersionCombo.IsEnabled = false;
        Step2StatusText.Text = Loc.T("Str_Ui_Fetching_Versions", "正在获取版本列表...");

        try
        {
            if (_selectedLoaderType == ServerCoreType.Vanilla)
            {
                using var downloader = DownloadService.CreateFromConfig(_owner.ConfigService.Config);
                var manifest = await downloader.GetVersionManifestAsync();
                foreach (var v in manifest.Versions.Where(v => v.Type == "release")) _mcVersions.Add(v.Id);
            }
            else
            {
                List<string> versions = _selectedLoaderType switch
                {
                    ServerCoreType.Fabric => await GetLoaderService().GetFabricMcVersionsAsync(),
                    ServerCoreType.Forge => await GetLoaderService().GetForgeVersionsAsync(),
                    ServerCoreType.NeoForge => await GetLoaderService().GetNeoForgeVersionsAsync(),
                    _ => new List<string>()
                };
                foreach (var v in versions) _mcVersions.Add(v);
            }

            if (_mcVersions.Count > 0) McVersionCombo.SelectedIndex = 0;
            Step2StatusText.Text = "";
        }
        catch (Exception ex)
        {
            Step2StatusText.Text = $"获取版本列表失败：{ex.Message}";
        }
        finally
        {
            McVersionCombo.IsEnabled = true;
        }
    }

    /// <summary>展开/折叠"高级选项"面板（自定义参数/启动前命令/版本隔离/窗口大小），
    /// 默认折叠不打扰新手，勾选后才展开——对应用户截图里要求补齐的这几个高级字段。</summary>
    private void ShowAdvancedOptions_Changed(object sender, RoutedEventArgs e)
    {
        AdvancedOptionsPanel.Visibility = ShowAdvancedOptionsCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _buildVersions.Clear();
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        if (_selectedLoaderType is ServerCoreType.Vanilla or ServerCoreType.NeoForge) return;

        try
        {
            List<ServerCoreBuild> builds = _selectedLoaderType switch
            {
                ServerCoreType.Fabric => await GetLoaderService().GetFabricLoaderVersionsAsync(mcVersion),
                ServerCoreType.Forge => await GetLoaderService().GetForgeInstallerVersionsAsync(mcVersion),
                _ => new List<ServerCoreBuild>()
            };
            foreach (var b in builds) _buildVersions.Add(b);
            BuildVersionCombo.SelectedItem = builds.FirstOrDefault(b => b.IsRecommended) ?? builds.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Step2StatusText.Text = $"获取构建版本列表失败：{ex.Message}";
        }
    }

    // ========== 步骤 3：Mod ==========

    private void ModSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) _ = RunModSearchAsync();
    }

    private async void ModSearch_Click(object sender, RoutedEventArgs e) => await RunModSearchAsync();

    private async Task RunModSearchAsync()
    {
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        var loaderTag = _selectedLoaderType == ServerCoreType.Vanilla ? null : _selectedLoaderType.ToString().ToLowerInvariant();

        try
        {
            var outcome = await GetModSearch().SearchAsync(ModSource.Combined, ModSearchBox.Text ?? "", mcVersion, loaderTag);
            _modResults.Clear();
            foreach (var item in outcome.Items) _modResults.Add(new SelectableModItem(item));
            UpdateModSelectedCount();
            if (outcome.Warnings.Count > 0) Step3HintText.Text = string.Join("；", outcome.Warnings);
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("搜索 Mod 失败，可能是网络连接问题，请检查网络后重试。",
                $"[一键启动向导 - 搜索Mod失败] {ex}", "搜索失败");
        }
    }

    private void UpdateModSelectedCount()
    {
        var count = _modSelections.Count;
        ModSelectedCountText.Text = count > 0 ? $"已选 {count} 个 Mod" : "";
    }

    /// <summary>点击搜索结果里的一个 Mod 条目：跳转整页详情（AddToWizardList 模式），
    /// 选具体版本后加入"已选清单"，不在这里下载任何东西——跟 DownloadCenterPage.OpenModDetailAsync
    /// 是同一个"构造详情页 + 异步拉版本 + ShowGroups"套路，只是模式换成 AddToWizardList。</summary>
    private async void ModResultItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not SelectableModItem selectable) return;
        var item = selectable.Item;

        var sourceUrl = item.RawItem switch
        {
            ModrinthSearchHit h => $"https://modrinth.com/mod/{h.Slug}",
            CurseForgeMod m => m.Links?.WebsiteUrl,
            _ => null
        };

        var detail = new ModDetailPage(
            ModDetailPage.DetailMode.AddToWizardList,
            item.Title, item.Description, item.IconUrl, item.Author, item.Downloads,
            item.SourceLabel, sourceUrl, item, isFavorite: false,
            onFavoriteToggle: null,
            onBack: HideDetail,
            onAddToList: (entry, _) =>
            {
                AddOrReplaceModSelection(item, entry);
                HideDetail();
            });

        ShowDetail(detail);

        detail.ShowLoading();
        var groups = await LoadModVersionGroupsAsync(item);
        detail.ShowGroups(groups);
    }

    /// <summary>拉取一个 Mod 条目的版本列表并按"加载器+游戏版本"分组——跟
    /// DownloadCenterPage.LoadModVersionsAsync 是同一套逻辑，这里独立实现一份而不是共享那个方法，
    /// 因为那个方法直接写回 UnifiedModItem.Versions/Groups（下载中心自己的展示状态），
    /// 向导这边不需要长期持有这份状态，选完版本就丢弃，直接返回分组列表即可。</summary>
    private async Task<List<VersionGroup>> LoadModVersionGroupsAsync(UnifiedModItem item)
    {
        var mcVersion = McVersionCombo.SelectedItem as string;
        var entries = new List<InlineVersionEntry>();
        try
        {
            if (item.Source == ModSource.Modrinth)
            {
                var versions = await _modrinth.GetVersionsAsync(item.SourceId, mcVersion);
                foreach (var v in versions) entries.Add(new InlineVersionEntry(v));
            }
            else if (item.Source == ModSource.CurseForge)
            {
                var modId = int.Parse(item.SourceId);
                var files = await GetCurseForge().GetFilesAsync(modId, mcVersion);
                foreach (var f in files) entries.Add(new InlineVersionEntry(f));
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Version_List_This_Is_", "获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"),
                $"[一键启动向导 - 获取Mod版本列表失败] {ex}", "获取版本列表失败");
            return new List<VersionGroup>();
        }

        var groups = ModVersionGrouping.Group(entries);
        if (groups.Count > 0) groups[0].IsExpanded = true;
        return groups;
    }

    /// <summary>加入"已选清单"：同一个 Mod（按 Source+SourceId 判断）重复选择会替换掉旧的选择，
    /// 而不是重复添加两条——用户在详情页里换了个版本重新点"加入清单"，应该理解成"改选这个版本"。</summary>
    private void AddOrReplaceModSelection(UnifiedModItem item, InlineVersionEntry entry)
    {
        var existing = _modSelections.FirstOrDefault(s => s.Source == item.Source && s.SourceId == item.SourceId);
        if (existing != null) _modSelections.Remove(existing);
        _modSelections.Add(new WizardModSelection(item, entry));
        UpdateModSelectedCount();
    }

    private void RemoveModSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not WizardModSelection selection) return;
        _modSelections.Remove(selection);
        UpdateModSelectedCount();
    }

    // ========== 步骤 4：资源包/光影包 ==========

    private void ResourceType_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _selectedResourceType = ResTypeShader.IsChecked == true ? ModrinthResourceType.Shader : ModrinthResourceType.ResourcePack;
    }

    private void ResourceSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) _ = RunResourceSearchAsync();
    }

    private async void ResourceSearch_Click(object sender, RoutedEventArgs e) => await RunResourceSearchAsync();

    private async Task RunResourceSearchAsync()
    {
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        try
        {
            var outcome = await GetModSearch().SearchResourcesAsync(ModSource.Combined, _selectedResourceType,
                ResourceSearchBox.Text ?? "", mcVersion);
            _resourceResults.Clear();
            foreach (var item in outcome.Items) _resourceResults.Add(new SelectableResourceItem(item));
            UpdateResourceSelectedCount();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Search_Failed_Most_Likely_A_Network_Prob", "搜索失败，可能是网络连接问题，请检查网络后重试。"), $"[搜索失败] {ex}", "搜索失败");
        }
    }

    private void UpdateResourceSelectedCount()
    {
        var count = _resourceResults.Count(r => r.IsSelected);
        ResourceSelectedCountText.Text = count > 0 ? $"已选 {count} 个" : "";
    }

    // ========== 通用：步骤切换 ==========

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            if (SourceModeSimple.IsChecked == true)
            {
                if (SimpleFolderCombo.SelectedItem is not GameFolder || SimpleVersionCombo.SelectedItem is not GameVersion simpleVersion)
                {
                    ErrorPresenter.ShowFriendlyError("请先选择一个已导入的文件夹，并选中里面一个已装好的版本。",
                        "[一键启动向导] 傻瓜式启动：未选择文件夹或版本", "提示");
                    return;
                }
                _useExistingInstance = true;
                _existingInstanceFolderPath = (SimpleFolderCombo.SelectedItem as GameFolder)!.Path;
                _existingInstanceVersionId = simpleVersion.Id;
                GoToStep(1);
                return;
            }

            if (SourceModeExisting.IsChecked == true)
            {
                if (_existingInstanceFolderPath == null || ExistingVersionCombo.SelectedItem is not GameVersion version)
                {
                    ErrorPresenter.ShowFriendlyError("请先选择一个 .minecraft 文件夹，并选中里面一个已装好的版本。",
                        "[一键启动向导] 使用现有实例：未选择文件夹或版本", "提示");
                    return;
                }
                _useExistingInstance = true;
                _existingInstanceVersionId = version.Id;
                GoToStep(1);
                return;
            }

            if (SourceModeModpack.IsChecked == true)
            {
                // 导入整合包走独立按钮（ImportModpackShortcut_Click），这里只提示用户点那个按钮。
                ErrorPresenter.ShowFriendlyError("请先点击「选择整合包文件...」完成导入，导入成功后会自动跳到下一步。",
                    "[一键启动向导] 导入整合包模式：未点击导入按钮", "提示");
                return;
            }

            _useExistingInstance = false;
            GoToStep(1);
            return;
        }

        if (_step == 1)
        {
            if (_owner.ConfigService.GetSelectedAccount() == null)
            {
                ErrorPresenter.ShowFriendlyError("请先创建离线账户或登录微软账户，才能继续下一步。",
                    "[一键启动向导] 步骤1：未选择账户", "提示");
                return;
            }
            // 使用现有实例/已导入整合包：跳过选版本/选Mod/选资源包，直接到第 5 步确认+启动。
            if (_useExistingInstance || _importedModpack)
            {
                GoToStep(5);
                return;
            }
            GoToStep(2);
            return;
        }

        if (_step == 2)
        {
            if (McVersionCombo.SelectedItem is not string)
            {
                MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Minecraft_Version", "请选择 Minecraft 版本。"), Loc.T("Str_Status_Tip", "提示"));
                return;
            }
            if (_selectedLoaderType != ServerCoreType.Vanilla && _selectedLoaderType != ServerCoreType.NeoForge
                && BuildVersionCombo.SelectedItem == null)
            {
                MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Build_Or_Loader_Version", "请选择构建/加载器版本。"), Loc.T("Str_Status_Tip", "提示"));
                return;
            }
            // 原版不能装 Mod，从步骤 2 直接跳到步骤 4（跳过步骤 3）。
            GoToStep(_selectedLoaderType == ServerCoreType.Vanilla ? 4 : 3);
            return;
        }

        if (_step == TotalSteps) return; // 步骤 5 没有"下一步"，靠 StartAll_Click 收尾
        GoToStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 0) return;
        // 使用现有实例/已导入整合包：第 5 步往回退，跳过 2/3/4，直接回到第 1 步（账户）。
        if (_step == 5 && (_useExistingInstance || _importedModpack))
        {
            GoToStep(1);
            return;
        }
        // 步骤 4 往回退：原版跳过了步骤 3，回退时同样要跳过，直接回到步骤 2。
        if (_step == 4 && _selectedLoaderType == ServerCoreType.Vanilla)
        {
            GoToStep(2);
            return;
        }
        if (_step == 1)
        {
            GoToStep(0);
            return;
        }
        GoToStep(_step - 1);
    }

    private void GoToStep(int step)
    {
        _step = Math.Clamp(step, 0, TotalSteps);

        Step0Panel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5Panel.Visibility = _step == 5 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var gray = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDD, 0xDD));
        StepDot1.Background = _step >= 1 ? accent : gray;
        StepDot2.Background = _step >= 2 ? accent : gray;
        StepDot3.Background = _step >= 3 ? accent : gray;
        StepDot4.Background = _step >= 4 ? accent : gray;
        StepDot5.Background = _step >= 5 ? accent : gray;

        BackBtn.IsEnabled = _step > 0;
        NextBtn.Visibility = _step < TotalSteps ? Visibility.Visible : Visibility.Collapsed;

        if (_step == 0) RefreshSimpleModeAvailability();
        if (_step == 1) { UpdateAccountStatusText(); RefreshExistingAccountCombo(); }
        if (_step == 3 && _modResults.Count == 0) _ = RunModSearchAsync();
        if (_step == 4 && _resourceResults.Count == 0) _ = RunResourceSearchAsync();
        if (_step == 5) UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_useExistingInstance)
        {
            SummaryText.Text = $"账户：{_owner.ConfigService.GetSelectedAccount()?.DisplayLabel}\n" +
                $"使用现有实例：{_existingInstanceFolderPath}\n" +
                $"版本：{_existingInstanceVersionId}\n\n" +
                "不会重新下载任何文件，接下来只会自动检测/安装匹配的 Java，然后直接启动游戏。";
            return;
        }

        if (_importedModpack)
        {
            SummaryText.Text = $"账户：{_owner.ConfigService.GetSelectedAccount()?.DisplayLabel}\n" +
                "已通过整合包导入版本/Mod/资源包，接下来会自动检测/安装 Java 并直接启动游戏。";
            return;
        }

        var acc = _owner.ConfigService.GetSelectedAccount();
        var loaderDesc = _selectedLoaderType == ServerCoreType.Vanilla ? "原版" : _selectedLoaderType.ToString();
        var mcVersion = McVersionCombo.SelectedItem as string ?? "(未选择)";
        var modCount = _modSelections.Count;
        var resCount = _resourceResults.Count(r => r.IsSelected);

        SummaryText.Text =
            $"账户：{acc?.DisplayLabel}\n" +
            $"游戏文件夹：{FolderPathBox.Text}\n" +
            $"版本：{mcVersion}（{loaderDesc}）\n" +
            $"Mod：{(modCount > 0 ? $"{modCount} 个" : "不安装")}\n" +
            $"资源包/光影包：{(resCount > 0 ? $"{resCount} 个" : "不安装")}\n\n" +
            "点击下面的按钮开始下载并启动游戏。核心步骤（版本/加载器/Java）失败会中止流程并提示；" +
            "Mod/资源包这类次要步骤某一项失败只会跳过，不影响其它内容继续安装。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(null);

    // ========== 步骤 5：下载编排 + 启动 ==========

    /// <summary>核心步骤（文件夹/版本/加载器/Java）执行失败时抛出，携带用户可读的阶段名，
    /// 用于在 catch 里统一弹窗提醒并中止整个流程——对应跟用户确认过的失败策略第 1 条。</summary>
    private class CriticalStepFailedException : Exception
    {
        public string StageName { get; }
        public CriticalStepFailedException(string stage, Exception inner)
            : base($"「{stage}」失败：{inner.Message}", inner) => StageName = stage;
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        StartAllBtn.IsEnabled = false;
        BackBtn.IsEnabled = false;
        RunPanel.Visibility = Visibility.Visible;
        RunBar.Value = 0;

        try
        {
            var cfg = _owner.ConfigService.Config;
            string versionId;

            if (_useExistingInstance)
            {
                // 使用现有实例：文件夹/版本都已经确定好了，不下载任何东西，只需要确保文件夹记录在
                // ConfigService.Folders 里（方便下次在「文件夹」页也能看到），然后直接走 Java 检测。
                versionId = _existingInstanceVersionId!;
                var existingFolder = cfg.Folders.FirstOrDefault(f => f.Path == _existingInstanceFolderPath);
                if (existingFolder == null)
                {
                    existingFolder = new GameFolder
                    {
                        Name = "现有实例",
                        Path = _existingInstanceFolderPath!,
                        IsDefault = cfg.Folders.Count == 0
                    };
                    cfg.Folders.Add(existingFolder);
                }
                cfg.SelectedFolderPath = existingFolder.Path;
                cfg.SelectedVersionId = versionId;
                _owner.ConfigService.Save();
                ReportRun("使用现有实例", "跳过下载，直接进入 Java 检测。", 30);
            }
            else if (_importedModpack)
            {
                // 整合包已经把版本/Mod/资源包都装好了，这里只需要确认文件夹配置已经写回
                // （ImportModpackShortcut_Click 里已经写过一次，这里是防御性兜底）。
                versionId = _importedVersionId!;
                ReportRun("整合包已导入", "跳过版本/Mod/资源包下载，直接进入 Java 检测。", 30);
            }
            else
            {
                // 1) 文件夹：确定/新建
                ReportRun("准备游戏文件夹", FolderPathBox.Text, 5);
                var folderPath = string.IsNullOrWhiteSpace(FolderPathBox.Text)
                    ? Path.Combine(AppContext.BaseDirectory, ".minecraft")
                    : FolderPathBox.Text.Trim();
                try { Directory.CreateDirectory(folderPath); }
                catch (Exception ex) { throw new CriticalStepFailedException("准备游戏文件夹", ex); }

                var folder = cfg.Folders.FirstOrDefault(f => f.Path == folderPath);
                if (folder == null)
                {
                    folder = new GameFolder { Name = "默认文件夹", Path = folderPath, IsDefault = cfg.Folders.Count == 0 };
                    cfg.Folders.Add(folder);
                }
                cfg.SelectedFolderPath = folder.Path;
                _owner.ConfigService.Save();

                if (McVersionCombo.SelectedItem is not string mcVersion)
                    throw new CriticalStepFailedException("选择版本", new InvalidOperationException("没有选中 Minecraft 版本。"));

                // 2) 装版本/加载器。Forge/NeoForge 安装器需要本地 Java 才能跑，必须先确保 Java 就绪，
                //    不能像原计划那样等到"启动前"才检测——这是 Round 11 交接文档里明确指出的顺序依赖问题。
                string? javaPath = null;
                if (_selectedLoaderType is ServerCoreType.Forge or ServerCoreType.NeoForge)
                {
                    javaPath = await EnsureJavaReadyAsync(mcVersion, stageOffset: 5, stageWeight: 15);
                }

                var progress = new Progress<ProgressInfo>(p =>
                    ReportRun(p.Stage, p.CurrentFile, 20 + (p.Total > 0 ? (double)p.Done / p.Total * 30 : 0)));

                try
                {
                    if (_selectedLoaderType == ServerCoreType.Vanilla)
                    {
                        using var downloader = DownloadService.CreateFromConfig(cfg);
                        var manifest = await downloader.GetVersionManifestAsync();
                        var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                            ?? throw new InvalidOperationException($"在版本清单中找不到 {mcVersion}。");
                        await downloader.InstallVersionAsync(folderPath, entry, progress);
                        versionId = mcVersion;
                    }
                    else if (_selectedLoaderType == ServerCoreType.Fabric)
                    {
                        var buildVersion = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion
                            ?? throw new InvalidOperationException("没有选中 Fabric Loader 版本。");
                        versionId = await GetLoaderService().InstallFabricClientAsync(folderPath, mcVersion, buildVersion,
                            progress, installFabricApi: InstallFabricApiCheck.IsChecked == true);
                    }
                    else
                    {
                        javaPath ??= await EnsureJavaReadyAsync(mcVersion, stageOffset: 20, stageWeight: 0);
                        var fullVersion = _selectedLoaderType == ServerCoreType.NeoForge
                            ? mcVersion
                            : (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion
                                ?? throw new InvalidOperationException("没有选中安装器版本。");
                        versionId = await GetLoaderService().InstallForgeOrNeoForgeClientAsync(
                            folderPath, _selectedLoaderType, fullVersion, javaPath, progress);
                    }
                }
                catch (Exception ex) when (ex is not CriticalStepFailedException)
                {
                    throw new CriticalStepFailedException("安装版本/加载器", ex);
                }

                // 3) 下载"已选清单"里锁定的具体版本：次要步骤，单条失败跳过、不中止整个流程。
                //    不再自动匹配"最新版"——清单里的 InlineVersionEntry 就是用户在 ModDetailPage
                //    里明确选中的那个文件。
                for (var i = 0; i < _modSelections.Count; i++)
                {
                    var selection = _modSelections[i];
                    var pct = 50 + (_modSelections.Count > 0 ? (double)i / _modSelections.Count * 20 : 0);
                    ReportRun("下载 Mod", selection.Title, pct);
                    await DownloadModSelectionSafeAsync(folderPath, selection);
                }

                // 4) 下载选中的资源包/光影包：同样次要步骤，单条失败跳过。
                var selectedRes = _resourceResults.Where(r => r.IsSelected).ToList();
                for (var i = 0; i < selectedRes.Count; i++)
                {
                    var res = selectedRes[i];
                    var pct = 70 + (selectedRes.Count > 0 ? (double)i / selectedRes.Count * 15 : 0);
                    ReportRun("下载资源包/光影包", res.Title, pct);
                    await DownloadResourceSafeAsync(folderPath, mcVersion, res);
                }
            }

            // 5) Java 检测/下载（Fabric/原版走到这里才第一次确保 Java；Forge/NeoForge 之前已经确保过）。
            ReportRun("检查 Java 环境", "", 88);
            var finalMinecraftDir = cfg.Folders.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath)?.Path
                ?? FolderPathBox.Text;
            string javaFinal;
            try
            {
                javaFinal = await EnsureJavaReadyAsync(_importedModpack ? null : McVersionCombo.SelectedItem as string,
                    stageOffset: 88, stageWeight: 8, minecraftDirForVersion: finalMinecraftDir, versionIdForJava: versionId);
            }
            catch (Exception ex)
            {
                throw new CriticalStepFailedException("安装 Java", ex);
            }
            if (string.IsNullOrEmpty(cfg.JavaPath)) cfg.JavaPath = javaFinal;

            // 6) 写回配置：注意 Fabric/Forge/NeoForge 装完后返回的 versionId 跟用户在下拉框选的
            //    MC 版本号不是同一个字符串，必须用安装方法的返回值。
            cfg.SelectedVersionId = versionId;

            // 高级选项只在走了正常下载流程（经过步骤 2）时才有意义——使用现有实例/导入整合包
            // 都跳过了步骤 2，AdvancedOptionsPanel 里的控件在这两种模式下没有被用户碰过，
            // 不应该拿默认值覆盖用户之前已经保存的设置。
            if (!_useExistingInstance && !_importedModpack && ShowAdvancedOptionsCheck.IsChecked == true)
            {
                cfg.CustomJvmArgs = string.IsNullOrWhiteSpace(CustomJvmArgsBox.Text) ? null : CustomJvmArgsBox.Text.Trim();
                cfg.PreLaunchCommand = string.IsNullOrWhiteSpace(PreLaunchCommandBox.Text) ? null : PreLaunchCommandBox.Text.Trim();
                if (int.TryParse(WindowWidthBox.Text, out var w) && w > 0) cfg.WindowWidth = w;
                if (int.TryParse(WindowHeightBox.Text, out var h) && h > 0) cfg.WindowHeight = h;
                cfg.VersionIsolationOverrides[versionId] = IsolateVersionCheck.IsChecked == true;

                // 自定义 JVM 参数在 MainWindow.LaunchInternalAsync 里只在 AdvancedMode=true 时生效，
                // 用户在向导里主动填了自定义参数，说明明确想要用这个设置，这里一并打开开关，
                // 否则填了却在启动时被静默忽略，会让用户困惑"为什么设置了没生效"。
                if (!string.IsNullOrWhiteSpace(cfg.CustomJvmArgs)) cfg.AdvancedMode = true;
            }

            _owner.ConfigService.Save();

            ReportRun("全部就绪，正在启动游戏...", "", 100);
            await Task.Delay(400);

            // 7) 启动游戏：MainWindow.Launch_Click 是 public，专门为跨窗口复用改过。
            // 修复"选择账户"弹窗点不了的问题：Launch_Click 内部在需要用户选择账户时会
            // 弹出 AccountPickerDialog，这个弹窗是挂在 MainWindow 里的 Overlay，而不是独立
            // Window——如果这里先调用 Launch_Click 再 Close() 向导，向导这个独立 Window 此时
            // 仍然在最上层、仍然是模态/置顶状态，MainWindow 里刚弹出的 Overlay 会被向导窗口
            // 整个盖住，用户能看见（透过向导半透明遮罩背景）却怎么点都点不到。必须先关闭向导
            // 窗口，让 MainWindow 重新成为最上层活动窗口，Overlay 才能真正接收到鼠标点击。
            //
            // 修复"傻瓜式启动/一键开始游戏走完之后，还会再弹一次「选择要用来启动游戏的账户」"：
            // 本向导步骤 1 已经强制要求用户显式选中/登录/创建了一个账户才能进入下一步，这里
            // 调用 Launch_Click 时账户已经是用户刚刚确认过的，不需要 Launch_Click 内部再重复
            // 弹一次一模一样的账户选择框——传 skipAccountConfirm: true 跳过那一步，直接用
            // 当前已选中的账户启动。
            _owner.RefreshSidebar();
            CloseWith(null);
            _owner.Launch_Click(this, new RoutedEventArgs(), skipAccountConfirm: true);
        }
        catch (CriticalStepFailedException ex)
        {
            ErrorPresenter.ShowFriendlyError(
                $"「{ex.StageName}」这一步失败了，一键启动没能完成，可能是网络连接问题或下载源暂时不可用，建议稍后重试。",
                $"[一键启动失败 - {ex.StageName}] {ex.InnerException}",
                "一键开始游戏失败");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                "一键启动过程中出现了意外错误，建议检查网络连接后重试。",
                $"[一键启动失败 - 未分类异常] {ex}",
                "一键开始游戏失败");
        }
        finally
        {
            StartAllBtn.IsEnabled = true;
            BackBtn.IsEnabled = _step > 1;
        }
    }

    private void ReportRun(string stage, string detail, double percent)
    {
        RunStageText.Text = stage;
        RunDetailText.Text = detail;
        RunBar.Value = Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// 确保有一个可用的 Java：先按目标 MC 版本（或已装好的版本 json，如果有）估算/读取所需的
    /// Java 主版本号去匹配本机已有安装，找不到就自动下载推荐 LTS 版本。复用
    /// JavaService.FindJava/DownloadJavaAsync，不重新发明一遍"自动匹配 + 找不到就下载"逻辑，
    /// 参照 MainWindow.LaunchInternalAsync/FirstRunWizardWindow.OneClickComplete_Click 里已有的写法。
    /// </summary>
    private async Task<string> EnsureJavaReadyAsync(string? mcVersionForEstimate, double stageOffset, double stageWeight,
        string? minecraftDirForVersion = null, string? versionIdForJava = null)
    {
        var cfg = _owner.ConfigService.Config;

        int? preferMajor = null;
        if (minecraftDirForVersion != null && versionIdForJava != null)
        {
            try { preferMajor = LauncherService.GetRequiredJavaMajorVersion(minecraftDirForVersion, versionIdForJava); }
            catch { /* 读取失败就退回按 MC 版本号估算 */ }
        }
        preferMajor ??= mcVersionForEstimate != null
            ? ServerJavaRequirement.EstimateMajorVersionForMcVersion(mcVersionForEstimate)
            : (int?)null;

        var found = _javaService.FindJava(cfg.JavaPath, preferMajor, _owner.ConfigService);
        if (found != null)
        {
            ReportRun("检查 Java 环境", $"已找到可用 Java：{found}", stageOffset + stageWeight);
            return found;
        }

        ReportRun("下载 Java 运行时", "本机没有匹配的 Java，正在自动下载...", stageOffset);
        var progress = new Progress<ProgressInfo>(p =>
        {
            var pct = stageOffset + (p.Total > 0 ? (double)p.Done / p.Total * stageWeight : 0);
            ReportRun("下载 Java 运行时", p.CurrentFile, pct);
        });

        var request = new JavaDownloadRequest(
            preferMajor ?? 21,
            Environment.Is64BitOperatingSystem ? "x64" : "x86",
            JavaInstallMode.Portable);
        return await _javaService.DownloadJavaAsync(request, progress);
    }

    /// <summary>
    /// 按"已选清单"里锁定的具体版本（WizardModSelection.Entry）下载，不再重新查询/匹配"最新版"——
    /// 用户在 ModDetailPage 里选的是哪个文件，这里就下载哪个文件。下载失败/次要步骤异常不阻断整体
    /// 流程，只记录到运行详情文本里，原则跟 ClientLoaderInstallService 里 Fabric API 那部分一致。
    /// </summary>
    private async Task DownloadModSelectionSafeAsync(string minecraftDir, WizardModSelection selection)
    {
        try
        {
            if (selection.Source == ModSource.Modrinth && selection.Entry.RawVersion is ModrinthVersion version)
            {
                await _modrinth.DownloadResourceAsync(minecraftDir, ModrinthResourceType.Mod, version, null, saveName: null);
            }
            else if (selection.Source == ModSource.CurseForge && selection.Entry.RawVersion is CurseForgeFile file)
            {
                await GetCurseForge().DownloadModAsync(minecraftDir, file, null);
            }
        }
        catch (Exception ex)
        {
            // 次要步骤失败不中止整体流程，只记录到运行详情文本里（用户在步骤 5 界面能看到这行
            // 一闪而过；更完整的记录可以在后续版本里接入 LogsPage，这里先保证不阻断主流程）。
            ReportRun("下载 Mod", $"{selection.Title}：下载失败（{ex.Message}），已跳过", RunBar.Value);
        }
    }

    private async Task DownloadResourceSafeAsync(string minecraftDir, string mcVersion, SelectableResourceItem res)
    {
        try
        {
            if (res.Source == ModSource.Modrinth)
            {
                var versions = await _modrinth.GetVersionsAsync(res.SourceId, mcVersion);
                var pick = versions.FirstOrDefault();
                if (pick == null)
                {
                    ReportRun("下载资源包/光影包", $"{res.Title}：没有匹配 {mcVersion} 的版本，已跳过", RunBar.Value);
                    return;
                }
                await _modrinth.DownloadResourceAsync(minecraftDir, _selectedResourceType, pick, null, saveName: null);
            }
            else if (res.Source == ModSource.CurseForge)
            {
                var modId = int.Parse(res.SourceId);
                var files = await GetCurseForge().GetFilesAsync(modId, mcVersion);
                var pick = files.FirstOrDefault();
                if (pick == null)
                {
                    ReportRun("下载资源包/光影包", $"{res.Title}：没有匹配 {mcVersion} 的文件，已跳过", RunBar.Value);
                    return;
                }
                var kind = _selectedResourceType == ModrinthResourceType.Shader
                    ? CurseForgeResourceKind.Shader : CurseForgeResourceKind.ResourcePack;
                await GetCurseForge().DownloadResourceAsync(minecraftDir, kind, pick, null);
            }
        }
        catch (Exception ex)
        {
            ReportRun("下载资源包/光影包", $"{res.Title}：下载失败（{ex.Message}），已跳过", RunBar.Value);
        }
    }
}

/// <summary>
/// Mod 搜索结果的可勾选包装类。没有直接给 UnifiedModItem 加 IsSelected 属性——那是
/// Models/ModSearchModels.cs 里的公共模型，被 DownloadCenterPage 等其它页面复用，加属性
/// 会影响那些地方的绑定行为，改用包装类更安全，参照 ModManagerPage 底部的
/// LocalModDisplayItem 包装类写法。
/// </summary>
public class SelectableModItem
{
    public UnifiedModItem Item { get; }
    public bool IsSelected { get; set; }
    public string Title => Item.Title;
    public string Description => Item.Description;
    public ModSource Source => Item.Source;
    public string SourceId => Item.SourceId;

    public SelectableModItem(UnifiedModItem item) => Item = item;
}

/// <summary>步骤3"已选清单"里的一条记录：来源 Mod（UnifiedModItem）+ 用户在 ModDetailPage 里
/// 明确选中的具体版本文件（InlineVersionEntry），步骤5下载时直接用 Entry.RawVersion 下载，
/// 不再重新查询"匹配当前游戏版本的最新版"。</summary>
public class WizardModSelection
{
    public UnifiedModItem Item { get; }
    public InlineVersionEntry Entry { get; }
    public string Title => Item.Title;
    public ModSource Source => Item.Source;
    public string SourceId => Item.SourceId;
    public string VersionLabel => $"{Entry.Name}（{Entry.GameVersionsText}）";

    public WizardModSelection(UnifiedModItem item, InlineVersionEntry entry)
    {
        Item = item;
        Entry = entry;
    }
}

/// <summary>同上，资源包/光影包搜索结果的可勾选包装类。</summary>
public class SelectableResourceItem
{
    public UnifiedResourceItem Item { get; }
    public bool IsSelected { get; set; }
    public string Title => Item.Title;
    public string Description => Item.Description;
    public ModSource Source => Item.Source;
    public string SourceId => Item.SourceId;

    public SelectableResourceItem(UnifiedResourceItem item) => Item = item;
}
