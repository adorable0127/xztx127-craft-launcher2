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
public partial class CreateServerWindow : Window
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
            Title = $"重新覆盖安装核心 - {reinstallTarget.DisplayName}";
            NameBox.Text = reinstallTarget.DisplayName;
            NameBox.IsEnabled = false; // 覆盖安装模式下名称/目录锁定，见上面 _reinstallTarget 注释
            TargetDirBox.Text = reinstallTarget.Directory;
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
            CreateBtn.Content = "开始覆盖安装";
        }
        else
        {
            TargetDirBox.Text = Path.Combine(App.DataDir, "servers", "new-server");
            NameBox.Text = "我的服务器";

            // 重要修复：这里之前固定用客户端全局的 PreferredJavaMajorVersion 去找 Java，
            // 跟服务器实际要装的 MC 版本/核心完全没关系——用户还没选版本，这个"预填"本来就
            // 只能算个初始猜测，且极容易和真正要求的版本不一致，是本轮修复的
            // "class file version 69.0...up to 65.0" 报错的根因之一。
            // 这里先不做版本限定的预填（留空，交给用户点"自动检测"或手动填），
            // 真正会用到的 Java 版本要求在 McVersionCombo/BuildVersionCombo 选定后才能确定，
            // 见 UpdateJavaRequirementHint() 和 Create_Click 里的 ResolveJavaForDownloadAsync。
            var detected = _javaService.FindJava(_owner.ConfigService.Config.JavaPath);
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

        BuildVersionPanel.Visibility = coreType == ServerCoreType.Vanilla ? Visibility.Collapsed : Visibility.Visible;
        BuildVersionLabel.Text = coreType switch
        {
            ServerCoreType.Paper => "Build 号",
            ServerCoreType.Fabric => "Loader 版本",
            ServerCoreType.Forge => "安装器版本",
            ServerCoreType.NeoForge => "版本号",
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
                _ => new List<string>()
            };
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
            CreateBtn.IsEnabled = true;
        }
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _buildVersions.Clear();
        UpdateJavaRequirementHint();
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        if (_selectedCoreType is ServerCoreType.Vanilla or ServerCoreType.NeoForge) return;

        try
        {
            List<ServerCoreBuild> builds = _selectedCoreType switch
            {
                ServerCoreType.Paper => await _coreService.GetPaperBuildsAsync(mcVersion),
                ServerCoreType.Fabric => await _coreService.GetFabricLoaderVersionsAsync(),
                ServerCoreType.Forge => await _coreService.GetForgeInstallerVersionsAsync(mcVersion),
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
        if (dialog.ShowDialog(this) == true) TargetDirBox.Text = dialog.FolderName;
    }

    private void AutoDetectJava_Click(object sender, RoutedEventArgs e)
    {
        var found = _javaService.FindJava(null);
        if (found == null)
        {
            MessageBox.Show("没有检测到可用的 Java。请在「设置」页先下载/配置 Java，或者手动填写路径。",
                "未找到 Java", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    private async Task<string> ResolveOrDownloadJavaAsync(int requiredMajor, IProgress<ProgressInfo>? progress)
    {
        var current = JavaPathBox.Text;
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            var matched = _javaService.FindJava(current, requiredMajor);
            if (matched != null)
            {
                JavaPathBox.Text = matched;
                // 用户在 Java 列表里选的这一条本来就满足版本要求，原样保留 _selectedJavaId，
                // 不需要再重新登记一遍；否则(比如用户手填/自动检测出来的路径)清空，走下面的自动登记。
                if (!string.Equals(matched, current, StringComparison.OrdinalIgnoreCase)
                    || (JavaListCombo.SelectedItem as JavaListItem)?.Entry == null)
                    _selectedJavaId = null;
                return matched;
            }
        }

        var found = _javaService.FindJava(null, requiredMajor);
        if (found != null)
        {
            JavaPathBox.Text = found;
            _selectedJavaId = _owner.ConfigService.RegisterJava(found, requiredMajor, "Manual").Id;
            _owner.ConfigService.Save();
            return found;
        }

        progress?.Report(new ProgressInfo("下载匹配的 Java 运行时", 0, 1, $"Java {requiredMajor}"));
        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var downloaded = await _javaService.DownloadJavaAsync(
            new JavaDownloadRequest(requiredMajor, arch, JavaInstallMode.Portable), progress);

        JavaPathBox.Text = downloaded;
        // 新下载的 Java 自动登记进全局 Java 列表，方便以后其它服务器/客户端版本也能直接选用它，
        // 不用每个服务器各自重复下载一份。
        _selectedJavaId = _owner.ConfigService.RegisterJava(downloaded, requiredMajor, "Downloaded").Id;
        _owner.ConfigService.Save();
        return downloaded;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("请填写服务器名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 重装模式下名称就是目标实例自身的名称，必然会命中同名检查，跳过；
        // 新建模式下才需要防止用户新建一个和现有实例撞名的服务器。
        if (_reinstallTarget == null && _owner.ServerInstanceService.Instances.Any(i => i.DisplayName == name))
        {
            MessageBox.Show("已经有一个同名的服务器了，请换一个名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (McVersionCombo.SelectedItem is not string mcVersion)
        {
            MessageBox.Show("请选择 Minecraft 版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetDirBox.Text))
        {
            MessageBox.Show("请选择安装位置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(MinMemoryBox.Text, out var minMem) || minMem <= 0 ||
            !int.TryParse(MaxMemoryBox.Text, out var maxMem) || maxMem <= 0 || maxMem < minMem)
        {
            MessageBox.Show("内存设置不合法：请确认最小/最大内存都是正整数，且最大不小于最小。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int? cpuLimit = null;
        if (!string.IsNullOrWhiteSpace(CpuLimitBox.Text))
        {
            if (!int.TryParse(CpuLimitBox.Text, out var cpu) || cpu is < 1 or > 100)
            {
                MessageBox.Show("CPU 上限必须是 1-100 之间的整数，或留空表示不限制。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            cpuLimit = cpu;
        }

        int? diskLimit = null;
        if (!string.IsNullOrWhiteSpace(DiskLimitBox.Text))
        {
            if (!int.TryParse(DiskLimitBox.Text, out var disk) || disk <= 0)
            {
                MessageBox.Show("磁盘上限必须是正整数（单位 MB），或留空表示不限制。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            diskLimit = disk;
        }

        // Vanilla/Paper/Fabric 必须要有 Java 才能后续启动；Forge/NeoForge 安装阶段同样需要 Java 跑安装器。
        if (string.IsNullOrWhiteSpace(JavaPathBox.Text) || !File.Exists(JavaPathBox.Text))
        {
            MessageBox.Show("请提供一个有效的 Java 路径（点击「自动检测」或手动填写）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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

        CreateBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        var downloadProgress = new Progress<ProgressInfo>(p =>
        {
            ProgressStageText.Text = p.Stage;
            ProgressDetailText.Text = p.CurrentFile;
            ProgressBarCtl.Maximum = Math.Max(p.Total, 1);
            ProgressBarCtl.Value = p.Done;
        });

        try
        {
            var result = await _coreService.DownloadAsync(req, downloadProgress);

            // 关键修复：不再无条件使用 JavaPathBox 里预填的路径（那很可能是客户端全局偏好版本，
            // 和这次实际下载的核心/MC版本要求的 Java 主版本号对不上）。下载完成后，服务端核心的
            // 真实 Java 版本要求（result.RequiredJavaMajorVersion）已经确定，这里据此重新校验/
            // 自动下载匹配的 Java，Forge/NeoForge 安装器本身也需要用这个匹配的 Java 来跑，
            // 否则同样会出现 "class file version ... UnsupportedClassVersionError"。
            var resolvedJavaPath = await ResolveOrDownloadJavaAsync(result.RequiredJavaMajorVersion, downloadProgress);

            string launchTarget;
            bool launchTargetIsScript;

            if (result.RequiresInstall)
            {
                ProgressStageText.Text = "正在运行安装器";
                ProgressDetailText.Text = "首次安装可能需要下载额外库文件，请耐心等待...";
                ProgressBarCtl.IsIndeterminate = true;

                var installProgress = new Progress<string>(line => ProgressDetailText.Text = line);
                var scriptOrDir = await _coreService.RunForgeInstallerAsync(
                    result.DownloadedFilePath, req.TargetDir, resolvedJavaPath, installProgress);

                ProgressBarCtl.IsIndeterminate = false;

                if (File.Exists(scriptOrDir) &&
                    (scriptOrDir.EndsWith("run.bat", StringComparison.OrdinalIgnoreCase) ||
                     scriptOrDir.EndsWith("run.sh", StringComparison.OrdinalIgnoreCase)))
                {
                    launchTarget = Path.GetFileName(scriptOrDir);
                    launchTargetIsScript = true;
                }
                else
                {
                    // 找不到标准启动脚本名（极老的 Forge 版本安装完是直接一个 jar，没有 run 脚本）：
                    // 退化为在目标目录里找一个看起来是服务端本体的 jar 文件。
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

            if (_reinstallTarget != null)
            {
                // 重装模式：更新已有实例记录的核心相关字段，不新增记录、不改名称/目录/内存等
                // 用户在这个窗口里被锁定不能改的字段（那些字段已经在构造函数里预填并禁用编辑）。
                _reinstallTarget.CoreType = _selectedCoreType;
                _reinstallTarget.McVersion = mcVersion;
                _reinstallTarget.LaunchTarget = launchTarget;
                _reinstallTarget.LaunchTargetIsScript = launchTargetIsScript;
                _reinstallTarget.JavaPath = resolvedJavaPath;
                _reinstallTarget.JavaId = _selectedJavaId;
                _reinstallTarget.RequiredJavaMajorVersion = result.RequiredJavaMajorVersion;

                _owner.ServerInstanceService.Update(_reinstallTarget);
                CreatedInstance = _reinstallTarget;

                MessageBox.Show($"服务器「{name}」的核心已重新安装完成！",
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
                return;
            }

            var instance = new ServerInstance
            {
                DisplayName = name,
                Directory = req.TargetDir,
                CoreType = _selectedCoreType,
                McVersion = mcVersion,
                LaunchTarget = launchTarget,
                LaunchTargetIsScript = launchTargetIsScript,
                JavaPath = resolvedJavaPath,
                JavaId = _selectedJavaId,
                RequiredJavaMajorVersion = result.RequiredJavaMajorVersion,
                MinMemoryMb = minMem,
                MaxMemoryMb = maxMem,
                CpuLimitPercent = cpuLimit,
                DiskLimitMb = diskLimit
            };

            _owner.ServerInstanceService.Add(instance);
            CreatedInstance = instance;

            MessageBox.Show($"服务器「{name}」创建完成！\n可以在服务器列表里启动它。",
                "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"创建失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CreateBtn.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressBarCtl.IsIndeterminate = false;
        }
    }
}
