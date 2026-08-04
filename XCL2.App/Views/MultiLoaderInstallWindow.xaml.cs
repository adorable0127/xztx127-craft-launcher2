using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// "初步测试版功能"下唯一的子功能：纯粹的整活功能——先把用户勾选的每一种加载器，
/// 按各自正常的安装流程分别装进同一个 .minecraft 实例（复用 ClientLoaderInstallService
/// 已经验证过的各加载器安装方法，跟正常单独装一个加载器没有任何区别，这一步是可靠的），
/// 装完之后再把每个加载器版本目录（versions/&lt;versionId&gt;/ 下的所有文件，包括
/// version json、加载器自带的 jar 等）原样拷贝进同一个新建的"合装目标"版本文件夹里，
/// 文件名冲突就加上加载器前缀区分，不做任何 json 合并/mainClass 仲裁之类的"聪明"处理。
///
/// 需求原话："这个安装纯是为了整活，你可以直接把它的版本文件夹、加载器文件以及版本 json
/// 直接拷过去，没法用也没关系"——所以这里就是老老实实的文件搬运工，合装出来的这个版本
/// 文件夹大概率没法正常启动（Minecraft 的 version json 本身不支持一个版本同时挂多个
/// mod loader 的 mainClass/launch arguments，这在技术上是不可能的，拷贝多份 json 到
/// 同一个目录后最终生效的 mainClass 只会是最后写入的那一个，其余加载器的启动逻辑根本
/// 不会被执行），能不能用完全不保证，纯粹是"看看 versions 文件夹被塞满是什么感觉"的
/// 娱乐功能，不追求技术上的正确性。
///
/// 需要先输入正确的 token（TokenGateService 校验）才能看到下面的安装配置区域，
/// 校验通过前 InstallPanel 保持 Collapsed。
/// </summary>
public partial class MultiLoaderInstallWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;
    private readonly ClientLoaderInstallService _loaderService;
    private readonly JavaService _javaService = new();
    private bool _tokenUnlocked;

    /// <summary>
    /// 本次合装成功安装的所有 versionId（可能装了不止一个加载器）。
    /// 修复：之前这个窗口关闭后从不设置 DialogResult，调用方（ExperimentalFeaturesWindow）
    /// 也就无从得知"装完了、该刷新一下版本列表"，导致新装的版本文件夹其实已经落在
    /// versions/ 目录下、启动器却因为没重新扫描而"找不到"这个版本——不是没装上，
    /// 是装完了但没人去刷新 UI。现在把结果暴露出来，让外层决定要不要刷新。
    /// </summary>
    public List<string> InstalledVersionIds { get; } = new();

    public MultiLoaderInstallWindow(MainWindow owner)
    {
        _owner = owner;
        _loaderService = new ClientLoaderInstallService(owner.ConfigService.Config);
        InitializeComponent();

        // 同上：Window.Closed → IOverlayDialog.RequestClose
        RequestClose += (_, _) => _loaderService.Dispose();

        RiskText.Text = BuildRiskWarningText();

        var detected = _javaService.FindJava(_owner.ConfigService.Config.JavaPath, configService: _owner.ConfigService);
        JavaPathBox.Text = detected ?? "";
    }

    /// <summary>
    /// 完整的风险说明文案。老实交代这个功能目前只做了什么、可能出什么问题、出问题了该怎么办，
    /// 不做法律意义上的"责任转移声明"（那种东西本身也没有实际法律效力），只把风险讲清楚，
    /// 让用户在知情的前提下自己决定要不要继续。
    /// </summary>
    private static string BuildRiskWarningText() =>
        "这是一个纯粹为了整活而做的实验性功能——目前只实现了「把同一个 MC 版本，用你勾选的" +
        "每一种加载器分别装进同一个 .minecraft 实例，再把每个加载器版本目录里的所有文件（版本" +
        "json、加载器自带的 jar 等）原样拷贝进同一个「合装」版本文件夹」这一件事，没有做任何" +
        "让它们真正一起工作的处理。\n\n" +
        "请提前了解：\n" +
        "· 拼出来的这个「合装」版本，大概率没法正常启动——Minecraft 的版本文件格式本身只支持" +
        "一个版本对应一个 mainClass，几份 json 拷进同一个目录后最终生效的只会是最后写入的" +
        "那一份，其余加载器的启动逻辑根本不会被执行，这是技术上就做不到的事，不是 bug；\n" +
        "· 多个加载器共用同一个 mods/ 文件夹时，同一个 Mod 文件如果只兼容其中一种加载器，" +
        "在另一个加载器的版本里启动可能直接崩溃，或者游戏能进但表现异常；\n" +
        "· 反复安装/卸载可能在 versions/ 目录下留下不完整的版本文件夹，占用磁盘空间；\n" +
        "· 如果你在同一个实例里开着「资源包/存档共享」之类的设置，极端情况下可能因为不同" +
        "加载器读取存档的方式细节不同，导致存档打开变慢、报错，甚至读取异常，最坏情况下" +
        "存档可能损坏或无法正常读取——安装前请自行提前备份好这个实例里的存档。\n\n" +
        "简单说：这个功能就是把 versions 文件夹当画布整活，能不能正常玩完全不保证，" +
        "纯粹图一乐。如果你只是想真的用上某个加载器玩游戏，请用「版本选择」页正常安装" +
        "单个加载器版本，不要用这个功能。\n\n" +
        "另外友情提示：这个功能不会让你的游戏突然拥有四个加载器叠加的超能力，也不会因为装得多" +
        "帧数就变高——它顶多让你的 versions/ 文件夹看起来更热闹一点。";

    private void UnlockToken_Click(object sender, RoutedEventArgs e)
    {
        if (TokenGateService.ValidateMultiLoaderToken(TokenBox.Text))
        {
            _tokenUnlocked = true;
            TokenStatusText.Text = Loc.T("Str_Cs_Token_Accepted_Welcome_To_The_Unfinished", "✅ Token 校验通过，欢迎来到还没写完的角落。下面可以开始配置了。");
            TokenStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessTextBrush");
            TokenPanel.Visibility = Visibility.Collapsed;
            InstallPanel.Visibility = Visibility.Visible;
        }
        else
        {
            TokenStatusText.Text = Loc.T("Str_Cs_Wrong_Token_This_Isn_T_A_Hint_Towards_An", "❌ Token 不对。这不是彩蛋提示——这里是真的需要正确的 token 才能继续，请检查有没有多打/漏打空格或符号。");
            TokenStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    private void LoaderCheck_Changed(object sender, RoutedEventArgs e)
    {
        var count = SelectedLoaders().Count;
        SelectedCountText.Text = count == 0
            ? "已选加载器数：0"
            : $"已选加载器数：{count} —— {SuccessRateJoke(count)}";
    }

    private List<ServerCoreType> SelectedLoaders()
    {
        var result = new List<ServerCoreType>();
        if (ChkFabric.IsChecked == true) result.Add(ServerCoreType.Fabric);
        if (ChkQuilt.IsChecked == true) result.Add(ServerCoreType.Quilt);
        if (ChkForge.IsChecked == true) result.Add(ServerCoreType.Forge);
        if (ChkNeoForge.IsChecked == true) result.Add(ServerCoreType.NeoForge);
        return result;
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
    }

    /// <summary>
    /// 每装完一个加载器随机换一句幽默小彩蛋，纯装饰，不影响任何实际逻辑——
    /// 毕竟这功能本身已经够严肃了，装完一个给自己鼓个掌也不过分。
    /// </summary>
    private static readonly string[] EasterEggLines =
    {
        "彩蛋：这个加载器装完了，你的 .minecraft 文件夹的心理阴影面积正在稳步扩大。",
        "彩蛋：如果 Mod 之间开始打架，请记住这不是你的错，是它们自己没有商量好。",
        "彩蛋：科学计数法告诉我们，加载器数量 × 想不开程度 = 今晚要写多少个 issue。",
        "彩蛋：恭喜解锁新成就「versions 文件夹考古学家」。",
        "彩蛋：据不完全统计，这个功能的耗电量主要来自你反复点安装按钮的手指。",
        "冷知识：Fabric 和 Quilt 其实是本家，Quilt 就是从 Fabric Loader fork 出来的——" +
        "表面上兄弟阋墙，底层 Meta API 长得几乎一模一样，这也是这个窗口能用同一套代码装两家的原因。",
        "冷知识：Forge 和 NeoForge 其实是父子——NeoForge 是从某个版本的 Forge 分支出来独立发展的，" +
        "但两边社区吵起来的时候，谁也不肯承认这门亲戚关系，标准的「你大爷永远是你大爷，但你大爷不认你」。",
        "彩蛋：有人说合装多个加载器是在挑战 Minecraft 版本文件格式的物理法则，" +
        "其实只是把每个加载器都各装成自己的一个 version json，老老实实，没有任何黑魔法。",
    };

    /// <summary>
    /// 加载器数量 -> 官方认证的（并不）成功率吐槽文案。纯装饰彩蛋，跟真实安装成功率毫无关系——
    /// 现实中装 4 个加载器大概率是会失败没错，但不会精确地失败到 -114514% 这么离谱的程度。
    /// </summary>
    private static string SuccessRateJoke(int loaderCount) => loaderCount switch
    {
        <= 1 => "选择 1 个加载器：官方认证成功率 100%（大概）。",
        2 => "选择 2 个加载器：官方认证成功率 99%，剩下 1% 留给玄学。",
        3 => "选择 3 个加载器：官方认证成功率 0.001%，建议先烧香。",
        _ => "选择 4 个加载器：启动成功率 -114514%，物理意义请自行脑补，反正不用太当真。",
    };

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_tokenUnlocked) return; // 双重保险：理论上按钮此时应该还不可见/不可点

        var mcVersion = McVersionBox.Text?.Trim();
        if (string.IsNullOrEmpty(mcVersion))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Enter_A_Minecraft_Version_E_G_1_2", "请填写 Minecraft 版本号，例如 1.20.1。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var loaders = SelectedLoaders();
        if (loaders.Count == 0)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Select_At_Least_One_Loader", "请至少勾选一个加载器。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var needsJava = loaders.Contains(ServerCoreType.Forge) || loaders.Contains(ServerCoreType.NeoForge);
        if (needsJava && (string.IsNullOrWhiteSpace(JavaPathBox.Text) || !File.Exists(JavaPathBox.Text)))
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Forge_Or_Neoforge_Is_Selected_Which_Need", "勾选了 Forge/NeoForge，需要一个有效的本地 Java 路径（点「自动检测」或手动填写）。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var minecraftDir = _owner.ConfigService.Config.Folders
            .FirstOrDefault(f => f.Path == _owner.ConfigService.Config.SelectedFolderPath)?.Path;
        if (string.IsNullOrEmpty(minecraftDir))
        {
            MessageBoxDialog.ShowWarning(Loc.T("Str_Cs_The_Selected_Minecraft_Folder_Couldn_T_B", "没有找到当前选中的 .minecraft 文件夹，请先在「版本选择」页选择/添加一个文件夹。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        InstallBtn.IsEnabled = false;
        var installedIds = new List<string>();
        var failed = new List<string>();
        var rnd = new Random();

        var progress = new Progress<ProgressInfo>(p =>
        {
            ProgressStageText.Text = p.Stage;
            ProgressDetailText.Text = p.CurrentFile;
            ProgressBarCtl.Maximum = Math.Max(p.Total, 1);
            ProgressBarCtl.Value = p.Done;
        });

        foreach (var loader in loaders)
        {
            ProgressStageText.Text = $"正在安装 {loader}...";
            try
            {
                string versionId = loader switch
                {
                    ServerCoreType.Fabric => await InstallFabricLatestAsync(minecraftDir, mcVersion, progress),
                    ServerCoreType.Quilt => await InstallQuiltLatestAsync(minecraftDir, mcVersion, progress),
                    ServerCoreType.Forge => await InstallForgeLatestAsync(minecraftDir, mcVersion, progress),
                    ServerCoreType.NeoForge => await InstallNeoForgeLatestAsync(minecraftDir, mcVersion, progress),
                    _ => throw new InvalidOperationException($"未知加载器类型：{loader}")
                };
                installedIds.Add(versionId);
                InstalledVersionIds.Add(versionId);
                EasterEggText.Text = EasterEggLines[rnd.Next(EasterEggLines.Length)];
            }
            catch (Exception ex)
            {
                failed.Add($"{loader}：{ex.Message}");
            }
        }

        InstallBtn.IsEnabled = true;

        var summary_MergeFailureNote = "";
        // 整活环节：只要成功装好的加载器数量 >= 1，就把它们各自版本目录里的所有文件
        // （version json、加载器自带的 jar 等）原样拷贝进同一个新建的"合装"版本文件夹。
        // 拷贝失败（比如某个文件被占用）只记录、不阻断整体流程——这一步本来就是纯装饰性的
        // 整活操作，前面各加载器的真实安装已经各自独立成功了，不应该被这一步的失败拖累。
        string? mergedVersionId = null;
        if (installedIds.Count > 0)
        {
            try
            {
                mergedVersionId = MergeIntoOnePlateOfMixedStew(minecraftDir, mcVersion, installedIds);
            }
            catch (Exception mergeEx)
            {
                summary_MergeFailureNote = $"\n\n（整活拼盘这一步失败了，不影响上面各加载器各自的安装结果：{mergeEx.Message}）";
            }
        }

        var summary = $"合装完成。\n\n成功安装 {installedIds.Count} 个：\n" +
            (installedIds.Count > 0 ? string.Join('\n', installedIds) : "（无）");
        if (failed.Count > 0)
        {
            summary += $"\n\n失败 {failed.Count} 个：\n" + string.Join('\n', failed);
        }
        if (mergedVersionId != null)
        {
            summary += $"\n\n🍲 整活拼盘完成：已经把上面这 {installedIds.Count} 个加载器版本目录里的所有文件" +
                $"（version json、加载器自带的 jar 等）原样拷贝进了同一个新版本文件夹「{mergedVersionId}」，" +
                "在「版本选择」页可以看到它。\n" +
                "再强调一遍：这个「拼盘」版本大概率没法正常启动——几份 mainClass 不同的 json 堆进同一个" +
                "目录，最终生效的只会是最后写入的那一份，其余加载器根本不会被真正执行到，这是 Minecraft" +
                "版本文件格式本身的限制，不是没拼好。纯粹图一乐，能不能进游戏随缘。";
        }
        summary += summary_MergeFailureNote;
        MessageBoxDialog.ShowWarning(summary, installedIds.Count > 0 ? Loc.T("Str_Common_Finish", "完成") : Loc.T("Str_Cs_All_Failed", "全部失败"));

        // 修复核心：只要成功装上了至少一个加载器，就把 DialogResult 设为 true。
        // 之前这里从不设置 DialogResult，ShowDialog() 调用方拿到的永远是 null/false，
        // 于是没人知道"该去重新扫描 versions/ 目录了"——这才是"装完了但启动器找不到
        // 这个版本"的真正原因：文件确实已经落盘，只是列表没刷新。
        // 全部失败时不设置，让调用方保持"什么都没变"的语义，用户也可以继续留在这个窗口重试。
        if (installedIds.Count > 0)
        {
            CloseWith(true);
        }
    }

    /// <summary>Fabric：自动取推荐的 Loader 版本（列表里第一个 IsRecommended，没有就取第一个），
    /// 合装功能不想再让用户对着每个加载器各选一次构建版本，直接用官方推荐版本。</summary>
    private async Task<string> InstallFabricLatestAsync(string minecraftDir, string mcVersion, IProgress<ProgressInfo> progress)
    {
        var builds = await _loaderService.GetFabricLoaderVersionsAsync(mcVersion);
        var loaderVersion = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion ?? builds.FirstOrDefault()?.DisplayVersion
            ?? throw new InvalidOperationException("没有可用的 Fabric Loader 版本。");
        return await _loaderService.InstallFabricClientAsync(minecraftDir, mcVersion, loaderVersion, progress);
    }

    private async Task<string> InstallQuiltLatestAsync(string minecraftDir, string mcVersion, IProgress<ProgressInfo> progress)
    {
        var builds = await _loaderService.GetQuiltLoaderVersionsAsync(mcVersion);
        var loaderVersion = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion ?? builds.FirstOrDefault()?.DisplayVersion
            ?? throw new InvalidOperationException("没有可用的 Quilt Loader 版本。");
        return await _loaderService.InstallQuiltClientAsync(minecraftDir, mcVersion, loaderVersion, progress);
    }

    private async Task<string> InstallForgeLatestAsync(string minecraftDir, string mcVersion, IProgress<ProgressInfo> progress)
    {
        var builds = await _loaderService.GetForgeInstallerVersionsAsync(mcVersion);
        var installerVersion = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion ?? builds.FirstOrDefault()?.DisplayVersion
            ?? throw new InvalidOperationException("没有可用的 Forge 安装器版本。");
        return await _loaderService.InstallForgeOrNeoForgeClientAsync(
            minecraftDir, ServerCoreType.Forge, installerVersion, JavaPathBox.Text, progress);
    }

    /// <summary>
    /// 修复："一锅乱炖"合装 4 个加载器时经常只成功 2 个的根因之一——之前这里直接把
    /// mcVersion（比如 "1.20.1"）当作 NeoForge 的 fullVersion 传给
    /// InstallForgeOrNeoForgeClientAsync，但 NeoForge 官方 Maven 仓库要的是它自己的完整
    /// 构建版本号（比如 "21.1.100"，格式跟 MC 版本号完全不同），这样拼出来的下载 URL
    /// 必然 404，NeoForge 安装 100% 失败，且失败信息只会显示成"下载失败：HTTP 404"，
    /// 不容易一眼看出是传错了版本号这种参数层面的问题。
    /// 修法跟 Forge 分支保持一致：先调用 GetNeoForgeVersionsAsync 查出这个 MC 版本对应的
    /// 全部 NeoForge 构建，优先选带 recommended 标记的，没有就退化取第一个（最新的）。
    /// </summary>
    private async Task<string> InstallNeoForgeLatestAsync(string minecraftDir, string mcVersion, IProgress<ProgressInfo> progress)
    {
        var allBuilds = await _loaderService.GetNeoForgeVersionsAsync();
        // NeoForge 的版本号格式是 "{MC次版本}.{MC补丁版本}.{构建号}"（例如 MC 1.20.1 对应
        // 21.1.x），不像 Forge 安装器列表那样自带"只返回匹配这个 MC 版本的构建"的过滤，
        // 这里按 mcVersion 的 "主.次" 段过滤一遍，避免选到其他 MC 版本的 NeoForge 构建。
        var mcParts = mcVersion.Split('.');
        var mcPrefix = mcParts.Length >= 2 ? $"{mcParts[1]}.{(mcParts.Length >= 3 ? mcParts[2] : "0")}" : null;
        var matched = mcPrefix != null
            ? allBuilds.Where(v => v.StartsWith(mcPrefix + ".", StringComparison.Ordinal)).ToList()
            : allBuilds;
        // GetNeoForgeVersionsAsync 返回的列表已经是"从新到旧"排好序的（内部做过 Reverse），
        // 取第一个就是这个 MC 版本目前最新的 NeoForge 构建，不能用 LastOrDefault（那样反而会
        // 选到最旧、很可能已经过时甚至不兼容当前 MC 版本细节的构建）。
        var fullVersion = (matched.Count > 0 ? matched : allBuilds).FirstOrDefault()
            ?? throw new InvalidOperationException($"没有找到 MC {mcVersion} 对应的 NeoForge 版本。");
        return await _loaderService.InstallForgeOrNeoForgeClientAsync(
            minecraftDir, ServerCoreType.NeoForge, fullVersion, JavaPathBox.Text, progress);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(null);
    }

    /// <summary>
    /// 整活核心：把 installedVersionIds 里每一个版本目录（versions/&lt;id&gt;/）下的所有文件，
    /// 原样拷贝进同一个新建的"合装"版本目录（versions/multiloader-mix-&lt;mcVersion&gt;-&lt;时间戳&gt;/），
    /// 不做任何 json 解析/合并/mainClass 仲裁——纯粹的文件搬运。
    ///
    /// 文件名处理：每个源版本目录里通常至少有一个 "&lt;versionId&gt;.json"，如果不同加载器的版本目录
    /// 里恰好有同名的其它文件（比如都叫 xxx.jar），直接覆盖会丢失其中一份，所以除了每个版本目录里
    /// 那个跟目录同名的主 json 文件（原样保留文件名，方便一眼看出这是哪个加载器的）之外，其余文件
    /// 一律加上 "&lt;versionId&gt;__" 前缀再拷贝，避免不同加载器的同名文件互相覆盖。
    ///
    /// 这个目标目录本身大概率无法被启动器识别为可启动版本（缺少一个跟目录同名的主 json），这是刻意
    /// 的——"合装"这个概念在 Minecraft 版本文件格式下本来就不成立，这里只负责把文件堆过去，
    /// 能不能用、算不算一个合法版本，都不是这个方法要保证的事。
    /// </summary>
    private string MergeIntoOnePlateOfMixedStew(string minecraftDir, string mcVersion, List<string> installedVersionIds)
    {
        var mergedVersionId = $"multiloader-mix-{mcVersion}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var mergedDir = Path.Combine(minecraftDir, "versions", mergedVersionId);
        Directory.CreateDirectory(mergedDir);

        foreach (var sourceVersionId in installedVersionIds)
        {
            var sourceDir = Path.Combine(minecraftDir, "versions", sourceVersionId);
            if (!Directory.Exists(sourceDir)) continue;

            foreach (var sourceFile in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(sourceFile);
                // 跟源目录同名的主 json（比如 versions/fabric-loader-xxx/fabric-loader-xxx.json）
                // 原样保留文件名，方便肉眼辨认这是哪个加载器的；其余文件加前缀避免互相覆盖。
                var isMainJson = fileName.Equals($"{sourceVersionId}.json", StringComparison.OrdinalIgnoreCase);
                var destFileName = isMainJson ? fileName : $"{sourceVersionId}__{fileName}";
                var destPath = Path.Combine(mergedDir, destFileName);
                File.Copy(sourceFile, destPath, overwrite: true);
            }
        }

        return mergedVersionId;
    }
}
