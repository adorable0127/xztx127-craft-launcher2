using System.IO;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 实例列表每一行右侧⚙️图标二级菜单里"版本设置..."点开的设置弹窗，是实例设置的唯一入口
/// （原页面下方的 3/7 设置面板 PerVersionSettingsPanel 已随"版本设置统一从 ⚙️ 菜单进入"的
/// 需求移除）。设置内容跟原来的面板完全一致，字段读写目标（VersionJavaIdOverrides /
/// VersionIsolationOverrides 等）也都一致，任何一处以后要改字段名，需要同步改这边。
/// </summary>
public partial class InstanceSettingsDialog : OverlayDialogControl
{
    private readonly AppConfig _config;
    private readonly string _folderPath;
    private readonly GameVersion _version;
    private readonly MainWindow _owner;

    public InstanceSettingsDialog(MainWindow owner, string folderPath, GameVersion version)
    {
        _owner = owner;
        _config = owner.ConfigService.Config;
        _folderPath = folderPath;
        _version = version;
        InitializeComponent();

        TitleText.Text = $"「{version.Id}」的单独设置";
        LoadSettings();
    }

    private void LoadSettings()
    {
        var versionId = _version.Id;

        VersionJavaListComboDlg.Items.Clear();
        VersionJavaListComboDlg.Items.Add(new JavaListItem { Entry = null }); // "（不指定）"
        foreach (var j in _config.InstalledJavas) VersionJavaListComboDlg.Items.Add(new JavaListItem { Entry = j });
        var selectedJavaId = _config.VersionJavaIdOverrides.TryGetValue(versionId, out var jid) ? jid : null;
        VersionJavaListComboDlg.SelectedItem = VersionJavaListComboDlg.Items.Cast<JavaListItem>()
            .FirstOrDefault(i => i.Entry?.Id == selectedJavaId) ?? VersionJavaListComboDlg.Items[0];

        VersionJavaOverrideBoxDlg.Text = _config.VersionJavaOverrides.TryGetValue(versionId, out var javaOverride) && javaOverride > 0
            ? javaOverride.ToString()
            : "";

        SkipJavaMismatchPromptCheckDlg.IsChecked = _config.VersionSkipJavaMismatchPrompt.Contains(versionId);

        VersionIsolationOverrideCheckDlg.IsChecked = _config.VersionIsolationOverrides.TryGetValue(versionId, out var isolate)
            ? isolate
            : _config.IsolateVersionsByDefault;

        VersionResourcePackIsolationOverrideCheckDlg.IsChecked = _config.VersionResourcePackIsolationOverrides.TryGetValue(versionId, out var resIsolate)
            ? resIsolate
            : _config.IsolateResourcePacksByDefault;

        var hasAutoJoin = _config.VersionAutoJoinServer.TryGetValue(versionId, out var autoJoinAddr)
            && !string.IsNullOrWhiteSpace(autoJoinAddr);
        AutoJoinServerCheckDlg.IsChecked = hasAutoJoin;
        AutoJoinServerAddressBoxDlg.Text = hasAutoJoin ? autoJoinAddr : "";
        AutoJoinServerAddressBoxDlg.IsEnabled = hasAutoJoin;

        var versionDir = Path.Combine(_folderPath, "versions", versionId);
        var instance = InstanceConfigService.LoadOrCreateDefault(versionDir);
        InstanceMinMemoryBoxDlg.Text = instance.MinMemoryMb?.ToString() ?? "";
        InstanceMaxMemoryBoxDlg.Text = instance.MaxMemoryMb?.ToString() ?? "";
        InstanceCustomJvmArgsBoxDlg.Text = instance.CustomJvmArgs ?? _config.CustomJvmArgs ?? "";
        InstanceXclDirTextDlg.Text = InstanceConfigService.GetXclDir(versionDir);
    }

    private void AutoJoinServerCheckDlg_Changed(object sender, RoutedEventArgs e)
    {
        AutoJoinServerAddressBoxDlg.IsEnabled = AutoJoinServerCheckDlg.IsChecked == true;
    }

    /// <summary>把弹窗控件上的值写回配置并保存。</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var versionId = _version.Id;

        var selectedJava = (VersionJavaListComboDlg.SelectedItem as JavaListItem)?.Entry;
        if (selectedJava != null)
            _config.VersionJavaIdOverrides[versionId] = selectedJava.Id;
        else
            _config.VersionJavaIdOverrides.Remove(versionId);

