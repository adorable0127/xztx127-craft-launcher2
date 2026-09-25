using System;
using System.Collections.Generic;
using System.Linq;

namespace XCL2.App.Services;

/// <summary>
/// 启动器命令行参数解析。
///
/// 支持的参数：
///   -help                                查看帮助并退出
///   -r --账户名 &lt;name&gt; --实例名称 &lt;name&gt;    用指定账户 + 指定实例（版本）直接启动游戏
///   -gui &lt;页面名&gt;                         启动后跳转到对应页面（复用现有的页面搜索关键字表）
///   --d --版本 &lt;mcVersion&gt; --加载器 &lt;loader&gt; 打开下载中心；--版本 留空时会弹出下载页面让用户自行选择，
///                                          填写时会自动跳到下载中心并按版本号筛选好，--加载器可选
///
/// 参数之间不区分大小写，"--参数 值" 和 "--参数=值" 两种写法都支持，方便脚本调用。
/// 只做“解析”，不做任何跳转/启动的具体动作——具体动作由 App.xaml.cs / MainWindow 消费本类结果后执行，
/// 这样解析逻辑本身可以脱离 WPF 独立测试。
/// </summary>
public static class CommandLineService
{
    public sealed class ParsedArgs
    {
        /// <summary>命中 -help，只需要打印帮助然后退出，不需要往下走任何其它逻辑。</summary>
        public bool ShowHelp;

        /// <summary>命中 -r，需要用指定账户+实例直接启动游戏。</summary>
        public bool LaunchGame;
        public string? AccountName;
        public string? InstanceName;

        /// <summary>命中 -gui &lt;page&gt;，需要跳转到的页面名（原样传入，交给 MainWindow 的页面关键字表去匹配）。</summary>
        public string? GuiPage;

        /// <summary>命中 --d，需要打开下载中心；Version 为空时弹出下载页面让用户手动选。</summary>
        public bool OpenDownload;
        public string? DownloadVersion;
        public string? DownloadLoader;

        /// <summary>用户显式输入 -l；先重启一次，再转换成隐藏的 --guest-session。</summary>
        public bool GuestModeRelaunchRequested;
        /// <summary>隐藏参数：当前这个进程就是一次性访客会话，不再二次重启。</summary>
        public bool EnterGuestMode;
        /// <summary>隐藏参数：开机 Run 项启动，用来应用“弹出/最小化/托盘”行为。</summary>
        public bool StartedByAutoStart;
        /// <summary>隐藏参数：重启接力时等待旧进程 PID 退出，避免误撞多开检测。</summary>
        public int WaitForPid;

        // ==================== 2.3.0 新增：20+ 个命令行参数 ====================
        // 以下全部是可选的“进阶/脚本化”参数，不影响原有 -help / -r / -gui / --d / -l 的行为，
        // 全部不填时启动器表现跟以前完全一样。

