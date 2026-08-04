using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 「百宝箱」页：正式功能范围内的一批小工具集合（成就图片生成、皮肤头像生成、
/// 自定义文件下载、正版皮肤下载、加载器 Jar 单独下载、清理游戏垃圾、创建快捷方式、
/// 查看启动计数、内存优化），不属于「实验性功能」，参见 ExperimentalGateWindow.xaml
/// 里"不属于实验性功能"那段说明。
///
/// XAML 界面（5 个 Tab）早就搭好了，这个文件是把每个按钮点击事件接上对应的、
/// 已经写好并有完整实现的 Service：AchievementImageService / SkinAvatarRenderService /
/// OfficialSkinFetchService / JunkCleanupService / ShortcutService /
/// ClientLoaderInstallService / MemoryOptimizerService，这里只是"胶水代码"，
/// 不重新实现任何一个功能的核心逻辑。
/// </summary>
public partial class ToolboxPage : UserControl
{
    private readonly MainWindow _owner;

    // ===== Tab 1：成就图片 =====
    private byte[]? _achPreviewBytes;

    // ===== Tab 2：皮肤头像 =====
    private string? _avatarSkinPath;
    private byte[]? _avatarPreviewBytes;

    // ===== Tab 3：文件下载 / 正版皮肤 =====
    private readonly HttpClient _dlHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly OfficialSkinFetchService _officialSkinService = new();
    private OfficialSkinFetchService.OfficialSkinInfo? _lookedUpSkinInfo;
    private bool _dlInProgress;

    // ===== Tab 4：加载器下载 =====
    private ClientLoaderInstallService? _loaderService;
    private string? _selectedLoaderTag;

    public ToolboxPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        _dlHttp.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher-Toolbox/1.0");

        MemOptCheck.IsChecked = _owner.ConfigService.Config.EnableMemoryOptimization;

