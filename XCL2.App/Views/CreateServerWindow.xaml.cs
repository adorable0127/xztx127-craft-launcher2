using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 创建服务器向导：一键完成"选加载器/版本 -> 下载核心(若尚未下载) -> (Forge/NeoForge时)本地安装
/// -> 写入 ServerInstance 配置并保存"的完整流程，对应清单里的"一键开服"（这里是"一键创建"，
/// 真正的"开服"启动动作由服务器列表页的"启动"按钮触发，创建和启动分成两步，
/// 避免用户还没检查完内存/CPU设置就已经跑起来了）。
/// </summary>
public partial class CreateServerWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly ServerCoreDownloadService _coreService = new();
    private readonly JavaService _javaService = new();

    private ServerCoreType _selectedCoreType = ServerCoreType.Vanilla;
    private readonly ObservableCollection<string> _mcVersions = new();
    private readonly ObservableCollection<ServerCoreBuild> _buildVersions = new();

    /// <summary>
    /// 根据当前选中的 MC 版本估算出的 Java 主版本要求，用于下载/安装阶段自动匹配 Java，
    /// 而不是像修复前那样固定套用客户端全局 PreferredJavaMajorVersion。
    /// Vanilla 的真实值要等下载完 version.json 才能确定（此处只是给用户看的预估提示），
    /// 实际下载流程里最终采信 ServerCoreDownloadResult.RequiredJavaMajorVersion。
    /// </summary>
    private int _estimatedRequiredJava = 21;

    public ServerInstance? CreatedInstance { get; private set; }

    /// <summary>
    /// 非 null 时表示"重新覆盖安装"模式：由 ServerManagerPage 的「重新覆盖安装核心」入口打开，
    /// 复用本向导选加载器/版本/下载/安装这套流程，但最终不新增一条 ServerInstance 记录，
    /// 而是更新这个已有实例的核心相关字段（CoreType/McVersion/LaunchTarget等），
    /// 名称/安装目录/Java路径在这个模式下锁定为该实例原有值，不允许用户改动——
    /// 覆盖安装的语义是"换核心"，不是"顺便改名/挪目录"，这两件事应该用各自专门的入口做。
    /// </summary>
    private readonly ServerInstance? _reinstallTarget;

    /// <summary>用户在"Java 列表"下拉框里直接选中的那一条(而不是自动检测/下载得到的)，
    /// 创建/覆盖安装完成后会写进 ServerInstance.JavaId。选"（不指定）"或者后续走了自动检测/
    /// 下载流程改掉了 JavaPathBox 内容时，这里会被清空，跟随旧的路径逻辑。</summary>
    private string? _selectedJavaId;

    /// <summary>
    /// 是否已经跑完构造函数里的 InitializeComponent()。
    ///
    /// 崩溃根因：与 DownloadCenterPage 完全相同的时序问题——XAML 里左侧分类栏默认选中的
    /// RadioButton 会在 InitializeComponent() 解析阶段同步触发 Checked 事件，但此时
    /// CorePanel/InstancesPanel/PlaceholderPanel/PlaceholderTitle 等自动生成字段还没赋值，
    /// Category_Checked 一读就是 NullReferenceException。用同一套 _initialized 短路方案。
    /// </summary>
    private bool _initialized;

    public CreateServerWindow(MainWindow owner, ServerInstance? reinstallTarget = null)
    {
        _owner = owner;
        _reinstallTarget = reinstallTarget;
        InitializeComponent();

        McVersionCombo.ItemsSource = _mcVersions;
        BuildVersionCombo.ItemsSource = _buildVersions;
        BuildVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);

        RefreshJavaListCombo();

        if (reinstallTarget != null)
        {
            // 修复编译错误 CS0103："当前上下文中不存在名称 Title"。
            // OverlayDialogControl 继承自 UserControl，没有 Window.Title 这个属性——
            // 这是上一轮批量迁移脚本的又一处漏网：脚本只清理了 XAML 里声明式的
            // Title="..." 属性，没有处理构造函数里这种运行时的 Title 赋值。
            // Overlay 弹窗没有系统标题栏，原来 Window.Title 显示的内容改成弹窗内容
            // 顶部那行大字标题（XAML 里现在叫 HeaderTitleText），效果是一样的——
            // "重新覆盖安装核心 - xxx" 依然会显示给用户看，只是从标题栏搬到了正文顶部。
            HeaderTitleText.Text = $"重新覆盖安装核心 - {reinstallTarget.DisplayName}";
            NameBox.Text = reinstallTarget.DisplayName;
            NameBox.IsEnabled = false; // 覆盖安装模式下名称/目录锁定，见上面 _reinstallTarget 注释
            TargetDirBox.Text = reinstallTarget.Directory;
            ServerPortBox.Text = ServerPortService.ReadPort(reinstallTarget).ToString();
            ServerPortBox.IsEnabled = false;
            TargetDirBox.IsEnabled = false;
            BrowseTargetDirBtn.IsEnabled = false;
            JavaPathBox.Text = reinstallTarget.JavaPath ?? "";
            if (!string.IsNullOrEmpty(reinstallTarget.JavaId))
            {
                _selectedJavaId = reinstallTarget.JavaId;
                var match = JavaListCombo.Items.Cast<JavaListItem>().FirstOrDefault(i => i.Entry?.Id == reinstallTarget.JavaId);
                if (match != null) JavaListCombo.SelectedItem = match;
            }
            MinMemoryBox.Text = reinstallTarget.MinMemoryMb.ToString();
            MaxMemoryBox.Text = reinstallTarget.MaxMemoryMb.ToString();
            CpuLimitBox.Text = reinstallTarget.CpuLimitPercent?.ToString() ?? "";
            DiskLimitBox.Text = reinstallTarget.DiskLimitMb?.ToString() ?? "";
            CreateBtn.Content = Loc.T("Str_Cs_Starting_Overwrite_Install", "开始覆盖安装");
        }
        else
        {
            TargetDirBox.Text = Path.Combine(App.DataDir, "servers", "new-server");
            NameBox.Text = Loc.T("Str_Cs_My_Server", "我的服务器");

            // 重要修复：这里之前固定用客户端全局的 PreferredJavaMajorVersion 去找 Java，
            // 跟服务器实际要装的 MC 版本/核心完全没关系——用户还没选版本，这个"预填"本来就
            // 只能算个初始猜测，且极容易和真正要求的版本不一致，是本轮修复的
            // "class file version 69.0...up to 65.0" 报错的根因之一。
            // 这里先不做版本限定的预填（留空，交给用户点"自动检测"或手动填），
            // 真正会用到的 Java 版本要求在 McVersionCombo/BuildVersionCombo 选定后才能确定，
            // 见 UpdateJavaRequirementHint() 和 Create_Click 里的 ResolveJavaForDownloadAsync。
            var detected = _javaService.FindJava(_owner.ConfigService.Config.JavaPath, configService: _owner.ConfigService);
            JavaPathBox.Text = detected ?? "";
        }

        _initialized = true;
        _ = LoadMcVersionsAsync();
    }

    private async void CoreType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // InitializeComponent() 解析阶段的默认选中触发，跳过
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ServerCoreType>(tag, out var coreType)) return;
        _selectedCoreType = coreType;

        // Vanilla 没有 build 号可选；Spigot 走 BuildTools 本地编译，同样没有 build 号概念
        // （只需要选 MC 版本，编译结果就是那个版本对应的 Spigot），两者都不展示 build 面板。
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

        await LoadMcVersionsAsync();
        UpdateJavaRequirementHint();
    }

    private async Task LoadMcVersionsAsync()
    {
        _mcVersions.Clear();
        _buildVersions.Clear();
        McVersionCombo.IsEnabled = false;
        CreateBtn.IsEnabled = false;

        try
        {
            List<string> versions = _selectedCoreType switch
            {
                ServerCoreType.Vanilla => await _coreService.GetVanillaVersionsAsync(includeSnapshots: false),
                ServerCoreType.Paper => await _coreService.GetPaperVersionsAsync(),
                ServerCoreType.Fabric => await _coreService.GetFabricMcVersionsAsync(),
                ServerCoreType.Forge => await _coreService.GetForgeVersionsAsync(),
                ServerCoreType.NeoForge => await _coreService.GetNeoForgeVersionsAsync(),
                ServerCoreType.Purpur => await _coreService.GetPurpurVersionsAsync(),
                ServerCoreType.Folia => await _coreService.GetFoliaVersionsAsync(),
                ServerCoreType.Velocity => await _coreService.GetVelocityVersionsAsync(),
                ServerCoreType.Waterfall => await _coreService.GetWaterfallVersionsAsync(),
                // Spigot 没有官方版本列表 API（BuildTools 支持"几乎任何官方发布过的 MC 版本"），
                // 复用 Vanilla 的版本清单最省事、也最准确——凡是 Mojang 发布过 release 的版本，
                // BuildTools 原则上都能编译。
                ServerCoreType.Spigot => await _coreService.GetVanillaVersionsAsync(includeSnapshots: false),
                _ => new List<string>()
            };
            foreach (var v in versions) _mcVersions.Add(v);
            if (_mcVersions.Count > 0) McVersionCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Version_List_This_Is_", "获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            McVersionCombo.IsEnabled = true;
            CreateBtn.IsEnabled = true;
        }
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _buildVersions.Clear();
        UpdateJavaRequirementHint();
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        if (_selectedCoreType is ServerCoreType.Vanilla or ServerCoreType.NeoForge or ServerCoreType.Spigot) return;

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
            BuildVersionCombo.SelectedItem = builds.FirstOrDefault(b => b.IsRecommended) ?? builds.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Fetch_The_Build_List_This_Is_Us", "获取构建版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。"), $"[获取构建版本列表失败] {ex}", "获取构建版本列表失败");
        }
    }

    /// <summary>
    /// 根据当前选中的核心类型/MC版本，估算这个服务器需要的 Java 主版本号，并在界面上提示用户。
    /// 只是"提示"，不强制修改用户已经填的 JavaPathBox；真正的自动匹配/下载发生在 Create_Click
    /// 点击创建时（见 ResolveOrDownloadJavaAsync）。
    /// </summary>
    private void UpdateJavaRequirementHint()
    {
        if (McVersionCombo.SelectedItem is not string mcVersion)
        {
            JavaRequirementHintText.Visibility = Visibility.Collapsed;
            return;
        }

        _estimatedRequiredJava = _selectedCoreType == ServerCoreType.NeoForge
            ? 21 // NeoForge 版本号和 MC 版本号不是同一个格式，这里选中项就是 NeoForge 版本号本身，
                 // 精确估算交给下载阶段的 NeoForgeVersionToMcVersion 换算，这里只给一个保守提示。
            : ServerJavaRequirement.EstimateMajorVersionForMcVersion(mcVersion);

        JavaRequirementHintText.Text = $"提示：MC {mcVersion} 预计需要 Java {_estimatedRequiredJava}，" +
            "创建时会自动检测/下载匹配版本（无需手动填写）。";
        JavaRequirementHintText.Visibility = Visibility.Visible;
    }

    /// <summary>用「设置」页登记的 Java 列表填充下拉框，第一项固定是"（不指定，自动检测/下载）"。</summary>
    private void RefreshJavaListCombo()
    {
        JavaListCombo.SelectionChanged -= JavaListCombo_SelectionChanged;
        JavaListCombo.Items.Clear();
        JavaListCombo.Items.Add(new JavaListItem { Entry = null });
        foreach (var j in _owner.ConfigService.Config.InstalledJavas) JavaListCombo.Items.Add(new JavaListItem { Entry = j });
        JavaListCombo.SelectedIndex = 0;
        JavaListCombo.SelectionChanged += JavaListCombo_SelectionChanged;
    }

    /// <summary>用户在 Java 列表下拉框里选了具体一条：直接把路径填进 JavaPathBox 并记下它的 Id；
    /// 选回"（不指定）"则清空 _selectedJavaId，恢复走自动检测/下载那套旧逻辑。</summary>
    private void JavaListCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var picked = (JavaListCombo.SelectedItem as JavaListItem)?.Entry;
        _selectedJavaId = picked?.Id;
        if (picked != null) JavaPathBox.Text = picked.JavawPath;
    }

    private void BrowseTargetDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择服务器安装位置" };
        if (Directory.Exists(TargetDirBox.Text)) dialog.InitialDirectory = TargetDirBox.Text;

        // 修复编译错误 CS1503：OpenFolderDialog/OpenFileDialog.ShowDialog(Window? owner)
        // 要的是一个真正的 Window，而这个类现在继承 OverlayDialogControl（UserControl），
        // this 不再能隐式转成 Window——这是上一轮批量迁移脚本的又一处漏网：脚本只处理了
        // "new XxxWindow(...) { Owner = ... }" 这一种写法，没处理
        // "原生文件/文件夹选择框.ShowDialog(this)" 这种把 this 当 owner 传的用法。
        // 改用 Window.GetWindow(this)：顺着可视化树往上找，找到的正是承载这个 Overlay
        // 弹窗的 MainWindow，效果跟以前"以自己为 owner"是一致的（原生对话框弹出时
        // 仍然正确地叠在主窗口之上、随主窗口一起最小化）。
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TargetDirBox.Text = dialog.FolderName;
    }

    private void AutoDetectJava_Click(object sender, RoutedEventArgs e)
    {
        var found = _javaService.FindJava(null, configService: _owner.ConfigService);
        if (found == null)
        {
            MessageBoxDialog.ShowWarning(Loc.T("Str_Cs_No_Usable_Java_Was_Found_Download_Or_Con", "没有检测到可用的 Java。请在「设置」页先下载/配置 Java，或者手动填写路径。"), Loc.T("Str_Cs_Java_Not_Found", "未找到 Java"));
            return;
        }
        JavaPathBox.Text = found;
        _selectedJavaId = null;
    }

    /// <summary>
    /// 确保拿到一个主版本号等于 requiredMajor 的可用 Java：
    /// 1) 先看 JavaPathBox 里现有路径是不是已经就是这个版本（用 JavaService 实测探测，不猜文件夹名）；
    /// 2) 不是的话，在已知 Java 里找一个匹配版本的（JavaService.FindJava 传入 requiredMajor 会做实测校验）；
    /// 3) 都没有就自动下载一个便携版 Java（Adoptium，失败回退 BMCLAPI），装到 xcl2/runtime 下。
    /// 这是本轮修复的核心：任何时候都不会再把"客户端全局偏好版本"或"用户随手填的路径"
    /// 未经校验就传给 Forge/NeoForge 安装器或服务端启动命令。
    /// </summary>
    private static async Task<(string Path, string? JavaId)> ResolveOrDownloadJavaAsync(
        JavaService javaService, MainWindow owner, string? currentPath, string? selectedJavaId, bool pickedFromList,
        int requiredMajor, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
        {
            var matched = javaService.FindJava(currentPath, requiredMajor, owner.ConfigService);
            if (matched != null)
            {
                // 用户在 Java 列表里选的这一条本来就满足版本要求，原样保留其 Id；
                // 否则(手填/自动检测出来的路径)清空，走旧的路径逻辑。
                var keepId = string.Equals(matched, currentPath, StringComparison.OrdinalIgnoreCase) && pickedFromList;
                return (matched, keepId ? selectedJavaId : null);
            }
        }

        var found = javaService.FindJava(null, requiredMajor, owner.ConfigService);
        if (found != null)
        {
            var id = owner.ConfigService.RegisterJava(found, requiredMajor, "Manual").Id;
            owner.ConfigService.Save();
            return (found, id);
        }

        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_A_Matching_Java_Runtime", "下载匹配的 Java 运行时"), 0, 1, $"Java {requiredMajor}"));
        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var downloaded = await javaService.DownloadJavaAsync(
            new JavaDownloadRequest(requiredMajor, arch, JavaInstallMode.Portable), progress, ct);

        // 新下载的 Java 自动登记进全局 Java 列表，方便以后其它服务器/客户端版本也能直接选用它。
        var downloadedId = owner.ConfigService.RegisterJava(downloaded, requiredMajor, "Downloaded").Id;
        owner.ConfigService.Save();
        return (downloaded, downloadedId);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Enter_A_Server_Name", "请填写服务器名称。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }
        // 重装模式下名称就是目标实例自身的名称，必然会命中同名检查，跳过；
        // 新建模式下才需要防止用户新建一个和现有实例撞名的服务器。
        if (_reinstallTarget == null && _owner.ServerInstanceService.Instances.Any(i => i.DisplayName == name))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_A_Server_With_That_Name_Already_Exists_P", "已经有一个同名的服务器了，请换一个名称。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }
        if (McVersionCombo.SelectedItem is not string mcVersion)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_A_Minecraft_Version", "请选择 Minecraft 版本。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetDirBox.Text))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_An_Install_Location", "请选择安装位置。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }
        if (!int.TryParse(ServerPortBox.Text.Trim(), out var serverPort) || !ServerPortService.IsValid(serverPort))
        {
            MessageBoxDialog.ShowInfo("服务器端口必须是 1–65535 之间的整数。", "端口无效");
            return;
        }
        if (_reinstallTarget == null && _owner.ServerInstanceService.Instances.Any(i =>
            string.Equals(Path.GetFullPath(i.Directory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(TargetDirBox.Text).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBoxDialog.ShowInfo("该目录已有服务器，请为新服务器选择独立目录。", "安装目录冲突");
            return;
        }
        if (!int.TryParse(MinMemoryBox.Text, out var minMem) || minMem <= 0 ||
            !int.TryParse(MaxMemoryBox.Text, out var maxMem) || maxMem <= 0 || maxMem < minMem)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Invalid_Memory_Settings_Minimum_And_Maxi", "内存设置不合法：请确认最小/最大内存都是正整数，且最大不小于最小。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        int? cpuLimit = null;
        if (!string.IsNullOrWhiteSpace(CpuLimitBox.Text))
        {
            if (!int.TryParse(CpuLimitBox.Text, out var cpu) || cpu is < 1 or > 100)
            {
                MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_The_Cpu_Limit_Must_Be_A_Whole_Number_Bet", "CPU 上限必须是 1-100 之间的整数，或留空表示不限制。"), Loc.T("Str_Status_Tip", "提示"));
                return;
            }
            cpuLimit = cpu;
        }

        int? diskLimit = null;
        if (!string.IsNullOrWhiteSpace(DiskLimitBox.Text))
        {
            if (!int.TryParse(DiskLimitBox.Text, out var disk) || disk <= 0)
            {
                MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_The_Disk_Limit_Must_Be_A_Positive_Whole_", "磁盘上限必须是正整数（单位 MB），或留空表示不限制。"), Loc.T("Str_Status_Tip", "提示"));
                return;
            }
            diskLimit = disk;
        }

        // Vanilla/Paper/Fabric 必须要有 Java 才能后续启动；Forge/NeoForge 安装阶段同样需要 Java 跑安装器。
        if (string.IsNullOrWhiteSpace(JavaPathBox.Text) || !File.Exists(JavaPathBox.Text))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Provide_A_Valid_Java_Path_Use_Aut", "请提供一个有效的 Java 路径（点击「自动检测」或手动填写）。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var buildVersion = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion;
        var req = new ServerCoreDownloadRequest
        {
            CoreType = _selectedCoreType,
            McVersion = mcVersion,
            TargetDir = TargetDirBox.Text
        };
        if (_selectedCoreType == ServerCoreType.Forge) req.InstallerVersion = buildVersion;
        else if (_selectedCoreType == ServerCoreType.NeoForge) req.InstallerVersion = mcVersion;
        else req.BuildOrLoaderVersion = buildVersion;

        // 整条流水线（下载核心 → 匹配/下载 Java → 编译/安装 → 登记实例）交给统一下载队列，
        // 在标题栏右上角的下载抽屉里显示进度，向导窗口立即关闭，不再阻塞界面。
        var reinstallTarget = _reinstallTarget;
        var coreType = _selectedCoreType;
        var owner = _owner;
        var coreService = _coreService;
        var javaService = _javaService;
        var javaPathText = JavaPathBox.Text;
        var selectedJavaId = _selectedJavaId;
        var javaPickedFromList = (JavaListCombo.SelectedItem as JavaListItem)?.Entry != null;

        var queueName = reinstallTarget != null ? $"覆盖安装核心 {name}" : $"创建服务器 {name}";
        if (DownloadQueueService.Instance.Items.Any(i => i.Name == queueName &&
                i.Status is DownloadQueueItemStatus.Downloading or DownloadQueueItemStatus.Paused))
        {
            MessageBoxDialog.ShowInfo("这个任务已经在下载列表中了，请等它完成。", Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        DownloadQueueService.Instance.StartNew(queueName, async (item, ct) =>
        {
            var progress = item.CreateProgress();
            var textProgress = new Progress<string>(line => item.StatusText = line);

            var result = await coreService.DownloadAsync(req, progress, ct);

            // 下载完成后核心的真实 Java 要求才确定，据此匹配/自动下载 Java；
            // Forge/NeoForge 安装器也要用这个匹配的 Java 来跑。
            var (resolvedJavaPath, resolvedJavaId) = await ResolveOrDownloadJavaAsync(
                javaService, owner, javaPathText, selectedJavaId, javaPickedFromList,
                result.RequiredJavaMajorVersion, progress, ct);

            string launchTarget;
            bool launchTargetIsScript;

            if (result.RequiresBuild)
            {
                // Spigot：编译耗时数分钟，状态文字里提示一下，避免用户以为卡住了。
                item.StatusText = Loc.T("Str_Cs_Building_Spigot_With_Buildtools", "正在用 BuildTools 编译 Spigot");
                try
                {
                    var jarPath = await coreService.RunSpigotBuildToolsAsync(
                        result.DownloadedFilePath, req.TargetDir, resolvedJavaPath, mcVersion, textProgress, ct);
                    launchTarget = Path.GetFileName(jarPath);
                    launchTargetIsScript = false;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Git"))
                {
                    // BuildTools 缺 Git 是最常见的失败原因，单独给个友好提示。
                    await owner.Dispatcher.InvokeAsync(() => ErrorPresenter.ShowFriendlyError(
                        "编译 Spigot 需要本机已安装 Git，请先安装 Git（https://git-scm.com/downloads）后重试。",
                        $"[Spigot BuildTools] {ex}", "缺少 Git"));
                    throw;
                }
            }
            else if (result.RequiresInstall)
            {
                item.StatusText = Loc.T("Str_Cs_Running_The_Installer", "正在运行安装器");
                var scriptOrDir = await coreService.RunForgeInstallerAsync(
                    result.DownloadedFilePath, req.TargetDir, resolvedJavaPath, textProgress, ct);

                if (File.Exists(scriptOrDir) &&
                    (scriptOrDir.EndsWith("run.bat", StringComparison.OrdinalIgnoreCase) ||
                     scriptOrDir.EndsWith("run.sh", StringComparison.OrdinalIgnoreCase)))
                {
                    launchTarget = Path.GetFileName(scriptOrDir);
                    launchTargetIsScript = true;
                }
                else
                {
                    // 极老的 Forge 版本安装完是直接一个 jar，没有 run 脚本：找一个看起来是服务端本体的 jar。
                    var fallbackJar = Directory.GetFiles(req.TargetDir, "*.jar")
                        .Select(Path.GetFileName)
                        .FirstOrDefault(f => f != null && !f.Contains("installer", StringComparison.OrdinalIgnoreCase));
                    if (fallbackJar == null)
                        throw new InvalidOperationException(
                            "安装完成，但没有找到可识别的启动文件（run.bat/run.sh 或服务端 jar）。请手动检查安装目录。");
                    launchTarget = fallbackJar;
                    launchTargetIsScript = false;
                }
            }
            else
            {
                launchTarget = result.ServerJarFileName ?? "server.jar";
                launchTargetIsScript = false;
            }

            item.ProgressPercent = 100;

            if (reinstallTarget != null)
            {
                // 重装模式：只更新已有实例的核心相关字段，不新增记录、不改名称/目录/内存等。
                await owner.Dispatcher.InvokeAsync(() =>
                {
                    reinstallTarget.CoreType = coreType;
                    reinstallTarget.McVersion = mcVersion;
                    reinstallTarget.LaunchTarget = launchTarget;
                    reinstallTarget.LaunchTargetIsScript = launchTargetIsScript;
                    reinstallTarget.JavaPath = resolvedJavaPath;
                    reinstallTarget.JavaId = resolvedJavaId;
                    reinstallTarget.RequiredJavaMajorVersion = result.RequiredJavaMajorVersion;
                    owner.ServerInstanceService.Update(reinstallTarget);

                    owner.EnsureVisibleForDialog();
                    MessageBoxDialog.ShowInfo($"服务器「{name}」的核心已重新安装完成！", "成功");
                });
                return;
            }

            await owner.Dispatcher.InvokeAsync(() =>
            {
                var instance = new ServerInstance
                {
                    DisplayName = name,
                    Directory = req.TargetDir,
                    CoreType = coreType,
                    McVersion = mcVersion,
                    LaunchTarget = launchTarget,
                    LaunchTargetIsScript = launchTargetIsScript,
                    JavaPath = resolvedJavaPath,
                    JavaId = resolvedJavaId,
                    RequiredJavaMajorVersion = result.RequiredJavaMajorVersion,
                    MinMemoryMb = minMem,
                    MaxMemoryMb = maxMem,
                    CpuLimitPercent = cpuLimit,
                    DiskLimitMb = diskLimit,
                    ServerPort = serverPort,
                    AutoAvoidPort = true
                };

                // 合并旧版“创建时指定端口”和新版“创建后再次确认端口”两套能力：
                // 先从用户填写的端口开始自动避让，写入 server.properties；随后仍保留安装完成后的确认提示。
                var actualPort = ServerPortService.FindAvailablePort(serverPort, instance,
                    owner.ServerInstanceService.Instances, owner.ServerProcessManager.Processes);
                ServerPortService.WritePort(instance, actualPort);
                instance.ServerPort = actualPort;

                owner.ServerInstanceService.Add(instance);
                ServerPostInstallPrompt.AskConfigurePort(owner, instance, $"服务器「{name}」创建完成！");
            });
        }, supportsPause: false);

        CloseWith(true);
    }
}
