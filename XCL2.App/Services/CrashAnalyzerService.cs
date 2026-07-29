using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>一份崩溃报告：原始文件路径 + 原文内容 + 自动分析出的中文提示列表。</summary>
public class CrashReportResult
{
    public string FilePath { get; init; } = "";
    public DateTime ModifiedAt { get; init; }
    public string RawText { get; init; } = "";
    public List<string> Findings { get; init; } = new();
}

/// <summary>
/// 崩溃报告分析：
/// 1) 原样读取 Minecraft 生成的 crash-reports/*.txt 和 JVM 的 hs_err_pid*.log，供用户/高手直接查看原文；
/// 2) 用一批常见规则做中文自动解析（mod 冲突、内存不足、显卡驱动、Java 版本不匹配等），给普通用户看得懂的结论。
/// 规则是启发式关键字匹配，不保证 100% 准确，仅作为定位问题的第一步提示。
/// </summary>
public class CrashAnalyzerService
{
    /// <summary>列出一个 .minecraft 目录下所有崩溃报告/JVM 错误日志，按时间倒序。</summary>
    public List<(string path, DateTime modifiedAt)> ListCrashFiles(string gameDir)
    {
        var results = new List<(string, DateTime)>();

        var crashDir = Path.Combine(gameDir, "crash-reports");
        if (Directory.Exists(crashDir))
        {
            foreach (var f in Directory.GetFiles(crashDir, "*.txt"))
                results.Add((f, File.GetLastWriteTime(f)));
        }

        // hs_err_pid*.log 由 JVM 本地代码崩溃（native crash，常见于显卡驱动/OptiFine/某些原生库）时
        // 直接写在游戏工作目录（一般是 gameDir 本身），而不是 crash-reports 里
        foreach (var f in Directory.GetFiles(gameDir, "hs_err_pid*.log"))
            results.Add((f, File.GetLastWriteTime(f)));

        return results.OrderByDescending(r => r.Item2).ToList();
    }

    public CrashReportResult Analyze(string filePath)
    {
        var raw = SafeReadAll(filePath);
        return new CrashReportResult
        {
            FilePath = filePath,
            ModifiedAt = File.Exists(filePath) ? File.GetLastWriteTime(filePath) : DateTime.MinValue,
            RawText = raw,
            Findings = RunRules(raw)
        };
    }

