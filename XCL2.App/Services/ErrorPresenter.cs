using System.IO;
using System.Windows;

namespace XCL2.App.Services;

/// <summary>
/// 统一的"用户友好错误提示"辅助类。
///
/// 需求背景：之前网络/登录相关的报错弹窗会直接把 HTTP 状态码、原始响应体这些工程细节
/// 糊在 MessageBox 里给用户看（比如"HTTP 404: {"error":"invalid_grant"...}"），
/// 对小白用户完全没有意义，只会增加恐慌和困惑。现在改为：弹窗里只显示一句人话概括
/// （"网络请求失败，请检查网络连接后重试"之类），完整的技术细节（状态码、响应体、
/// 异常堆栈）只写进 xcl2/logs/crash.log，并引导用户"把完整日志文件发给可信的专业人士"，
/// 而不是截图窗口——截图经常漏掉关键信息（比如日志文件里更早的报错行），完整日志文件
/// 才能让人真正帮上忙。
/// </summary>
public static class ErrorPresenter
{
    /// <summary>本项目的 GitHub 仓库地址，日志/问题反馈的默认落脚点。</summary>
    public const string GitHubRepoUrl = "https://github.com/adorable0127/xztx127-craft-launcher2";

    /// <summary>
    /// 显示一个用户友好的错误弹窗：只给出场景化的中文概括（不含状态码/异常类型名），
    /// 并统一引导"完整日志已经记录，如果没解决，请把日志文件发给可信的专业人士，
    /// 或前往 GitHub 提交 issue"，同时把技术细节完整写入 crash.log 供后续排查。
    /// </summary>
    /// <param name="friendlySummary">一句人话概括，例如"登录失败，可能是网络连接问题"。</param>
    /// <param name="technicalDetail">完整技术细节（异常信息、堆栈、HTTP 响应体等），只写日志不弹窗。</param>
    /// <param name="title">弹窗标题，默认"出了点问题"。</param>
    public static void ShowFriendlyError(string friendlySummary, string technicalDetail, string title = "出了点问题")
    {
        LogTechnicalDetail(technicalDetail);

        // 普通模式除了“发生了什么”，再补一句不带工程术语的类比/下一步，降低纯错误码式提示的理解门槛。
        // 高手模式保持原来的紧凑描述，避免有经验的用户每次都被额外科普打断。
        if (ConfigService.Active?.Config.AdvancedMode != true)
            friendlySummary = AddPlainLanguageContext(friendlySummary);

        // 改用进程内 Overlay 弹窗（Views.MessageBoxDialog），不再是系统原生 MessageBox——
        // 原生 MessageBox 是 Win32 对话框，样式跟启动器自己的皮肤系统完全脱节，错误提示
        // 又是玩家最容易频繁看到的弹窗类型之一，所以这一类优先迁移。见
        // Views/MessageBoxDialog.xaml.cs 顶部注释。
        Views.MessageBoxDialog.ShowError(
            $"{friendlySummary}\n\n" +
            "详细的技术日志已经自动保存在本地，如果这个问题反复出现：\n" +
            "1. 打开「日志」页面，把完整日志内容发给你信任的专业人士（不要只发窗口截图，截图经常漏掉关键信息）；\n" +
            $"2. 或者前往 GitHub 提交反馈：{GitHubRepoUrl}",
            title);
    }


    private static string AddPlainLanguageContext(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return summary;
        var text = summary.ToLowerInvariant();

        if (text.Contains("java"))
            return summary + "\n\nJava 是这个游戏的心脏。你可以理解为：没有合适的 Java，游戏就只是一层空壳，操作系统无法按 Minecraft 需要的方式运行它。";
        if (text.Contains("内存") || text.Contains("memory") || text.Contains("outofmemory"))
            return summary + "\n\n可以把内存理解成游戏临时工作的桌面：桌面太小，资源和 Mod 放不下就会启动失败或崩溃。可以先到设置里检查内存分配。";
        if (text.Contains("网络") || text.Contains("http") || text.Contains("连接") || text.Contains("下载"))
            return summary + "\n\n这通常表示启动器暂时拿不到远端文件，不等于你的游戏文件一定损坏。可以先检查网络，再尝试切换下载源后重试。";
        if (text.Contains("mod") || text.Contains("模组") || text.Contains("加载器"))
            return summary + "\n\nMod 和加载器像一组必须对得上的齿轮：Minecraft 版本、加载器类型或依赖少一个不匹配，都可能让游戏停在启动阶段。";
        if (text.Contains("权限") || text.Contains("拒绝访问") || text.Contains("access"))
            return summary + "\n\n这通常不是文件本身坏了，而是 Windows 没允许当前进程修改那个位置。可以检查目录是否只读、是否被其他程序占用，以及当前账户是否有写入权限。";
        if (text.Contains("文件") || text.Contains("目录") || text.Contains("路径"))
            return summary + "\n\n可以把它理解为启动器按记录的地址去找文件，但那个地址现在不可用。检查文件是否被移动、删除或改名通常能更快定位问题。";

        return summary;
    }

    /// <summary>把技术细节追加写入 xcl2/logs/crash.log，静默失败（写日志本身不应该再抛出新异常打断主流程）。</summary>
    public static void LogTechnicalDetail(string technicalDetail)
    {
        try
        {
            var logDir = Path.Combine(App.DataDir, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "crash.log"),
                $"[{DateTime.Now}] {technicalDetail}\n\n");
        }
        catch { /* 写日志失败不应该影响主流程，忽略 */ }
    }

    /// <summary>
    /// 记录那些为了不中断主流程而被兜底吞掉的异常。调用方继续按原逻辑降级/忽略，
    /// 但排查问题时能在 crash.log 和会话日志里看到发生过什么。
    /// </summary>
    public static void LogFallback(string context, Exception? ex = null)
    {
        var detail = ex == null
            ? $"[兜底日志] {context}"
            : $"[兜底日志] {context}\n{ex}";

        LogTechnicalDetail(detail);
        try
        {
            LauncherLogService.AppendLine(ex == null
                ? $"[兜底] {context}"
                : $"[兜底] {context}: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // 会话日志自身不可用时不能影响原有兜底路径。
        }
    }
}
