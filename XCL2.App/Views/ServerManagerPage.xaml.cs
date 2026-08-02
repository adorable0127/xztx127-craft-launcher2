using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 服务端管理页：目前实现了"核心下载"（Vanilla/Paper/Fabric 直接下载，Forge/NeoForge
/// 下载安装器 + 本地运行安装）、"服务器列表"、"资源包下载"（复用 DownloadCenterPage 同一套
/// Modrinth/CurseForge 搜索+下载逻辑，目标目录换成选中的服务器实例）。"插件下载"仍先占位，
/// 待后续实现。
/// </summary>
public partial class ServerManagerPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly ServerCoreDownloadService _coreService = new();
    private readonly JavaService _javaService = new();

    private ServerCoreType _selectedCoreType = ServerCoreType.Vanilla;
    private readonly ObservableCollection<string> _mcVersions = new();
    private readonly ObservableCollection<ServerCoreBuild> _buildVersions = new();

    // 下载完成后，若需要安装（Forge/NeoForge），暂存下来供"立即运行安装"按钮使用
    private ServerCoreDownloadResult? _pendingInstallResult;
    private string? _pendingInstallTargetDir;
    /// <summary>Spigot 走 RunSpigotBuildToolsAsync 编译时需要传 MC 版本号（BuildTools 的 --rev 参数），
    /// Forge/NeoForge 的 RunForgeInstallerAsync 不需要这个，仅在 RequiresBuild 场景下会用到。</summary>
    private string? _pendingInstallMcVersion;
    /// <summary>
    /// 修复"核心下载面板下载完成后，服务器不会出现在服务器列表里"：这个面板只负责下载/安装
    /// 服务端核心文件本身，跟"创建服务器"向导（CreateServerWindow）是两条独立路径——之前
    /// 只有 CreateServerWindow 那条路径会在成功后调用 ServerInstanceService.Add() 登记一个
    /// ServerInstance，这里下载完只弹了个"下载完成"的提示框，没有登记，导致"服务器列表"
    /// 面板永远看不到这个刚下载好的核心。这里在发起下载前记下这次请求的核心类型/MC版本，
    /// 供下载/安装全部完成后统一调用 RegisterDownloadedInstance 登记。</summary>
    private ServerCoreType _pendingInstallCoreType;
    private string? _pendingInstallMcVersionForRegister;

    // ===== 资源包下载面板：与 DownloadCenterPage 的材质包分类共用 ModrinthService/CurseForgeService/
    // ModSearchService（这几个服务类本身不跟 .minecraft 目录绑定，创建成本也很低，没必要要求
    // MainWindow 注入同一个实例；跟 DownloadCenterPage 保持各自独立一份是这个项目里已有的模式，
    // 比如 CurseForgeMapPickerDialog/ModManagerPage 也都是各自 new 一份）。
    private readonly ModrinthService _resourceModrinth = new();
    private readonly CurseForgeKeyService _resourceCurseForgeKeyService = new();
    private CurseForgeService? _resourceCurseForge;
    private ModSearchService? _resourceModSearch;
    private readonly ObservableCollection<UnifiedResourceItem> _serverResources = new();
    private ModSource _serverResourceSource = ModSource.Combined;
    /// <summary>当前"服务端资源下载"面板选中的资源类型，默认插件——服务端场景下插件是最常用的
    /// 下载需求（Bukkit/Spigot/Paper 生态），跟客户端默认"Mod"分类的默认值不同。</summary>
    private ModrinthResourceType _serverResourceType = ModrinthResourceType.Plugin;
    private DispatcherTimer? _serverResourceDebounceTimer;
    private Action? _pendingServerResourceDebouncedAction;
    private int _serverResourceSearchSeq;
    private bool _serverResourcePanelLoaded;

    /// <summary>
    /// 是否已经跑完构造函数里的 InitializeComponent()。
    ///
    /// 崩溃根因：与 DownloadCenterPage 完全相同的时序问题——XAML 里左侧分类栏默认选中的
    /// RadioButton 会在 InitializeComponent() 解析阶段同步触发 Checked 事件，但此时
    /// CorePanel/InstancesPanel/ServerResourcePanel 等自动生成字段还没赋值，
    /// Category_Checked 一读就是 NullReferenceException。用同一套 _initialized 短路方案。
    /// </summary>
    private bool _initialized;

    public ServerManagerPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        McVersionCombo.ItemsSource = _mcVersions;
        BuildVersionCombo.ItemsSource = _buildVersions;
        BuildVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);

        TargetDirBox.Text = System.IO.Path.Combine(App.DataDir, "servers");

        ServerResourceListBox.ItemsSource = _serverResources;
        _serverResourceDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _serverResourceDebounceTimer.Tick += (_, _) =>
        {
            _serverResourceDebounceTimer.Stop();
            var action = _pendingServerResourceDebouncedAction;
            _pendingServerResourceDebouncedAction = null;
            action?.Invoke();
        };

        _initialized = true;
        Category_Checked(CatHome, new RoutedEventArgs()); // 补上初始化阶段被跳过的那次面板显隐

        _ = LoadMcVersionsAsync();
    }

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // InitializeComponent() 过程中触发的事件：控件树还没解析完，直接跳过
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag as string;

        HomePanel.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        CorePanel.Visibility = tag == "core" ? Visibility.Visible : Visibility.Collapsed;
        InstancesPanel.Visibility = tag == "instances" ? Visibility.Visible : Visibility.Collapsed;
        // "插件下载"和"资源包"现在共用同一个 ServerResourcePanel（内部靠 SrvRes* 单选按钮切换
        // 具体资源类型），不再是两个分开的入口/占位符。左侧点"插件下载"时把面板内的类型选择器
        // 也同步切到"插件"，点"资源包"时切到"资源包"，避免出现"点插件却看到材质包"的错觉。
        ServerResourcePanel.Visibility = tag is "plugins" or "resourcepack" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "instances") RefreshInstanceList();
        if (tag == "plugins")
        {
            InitServerResourcePanel();
            if (SrvResPlugin.IsChecked != true) SrvResPlugin.IsChecked = true;
            else RefreshServerResourceTitle();
        }
        else if (tag == "resourcepack")
        {
            InitServerResourcePanel();
            if (SrvResResourcePack.IsChecked != true) SrvResResourcePack.IsChecked = true;
            else RefreshServerResourceTitle();
        }
    }

    private async void CoreType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // 同上：避免 InitializeComponent() 解析阶段的默认选中触发未初始化字段访问
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ServerCoreType>(tag, out var coreType)) return;
        _selectedCoreType = coreType;

        // Vanilla 没有单独的"构建版本"概念，隐藏该栏；Spigot 走 BuildTools 本地编译同样没有
        // build 号概念（只需要选 MC 版本），也隐藏；其余类型显示对应标签
        BuildVersionPanel.Visibility = coreType is ServerCoreType.Vanilla or ServerCoreType.Spigot
            ? Visibility.Collapsed : Visibility.Visible;
        BuildVersionLabel.Text = coreType switch
        {
            ServerCoreType.Paper => "Build 号",
            ServerCoreType.Fabric => "Loader 版本",
            ServerCoreType.Forge => "安装器版本",
            ServerCoreType.NeoForge => "版本号",
            ServerCoreType.Purpur => "Build 号",
            ServerCoreType.Folia => "Build 号",
            ServerCoreType.Velocity => "Build 号",
            ServerCoreType.Waterfall => "Build 号",
            _ => "构建版本"
        };

        InstallRequiredPanel.Visibility = Visibility.Collapsed;
        _pendingInstallResult = null;

        await LoadMcVersionsAsync();
    }

    private async Task LoadMcVersionsAsync()
    {
        _mcVersions.Clear();
        _buildVersions.Clear();
        McVersionCombo.IsEnabled = false;
        DownloadCoreBtn.IsEnabled = false;

        try
        {
            List<string> versions = _selectedCoreType switch
            {
                ServerCoreType.Vanilla => await _coreService.GetVanillaVersionsAsync(includeSnapshots: false),
                ServerCoreType.Paper => await _coreService.GetPaperVersionsAsync(),
                ServerCoreType.Fabric => await _coreService.GetFabricMcVersionsAsync(),
                ServerCoreType.Forge => await _coreService.GetForgeVersionsAsync(),
                ServerCoreType.NeoForge => await LoadNeoForgeMcVersionPlaceholderAsync(),
                ServerCoreType.Purpur => await _coreService.GetPurpurVersionsAsync(),
                ServerCoreType.Folia => await _coreService.GetFoliaVersionsAsync(),
                ServerCoreType.Velocity => await _coreService.GetVelocityVersionsAsync(),
                ServerCoreType.Waterfall => await _coreService.GetWaterfallVersionsAsync(),
                ServerCoreType.Spigot => await _coreService.GetVanillaVersionsAsync(includeSnapshots: false),
                _ => new List<string>()
            };

            // 版本号排序：尝试按语义化版本从新到旧排列，排不了的（NeoForge 独立编号体系等）保留原始顺序
            foreach (var v in versions) _mcVersions.Add(v);

            if (_mcVersions.Count > 0) McVersionCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            McVersionCombo.IsEnabled = true;
            DownloadCoreBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// NeoForge 的版本号是独立编号（如 21.1.100），不是直接的 MC 版本号；
    /// 这里直接把 NeoForge 版本号本身列出来供用户选择，"MC 版本"栏对 NeoForge 而言
    /// 实际展示的就是完整 NeoForge 版本号，下载时二者取值相同。
    /// 后续如果要做"输入 MC 版本反查 NeoForge 版本"的映射，需要额外解析 NeoForge 版本号的命名约定，
    /// 当前先用这个更简单但完全可用的方式实现。
    /// </summary>
    private async Task<List<string>> LoadNeoForgeMcVersionPlaceholderAsync()
        => await _coreService.GetNeoForgeVersionsAsync();

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _buildVersions.Clear();
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        if (_selectedCoreType == ServerCoreType.Vanilla) return;
        if (_selectedCoreType == ServerCoreType.NeoForge) return; // NeoForge 的"版本"栏就是完整版本号，无需二级选择
        if (_selectedCoreType == ServerCoreType.Spigot) return; // Spigot 走本地编译，没有 build 号可选

        try
        {
            List<ServerCoreBuild> builds = _selectedCoreType switch
            {
                ServerCoreType.Paper => await _coreService.GetPaperBuildsAsync(mcVersion),
                ServerCoreType.Fabric => await _coreService.GetFabricLoaderVersionsAsync(),
                ServerCoreType.Forge => await _coreService.GetForgeInstallerVersionsAsync(mcVersion),
                ServerCoreType.Purpur => await _coreService.GetPurpurBuildsAsync(mcVersion),
                ServerCoreType.Folia => await _coreService.GetFoliaBuildsAsync(mcVersion),
                ServerCoreType.Velocity => await _coreService.GetVelocityBuildsAsync(mcVersion),
                ServerCoreType.Waterfall => await _coreService.GetWaterfallBuildsAsync(mcVersion),
                _ => new List<ServerCoreBuild>()
            };

            foreach (var b in builds) _buildVersions.Add(b);

            var recommended = builds.FirstOrDefault(b => b.IsRecommended);
            BuildVersionCombo.SelectedItem = recommended ?? builds.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取构建版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取构建版本列表失败] {ex}", "获取构建版本列表失败");
        }
    }

    private void BrowseTargetDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择服务端安装位置" };
        if (System.IO.Directory.Exists(TargetDirBox.Text)) dialog.InitialDirectory = TargetDirBox.Text;
        if (dialog.ShowDialog() == true)
            TargetDirBox.Text = dialog.FolderName;
    }

    private async void DownloadCore_Click(object sender, RoutedEventArgs e)
    {
        if (McVersionCombo.SelectedItem is not string mcVersion)
        {
            MessageBoxDialog.ShowInfo("请先选择 Minecraft 版本。");
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetDirBox.Text))
        {
            MessageBoxDialog.ShowInfo("请先选择安装位置。");
            return;
        }

        // 目标目录已存在且非空时提醒一下，避免用户没意识到会往一个已有文件夹里混入服务端文件
        if (System.IO.Directory.Exists(TargetDirBox.Text) &&
            System.IO.Directory.EnumerateFileSystemEntries(TargetDirBox.Text).Any())
        {
            var confirm = MessageBoxDialog.ShowConfirm(
                $"目标目录「{TargetDirBox.Text}」不是空目录，服务端文件会下载到这个目录下。确定继续吗？",
                "确认");
            if (!confirm) return;
        }

        var buildVersion = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion;

        var req = new ServerCoreDownloadRequest
        {
            CoreType = _selectedCoreType,
            McVersion = mcVersion,
            TargetDir = TargetDirBox.Text
        };

        // Forge 的"构建版本"下拉框里存的就是完整安装器版本号（mcVer-forgeVer），
        // NeoForge 则直接用选中的 McVersion 本身（见 LoadNeoForgeMcVersionPlaceholderAsync 的说明）
        if (_selectedCoreType == ServerCoreType.Forge) req.InstallerVersion = buildVersion;
        else if (_selectedCoreType == ServerCoreType.NeoForge) req.InstallerVersion = mcVersion;
        else req.BuildOrLoaderVersion = buildVersion;

        DownloadCoreBtn.IsEnabled = false;
        InstallRequiredPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBarCtl.Value = 0;

        var progress = new Progress<ProgressInfo>(p =>
        {
            ProgressStageText.Text = p.Stage;
            ProgressDetailText.Text = p.CurrentFile;
            ProgressBarCtl.Maximum = Math.Max(p.Total, 1);
            ProgressBarCtl.Value = p.Done;
        });

        // 记下这次请求的核心类型/MC版本，供下载/安装全部完成后 RegisterDownloadedInstance 使用
        // （req.TargetDir 本身已经记录在各分支的 _pendingInstallTargetDir 里，不需要重复存）。
        _pendingInstallCoreType = _selectedCoreType;
        _pendingInstallMcVersionForRegister = mcVersion;

        try
        {
            var result = await _coreService.DownloadAsync(req, progress);

            if (result.RequiresBuild)
            {
                // Spigot：复用同一个"待安装"面板和按钮，只是文案换成"编译"，
                // RunInstaller_Click 内部会根据 _pendingInstallResult.RequiresBuild 分流到
                // RunSpigotBuildToolsAsync 而不是 RunForgeInstallerAsync。
                _pendingInstallResult = result;
                _pendingInstallTargetDir = req.TargetDir;
                _pendingInstallMcVersion = mcVersion;
                InstallHintText.Text = "Spigot 官方不提供预编译文件，需要本地用 BuildTools 编译（需要联网 + 已安装 Git，" +
                    "耗时可能有几分钟）。点击下方按钮开始编译。";
                InstallRequiredPanel.Visibility = Visibility.Visible;
            }
            else if (result.RequiresInstall)
            {
                _pendingInstallResult = result;
                _pendingInstallTargetDir = req.TargetDir;
                _pendingInstallMcVersion = null; // Forge/NeoForge 安装器不需要这个，清空避免残留上次 Spigot 流程的值
                InstallHintText.Text = $"{_selectedCoreType} 官方只提供安装器，需要本地再运行一次才能生成实际可用的服务端文件。" +
                    "点击下方按钮，使用启动器已配置的 Java 自动完成安装。";
                InstallRequiredPanel.Visibility = Visibility.Visible;
            }
            else
            {
                // 不需要额外安装步骤（Vanilla/Paper/Folia/Purpur/Fabric/Velocity/Waterfall）：
                // 下载即可用，立即登记成一个服务器实例，用户不需要再手动去"创建服务器"重复一遍。
                var registered = RegisterDownloadedInstance(req.TargetDir, _selectedCoreType, mcVersion,
                    result.ServerJarFileName ?? "server.jar", isScript: false, result.RequiredJavaMajorVersion);

                MessageBoxDialog.ShowSuccess(registered
                        ? $"服务端核心下载完成，已自动添加到「服务器列表」：\n{result.DownloadedFilePath}"
                        : $"服务端核心下载完成：\n{result.DownloadedFilePath}\n\n（未能自动添加到服务器列表，可以在「创建服务器」里手动指向这个目录。）");
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[下载失败] {ex}", "下载失败");
        }
        finally
        {
            DownloadCoreBtn.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void RunInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingInstallResult == null || _pendingInstallTargetDir == null) return;

        var javaPath = _javaService.FindJava(_owner.ConfigService.Config.JavaPath,
            _owner.ConfigService.Config.PreferredJavaMajorVersion, _owner.ConfigService);
        if (javaPath == null)
        {
            MessageBoxDialog.ShowWarning(
                "没有找到可用的 Java，无法运行安装器。请先在「设置」页配置或下载 Java 后再试。",
                "缺少 Java");
            return;
        }

        RunInstallerBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStageText.Text = _pendingInstallResult.RequiresBuild ? "正在用 BuildTools 编译 Spigot" : "正在运行安装器";
        ProgressDetailText.Text = _pendingInstallResult.RequiresBuild
            ? "首次编译需要联网拉取源码并本地反编译/打补丁，可能需要几分钟，请耐心等待..."
            : "";
        ProgressBarCtl.IsIndeterminate = true;

        var progress = new Progress<string>(line => ProgressDetailText.Text = line);

        try
        {
            string resultPath;
            if (_pendingInstallResult.RequiresBuild)
            {
                if (string.IsNullOrEmpty(_pendingInstallMcVersion))
                    throw new InvalidOperationException("内部错误：缺少 Spigot 编译所需的 MC 版本号，请重新走一遍下载流程。");
                resultPath = await _coreService.RunSpigotBuildToolsAsync(
                    _pendingInstallResult.DownloadedFilePath, _pendingInstallTargetDir, javaPath,
                    _pendingInstallMcVersion, progress);
            }
            else
            {
                resultPath = await _coreService.RunForgeInstallerAsync(
                    _pendingInstallResult.DownloadedFilePath, _pendingInstallTargetDir, javaPath, progress);
            }

            InstallRequiredPanel.Visibility = Visibility.Collapsed;

            // 同"直接下载即可用"分支一样，安装器/BuildTools 跑完之后核心才算真正就绪，
            // 这里补上登记服务器实例这一步，之前这里只弹了个提示框，没有调用
            // ServerInstanceService.Add，导致 Forge/NeoForge/Spigot 下载完同样不会出现在
            // "服务器列表"里。
            string launchTarget;
            bool launchTargetIsScript;
            if (File.Exists(resultPath) &&
                (resultPath.EndsWith("run.bat", StringComparison.OrdinalIgnoreCase) ||
                 resultPath.EndsWith("run.sh", StringComparison.OrdinalIgnoreCase)))
            {
                launchTarget = Path.GetFileName(resultPath);
                launchTargetIsScript = true;
            }
            else
            {
                launchTarget = Path.GetFileName(resultPath);
                launchTargetIsScript = false;
            }

            var registered = RegisterDownloadedInstance(_pendingInstallTargetDir, _pendingInstallCoreType,
                _pendingInstallMcVersionForRegister ?? "", launchTarget, launchTargetIsScript,
                _pendingInstallResult.RequiredJavaMajorVersion, javaPath);

            MessageBoxDialog.ShowSuccess(registered
                    ? $"{(_pendingInstallResult.RequiresBuild ? "编译" : "安装")}完成！服务端已生成到：\n{_pendingInstallTargetDir}\n\n生成文件：{resultPath}\n\n已自动添加到「服务器列表」。"
                    : $"{(_pendingInstallResult.RequiresBuild ? "编译" : "安装")}完成！服务端已生成到：\n{_pendingInstallTargetDir}\n\n生成文件：{resultPath}");
            _pendingInstallResult = null;
            _pendingInstallMcVersion = null;
        }
        catch (Exception ex)
        {
            var isBuild = _pendingInstallResult?.RequiresBuild == true;
            ErrorPresenter.ShowFriendlyError(
                isBuild
                    ? "编译失败，可能是网络连接问题、缺少 Git，或该 MC 版本 BuildTools 不再支持，请检查后重试。"
                    : "安装失败，可能是网络连接问题、下载源暂时不可用，或安装文件已损坏，请检查网络后重试。",
                $"[{(isBuild ? "编译" : "安装")}失败] {ex}", isBuild ? "编译失败" : "安装失败");
        }
        finally
        {
            RunInstallerBtn.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressBarCtl.IsIndeterminate = false;
        }
    }

    // ============================================================
    // 服务器列表：启动/停止/控制台/删除
    // ============================================================

    /// <summary>
    /// 把"核心下载"面板刚下载/安装完成的服务端核心登记成一份 ServerInstance，
    /// 让它出现在"服务器列表"面板里——之前这个面板下载完只弹提示框，不调用
    /// ServerInstanceService.Add，用户还得再走一遍"创建服务器"向导重新指向同一个目录
    /// 才能让它出现在列表里。这里尽量复用 CreateServerWindow 里同样的登记方式，
    /// 保持字段含义一致（DisplayName/Directory/CoreType/McVersion/LaunchTarget 等）。
    ///
    /// 目录名作为默认显示名：下载面板本身没有"服务器名称"这个输入框，用户是先选目录再下载，
    /// 用目录的文件夹名当默认名字最直观（用户随时可以在服务器列表里重命名）。
    ///
    /// 如果同一个目录已经登记过一个实例（比如用户对着同一个目录重复点了几次"开始下载"，
    /// 或者之前已经通过"创建服务器"向导指向过这个目录），不重复添加，只更新已有记录的
    /// 核心类型/MC版本/启动目标，避免服务器列表里出现一堆指向同一个目录的重复条目。
    /// </summary>
    private bool RegisterDownloadedInstance(string targetDir, ServerCoreType coreType, string mcVersion,
        string launchTarget, bool isScript, int requiredJavaMajorVersion, string? javaPath = null)
    {
        try
        {
            var fullDir = Path.GetFullPath(targetDir);

            var existing = _owner.ServerInstanceService.Instances.FirstOrDefault(i =>
                string.Equals(Path.GetFullPath(i.Directory), fullDir, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.CoreType = coreType;
                existing.McVersion = mcVersion;
                existing.LaunchTarget = launchTarget;
                existing.LaunchTargetIsScript = isScript;
                existing.RequiredJavaMajorVersion = requiredJavaMajorVersion;
                if (!string.IsNullOrEmpty(javaPath)) existing.JavaPath = javaPath;
                _owner.ServerInstanceService.Update(existing);
                RefreshInstanceList();
                return true;
            }

            var resolvedJavaPath = javaPath
                ?? _javaService.FindJava(_owner.ConfigService.Config.JavaPath, requiredJavaMajorVersion, _owner.ConfigService);

            var displayName = Path.GetFileName(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(displayName)) displayName = $"{coreType} {mcVersion}";

            // 名称冲突时加序号，跟服务器列表其它地方对重名的处理方式保持一致
            var baseName = displayName;
            var suffix = 1;
            while (_owner.ServerInstanceService.Instances.Any(i => i.DisplayName == displayName))
            {
                suffix++;
                displayName = $"{baseName} ({suffix})";
            }

            var instance = new ServerInstance
            {
                DisplayName = displayName,
                Directory = fullDir,
                CoreType = coreType,
                McVersion = mcVersion,
                LaunchTarget = launchTarget,
                LaunchTargetIsScript = isScript,
                JavaPath = resolvedJavaPath,
                RequiredJavaMajorVersion = requiredJavaMajorVersion
            };

            _owner.ServerInstanceService.Add(instance);
            RefreshInstanceList();
            return true;
        }
        catch
        {
            // 登记失败不应该掩盖"下载/安装已经成功"这个事实——调用方会在提示文案里说明
            // "未能自动添加到服务器列表，可以手动创建"，用户手头的服务端文件本身没有任何损失。
            return false;
        }
    }

    // ============================================================
    // 首页磁贴：四个入口分别对应"傻瓜式开服/启动所选/插件资源包/服务器参数"
    // ============================================================

    /// <summary>
    /// "启动所选的服务器"和"设置选中的服务器参数"这两个磁贴需要先确定"选中的服务器"是哪一个，
    /// 但首页本身没有单独的服务器选择控件（不想在磁贴上再叠一层下拉框，违背"磁贴=一步到位"
    /// 的设计初衷）。这里用同一套"默认服务器优先，没设默认就用列表第一个"规则：
    /// - 用户在服务器列表页右键某个实例"设为默认服务器"过，这里就精确对应那一个；
    /// - 完全没设置过默认时，回退成"服务器列表"面板里最上面那个（Instances 列表第一项），
    /// 这样只有一台服务器的最常见场景不需要额外操作，直接就是它。
    /// 列表为空（还没创建过任何服务器）时返回 null，调用方据此提示用户先去"傻瓜式开服"。
    /// </summary>
    private ServerInstance? GetPreferredInstance()
    {
        var instances = _owner.ServerInstanceService.Instances;
        if (instances.Count == 0) return null;
        return instances.FirstOrDefault(i => i.IsDefault) ?? instances[0];
    }

    /// <summary>
    /// "傻瓜式开服"：直接复用"创建服务器"向导——它本身就是"选核心类型/MC版本 -> 自动下载
    /// -> 需要时自动装安装器/编译 -> 自动匹配 Java -> 登记成服务器实例"一整条不需要用户
    /// 理解任何底层细节的流程，不需要为首页再单独做一套简化版。
    /// </summary>
    private void TileEasySetup_Click(object sender, RoutedEventArgs e)
    {
        CreateServer_Click(sender, e);
    }

    /// <summary>
    /// "启动所选的服务器"：目标实例正在运行时不重复启动（避免用户连点两次导致端口冲突之类的
    /// 报错），改为直接打开它的控制台窗口，跟点服务器列表卡片上"打开控制台"的效果一致。
    /// </summary>
    private void TileStartSelected_Click(object sender, RoutedEventArgs e)
    {
        var instance = GetPreferredInstance();
        if (instance == null)
        {
            MessageBoxDialog.ShowInfo("还没有任何服务器，请先用「傻瓜式开服」创建一个。");
            return;
        }

        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            OpenConsole(instance);
            return;
        }

        StartInstance(instance);
    }

    /// <summary>
    /// "插件、资源包管理"：跳到左侧"插件下载"分类。插件和资源包本来就共用同一个
    /// ServerResourcePanel（内部用 SrvRes* 单选按钮切类型），选中"插件下载"这个分类
    /// 就已经能在同一个面板里切到资源包，不需要在首页额外做"插件"和"资源包"两个磁贴。
    /// </summary>
    private void TilePluginResource_Click(object sender, RoutedEventArgs e)
    {
        CatPlugins.IsChecked = true;
    }

    /// <summary>
    /// "设置选中的服务器参数"：对 GetPreferredInstance() 选出的目标实例打开
    /// ServerPropertiesWindow，跟服务器列表卡片上"服务器设置"菜单项调用的是同一个方法。
    /// </summary>
    private void TileServerSettings_Click(object sender, RoutedEventArgs e)
    {
        var instance = GetPreferredInstance();
        if (instance == null)
        {
            MessageBoxDialog.ShowInfo("还没有任何服务器，请先用「傻瓜式开服」创建一个。");
            return;
        }

        OpenServerProperties(instance);
    }

    private void RefreshInstanceList()
    {
        InstanceListPanel.Children.Clear();
        var instances = _owner.ServerInstanceService.Instances;

        if (instances.Count == 0)
        {
            InstanceListPanel.Children.Add(new TextBlock
            {
                Text = "还没有创建任何服务器，点击右上角「创建服务器」开始。",
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var instance in instances)
            InstanceListPanel.Children.Add(BuildInstanceCard(instance));
    }

    private Border BuildInstanceCard(ServerInstance instance)
    {
        var isRunning = _owner.ServerProcessManager.IsRunning(instance.Id);

        var card = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SideBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标：有自定义图标就加载显示，否则用一个占位方块 + 首字符，保持卡片布局不因缺图标而错位。
        var iconHost = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(6),
            Background = (System.Windows.Media.Brush)FindResource("GlowSoftBrush"),
            Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true
        };
        if (!string.IsNullOrEmpty(instance.IconPath) && File.Exists(instance.IconPath))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(instance.IconPath, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // 立即读完文件，避免占用文件句柄导致后续换图标时删不掉旧文件
                bmp.EndInit();
                iconHost.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
            }
            catch
            {
                // 图标文件损坏/格式不支持：静默回退到占位符，不阻断整个列表的渲染
                iconHost.Child = BuildIconPlaceholder(instance.DisplayName);
            }
        }
        else
        {
            iconHost.Child = BuildIconPlaceholder(instance.DisplayName);
        }
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(new TextBlock
        {
            Text = instance.DisplayName, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center
        });
        titleLine.Children.Add(new Border
        {
            Background = isRunning ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = isRunning ? "运行中" : "已停止", Foreground = System.Windows.Media.Brushes.White, FontSize = 11
            }
        });
        if (instance.IsDefault)
        {
            titleLine.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock { Text = "默认", Foreground = System.Windows.Media.Brushes.White, FontSize = 11 }
            });
        }
        infoPanel.Children.Add(titleLine);
        infoPanel.Children.Add(new TextBlock
        {
            Text = $"{instance.CoreType} · MC {instance.McVersion} · 内存 {instance.MinMemoryMb}~{instance.MaxMemoryMb}MB" +
                   (instance.CpuLimitPercent != null ? $" · CPU上限 {instance.CpuLimitPercent}%" : ""),
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        });

        // 连接地址：修复"新增出来的服务器没有 IP 地址"——之前卡片完全不展示怎么连进这个服务器，
        // 这里读取 server.properties 的 server-port + 本机局域网 IP 拼出连接地址；
        // 未运行时也照样展示（server.properties 在核心下载完成后就已经存在，不需要等服务器启动）。
        var connectionText = ServerConnectionInfoService.Resolve(instance);
        var addressLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        addressLine.Children.Add(new TextBlock
        {
            Text = $"连接地址：{connectionText}",
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            FontSize = 12
        });
        var copyAddrBtn = new Button
        {
            Content = "复制", Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(6, 0, 6, 0), FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0), Background = System.Windows.Media.Brushes.Gray
        };
        copyAddrBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(connectionText); } catch { /* 剪贴板偶发被占用，忽略即可，不阻断界面 */ }
        };
        addressLine.Children.Add(copyAddrBtn);
        infoPanel.Children.Add(addressLine);

        Grid.SetColumn(infoPanel, 1);
        grid.Children.Add(infoPanel);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (isRunning)
        {
            var consoleBtn = new Button { Content = "控制台", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 6, 0) };
            consoleBtn.Click += (_, _) => OpenConsole(instance);
            btnPanel.Children.Add(consoleBtn);

            var stopBtn = new Button
            {
                Content = "■ 停止", Style = (Style)FindResource("PrimaryButton"),
                Background = System.Windows.Media.Brushes.IndianRed, Margin = new Thickness(0, 0, 6, 0)
            };
            stopBtn.Click += async (_, _) => await StopInstanceAsync(instance);
            btnPanel.Children.Add(stopBtn);
        }
        else
        {
            var startBtn = new Button { Content = "▶ 启动", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 6, 0) };
            startBtn.Click += (_, _) => StartInstance(instance);
            btnPanel.Children.Add(startBtn);

            var deleteBtn = new Button
            {
                Content = "删除", Style = (Style)FindResource("PrimaryButton"),
                Background = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 6, 0)
            };
            deleteBtn.Click += (_, _) => DeleteInstance(instance);
            btnPanel.Children.Add(deleteBtn);
        }

        // "更多"按钮：承载导入导出/自定义图标/设为默认/重新覆盖安装这几个不常用的操作，
        // 避免每张卡片挤上 6-7 个常驻按钮导致列表过宽、误触风险变高。
        var moreBtn = new Button { Content = "⋯", Style = (Style)FindResource("PrimaryButton"), Background = System.Windows.Media.Brushes.Gray, Padding = new Thickness(10, 8, 10, 8) };
        moreBtn.Click += (_, _) => ShowInstanceMoreMenu(moreBtn, instance);
        btnPanel.Children.Add(moreBtn);

        Grid.SetColumn(btnPanel, 2);
        grid.Children.Add(btnPanel);

        card.Child = grid;
        return card;
    }

    private static Border BuildIconPlaceholder(string displayName)
    {
        var ch = string.IsNullOrEmpty(displayName) ? "?" : displayName.Substring(0, 1).ToUpperInvariant();
        return new Border
        {
            Child = new TextBlock
            {
                Text = ch, FontSize = 16, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>
    /// "更多"菜单：导入/导出/自定义图标/设为(取消)默认/重新覆盖安装。用 ContextMenu 而不是
    /// 常驻按钮组，因为这几项都是低频操作，塞进主按钮行会让每张卡片的操作区过宽。
    /// </summary>
    private void ShowInstanceMoreMenu(Button anchor, ServerInstance instance)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };

        // 修复"没有自定义服务器名称功能"：创建向导里虽然可以填初始名称，但创建完之后
        // 没有任何地方能改名字——之前"更多"菜单只有导入/导出/图标/默认/重装这几项，缺重命名。
        var renameItem = new MenuItem { Header = "重命名..." };
        renameItem.Click += (_, _) => RenameInstance(instance);
        menu.Items.Add(renameItem);

        var exportItem = new MenuItem { Header = "导出存档..." };
        exportItem.Click += (_, _) => ExportInstance(instance);
        menu.Items.Add(exportItem);

        var importItem = new MenuItem { Header = "导入存档 (覆盖此实例)..." };
        importItem.Click += (_, _) => ImportInstance(instance);
        menu.Items.Add(importItem);

        menu.Items.Add(new Separator());

        var iconItem = new MenuItem { Header = "设置自定义图标..." };
        iconItem.Click += (_, _) => SetInstanceIcon(instance);
        menu.Items.Add(iconItem);

        if (!string.IsNullOrEmpty(instance.IconPath))
        {
            var clearIconItem = new MenuItem { Header = "清除自定义图标" };
            clearIconItem.Click += (_, _) => ClearInstanceIcon(instance);
            menu.Items.Add(clearIconItem);
        }

        menu.Items.Add(new Separator());

        var defaultItem = new MenuItem { Header = instance.IsDefault ? "取消默认服务器" : "设为默认服务器" };
        defaultItem.Click += (_, _) => ToggleDefaultInstance(instance);
        menu.Items.Add(defaultItem);

        menu.Items.Add(new Separator());

        var propertiesItem = new MenuItem { Header = "服务器设置..." };
        propertiesItem.Click += (_, _) => OpenServerProperties(instance);
        menu.Items.Add(propertiesItem);

        menu.Items.Add(new Separator());

        var selectJavaItem = new MenuItem { Header = "选择 Java..." };
        selectJavaItem.Click += (_, _) => SelectInstanceJava(instance);
        menu.Items.Add(selectJavaItem);

        var reinstallItem = new MenuItem { Header = "重新覆盖安装核心..." };
        reinstallItem.Click += (_, _) => ReinstallInstanceCore(instance);
        menu.Items.Add(reinstallItem);

        menu.Items.Add(new Separator());

        // "清除服务器数据"：跟卡片上的"删除"按钮不同，这个会连同磁盘上的服务端文件夹
        // 一起永久删除（世界存档/插件/配置/日志全部清掉），是这批操作里最具破坏性的一个，
        // 单独放在最后一组、并且加上警示色文字，跟前面的常规操作明显区分开。
        var clearDataItem = new MenuItem
        {
            Header = "清除服务器数据（永久删除文件）...",
            Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush")
        };
        clearDataItem.Click += (_, _) => ClearServerData(instance);
        menu.Items.Add(clearDataItem);

        menu.IsOpen = true;
    }

    /// <summary>
    /// 让用户从「设置」页登记的 Java 列表里，为这个已创建好的服务器实例单独选一个 Java，
    /// 不需要重新走一遍"重新覆盖安装核心"的整个下载/安装流程——这是纯粹的"换 Java"操作。
    /// </summary>
    /// <summary>
    /// 打开"服务器设置"窗口，图形化编辑 server.properties 常用字段。修法对应用户截图诉求：
    /// 让服务器可以自定义介绍(motd)/人数(max-players)/正版验证(online-mode)/允许飞行
    /// (allow-flight)等，字段清单照搬截图里另一个面板工具的分组。
    /// </summary>
    private void OpenServerProperties(ServerInstance instance)
    {
        var window = new ServerPropertiesWindow(instance.Directory) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void SelectInstanceJava(ServerInstance instance)
    {
        var dlg = new SelectJavaDialog(_owner.ConfigService, instance.JavaId, instance.JavaPath);
        if (OverlayDialogService.ShowModal(dlg) != true) return;

        instance.JavaId = dlg.SelectedJavaId;
        instance.JavaPath = dlg.SelectedJavaPath ?? instance.JavaPath;
        _owner.ServerInstanceService.Update(instance);
        MessageBoxDialog.ShowSuccess($"「{instance.DisplayName}」的 Java 已更新。", "已保存");
    }

    private void CreateServer_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new CreateServerWindow(_owner) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        RefreshInstanceList(); // 无论用户是否成功创建/取消，都刷新一遍，成功创建时列表会多一条
    }

    private void StartInstance(ServerInstance instance)
    {
        try
        {
            _owner.ServerProcessManager.Start(instance);
            RefreshInstanceList();
            OpenConsole(instance);
            MaybeShowNetworkGuide();
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"启动失败：\n{ex.Message}");
        }
    }

    /// <summary>
    /// 服务器列表页顶部"🌐 如何联机/内网穿透教程"按钮：手动打开教程窗口，不判断
    /// ShowServerNetworkGuideOnStart（那个开关只管"启动服务器后是否自动弹"，手动点击这个
    /// 按钮时用户明确想看教程，不应该因为之前勾选过"不再自动提示"就被挡住）。
    /// 打开后如果用户又勾选了"不再自动提示"，一样写回配置——跟自动弹出那次行为一致。
    /// </summary>
    private void OpenNetworkGuide_Click(object sender, RoutedEventArgs e)
    {
        var guide = new ServerNetworkGuideWindow { Owner = Window.GetWindow(this) };
        guide.ShowDialog();
        if (guide.DontShowAgain)
        {
            _owner.ConfigService.Config.ShowServerNetworkGuideOnStart = false;
            _owner.ConfigService.Save();
        }
    }

    /// <summary>
    /// 服务器启动成功后，按用户设置弹出"如何开放外网访问"教程（内网穿透/路由器映射/云服务器）。
    /// 用 AppConfig.ShowServerNetworkGuideOnStart 控制是否弹出，用户在教程窗口里勾选
    /// "不再提示"后这里写回配置并保存，之后启动服务器就不会再自动弹出。现在还有另一个独立入口
    /// （服务器列表页顶部的"🌐 如何联机/内网穿透教程"按钮，见 OpenNetworkGuide_Click），
    /// 那个入口不受这个开关影响，随时可以手动打开。
    /// </summary>
    private void MaybeShowNetworkGuide()
    {
        var cfg = _owner.ConfigService.Config;
        if (!cfg.ShowServerNetworkGuideOnStart) return;

        var guide = new ServerNetworkGuideWindow { Owner = Window.GetWindow(this) };
        guide.ShowDialog();
        if (guide.DontShowAgain)
        {
            cfg.ShowServerNetworkGuideOnStart = false;
            _owner.ConfigService.Save();
        }
    }

    private async Task StopInstanceAsync(ServerInstance instance)
    {
        var confirm = MessageBoxDialog.ShowConfirm(
            $"确定要停止服务器「{instance.DisplayName}」吗？会先尝试正常关服保存世界。",
            "确认停止");
        if (!confirm) return;

        try
        {
            await _owner.ServerProcessManager.StopAsync(instance.Id);
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"停止失败：\n{ex.Message}");
        }
        finally
        {
            RefreshInstanceList();
        }
    }

    private void OpenConsole(ServerInstance instance)
    {
        var console = new ServerConsoleWindow(_owner, instance) { Owner = Window.GetWindow(this) };
        console.Closed += (_, _) => RefreshInstanceList(); // 控制台关闭时（可能服务器也被停止了）刷新状态
        console.Show();
    }

    private void DeleteInstance(ServerInstance instance)
    {
        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            MessageBoxDialog.ShowInfo("服务器正在运行，请先停止后再删除。");
            return;
        }

        var confirm = MessageBoxDialog.ShowConfirm(
            $"确定要删除服务器「{instance.DisplayName}」吗？\n\n" +
            "这里只会移除启动器里的记录，不会删除磁盘上的服务端文件夹。\n" +
            "如果需要连同存档/配置一起删除，请使用「更多」菜单里的「清除服务器数据」。",
            "确认删除");
        if (!confirm) return;

        try
        {
            _owner.ServerInstanceService.Remove(instance.Id, deleteFiles: false);
            RefreshInstanceList();
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"删除失败：\n{ex.Message}");
        }
    }

    /// <summary>
    /// "清除服务器数据"：跟上面 DeleteInstance（只移除启动器记录，保留磁盘文件）不同，
    /// 这里连同服务端目录本身一起永久删除——对应 ServerInstanceService.Remove 早就支持的
    /// deleteFiles=true 分支，之前只是没有任何 UI 入口调用它。
    /// 需要正在运行时先拒绝（避免删除一个还在写文件的目录导致中间状态错乱），
    /// 并要求用户在 ClearServerDataWindow 里原样输入服务器名称才会真正执行。
    /// </summary>
    private void ClearServerData(ServerInstance instance)
    {
        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            MessageBoxDialog.ShowInfo("服务器正在运行，请先停止后再清除数据。");
            return;
        }

        var dlg = new ClearServerDataDialog(instance.DisplayName, instance.Directory);
        if (OverlayDialogService.ShowModal(dlg) != true || !dlg.Confirmed) return;

        try
        {
            _owner.ServerInstanceService.Remove(instance.Id, deleteFiles: true);
            RefreshInstanceList();
            MessageBoxDialog.ShowSuccess($"「{instance.DisplayName}」的数据已永久清除。", "已清除");
        }
        catch (Exception ex)
        {
            // Remove() 内部：实例记录已经先被移除并 Save() 了，只是删文件夹这一步失败
            // （常见于文件被其它程序占用），这里如实告知"记录已删、文件没删掉"，
            // 不让用户误以为"点了没反应"又对着同一个目录重复操作。
            ErrorPresenter.ShowFriendlyError(
                $"启动器记录已移除，但删除磁盘文件失败（可能是文件正被占用）：\n{ex.Message}\n\n" +
                $"可以手动删除目录：\n{instance.Directory}",
                $"[清除服务器数据] {ex}", "清除失败");
            RefreshInstanceList();
        }
    }

    // ============================================================
    // 存档导入导出 / 自定义图标 / 默认服务器 / 重新覆盖安装
    // ============================================================

    private readonly ServerInstanceTransferService _transferService = new();

    private void ExportInstance(ServerInstance instance)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出服务器存档",
            Filter = "XCL2 服务器存档 (*.xcl2server)|*.xcl2server",
            FileName = $"{instance.DisplayName}.xcl2server"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _transferService.Export(instance, dlg.FileName);
            MessageBoxDialog.ShowSuccess("导出完成。", "存档导出");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导出失败：\n{ex.Message}");
        }
    }

    /// <summary>
    /// 导入存档并覆盖到指定实例的目录下（合并覆盖策略，见 ServerInstanceTransferService.Import 注释）。
    /// 不改动实例的加载器/内存等配置字段——如果包内 manifest 有配置信息，只用于提示，不做静默覆盖，
    /// 避免用户没注意到的情况下配置被意外改掉。
    /// </summary>
    private void ImportInstance(ServerInstance instance)
    {
        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            MessageBoxDialog.ShowInfo("服务器正在运行，请先停止后再导入。");
            return;
        }

        var dlg = new OpenFileDialog { Title = "导入服务器存档", Filter = "XCL2 服务器存档 (*.xcl2server)|*.xcl2server|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;

        var confirm = MessageBoxDialog.ShowConfirm(
            $"即将把存档内容合并覆盖到「{instance.DisplayName}」的服务器目录：\n{instance.Directory}\n\n" +
            "同名文件会被存档内容覆盖，其余现有文件保留。此操作不可撤销，建议先自行备份重要数据。",
            "确认导入");
        if (!confirm) return;

        try
        {
            var manifest = _transferService.Import(dlg.FileName, instance.Directory);
            var extra = manifest != null
                ? $"\n\n存档内附带的原始配置：{manifest.CoreType} · MC {manifest.McVersion}，内存 {manifest.MinMemoryMb}~{manifest.MaxMemoryMb}MB。\n" +
                  "如果需要按这份配置更新当前实例，请手动在创建/编辑向导里调整。"
                : "";
            MessageBoxDialog.ShowSuccess("导入完成。" + extra, "存档导入");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导入失败：\n{ex.Message}");
        }
    }

    private void SetInstanceIcon(ServerInstance instance)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择服务器图标",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var savedPath = _owner.ServerInstanceService.SetIcon(instance.Id, dlg.FileName);
            instance.IconPath = savedPath;
            _owner.ServerInstanceService.Update(instance);
            RefreshInstanceList();
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"设置图标失败：\n{ex.Message}");
        }
    }

    private void ClearInstanceIcon(ServerInstance instance)
    {
        _owner.ServerInstanceService.ClearIcon(instance.IconPath);
        instance.IconPath = null;
        _owner.ServerInstanceService.Update(instance);
        RefreshInstanceList();
    }

    /// <summary>
    /// 重命名一个已有服务器实例。名称冲突校验复用与创建向导一致的规则（同名不允许），
    /// 但要排除"实例改名改回自己原来的名字"这种不该算冲突的情况——否则用户点开重命名框
    /// 不改内容直接确定都会被拒绝。改名只影响 DisplayName，不影响 Id/目录/日志文件命名
    /// （那些都是用 Id，与 DisplayName 完全解耦，见 ServerInstance.Id 上的注释）。
    /// </summary>
    private void RenameInstance(ServerInstance instance)
    {
        var dlg = new RenameInstanceDialog(
            instance.DisplayName,
            isNameTaken: candidate => candidate != instance.DisplayName &&
                _owner.ServerInstanceService.Instances.Any(i => i.Id != instance.Id && i.DisplayName == candidate),
            title: "重命名服务器");

        if (OverlayDialogService.ShowModal(dlg) != true) return;

        instance.DisplayName = dlg.NewName;
        _owner.ServerInstanceService.Update(instance);
        RefreshInstanceList();
    }

    private void ToggleDefaultInstance(ServerInstance instance)
    {
        _owner.ServerInstanceService.SetDefault(instance.IsDefault ? null : instance.Id);
        RefreshInstanceList();
    }

    /// <summary>
    /// 重新覆盖安装核心：复用创建向导 CreateServerWindow 的"选加载器/版本 -> 下载 -> (若需要)本地安装"
    /// 整套流程，而不是在这里重写一份下载 UI。窗口以 reinstallTarget 模式打开时不写入新的
    /// ServerInstance 记录，而是把下载/安装结果直接落到传入实例的 Directory 下，完成后更新
    /// 该实例现有记录的 CoreType/McVersion/LaunchTarget 等字段（而不是新增一条记录）。
    /// </summary>
    private void ReinstallInstanceCore(ServerInstance instance)
    {
        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            MessageBoxDialog.ShowInfo("服务器正在运行，请先停止后再重新安装核心。");
            return;
        }

        var confirm = MessageBoxDialog.ShowConfirm(
            $"即将为「{instance.DisplayName}」重新下载并覆盖安装服务端核心文件。\n" +
            "world 存档等其余文件不会被清空，但核心 jar/启动脚本会被替换。是否继续？",
            "确认重新安装");
        if (!confirm) return;

        var wizard = new CreateServerWindow(_owner, reinstallTarget: instance) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        RefreshInstanceList();
    }

    // ===== 服务端资源下载面板：统一承载 插件/资源包/数据包/光影包/Mod 五个分类，逻辑结构
    // 对齐 DownloadCenterPage 材质包分类那一套（SwitchResourceCategory/RunResourceSearchAsync/
    // ViewResourceVersions_Click），区别是：1) 目标目录来自"选中的服务器实例"而不是
    // ".minecraft 版本目录"；2) 用 _serverResourceType 记录当前选中的资源类型，所有搜索/下载
    // 都按这个类型分派，不再像之前那样写死 ModrinthResourceType.ResourcePack。 =====

    private CurseForgeService GetResourceCurseForge() => _resourceCurseForge ??= new CurseForgeService(_resourceCurseForgeKeyService);
    private ModSearchService GetResourceModSearch() => _resourceModSearch ??= new ModSearchService(_resourceModrinth, GetResourceCurseForge());

    private void ServerResourceDebounce(Action action)
    {
        if (!_initialized || _serverResourceDebounceTimer == null) return;
        _pendingServerResourceDebouncedAction = action;
        _serverResourceDebounceTimer.Stop();
        _serverResourceDebounceTimer.Start();
    }

    /// <summary>第一次切到"插件下载"/"资源包"分类时：填充服务器下拉框 + 触发一次默认搜索（浏览热门）。
    /// 之后再切回来不重复搜索，跟 DownloadCenterPage 的 _lastLoadedResourceType 是同一个思路——
    /// 用 _serverResourcePanelLoaded 记录"是否已经初始化过一次"。</summary>
    private void InitServerResourcePanel()
    {
        RefreshServerTargetCombo();

        if (_serverResourcePanelLoaded) return;
        _serverResourcePanelLoaded = true;
        _ = RunServerResourceSearchAsync();
    }

    /// <summary>资源类型切换(插件/资源包/数据包/光影包/Mod)：更新标题文案、决定"目标世界文件夹"
    /// 输入框是否显示(只有数据包需要)，并重新触发一次搜索——不同类型对应完全不同的 Modrinth
    /// project_type/CurseForge classId，结果集不能沿用上一个类型的搜索结果。</summary>
    private void ServerResourceType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ModrinthResourceType>(tag, out var type)) return;

        _serverResourceType = type;
        ServerDataPackWorldPanel.Visibility = type == ModrinthResourceType.DataPack ? Visibility.Visible : Visibility.Collapsed;
        RefreshServerResourceTitle();
        _ = RunServerResourceSearchAsync();
    }

    private void RefreshServerResourceTitle()
    {
        ServerResourcePanelTitle.Text = _serverResourceType switch
        {
            ModrinthResourceType.Plugin => "服务端插件下载",
            ModrinthResourceType.ResourcePack => "服务端资源包下载",
            ModrinthResourceType.DataPack => "服务端数据包下载",
            ModrinthResourceType.Shader => "服务端光影包下载",
            ModrinthResourceType.Mod => "服务端 Mod 下载",
            _ => "服务端资源下载"
        };
    }

    /// <summary>刷新"下载到服务器"下拉框的候选列表。每次切到这个分类都刷一次（而不是只在
    /// 首次加载时刷），这样如果用户是先创建了服务器、再回来点资源分类，下拉框也能看到新建的。
    /// 尽量保留用户原来选中的那一项（按 Id 比较，服务器改名不受影响）。</summary>
    private void RefreshServerTargetCombo()
    {
        var previouslySelected = (ServerTargetCombo.SelectedItem as ServerInstance)?.Id;
        var instances = _owner.ServerInstanceService.Instances;

        ServerTargetCombo.ItemsSource = instances;
        if (instances.Count == 0)
        {
            ServerResourceNoTargetHint.Visibility = Visibility.Visible;
            ServerResourceListBox.Visibility = Visibility.Collapsed;
            return;
        }

        ServerResourceNoTargetHint.Visibility = Visibility.Collapsed;
        ServerResourceListBox.Visibility = Visibility.Visible;

        var matched = previouslySelected != null ? instances.FirstOrDefault(i => i.Id == previouslySelected) : null;
        ServerTargetCombo.SelectedItem = matched ?? instances.FirstOrDefault(i => i.IsDefault) ?? instances[0];
        RefreshServerResourceModLoaderVisibility();
    }

    /// <summary>选服务器实例变化时：Mod 分类只有目标是 Fabric/Forge/NeoForge(modded 服务端)才有意义，
    /// 纯 Vanilla/Paper/Purpur 服务端选中时禁用"Mod"这个类型按钮，避免用户装了一堆装不上的 mod jar。
    /// 插件(Plugin)不受此限制——插件是 Bukkit/Spigot/Paper 生态的东西，跟 Mod 完全是两回事。</summary>
    private void ServerTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        RefreshServerResourceModLoaderVisibility();
    }

    private void RefreshServerResourceModLoaderVisibility()
    {
        var target = ServerTargetCombo.SelectedItem as ServerInstance;
        var isModdedCore = target != null && target.CoreType is ServerCoreType.Fabric or ServerCoreType.Forge or ServerCoreType.NeoForge;
        SrvResMod.IsEnabled = isModdedCore;
        if (!isModdedCore && SrvResMod.IsChecked == true)
        {
            // 当前选的服务器不支持 Mod（比如切到了 Paper），自动退回"插件"分类，
            // 而不是让用户停留在一个"选了也下载不了"的死状态上。
            SrvResPlugin.IsChecked = true;
        }
    }

    private async void ServerResourceSearch_Click(object sender, RoutedEventArgs e) => await RunServerResourceSearchAsync(showEmptyHint: true);

    /// <summary>"重置条件"：清空名称/游戏版本输入框，重新按当前类型搜索一次（浏览热门资源）。
    /// 不清空"目标世界文件夹"（数据包场景），也不重置类型/来源/目标服务器——这几个决定的是
    /// "这次操作的对象是什么"，跟"筛选条件"是两回事，重置筛选不应该连带清空这些选择。</summary>
    private void ServerResourceFilterReset_Click(object sender, RoutedEventArgs e)
    {
        ServerResourceSearchBox.Text = "";
        ServerResourceGameVersionBox.Text = "";
        _serverResourceDebounceTimer?.Stop();
        _ = RunServerResourceSearchAsync(showEmptyHint: true);
    }

    /// <summary>版本快捷选择条点击，同 DownloadCenterPage.ResourceVersionChip_Click，见那边注释。</summary>
    private void ServerResourceVersionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        ServerResourceGameVersionBox.Text = btn.Tag as string ?? "";
    }

    private void ServerResourceFilter_Changed(object sender, RoutedEventArgs e) => ServerResourceDebounce(() => _ = RunServerResourceSearchAsync());

    private void ServerResourceSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _serverResourceSource = ((ServerResourceSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "modrinth" => ModSource.Modrinth,
            "curseforge" => ModSource.CurseForge,
            _ => ModSource.Combined
        };
        _ = RunServerResourceSearchAsync();
    }

    private async Task RunServerResourceSearchAsync(bool showEmptyHint = false)
    {
        var seq = ++_serverResourceSearchSeq;
        try
        {
            var keyword = ServerResourceSearchBox.Text?.Trim() ?? "";
            var gameVersion = ServerResourceGameVersionBox.Text?.Trim();
            // 插件/Mod 可以按目标服务器的核心类型进一步过滤，减少搜到装不上的结果：
            // - Mod 场景：核心类型就是加载器(Fabric/Forge/NeoForge)，直接传。
            // - 插件场景：只有 Paper 核心才有意义传"paper"分类facet(Modrinth 插件分类用的是
            //   paper/spigot/purpur/bukkit/folia 这套词汇，Vanilla/Fabric/Forge/NeoForge
            //   对插件搜索毫无意义，传了反而会把结果过滤没——所以这里只在核心是 Paper 时才传，
            //   其余核心类型(包括还没做插件下载的 Vanilla)不加这个 facet，交给用户自己筛选)。
            var targetCore = (ServerTargetCombo.SelectedItem as ServerInstance)?.CoreType;
            var loaderFilter = _serverResourceType == ModrinthResourceType.Mod
                ? targetCore?.ToString()
                : (_serverResourceType == ModrinthResourceType.Plugin && targetCore == ServerCoreType.Paper
                    ? "paper" : null);
            var outcome = await GetResourceModSearch().SearchResourcesAsync(_serverResourceSource, _serverResourceType,
                keyword, string.IsNullOrEmpty(gameVersion) ? null : gameVersion, modLoader: loaderFilter);

            if (seq != _serverResourceSearchSeq) return; // 期间已有更新的搜索发出，这次结果已过时，丢弃

            _serverResources.Clear();
            var showIcons = _owner.ConfigService.Config.ShowModIcons;
            foreach (var item in outcome.Items)
            {
                item.ShowIcon = showIcons;
                _serverResources.Add(item);
            }

            if (showEmptyHint)
            {
                if (outcome.Items.Count == 0 && outcome.Warnings.Count == 0)
                    MessageBoxDialog.ShowInfo("没有找到匹配的资源，换个关键词试试。");
                else if (outcome.Warnings.Count > 0)
                    MessageBoxDialog.ShowWarning(string.Join("\n", outcome.Warnings), "部分来源搜索失败");
            }
        }
        catch (Exception ex)
        {
            if (seq != _serverResourceSearchSeq) return;
            if (showEmptyHint)
                ErrorPresenter.ShowFriendlyError("搜索失败，可能是网络连接问题，请检查网络后重试。", $"[搜索失败] {ex}", "搜索失败");
        }
    }

    private async void ServerResourceListItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not UnifiedResourceItem item) return;
        await OpenServerResourceDetailAsync(item);
    }

    /// <summary>点击资源条目：整页跳转到 ModDetailPage，取代原来已废弃的"卡片内联展开"样式
    /// (旧版 ToggleServerResourceExpandAsync)——跟客户端 DownloadCenterPage.OpenResourceDetailAsync
    /// 是同一套交互，区别只在于：1) 下载目标目录是 ServerTargetCombo 选中的服务器实例目录，
    /// 不是 .minecraft 文件夹；2) 数据包场景下"目标世界文件夹"来自 ServerDataPackWorldBox 这个
    /// 单一输入框（不是像客户端那样扫描现有存档列表），所以 SaveNames 只填一项；3) 服务端资源
    /// 暂不接入收藏功能，onFavoriteToggle 传 null。</summary>
    private async Task OpenServerResourceDetailAsync(UnifiedResourceItem item)
    {
        if (ServerTargetCombo.SelectedItem is not ServerInstance targetInstance)
        {
            MessageBoxDialog.ShowInfo("请先在上面选择一个要下载到的服务器（还没有服务器的话，先去「服务器列表」创建一个）。");
            return;
        }

        item.IsDataPack = _serverResourceType == ModrinthResourceType.DataPack;
        if (item.IsDataPack)
        {
            var worldName = string.IsNullOrWhiteSpace(ServerDataPackWorldBox.Text) ? "world" : ServerDataPackWorldBox.Text.Trim();
            item.SaveNames.Clear();
            item.SaveNames.Add(worldName);
            item.SelectedSaveName = worldName;
        }

        var sourceUrl = item.RawItem switch
        {
            ModrinthSearchHit h => $"https://modrinth.com/{ServerResourceProjectTypeSlug(_serverResourceType)}/{h.Slug}",
            CurseForgeMod m => m.Links?.WebsiteUrl,
            _ => null
        };

        var detail = new ModDetailPage(
            ModDetailPage.DetailMode.DirectDownload,
            item.Title, item.Description, item.IconUrl, item.Author, item.Downloads,
            item.SourceLabel, sourceUrl, item, item.IsFavorite,
            onFavoriteToggle: null,
            onBack: HideServerResourceDetail,
            onDownload: entry => DownloadServerResourceInlineAsync(item, entry, targetInstance),
            isDataPack: item.IsDataPack,
            saveNames: item.SaveNames);

        ShowServerResourceDetail(detail);

        if (item.VersionsLoaded)
        {
            detail.SetFlatEntries(item.Versions);
            detail.ShowGroups(item.Groups);
            return;
        }

        detail.ShowLoading();
        await LoadServerResourceVersionsAsync(item, ServerResourceGameVersionBox.Text?.Trim());
        detail.SetFlatEntries(item.Versions);
        detail.ShowGroups(item.Groups);
    }

    private void ShowServerResourceDetail(ModDetailPage page)
    {
        DetailHost.Content = page;
        DetailHost.Visibility = Visibility.Visible;
    }

    private void HideServerResourceDetail()
    {
        DetailHost.Visibility = Visibility.Collapsed;
        DetailHost.Content = null;
    }

    private static string ServerResourceProjectTypeSlug(ModrinthResourceType type) => type switch
    {
        ModrinthResourceType.ResourcePack => "resourcepack",
        ModrinthResourceType.Shader => "shader",
        ModrinthResourceType.DataPack => "mod",
        ModrinthResourceType.Plugin => "plugin",
        ModrinthResourceType.Mod => "mod",
        _ => "mod"
    };

    /// <summary>拉取一个服务端资源条目的版本列表并分组，从原来 ToggleServerResourceExpandAsync
    /// 里抽出来的共享逻辑，供整页详情复用，跟 DownloadCenterPage.LoadResourceVersionsAsync
    /// 是同一个思路。</summary>
    private async Task LoadServerResourceVersionsAsync(UnifiedResourceItem item, string? gameVersion)
    {
        item.IsLoadingVersions = true;
        try
        {
            item.Versions.Clear();

            if (item.Source == ModSource.Modrinth)
            {
                var versions = await _resourceModrinth.GetVersionsAsync(item.SourceId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                foreach (var v in versions) item.Versions.Add(new InlineVersionEntry(v));
            }
            else if (item.Source == ModSource.CurseForge)
            {
                // ModrinthResourceType 和 CurseForgeResourceKind 是两套独立的枚举(历史遗留，
                // 前者还多一个 Mod 值)；Mod 类型的服务端下载目前只支持 Modrinth 来源
                // (CurseForgeResourceKind 没有 Mod 这个成员)。
                if (_serverResourceType == ModrinthResourceType.Mod)
                {
                    MessageBoxDialog.ShowInfo("Mod 类型目前仅支持从 Modrinth 下载，请把上方来源切换为「仅 Modrinth」或「综合」。");
                    item.HasNoResults = true;
                    return;
                }

                var modId = int.Parse(item.SourceId);
                var files = await GetResourceCurseForge().GetFilesAsync(modId, string.IsNullOrEmpty(gameVersion) ? null : gameVersion);
                foreach (var f in files) item.Versions.Add(new InlineVersionEntry(f));
            }
            item.HasNoResults = item.Versions.Count == 0;

            item.Groups.Clear();
            foreach (var g in ModVersionGrouping.Group(item.Versions)) item.Groups.Add(g);
            if (item.Groups.Count > 0) item.Groups[0].IsExpanded = true;

            item.VersionsLoaded = true;
        }
        catch (CurseForgeKeyMissingException ex)
        {
            MessageBoxDialog.ShowInfo(ex.Message, "未配置 Key");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            item.IsLoadingVersions = false;
        }
    }

    /// <summary>ModDetailPage 里点"下载"回调，目标目录是 OpenServerResourceDetailAsync 打开
    /// 详情页那一刻就已经确定好的服务器实例（避免用户在详情页停留期间切换了 ServerTargetCombo
    /// 导致下载目录和预期不一致）。</summary>
    private async Task DownloadServerResourceInlineAsync(UnifiedResourceItem item, InlineVersionEntry entry, ServerInstance targetInstance)
    {
        if (item.IsDataPack && string.IsNullOrEmpty(item.SelectedSaveName))
        {
            MessageBoxDialog.ShowInfo("请先填写要安装到哪个存档（数据包必须放进具体存档才会生效）。");
            return;
        }

        var progressWin = new ProgressDialog($"正在下载 {entry.Name} ...");
        progressWin.Show();
        try
        {
            var progress = new Progress<string>(msg => progressWin.Progress.Report(new ProgressInfo("下载中", 0, 1, msg)));
            string path;
            if (entry.Source == ModSource.Modrinth)
            {
                path = await _resourceModrinth.DownloadResourceAsync(targetInstance.Directory, _serverResourceType,
                    (ModrinthVersion)entry.RawVersion, progress, item.IsDataPack ? item.SelectedSaveName : null);
            }
            else
            {
                var kind = _serverResourceType switch
                {
                    ModrinthResourceType.Plugin => CurseForgeResourceKind.Plugin,
                    ModrinthResourceType.ResourcePack => CurseForgeResourceKind.ResourcePack,
                    ModrinthResourceType.Shader => CurseForgeResourceKind.Shader,
                    ModrinthResourceType.DataPack => CurseForgeResourceKind.DataPack,
                    _ => throw new ArgumentOutOfRangeException()
                };
                path = await GetResourceCurseForge().DownloadResourceAsync(targetInstance.Directory, kind,
                    (CurseForgeFile)entry.RawVersion, progress, item.IsDataPack ? item.SelectedSaveName : null);
            }
            _owner.EnsureVisibleForDialog();
            MessageBoxDialog.ShowSuccess($"下载完成：\n{path}");
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[下载失败] {ex}", "下载失败");
        }
        finally
        {
            progressWin.Close();
        }
    }
}