        /// <summary>--skip-update-check：本次启动跳过自动检查更新。</summary>
        public bool SkipUpdateCheck;
        /// <summary>--check-update-only：只检查一次更新（弹提示或打印结果），不进入主界面。</summary>
        public bool CheckUpdateOnly;
        /// <summary>--offline：本次强制以离线模式启动（不请求微软账户相关网络接口）。</summary>
        public bool ForceOffline;
        /// <summary>--portable：强制使用便携模式（配置读写启动器目录而非 AppData）。</summary>
        public bool ForcePortable;
        /// <summary>--no-tray：本次启动不创建托盘图标。</summary>
        public bool DisableTray;
        /// <summary>--minimized：启动后直接最小化到任务栏/托盘。</summary>
        public bool StartMinimized;
        /// <summary>--maximized：启动后直接最大化窗口。</summary>
        public bool StartMaximized;
        /// <summary>--reset-window：忽略上次记住的窗口位置/大小，使用默认值。</summary>
        public bool ResetWindowLayout;
        /// <summary>--width &lt;px&gt;：启动窗口宽度。</summary>
        public int? WindowWidth;
        /// <summary>--height &lt;px&gt;：启动窗口高度。</summary>
        public int? WindowHeight;
        /// <summary>--lang &lt;code&gt;：本次启动强制使用的界面语言（如 zh-CN / en-US），不写回配置。</summary>
        public string? LanguageOverride;
        /// <summary>--theme &lt;light|dark&gt;：本次启动强制使用的主题，不写回配置。</summary>
        public string? ThemeOverride;
        /// <summary>--zoom &lt;percent&gt;：本次启动强制使用的界面缩放百分比（如 125）。</summary>
        public int? UiZoomPercent;
        /// <summary>--no-animation：关闭界面动效（磁贴轮播/过渡动画等），低性能设备/远程桌面场景用。</summary>
        public bool DisableAnimations;
        /// <summary>--safe-mode：安全模式启动——跳过公告/AI 悬浮球/自定义主题等非核心增强功能，便于排障。</summary>
        public bool SafeMode;
        /// <summary>--debug-console：额外弹出一个调试控制台窗口，显示启动器内部日志输出。</summary>
        public bool ShowDebugConsole;
        public bool DebugAprilFools;
        /// <summary>--config-path &lt;path&gt;：使用指定路径的配置文件而非默认 AppData 路径（多实例/测试用）。</summary>
        public string? ConfigPathOverride;
        /// <summary>--instance-dir &lt;path&gt;：使用指定目录作为实例（.minecraft）根目录。</summary>
        public string? InstanceDirOverride;
        /// <summary>--java &lt;path&gt;：本次启动游戏强制使用指定 Java 可执行文件路径，跳过自动探测。</summary>
        public string? JavaPathOverride;
        /// <summary>--min-memory &lt;MB&gt; / --max-memory &lt;MB&gt;：本次启动游戏强制使用的 JVM 内存参数。</summary>
        public int? MinMemoryMb;
        public int? MaxMemoryMb;
        /// <summary>--jvm-args &lt;args&gt;：本次启动游戏额外追加的 JVM 参数（原样拼接在自动生成参数之后）。</summary>
        public string? ExtraJvmArgs;
        /// <summary>--game-args &lt;args&gt;：本次启动游戏额外追加的游戏参数。</summary>
        public string? ExtraGameArgs;
        /// <summary>--server &lt;ip:port&gt;：随 -r 一起使用时，游戏启动后自动尝试直连该服务器。</summary>
        public string? AutoJoinServer;
        /// <summary>--fullscreen：随 -r 一起使用时，游戏以全屏模式启动。</summary>
        public bool LaunchFullscreen;
        /// <summary>--clear-cache：启动前先清理下载缓存/临时文件，再继续后续动作。</summary>
        public bool ClearCacheOnStart;
        /// <summary>--export-logs &lt;path&gt;：启动后立即把日志/崩溃报告打包导出到指定路径，用于自动化收集。</summary>
        public string? ExportLogsPath;
        /// <summary>--no-single-instance：本次跳过单实例检测，允许多开（调试多账户场景用）。</summary>
        public bool DisableSingleInstance;
        /// <summary>--no-gpu-accel：关闭窗口硬件加速渲染（部分老旧显卡驱动兼容性问题时用）。</summary>
        public bool DisableGpuAcceleration;
        /// <summary>--verbose：本次启动打印更详细的启动器日志（不影响写盘日志级别，只影响调试控制台）。</summary>
        public bool VerboseLogging;

        /// <summary>隐藏参数 --just-updated：只由 UpdateCheckService 生成的重启脚本自动带上，
        /// 标记\"这次启动是自动更新流程重启回来的那一次\"，不写进帮助文本、也不建议用户手动使用。
        /// MainWindow 首帧后据此决定是否弹出「这次更新了什么」的内嵌变更日志弹窗，见
        /// MainWindow.ApplyStartupArgs 和 AppConfig.ShowUpdateChangelogPopup。</summary>
        public bool JustUpdated;