        // "不再提示切换"这个开关只在确实选了某个具体 Java 时才有意义——没选具体 Java 的话
        // 启动时走的是自动搜索/下载逻辑，根本不会触发"手动指定的 Java 跟自动匹配不一致"这个
        // 判断分支，勾了也不会有任何实际效果，这里直接不落盘，避免配置文件里留一条死数据、
        // 以后用户换掉具体 Java 选择后这个开关却还残留着生效范围以外的旧状态。
        if (selectedJava != null && SkipJavaMismatchPromptCheckDlg.IsChecked == true)
            _config.VersionSkipJavaMismatchPrompt.Add(versionId);
        else
            _config.VersionSkipJavaMismatchPrompt.Remove(versionId);

        var javaText = VersionJavaOverrideBoxDlg.Text.Trim();
        if (javaText.Length == 0)
        {
            _config.VersionJavaOverrides.Remove(versionId);
        }
        else if (int.TryParse(javaText, out var javaMajor) && javaMajor is >= 8 and <= 99)
        {
            _config.VersionJavaOverrides[versionId] = javaMajor;
        }
        else
        {
            MessageBoxDialog.ShowWarning("Java 版本请填一个数字(如 8、17、21、25)，或留空使用自动匹配。",
                Loc.T("Str_Cs_Invalid_Input", "输入有误"));
            return;
        }

        _config.VersionIsolationOverrides[versionId] = VersionIsolationOverrideCheckDlg.IsChecked == true;
        _config.VersionResourcePackIsolationOverrides[versionId] = VersionResourcePackIsolationOverrideCheckDlg.IsChecked == true;

        if (AutoJoinServerCheckDlg.IsChecked == true)
        {
            var addr = AutoJoinServerAddressBoxDlg.Text.Trim();
            if (addr.Length == 0)
            {
                MessageBoxDialog.ShowWarning("已勾选「开启后进入某某某服务器」，请填写服务器地址（例如 play.example.com），或取消勾选。",
                    Loc.T("Str_Cs_Invalid_Input", "输入有误"));
                return;
            }
            _config.VersionAutoJoinServer[versionId] = addr;
        }
        else
        {
            _config.VersionAutoJoinServer.Remove(versionId);
        }

        int? instanceMin = ParseNullableMemory(InstanceMinMemoryBoxDlg.Text);
        int? instanceMax = ParseNullableMemory(InstanceMaxMemoryBoxDlg.Text);
        _config.SelectedVersionId = versionId;

        // 镜像写入实例目录：versions/<id>/xcl/settings.json（同 VersionSelectPage 那份逻辑）。
        var versionDir = Path.Combine(_folderPath, "versions", versionId);
        if (Directory.Exists(versionDir))
        {
            var instanceSettings = new InstanceSettings
            {
                IsolateVersion = VersionIsolationOverrideCheckDlg.IsChecked == true,
                JavaId = selectedJava?.Id,
                MinMemoryMb = instanceMin,
                MaxMemoryMb = instanceMax,
                CustomJvmArgs = string.IsNullOrWhiteSpace(InstanceCustomJvmArgsBoxDlg.Text) ? null : InstanceCustomJvmArgsBoxDlg.Text.Trim(),
                AutoJoinServerAddress = AutoJoinServerCheckDlg.IsChecked == true ? AutoJoinServerAddressBoxDlg.Text.Trim() : null
            };
            try
            {
                InstanceConfigService.Save(versionDir, instanceSettings);
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogFallback($"镜像写入实例设置失败：{versionDir}", ex);
            }
        }

