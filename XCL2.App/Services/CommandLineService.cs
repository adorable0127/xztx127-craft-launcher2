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

        /// <summary>是否解析出了任何一个需要 MainWindow 首帧后处理的有效动作。</summary>
        public bool HasAnyAction => ShowHelp || LaunchGame || GuiPage != null || OpenDownload || EnterGuestMode || StartedByAutoStart;
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
        "示例：\n" +
        "  XCL2.App.exe -r --账户名 Steve --实例名称 \"1.20.1-Fabric\"\n" +
        "  XCL2.App.exe -gui 百宝箱\n" +
        "  XCL2.App.exe --d --版本 1.21.1 --加载器 Fabric\n" +
        "  XCL2.App.exe --d\n" +
        "  XCL2.App.exe -l\n";

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

        // -gui 的值是紧跟在 -gui 后面的下一个参数（不是 --key value 形式，而是位置参数）。
        var guiIndex = Array.FindIndex(args, a => string.Equals(a, "-gui", StringComparison.OrdinalIgnoreCase));
        if (guiIndex >= 0 && guiIndex + 1 < args.Length)
        {
            result.GuiPage = args[guiIndex + 1];
        }

        return result;
    }
}