        /// <summary>是否解析出了任何一个需要 MainWindow 首帧后处理的有效动作。</summary>
        public bool HasAnyAction => ShowHelp || LaunchGame || GuiPage != null || OpenDownload || EnterGuestMode ||
            StartedByAutoStart || CheckUpdateOnly || ClearCacheOnStart || ExportLogsPath != null || JustUpdated;
    }

    public const string HelpText =
        "XCL2 启动器 命令行参数说明\n" +
        "\n" +
        "  -help                                  显示本帮助并退出\n" +
        "  -r --账户名 <账户名> --实例名称 <实例名>   使用指定账户与实例直接启动游戏\n" +
        "  -gui <页面名>                           启动后跳转到对应页面，例如：-gui 设置 / -gui 下载中心 / -gui 百宝箱\n" +
        "  --d --版本 <版本号> --加载器 <加载器>      打开下载中心；不填 --版本 时会直接弹出下载页面供手动选择，\n" +
        "                                          填写 --版本 后会自动跳转并按该版本号筛选，--加载器 可省略\n" +
        "  -l                                     以访客模式启动本次会话（会要求重新阅读并同意协议）\n" +
        "\n" +
        "进阶/脚本化参数（可选，不填时行为与以前完全一致）：\n" +
        "  --skip-update-check                    本次启动跳过自动检查更新\n" +
        "  --check-update-only                    只检查一次更新，不进入主界面\n" +
        "  --offline                              强制以离线模式启动\n" +
        "  --portable                             强制使用便携模式（配置存在启动器目录）\n" +
        "  --no-tray                              不创建托盘图标\n" +
        "  --minimized                            启动后直接最小化\n" +
        "  --maximized                            启动后直接最大化\n" +
        "  --reset-window                         重置窗口位置/大小为默认值\n" +
        "  --width <px> --height <px>             指定启动窗口大小\n" +
        "  --lang <代码>                           本次强制界面语言，例如 zh-CN / en-US\n" +
        "  --theme <light|dark>                   本次强制主题\n" +
        "  --zoom <百分比>                         本次强制界面缩放，例如 125\n" +
        "  --no-animation                         关闭界面动效\n" +
        "  --safe-mode                            安全模式启动，跳过公告/AI 悬浮球等增强功能\n" +
        "  --debug-console                        额外弹出调试控制台窗口\n" +
        "  --debug -yrj                          本次会话无视日期开启愚人节彩蛋（仍尊重 noyrj 禁用）\n" +
        "  --verbose                              调试控制台输出更详细日志\n" +
        "  --config-path <路径>                    使用指定配置文件路径\n" +
        "  --instance-dir <路径>                   使用指定实例（.minecraft）根目录\n" +
        "  --java <路径>                           启动游戏强制使用指定 Java 路径\n" +
        "  --min-memory <MB> --max-memory <MB>    启动游戏强制使用的 JVM 内存参数\n" +
        "  --jvm-args <参数>                       启动游戏额外追加的 JVM 参数\n" +
        "  --game-args <参数>                      启动游戏额外追加的游戏参数\n" +
        "  --server <ip:port>                     配合 -r 使用，启动后自动直连该服务器\n" +
        "  --fullscreen                           配合 -r 使用，游戏以全屏启动\n" +
        "  --clear-cache                          启动前先清理下载缓存/临时文件\n" +
        "  --export-logs <路径>                    启动后自动打包导出日志/崩溃报告到指定路径\n" +
        "  --no-single-instance                   跳过单实例检测，允许多开\n" +
        "  --no-gpu-accel                         关闭窗口硬件加速渲染\n" +
        "\n" +
        "示例：\n" +
        "  XCL2.App.exe -r --账户名 Steve --实例名称 \"1.20.1-Fabric\"\n" +
        "  XCL2.App.exe -gui 百宝箱\n" +
        "  XCL2.App.exe --d --版本 1.21.1 --加载器 Fabric\n" +
        "  XCL2.App.exe --d\n" +
        "  XCL2.App.exe -l\n" +
        "  XCL2.App.exe -r --账户名 Steve --实例名称 \"1.21.1-Fabric\" --server play.example.com:25565\n" +
        "  XCL2.App.exe --safe-mode --no-animation --minimized\n";

