using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace XCL2.App.Services;

/// <summary>
/// 内嵌登录(MicrosoftLoginWindow)用的 WebView2 Runtime 检测。
///
/// 背景：之前 WebView2 控件是跟主界面一起初始化/加载的，就算用户从来不点"内嵌登录"，
/// 这部分运行时开销也白白付出了；而且真正没装 WebView2 Runtime 的机器上，只有在
/// 用户点了"内嵌登录"、控件真正尝试 EnsureCoreWebView2Async() 失败之后才能知道，
/// 体验上是先看到一个空白/卡住的窗口，再看错误文字，容易让人以为程序卡死。
///
/// 现在的流程：只有点击"内嵌登录"这个按钮时才会走到这里——WebView2 相关类型
/// (Microsoft.Web.WebView2.*) 不会因为程序启动、打开主界面就被加载，真正做到
/// "不跟主界面一起加载"。点击后：
///   1) 先用 CoreWebView2Environment.GetAvailableBrowserVersionString() 探测本机是否
///      已经装了 WebView2 Runtime（这是官方 API，比自己翻注册表判断更准，能覆盖
///      "Evergreen 版本/Fixed Version/企业策略指定路径"等各种安装方式，避免自己写的
///      注册表检测出现误判——比如只看默认注册表路径会漏判"通过策略指定了别的安装位置"
///      的情况）。
///   2) 探测失败(意味着真的没装，或者装了但已损坏)时，不再尝试打开内嵌登录窗口，
///      而是直接提示用户去下载 WebView2 Runtime 或改用浏览器登录，避免打开一个
///      注定会失败的空窗口。
/// </summary>
public static class WebView2RuntimeDetector
{
    /// <summary>官方 WebView2 Runtime 独立安装包下载地址（Evergreen Bootstrapper）。</summary>
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>
    /// 探测本机是否已安装可用的 WebView2 Runtime。
    /// 优先用官方 SDK 提供的 <see cref="CoreWebView2Environment.GetAvailableBrowserVersionString"/>，
    /// 这个调用本身很轻量(只读注册表/文件系统探测版本号，不会启动浏览器进程、不会创建窗口)，
    /// 所以可以放心在"点击内嵌登录"这个时机同步调用，不会有明显卡顿。
    /// 万一将来 SDK 行为有变化导致这个调用本身抛异常，兜底再手动查一次常见注册表路径，
    /// 尽量不要出现"运行时明明装了、却被误判为没装"的情况。
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrEmpty(version)) return true;
        }
        catch
        {
            // 忽略：SDK 内部探测失败不代表运行时一定没装，继续走下面的注册表兜底检测。
        }

        return HasRegistryEvidence();
    }

    /// <summary>
    /// 兜底：直接查 WebView2 Runtime 安装时会写入的注册表位置（Evergreen 版安装器的标准落点）。
    /// 同时查 64 位和 32 位视图，以及 HKLM(系统级安装)和 HKCU(用户级安装，部分环境下企业策略
    /// 会限定只能装到用户目录)两个位置，尽量覆盖不同安装方式，减少误判"没装"的概率。
    /// </summary>
    private static bool HasRegistryEvidence()
    {
        // {F3017226-FE2A-4295-8BDF-00C3A9A7E4C5} 是 WebView2 Runtime 的固定 ClientState GUID，
        // 微软官方文档里给出的检测方式就是查这个键下面是否有 pv (版本号) 值。
        const string clientStateSubKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        var roots = new (RegistryKey Hive, RegistryView View)[]
        {
            (Registry.LocalMachine, RegistryView.Registry64),
            (Registry.LocalMachine, RegistryView.Registry32),
            (Registry.CurrentUser, RegistryView.Registry64),
            (Registry.CurrentUser, RegistryView.Registry32),
        };

        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(
                    hive == Registry.LocalMachine ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser, view);
                using var key = baseKey.OpenSubKey(clientStateSubKey);
                var pv = key?.GetValue("pv") as string;
                if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return true;
            }
            catch
            {
                // 单个视图/权限问题导致的异常不影响继续检查其他位置。
            }
        }

        return false;
    }
}
