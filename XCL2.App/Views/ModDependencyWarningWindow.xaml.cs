using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 交接文档需求："安装前置模组分析"——例如钠(Sodium)没装 Fabric API 时，提示"发现了问题，
/// 解决方法可能可以解决问题：Sodium 无法正确加载，因为缺少前置模组 Fabric API（某版本，
/// 没有明确要求的不用写），只有在安装后才可以正常启动"，可选删除 Sodium 或下载前置，
/// 并提供"查看日志"/"提交问题报告"两个引导按钮。
///
/// 这里做成通用的"缺失依赖列表"展示，而不是写死 Sodium 这一个特例——分析逻辑
/// (ModDependencyAnalysisService)本来就是通用扫描 fabric.mod.json depends 字段，
/// Sodium 缺 Fabric API 只是最常见的一个例子，不代表只处理这一种情况。
/// </summary>
public partial class ModDependencyWarningWindow : OverlayDialogControl
{
    private readonly string _gameDir;
    private readonly LocalModService _localModService;
    private readonly List<LocalModInfo> _allEnabledMods;
    private readonly string? _mcVersion;
    private readonly string _loader;
    private List<MissingDependencyDisplay> _items = new();

    /// <summary>窗口里下载了前置或删除了 mod 之后触发，调用方据此立即刷新模组列表。</summary>
    public event EventHandler? ModsChanged;

    /// <summary>用户点了"我知道了，仍要继续"确认要跳过警告继续启动。</summary>
    public bool UserChoseToContinue { get; private set; }

    /// <param name="mcVersion">当前实例的 Minecraft 版本，用来只下载适配这个游戏版本的前置。</param>
    /// <param name="loader">当前实例的加载器（Fabric/Forge/NeoForge/Quilt）。依赖分析目前只读 fabric.mod.json，
    /// 所以没传时按 Fabric 处理。前置必须跟加载器一致，否则下载下来也加载不了。</param>
    public ModDependencyWarningWindow(ModDependencyAnalysisResult analysis, string gameDir,
        List<LocalModInfo> allEnabledMods, string? mcVersion = null, string? loader = null)
    {
        InitializeComponent();
        _gameDir = gameDir;
        _localModService = new LocalModService();
        _allEnabledMods = allEnabledMods;
        _mcVersion = string.IsNullOrWhiteSpace(mcVersion) ? null : mcVersion;
        _loader = string.IsNullOrWhiteSpace(loader) ? "Fabric" : loader!;

        _items = analysis.MissingDependencies
            .Select(m => new MissingDependencyDisplay(m))
            .ToList();
        MissingList.ItemsSource = _items;
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        // 需求原文"查看日志，到处错误报告"（应为"提交错误报告"的笔误）——直接打开
        // crash.log 所在文件夹，跟 LogsPage 保持一致的"打开文件所在位置"体验，
        // 而不是在弹窗里再塞一个日志文本框（那样窗口会变得很拥挤，LogsPage 已经有更完整的查看功能）。
        try
        {
            var logDir = Path.Combine(App.DataDir, "logs");
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError(Loc.T("Str_Cs_Couldn_T_Open_The_Log_Folder_You_Can_Fin", "打不开日志文件夹，可以手动在游戏目录里找 xcl2/logs。"),
                ex.ToString(), "打开日志失败");
        }
    }

