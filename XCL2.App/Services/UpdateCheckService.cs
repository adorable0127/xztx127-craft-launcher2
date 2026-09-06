using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 启动器自身的自动更新检查/下载/替换服务。
///
/// ===== 发布形态（决定了下面的实现方式）=====
/// 项目发布的不是安装程序、也不是 zip 包，而是按架构区分的两个「单文件、不含运行时」的
/// exe（framework-dependent + PublishSingleFile，双击即可直接运行，仍然依赖用户机器上
/// 已装的 .NET8 Desktop Runtime）：
///   - xztx127 craft launcher2 x86(32位特供）不含运行时.exe
///   - xztx127 craft launcher2 x64 不含运行时.exe
/// 也就是说"更新"本质上就是"下载对应架构的新 exe，覆盖掉当前正在运行的这一个 exe 文件"，
/// 不需要解压、不需要按文件树做增量覆盖，比处理 zip 包简单很多。
///
/// ===== 整体流程 =====
/// 1) 启动几秒后（不卡启动流程）后台请求 GitHub Releases「latest」接口，跟当前程序集版本
///    比较大小。
/// 2) 有新版本 → 弹一个确认框（带更新日志），用户选"现在更新"才会继续下载，选"稍后"
///    本次直接结束，不写任何"已忽略/跳过"的标记。当前版本为 2.2.10；下次 GitHub 发布
///    2.2.11（或更高版本）时仍会正常提示，不会被"之前拒绝过"影响。
/// 3) 根据当前系统是 32 位还是 64 位，从 Release 附件里挑出对应架构的 exe 下载到临时目录，
///    在 xcl2/up-log/ 下写一条以时间戳命名的日志。
/// 4) 生成一个 .bat：等主程序进程真正退出 → 把当前正在跑的这个 exe 备份一份（"新旧互换"
///    里"旧"的那一半）→ 把刚下载的新 exe 复制过去、覆盖掉旧文件 → 按当前系统架构把文件
///    统一改名为标准发布名（x64:"xztx127 craft launcher2 x64 不含运行时.exe"；
///    x86:"xztx127 craft launcher2 x86(32位特供） 不含运行时.exe"）——注意这一步跟旧版本
///    行为不同：旧版本是"保留原文件名，不管用户之前叫它什么"，现在改成"更新完统一纠正
///    成标准命名"，即使用户之前把 exe 随手改了名字，更新一次之后也会变回标准名字
///    （代价是如果用户给这个 exe 建过桌面快捷方式/任务栏固定项，指向的还是旧文件名，
///    更新后会失效，需要重新固定一次——这是本次改动特意接受的取舍，不是遗漏）→
///    重新拉起程序（用改名后的新路径）→ 继续把过程追加写回同一份 up-log。
/// 5) 主程序侧只负责启动这个 bat（隐藏窗口）然后自己退出，真正的文件替换/改名动作完全
///    在主程序退出之后发生，避免"进程覆盖自己正在运行的 exe"这个 Windows 下办不到的操作。
///
/// ===== 为什么不做"跳过此版本" =====
/// 当前正式版是 2.2.10；下一次 GitHub Release 为 2.2.11（或更高版本）时要继续提示。
/// 只要不持久化任何"已拒绝/已忽略"的版本号，每次检查都是"当前版本 vs 服务端最新版本"
/// 的即时比较，这个需求就是默认行为，不需要额外写状态。
/// </summary>
public static class UpdateCheckService
{
    // 项目实际发布地址：https://github.com/adorable0127/xztx127-craft-launcher2/releases
    private const string RepoOwner = "adorable0127";
    private const string RepoName = "xztx127-craft-launcher2";
    private static readonly string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>up-log 目录：xcl2/up-log，每次真正触发一次更新流程（用户点了"现在更新"）
    /// 就会在这里生成一个以时间戳命名的日志文件，记录从检测到（若走完流程）重启的全过程。
    /// 仅仅"检查了一下但没有新版本"不写日志，避免每次启动都在这个目录里堆空文件。</summary>
    private static string UpLogDir => Path.Combine(App.DataDir, "up-log");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static UpdateCheckService()
    {
        // GitHub API 强制要求带 User-Agent，否则直接 403。
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher-UpdateChecker");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private sealed record GithubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

    private sealed record GithubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] List<GithubAsset>? Assets);

    /// <summary>供 App.xaml.cs 在主窗口显示之后调用：整个检查过程完全跑在后台线程，
    /// 任何环节出错都只记日志、绝不弹错误打扰用户，也绝不影响主流程。</summary>
    public static void CheckForUpdateInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // 让最耗资源的启动阶段（首帧渲染、账户刷新等）先跑完，更新检查不抢这个时间段。
                await Task.Delay(TimeSpan.FromSeconds(5));
                await RunCheckAsync();
            }
            catch (Exception ex)
            {
                LauncherLogService.AppendLine($"[自动更新] 检查更新时发生异常（已忽略，不影响正常使用）：{ex.Message}");
            }
        });
    }

    private static async Task RunCheckAsync()
    {
        var release = await FetchLatestReleaseAsync();
        if (release == null) return;

        var remoteVersion = ExtractVersion(release.TagName);
        var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        if (remoteVersion == null || remoteVersion <= localVersion) return;

        var asset = PickAssetForCurrentArch(release.Assets);
        if (asset == null)
        {
            LauncherLogService.AppendLine($"[自动更新] 发现新版本 {remoteVersion}，但 Release 里没有找到匹配当前系统架构（{(Environment.Is64BitOperatingSystem ? "x64" : "x86")}）的 exe 附件，跳过本次更新提示。");
            return;
        }

        var changelog = string.IsNullOrWhiteSpace(release.Body) ? "（作者未填写更新日志）" : release.Body!.Trim();
        if (changelog.Length > 600) changelog = changelog[..600] + "\n……（更新日志过长，已截断）";

        var confirmed = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            confirmed = MessageBoxDialog.ShowConfirm(
                $"发现新版本 v{FormatVersion(remoteVersion)}（当前 v{FormatVersion(localVersion)}）：\n\n{changelog}\n\n是否现在下载并更新？\n" +
                "（如果选择「否」，下次启动检测到更新版本时还会继续提示。）",
                "发现新版本");
        });

        if (!confirmed) return;

        await DownloadAndApplyUpdateAsync(remoteVersion, localVersion, asset, changelog);
    }

    private static async Task<GithubRelease?> FetchLatestReleaseAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseApi);
            if (!resp.IsSuccessStatusCode)
            {
                LauncherLogService.AppendLine($"[自动更新] 检查更新失败：HTTP {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GithubRelease>(json);
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine($"[自动更新] 检查更新失败（网络问题，静默忽略）：{ex.Message}");
            return null;
        }
    }

    /// <summary>tag_name 可能是 "v2.2.8" / "2.2.8" / "2.2.8-fix1" 这几种写法，
    /// 统一提取开头的 数字.数字[.数字[.数字]] 部分喂给 System.Version。</summary>
    private static Version? ExtractVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag, @"\d+(?:\.\d+){1,3}");
        if (!m.Success) return null;
        return Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>程序集版本通常是 2.2.10.0，而 Release tag 是 2.2.11。提示里去掉末尾无意义的 .0，
    /// 让当前版本稳定显示为 2.2.10、下一版稳定显示为 2.2.11。</summary>
    private static string FormatVersion(Version version)
    {
        if (version.Revision > 0) return version.ToString(4);
        if (version.Build >= 0) return version.ToString(3);
        return version.ToString(2);
    }

    /// <summary>
    /// 按当前系统是 32 位还是 64 位，从 assets 里挑对应的那个 exe。
    /// 附件命名固定形如："...x86(32位特供）不含运行时.exe" / "...x64...不含运行时.exe"，
    /// 只需要认文件名里的 "x86" / "x64" 关键字，不关心中文部分（避免全角括号、空格
    /// 之类的细节写法以后微调导致匹配失效）。64 位系统下如果找不到 x64 包，不会退回
    /// 去装 x86 包——32 位程序在 64 位系统上其实能跑，但"该给用户装对应位数的版本"
    /// 这件事不该由更新逻辑自作主张，找不到匹配的就跳过这次更新提示，比装错架构安全。
    /// </summary>
    private static GithubAsset? PickAssetForCurrentArch(List<GithubAsset>? assets)
    {
        if (assets == null || assets.Count == 0) return null;
        var exes = assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        var wantX64 = Environment.Is64BitOperatingSystem;
        var want = wantX64 ? "x64" : "x86";
        return exes.FirstOrDefault(a => a.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 更新完成后，无论更新前 exe 叫什么名字，统一按当前系统架构改成固定的标准文件名——
    /// 这样即使用户之前把文件改过名、或者装的是很久以前发布时用的旧命名规则，更新一次
    /// 之后就能统一到当前发布规范的命名上。两个文件名的括号故意是"半角(+全角）"混用，
    /// 跟仓库 Release 里实际发布的文件名保持字符级一致，不要"修正"成好看的全角/半角配对。
    /// </summary>
    private static string GetStandardExeName(bool wantX64) => wantX64
        ? "xztx127 craft launcher2 x64 不含运行时.exe"
        : "xztx127 craft launcher2 x86(32位特供） 不含运行时.exe";

    private static async Task DownloadAndApplyUpdateAsync(Version remoteVersion, Version localVersion, GithubAsset asset, string changelog)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(UpLogDir);
        var logPath = Path.Combine(UpLogDir, $"{timestamp}.log");

        void Log(string line)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n", Encoding.UTF8); }
            catch { /* 日志写失败不能影响更新流程本身 */ }
        }

        // 当前正在跑的这个 exe 的真实路径（不是拼出来的"程序目录\XCL2.exe"——万一用户
        // 把 exe 改过名字，MainModule.FileName 拿到的才是它实际的文件名和路径）。
        var currentExePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "XCL2.exe");

        var wantX64 = Environment.Is64BitOperatingSystem;
        var standardExeName = GetStandardExeName(wantX64);
        var targetExePath = Path.Combine(
            Path.GetDirectoryName(currentExePath) ?? AppContext.BaseDirectory,
            standardExeName);

        Log($"检测到新版本：v{FormatVersion(localVersion)} -> v{FormatVersion(remoteVersion)}（{(wantX64 ? "x64" : "x86")}）");
        Log($"更新包：{asset.Name}");
        Log($"当前程序文件：{currentExePath}");
        Log($"更新后将统一改名为：{targetExePath}");
        Log($"更新日志：\n{changelog}");

        var tempRoot = Path.Combine(App.DataDir, "_update_temp", FormatVersion(remoteVersion));

        ProgressDialog? progressDialog = null;
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                progressDialog = new ProgressDialog($"正在下载 v{FormatVersion(remoteVersion)}…");
                progressDialog.Show();
            });

            Log("开始下载更新包……");
            using var downloader = new GenericFileDownloadService();
            var progress = new Progress<ProgressInfo>(p =>
            {
                progressDialog?.Progress.Report(p);
            });
            // 下载下来统一存成 new.exe，跟原文件名无关——反正 bat 里是直接覆盖到
            // currentExePath，新包叫什么名字不重要。
            var newExePath = await downloader.DownloadAsync(asset.BrowserDownloadUrl, tempRoot, "new.exe", null, progress);
            Log($"下载完成：{newExePath}");

            var backupPath = Path.Combine(App.DataDir, "_backup",
                $"{Path.GetFileNameWithoutExtension(currentExePath)}_v{FormatVersion(localVersion)}_{timestamp}.exe");

            var batPath = Path.Combine(Path.GetTempPath(), $"xcl2_update_{timestamp}.bat");
            WriteUpdateBat(batPath, newExePath, currentExePath, targetExePath, backupPath, logPath, tempRoot, Environment.ProcessId);
            Log($"更新脚本已生成：{batPath}");
            Log($"旧版本备份到：{backupPath}");
            Log("即将关闭程序并交由脚本完成替换、改名与重启……");

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batPath}\"\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            Application.Current.Dispatcher.Invoke(() =>
            {
                progressDialog?.Close();
                Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            Log($"更新失败：{ex}");
            Application.Current.Dispatcher.Invoke(() =>
            {
                progressDialog?.Close();
                MessageBoxDialog.ShowError($"更新下载失败，本次先不更新：\n{ex.Message}\n\n下次启动会重新检查。", "更新失败");
            });
        }
    }

    /// <summary>
    /// 生成负责"等主程序退出 → 备份旧 exe → 用新 exe 覆盖 → 按架构统一改名 → 重启"的 bat。
    /// 只动 currentExePath 这一个文件（改名后变成 targetExePath），不碰程序目录下的
    /// 其它内容（.minecraft、下载缓存、xcl2 配置等用户数据完全不受影响）。
    /// 如果 targetExePath 跟 currentExePath 其实是同一个路径（用户本来就没改过文件名），
    /// 就不需要额外改名这一步，直接跳过。
    /// </summary>
    private static void WriteUpdateBat(string batPath, string newExePath, string currentExePath, string targetExePath,
        string backupPath, string logPath, string tempRoot, int pid)
    {
        var needRename = !string.Equals(
            Path.GetFullPath(currentExePath), Path.GetFullPath(targetExePath),
            StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"PID={pid}\"");
        sb.AppendLine($"set \"NEWEXE={newExePath}\"");
        sb.AppendLine($"set \"CUREXE={currentExePath}\"");
        sb.AppendLine($"set \"TARGETEXE={targetExePath}\"");
        sb.AppendLine($"set \"BACKUP={backupPath}\"");
        sb.AppendLine("for %%D in (\"%BACKUP%\") do set \"BACKUPDIR=%%~dpD\"");
        sb.AppendLine($"set \"LOG={logPath}\"");
        sb.AppendLine($"set \"TEMPROOT={tempRoot}\"");
        sb.AppendLine();
        sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 更新脚本已启动，等待主程序(PID=%PID%)退出...");
        sb.AppendLine(":waitloop");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 主程序已退出，开始备份旧版本...");
        sb.AppendLine("for %%D in (\"%BACKUP%\") do if not exist \"%%~dpD\" mkdir \"%%~dpD\" >nul 2>nul");
        sb.AppendLine("copy /y \"%CUREXE%\" \"%BACKUP%\" >nul 2>nul");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  >>\"%LOG%\" echo [%date% %time%] 旧版本备份失败，为避免丢失回退版本，本次更新已取消。");
        sb.AppendLine("  start \"\" \"%CUREXE%\"");
        sb.AppendLine("  rmdir /s /q \"%TEMPROOT%\" >nul 2>nul");
        sb.AppendLine("  (goto) 2>nul & del \"%~f0\"");
        sb.AppendLine("  exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 用新版本覆盖旧文件...");
        sb.AppendLine("copy /y \"%NEWEXE%\" \"%CUREXE%\" >nul");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  >>\"%LOG%\" echo [%date% %time%] 覆盖失败，尝试从备份还原...");
        sb.AppendLine("  copy /y \"%BACKUP%\" \"%CUREXE%\" >nul 2>nul");
        sb.AppendLine("  start \"\" \"%CUREXE%\"");
        sb.AppendLine("  rmdir /s /q \"%TEMPROOT%\" >nul 2>nul");
        sb.AppendLine("  (goto) 2>nul & del \"%~f0\"");
        sb.AppendLine("  exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 文件替换完成。");
        sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 清理 _backup 中的旧临时文件，只保留本次更新前版本...");
        sb.AppendLine("for %%F in (\"%BACKUPDIR%*\") do if /I not \"%%~fF\"==\"%BACKUP%\" del /f /q \"%%~fF\" >nul 2>nul");
        sb.AppendLine();

        if (needRename)
        {
            // 按架构统一改名：如果目标名字已经被别的文件占用（极少见，比如用户手动放了
            // 一个同名文件在旁边），先挪开备份，不直接覆盖，避免误删用户自己的文件。
            sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 按当前系统架构统一改名为标准文件名...");
            sb.AppendLine("if exist \"%TARGETEXE%\" if /I not \"%TARGETEXE%\"==\"%CUREXE%\" (");
            sb.AppendLine("  >>\"%LOG%\" echo [%date% %time%] 目标文件名已存在同名文件，先备份挪开...");
            sb.AppendLine("  move /y \"%TARGETEXE%\" \"%TARGETEXE%.old\" >nul 2>nul");
            sb.AppendLine(")");
            sb.AppendLine("move /y \"%CUREXE%\" \"%TARGETEXE%\" >nul 2>nul");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  >>\"%LOG%\" echo [%date% %time%] 改名失败，继续使用原文件名启动。");
            sb.AppendLine("  set \"TARGETEXE=%CUREXE%\"");
            sb.AppendLine(") else (");
            sb.AppendLine("  >>\"%LOG%\" echo [%date% %time%] 改名完成：%TARGETEXE%");
            sb.AppendLine(")");
            sb.AppendLine();
        }

        sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 重新启动程序...");
        sb.AppendLine("start \"\" \"%TARGETEXE%\"");
        sb.AppendLine("rmdir /s /q \"%TEMPROOT%\" >nul 2>nul");
        sb.AppendLine(">>\"%LOG%\" echo [%date% %time%] 更新流程结束。");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");

        // bat 开头已经 chcp 65001 切到 UTF-8 代码页，脚本文件本身也要用不带 BOM 的 UTF-8
        // 保存，两边代码页对上，日志里的中文才不会乱码。
        File.WriteAllText(batPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
