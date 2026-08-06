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

    /// <summary>带置信度的结论列表。Findings 保留为纯文本以兼容既有界面绑定，
    /// 新界面可以用这个按置信度排序/分级显示。</summary>
    public List<CrashFinding> RankedFindings { get; init; } = new();
}

/// <summary>一条分析结论。Confidence 决定显示顺序——高置信度的结论排前面，
/// 避免"内存不足"这种泛泛的猜测盖过"某个 mod 缺依赖"这种确定性结论。</summary>
public record CrashFinding(string Text, CrashConfidence Confidence)
{
    public override string ToString() => Text;
}

public enum CrashConfidence
{
    /// <summary>启发式猜测，可能误报。</summary>
    Guess = 0,
    /// <summary>比较可靠的关键字匹配。</summary>
    Likely = 1,
    /// <summary>日志里明确写出了原因（例如 Fabric 直接报"缺少依赖 X"），基本可以确定。</summary>
    Certain = 2,
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

        // ===== 关键补充：logs/latest.log =====
        // 这是之前最大的盲区。**大量启动失败根本不会产生 crash-report**：
        // - Fabric/Forge 的依赖解析失败（缺前置 mod、版本不匹配）会打印错误后直接 exit，
        //   crash-reports 目录里什么都没有；
        // - mod 在 mixin 阶段就炸掉时也常常只有日志、没有崩溃报告。
        // 这两类恰恰是玩家最常遇到的问题。只看 crash-reports 就等于对它们完全失明——
        // 用户会看到"游戏闪退但找不到崩溃报告"，启动器也给不出任何提示。
        var logsDir = Path.Combine(gameDir, "logs");
        if (Directory.Exists(logsDir))
        {
            foreach (var name in new[] { "latest.log", "debug.log" })
            {
                var f = Path.Combine(logsDir, name);
                if (File.Exists(f)) results.Add((f, File.GetLastWriteTime(f)));
            }
        }