    private void SubmitReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"{ErrorPresenter.GitHubRepoUrl}/issues/new") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError($"打不开浏览器，可以手动前往 {ErrorPresenter.GitHubRepoUrl} 提交反馈。",
                ex.ToString(), "打开反馈页面失败");
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        UserChoseToContinue = true;
        CloseWith(true);
    }

    private async void DownloadDependency_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MissingDependencyDisplay item }) return;

        var loaderLabel = _loader + (_mcVersion != null ? $" {_mcVersion}" : "");
        var progressWin = new ProgressDialog($"正在下载 {item.DisplayName} ...");
        progressWin.Show();
        try
        {
            var modrinth = new ModrinthService();

            // 1) 先确定 Modrinth 项目：内置映射表命中就直接用，没命中就按 mod id 去 Modrinth 解析
            //    （只接受名字/slug 完全一致的结果，避免装错同名不同物的 mod）。
            var slug = item.Info.ModrinthSlug
                       ?? await modrinth.ResolveModProjectSlugAsync(item.Info.DependencyModId, _loader, _mcVersion);
            if (slug == null)
            {
                progressWin.Close();
                MessageBoxDialog.ShowWarning(
                    $"没能在 Modrinth 上找到适配 {loaderLabel} 的「{item.DisplayName}」，请到 Modrinth 手动搜索（模组 ID：{item.Info.DependencyModId}）。", "提示");
                return;
            }

            // 2) 按「加载器 + 游戏版本」过滤版本列表。以前这里不传任何过滤条件、直接取列表第一项，
            //    所以 Fabric 的实例也可能拿到 NeoForge/Forge 的前置，装上必崩。
            //    现在找不到匹配的就明确告诉用户，绝不退回去下一个别的加载器的文件。
            var versions = await modrinth.GetVersionsForLoaderAsync(slug, _mcVersion, _loader);
            // 优先正式版，其次 beta/alpha（列表本身已按发布时间倒序）。
            var version = versions.FirstOrDefault(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
                          ?? versions.FirstOrDefault();
            if (version == null)
            {
                progressWin.Close();
                MessageBoxDialog.ShowWarning(
                    $"Modrinth 上没有 {item.DisplayName} 适配 {loaderLabel} 的版本，请换个游戏版本，或手动搜索其它来源。", "提示");
                return;
            }

            var progress = new Progress<string>(msg =>
                progressWin.Progress.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Dependencies", "下载前置模组"), 0, 1, msg)));
            await modrinth.DownloadResourceAsync(_gameDir, Models.ModrinthResourceType.Mod, version, progress);
            progressWin.Close();

            // 下载完立刻通知调用方刷新模组列表，并把已解决的这一项从警告里去掉。
            ModsChanged?.Invoke(this, EventArgs.Empty);
            _items = _items.Where(i => !ReferenceEquals(i, item)).ToList();
            MissingList.ItemsSource = _items;
            MessageBoxDialog.ShowInfo($"{item.DisplayName}（{version.VersionNumber}，{string.Join("/", version.Loaders)}）已下载安装完成。", "完成");
            if (_items.Count == 0) CloseWith(true);
        }
        catch (Exception ex)
        {
            progressWin.Close();
            ErrorPresenter.ShowFriendlyError($"下载 {item.DisplayName} 失败，可能是网络问题，请检查网络后重试。",
                ex.ToString(), "下载前置模组失败");
        }
    }

    private void DeleteDependents_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MissingDependencyDisplay item }) return;

        var targets = _allEnabledMods.Where(m => item.Info.RequiredByModNames.Contains(m.DisplayName)).ToList();
        if (targets.Count == 0) return;

        var names = string.Join("、", targets.Select(t => t.DisplayName));
        var confirm = MessageBoxDialog.ShowConfirm($"确定要删除以下 {targets.Count} 个 Mod 吗？此操作无法撤销。\n\n{names}", "确认删除");
        if (!confirm) return;   // ShowConfirm 返回 bool（true=用户点了"是"），不再是 MessageBoxResult

        var failed = new List<string>();
        foreach (var t in targets)
        {
            try { _localModService.Delete(t.FilePath); }
            catch { failed.Add(t.DisplayName); }
        }

        if (failed.Count > 0)
            MessageBoxDialog.ShowWarning($"以下 Mod 删除失败：{string.Join("、", failed)}", "部分失败");

        ModsChanged?.Invoke(this, EventArgs.Empty);

        CloseWith(true); // 让调用方知道列表状态变了，需要刷新
        CloseWith(null);
    }
}

/// <summary>MissingDependency 的展示包装，补充 XAML 绑定用的拼接字段。</summary>
public class MissingDependencyDisplay
{
    public MissingDependency Info { get; }
    public string DisplayName => Info.DisplayName;
    public string RequiredByText => string.Join("、", Info.RequiredByModNames);
    /// <summary>内置映射表里没有的前置，点击下载时会实时去 Modrinth 按 mod id 解析，
    /// 所以按钮始终可点；解析不到会给出明确提示而不是静默失败。</summary>
    public bool CanDownload => true;

    public string VersionHint => Info.VersionRange == "*" || string.IsNullOrWhiteSpace(Info.VersionRange)
        ? ""
        : $"版本要求：{Info.VersionRange}";

    public MissingDependencyDisplay(MissingDependency info) => Info = info;
}