        CloseWith(true);
    }

    private static int? ParseNullableMemory(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (int.TryParse(text, out var value) && value > 0) return value;
        throw new InvalidOperationException("实例内存必须是正整数或留空");
    }

    private async void BackupInstanceDlg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await InstanceBackupService.CreateBackupAsync(_folderPath, _version.Id, "手动备份");
            MessageBoxDialog.ShowSuccess($"实例备份已创建：\n{path}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"实例备份失败：{ex.Message}");
        }
    }

    private void OpenLoaderMaintenance_Click(object sender, RoutedEventArgs e)
    {
        // 修复两处问题：
        // 1）提示文案自相矛盾——原来的话术让用户"去版本选择页的加载器安装入口与 Mod 管理页
        //    执行"，但点击后实际调用的是 _owner.NavigateToSettings()，把人带去了完全不相关的
        //    设置页，说的和做的对不上。
        // 2）_owner 当时没有 NavigateToVersions 这样的公开方法可用（其它页面都有对应的
        //    NavigateToXxx，唯独版本选择页没有），所以这个"加载器维护"入口实际上根本走不到
        //    真正切换/互换加载器、转换版本的功能页面——现在 MainWindow 补上了
        //    NavigateToVersions()，这里改成跳转到真正提供加载器安装/切换入口的版本选择页，
        //    文案与实际跳转目标保持一致。
        var backup = InstanceBackupService.CreateBackupAsync(_folderPath, _version.Id, "加载器维护前备份");
        _ = backup.ContinueWith(_ => { });
        CloseWith(false);
        MessageBoxDialog.ShowInfo("已开始创建加载器维护前备份。即将跳转到版本选择页——加载器切换、互换请使用该版本卡片上的加载器安装入口，模组一键更新请前往 Mod 管理页执行；维护完成后建议同步检查并更新不兼容模组。", "加载器维护");
        _owner.NavigateToVersions();
    }

    /// <summary>导出这个版本的启动脚本到实例目录 versions/&lt;id&gt;/xcl/launch.bat。</summary>
    private void ExportInstanceLaunchScriptDlg_Click(object sender, RoutedEventArgs e)
    {
        var owner = _owner;
        var cfg = _config;
        var account = owner.ConfigService.GetSelectedAccount();
        if (account == null)
        {
            MessageBoxDialog.ShowInfo("请先在“账户管理”中登录或创建一个账户，再导出启动脚本。", Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var versionId = _version.Id;
        var javaId = cfg.VersionJavaIdOverrides.TryGetValue(versionId, out var vid) ? vid : cfg.SelectedJavaId;
        var javaPath = owner.ConfigService.ResolveJavaPath(javaId);
        if (javaPath == null)
        {
            var preferMajor = cfg.VersionJavaOverrides.TryGetValue(versionId, out var major) ? major : (int?)null;
            javaPath = new JavaService().FindJava(cfg.JavaPath, preferMajor, owner.ConfigService);
        }
        if (javaPath == null)
        {
            MessageBoxDialog.ShowWarning(
                "没有找到可用的 Java，无法导出启动脚本。请先在设置页下载/添加一个 Java，或正常启动一次这个版本。",
                Loc.T("Str_Cs_Error", "错误"));
            return;
        }

        var isolateVersion = cfg.VersionIsolationOverrides.TryGetValue(versionId, out var isolateOverride)
            ? isolateOverride
            : cfg.IsolateVersionsByDefault;
        var autoJoinServer = cfg.VersionAutoJoinServer.TryGetValue(versionId, out var joinAddr) && !string.IsNullOrWhiteSpace(joinAddr)
            ? joinAddr.Trim()
            : null;

        var options = new LauncherService.LaunchOptions
        {
            MinecraftDir = _folderPath,
            VersionId = versionId,
            JavaPath = javaPath,
            Account = account,
            MinMemoryMb = InstanceConfigService.TryLoad(Path.Combine(_folderPath, "versions", versionId))?.MinMemoryMb ?? cfg.MinMemoryMb,
            MaxMemoryMb = InstanceConfigService.TryLoad(Path.Combine(_folderPath, "versions", versionId))?.MaxMemoryMb ?? cfg.MaxMemoryMb,
            WindowWidth = cfg.WindowWidth,
            WindowHeight = cfg.WindowHeight,
            ShowConsoleWindow = cfg.EnableGameConsoleWindow,
            IsolateVersion = isolateVersion,
            GameLanguage = cfg.GameLanguage,
            VersionTypeLabel = cfg.GameVersionTypeLabel,
            CustomJvmArgs = InstanceConfigService.TryLoad(Path.Combine(_folderPath, "versions", versionId))?.CustomJvmArgs ?? (cfg.AdvancedMode ? cfg.CustomJvmArgs : null),
            PreLaunchCommand = cfg.PreLaunchCommand,
            AutoJoinServerAddress = autoJoinServer
        };

        try
        {
            var launcher = new LauncherService();
            var path = launcher.ExportLaunchScript(options);
            MessageBoxDialog.ShowSuccess($"启动脚本已导出到：\n{path}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"导出启动脚本失败：{ex.Message}");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