    public static ParsedArgs Parse(string[] args)
    {
        var result = new ParsedArgs();
        if (args == null || args.Length == 0) return result;

        // 统一小写比较用的一份拷贝，但取值时仍然用原始大小写（账户名/实例名/版本号可能大小写敏感）。
        string? GetOptionValue(int fromIndex, string optionName)
        {
            for (var i = fromIndex; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];

                // 兼容 --参数=值 写法
                var prefix = optionName + "=";
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return args[i][prefix.Length..];
            }

            // 单独处理最后一个元素是 --参数=值 的情况（上面循环 fromIndex..Length-2 会漏掉数组最后一位）
            if (args.Length > 0)
            {
                var last = args[^1];
                var prefix = optionName + "=";
                if (last.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return last[prefix.Length..];
            }

            return null;
        }

        result.DebugAprilFools = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase)) &&
                                 args.Any(a => a.Equals("-yrj", StringComparison.OrdinalIgnoreCase));

        foreach (var raw in args)
        {
            if (string.Equals(raw, "-help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "/?", StringComparison.OrdinalIgnoreCase))
            {
                result.ShowHelp = true;
            }
            else if (string.Equals(raw, "-r", StringComparison.OrdinalIgnoreCase))
            {
                result.LaunchGame = true;
            }
            else if (string.Equals(raw, "--d", StringComparison.OrdinalIgnoreCase))
            {
                result.OpenDownload = true;
            }
            else if (string.Equals(raw, "-l", StringComparison.OrdinalIgnoreCase))
            {
                result.GuestModeRelaunchRequested = true;
            }
            else if (string.Equals(raw, "--guest-session", StringComparison.OrdinalIgnoreCase))
            {
                result.EnterGuestMode = true;
            }
            else if (string.Equals(raw, "--autostart", StringComparison.OrdinalIgnoreCase))
            {
                result.StartedByAutoStart = true;
            }
            else if (string.Equals(raw, "--skip-update-check", StringComparison.OrdinalIgnoreCase))
            {
                result.SkipUpdateCheck = true;
            }
            else if (string.Equals(raw, "--check-update-only", StringComparison.OrdinalIgnoreCase))
            {
                result.CheckUpdateOnly = true;
            }
            else if (string.Equals(raw, "--offline", StringComparison.OrdinalIgnoreCase))
            {
                result.ForceOffline = true;
            }
            else if (string.Equals(raw, "--portable", StringComparison.OrdinalIgnoreCase))
            {
                result.ForcePortable = true;
            }
            else if (string.Equals(raw, "--no-tray", StringComparison.OrdinalIgnoreCase))
            {
                result.DisableTray = true;
            }
            else if (string.Equals(raw, "--minimized", StringComparison.OrdinalIgnoreCase))
            {
                result.StartMinimized = true;
            }
            else if (string.Equals(raw, "--maximized", StringComparison.OrdinalIgnoreCase))
            {
                result.StartMaximized = true;
            }
            else if (string.Equals(raw, "--reset-window", StringComparison.OrdinalIgnoreCase))
            {
                result.ResetWindowLayout = true;
            }
            else if (string.Equals(raw, "--no-animation", StringComparison.OrdinalIgnoreCase))
            {
                result.DisableAnimations = true;
            }
            else if (string.Equals(raw, "--safe-mode", StringComparison.OrdinalIgnoreCase))
            {
                result.SafeMode = true;
            }
            else if (string.Equals(raw, "--debug-console", StringComparison.OrdinalIgnoreCase))
            {
                result.ShowDebugConsole = true;
            }
            else if (string.Equals(raw, "--verbose", StringComparison.OrdinalIgnoreCase))
            {
                result.VerboseLogging = true;
            }
            else if (string.Equals(raw, "--just-updated", StringComparison.OrdinalIgnoreCase))
            {
                // 隐藏参数，不写进 HelpText：只有 UpdateCheckService 生成的重启脚本会带上它。
                result.JustUpdated = true;
            }
            else if (string.Equals(raw, "--fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                result.LaunchFullscreen = true;
            }
            else if (string.Equals(raw, "--clear-cache", StringComparison.OrdinalIgnoreCase))
            {
                result.ClearCacheOnStart = true;
            }
            else if (string.Equals(raw, "--no-single-instance", StringComparison.OrdinalIgnoreCase))
            {
                result.DisableSingleInstance = true;
            }
            else if (string.Equals(raw, "--no-gpu-accel", StringComparison.OrdinalIgnoreCase))
            {
                result.DisableGpuAcceleration = true;
            }
        }

        // -help 优先级最高：只要命中就不用再解析别的，调用方看到 ShowHelp=true 直接展示帮助并退出。
        if (result.ShowHelp) return result;

        if (result.LaunchGame)
        {
            result.AccountName = GetOptionValue(0, "--账户名") ?? GetOptionValue(0, "--account");
            result.InstanceName = GetOptionValue(0, "--实例名称") ?? GetOptionValue(0, "--instance");
        }

        if (result.OpenDownload)
        {
            result.DownloadVersion = GetOptionValue(0, "--版本") ?? GetOptionValue(0, "--version");
            result.DownloadLoader = GetOptionValue(0, "--加载器") ?? GetOptionValue(0, "--loader");
        }

        var waitPidText = GetOptionValue(0, "--wait-pid");
        if (int.TryParse(waitPidText, out var waitPid) && waitPid > 0)
            result.WaitForPid = waitPid;

        // 20+ 新参数里带值的那些，统一在这里取值/解析。
        result.LanguageOverride = GetOptionValue(0, "--lang");
        result.ThemeOverride = GetOptionValue(0, "--theme");
        result.ConfigPathOverride = GetOptionValue(0, "--config-path");
        result.InstanceDirOverride = GetOptionValue(0, "--instance-dir");
        result.JavaPathOverride = GetOptionValue(0, "--java");
        result.ExtraJvmArgs = GetOptionValue(0, "--jvm-args");
        result.ExtraGameArgs = GetOptionValue(0, "--game-args");
        result.AutoJoinServer = GetOptionValue(0, "--server");
        result.ExportLogsPath = GetOptionValue(0, "--export-logs");

        if (int.TryParse(GetOptionValue(0, "--width"), out var w) && w > 0) result.WindowWidth = w;
        if (int.TryParse(GetOptionValue(0, "--height"), out var h) && h > 0) result.WindowHeight = h;
        if (int.TryParse(GetOptionValue(0, "--zoom"), out var zoom) && zoom > 0) result.UiZoomPercent = zoom;
        if (int.TryParse(GetOptionValue(0, "--min-memory"), out var minMem) && minMem > 0) result.MinMemoryMb = minMem;
        if (int.TryParse(GetOptionValue(0, "--max-memory"), out var maxMem) && maxMem > 0) result.MaxMemoryMb = maxMem;

        // -gui 的值是紧跟在 -gui 后面的下一个参数（不是 --key value 形式，而是位置参数）。
        var guiIndex = Array.FindIndex(args, a => string.Equals(a, "-gui", StringComparison.OrdinalIgnoreCase));
        if (guiIndex >= 0 && guiIndex + 1 < args.Length)
        {
            result.GuiPage = args[guiIndex + 1];
        }

        return result;
    }
}