        return results.OrderByDescending(r => r.Item2).ToList();
    }

    public CrashReportResult Analyze(string filePath)
    {
        var raw = SafeReadAll(filePath);
        var ranked = RunRankedRules(raw);
        return new CrashReportResult
        {
            FilePath = filePath,
            ModifiedAt = File.Exists(filePath) ? File.GetLastWriteTime(filePath) : DateTime.MinValue,
            RawText = raw,
            // 按置信度从高到低排：日志里明确写出原因的结论要排在启发式猜测前面，
            // 否则"内存不足"这种泛泛的提示会盖过"缺少前置 mod X"这种确定性结论。
            RankedFindings = ranked,
            Findings = ranked.Select(f => f.Text).ToList(),
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
    /// <summary>
    /// 分析入口：先跑「高置信度」规则（日志里明确写出了原因的），再跑原有的启发式规则，
    /// 最后按置信度排序。高置信度规则命中时会抑制掉那句"未匹配到已知规则"的兜底提示。
    /// </summary>
    private static List<CrashFinding> RunRankedRules(string text)
    {
        var result = new List<CrashFinding>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        result.AddRange(RunDependencyRules(text));
        result.AddRange(RunExitCodeRules(text));
        result.AddRange(RunModuleSystemRules(text));

        // 原有启发式规则：整体归为 Likely（关键字匹配，比"猜"强，但不如日志明说可靠）。
        var heuristic = RunRules(text);
        foreach (var h in heuristic)
        {
            // 已经有高置信度结论时，不要再附上"未匹配到已知规则"那句兜底——自相矛盾。
            if (result.Count > 0 && h.StartsWith("未匹配到已知的常见崩溃规则")) continue;
            result.Add(new CrashFinding(h, CrashConfidence.Likely));
        }

        return result.OrderByDescending(f => f.Confidence).ToList();
    }

    /// <summary>
    /// 依赖/前置 mod 相关规则 —— 这是**玩家最常遇到、而旧版完全没覆盖**的一类。
    ///
    /// Fabric 和 Forge 在依赖解析失败时会打印结构化的错误块，里面明确写着
    /// "哪个 mod 需要哪个前置的哪个版本"。这类信息是确定性的（不是猜的），
    /// 而且直接告诉用户该去下什么，所以给 Certain 置信度、排在最前面。
    ///
    /// 注意这类失败通常**不产生 crash-report**，只写进 logs/latest.log——
    /// 这也是为什么 ListCrashFiles 现在必须把 latest.log 一起列出来。
    /// </summary>
    private static List<CrashFinding> RunDependencyRules(string text)
    {
        var found = new List<CrashFinding>();

        // --- Fabric: "requires version x.y.z of mod abc, which is missing!" ---
        foreach (Match m in Regex.Matches(text,
            @"Mod '(?<name>[^']+)'\s*\((?<id>[\w\-]+)\)[^\n]*?requires\s+(?<req>[^\n]+?)\s+of\s+(?<dep>[\w\-']+)[^\n]*?which is missing",
            RegexOptions.IgnoreCase))
        {
            found.Add(new CrashFinding(
                $"缺少前置 Mod：「{m.Groups["name"].Value}」需要 {m.Groups["dep"].Value}（版本要求 {m.Groups["req"].Value}），" +
                "但这个前置没有安装。去 Modrinth/CurseForge 搜这个前置的名字，下载版本要求里写的那个版本装进 mods 文件夹即可。",
                CrashConfidence.Certain));
        }

        // --- Fabric 简化形态：requires any version of fabric-api ---
        foreach (Match m in Regex.Matches(text,
            @"requires\s+(?:any version of\s+)?(?<dep>[\w\-]+)[^\n]{0,40}?(?:which is missing|but it is missing)",
            RegexOptions.IgnoreCase))
        {
            var dep = m.Groups["dep"].Value;
            if (found.Any(f => f.Text.Contains(dep))) continue;
            found.Add(new CrashFinding(
                $"缺少前置 Mod：{dep}。这个 mod 是其它 mod 运行的必需依赖，没装就会直接启动失败。" +
                (dep.Contains("fabric", StringComparison.OrdinalIgnoreCase) && dep.Contains("api", StringComparison.OrdinalIgnoreCase)
                    ? " 注意 Fabric API 要下跟你游戏版本完全对应的那一份。"
                    : ""),
                CrashConfidence.Certain));
        }

        // --- Forge/NeoForge: "Mod X requires Y a.b.c or above" ---
        foreach (Match m in Regex.Matches(text,
            @"Mod\s+(?<id>[\w\-]+)\s+requires\s+(?<dep>[\w\-]+)\s+(?<ver>[\d.\[\],)(]+)\s*(?:or above)?",
            RegexOptions.IgnoreCase))
        {
            found.Add(new CrashFinding(
                $"依赖版本不满足：「{m.Groups["id"].Value}」要求 {m.Groups["dep"].Value} {m.Groups["ver"].Value} 或更高，" +
                "当前装的版本太低或者没装。把这个依赖更新到要求的版本即可。",
                CrashConfidence.Certain));
        }

        // --- 版本不匹配：mod 声明只支持某些 MC 版本 ---
        foreach (Match m in Regex.Matches(text,
            @"Mod\s+'?(?<id>[\w\-]+)'?\s+requires\s+minecraft\s+(?<ver>[^\n,]+)",
            RegexOptions.IgnoreCase))
        {
            found.Add(new CrashFinding(
                $"游戏版本不匹配：「{m.Groups["id"].Value}」只支持 Minecraft {m.Groups["ver"].Value.Trim()}，" +
                "跟你当前启动的版本对不上。要么换这个 mod 的对应版本，要么换一个游戏版本启动。",
                CrashConfidence.Certain));
        }

        // --- Fabric 的整块 "Incompatible mods found" 摘要 ---
        if (Regex.IsMatch(text, @"Incompatible mod(s)? found|Mod resolution encountered an incompatible mod set",
                RegexOptions.IgnoreCase) && found.Count == 0)
        {
            found.Add(new CrashFinding(
                "Fabric 报告 mod 之间不兼容（依赖解析失败），游戏还没进主菜单就退出了。" +
                "日志里紧跟这句话的下面几行会写明是哪几个 mod 冲突，按那里的提示增删对应 mod 即可。",
                CrashConfidence.Certain));
        }

        // --- 重复安装同一个 mod（换版本时最常见的手滑）---
        foreach (Match m in Regex.Matches(text,
            @"Duplicate mod(?:s)?[^\n]*?['""]?(?<id>[\w\-]+)['""]?",
            RegexOptions.IgnoreCase))
        {
            found.Add(new CrashFinding(
                $"mods 文件夹里装了两份「{m.Groups["id"].Value}」（多半是更新时新旧版本都留着了）。" +
                "打开 mods 文件夹，把旧的那个 jar 删掉，只保留一个。",
                CrashConfidence.Certain));
        }

        // 同一条结论可能被多个正则重复命中，去重
        return found
            .GroupBy(f => f.Text)
            .Select(g => g.First())
            .Take(8)
            .ToList();
    }

    /// <summary>
    /// Java 模块系统（JPMS）相关规则——覆盖 Forge/NeoForge 用 securejarhandler +
    /// bootstraplauncher 启动时特有的一类崩溃，这类崩溃 JVM 本身能正常起来（早期显示窗口
    /// 甚至会一闪而过），是在构建模块图（ModuleLayerHandler.buildLayer）这一步失败的，
    /// 报错栈里全是 java.lang.module.* 而不是常见的 mod/mixin 异常，容易被误判成"未知崩溃"。
    /// </summary>
    private static List<CrashFinding> RunModuleSystemRules(string text)
    {
        var found = new List<CrashFinding>();

        // "Module minecraft contains package X, module Y exports package X to minecraft"：
        // vanilla client jar 和 Forge patched jar 被同时当成独立模块解析，两者都声称拥有
        // 同一个包。根因是启动参数缺少 securejarhandler 认的 -DignoreList=/-DmergeModules=
        // 这两个属性（详见 LauncherService.BuildArguments 里对应注释），不是库文件损坏，
        // 也不是版本装坏了，重装/补全依赖库都无法解决，必须由启动器自己修正启动参数。
        var dupPkgMatch = Regex.Match(text,
            @"Module\s+(?<a>[\w.\-]+)\s+contains\s+package\s+(?<pkg>[\w.]+),\s*module\s+(?<b>[\w.\-]+)\s+exports\s+package\s+\k<pkg>\s+to\s+(?<a2>[\w.\-]+)",
            RegexOptions.IgnoreCase);
        if (dupPkgMatch.Success)
        {
            found.Add(new CrashFinding(
                $"Java 模块系统冲突：模块「{dupPkgMatch.Groups["a"].Value}」和「{dupPkgMatch.Groups["b"].Value}」" +
                $"都声称拥有包 {dupPkgMatch.Groups["pkg"].Value}。这是 Forge/NeoForge（新版 securejarhandler 启动方式）" +
                "的一个已知启动参数问题：原版客户端 jar 和 Forge 打过补丁的 jar 被同时当成独立模块解析导致冲突，" +
                "跟库文件是否完整、版本是否装坏无关。请更新到修复了该问题的启动器版本（需要在启动参数里补上 " +
                "-DignoreList= 和 -DmergeModules= 这两个属性），普通重装/补全依赖库无法解决这个问题。",
                CrashConfidence.Certain));
        }

        // 更泛化的 ResolutionException 兜底（同一根因的其它措辞，例如 "Two versions of module"
        // 等），没有精确捕获到具体包名时也给出方向性提示，而不是完全沉默。
        else if (Regex.IsMatch(text, "java.lang.module.ResolutionException", RegexOptions.IgnoreCase))
        {
            found.Add(new CrashFinding(
                "Java 模块系统（JPMS）解析失败 (java.lang.module.ResolutionException)：多见于 Forge/NeoForge 用" +
                "模块化方式启动时，classpath 上的某几个 jar 之间存在模块声明冲突（重复的包/重复的模块名）。" +
                "跟库文件损坏无关，需要启动器在启动参数里正确设置 -DignoreList=/-DmergeModules= 来排除不该参与" +
                "模块解析的 jar。",
                CrashConfidence.Likely));
        }

        return found;
    }

    /// <summary>
    /// 退出码规则。游戏被系统杀掉时往往没有任何 Java 堆栈，只有一个退出码，
    /// 旧版对这种情况完全给不出提示。这里覆盖几个最常见的。
    /// </summary>
    private static List<CrashFinding> RunExitCodeRules(string text)
    {
        var found = new List<CrashFinding>();

        var m = Regex.Match(text, @"(?:exit code|Process exited with code|退出代码)[:\s]+(-?\d+)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return found;

        if (!long.TryParse(m.Groups[1].Value, out var code)) return found;

        switch (code)
        {
            case 0:
                break;   // 正常退出，不用提示
            case -1073741819:   // 0xC0000005
                found.Add(new CrashFinding(
                    "退出码 -1073741819（内存访问违规）：这是系统层面的崩溃，几乎总是显卡驱动或某个自带 .dll 的 mod 导致的。" +
                    "先更新显卡驱动；还不行就把最近新装的 mod（尤其是光影/渲染类）逐个移除试。",
                    CrashConfidence.Likely));
                break;
            case -1073740791:   // 0xC0000409 stack buffer overrun
                found.Add(new CrashFinding(
                    "退出码 -1073740791（栈溢出保护触发）：通常是某个 mod 递归调用失控。" +
                    "回想一下最近装了什么 mod，先把它移除试试。",
                    CrashConfidence.Likely));
                break;
            case 1:
                found.Add(new CrashFinding(
                    "退出码 1：游戏在启动早期就退出了，多半是 mod 依赖没满足或者 Java 版本不对。" +
                    "翻一下上面的日志内容，通常会有更具体的报错行。",
                    CrashConfidence.Guess));
                break;
            case -805306369:    // 0xCFFFFFFF
                found.Add(new CrashFinding(
                    "退出码 -805306369：游戏卡死后被强制结束。常见于内存给得太少导致长时间 GC 停顿，" +
                    "可以在设置里把最大内存调高一些。",
                    CrashConfidence.Likely));
                break;
            default:
                if (code < 0)
                    found.Add(new CrashFinding(
                        $"退出码 {code}：游戏被系统异常终止（不是 Java 层的崩溃，所以没有崩溃报告）。" +
                        "这类问题优先怀疑显卡驱动、杀毒软件拦截、内存不足三个方向。",
                        CrashConfidence.Guess));
                break;
        }

        return found;
    }

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
