using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace XCL2.App;

public partial class App : Application
{
    /// <summary>
    /// XCL2 的私有数据目录：启动器运行目录下的 "xcl2" 文件夹。
    /// 存放配置文件(config.json)、账户缓存(accounts.json)、日志、下载的 Java 等。
    /// </summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "xcl2");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 【新增】环境预检，缺失则弹窗引导后退出
        if (!CheckEnvironment()) return;

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(DataDir, "runtime")); // java
        Directory.CreateDirectory(Path.Combine(DataDir, "scripts")); // 导出的启动脚本

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "logs", "crash.log"),
                    $"[{DateTime.Now}] {args.Exception}\n\n");
            }
            catch { /* ignore */ }
            MessageBox.Show("发生未处理的异常：\n" + args.Exception.Message, "XCL2 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    /// <summary>
    /// 检查 .NET 8 和 WebView2 运行时（纯注册表检测，不触发任何崩溃）
    /// </summary>
    private bool CheckEnvironment()
    {
        // 1. 检查 .NET 8 Desktop Runtime
        using var netKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App");
        bool hasDotNet8 = false;
        if (netKey != null)
        {
            foreach (var name in netKey.GetValueNames())
            {
                if (name.StartsWith("8.0.", StringComparison.OrdinalIgnoreCase))
                {
                    hasDotNet8 = true;
                    break;
                }
            }
        }

        if (!hasDotNet8)
        {
            var result = MessageBox.Show(
                "检测到您的电脑未安装 .NET 8.0 桌面运行时。\n" +
                "XCL2 需要它才能运行。\n\n" +
                "是否立即前往微软官网下载？",
                "缺少 .NET 8 运行时",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0/runtime",
                    UseShellExecute = true
                });
            }
            return false;
        }

        // 2. 检查 WebView2 Runtime（仅提示，不阻止启动）
        using var wvKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
        using var wvKey64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
        bool hasWebView2 = (wvKey?.GetValue("pv") != null) || (wvKey64?.GetValue("pv") != null);

        if (!hasWebView2)
        {
            MessageBox.Show(
                "检测到您的电脑未安装 Microsoft Edge WebView2 运行库。\n" +
                "XCL2 的内嵌登录功能需要它，但其他功能仍可正常使用。\n\n" +
                "如需使用完整功能，请前往微软官网下载安装。",
                "缺少 WebView2 运行库",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        return true;
    }
}