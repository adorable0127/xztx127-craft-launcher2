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
    public const string GitHubRepoUrl = "https://github.com/xztx127-craft/xcl2";

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

        MessageBox.Show(
            $"{friendlySummary}\n\n" +
            "详细的技术日志已经自动保存在本地，如果这个问题反复出现：\n" +
            "1. 打开「日志」页面，把完整日志内容发给你信任的专业人士（不要只发窗口截图，截图经常漏掉关键信息）；\n" +
            $"2. 或者前往 GitHub 提交反馈：{GitHubRepoUrl}",
            title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
}
