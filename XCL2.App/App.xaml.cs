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

        // 环境预检，缺失则弹窗引导后退出
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
    /// 全面检查 .NET 8 和 WebView2 运行时
    /// </summary>
    private bool CheckEnvironment()
    {
        var regPaths = new[]
        {
            @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App",
            @"SOFTWARE\dotnet\Setup\InstalledVersions\arm64\sharedfx\Microsoft.NETCore.App",
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x86\sharedfx\Microsoft.NETCore.App",
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\arm\sharedfx\Microsoft.NETCore.App"
        };

        bool hasDotNet8 = false;

        // 1. 扫描 HKLM (本机所有用户)
        foreach (var path in regPaths)
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (name.StartsWith("8.0.", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDotNet8 = true;
                        break;
                    }
                }
            }
            if (hasDotNet8) break;
        }

        // 2. 扫描 HKCU (当前用户，针对 Per-User 安装)
        if (!hasDotNet8)
        {
            foreach (var path in regPaths)
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key != null)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        if (name.StartsWith("8.0.", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDotNet8 = true;
                            break;
                        }
                    }
                }
                if (hasDotNet8) break;
            }
        }

        // 3. 终极兜底：调用 dotnet --list-runtimes
        if (!hasDotNet8)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                string output = proc?.StandardOutput.ReadToEnd() ?? "";
                hasDotNet8 = output.Contains("Microsoft.NETCore.App 8.0.");
            }
            catch { }
        }

        // .NET 8 缺失拦截
        if (!hasDotNet8)
        {
            var result = MessageBox.Show(
                "未检测到 .NET 8.0 桌面运行时。\nXCL2 需要它才能运行。\n\n是否前往微软官网下载？",
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

        // --- WebView2 检测 (仅影响内嵌登录) ---
        using var wvKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
        using var wvKey64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
        bool hasWebView2 = (wvKey?.GetValue("pv") != null) || (wvKey64?.GetValue("pv") != null);

        if (!hasWebView2)
        {
            MessageBox.Show(
                "未检测到 Microsoft Edge WebView2 运行库。\n" +
                "XCL2 的内嵌微软账号登录功能将无法使用。\n\n" +
                "如需登录正版账号，请前往微软官网下载安装后重启启动器。",
                "缺少 WebView2 运行库",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return true;
    }
}