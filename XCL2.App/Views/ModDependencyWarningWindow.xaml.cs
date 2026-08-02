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
public partial class ModDependencyWarningWindow : Window
{
    private readonly string _gameDir;
    private readonly LocalModService _localModService;
    private readonly List<LocalModInfo> _allEnabledMods;

    /// <summary>用户点了"我知道了，仍要继续"确认要跳过警告继续启动。</summary>
    public bool UserChoseToContinue { get; private set; }

    public ModDependencyWarningWindow(ModDependencyAnalysisResult analysis, string gameDir,
        List<LocalModInfo> allEnabledMods)
    {
        InitializeComponent();
        _gameDir = gameDir;
        _localModService = new LocalModService();
        _allEnabledMods = allEnabledMods;

        MissingList.ItemsSource = analysis.MissingDependencies
            .Select(m => new MissingDependencyDisplay(m))
            .ToList();
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
            ErrorPresenter.ShowFriendlyError("打不开日志文件夹，可以手动在游戏目录里找 xcl2/logs。",
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
        DialogResult = true;
        Close();
    }

    private async void DownloadDependency_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MissingDependencyDisplay item } || item.Info.ModrinthSlug == null) return;

        var progressWin = new ProgressDialog($"正在下载 {item.DisplayName} ...");
        progressWin.Show();
        try
        {
            var modrinth = new ModrinthService();
            // 直接按项目 slug 拉版本列表，不限定游戏版本/加载器——这里没有可靠的方式知道用户
            // 当前选中的具体 MC 版本号（这个窗口不持有 GameVersion 上下文），保守起见拿最新版本，
            // 装完之后如果版本不匹配，加载器本身也会在启动时给出明确的版本不兼容报错，
            // 不会是"完全没反应"的静默失败。
            var versions = await modrinth.GetVersionsAsync(item.Info.ModrinthSlug!, gameVersion: null);
            var version = versions.FirstOrDefault();
            if (version == null)
            {
                progressWin.Close();
                MessageBox.Show($"没有在 Modrinth 上找到 {item.DisplayName} 的可用版本，请手动搜索安装。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var progress = new Progress<string>(msg =>
                progressWin.Progress.Report(new ProgressInfo("下载前置模组", 0, 1, msg)));
            await modrinth.DownloadResourceAsync(_gameDir, Models.ModrinthResourceType.Mod, version, progress);
            progressWin.Close();
            MessageBox.Show($"{item.DisplayName} 已下载安装完成，重新扫描一次 Mod 列表就能看到。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var confirm = MessageBox.Show($"确定要删除以下 {targets.Count} 个 Mod 吗？此操作无法撤销。\n\n{names}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var failed = new List<string>();
        foreach (var t in targets)
        {
            try { _localModService.Delete(t.FilePath); }
            catch { failed.Add(t.DisplayName); }
        }

        if (failed.Count > 0)
            MessageBox.Show($"以下 Mod 删除失败：{string.Join("、", failed)}", "部分失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        DialogResult = true; // 让调用方知道列表状态变了，需要刷新
        Close();
    }
}

/// <summary>MissingDependency 的展示包装，补充 XAML 绑定用的拼接字段。</summary>
public class MissingDependencyDisplay
{
    public MissingDependency Info { get; }
    public string DisplayName => Info.DisplayName;
    public string RequiredByText => string.Join("、", Info.RequiredByModNames);
    public bool CanDownload => Info.ModrinthSlug != null;

    public string VersionHint => Info.VersionRange == "*" || string.IsNullOrWhiteSpace(Info.VersionRange)
        ? ""
        : $"版本要求：{Info.VersionRange}";

    public MissingDependencyDisplay(MissingDependency info) => Info = info;
}
