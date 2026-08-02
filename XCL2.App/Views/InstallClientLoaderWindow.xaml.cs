using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 安装客户端加载器(Fabric/Forge/NeoForge)向导。
///
/// 对应 HANDOFF-ROUND3.md 里 #1"无法下载游戏加载器"未完成的 UI 接入部分：之前
/// ClientLoaderInstallService 写好了但完全是个孤立的类，界面上没有任何入口能调用它。
/// 这个窗口就是那个入口，UI 结构照抄 CreateServerWindow 已经验证过的
/// "加载器 RadioButton + MC版本 Combo + 构建版本 Combo 三级联动"模式，只是把
/// 服务端下载(ServerCoreDownloadService) 换成客户端加载器安装(ClientLoaderInstallService)。
///
/// 由 VersionSelectPage 的"安装新版本"按钮打开。装完之后不需要额外把新版本"注册"到哪里——
/// FolderService.ScanVersions 是直接扫描 .minecraft/versions/ 目录的，装完的版本文件夹
/// 天然就会被扫描到，这里只需要在关闭时通知 VersionSelectPage 刷新一次列表。
/// </summary>
public partial class InstallClientLoaderWindow : Window
{
    private readonly MainWindow _owner;
    private readonly ClientLoaderInstallService _loaderService;
    private readonly JavaService _javaService = new();
    /// <summary>原版直装用：跟 ClientLoaderInstallService 内部的 _vanillaDownloader 是同一套
    /// DownloadService，这里单独持有一份是因为 ClientLoaderInstallService 没有把它暴露出来，
    /// 复用同一个 AppConfig 构造，保证多线程下载/限速设置跟加载器安装时一致。</summary>
    private readonly DownloadService _vanillaDownloader;
    private VersionManifestRoot? _versionManifest;

    private ServerCoreType _selectedLoaderType = ServerCoreType.Vanilla;
    private readonly ObservableCollection<string> _mcVersions = new();
    private readonly ObservableCollection<ServerCoreBuild> _buildVersions = new();

    /// <summary>是否已经跑完构造函数里的 InitializeComponent()，跟 CreateServerWindow 同样的
    /// 时序问题：默认选中的 RadioButton 会在 InitializeComponent() 阶段同步触发 Checked 事件，
    /// 此时自动生成的字段还没赋值，直接读会 NullReferenceException。</summary>
    private bool _initialized;

    /// <summary>安装完成后新版本的 id（供调用方刷新已安装版本列表/直接选中它）。</summary>
    public string? InstalledVersionId { get; private set; }

    /// <summary>预选的加载器类型/MC 版本，来自"下载中心"的加载器筛选行（用户已经在那边选好了
    /// 加载器类型+具体 MC 版本，这里不需要用户重新选一遍，跳过这两步直接定位到"选构建版本"这一步）。
    /// 为 null 表示走原来"从头选择"的完整流程（比如从「版本选择」页的"➕ 安装新版本"按钮进来）。</summary>
    private readonly ServerCoreType? _preselectedLoaderType;
    private readonly string? _preselectedMcVersion;

    public InstallClientLoaderWindow(MainWindow owner, ServerCoreType? preselectLoaderType = null, string? preselectMcVersion = null)
    {
        _owner = owner;
        _preselectedLoaderType = preselectLoaderType;
        _preselectedMcVersion = preselectMcVersion;
        // 用完整 AppConfig 的构造：加载器安装内部也要下载原版底座的 libraries/assets，
        // 应该跟"下载中心-游戏版本"面板一样吃到多线程下载/限速设置，而不是永远单线程。
        _loaderService = new ClientLoaderInstallService(owner.ConfigService.Config);
        _vanillaDownloader = DownloadService.CreateFromConfig(owner.ConfigService.Config);
        InitializeComponent();

        // 窗口关闭时释放 _loaderService/_vanillaDownloader（进而释放它们内部可能持有的智能限速
        // 后台采样任务）。不释放不会导致下载出错，但会让那个采样循环一直跑到进程退出，
        // 每次打开又关闭这个窗口就多留一个永不退出的后台任务，属于资源泄漏，顺手修掉。
        Closed += (_, _) => { _loaderService.Dispose(); _vanillaDownloader.Dispose(); };

        McVersionCombo.ItemsSource = _mcVersions;
        BuildVersionCombo.ItemsSource = _buildVersions;
        BuildVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);

        var detected = _javaService.FindJava(_owner.ConfigService.Config.JavaPath, configService: _owner.ConfigService);
        JavaPathBox.Text = detected ?? "";

