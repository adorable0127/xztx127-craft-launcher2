using Microsoft.Win32;

namespace XCL2.App.Services;

/// <summary>
/// 开机自启动：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，不需要管理员权限，
/// 只影响当前登录用户（不是全机器级的 HKLM，避免安装/卸载时权限提示，也符合"个人启动器"
/// 这种应用场景——没必要影响同一台机器上的其它用户账户）。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "XCL2";

    /// <summary>把注册表里的自启动项同步成跟 enabled 参数一致的状态。
    /// 每次启动器启动、以及用户在设置页切换这个开关时都应该调用一次，保证配置文件里
    /// 记的状态和注册表里实际生效的状态不会跑偏（比如用户手动去注册表编辑器删过这一项）。</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;
                key.SetValue(ValueName, $"\"{exePath}\" --autostart");
            }
            else
            {
                if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 极少数受限环境下（比如注册表被组策略锁死）写入会失败，不应该因为这个
            // 阻断启动器本身的正常使用，静默忽略，用户在设置页看到的开关状态照常保存。
        }
    }
}
