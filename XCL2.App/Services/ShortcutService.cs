using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「创建快捷方式」：在桌面生成一个指向本启动器 exe 的 .lnk 快捷方式。
/// .NET 没有内置创建 .lnk 的托管 API，标准做法是通过 COM 互操作调用 Windows Script Host
/// 的 IWshRuntimeLibrary（"Windows Script Host Object Model"），这是市面主流做法，
/// 不需要额外 NuGet 包，只依赖 Windows 系统自带的 COM 组件。
/// </summary>
public static class ShortcutService
{
    /// <summary>在用户桌面创建一个指向当前启动器可执行文件的快捷方式。
    /// 返回创建成功的快捷方式路径；如果 Windows Script Host COM 组件不可用会抛异常，
    /// 调用方负责捕获并给用户友好提示（极端精简系统可能缺失该组件，但绝大多数
    /// Windows 桌面版都自带）。</summary>
    public static string CreateDesktopShortcut(string? shortcutName = null)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Path.Combine(AppContext.BaseDirectory, "XCL2.exe");

        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var name = string.IsNullOrWhiteSpace(shortcutName) ? "XCL2" : shortcutName.Trim();
        var lnkPath = Path.Combine(desktopDir, $"{name}.lnk");

        // 通过后期绑定(late-bound) COM 调用 WScript.Shell，避免项目额外引用
        // Windows Script Host 的 COM Interop 类型库（那样会牵扯到项目平台目标/嵌入互操作
        // 类型等额外配置），Type.GetTypeFromProgID + Activator.CreateInstance 这种反射式
        // 调用方式更轻量，是社区里创建 .lnk 快捷方式最常见的写法。
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                         ?? throw new InvalidOperationException("当前系统缺少 Windows Script Host 组件，无法创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.IconLocation = exePath;
                shortcut.Description = "XCL2 Minecraft 启动器";
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return lnkPath;
    }
}