        // 应用预选的加载器类型：XAML 默认选中的是 Fabric（LoaderFabric IsChecked="True"），
        // 如果调用方要的是 Forge/NeoForge，这里手动切换一下对应 RadioButton 的选中状态——
        // 这会走正常的 Checked 事件（此时 _initialized 还是 false，事件会被跳过），
        // 所以选中状态本身要在下面手动设置 _selectedLoaderType 字段来保持一致。
        if (_preselectedLoaderType is { } loaderType && loaderType != ServerCoreType.Vanilla)
        {
            _selectedLoaderType = loaderType;
            switch (loaderType)
            {
                case ServerCoreType.Fabric: LoaderFabric.IsChecked = true; break;
                case ServerCoreType.Forge: LoaderForge.IsChecked = true; break;
                case ServerCoreType.NeoForge: LoaderNeoForge.IsChecked = true; break;
                case ServerCoreType.Quilt: LoaderQuilt.IsChecked = true; break;
            }
            BuildVersionPanel.Visibility = loaderType == ServerCoreType.NeoForge ? Visibility.Collapsed : Visibility.Visible;
            BuildVersionLabel.Text = loaderType == ServerCoreType.Forge ? "安装器版本" : "构建版本";
        }
        else
        {
            // 原版：没有独立的加载器/构建版本这一级，直接隐藏该面板。
            BuildVersionPanel.Visibility = Visibility.Collapsed;
        }
        // Quilt 跟 Fabric 一样不需要本地 Java；Fabric 对应可选的"Fabric API"，Quilt 对应可选的
        // "QSL"（见 ClientLoaderInstallService.InstallQuiltClientAsync 的注释），两者分别只在
        // 各自加载器类型下显示。
        FabricNoJavaHintText.Visibility = _selectedLoaderType == ServerCoreType.Fabric ? Visibility.Visible : Visibility.Collapsed;
        InstallFabricApiCheck.Visibility = _selectedLoaderType == ServerCoreType.Fabric ? Visibility.Visible : Visibility.Collapsed;
        InstallQslCheck.Visibility = _selectedLoaderType == ServerCoreType.Quilt ? Visibility.Visible : Visibility.Collapsed;
        // 原版/Fabric/Quilt 都不需要本地 Java 来完成"安装"这一步，只有 Forge/NeoForge 需要
        // （游戏本身运行仍会在启动时自动匹配/下载 Java，跟这里的"安装期 Java"是两回事）。
        JavaPathPanel.Visibility = _selectedLoaderType is ServerCoreType.Vanilla or ServerCoreType.Quilt
            ? Visibility.Collapsed : Visibility.Visible;

