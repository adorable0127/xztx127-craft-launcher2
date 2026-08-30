using System.Diagnostics;
using System.IO;

namespace XCL2.App.Services;

public enum ModuleRisk { Trusted, Unknown, Suspicious }

public class ScannedModule
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string? CompanyName { get; init; }
    public string? FileDescription { get; init; }
    public ModuleRisk Risk { get; init; }
    public string? MatchedRule { get; init; }
}

public class InjectionScanResult
{
    public int Pid { get; init; }
    public List<ScannedModule> Modules { get; init; } = new();
    public bool HasSuspiciousModule => Modules.Any(m => m.Risk == ModuleRisk.Suspicious);
    public int UnknownCount => Modules.Count(m => m.Risk == ModuleRisk.Unknown);
}

/// <summary>
/// 注入检测（进阶版）：枚举游戏 Java 进程当前加载的模块(DLL)列表，分三类：
///   - Trusted：Windows 系统目录下的模块、JVM 自身模块、以及公开签名为 Mojang/Microsoft/Oracle/常见正规 mod 加载器的模块
///   - Suspicious：文件名/路径命中已知外挂/密码窃取工具特征码规则库
///   - Unknown：既不在信任名单，也没命中规则库，交给用户自行判断（比如一些小众但正常的 mod 原生库）
///
/// 用途：外挂（游戏内作弊）和恶意软件（借着"游戏辅助/mod"的名义偷偷注入进程窃取账户密码/token）
/// 都是通过向游戏进程注入 DLL 实现的，这里做的是"事后可见性"检测，而不是主动拦截/杀毒，
/// 目的是让用户能看到"这个游戏进程里到底加载了什么"，尽早发现异常。
/// </summary>
public class InjectionScanService
{
    // 已知安全/官方相关模块前缀（不区分大小写），命中即视为 Trusted，减少噪音。
    private static readonly string[] TrustedPrefixes =
    {
        "ntdll", "kernel32", "kernelbase", "user32", "gdi32", "advapi32", "ole32", "oleaut32",
        "shell32", "shlwapi", "ws2_32", "winmm", "msvcrt", "ucrtbase", "vcruntime",
        "d3d", "dxgi", "opengl32", "glu32",
        "nvoglv", "nvwgf2um", "nvcuda", "atioglxx", "atig6txx", "igdumdim", "igxelpicd", // 官方显卡驱动模块
        "jvm.dll", "java.exe", "javaw.exe", "verify.dll", "zip.dll", "net.dll", "awt.dll",
        "lwjgl", "openal32", "glfw" // Minecraft/LWJGL 官方使用的原生库
    };

    // 已知外挂/密码窃取/注入工具的特征码规则库（文件名或路径关键字，不区分大小写）。
    // 覆盖思路：常见的 Minecraft 作弊客户端注入器、通用游戏外挂框架、以及伪装成"修复工具/加速器"的窃密木马命名习惯。
    private static readonly (string keyword, string ruleName)[] SuspiciousRules =
    {
        ("wurst", "已知外挂客户端特征(Wurst 系)"),
        ("impact", "已知外挂客户端特征(Impact 系)"),
        ("aristois", "已知外挂客户端特征(Aristois)"),
        ("sigma", "已知外挂客户端特征(Sigma 系)"),
        ("kamiblue", "已知外挂客户端特征(KamiBlue/Kami)"),
        ("meteor-client-injector", "已知外挂注入器特征(Meteor 注入变种)"),
        ("xenongui", "已知外挂GUI注入特征"),
        ("cheatbreaker", "已知外挂平台特征(CheatBreaker)"),
        ("injector", "文件名包含 injector（进程注入器，常见于外挂/木马加载器）"),
        ("inject", "文件名包含 inject 关键字，存在被注入风险"),
        ("hookdll", "文件名包含 hook 关键字，可能是 API Hook 型外挂或密码劫持模块"),
        ("keylog", "文件名包含 keylog 关键字，高度疑似键盘记录器(窃取密码风险)"),
        ("stealer", "文件名包含 stealer 关键字，高度疑似信息窃取木马"),
        ("tokenlogger", "文件名包含 tokenlogger，疑似盗取登录令牌的恶意模块"),
        ("discordtoken", "文件名涉及 discordtoken，疑似盗号木马常见命名"),
        ("nighthawk", "已知远控/注入框架特征(Nighthawk)"),
        ("cobaltstrike", "已知渗透/远控框架特征(Cobalt Strike)，正常游戏不应出现"),
    };

    /// <summary>扫描指定进程当前加载的模块，返回分类结果。仅在进程仍在运行时可用。</summary>
    public InjectionScanResult Scan(Process process)
    {
        var result = new InjectionScanResult { Pid = SafeGetPid(process) };
        ProcessModuleCollection? modules;
        try
        {
            process.Refresh();
            modules = process.Modules;
        }
        catch (Exception ex)
        {
            result.Modules.Add(new ScannedModule
            {
                FileName = "(扫描失败)",
                FullPath = "",
                FileDescription = ex.Message,
                Risk = ModuleRisk.Unknown
            });
            return result;
        }

        foreach (ProcessModule m in modules)
        {
            string fileName;
            string fullPath;
            string? company = null;
            string? description = null;
            try
            {
                fileName = m.ModuleName ?? "";
                fullPath = m.FileName ?? "";
                var info = m.FileVersionInfo;
                company = info?.CompanyName;
                description = info?.FileDescription;
            }
            catch
            {
                fileName = m.ModuleName ?? "(未知模块)";
                fullPath = "";
            }

            var risk = Classify(fileName, fullPath, company, out var matchedRule);
            result.Modules.Add(new ScannedModule
            {
                FileName = fileName,
                FullPath = fullPath,
                CompanyName = company,
                FileDescription = description,
                Risk = risk,
                MatchedRule = matchedRule
            });
        }

        return result;
    }

    private static int SafeGetPid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    private static ModuleRisk Classify(string fileName, string fullPath, string? company, out string? matchedRule)
    {
        var lowerName = fileName.ToLowerInvariant();
        var lowerPath = fullPath.ToLowerInvariant();

        foreach (var (keyword, ruleName) in SuspiciousRules)
        {
            if (lowerName.Contains(keyword) || lowerPath.Contains(keyword))
            {
                matchedRule = ruleName;
                return ModuleRisk.Suspicious;
            }
        }

        matchedRule = null;

        if (TrustedPrefixes.Any(p => lowerName.StartsWith(p)))
            return ModuleRisk.Trusted;

        // 系统目录下的模块默认信任（System32/SysWOW64 下的官方系统组件）
        if (lowerPath.Contains(@"\windows\system32\") || lowerPath.Contains(@"\windows\syswow64\"))
            return ModuleRisk.Trusted;

        // 公司签名信息里出现知名厂商，视为信任（不是签名验证，只是弱信号，仍展示给用户自行判断）
        if (company != null)
        {
            var c = company.ToLowerInvariant();
            if (c.Contains("mojang") || c.Contains("microsoft") || c.Contains("oracle") ||
                c.Contains("nvidia") || c.Contains("advanced micro devices") || c.Contains("intel"))
                return ModuleRisk.Trusted;
        }

        // 位于当前 .minecraft/mods 或游戏目录内的普通 mod 原生库，默认按 Unknown 展示（不是 Trusted 也不是 Suspicious），
        // 用户可自行判断这是不是自己安装的正常 mod。
        return ModuleRisk.Unknown;
    }
}
