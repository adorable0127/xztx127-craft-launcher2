using System.Globalization;
using System.IO;
using System.IO.Compression;
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
        var modsDir = ResolveLikelyModsDir(filePath);
        var ranked = RunRankedRules(raw, modsDir);
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

    /// <summary>
    /// 从崩溃文件路径反推它所属实例的 mods 文件夹在哪。崩溃文件常见落点是
    /// &lt;实例目录&gt;/crash-reports/xxx.txt、&lt;实例目录&gt;/logs/latest.log 或
    /// &lt;实例目录&gt;/hs_err_pid*.log，往上最多找 3 层目录，找到第一个挂着 mods
    /// 子文件夹的就当作这个崩溃所属的实例目录（版本隔离开/关两种布局都能兼容：
    /// 隔离时实例目录本身就有 mods/，不隔离时最终会找到 .minecraft 根目录的 mods/）。
    /// 找不到就返回 null，调用方据此跳过"具体是哪个 mod 崩的"这一步分析（没有 mods
    /// 文件夹就没法比对，不代表分析失败，只是少了这一条结论）。
    /// </summary>
    private static string? ResolveLikelyModsDir(string crashFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(crashFilePath));
            for (var i = 0; i < 3 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, "mods");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* 路径解析异常就当作找不到，不影响其它规则继续跑 */ }
        return null;
    }

    /// <summary>启发式规则库：按顺序匹配，命中即加入提示，允许同时命中多条。
    /// 面向"小白也能看懂"这个目标重写：每条结论都尽量说清楚"是什么原因"+"具体是哪个方块/
    /// 实体/mod"+"应该怎么处理"，而不是只甩一个异常类名。</summary>
    /// <summary>
    /// 分析入口：先跑「高置信度」规则（日志里明确写出了原因的），再跑原有的启发式规则，
    /// 最后按置信度排序。高置信度规则命中时会抑制掉那句"未匹配到已知规则"的兜底提示。
    /// </summary>
    private static List<CrashFinding> RunRankedRules(string text, string? modsDir)
    {
        var result = new List<CrashFinding>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        if (OpenGlTroubleshooter.IsOpenGlFailure(text))
            result.Add(new CrashFinding("OpenGL 初始化/版本要求失败。" + OpenGlTroubleshooter.RepairSteps,
                                        CrashConfidence.Certain));
        // JVM 启动参数错误（Unrecognized VM option / 堆内存参数 / GC 冲突 / 缺 - 等）：
        // 由 JvmArgumentDiagnostics 统一负责，下面 RunExtendedRules 里原先零散的同类规则已移除，避免重复结论。
        result.AddRange(JvmArgumentDiagnostics.ToFindings(text));
        result.AddRange(RunDependencyRules(text));
        result.AddRange(RunExitCodeRules(text));
        result.AddRange(RunModuleSystemRules(text));
        result.AddRange(RunExtendedRules(text, modsDir));
        result.AddRange(RunMoreRules(text, modsDir));
        result.AddRange(RunModAttributionRules(text, modsDir));

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

        // --- Fabric / Forge 结构化依赖解析（能直接指出缺的是哪个前置、谁需要它）---
        found.AddRange(RunFabricResolutionRules(text));
        found.AddRange(RunForgeResolutionRules(text));

        // --- Fabric 简化形态：requires any version of fabric-api（上面的结构化解析没命中时才兜底）---
        if (found.Count == 0)
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
            @"Duplicate mods?[^\n]*?['""](?<id>[\w\-]+)['""]",
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

    private static readonly HashSet<string> NotModIdWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Formatted", "Resolution", "Loading", "Loader", "Mixin", "Entrypoint", "Initializer", "the", "mod", "mods", "and",
    };

    private sealed class MissingDepInfo
    {
        public string Id = "";
        public string? Display;
        public string? Req;
        public List<string> RequiredBy = new();
    }

    /// <summary>把 Fabric 日志里依赖的几种写法统一成 (mod id, 友好名)：
    /// "fabric-api" / "mod fabric-api" / "mod 'Sodium' (sodium)" / "'Sodium' (sodium)"。</summary>
    private static (string Id, string? Display) NormalizeFabricDep(string raw)
    {
        raw = raw.Trim().TrimEnd('!', '.', ',').Trim();
        var withParen = Regex.Match(raw, @"^(?:mod\s+)?(?:'(?<name>[^']*)'|""(?<name2>[^""]*)"")?\s*\((?<id>[\w\-.]+)\)$",
            RegexOptions.IgnoreCase);
        if (withParen.Success)
        {
            var name = withParen.Groups["name"].Success ? withParen.Groups["name"].Value
                     : withParen.Groups["name2"].Success ? withParen.Groups["name2"].Value : null;
            return (withParen.Groups["id"].Value, string.IsNullOrWhiteSpace(name) ? null : name);
        }
        raw = Regex.Replace(raw, @"^mod\s+", "", RegexOptions.IgnoreCase).Trim('\'', '"', ' ');
        return (raw, null);
    }

    private static bool IsBuiltinDependencyId(string id) =>
        id.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
        || id.Equals("java", StringComparison.OrdinalIgnoreCase)
        || id.Equals("fabricloader", StringComparison.OrdinalIgnoreCase)
        || id.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase);

    /// <summary>"any version" -> null（不限版本）；"version 0.5 or later" -> "0.5 或更高"。</summary>
    private static string? PrettyVersionReq(string? req)
    {
        if (string.IsNullOrWhiteSpace(req)) return null;
        req = req.Trim();
        if (req.StartsWith("any version", StringComparison.OrdinalIgnoreCase)) return null;
        req = Regex.Replace(req, @"^version\s+", "", RegexOptions.IgnoreCase);
        req = Regex.Replace(req, @"\s+or\s+later", " 或更高", RegexOptions.IgnoreCase);
        return req;
    }

    /// <summary>
    /// Fabric 依赖解析失败的结构化解析。Fabric 会打印两种互相印证的信息：
    ///   - "A potential solution..." 下面的 "- Install sodium, version 0.5 or later."（要装什么）
    ///   - "More details:" 下面的 "Mod 'X' (x) 1.0 requires ... of sodium, which is missing!"（谁需要它）
    /// 把两处合并成「缺哪个前置 + 谁需要它 + 版本要求」，而不是只说一句"依赖没满足"。
    /// 同时覆盖"前置装了但版本不对"（but only the wrong version is present）。
    /// </summary>
    private static List<CrashFinding> RunFabricResolutionRules(string text)
    {
        var found = new List<CrashFinding>();
        if (!Regex.IsMatch(text, @"requires|potential solution|Incompatible mod", RegexOptions.IgnoreCase))
            return found;

        var missing = new Dictionary<string, MissingDepInfo>(StringComparer.OrdinalIgnoreCase);
        void AddMissing(string depRaw, string? req, string? requiredBy)
        {
            var (id, display) = NormalizeFabricDep(depRaw);
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || IsBuiltinDependencyId(id)) return;
            if (!missing.TryGetValue(id, out var info))
                missing[id] = info = new MissingDepInfo { Id = id };
            info.Display ??= display;
            info.Req ??= PrettyVersionReq(req);
            if (!string.IsNullOrWhiteSpace(requiredBy) && !info.RequiredBy.Contains(requiredBy))
                info.RequiredBy.Add(requiredBy);
        }

        // 谁需要谁（且那个前置没装）
        foreach (Match m in Regex.Matches(text,
            @"Mod\s+'(?<name>[^']+)'\s*\((?<id>[^)]+)\)[^\n]*?\brequires\s+(?<req>[^\n]+?)\s+of\s+(?<dep>[^\n]+?),\s*which\s+is\s+missing",
            RegexOptions.IgnoreCase))
        {
            AddMissing(m.Groups["dep"].Value, m.Groups["req"].Value, m.Groups["name"].Value);
        }

        // "- Install xxx, version ..." 解决方案行（有些 Fabric 版本只给这一种）
        foreach (Match m in Regex.Matches(text,
            @"^\s*-\s*Install\s+(?<dep>[^,\r\n]+?)(?:,\s*(?<req>[^\r\n]+?))?\s*\.?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            AddMissing(m.Groups["dep"].Value, m.Groups["req"].Success ? m.Groups["req"].Value : null, null);
        }

        // 游戏版本 / 加载器信息（用来让建议更具体：下载哪个加载器、哪个游戏版本的文件）
        var mc = Regex.Match(text, @"Loading Minecraft (?<v>\d+(?:\.\d+){1,2}[\w.\-]*)", RegexOptions.IgnoreCase);
        var mcText = mc.Success ? mc.Groups["v"].Value : null;
        var target = "Fabric" + (mcText != null ? $" {mcText}" : "");

        foreach (var info in missing.Values.Take(8))
        {
            var label = info.Display != null ? $"{info.Display}（ID：{info.Id}）" : info.Id;
            var by = info.RequiredBy.Count > 0 ? $"，需要它的模组：{string.Join("、", info.RequiredBy.Take(5))}" : "";
            var ver = info.Req != null ? $"，版本要求：{info.Req}" : "";
            var apiNote = info.Id.Equals("fabric-api", StringComparison.OrdinalIgnoreCase) || info.Id.Equals("fabric", StringComparison.OrdinalIgnoreCase)
                ? "（在 Modrinth 上搜「Fabric API」）" : "";
            found.Add(new CrashFinding(
                $"缺少前置模组：{label}{ver}{by}。" +
                $"解决：去 Modrinth 搜索它{apiNote}，下载适配「{target}」的版本放进 mods 文件夹——" +
                "注意加载器必须一致，Fabric 的模组不能用 NeoForge/Forge 版的前置。",
                CrashConfidence.Certain));
        }

        // 前置装了，但版本不对
        foreach (Match m in Regex.Matches(text,
            @"Mod\s+'(?<name>[^']+)'\s*\((?<id>[^)]+)\)[^\n]*?\brequires\s+(?<req>[^\n]+?)\s+of\s+(?<dep>[^\n]+?),\s*but\s+only\s+the\s+wrong\s+version\s+is\s+present:\s*(?<have>[^\r\n!]+)",
            RegexOptions.IgnoreCase))
        {
            var (depId, depName) = NormalizeFabricDep(m.Groups["dep"].Value);
            var req = PrettyVersionReq(m.Groups["req"].Value) ?? "任意版本";
            var have = m.Groups["have"].Value.Trim();
            var modName = m.Groups["name"].Value;
            string text2;
            if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                text2 = $"游戏版本不匹配：「{modName}」要求 Minecraft {req}，但你当前是 {have}。换这个模组的对应版本，或换游戏版本。";
            else if (depId.Equals("java", StringComparison.OrdinalIgnoreCase))
                text2 = $"Java 版本不满足：「{modName}」要求 Java {req}，当前是 {have}。到「设置」里给这个版本选择更高版本的 Java（1.18~1.20.4 用 17，1.20.5 及以后用 21）。";
            else if (depId.Equals("fabricloader", StringComparison.OrdinalIgnoreCase) || depId.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
                text2 = $"Fabric Loader 版本不满足：「{modName}」要求 {req}，当前是 {have}。请在「安装加载器」里升级 Fabric Loader。";
            else
                text2 = $"前置模组版本不对：「{modName}」要求 {depName ?? depId} {req}，当前装的是 {have}。把它更新到要求的版本（加载器保持 Fabric）。";
            found.Add(new CrashFinding(text2, CrashConfidence.Certain));
        }

        return found
            .GroupBy(f => f.Text)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Forge/NeoForge 的 "Missing or unsupported mandatory dependencies" 块：
    ///   Mod ID: 'architectury', Requested by: 'jei', Expected range: '[8.0,)', Actual version: '[MISSING]'
    /// </summary>
    private static List<CrashFinding> RunForgeResolutionRules(string text)
    {
        var found = new List<CrashFinding>();
        foreach (Match m in Regex.Matches(text,
            @"Mod ID:\s*'(?<dep>[^']+)',\s*Requested by:\s*'(?<by>[^']+)',\s*Expected range:\s*'(?<range>[^']*)',\s*Actual version:\s*'(?<actual>[^']*)'",
            RegexOptions.IgnoreCase))
        {
            var dep = m.Groups["dep"].Value;
            var by = m.Groups["by"].Value;
            var range = m.Groups["range"].Value;
            var actual = m.Groups["actual"].Value;
            var missingDep = actual.Contains("MISSING", StringComparison.OrdinalIgnoreCase);

            if (dep.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
                || dep.Equals("forge", StringComparison.OrdinalIgnoreCase)
                || dep.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new CrashFinding(
                    $"版本不匹配：「{by}」要求 {dep} {range}，当前是 {actual}。这个模组和你的游戏/加载器版本对不上，换对应版本的模组。",
                    CrashConfidence.Certain));
            }
            else if (missingDep)
            {
                found.Add(new CrashFinding(
                    $"缺少前置模组：{dep}（版本要求 {range}），需要它的模组：{by}。去 Modrinth 搜索下载与当前加载器（Forge/NeoForge）和游戏版本一致的文件。",
                    CrashConfidence.Certain));
            }
            else
            {
                found.Add(new CrashFinding(
                    $"前置模组版本不对：「{by}」要求 {dep} {range}，当前装的是 {actual}。更新这个前置到要求的版本。",
                    CrashConfidence.Certain));
            }
        }
        return found.GroupBy(f => f.Text).Select(g => g.First()).Take(8).ToList();
    }

    // =====================================================================
    // 扩展规则：覆盖更多常见的游戏报错类型。
    // 分两类处理，避免在大段 latest.log 里被无关的 WARN/ERROR 噪音误导：
    //   - 「致命型」报错（JVM 起不来、内存耗尽、启动参数不被识别……）出现了基本就是真因，扫全文；
    //   - 「噪音型」报错（网络证书、登录验证、文件占用、配置解析……）正常运行的日志里也经常出现，
    //     只扫日志最后 250 行（崩溃/退出前的那一段），并且降低置信度。
    // =====================================================================

    private static readonly (string Prefix, string Name, string Hint)[] KnownLibPrefixes =
    {
        ("kotlin.", "Kotlin 运行库", "Fabric 下安装「Fabric Language Kotlin」，Forge/NeoForge 下安装「Kotlin for Forge」"),
        ("kotlinx.", "Kotlin 运行库", "Fabric 下安装「Fabric Language Kotlin」，Forge/NeoForge 下安装「Kotlin for Forge」"),
        ("me.shedaniel.clothconfig2", "Cloth Config API", "去 Modrinth 搜索「Cloth Config API」"),
        ("me.shedaniel.cloth", "Cloth Config API", "去 Modrinth 搜索「Cloth Config API」"),
        ("dev.architectury", "Architectury API", "去 Modrinth 搜索「Architectury API」"),
        ("software.bernie.geckolib", "GeckoLib", "去 Modrinth 搜索「GeckoLib」"),
        ("net.fabricmc.fabric", "Fabric API", "去 Modrinth 搜索「Fabric API」"),
        ("com.terraformersmc.modmenu", "Mod Menu", "去 Modrinth 搜索「Mod Menu」"),
    };

    private static bool RxHas(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);

    /// <summary>取日志最后 N 行（崩溃/退出前的那一段）。</summary>
    private static string Tail(string text, int lineCount)
    {
        var idx = text.Length;
        for (var i = 0; i < lineCount && idx > 0; i++)
        {
            idx = text.LastIndexOf('\n', idx - 1);
            if (idx < 0) return text;
        }
        return idx > 0 ? text[idx..] : text;
    }

    /// <summary>按名字线索（mixin 配置名、mod id）在 mods 文件夹里找最像的 jar 文件名。找不到返回 null。</summary>
    private static string? FindJarByHint(string? modsDir, string hint)
    {
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir) || string.IsNullOrWhiteSpace(hint)) return null;
        static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
        var h = Norm(hint);
        if (h.Length < 3 || h == "mixins" || h == "mixin") return null;
        try
        {
            return Directory.GetFiles(modsDir, "*.jar")
                .Select(p => Path.GetFileName(p))
                .FirstOrDefault(n => n != null && Norm(n).Contains(h));
        }
        catch { return null; }
    }

    private static string ShortLine(string s, int max = 120)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static List<CrashFinding> RunExtendedRules(string text, string? modsDir)
    {
        var f = new List<CrashFinding>();
        void Add(string msg, CrashConfidence c) => f.Add(new CrashFinding(msg, c));
        var tail = Tail(text, 250);

        // ---------- A. JVM 启动失败 / Java 版本问题（致命型，扫全文）----------
        if (RxHas(text, @"InaccessibleObjectException|Unable to make (?:field|method|constructor)[^\n]*accessible|module java\.base does not ""?opens"))
            Add("Java 版本太新：这个游戏版本/加载器/模组还没适配当前的 Java（反射访问 java.base 被拒绝）。" +
                "Minecraft 1.16.5 及更早的版本通常要用 Java 8，1.17 用 Java 16，1.18~1.20.4 用 Java 17，1.20.5 及以后用 Java 21。" +
                "在「设置」里给这个版本换成对应的 Java 即可。",
                CrashConfidence.Certain);

        if (RxHas(text, @"cannot be cast to (?:class )?java\.net\.URLClassLoader"))
            Add("Java 版本太新：老版本 Forge（1.12.2 及更早）只能跑在 Java 8 上，用 Java 9 以上会在启动时直接报这个错。" +
                "请在「设置」里给这个版本选 Java 8。",
                CrashConfidence.Certain);

        var classMajor = Regex.Match(text, @"Unsupported class file major version (?<v>\d+)", RegexOptions.IgnoreCase);
        if (classMajor.Success && int.TryParse(classMajor.Groups["v"].Value, out var cmv))
            Add($"检测到 class 文件版本 {cmv}（对应 Java {cmv - 44}）不被支持。两种可能：① 当前 Java 比 {cmv - 44} 低，换成 Java {cmv - 44} 或更高；" +
                "② 老版本加载器配新 Java，无法解析新字节码，换成这个游戏版本要求的那版 Java。",
                CrashConfidence.Likely);

        if (RxHas(text, @"Could not find or load main class|ClassNotFoundException:\s*(?:cpw\.mods\.(?:modlauncher|bootstraplauncher)|net\.minecraft\.client\.main\.Main|net\.fabricmc\.loader\.impl\.launch)"))
            Add("启动器缺少游戏启动所需的核心库文件（找不到主类），多半是依赖库没下载完整或被杀毒软件删掉了。" +
                "对这个版本执行一次「补全文件/检查完整性」，仍不行就重新安装这个版本。",
                CrashConfidence.Likely);

        // ---------- B. 文件损坏 ----------
        var zipErr = Regex.Match(text,
            @"(?:zip END header not found|error in opening zip file|Invalid or corrupt jarfile|invalid LOC header|invalid CEN header|zip file is empty)[^\n]*",
            RegexOptions.IgnoreCase);
        if (zipErr.Success)
        {
            var jar = Regex.Match(zipErr.Value, @"[\w\-.+() ]+\.jar", RegexOptions.IgnoreCase);
            Add((jar.Success ? $"文件「{jar.Value.Trim()}」" : "某个 jar 文件") +
                "已损坏（不是有效的压缩包），通常是下载中断或磁盘问题。把它删掉重新下载；" +
                "如果是游戏本体/依赖库，就对这个版本执行「补全文件」。",
                CrashConfidence.Certain);
        }

        var digest = Regex.Match(text, @"SHA-?\d*\s+digest error for\s+(?<c>[^\r\n]+)", RegexOptions.IgnoreCase);
        if (digest.Success || RxHas(text, @"Invalid signature file digest"))
            Add("某个带数字签名的 jar 被改动或损坏（签名校验失败）" +
                (digest.Success ? $"，出问题的文件：{ShortLine(digest.Groups["c"].Value, 80)}" : "") +
                "。常见于下载不完整，或两个 mod 内嵌了同一个库的不同签名版本。重新下载最近安装的 mod，必要时对游戏「补全文件」。",
                CrashConfidence.Likely);

        if (RxHas(text, @"ClassFormatError|Truncated class file|Extra bytes at the end of the class file|Incompatible magic value"))
            Add("检测到类文件格式错误：某个 jar 里的文件被破坏，或下载不完整。重新下载最近安装的 mod，必要时「补全文件」。",
                CrashConfidence.Likely);

        // ---------- C. 本地库(natives)/ 运行库 ----------
        if (RxHas(text, @"UnsatisfiedLinkError|Failed to locate library|no (?:lwjgl|jinput|glfw)[\w\-]* in java\.library\.path|Unable to load native library"))
            Add("本地库（natives，如 LWJGL 的 .dll）加载失败。多为 natives 文件夹缺失/损坏，或被杀毒软件拦截删除。" +
                "删掉这个版本目录下的 natives 文件夹后重新启动（启动器会重新解压），不行再执行「补全文件」，" +
                "并把游戏目录加入杀毒软件白名单。",
                CrashConfidence.Likely);

        // ---------- D. 内存 / 磁盘（致命型）----------
        if (RxHas(text, @"OutOfMemoryError:?\s*Metaspace"))
            Add("类元空间（Metaspace）耗尽：加载的 mod 太多，类元数据装不下了。可以在 JVM 参数里加上 -XX:MaxMetaspaceSize=512m（或更大），" +
                "同时适当调高最大内存。",
                CrashConfidence.Certain);
        if (RxHas(text, @"unable to create (?:new )?native thread"))
            Add("系统无法再创建新线程：这是系统资源/本地内存不足，而不是给游戏的堆内存不够。关闭后台占内存的程序，" +
                "适当把最大内存调小一点（给系统留出余量），并确认页面文件（虚拟内存）没被关掉。",
                CrashConfidence.Certain);
        if (RxHas(text, @"GC overhead limit exceeded"))
            Add("垃圾回收耗时过长（GC overhead limit exceeded）：内存分给得太少，或某个 mod 内存泄漏。调高最大内存；" +
                "如果刚装了新 mod，先把它移除试试。",
                CrashConfidence.Certain);
        if (RxHas(text, @"OutOfMemoryError:?\s*Direct buffer memory"))
            Add("直接内存（Direct buffer）不足：常见于高分辨率材质包/光影。换低分辨率材质包，" +
                "或在 JVM 参数里加上 -XX:MaxDirectMemorySize=2G。",
                CrashConfidence.Certain);
        if (RxHas(text, @"There is insufficient memory for the Java Runtime Environment|Out of Memory Error \(.*(?:os_windows|allocation|arena)"))
            Add("系统可用内存不足，Java 连运行环境都起不来。关闭其它占内存的程序，把游戏最大内存调小，" +
                "并检查页面文件（虚拟内存）是否被关闭。",
                CrashConfidence.Certain);
        if (RxHas(text, @"No space left on device|There is not enough space on the disk|磁盘空间不足"))
            Add("磁盘空间不足：游戏所在的磁盘已经写满，无法继续写入。清理出至少几个 GB 的空间后重试。",
                CrashConfidence.Certain);
        if (RxHas(text, @"StackOverflowError"))
            Add("栈溢出（StackOverflowError）：通常是某个 mod 的递归调用失控，多见于 mod 之间互相冲突。" +
                "回想最近新装/更新的 mod，先移除它们试试；也可以在 JVM 参数里加 -Xss4M 增大栈。",
                CrashConfidence.Likely);

        // ---------- E. 版本不一致：NoSuchMethodError 等 ----------
        var linkErr = Regex.Match(text,
            @"(?<err>NoSuchMethodError|NoSuchFieldError|AbstractMethodError|IncompatibleClassChangeError)[:\s]+(?<what>[^\r\n]+)",
            RegexOptions.IgnoreCase);
        if (linkErr.Success)
            Add($"模组之间（或模组与游戏）版本不一致：有代码调用了一个不存在的{(linkErr.Groups["err"].Value.Contains("Field", StringComparison.OrdinalIgnoreCase) ? "字段" : "方法")}" +
                $"（{ShortLine(linkErr.Groups["what"].Value)}）。通常是只更新了其中一个 mod 或它的前置库，" +
                "把相关 mod 一起更新到同一时期的版本，或确认它们都对应当前的游戏版本和加载器。",
                CrashConfidence.Likely);

        // ---------- F. 缺少运行库类（Kotlin / Cloth Config / Architectury …）----------
        var missingClass = Regex.Match(text, @"(?:NoClassDefFoundError|ClassNotFoundException):\s*(?<c>[\w./$]+)", RegexOptions.IgnoreCase);
        if (missingClass.Success)
        {
            var cls = missingClass.Groups["c"].Value.Replace('/', '.');
            foreach (var lib in KnownLibPrefixes)
            {
                if (!cls.StartsWith(lib.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                Add($"缺少运行库：{lib.Name}（找不到类 {ShortLine(cls, 80)}）。{lib.Hint}，" +
                    "并确保它和当前游戏版本、加载器一致。",
                    CrashConfidence.Likely);
                break;
            }
        }

        // ---------- G. Mixin ----------
        var mixinMod = Regex.Match(text, @"Mixin apply for mod (?<mod>[\w\-]+) failed", RegexOptions.IgnoreCase);
        if (mixinMod.Success)
        {
            var jar = FindJarByHint(modsDir, mixinMod.Groups["mod"].Value);
            Add($"模组「{mixinMod.Groups["mod"].Value}」{(jar != null ? $"（{jar}）" : "")}的 Mixin 注入失败：它要修改的游戏代码对不上，" +
                "通常是它与当前 Minecraft/加载器版本不匹配，或与另一个同样修改这段代码的 mod 冲突。" +
                "更新或换成匹配版本；仍不行就逐个禁用最近装的 mod 排查。",
                CrashConfidence.Certain);
        }
        else if (RxHas(text, @"InvalidInjectionException|Critical injection failure|MixinApplyError|InvalidMixinException|MixinTransformerError"))
        {
            var cfg = Regex.Match(text, @"(?<n>[\w\-.]+?)\.mixins?\.json", RegexOptions.IgnoreCase);
            var jar = cfg.Success ? FindJarByHint(modsDir, cfg.Groups["n"].Value) : null;
            Add("Mixin 注入失败：某个 mod 想修改的游戏代码找不到目标。" +
                (cfg.Success ? $"日志里出现的 Mixin 配置是 {cfg.Value}" + (jar != null ? $"，对应的很可能是 {jar}" : "") + "。" : "") +
                "原因通常是 mod 与当前游戏/加载器版本不匹配，或与别的 mod（例如 OptiFine 与 Sodium/Iris）冲突。更新或换成匹配版本，" +
                "或把最近装的 mod 逐个禁用排查。",
                cfg.Success && jar != null ? CrashConfidence.Likely : CrashConfidence.Guess);
        }

        // ---------- H. 加载器 / 模组文件本身的问题 ----------
        foreach (Match m in Regex.Matches(text,
            @"Mod file\s+[^\r\n]*?(?<f>[^\\/\s]+\.jar)\s+is missing\s+(?:META-INF/)?(?<t>(?:neoforge\.)?mods\.toml)",
            RegexOptions.IgnoreCase).Take(5))
        {
            Add($"「{m.Groups["f"].Value}」不是给 Forge/NeoForge 用的模组（缺少 {m.Groups["t"].Value}）。" +
                "多半是把 Fabric 的 mod 放进了 Forge/NeoForge 实例，或者装错了加载器版本。把它换成对应加载器的版本，或先移出 mods 文件夹。",
                CrashConfidence.Certain);
        }

        var badFabricJson = Regex.Match(text,
            @"(?:Could not parse|Failed to parse|Invalid)[^\n]*fabric\.mod\.json[^\n]*|fabric\.mod\.json[^\n]*(?:invalid|malformed|syntax)[^\n]*",
            RegexOptions.IgnoreCase);
        if (badFabricJson.Success)
        {
            var jar = Regex.Match(badFabricJson.Value, @"[\w\-.+()]+\.jar", RegexOptions.IgnoreCase);
            Add((jar.Success ? $"「{jar.Value}」" : "某个 mod") +
                "的 fabric.mod.json 无法解析：文件损坏，或这个 mod 的版本太新/太旧，当前 Fabric Loader 读不懂。重新下载，或升级 Fabric Loader。",
                CrashConfidence.Likely);
        }

        foreach (Match m in Regex.Matches(text,
            @"Mod ID:\s*'(?<id>[^']+)'\s*from mod files:\s*(?<files>[^\r\n]+)",
            RegexOptions.IgnoreCase).Take(5))
        {
            Add($"mods 文件夹里同一个 mod「{m.Groups["id"].Value}」有多个文件：{ShortLine(m.Groups["files"].Value, 160)}。" +
                "只保留一个版本，把其余的删掉。",
                CrashConfidence.Certain);
        }

        // ---------- I. 服务端 / 联机相关 ----------
        if (RxHas(text, @"You need to agree to the EULA|eula=false|Failed to load eula\.txt"))
            Add("服务端没有同意 Minecraft EULA：打开服务器目录里的 eula.txt，把 eula=false 改成 eula=true，保存后重新启动。",
                CrashConfidence.Certain);
        if (RxHas(tail, @"Address already in use|Failed to bind to port|java\.net\.BindException"))
            Add("端口被占用：这个端口已经被另一个程序（多半是另一个还没关掉的服务端/游戏）使用。关掉它，" +
                "或在 server.properties 里改一个别的 server-port。",
                CrashConfidence.Certain);

        // ---------- J. 存档 / 数据包 / 注册表 ----------
        if (RxHas(text, @"Failed to load registries|Registry Object not present|Unknown registry key|Missing registry entries|Unknown registry"))
            Add("存档里用到了当前没有安装的 mod 内容（注册表缺失）。把当初创建/游玩这个存档时用的 mod 装回来，" +
                "或换回原来的游戏版本；如果必须移除某个 mod，请先备份存档。",
                CrashConfidence.Likely);
        if (RxHas(text, @"Failed to load datapacks?|Couldn't load data ?pack|Failed to load data ?pack"))
            Add("数据包加载失败。把这个存档 datapacks 文件夹里最近添加的数据包移走；" +
                "游戏出现提示时也可以选「安全模式」（Safe Mode）先进入存档。",
                CrashConfidence.Likely);
        if (RxHas(tail, @"Exception reading[^\n]*level\.dat|Level save corrupted|Failed to read level|EOFException[^\n]*(?:\.mca|region)|Chunk file at[^\n]*is in the wrong location|Couldn't load chunk"))
            Add("存档文件可能已损坏（level.dat 或区块文件读取失败）。先备份整个存档文件夹；" +
                "level.dat_old 如果存在，可以把它改名成 level.dat 覆盖试试；还不行再尝试用存档修复工具。",
                CrashConfidence.Likely);

        // ---------- K. 配置文件损坏（噪音型，只看日志末尾）----------
        var cfgMatch = Regex.Match(tail,
            @"(?:Failed to (?:load|parse|read)|Couldn't (?:load|parse|read)|Error (?:loading|parsing|reading)|TomlParseError|ParsingException|JsonSyntaxException)[^\n]*?(?<p>config[\\/][\w\-./\\ ]+\.(?:toml|json5?|cfg|properties|ya?ml))",
            RegexOptions.IgnoreCase);
        if (cfgMatch.Success)
            Add($"配置文件「{cfgMatch.Groups["p"].Value.Trim()}」格式错误或已损坏，导致相关 mod 加载失败。" +
                "把这个文件改名备份（或直接删除），重新启动游戏会自动生成默认配置。",
                CrashConfidence.Likely);

        // ---------- L. 光影 / 渲染 ----------
        if (RxHas(text, @"Failed to (?:compile|link) (?:shader|program)|ShaderCompileException|ShaderLinkException|Error compiling shader|Shader compilation failed"))
            Add("光影（着色器）编译失败：当前光影包与你的显卡、游戏版本或 Iris/OptiFine 版本不兼容。" +
                "换一个光影包，或在视频设置里关闭光影；如果游戏进不去，可以把 shaderpacks 里这个光影包移走，" +
                "再删除 config/iris.properties（Iris）或 optionsshaders.txt（OptiFine）。",
                CrashConfidence.Likely);

        var screen = Regex.Match(text, @"--\s*Screen render details\s*--[\s\S]{0,300}?Screen name:\s*(?<s>[^\r\n]+)", RegexOptions.IgnoreCase);
        if (screen.Success)
            Add($"崩溃发生在渲染界面「{ShortLine(screen.Groups["s"].Value, 80)}」时。如果这是某个 mod 的界面，多半是该 mod 的问题或与别的 mod 冲突；" +
                "更新它，或暂时移除后再试。",
                CrashConfidence.Likely);

        if (RxHas(text, @"--\s*Block being tessellated\s*--|--\s*Tesselating block in world\s*--|--\s*Block being tesselated\s*--"))
            Add("崩溃发生在渲染方块模型时，常见于材质包/模型包出错，或渲染类 mod（Sodium、OptiFine、Iris 等）冲突。" +
                "先切回默认材质包，再逐个禁用渲染类 mod 排查。",
                CrashConfidence.Likely);

        if (RxHas(text, @"optifine") && RxHas(text, @"sodium|rubidium|embeddium|canvas") &&
            RxHas(text, @"Exception|Error|Crash"))
            Add("日志里同时出现了 OptiFine 和 Sodium 系（Sodium/Rubidium/Embeddium）模组，它们几乎总是互相冲突。" +
                "只保留其中一套：Fabric 下推荐 Sodium + Iris，Forge 下推荐 Embeddium/Rubidium + Oculus。",
                CrashConfidence.Guess);

        // ---------- M. hs_err 里的第三方 DLL / 悬浮软件 ----------
        var frame = Regex.Match(text, @"Problematic frame:\s*[\r\n]+#\s*C\s+\[(?<dll>[\w\-.]+)\+0x", RegexOptions.IgnoreCase);
        if (frame.Success)
        {
            var dll = frame.Groups["dll"].Value;
            if (Regex.IsMatch(dll, @"overlay|hook|rtss|discord|steam|zoomit|bandicam|fraps|obs|nahimic|sonic|rivatuner|afterburner|kaspersky|avast|huorong|360|nvspcap|gameoverlay", RegexOptions.IgnoreCase))
                Add($"JVM 崩溃在第三方软件注入的模块「{dll}」里：这是悬浮窗/录屏/加速/杀毒类软件钩住了游戏进程。" +
                    "退出这类软件（Steam/Discord 悬浮窗、MSI Afterburner/RTSS、录屏软件、游戏加速器等）后再启动游戏。",
                    CrashConfidence.Certain);
            else if (Regex.IsMatch(dll, @"^(?:jvm|libjvm)\.", RegexOptions.IgnoreCase))
                Add($"崩溃发生在 Java 虚拟机自身（{dll}）：常见于 Java 版本有 bug、内存硬件不稳定或超频。" +
                    "换一个 Java 发行版/版本再试，并把内存、CPU 超频还原。",
                    CrashConfidence.Likely);
            else if (!Regex.IsMatch(dll, @"nvoglv|nvlddmkm|atio|amdxx|ig\w*icd|igxx|opengl32|nvwgf2um", RegexOptions.IgnoreCase))
                Add($"JVM 崩溃在原生模块「{dll}」里。它不是常见的显卡驱动模块，" +
                    "多半来自某个 mod 自带的原生库或后台注入软件，先把最近新增的 mod、后台软件逐个排除。",
                    CrashConfidence.Likely);
        }

        // ---------- N. 登录 / 网络（噪音型，只看日志末尾）----------
        if (RxHas(tail, @"Invalid session|Bad login|AuthenticationUnavailableException|Failed to verify username"))
            Add("账号登录状态无效或无法验证：正版登录已过期，或用离线账号进了开了正版验证的服务器。" +
                "重新登录一次账号；若是离线账号，请进离线模式的服务器。",
                CrashConfidence.Guess);
        if (RxHas(tail, @"PKIX path building failed|unable to find valid certification path|SSLHandshakeException|certificate_unknown"))
            Add("HTTPS 证书校验失败（SSL 握手失败）：常见原因是电脑系统时间不对、使用了会拦截 HTTPS 的代理/杀毒软件，" +
                "或 Java 版本太旧、证书库过期。校准系统时间、临时关闭代理，必要时更新 Java。",
                CrashConfidence.Guess);

        // ---------- O. 文件被占用 / 权限（噪音型，只看日志末尾）----------
        if (RxHas(tail, @"AccessDeniedException|Access is denied|拒绝访问|being used by another process|另一个程序正在使用此文件"))
            Add("有文件无法读写：被另一个程序占用，或没有权限。关闭可能占用游戏目录的程序（同步盘、杀毒软件、另一个游戏窗口），" +
                "并避免把游戏放在 C:\\Program Files 或 OneDrive 同步目录里。",
                CrashConfidence.Guess);

        // ---------- P. 用崩溃报告的 Description 字段兜底给出崩溃阶段 ----------
        var desc = Regex.Match(text, @"^Description:\s*(?<d>[^\r\n]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (desc.Success && f.Count == 0)
        {
            var d = desc.Groups["d"].Value;
            string? stage = null;
            if (d.Contains("Initializing game", StringComparison.OrdinalIgnoreCase))
                stage = "游戏初始化阶段（还没进主菜单）。多半是 mod 加载、资源包或配置出了问题，先看日志里第一个 ERROR 附近的内容。";
            else if (d.Contains("Rendering overlay", StringComparison.OrdinalIgnoreCase))
                stage = "加载界面（资源加载覆盖层）。常见于材质包损坏/不兼容，或渲染类 mod 冲突。";
            else if (d.Contains("Exception in server tick loop", StringComparison.OrdinalIgnoreCase))
                stage = "服务端主循环（单人世界也属于）。多半是某个 mod 的方块/实体/世界生成逻辑出错。";
            else if (d.Contains("Rendering entity in world", StringComparison.OrdinalIgnoreCase))
                stage = "渲染世界中的实体时。多半是某个 mod 的实体模型/材质出错。";
            else if (d.Contains("Loading world", StringComparison.OrdinalIgnoreCase) || d.Contains("Loading level", StringComparison.OrdinalIgnoreCase))
                stage = "加载存档时。可能是存档损坏，或存档里用到了没安装的 mod。";
            if (stage != null)
                Add($"崩溃报告显示崩溃发生在：{stage}", CrashConfidence.Guess);
        }

        return f.GroupBy(x => x.Text).Select(g => g.First()).Take(10).ToList();
    }

    /// <summary>
    /// 第二批扩展规则：更多常见的游戏崩溃日志（加载器给出的"嫌疑 Mod"、入口点失败、缺 DirectX/VC++ DLL、
    /// GLFW/Vulkan 图形错误、hs_err 里的更多异常码、JVM 堆参数矛盾、存档被占用、音频设备等）。
    /// 分类沿用 RunExtendedRules：致命型扫全文，噪音型只扫日志末尾。
    /// </summary>
    private static List<CrashFinding> RunMoreRules(string text, string? modsDir)
    {
        var f = new List<CrashFinding>();
        void Add(string msg, CrashConfidence c) => f.Add(new CrashFinding(msg, c));
        var tail = Tail(text, 250);

        // ---------- 1. 加载器给出的"嫌疑 Mod"/入口点失败（直接点名）----------
        var suspects = Regex.Match(text, @"Suspected Mods?:\s*(?<m>[^\r\n]+)", RegexOptions.IgnoreCase);
        if (suspects.Success)
        {
            var list = suspects.Groups["m"].Value.Trim();
            if (list.Length > 0 && !Regex.IsMatch(list, @"^(?:none|unknown|n/a)\b", RegexOptions.IgnoreCase))
                Add($"崩溃报告里加载器列出的「嫌疑 Mod」：{ShortLine(list, 200)}。先更新这些 mod；仍崩溃就暂时移出 mods 文件夹验证。",
                    CrashConfidence.Likely);
        }

        var entry = Regex.Match(text,
            @"Could not execute entrypoint stage '(?<stage>[^']+)' due to errors, provided by '(?<mod>[^']+)'",
            RegexOptions.IgnoreCase);
        if (entry.Success)
        {
            var jar = FindJarByHint(modsDir, entry.Groups["mod"].Value);
            Add($"模组「{entry.Groups["mod"].Value}」{(jar != null ? $"（{jar}）" : "")}在 {entry.Groups["stage"].Value} 入口点执行时出错，" +
                "游戏因此启动失败。通常是它与当前游戏/加载器版本不匹配，或缺少前置。更新到匹配版本，或先暂时移除它。",
                CrashConfidence.Certain);
        }

        var forgeCaught = Regex.Match(text, @"Caught exception from\s+(?<n>[^\r\n]+)", RegexOptions.IgnoreCase);
        if (forgeCaught.Success)
            Add($"Forge/NeoForge 报告模组「{ShortLine(forgeCaught.Groups["n"].Value, 80)}」在事件处理中抛出异常。更新它到匹配当前游戏版本的版本，或暂时移除。",
                CrashConfidence.Likely);

        // ---------- 2. 缺少 DLL：DirectX / Visual C++ 运行库 ----------
        var dllWords = @"not found|cannot find|can't find|missing|Unable to load|Can't load|Failed to load|找不到|无法找到|无法加载|UnsatisfiedLinkError";
        var dx = Regex.Match(text, @"\b(?<d>d3dx\d*_\d+|d3dcompiler_\d+|xinput\d_\d|xaudio\d_\d|x3daudio\d_\d)\.dll", RegexOptions.IgnoreCase);
        if (dx.Success && RxHas(text, dllWords))
            Add($"缺少 DirectX 组件「{dx.Value}」：系统里没有这个 DirectX 运行库文件。点崩溃弹窗里的「补全 DirectX」按钮即可从微软官方安装；" +
                "这不是显卡驱动问题，不需要换驱动。",
                CrashConfidence.Likely);

        var vc = Regex.Match(text,
            @"\b(?<d>msvcp\d+(?:_\d)?|vcruntime\d+(?:_\d)?|msvcr\d+|concrt\d+|ucrtbase|api-ms-win-crt-[\w\-]+)\.dll", RegexOptions.IgnoreCase);
        if (vc.Success && RxHas(text, dllWords))
            Add($"缺少 Visual C++ 运行库组件「{vc.Value}」：安装微软 Visual C++ 2015-2022 运行库（x64 版，https://aka.ms/vc14/vc_redist.x64.exe），" +
                "再重新启动游戏。",
                CrashConfidence.Likely);

        // ---------- 3. GLFW / Vulkan 图形错误 ----------
        var glfw = Regex.Match(text, @"GLFW error (?<c>65537|65544|65545)[^\r\n]*", RegexOptions.IgnoreCase);
        if (glfw.Success)
        {
            var glfwCode = glfw.Groups["c"].Value;
            var what = glfwCode switch
            {
                "65537" => "GLFW 没有初始化成功（窗口系统/图形库起不来）",
                "65544" => "窗口平台错误（Platform Error）",
                _ => "不支持所需的像素格式（Format Unavailable）",
            };
            Add($"图形窗口创建失败：GLFW 错误 {glfwCode}，{what}。多见于显卡驱动异常或过旧、在远程桌面/虚拟机里运行、" +
                "显示器接在了不被使用的显卡上，或缺少 DirectX 组件。更新显卡驱动，确认本机有硬件加速；" +
                "也可以点崩溃弹窗里的「补全 DirectX」试试。",
                CrashConfidence.Likely);
        }

        var vk = Regex.Match(text,
            @"VK_ERROR_(?<e>INCOMPATIBLE_DRIVER|INITIALIZATION_FAILED|DEVICE_LOST|OUT_OF_DEVICE_MEMORY|OUT_OF_HOST_MEMORY)|Failed to create Vulkan (?:instance|device)|No (?:suitable )?Vulkan",
            RegexOptions.IgnoreCase);
        if (vk.Success)
            Add("Vulkan 图形接口初始化/运行失败" + (vk.Groups["e"].Success ? $"（{vk.Groups["e"].Value}）" : "") +
                "：显卡或驱动不支持所需的 Vulkan 版本，或显存不足、驱动崩溃（设备丢失）。更新显卡驱动；" +
                "笔记本确认游戏使用的是独立显卡；仍不行就换一个使用 OpenGL 渲染的游戏版本。",
                CrashConfidence.Likely);

        // ---------- 4. hs_err / 原生异常 ----------
        if (RxHas(text, @"EXCEPTION_ILLEGAL_INSTRUCTION"))
            Add("JVM 报告「非法指令」（EXCEPTION_ILLEGAL_INSTRUCTION）：某个原生库用了这颗 CPU 不支持的指令（老 CPU 常见），或 CPU 超频不稳定。" +
                "还原超频，换一个 Java 发行版试试；如果出问题的是某个 mod 的原生库，更换或移除它。",
                CrashConfidence.Likely);
        if (RxHas(text, @"EXCEPTION_STACK_OVERFLOW"))
            Add("JVM 报告原生栈溢出（EXCEPTION_STACK_OVERFLOW）：多为某个 mod 或原生库递归失控。先移除最近新增的 mod，" +
                "也可以在 JVM 参数里加 -Xss4M。",
                CrashConfidence.Likely);
        if (RxHas(text, @"EXCEPTION_IN_PAGE_ERROR"))
            Add("JVM 读取内存映射文件失败（EXCEPTION_IN_PAGE_ERROR）：游戏文件所在的磁盘读取出错，常见于文件放在移动硬盘/网络盘/同步盘，" +
                "或磁盘有坏道。把游戏目录搬到本机的固态/机械硬盘上，并检查磁盘健康。",
                CrashConfidence.Likely);
        if (RxHas(text, @"Native memory allocation \((?:mmap|malloc)\) failed|Failed to commit metaspace"))
            Add("系统无法再给 Java 分配本地内存：物理内存或页面文件（虚拟内存）不够。关闭其它程序，把游戏最大内存调小，" +
                "并确认页面文件没被关闭或设得太小。",
                CrashConfidence.Certain);

        // ---------- 6. 启动参数 / 资源索引 ----------
        if (RxHas(text, @"is not a recognized option|UnrecognizedOptionException|Unknown option\s*'?--"))
            Add("游戏不认识某个启动参数：多半是在「设置」里写的自定义游戏参数有误，或这个参数不适用于当前游戏版本。删掉自定义游戏参数再试。",
                CrashConfidence.Likely);
        if (RxHas(text, @"(?:Missing|Could not (?:find|load)|Failed to load|Unable to (?:find|load))[^\n]{0,30}asset(?:s)? index"))
            Add("找不到或无法读取资源索引文件：这个版本的资源没有下载完整。对该版本执行「补全文件」（⚙ 菜单里的「补全文件」）即可。",
                CrashConfidence.Likely);

        // ---------- 7. 存档 / 资源包 / 音频 ----------
        if (RxHas(text, @"session\.lock|The save is being accessed from another location"))
            Add("这个存档正被另一个游戏实例（或同步软件）占用：关闭其它正在运行的 Minecraft 窗口和服务端，再进入存档。",
                CrashConfidence.Likely);
        if (RxHas(tail, @"Invalid pack\.mcmeta|Failed to load resource ?pack|Couldn't load resource ?pack|pack_format"))
            Add("资源包/数据包的 pack.mcmeta 无效，或 pack_format 与当前游戏版本不匹配。换一个对应版本的资源包，或把出问题的包移出 resourcepacks/datapacks 文件夹。",
                CrashConfidence.Guess);
        if (RxHas(tail, @"Failed to open OpenAL device|ALC_INVALID_DEVICE|Error in class 'ALC|Sound engine started failed"))
            Add("音频设备初始化失败（OpenAL）：多为没有可用的声音输出设备，或声卡驱动异常。插上耳机/音箱并在 Windows 声音设置里选好默认输出设备，" +
                "必要时更新声卡驱动。这一般不会让游戏崩溃，只是没有声音。",
                CrashConfidence.Guess);

        return f.GroupBy(x => x.Text).Select(g => g.First()).Take(10).ToList();
    }

    /// <summary>
    /// 崩溃 mod 归因：不满足于"哪个方块/实体在 tick"这种笼统提示，进一步比对崩溃堆栈里
    /// 实际的 Java 代码包名跟已安装 mod jar 内部真实包含的 class 包名，直接指认"是这个
    /// mod 的问题"，而不是让用户自己拿着一串 com.xxx.yyy 包名去猜是谁。
    ///
    /// 做法：
    /// 1. 定位崩溃报告里最后一个 "Caused by:"（真正的根因异常，前面的往往只是包装层），
    ///    取它后面紧跟的调用栈 "at 包名.类名.方法名(...)" 行；
    /// 2. 从上到下找第一行**不属于** Minecraft/Mixin/加载器/JDK/常见第三方库自身代码的包名
    ///    ——这些框架代码即使出现在栈顶，也不代表"崩溃是它的锅"，真正有意义的是第一个
    ///    落在"玩家自己装的 mod 的代码"里的栈帧；
    /// 3. 遍历 mods 文件夹，读每个 jar 内部实际包含哪些 class 包名，找哪个 mod 的包名是
    ///    上面选中包名的前缀（或完全相等）——包名匹配长度最长的那个判定为"元凶"。
    ///
    /// 这是启发式但比"猜"可靠得多：包名是编译产物里的客观事实，一个 mod 的代码只会出现在
    /// 它自己声明的包名下，误判概率远低于关键字匹配。给 Certain 置信度。
    /// </summary>
    private static readonly string[] FrameworkPackagePrefixes =
    {
        "net.minecraft.", "com.mojang.", "net.fabricmc.", "net.minecraftforge.", "net.neoforged.",
        "org.spongepowered.asm.", "cpw.mods.", "java.", "javax.", "jdk.", "sun.", "com.sun.",
        "com.google.", "org.apache.", "it.unimi.dsi.", "io.netty.", "org.lwjgl.", "kotlin.", "kotlinx.",
        "com.electronwill.", "org.objectweb.asm.", "javassist."
    };

    private static List<CrashFinding> RunModAttributionRules(string text, string? modsDir)
    {
        var found = new List<CrashFinding>();
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return found;

        var causedByIdx = text.LastIndexOf("Caused by:", StringComparison.OrdinalIgnoreCase);
        var relevant = causedByIdx >= 0 ? text[causedByIdx..] : text;

        var stackPackages = Regex.Matches(relevant, @"^\s*at\s+([\w.$]+)\.[\w$<>]+\([^\n]*\)", RegexOptions.Multiline)
            .Take(30)
            .Select(m => m.Groups[1].Value)
            .ToList();
        if (stackPackages.Count == 0) return found;

        var candidatePackage = stackPackages.FirstOrDefault(pkg =>
            !FrameworkPackagePrefixes.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        if (candidatePackage == null) return found;

        (string jarName, string modName, int matchedLength)? best = null;
        foreach (var jar in Directory.GetFiles(modsDir, "*.jar"))
        {
            try
            {
                using var archive = ZipFile.OpenRead(jar);
                var matchedPkgLength = archive.Entries
                    .Where(en => en.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase) && en.FullName.Contains('/'))
                    .Select(en => string.Join('.', en.FullName[..en.FullName.LastIndexOf('/')].Split('/')))
                    .Distinct()
                    .Where(pkg => candidatePackage.Equals(pkg, StringComparison.OrdinalIgnoreCase)
                                  || candidatePackage.StartsWith(pkg + ".", StringComparison.OrdinalIgnoreCase))
                    .Select(pkg => (int?)pkg.Length)
                    .Max();

                if (matchedPkgLength.HasValue && (best == null || matchedPkgLength.Value > best.Value.matchedLength))
                {
                    var fileName = Path.GetFileName(jar);
                    var isDisabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    var modName = LocalModService.TryReadModName(jar, isDisabled) ?? Path.GetFileNameWithoutExtension(jar);
                    best = (fileName, modName, matchedPkgLength.Value);
                }
            }
            catch { /* 单个 mod jar 打不开/读取失败不影响其它 mod 的比对 */ }
        }

        if (best == null) return found;

        found.Add(new CrashFinding(
            $"根据崩溃堆栈里的代码包名（{candidatePackage}）比对已安装的 mod，判断这次崩溃是由「{best.Value.modName}」" +
            $"（文件：{best.Value.jarName}）引起的。建议：先去 Modrinth/CurseForge 检查这个 mod 有没有新版本，" +
            "更新到最新版本很可能就能解决；如果暂时没有更新，可以在「模组管理」页面先临时禁用它" +
            "（不需要删除，禁用后随时可以恢复），验证不再崩溃后再考虑要不要换个替代 mod。",
            CrashConfidence.Certain));

        return found;
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

        var m = Regex.Match(text, @"(?:exit code|Process exited with code|退出代码)[:\s]+(-?\d+|0x[0-9a-f]+)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return found;

        // 同时支持十进制（有符号/无符号）和 0x 十六进制写法，统一成有符号 32 位。
        var token = m.Groups[1].Value;
        long code;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) return found;
        }
        else if (!long.TryParse(token, out code)) return found;
        if (code > int.MaxValue && code <= uint.MaxValue) code = unchecked((int)(uint)code);

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
            case -1073741515:   // 0xC0000135 STATUS_DLL_NOT_FOUND
                found.Add(new CrashFinding(
                    "退出码 -1073741515（缺少系统运行库 DLL）：系统缺少 Java 或某个原生库依赖的 Visual C++ 运行库，" +
                    "也可能缺 DirectX 组件。先安装最新的 Microsoft Visual C++ 2015-2022 运行库（x64 版）；" +
                    "仍不行就点崩溃弹窗里的「补全 DirectX」，再重新启动。",
                    CrashConfidence.Likely));
                break;
            case -1073741701:   // 0xC000007B STATUS_INVALID_IMAGE_FORMAT
                found.Add(new CrashFinding(
                    "退出码 -1073741701（程序位数不匹配）：多半是 32 位和 64 位混用，例如 32 位 Java 加载了 64 位的库。" +
                    "在「设置」里改用 64 位 Java，并安装 x64 版 Visual C++ 运行库。",
                    CrashConfidence.Likely));
                break;
            case -1073740940:   // 0xC0000374 STATUS_HEAP_CORRUPTION
                found.Add(new CrashFinding(
                    "退出码 -1073740940（堆内存被破坏）：常见于显卡驱动问题、后台注入软件（悬浮窗/录屏/加速器），或某个 mod 的原生库。" +
                    "先更新显卡驱动并退出这类后台软件。",
                    CrashConfidence.Likely));
                break;
            case -1073741502:   // 0xC0000142 STATUS_DLL_INIT_FAILED
                found.Add(new CrashFinding(
                    "退出码 -1073741502（DLL 初始化失败）：某个运行库在加载时初始化失败。常见原因是杀毒软件/悬浮窗类软件拦截或注入、" +
                    "系统虚拟内存（页面文件）太小或被关闭、Visual C++ / DirectX 运行库损坏。退出这类后台软件，检查页面文件，" +
                    "必要时重装 Visual C++ 运行库或点崩溃弹窗里的「补全 DirectX」。",
                    CrashConfidence.Likely));
                break;
            case -1073741801:   // 0xC0000017 STATUS_NO_MEMORY
                found.Add(new CrashFinding(
                    "退出码 -1073741801（系统内存不足）：系统没有足够的内存/虚拟内存可分配。关闭其它占内存的程序，" +
                    "把游戏最大内存调小一点，并确认页面文件（虚拟内存）没被关闭。",
                    CrashConfidence.Likely));
                break;
            case -1073741571:   // 0xC00000FD STATUS_STACK_OVERFLOW
                found.Add(new CrashFinding(
                    "退出码 -1073741571（原生栈溢出）：某个原生库或 mod 自带的本地代码递归失控。先移除最近新增的 mod / 后台注入软件再试。",
                    CrashConfidence.Likely));
                break;
            case -1073741795:   // 0xC000001D STATUS_ILLEGAL_INSTRUCTION
                found.Add(new CrashFinding(
                    "退出码 -1073741795（非法指令）：某个原生库使用了这颗 CPU 不支持的指令（较旧的 CPU 常见），或 CPU/内存超频不稳定。" +
                    "还原超频；换一个 Java 发行版试试；如果是某个带原生库的 mod，更换或移除它。",
                    CrashConfidence.Likely));
                break;
            case -1073741510:   // 0xC000013A STATUS_CONTROL_C_EXIT
            case 143:           // SIGTERM
                found.Add(new CrashFinding(
                    $"退出码 {code}：游戏进程是被外部终止的（在任务管理器里结束、关闭了启动器，或被其它软件结束），不一定是游戏本身崩溃。",
                    CrashConfidence.Guess));
                break;
            case 137:           // SIGKILL，Linux/容器里多为内存不足被系统杀掉
                found.Add(new CrashFinding(
                    "退出码 137：进程被系统强制结束，多半是内存耗尽被系统杀掉。调低游戏最大内存、关闭后台程序后再试。",
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
            @"\bMod\s+['""]?([\w\-]+)['""]?\s*(?:has failed to load|failed to initialize|encountered an error)|" +
            @"An error occurred while (?:loading|initializing) mod\s*['""]?([\w\-]+)",
            RegexOptions.IgnoreCase);
        var failedModIds = modLoadFailMatch
            .SelectMany(m => m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1))
            // 过滤掉明显不是 mod id 的捕获（旧版把 FormattedException 这类异常类名的前缀、
            // 甚至单个字母当成了 mod id，才会出现「Formatted」「s」注入失败崩溃这种误报）。
            .Where(g => g.Success && g.Value.Length >= 3 && !NotModIdWords.Contains(g.Value))
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
        if (Regex.IsMatch(text, "OutOfMemoryError", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(text, @"Metaspace|unable to create (?:new )?native thread|GC overhead limit|Direct buffer memory", RegexOptions.IgnoreCase))
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