        _initialized = true;
        _ = LoadMcVersionsAsync();
    }

    private async void LoaderType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // InitializeComponent() 解析阶段的默认选中触发，跳过
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ServerCoreType>(tag, out var loaderType)) return;
        _selectedLoaderType = loaderType;

        // NeoForge 的"版本"下拉框里选的就是完整版本号本身，没有独立的"构建版本"这一级；
        // 原版同样没有"构建版本"这一级（原版一个 MC 版本就对应唯一一个 client jar），
        // 跟 CreateServerWindow 里对 NeoForge 的处理是同一个道理，Vanilla 直接照搬。
        // Quilt 跟 Fabric 一样只有"Loader 版本"这一级构建版本选择，没有 NeoForge/Vanilla 那种
        // "没有独立构建版本"的情况，所以只在 NeoForge/Vanilla 时隐藏该面板，Quilt 沿用默认可见。
        BuildVersionPanel.Visibility = loaderType is ServerCoreType.NeoForge or ServerCoreType.Vanilla
            ? Visibility.Collapsed : Visibility.Visible;
        BuildVersionLabel.Text = loaderType switch
        {
            ServerCoreType.Fabric => "Loader 版本",
            ServerCoreType.Quilt => "Loader 版本",
            ServerCoreType.Forge => "安装器版本",
            _ => "构建版本"
        };

        // Fabric/Quilt 都走各自 Meta API 的现成 profile json，不需要本地跑安装器，不强制要求 Java；
        // Forge/NeoForge 必须本地跑安装器，缺 Java 无法继续。
        FabricNoJavaHintText.Visibility = loaderType == ServerCoreType.Fabric ? Visibility.Visible : Visibility.Collapsed;
        // Fabric API 只对 Fabric 有意义，QSL 只对 Quilt 有意义（Forge/NeoForge 生态没有这类概念），
        // 切换加载器类型时同步显示/隐藏各自对应的可选安装项。
        InstallFabricApiCheck.Visibility = loaderType == ServerCoreType.Fabric ? Visibility.Visible : Visibility.Collapsed;
        InstallQslCheck.Visibility = loaderType == ServerCoreType.Quilt ? Visibility.Visible : Visibility.Collapsed;
        JavaPathPanel.Visibility = loaderType is ServerCoreType.Vanilla or ServerCoreType.Quilt
            ? Visibility.Collapsed : Visibility.Visible;

        await LoadMcVersionsAsync();
        UpdateJavaRequirementHint();
    }

    private async Task LoadMcVersionsAsync()
    {
        _mcVersions.Clear();
        _buildVersions.Clear();
        McVersionCombo.IsEnabled = false;
        InstallBtn.IsEnabled = false;

        try
        {
            List<string> versions;
            if (_selectedLoaderType == ServerCoreType.Vanilla)
            {
                // 原版：直接用官方/BMCL 的 version manifest，只列正式版（同向导/下载中心其它
                // 地方"默认只看正式版"的口径保持一致），把清单缓存下来供 Install_Click 里
                // 反查具体 VersionManifestEntry 用。
                _versionManifest = await _vanillaDownloader.GetVersionManifestAsync();
                versions = _versionManifest.Versions
                    .Where(v => v.GetCategory() == VersionCategory.Release)
                    .Select(v => v.Id)
                    .ToList();
            }
            else
            {
                versions = _selectedLoaderType switch
                {
                    ServerCoreType.Fabric => await _loaderService.GetFabricMcVersionsAsync(),
                    ServerCoreType.Forge => await _loaderService.GetForgeVersionsAsync(),
                    ServerCoreType.NeoForge => await _loaderService.GetNeoForgeVersionsAsync(),
                    ServerCoreType.Quilt => await _loaderService.GetQuiltMcVersionsAsync(),
                    _ => new List<string>()
                };
            }
            foreach (var v in versions) _mcVersions.Add(v);
            // 有预选 MC 版本时优先选它（下载中心场景：用户已经在版本列表里点了具体版本的
            // "下载安装"），选不中（比如这个加载器根本没适配这个版本）才退回选第一项。
            if (_preselectedMcVersion != null && _mcVersions.Contains(_preselectedMcVersion))
                McVersionCombo.SelectedItem = _preselectedMcVersion;
            else if (_mcVersions.Count > 0) McVersionCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            McVersionCombo.IsEnabled = true;
            InstallBtn.IsEnabled = true;
        }
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _buildVersions.Clear();
        UpdateJavaRequirementHint();
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        if (_selectedLoaderType is ServerCoreType.NeoForge or ServerCoreType.Vanilla) return; // 没有独立的第二级下拉框

        try
        {
            List<ServerCoreBuild> builds = _selectedLoaderType switch
            {
                ServerCoreType.Fabric => await _loaderService.GetFabricLoaderVersionsAsync(),
                ServerCoreType.Forge => await _loaderService.GetForgeInstallerVersionsAsync(mcVersion),
                ServerCoreType.Quilt => await _loaderService.GetQuiltLoaderVersionsAsync(),
                _ => new List<ServerCoreBuild>()
            };
            foreach (var b in builds) _buildVersions.Add(b);
            BuildVersionCombo.SelectedItem = builds.FirstOrDefault(b => b.IsRecommended) ?? builds.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取构建版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取构建版本列表失败] {ex}", "获取构建版本列表失败");
        }
    }

    /// <summary>
    /// 提示这个版本大概需要的 Java 主版本号。Forge/NeoForge 复用服务端已经在用的
    /// ServerJavaRequirement 估算表（安装器本身需要用这个版本的 Java 来跑，逻辑跟服务端安装
    /// Forge/NeoForge 核心时完全一致）；Fabric 客户端安装本身不需要本地 Java（见
    /// FabricNoJavaHintText），这里不显示 Java 要求提示，真正的 Java 版本要求要等装完之后
    /// 由 LauncherService.GetRequiredJavaMajorVersion 在启动时读 version json 决定。
    /// </summary>
    private void UpdateJavaRequirementHint()
    {
        // Quilt 跟 Fabric 一样不需要本地 Java 来完成"安装"这一步（见类头/InstallQuiltClientAsync 注释），
        // 一并跳过这个提示。
        if (_selectedLoaderType is ServerCoreType.Fabric or ServerCoreType.Quilt
            || McVersionCombo.SelectedItem is not string mcVersion)
        {
            JavaRequirementHintText.Visibility = Visibility.Collapsed;
            return;
        }

        var estimated = ServerJavaRequirement.EstimateMajorVersionForMcVersion(mcVersion);
        JavaRequirementHintText.Text = $"提示：MC {mcVersion} 预计需要 Java {estimated} 来运行安装器" +
            "（点击「自动检测」尝试匹配，找不到时需要先去「设置」页下载对应版本的 Java）。";
        JavaRequirementHintText.Visibility = Visibility.Visible;
    }

    private void AutoDetectJava_Click(object sender, RoutedEventArgs e)
    {
        int? preferMajor = null;
        if (_selectedLoaderType is not (ServerCoreType.Fabric or ServerCoreType.Quilt) && McVersionCombo.SelectedItem is string mcVersion)
            preferMajor = ServerJavaRequirement.EstimateMajorVersionForMcVersion(mcVersion);

        var found = _javaService.FindJava(null, preferMajor, _owner.ConfigService);
        if (found == null)
        {
            MessageBox.Show("没有检测到可用的 Java。请在「设置」页先下载/配置 Java，或者手动填写路径。",
                "未找到 Java", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        JavaPathBox.Text = found;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (McVersionCombo.SelectedItem is not string mcVersion)
        {
            MessageBox.Show("请选择 Minecraft 版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var buildVersion = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion;
        if (_selectedLoaderType is not (ServerCoreType.NeoForge or ServerCoreType.Vanilla) && string.IsNullOrEmpty(buildVersion))
        {
            MessageBox.Show("请选择构建/加载器版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 原版走 Mojang 官方直装，本地不需要跑任何安装器；Fabric/Quilt 客户端安装同样不需要本地 Java
        // （见 FabricNoJavaHintText / InstallQuiltClientAsync 注释）；只有 Forge/NeoForge 必须
        // 本地跑安装器，需要有效 Java。
        if (_selectedLoaderType is not (ServerCoreType.Fabric or ServerCoreType.Quilt or ServerCoreType.Vanilla) &&
            (string.IsNullOrWhiteSpace(JavaPathBox.Text) || !File.Exists(JavaPathBox.Text)))
        {
            MessageBox.Show("请提供一个有效的 Java 路径（点击「自动检测」或手动填写），Forge/NeoForge 安装器需要本地 Java 才能运行。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var minecraftDir = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)?.Path;
        if (string.IsNullOrEmpty(minecraftDir))
        {
            MessageBox.Show("没有找到当前选中的 .minecraft 文件夹，请先在「版本管理」页选择/添加一个文件夹。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InstallBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        var progress = new Progress<ProgressInfo>(p =>
        {
            ProgressStageText.Text = p.Stage;
            ProgressDetailText.Text = p.CurrentFile;
            ProgressBarCtl.Maximum = Math.Max(p.Total, 1);
            ProgressBarCtl.Value = p.Done;
        });

        try
        {
            string versionId;
            if (_selectedLoaderType == ServerCoreType.Vanilla)
            {
                // 原版：从缓存的 manifest 里反查完整条目（含 url/sha1），直接调用
                // DownloadService.InstallVersionAsync，跟"下载中心-游戏版本"面板走的是
                // 完全同一套下载路径，行为、限速、多线程设置都一致。
                var entry = _versionManifest?.Versions.FirstOrDefault(v => v.Id == mcVersion);
                if (entry == null)
                {
                    MessageBox.Show("找不到该版本的清单信息，请重新打开这个窗口再试一次。",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                await _vanillaDownloader.InstallVersionAsync(minecraftDir, entry, progress);
                versionId = entry.Id;
            }
            else if (_selectedLoaderType == ServerCoreType.Fabric)
            {
                versionId = await _loaderService.InstallFabricClientAsync(
                    minecraftDir, mcVersion, buildVersion!, progress,
                    installFabricApi: InstallFabricApiCheck.IsChecked == true);
            }
            else if (_selectedLoaderType == ServerCoreType.Quilt)
            {
                // Quilt 走独立的 InstallQuiltClientAsync，不能落到下面 Forge/NeoForge 那个
                // else 分支——那个分支调的是 InstallForgeOrNeoForgeClientAsync，会对 Quilt
                // 走本地跑安装器那一套逻辑，Quilt 根本没有安装器 jar，会直接报错。
                versionId = await _loaderService.InstallQuiltClientAsync(
                    minecraftDir, mcVersion, buildVersion!, progress,
                    installQsl: InstallQslCheck.IsChecked == true);
            }
            else
            {
                var fullVersion = _selectedLoaderType == ServerCoreType.NeoForge ? mcVersion : buildVersion!;
                versionId = await _loaderService.InstallForgeOrNeoForgeClientAsync(
                    minecraftDir, _selectedLoaderType, fullVersion, JavaPathBox.Text, progress);
            }

            InstalledVersionId = versionId;
            MessageBox.Show($"版本「{versionId}」安装完成！\n可以在「已安装版本」列表里选中它。",
                "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("安装失败，可能是网络连接问题、下载源暂时不可用，或安装文件已损坏，请检查网络后重试。", $"[安装失败] {ex}", "安装失败");
        }
        finally
        {
            InstallBtn.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }
}
