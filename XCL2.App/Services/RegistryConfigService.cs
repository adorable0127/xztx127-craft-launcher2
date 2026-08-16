using System.Security.Principal;
using Microsoft.Win32;

namespace XCL2.App.Services;

/// <summary>
/// 独立的注册表读写服务，只负责跟 Windows 注册表打交道，不关心业务含义
/// （具体存哪些字段、默认值是什么，全部由 <see cref="ConfigService"/> 决定并调用这里的方法）。
///
/// ===== 存储位置 =====
/// 固定键：<c>SOFTWARE\XCL2</c>，同时可能存在于两个分支：
///   - HKEY_LOCAL_MACHINE（下称 HKLM）："全设备"范围，写入需要管理员权限。
///   - HKEY_CURRENT_USER（下称 HKCU）："当前用户"范围，普通权限即可写入。
///
/// ===== 读取顺序（双路径检查，不管有没有提权都两边都查） =====
/// 先查 HKLM\SOFTWARE\XCL2，查不到（键不存在/没有权限访问）就退回查 HKCU\SOFTWARE\XCL2，
/// 都没有就返回 null，调用方（ConfigService）应当回退到默认值。HKLM 优先，代表它是
/// "更高优先级/全局"的设置来源。
///
/// ===== 写入规则 =====
/// - 当前进程没有管理员权限，或者用户没有开启"使用管理员/全设备"开关 → 写 HKCU。
/// - 用户开启了"使用管理员/全设备"开关，且当前进程确实是以管理员身份运行 → 写 HKLM。
/// - 不管写哪一支，都不会因为"这次没有提权"就把已经写在另一支里的设置删除/覆盖——
///   本类的写入方法只触碰自己权限能碰到的那一支，从不主动删除另一支的数据。
///   这样即使用户这次用普通权限打开（写入 HKCU），上次用管理员权限写在 HKLM 里的设置
///   依然完整保留，下次重新以管理员运行时，读取仍然会优先命中 HKLM 里那份。
/// </summary>
public static class RegistryConfigService
{
    private const string SubKeyPath = @"SOFTWARE\XCL2";

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 极端环境（比如非 Windows 沙箱、身份查询被拒绝）下保守认为不是管理员，
            // 不让异常向上传播打断整个配置读取流程。
            return false;
        }
    }

    /// <summary>
    /// 读取一个字符串值。按"先 HKLM 后 HKCU"的顺序查找，都没有则返回 <paramref name="defaultValue"/>。
    /// 同时通过 <paramref name="foundInHive"/> 告知调用方这个值实际来自哪一支（或者两边都没找到）——
    /// ConfigService 用不到这个信息也可以忽略，主要给"导出注册表"之类需要知道来源的功能使用。
    /// </summary>
    public static string? GetString(string name, string? defaultValue, out RegistryHive? foundInHive)
    {
        if (TryGetValue(Registry.LocalMachine, name, out var hklmValue))
        {
            foundInHive = RegistryHive.LocalMachine;
            return hklmValue as string ?? defaultValue;
        }
        if (TryGetValue(Registry.CurrentUser, name, out var hkcuValue))
        {
            foundInHive = RegistryHive.CurrentUser;
            return hkcuValue as string ?? defaultValue;
        }
        foundInHive = null;
        return defaultValue;
    }

    /// <summary>读取一个整型值（内部用 DWORD 存储），规则同 <see cref="GetString"/>。</summary>
    public static int GetInt(string name, int defaultValue)
    {
        if (TryGetValue(Registry.LocalMachine, name, out var hklmValue) && hklmValue is int hklmInt)
            return hklmInt;
        if (TryGetValue(Registry.CurrentUser, name, out var hkcuValue) && hkcuValue is int hkcuInt)
            return hkcuInt;
        return defaultValue;
    }

    /// <summary>读取一个布尔值（内部用 DWORD 0/1 存储），规则同 <see cref="GetString"/>。</summary>
    public static bool GetBool(string name, bool defaultValue) =>
        GetInt(name, defaultValue ? 1 : 0) != 0;

    private static bool TryGetValue(RegistryKey root, string name, out object? value)
    {
        value = null;
        try
        {
            using var key = root.OpenSubKey(SubKeyPath, writable: false);
            if (key == null) return false;
            value = key.GetValue(name, null);
            return value != null;
        }
        catch
        {
            // 键存在但没有访问权限、或者其它注册表异常：视为"查不到"，让调用方退到下一支/默认值。
            return false;
        }
    }

    /// <summary>
    /// 写入一个字符串值。<paramref name="useLocalMachine"/> 为 true 且当前进程确实是管理员时
    /// 写 HKLM，否则一律写 HKCU（即使用户开着"使用管理员"开关但这次没有真正提权运行，
    /// 也不会假装能写 HKLM——避免抛出无权限异常中断整个保存流程，静默退化到写 HKCU，
    /// 保证"至少有一份写成功"）。
    /// </summary>
    public static bool SetString(string name, string value, bool useLocalMachine)
    {
        var root = (useLocalMachine && IsRunningAsAdministrator()) ? Registry.LocalMachine : Registry.CurrentUser;
        return TrySetValue(root, name, value, RegistryValueKind.String);
    }

    public static bool SetInt(string name, int value, bool useLocalMachine)
    {
        var root = (useLocalMachine && IsRunningAsAdministrator()) ? Registry.LocalMachine : Registry.CurrentUser;
        return TrySetValue(root, name, value, RegistryValueKind.DWord);
    }

    public static bool SetBool(string name, bool value, bool useLocalMachine) =>
        SetInt(name, value ? 1 : 0, useLocalMachine);

    private static bool TrySetValue(RegistryKey root, string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var key = root.CreateSubKey(SubKeyPath, writable: true);
            if (key == null) return false;
            key.SetValue(name, value, kind);
            return true;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"写入注册表失败：{root.Name}\\{SubKeyPath}\\{name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 判断 XCL2 的注册表键在 HKLM 和/或 HKCU 下是否存在（用于设置页展示"注册表功能"当前状态、
    /// 以及"删除所有新增的启动器注册表项"前先确认有没有东西可删）。
    /// </summary>
    public static (bool ExistsInHklm, bool ExistsInHkcu) CheckExistence()
    {
        bool hklm = false, hkcu = false;
        try { using var k = Registry.LocalMachine.OpenSubKey(SubKeyPath, false); hklm = k != null; } catch { /* 无权限视为不存在（查不到就是查不到） */ }
        try { using var k = Registry.CurrentUser.OpenSubKey(SubKeyPath, false); hkcu = k != null; } catch { /* 同上 */ }
        return (hklm, hkcu);
    }

    /// <summary>
    /// 删除 XCL2 的注册表键（HKLM、HKCU 两边只要能删的都删，各自独立 try，一边失败不影响另一边）。
    /// 这是"关闭注册表功能"/"删除所有新增的启动器注册表项"/"清除本机痕迹"三个危险操作的
    /// 共同底层实现——调用方（业务层）负责先做 xztx127 二次确认，这里只管执行删除本身，
    /// **只删 SOFTWARE\XCL2 这一个子键，绝不触碰这个子键以外的任何注册表内容**
    /// （对应需求里"注意界限，不要把此电脑删了"——历史 bug 的教训：必须把删除范围硬编码锁死在
    /// 这一个固定子键路径上，任何情况下都不允许把删除目标做成可配置/可传参，防止被误传成
    /// 其它路径导致删除范围失控）。
    /// </summary>
    public static (bool HklmDeleted, bool HkcuDeleted) DeleteXcl2Key()
    {
        bool hklmDeleted = false, hkcuDeleted = false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(SubKeyPath, false);
            if (k != null)
            {
                Registry.LocalMachine.DeleteSubKeyTree(SubKeyPath, throwOnMissingSubKey: false);
                hklmDeleted = true;
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("删除 HKLM\\SOFTWARE\\XCL2 失败（可能缺少管理员权限）", ex);
        }

        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(SubKeyPath, false);
            if (k != null)
            {
                Registry.CurrentUser.DeleteSubKeyTree(SubKeyPath, throwOnMissingSubKey: false);
                hkcuDeleted = true;
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("删除 HKCU\\SOFTWARE\\XCL2 失败", ex);
        }

        return (hklmDeleted, hkcuDeleted);
    }

    /// <summary>
    /// 导出 XCL2 注册表键为标准 .reg 文件内容（不管键实际在 HKLM 还是 HKCU，两边只要存在
    /// 都各自导出成一段，都不存在则返回 null 交由调用方提示"当前没有可导出的注册表内容"）。
    /// 手写标准 .reg 文本格式而不是调用外部 reg.exe（避免额外进程依赖/编码坑），
    /// 格式对齐 Windows 注册表编辑器"导出"生成的标准文件头 <c>Windows Registry Editor Version 5.00</c>。
    /// </summary>
    public static string? ExportToRegFileContent()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();

        var any = false;
        any |= AppendHiveSection(sb, Registry.LocalMachine, "HKEY_LOCAL_MACHINE");
        any |= AppendHiveSection(sb, Registry.CurrentUser, "HKEY_CURRENT_USER");

        return any ? sb.ToString() : null;
    }

    private static bool AppendHiveSection(System.Text.StringBuilder sb, RegistryKey root, string hiveName)
    {
        try
        {
            using var key = root.OpenSubKey(SubKeyPath, false);
            if (key == null) return false;

            sb.AppendLine($"[{hiveName}\\{SubKeyPath}]");
            foreach (var valueName in key.GetValueNames())
            {
                var kind = key.GetValueKind(valueName);
                var value = key.GetValue(valueName);
                var escapedName = valueName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                switch (kind)
                {
                    case RegistryValueKind.DWord:
                        sb.AppendLine($"\"{escapedName}\"=dword:{Convert.ToInt32(value):x8}");
                        break;
                    case RegistryValueKind.String:
                    default:
                        var escapedValue = (value?.ToString() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                        sb.AppendLine($"\"{escapedName}\"=\"{escapedValue}\"");
                        break;
                }
            }
            sb.AppendLine();
            return true;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"导出注册表分支失败：{hiveName}\\{SubKeyPath}", ex);
            return false;
        }
    }
}

/// <summary>标识一个值实际来自哪一个注册表分支。</summary>
public enum RegistryHive
{
    LocalMachine,
    CurrentUser
}