        // 基岩版检测是异步的（要跑一次 PowerShell），不阻塞页面构造。
        RefreshBedrockStatusAsync();
    }

    // ============================================================
    // Tab 1：自定义成就图片生成器
    // ============================================================

    private void AchPreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rawItemId = string.IsNullOrWhiteSpace(AchItemIdBox.Text) ? "minecraft:diamond" : AchItemIdBox.Text.Trim();

            // 物品 ID 归一化：把 "Diamond Sword" 这种写法规整成合法的 minecraft:diamond_sword，
            // 并把更正后的结果回填到输入框，让用户看到实际用的是什么
            // （旧版完全不校验格式，还会在 ID 以冒号结尾时画出一个 NUL 字符，
            //  详见 AchievementImageService 类头注释里对这个 bug 的说明）。
            var normalized = AchievementImageService.NormalizeItemId(rawItemId);
            if (normalized.WasChanged) AchItemIdBox.Text = normalized.FullId;
            var itemId = normalized.FullId;
            var achName = string.IsNullOrWhiteSpace(AchNameBox.Text) ? "Achievement Get!" : AchNameBox.Text.Trim();
            var line1 = AchLine1Box.Text?.Trim() ?? "";
            var line2 = string.IsNullOrWhiteSpace(AchLine2Box.Text) ? null : AchLine2Box.Text.Trim();

            _achPreviewBytes = AchievementImageService.Generate(itemId, achName, line1, line2);
            AchPreviewImage.Source = BytesToBitmapImage(_achPreviewBytes);
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"生成成就预览图失败：{ex.Message}");
        }
    }

    private void AchSave_Click(object sender, RoutedEventArgs e)
    {
        // 生成图片是纯 CPU 计算，很快，这里直接保存前先跑一遍预览逻辑，
        // 避免用户改完文字忘记点"预览"就直接点"保存图片"，保存的还是上一次的旧内容。
        AchPreview_Click(sender, e);
        if (_achPreviewBytes == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存成就图片",
            Filter = "PNG 图片|*.png",
            FileName = $"achievement_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _achPreviewBytes);
            MessageBoxDialog.ShowSuccess($"已保存到：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"保存失败：{ex.Message}");
        }
    }

    // ============================================================
    // Tab 2：皮肤头像生成器
    // ============================================================

    private void AvatarSelectSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择皮肤文件", Filter = "PNG 图片|*.png" };
        if (dialog.ShowDialog() != true) return;

        _avatarSkinPath = dialog.FileName;
        AvatarSourceText.Text = _avatarSkinPath;
        RenderAvatarPreview();
    }

    private void AvatarSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderAvatarPreview();

    private void RenderAvatarPreview()
    {
        if (string.IsNullOrEmpty(_avatarSkinPath) || !File.Exists(_avatarSkinPath)) return;

        try
        {
            var size = GetSelectedAvatarSize();
            var skinBytes = File.ReadAllBytes(_avatarSkinPath);
            _avatarPreviewBytes = SkinAvatarRenderService.RenderFaceAvatar(skinBytes, size);
            AvatarPreviewImage.Source = BytesToBitmapImage(_avatarPreviewBytes);
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"生成头像预览失败：{ex.Message}");
        }
    }

    private int GetSelectedAvatarSize()
    {
        var content = (AvatarSizeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "64x64";
        var sizeText = content.Split('x')[0];
        return int.TryParse(sizeText, out var size) ? size : 64;
    }

    private void AvatarSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_avatarSkinPath))
        {
            MessageBoxDialog.ShowWarning("请先选择一个皮肤文件。");
            return;
        }

        RenderAvatarPreview();
        if (_avatarPreviewBytes == null) return;

        var size = GetSelectedAvatarSize();
        var dialog = new SaveFileDialog
        {
            Title = "保存皮肤头像",
            Filter = "PNG 图片|*.png",
            FileName = $"avatar_{size}x{size}.png"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _avatarPreviewBytes);
            MessageBoxDialog.ShowSuccess($"已保存到：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"保存失败：{ex.Message}");
        }
    }

    // ============================================================
    // Tab 3：下载自定义文件 / 下载正版玩家的皮肤
    // ============================================================

    private void DlBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择保存目录" };
        if (!string.IsNullOrWhiteSpace(DlSaveDirBox.Text) && Directory.Exists(DlSaveDirBox.Text))
            dialog.InitialDirectory = DlSaveDirBox.Text;

        if (dialog.ShowDialog() == true)
            DlSaveDirBox.Text = dialog.FolderName;
    }

    private void DlOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetDlSaveDirOrDefault();
        Directory.CreateDirectory(dir);
        FolderOpenHelper.Open(dir);
    }

    private string GetDlSaveDirOrDefault()
    {
        if (!string.IsNullOrWhiteSpace(DlSaveDirBox.Text)) return DlSaveDirBox.Text;
        var dir = Path.Combine(AppContext.BaseDirectory, "Downloads");
        DlSaveDirBox.Text = dir;
        return dir;
    }

    private async void DlStart_Click(object sender, RoutedEventArgs e)
    {
        if (_dlInProgress) return;

        var url = DlUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBoxDialog.ShowWarning("请填写下载地址。");
            return;
        }

        var saveDir = GetDlSaveDirOrDefault();

        _dlInProgress = true;
        DlStartBtn.IsEnabled = false;
        DlStatusText.Text = "正在下载...";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(DlUserAgentBox.Text))
            {
                request.Headers.Remove("User-Agent");
                request.Headers.TryAddWithoutValidation("User-Agent", DlUserAgentBox.Text.Trim());
            }

            using var response = await _dlHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var fileName = string.IsNullOrWhiteSpace(DlFileNameBox.Text)
                ? ResolveDownloadFileName(url, response)
                : DlFileNameBox.Text.Trim();

            Directory.CreateDirectory(saveDir);
            var destPath = Path.Combine(saveDir, fileName);

            await using (var fs = File.Create(destPath))
            await using (var stream = await response.Content.ReadAsStreamAsync())
            {
                await stream.CopyToAsync(fs);
            }

            DlStatusText.Text = $"下载完成：{destPath}";
        }
        catch (Exception ex)
        {
            // 403 等特定情形按需求文案单独提示一句，更贴近截图里"部分网站可能会报错 (403) 已禁止"的说明。
            DlStatusText.Text = ex is HttpRequestException httpEx && httpEx.Message.Contains("403")
                ? "下载失败：目标网站返回 403（已禁止），该站点可能不允许程序直接下载，请尝试用浏览器手动下载。"
                : $"下载失败：{ex.Message}";
        }
        finally
        {
            _dlInProgress = false;
            DlStartBtn.IsEnabled = true;
        }
    }

    private static string ResolveDownloadFileName(string url, HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        if (!string.IsNullOrWhiteSpace(contentDisposition?.FileNameStar))
            return contentDisposition.FileNameStar!.Trim('"');
        if (!string.IsNullOrWhiteSpace(contentDisposition?.FileName))
            return contentDisposition.FileName!.Trim('"');

        try
        {
            var uri = new Uri(url);
            var name = Path.GetFileName(uri.LocalPath);
            return string.IsNullOrWhiteSpace(name) ? $"download_{DateTime.Now:yyyyMMdd_HHmmss}" : name;
        }
        catch
        {
            return $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
    }

    private async void OfficialSkinSave_Click(object sender, RoutedEventArgs e)
    {
        var playerName = OfficialPlayerNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            MessageBoxDialog.ShowWarning("请输入正版玩家名。");
            return;
        }

        OfficialSkinStatusText.Text = "正在查询玩家信息...";
        try
        {
            _lookedUpSkinInfo = await _officialSkinService.LookupAsync(playerName);
            OfficialSkinStatusText.Text = $"已找到玩家 {_lookedUpSkinInfo.PlayerName}（UUID: {_lookedUpSkinInfo.Uuid}），正在下载皮肤...";

            var saveDir = Path.Combine(AppContext.BaseDirectory, "Skins");
            var savedPath = await _officialSkinService.DownloadSkinAsync(_lookedUpSkinInfo, saveDir);
            OfficialSkinStatusText.Text = $"已保存到：{savedPath}";
        }
        catch (Exception ex)
        {
            OfficialSkinStatusText.Text = $"获取失败：{ex.Message}";
        }
    }

    // ============================================================
    // Tab 4：加载器 Jar 单独下载
    // ============================================================

    private ClientLoaderInstallService LoaderService =>
        _loaderService ??= new ClientLoaderInstallService(_owner.ConfigService.Config);

    private async void LoaderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = LoaderListBox.SelectedItem as ListBoxItem;
        var tag = item?.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;

        _selectedLoaderTag = tag;
        LoaderTitleText.Text = $"{tag} — 选择版本后下载 Jar";
        LoaderDownloadBtn.IsEnabled = false;
        LoaderMcVersionCombo.ItemsSource = null;
        LoaderVersionCombo.ItemsSource = null;
        LoaderStatusText.Text = Loc.T("Str_Ui_Fetching_Versions", "正在获取版本列表...");

        try
        {
            switch (tag)
            {
                case "Forge":
                    var forgeMcVersions = await LoaderService.GetForgeVersionsAsync();
                    LoaderMcVersionCombo.ItemsSource = forgeMcVersions;
                    break;
                case "NeoForge":
                    // NeoForge 只有 1.20.1 之后的版本，接口直接给的是完整版本号(如 21.1.100)，
                    // 不是"先选 MC 版本、再选 loader 版本"这种两级结构，这里把 MC 版本下拉框
                    // 隐去(不赋值 ItemsSource，UI 保留但留空)，加载器版本下拉框直接放完整列表。
                    var neoVersions = await LoaderService.GetNeoForgeVersionsAsync();
                    LoaderVersionCombo.ItemsSource = neoVersions;
                    LoaderMcVersionCombo.IsEnabled = false;
                    break;
                case "Fabric":
                    LoaderMcVersionCombo.IsEnabled = true;
                    var fabricMcVersions = await LoaderService.GetFabricMcVersionsAsync();
                    LoaderMcVersionCombo.ItemsSource = fabricMcVersions;
                    break;
                case "Quilt":
                    LoaderMcVersionCombo.IsEnabled = true;
                    var quiltMcVersions = await LoaderService.GetQuiltMcVersionsAsync();
                    LoaderMcVersionCombo.ItemsSource = quiltMcVersions;
                    break;
            }

            LoaderStatusText.Text = tag == "NeoForge" ? "请选择加载器版本。" : Loc.T("Str_Cs_Please_Choose_A_Minecraft_Version", "请选择 Minecraft 版本。");
        }
        catch (Exception ex)
        {
            LoaderStatusText.Text = $"获取版本列表失败：{ex.Message}";
        }
    }

    private async void LoaderMcVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LoaderMcVersionCombo.SelectedItem is not string mcVersion || string.IsNullOrEmpty(_selectedLoaderTag)) return;

        LoaderVersionCombo.ItemsSource = null;
        LoaderDownloadBtn.IsEnabled = false;
        LoaderStatusText.Text = "正在获取加载器版本列表...";

        try
        {
            switch (_selectedLoaderTag)
            {
                case "Forge":
                    var forgeBuilds = await LoaderService.GetForgeInstallerVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = forgeBuilds;
                    LoaderVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
                    break;
                case "Fabric":
                    var fabricLoaders = await LoaderService.GetFabricLoaderVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = fabricLoaders;
                    LoaderVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
                    break;
                case "Quilt":
                    var quiltLoaders = await LoaderService.GetQuiltLoaderVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = quiltLoaders;
                    LoaderVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
                    break;
            }

            LoaderStatusText.Text = "请选择加载器 / 安装器版本。";
            LoaderDownloadBtn.IsEnabled = LoaderVersionCombo.ItemsSource != null;
        }
        catch (Exception ex)
        {
            LoaderStatusText.Text = $"获取加载器版本列表失败：{ex.Message}";
        }
    }

    /// <summary>
    /// NeoForge 分支下拉框直接是版本号本身，选中即可下载，不需要再等 LoaderVersionCombo 的
    /// SelectionChanged（因为 NeoForge 场景下 LoaderVersionCombo 本身就承担了"唯一一级选择"
    /// 的角色，没有 McVersionCombo 的 SelectionChanged 顺带把下载按钮点亮），这里额外接一个
    /// 处理器保证按钮状态跟着联动。
    /// </summary>
    private void LoaderVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoaderDownloadBtn.IsEnabled = LoaderVersionCombo.SelectedItem != null;
    }

    private async void LoaderDownload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedLoaderTag))
        {
            MessageBoxDialog.ShowWarning("请先选择一个加载器。");
            return;
        }

        LoaderDownloadBtn.IsEnabled = false;
        LoaderStatusText.Text = "正在下载 Jar...";

        try
        {
            var saveDir = GetLoaderDownloadDir();
            Directory.CreateDirectory(saveDir);

            string url;
            string fileName;

            switch (_selectedLoaderTag)
            {
                case "Forge":
                {
                    var mcVersion = LoaderMcVersionCombo.SelectedItem as string
                                     ?? throw new InvalidOperationException("请先选择 Minecraft 版本。");
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择安装器版本。");
                    var forgeFullVersion = $"{mcVersion}-{build.DisplayVersion}";
                    url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{forgeFullVersion}/forge-{forgeFullVersion}-installer.jar";
                    fileName = $"forge-{forgeFullVersion}-installer.jar";
                    break;
                }
                case "NeoForge":
                {
                    var build = LoaderVersionCombo.SelectedItem as string
                                ?? throw new InvalidOperationException("请先选择加载器版本。");
                    url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{build}/neoforge-{build}-installer.jar";
                    fileName = $"neoforge-{build}-installer.jar";
                    break;
                }
                case "Fabric":
                {
                    var mcVersion = LoaderMcVersionCombo.SelectedItem as string
                                     ?? throw new InvalidOperationException("请先选择 Minecraft 版本。");
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择加载器版本。");
                    // Fabric 的"客户端 profile json"本身不是单独一个 jar 文件（是若干 library
                    // 引用+启动参数拼成的 json，LauncherService 靠这份 json 走 inheritsFrom
                    // 完整安装），"下载 Jar"这里改成下载 Fabric Loader 本体的 jar
                    // （Maven 坐标：net/fabricmc/fabric-loader/{loaderVersion}/），这跟
                    // 截图里"单独下载一个 jar 文件"的诉求对得上，而不是尝试下载一个根本不存在
                    // 的单文件"Fabric 客户端 jar"。
                    url = $"https://maven.fabricmc.net/net/fabricmc/fabric-loader/{build.DisplayVersion}/fabric-loader-{build.DisplayVersion}.jar";
                    fileName = $"fabric-loader-{build.DisplayVersion}.jar";
                    break;
                }
                case "Quilt":
                {
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择加载器版本。");
                    url = $"https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-loader/{build.DisplayVersion}/quilt-loader-{build.DisplayVersion}.jar";
                    fileName = $"quilt-loader-{build.DisplayVersion}.jar";
                    break;
                }
                default:
                    throw new InvalidOperationException("暂不支持该加载器的单独下载。");
            }

            var destPath = Path.Combine(saveDir, fileName);
            var bytes = await _dlHttp.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(destPath, bytes);

            LoaderStatusText.Text = $"下载完成：{destPath}";
        }
        catch (Exception ex)
        {
            LoaderStatusText.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            LoaderDownloadBtn.IsEnabled = true;
        }
    }

    private void LoaderOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetLoaderDownloadDir();
        Directory.CreateDirectory(dir);
        FolderOpenHelper.Open(dir);
    }

    private static string GetLoaderDownloadDir() => Path.Combine(AppContext.BaseDirectory, "LoaderJars");

    // ============================================================
    // Tab 5：清理游戏垃圾 / 创建快捷方式 / 启动计数 / 内存优化
    // ============================================================

    private JunkCleanupService.JunkScanResult? _junkScanResult;

    private string GetCurrentMinecraftDir()
    {
        var cfg = _owner.ConfigService.Config;
        var folder = cfg.Folders?.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath);
        return folder?.Path ?? cfg.Folders?.FirstOrDefault()?.Path ?? "";
    }

    private void JunkScan_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetCurrentMinecraftDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            JunkStatusText.Text = "还没有选择 .minecraft 文件夹，请先去「版本选择」页添加一个。";
            JunkCleanBtn.IsEnabled = false;
            return;
        }

        try
        {
            _junkScanResult = JunkCleanupService.Scan(dir);
            JunkStatusText.Text = _junkScanResult.Items.Count == 0
                ? "扫描完成：没有发现可清理的垃圾文件，很干净！"
                : $"扫描完成：发现 {_junkScanResult.Items.Count} 个可清理文件，共 {JunkCleanupService.FormatBytes(_junkScanResult.TotalBytes)}。";
            JunkCleanBtn.IsEnabled = _junkScanResult.Items.Count > 0;
        }
        catch (Exception ex)
        {
            JunkStatusText.Text = $"扫描失败：{ex.Message}";
            JunkCleanBtn.IsEnabled = false;
        }
    }

    private void JunkClean_Click(object sender, RoutedEventArgs e)
    {
        if (_junkScanResult == null || _junkScanResult.Items.Count == 0) return;

        if (!MessageBoxDialog.ShowConfirm(
                $"即将删除 {_junkScanResult.Items.Count} 个文件，共释放约 {JunkCleanupService.FormatBytes(_junkScanResult.TotalBytes)}。\n" +
                "不会影响存档/Mod/资源包/设置，确定继续吗？"))
        {
            return;
        }

        var (deletedCount, freedBytes) = JunkCleanupService.Delete(_junkScanResult.Items);
        JunkStatusText.Text = $"清理完成：删除了 {deletedCount} 个文件，释放 {JunkCleanupService.FormatBytes(freedBytes)}。";
        _junkScanResult = null;
        JunkCleanBtn.IsEnabled = false;
    }

    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ShortcutService.CreateDesktopShortcut();
            MessageBoxDialog.ShowSuccess($"已在桌面创建快捷方式：{path}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"创建快捷方式失败：{ex.Message}");
        }
    }

    private void ShowLaunchCount_Click(object sender, RoutedEventArgs e)
    {
        // 复用 AppConfig.GameLaunchSuccessCount：MainWindow.LaunchInternalAsync 里游戏
        // 每次成功启动都会 ++ 这个字段并持久化保存，这里只是读出来展示，不需要另建一套
        // 独立的"启动计数"存储/统计逻辑。
        var count = _owner.ConfigService.Config.GameLaunchSuccessCount;
        MessageBoxDialog.ShowInfo($"累计成功启动游戏 {count} 次。", "启动计数");
    }

    private void MemOptCheck_Changed(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;
        cfg.EnableMemoryOptimization = MemOptCheck.IsChecked == true;
        _owner.ConfigService.Save();

        MemOptStatusText.Text = cfg.EnableMemoryOptimization
            ? "已开启：下次启动游戏前会自动按当前可用内存重新计算 -Xms/-Xmx。"
            : "已关闭：启动游戏将使用「设置」页手动填写的固定内存数值。";
    }

    private void MemOptPreview_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;
        var recommendation = MemoryOptimizerService.Calculate(cfg.MemoryOptimizationReserveMb);

        MemOptStatusText.Text = recommendation == null
            ? "无法获取系统内存信息（可能不是 Windows 系统），此功能暂不可用。"
            : recommendation.Explanation;
    }

    // ============================================================
    // 公共工具方法
    // ============================================================

    private static BitmapImage BytesToBitmapImage(byte[] bytes)
    {
        var image = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ============================================================
    // Tab：基岩版
    // ============================================================

    private readonly BedrockContentService _bedrockService = new();

    /// <summary>检测基岩版是否已安装并更新界面状态。构造时和用户点"重新检测"时都会调。</summary>
    private async void RefreshBedrockStatusAsync()
    {
        try
        {
            BedrockStatusText.Text = "正在检测...";
            var installed = await BedrockLaunchService.IsInstalledAsync();
            BedrockLaunchBtn.IsEnabled = installed;
            BedrockStatusText.Text = installed
                ? "已检测到 Minecraft for Windows（基岩版）。"
                : "没有检测到基岩版。请从 Microsoft Store 安装并至少启动一次，之后再回来这里。";
        }
        catch
        {
            BedrockStatusText.Text = "检测失败（可能是 PowerShell 被禁用）。可以直接点「启动基岩版」试试。";
            BedrockLaunchBtn.IsEnabled = true;
        }
    }

    private void BedrockDetect_Click(object sender, RoutedEventArgs e) => RefreshBedrockStatusAsync();

    private void BedrockLaunch_Click(object sender, RoutedEventArgs e)
    {
        try { BedrockLaunchService.Launch(); }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("唤起基岩版失败，可能它没有正确安装。",
                ex.ToString(), Loc.T("Str_Cs_Couldn_T_Start_Bedrock_Edition", "启动基岩版失败"));
        }
    }

    private void BedrockOpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = BedrockContentService.ComMojangDir;
        if (!Directory.Exists(dir))
        {
            MessageBoxDialog.ShowInfo(
                "基岩版的数据目录还不存在。基岩版**首次启动之后**才会创建这个目录，" +
                "请先启动一次基岩版再来。", Loc.T("Str_Cs_No_Data_Folder_Yet", "还没有数据目录"));
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBoxDialog.ShowError($"打开文件夹失败：{ex.Message}"); }
    }

    private async void BedrockImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要导入的基岩版内容",
            Filter = "基岩版内容|*.mcworld;*.mcpack;*.mcaddon;*.mctemplate|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        if (!BedrockContentService.IsBedrockDataPresent)
        {
            MessageBoxDialog.ShowInfo(
                "这台电脑上还没有安装基岩版，或者基岩版从未启动过（首次启动才会创建数据目录），无法导入。",
                Loc.T("Str_Cs_Bedrock_Edition_Isn_T_Installed", "还没有安装基岩版"));
            return;
        }

        var pd = new ProgressDialog("正在导入基岩版内容 ...");
        pd.Show();
        try
        {
            var files = dlg.FileNames.ToList();
            var r = await Task.Run(() => _bedrockService.ImportMany(files,
                new Progress<string>(msg => pd.Progress.Report(new ProgressInfo(msg, 0, 1, "")))));

            var lines = new List<string>();
            if (r.Installed.Count > 0) lines.Add($"成功导入 {r.Installed.Count} 项：\n" + string.Join("\n", r.Installed));
            if (r.Failed.Count > 0) lines.Add($"\n未能导入 {r.Failed.Count} 项：\n" + string.Join("\n", r.Failed));
            MessageBoxDialog.ShowInfo(string.Join("\n", lines) + "\n\n重启基岩版后生效。", Loc.T("Str_Cs_Import_Complete_2", Loc.T("Str_Cs_Import_Complete_2", "导入完成")));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : Loc.T("Str_Cs_Couldn_T_Import_The_Bedrock_Content", "导入基岩版内容失败。"),
                ex.ToString(), Loc.T("Str_Cs_Import_Failed", "导入失败"));
        }
        finally { pd.Close(); }
    }

    private async void BedrockServerDownload_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择基岩版服务端的安装位置" };
        if (picker.ShowDialog() != true) return;

        BedrockServerDownloadBtn.IsEnabled = false;
        var pd = new ProgressDialog("正在下载基岩版服务端 ...");
        pd.Show();
        try
        {
            var version = await _bedrockService.DownloadDedicatedServerAsync(picker.FolderName, pd.Progress);
            BedrockServerStatusText.Text = $"已安装基岩版服务端 {version} 到：{picker.FolderName}";
            MessageBoxDialog.ShowSuccess(
                $"基岩版服务端 {version} 已下载并解压到：\n{picker.FolderName}\n\n" +
                "运行里面的 bedrock_server.exe 即可开服。首次运行会生成 server.properties，" +
                "改完记得重启服务端。",
                Loc.T("Str_Cs_Download_Complete", "下载完成"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(
                ex is InvalidOperationException ? ex.Message : "下载基岩版服务端失败，可能是网络问题。",
                ex.ToString(), Loc.T("Str_Cs_Download_Failed", "下载失败"));
        }
        finally
        {
            pd.Close();
            BedrockServerDownloadBtn.IsEnabled = true;
        }
    }
}

/// <summary>用 Windows 资源管理器打开一个文件夹，多处 Tab（文件下载/加载器下载）
/// 的"打开文件夹"按钮共用，避免每个按钮各自写一遍 Process.Start。</summary>
internal static class FolderOpenHelper
{
    public static void Open(string dir)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch
        {
            // 极端情况下打不开资源管理器（比如目录被删了）不应该抛异常打断用户操作，
            // 静默失败即可——用户能直接看到状态文字里已经显示的完整路径，自己手动导航过去。
        }
    }

}