    private static string SafeReadAll(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) { return $"(读取崩溃文件失败: {ex.Message})"; }
    }

    /// <summary>启发式规则库：按顺序匹配，命中即加入提示，允许同时命中多条。
    /// 面向"小白也能看懂"这个目标重写：每条结论都尽量说清楚"是什么原因"+"具体是哪个方块/
    /// 实体/mod"+"应该怎么处理"，而不是只甩一个异常类名。</summary>
    private static List<string> RunRules(string text)
    {
        var findings = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return findings;

        // --- 1) 某个具体方块导致的崩溃 ---
        // Minecraft 崩溃报告里，方块相关的崩溃通常会在 "-- Block entity being ticked --" 或
        // "-- Block being ticked --" 段落里带 "Block: minecraft:xxx" 这样的字段，直接把具体
        // 方块名读出来，比只说"游戏崩溃了"有用得多。
        var blockMatch = Regex.Match(text, @"--\s*Block(?:\s+entity)?\s+being\s+ticked\s*--.*?Block:\s*([\w:.\[\]=,\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (blockMatch.Success)
        {
            var blockName = blockMatch.Groups[1].Value.Split('\n')[0].Trim();
            findings.Add($"崩溃发生在方块「{blockName}」被更新（tick）的时候。建议：先记下这个方块的坐标" +
                          "（崩溃报告里搜索「Block location」或「Position」字段能找到），回到游戏后把这个位置的方块" +
                          "拆除或用创造模式替换掉，通常就能避免再次崩溃；如果这个方块来自某个 mod，" +
                          "也可以尝试更新或暂时移除那个 mod。");
        }

        // --- 2) 某个具体实体/生物导致的崩溃 ---
        var entityMatch = Regex.Match(text, @"--\s*Entity\s+being\s+ticked\s*--.*?Entity Type:\s*([\w:.\[\]=,\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (entityMatch.Success)
        {
            var entityName = entityMatch.Groups[1].Value.Split('\n')[0].Trim();
            findings.Add($"崩溃发生在实体「{entityName}」（生物/掉落物/载具等）被更新（tick）的时候。" +
                          "建议：回到游戏后找到并清除附近同类型的实体（可以用指令 /kill 配合实体类型清除，" +
                          "或者干脆远离出问题的区域），如果这个实体来自某个 mod，同样可以尝试先更新那个 mod。");
        }

        // --- 3) 某个具体 mod 注入/初始化失败导致的崩溃 ---
        // Fabric/Forge/NeoForge 的 mod 加载失败信息里通常会带 modid，尽量把 modid 提取出来，
        // 拼成"XX 注入失败崩溃"这种一眼就能看懂"是哪个 mod 的问题"的结论。
        var modLoadFailMatch = Regex.Matches(text,
            @"Mod\s*['""]?([\w\-]+)['""]?\s*(?:has failed to load|failed to initialize|encountered an error)|" +
            @"An error occurred while (?:loading|initializing) mod\s*['""]?([\w\-]+)|" +
            @"net\.(?:fabricmc|minecraftforge|neoforged)\S*\.(\w+)Exception.*?mod\s*['""]?([\w\-]+)",
            RegexOptions.IgnoreCase);
        var failedModIds = modLoadFailMatch
            .SelectMany(m => m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1))
            .Where(g => g.Success && !string.IsNullOrWhiteSpace(g.Value))
            .Select(g => g.Value)
            .Distinct()
            .Take(5)
            .ToList();
        if (failedModIds.Count > 0)
        {
            foreach (var modId in failedModIds)
                findings.Add($"「{modId}」注入失败崩溃：这个 mod 在加载游戏时出错了，最常见的原因是版本不匹配" +
                              "（这个 mod 和你当前的 Minecraft/Fabric/Forge 版本不兼容），或者它依赖的另一个 mod" +
                              "没有装。建议：先去 Modrinth/CurseForge 确认这个 mod 支持你当前的游戏版本，" +
                              "不支持就换一个匹配版本的下载，或者暂时移除它。");
        }
        else if (Regex.IsMatch(text, @"mixin", RegexOptions.IgnoreCase) && Regex.IsMatch(text, @"(Exception|Error)"))
        {
            findings.Add("崩溃堆栈中出现 Mixin 相关报错，通常意味着两个或多个 mod 试图修改同一段游戏代码发生冲突。" +
                          "建议查看堆栈中出现的 mod 名/包名，尝试逐个禁用最近新安装的 mod 定位冲突方。");
        }

        var modIdMatches = Regex.Matches(text, @"mods[\\/]([\w\-.]+\.jar)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Distinct().Take(5).ToList();
        if (modIdMatches.Count > 0)
            findings.Add($"崩溃堆栈中提到了以下 mod 文件，建议优先检查这些是否为最新兼容版本：{string.Join("、", modIdMatches)}");

        // --- 4) Java 版本不匹配导致的崩溃 ---
        // UnsupportedClassVersionError 的报错信息里通常带 "class file version 65.0"（65 对应 Java 21）
        // 这类数字，尝试解析出"需要的 class file 版本"，反推出用户应该用的 Java 主版本号，
        // 直接给出"该不该更改这个版本的默认设置"这样具体的建议，而不是只甩一个异常类名。
        if (Regex.IsMatch(text, "UnsupportedClassVersionError", RegexOptions.IgnoreCase))
        {
            var classVersionMatch = Regex.Match(text, @"class file version (\d+)\.\d+");
            string javaHint;
            if (classVersionMatch.Success && int.TryParse(classVersionMatch.Groups[1].Value, out var classVersion))
            {
                // class file major version 与 Java 主版本号的对应关系是固定的：Java N 对应 (44+N)。
                var requiredJava = classVersion - 44;
                javaHint = $"这次崩溃需要 Java {requiredJava} 或更高版本，但当前使用的 Java 版本过低。" +
                            $"建议在「设置」页把这个版本要求的 Java 改成 Java {requiredJava}，" +
                            "或者直接把这个版本的默认 Java 设置更新为这个更高的版本，避免下次再手动切换。";
            }
            else
            {
                javaHint = "检测到 UnsupportedClassVersionError：当前使用的 Java 版本过低，无法运行该游戏版本/mod 所需的字节码。" +
                            "请在「设置」里切换到更高版本的 Java（例如 1.20.5 以后的版本大多需要 Java 21 或以上）。";
            }
            findings.Add(javaHint);
        }

        // --- 内存不足 ---
        if (Regex.IsMatch(text, "OutOfMemoryError", RegexOptions.IgnoreCase))
            findings.Add("检测到 OutOfMemoryError：游戏运行内存不足。建议在「设置」里调高最大内存分配（-Xmx），" +
                          "或关闭高分辨率材质包/减少同时加载的 mod 数量。");

        // --- 显卡/驱动相关 native crash ---
        if (Regex.IsMatch(text, "EXCEPTION_ACCESS_VIOLATION", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(text, @"nvoglv|atioglxx|igxx|opengl32|nvwgf2um", RegexOptions.IgnoreCase))
            findings.Add("崩溃发生在显卡驱动相关模块（OpenGL/NVIDIA/AMD/Intel 驱动）。建议更新显卡驱动到最新版本，" +
                          "或尝试在游戏设置中关闭「高级 OpenGL」「快速渲染」等选项。");
        else if (Regex.IsMatch(text, "EXCEPTION_ACCESS_VIOLATION", RegexOptions.IgnoreCase))
            findings.Add("检测到底层内存访问异常 (EXCEPTION_ACCESS_VIOLATION)，通常是某个 native 库(显卡驱动/" +
                          "某些 mod 自带的 .dll)导致，建议先尝试更新显卡驱动，再逐个排查最近新增的 mod。");

        // --- 找不到类/依赖缺失 ---
        if (Regex.IsMatch(text, "ClassNotFoundException|NoClassDefFoundError"))
            findings.Add("检测到 ClassNotFoundException/NoClassDefFoundError：可能是某个 mod 缺少依赖库，" +
                          "或该 mod 与当前 Minecraft/模组加载器版本不匹配，建议确认 mod 版本号是否对应。");

        // --- 材质/资源包相关 ---
        if (Regex.IsMatch(text, "resourcepack|texture", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(text, "Exception|Error"))
            findings.Add("崩溃可能与材质包/资源包有关，建议尝试切换回默认材质包后重新进入游戏验证。");

        // --- 网络/多人游戏 ---
        if (Regex.IsMatch(text, "ConnectException|SocketTimeoutException|UnknownHostException"))
            findings.Add("检测到网络连接相关异常，可能是服务器地址错误、服务器未开放端口，或本地网络/防火墙拦截。");

        if (findings.Count == 0)
            findings.Add("未匹配到已知的常见崩溃规则，建议在「日志」页面把完整崩溃日志发给你信任的专业人士" +
                          $"（不要只发窗口截图），或前往 GitHub 提交反馈：{ErrorPresenter.GitHubRepoUrl}");

        return findings;
    }
}
