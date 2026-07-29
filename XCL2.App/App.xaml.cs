/// <summary>
/// 全面检查 .NET 8 Desktop Runtime（扫描所有可能的注册表位置 + 命令行兜底）
/// </summary>
private bool CheckEnvironment()
{
    // 定义所有可能的注册表检测路径
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

    // 2. 如果 HKLM 没找到，扫描 HKCU (当前用户，针对 Per-User 安装)
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

    // 3. 终极兜底：调用 dotnet --list-runtimes (能检测到 Portable/手动解压版)
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
            // 输出格式如: Microsoft.NETCore.App 8.0.x [...]
            hasDotNet8 = output.Contains("Microsoft.NETCore.App 8.0.");
        }
        catch { /* dotnet 命令不存在或执行失败，忽略 */ }
    }

    if (!hasDotNet8)
    {
        var result = MessageBox.Show(
            "未检测到 .NET 8.0 桌面运行时。\n" +
            "XCL2 需要它才能运行。\n\n" +
            "是否前往微软官网下载？",
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

    // WebView2 检测保持不变（它的路径相对固定）
    using var wvKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
    using var wvKey64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
    bool hasWebView2 = (wvKey?.GetValue("pv") != null) || (wvKey64?.GetValue("pv") != null);

    if (!hasWebView2)
    {
        MessageBox.Show(
            "未检测到 Microsoft Edge WebView2 运行库。\n" +
            "登录和模组搜索功能将不可用，但其他功能正常。\n\n" +
            "如需完整功能，请前往微软官网下载安装。",
            "缺少 WebView2",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    return true;
}