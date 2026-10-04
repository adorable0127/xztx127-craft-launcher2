using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public enum JvmIssueSeverity
{
    /// <summary>不一定会让游戏起不来（被忽略 / 被覆盖 / 已弃用），只记日志，不拦截启动。</summary>
    Warning = 0,
    /// <summary>基本可以确定会让 JVM 启动失败，或让游戏根本不会运行。</summary>
    Fatal = 1,
}

/// <summary>一条 JVM 参数问题。Title 说“是什么问题”，Advice 说“怎么改”，两者拼起来就是给用户看的完整结论。</summary>
public sealed record JvmArgIssue(
    string Code,
    string Title,
    string Advice,
    JvmIssueSeverity Severity,
    CrashConfidence Confidence = CrashConfidence.Certain,
    string? Option = null)
{
    public string Text => Title + Advice;
}

/// <summary>
/// JVM（Java 虚拟机）启动参数错误的检测，分两条线：
///
/// 1) 事后检测 <see cref="Detect"/>：从游戏进程的控制台输出里认出 JVM 启动阶段的报错
///    （Unrecognized VM option、Invalid maximum heap size、Conflicting collector combinations ……）。
///    这类错误发生在游戏代码运行之前——不会有 crash-report、也没有 latest.log，只有进程 stderr，
///    所以只能看控制台输出，不能看磁盘上可能是上一局遗留的 latest.log。
/// 2) 事前预检 <see cref="Lint"/>：启动前就检查「设置里的内存」+「自定义 JVM 参数」，
///    把必然失败的写法（缺 -、Xms&gt;Xmx、-Xmx4096 没单位、写了 -version ……）在拉起进程之前拦住。
///
/// 想加新的检测：在 <see cref="Rules"/> 里加一行 <c>Rule(...)</c>（事后检测），
/// 或在 <see cref="Lint"/> 里加一段检查（事前预检）。其它崩溃类型的检测可以照这个形式另起一个类。
/// </summary>
public static class JvmArgumentDiagnostics
{
    private const long Kb = 1024;
    private const long Mb = 1024 * 1024;

    // ======================================================================
    // 一、事后检测：控制台输出 -> 问题列表
    // ======================================================================

    private sealed record DetectionRule(Regex Pattern, Func<Match, JvmArgIssue?> Build);

    private static DetectionRule Rule(string pattern, Func<Match, JvmArgIssue?> build)
        => new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant), build);

    /// <summary>
    /// 被 Java 版本移除/改名的参数 -> 说明。key 是去掉前缀 +/-、去掉 =值 之后的名字。
    /// </summary>
    private static readonly Dictionary<string, string> RemovedOptionHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UseConcMarkSweepGC"] = "CMS 垃圾回收器在 Java 14 起已被移除。删掉所有 -XX:...CMS... 相关参数，需要的话改用 -XX:+UseG1GC。",
        ["CMSIncrementalMode"] = "CMS 垃圾回收器在 Java 14 起已被移除。删掉所有 CMS 相关参数。",
        ["CMSParallelRemarkEnabled"] = "CMS 垃圾回收器在 Java 14 起已被移除。删掉所有 CMS 相关参数。",
        ["CMSInitiatingOccupancyFraction"] = "CMS 垃圾回收器在 Java 14 起已被移除。删掉所有 CMS 相关参数。",
        ["UseCMSInitiatingOccupancyOnly"] = "CMS 垃圾回收器在 Java 14 起已被移除。删掉所有 CMS 相关参数。",
        ["CMSClassUnloadingEnabled"] = "CMS 垃圾回收器在 Java 14 起已被移除。删掉所有 CMS 相关参数。",
        ["UseParNewGC"] = "ParNew 垃圾回收器在 Java 10 起已被移除，删掉它。",
        ["AggressiveOpts"] = "这个参数在 Java 12 起已被移除，删掉它。",
        ["UseFastAccessorMethods"] = "这个参数在新版 Java 里已被移除，删掉它。",
        ["MaxPermSize"] = "永久代在 Java 8 起被元空间取代，这个参数已经没有意义了，删掉它（需要的话用 -XX:MaxMetaspaceSize=）。",
        ["PermSize"] = "永久代在 Java 8 起被元空间取代，这个参数已经没有意义了，删掉它。",
        ["UseBiasedLocking"] = "偏向锁在新版 Java 里已被弃用并逐步移除，删掉这个参数。",
        ["UseZGC"] = "ZGC 需要 Java 11 以上才有（Java 15 起才算正式功能）。换成更高版本的 Java，或删掉这个参数。",
        ["ZGenerational"] = "分代 ZGC 需要 Java 21 以上。换成 Java 21+，或删掉这个参数。",
        ["UseShenandoahGC"] = "你的 Java 发行版可能不带 Shenandoah 垃圾回收器（例如 Oracle JDK）。换成 Temurin/Zulu 等发行版，或删掉这个参数。",
        ["Xincgc"] = "这个参数在新版 Java 里已被移除，删掉它。",
    };

    private static readonly Regex ModuleOnlyOption = new(
        @"^-{0,2}(?:add-opens|add-exports|add-reads|add-modules|module-path|upgrade-module-path|limit-modules|patch-module|enable-native-access)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly List<DetectionRule> Rules = new()
    {
        // ---- 不认识的参数 ----
        Rule(@"^\s*Unrecognized VM option\s+'(?<o>[^'\r\n]+)'", m => BuildUnrecognized(m.Groups["o"].Value)),
        Rule(@"^\s*Unrecognized option:\s*(?<o>\S+)", m => BuildUnrecognized(m.Groups["o"].Value)),

        // ---- -XX 写法不对 ----
        Rule(@"Improperly specified VM option\s+'(?<o>[^'\r\n]*)'", m => new JvmArgIssue(
            "jvm-improper-option",
            $"虚拟机参数「-XX:{m.Groups["o"].Value}」的写法不对。",
            "-XX 参数只有两种写法：开关类用 -XX:+名称（开）/ -XX:-名称（关），数值类用 -XX:名称=值（要带等号）。" +
            "检查是不是漏了 + 或 =，或者值里混进了空格、中文符号。",
            JvmIssueSeverity.Fatal, Option: m.Groups["o"].Value)),

        // ---- 需要先解锁的参数 ----
        Rule(@"VM option\s+'(?<o>[^'\r\n]+)'\s+is\s+(?<k>experimental|diagnostic)\b", m =>
        {
            var experimental = m.Groups["k"].Value.Equals("experimental", StringComparison.OrdinalIgnoreCase);
            var unlock = experimental ? "-XX:+UnlockExperimentalVMOptions" : "-XX:+UnlockDiagnosticVMOptions";
            return new JvmArgIssue(
                "jvm-needs-unlock",
                $"虚拟机参数「{m.Groups["o"].Value}」是{(experimental ? "实验性" : "诊断用")}参数，必须先解锁才能用。",
                $"在它前面加上 {unlock}（必须写在它之前，顺序反了无效），或者直接把这个参数删掉。",
                JvmIssueSeverity.Fatal, Option: m.Groups["o"].Value);
        }),

        // ---- 垃圾回收器冲突 ----
        Rule(@"Conflicting collector combinations", _ => new JvmArgIssue(
            "jvm-gc-conflict",
            "垃圾回收器参数冲突：同时指定了多个垃圾回收器。",
            "在自定义 JVM 参数里只保留一个 -XX:+UseG1GC / -XX:+UseParallelGC / -XX:+UseZGC / -XX:+UseSerialGC，其余的删掉。",
            JvmIssueSeverity.Fatal)),

        // ---- 堆内存 ----
        Rule(@"Initial heap size set to a larger value than the maximum heap size", _ => new JvmArgIssue(
            "jvm-xms-gt-xmx",
            "内存参数矛盾：初始内存（-Xms）比最大内存（-Xmx）还大。",
            "到「设置」里把最大内存调大（或把最小内存调小）；如果在自定义 JVM 参数里写过 -Xms / -Xmx，也一并检查，自定义的会覆盖设置里的。",
            JvmIssueSeverity.Fatal)),

        Rule(@"Invalid (?<k>maximum|initial) heap size:?\s*(?<o>-Xm[sx]\S*)?", m => new JvmArgIssue(
            "jvm-invalid-heap",
            $"{(m.Groups["k"].Value.Equals("maximum", StringComparison.OrdinalIgnoreCase) ? "最大" : "初始")}内存参数" +
            (m.Groups["o"].Success ? $"「{m.Groups["o"].Value}」" : "") + "无效。",
            "正确写法是数字紧跟单位，如 -Xmx4G 或 -Xmx4096M：不能漏单位、不能有空格、不能写小数（4.5G 不行，写 4608M）。" +
            "到「设置」/「自定义 JVM 参数」里检查。",
            JvmIssueSeverity.Fatal, Option: m.Groups["o"].Success ? m.Groups["o"].Value : null)),

        Rule(@"Too small (?<k>maximum|initial) heap", m => new JvmArgIssue(
            "jvm-heap-too-small",
            $"{(m.Groups["k"].Value.Equals("maximum", StringComparison.OrdinalIgnoreCase) ? "最大" : "初始")}内存设置得太小。",
            "最常见的原因是自定义参数里写成了 -Xmx4096（没写单位，Java 会当成 4096 字节）。补上单位，如 -Xmx4096M 或 -Xmx4G。",
            JvmIssueSeverity.Fatal)),

        Rule(@"Could not reserve enough space for\s+(?:(?<kb>\d+)\s*KB\s+)?object heap", m =>
        {
            var size = "";
            if (long.TryParse(m.Groups["kb"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
                size = $"（{kb / 1024} MB）";
            return new JvmArgIssue(
                "jvm-heap-reserve-failed",
                $"Java 无法一次性预留所需的堆内存{size}。",
                "分配的最大内存超过了这台电脑当前能提供的（或这个 Java 能用的）上限。常见原因：用了 32 位 Java（换 64 位 Java）、" +
                "最大内存设得比物理内存还大、系统可用内存/页面文件太少。到「设置」里把最大内存调小（建议不超过物理内存的一半）。",
                JvmIssueSeverity.Fatal);
        }),

        Rule(@"The specified size exceeds the maximum representable size", _ => new JvmArgIssue(
            "jvm-size-overflow",
            "某个内存参数的数值大到 Java 无法表示。",
            "检查自定义 JVM 参数里的 -Xmx / -Xms / -Xss 等，数值写多了位数（例如把 MB 当成了字节数）。",
            JvmIssueSeverity.Fatal)),

        Rule(@"Invalid thread stack size|The stack size specified is too small", _ => new JvmArgIssue(
            "jvm-invalid-xss",
            "线程栈大小参数（-Xss）无效或太小。",
            "-Xss 要带单位且不能太小，例如 -Xss1M。只写 -Xss512 会被当成 512 字节。不确定就直接删掉它。",
            JvmIssueSeverity.Fatal)),

        // ---- 少了开头的 -，参数被当成主类名 ----
        Rule(@"^\s*Error: Could not find or load main class\s+(?<c>\S+)", BuildMainClassIssue),

        // ---- -javaagent / -agentlib ----
        Rule(@"Error opening zip file or JAR manifest missing\s*:\s*(?<p>[^\r\n]+)", m => new JvmArgIssue(
            "jvm-agent-file",
            $"-javaagent 指定的文件打不开：{m.Groups["p"].Value.Trim()}。",
            "路径不存在、文件不是合法的 jar（缺少 manifest），或者下载到一半损坏了。检查自定义 JVM 参数里的 -javaagent: 路径；" +
            "如果这是离线自定义皮肤用的 authlib-injector，到账户里重新选一次皮肤，让启动器重新下载。",
            JvmIssueSeverity.Fatal, Option: m.Groups["p"].Value.Trim())),

        Rule(@"Could not find agent library\s+(?<l>\S+)", m => new JvmArgIssue(
            "jvm-agent-lib",
            $"找不到 -agentlib / -agentpath 指定的库「{m.Groups["l"].Value}」。",
            "检查自定义 JVM 参数里的 -agentlib: / -agentpath: 是否写对、对应的文件是否存在，不需要就删掉。",
            JvmIssueSeverity.Fatal, Option: m.Groups["l"].Value)),

        // ---- --add-modules / --module-path 写错 ----
        Rule(@"Error occurred during initialization of boot layer\s*[\r\n]+\s*java\.lang\.module\.FindException:\s*Module\s+(?<m>\S+)\s+not found", m => new JvmArgIssue(
            "jvm-module-not-found",
            $"Java 模块系统找不到模块「{m.Groups["m"].Value}」。",
            "多半是自定义 JVM 参数里的 --add-modules / --module-path 写错了，或者当前 Java 里没有这个模块（例如用了精简版 JRE）。" +
            "删掉这些自定义参数，或换成完整的 JDK。",
            JvmIssueSeverity.Fatal, CrashConfidence.Likely, m.Groups["m"].Value)),
    };

    /// <summary>
    /// 兜底：JVM 报了“创建虚拟机失败”但上面没有任何一条具体规则命中时，至少明确告诉用户
    /// “这是 JVM 启动参数层面的失败、游戏代码根本没跑起来”，而不是让用户对着一堆输出发呆。
    /// </summary>
    private static readonly Regex CouldNotCreateJvm = new(
        @"Could not create the Java Virtual Machine", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>从控制台输出里识别 JVM 参数相关的启动失败。没有命中返回空列表。同一类问题只保留第一条。</summary>
    public static IReadOnlyList<JvmArgIssue> Detect(string? consoleOutput)
    {
        var found = new List<JvmArgIssue>();
        if (string.IsNullOrWhiteSpace(consoleOutput)) return found;

        foreach (var rule in Rules)
        {
            foreach (Match m in rule.Pattern.Matches(consoleOutput))
            {
                JvmArgIssue? issue;
                try { issue = rule.Build(m); }
                catch { continue; /* 单条规则出错不能影响其它规则 */ }
                if (issue == null) continue;
                // 同一个 Code + 同一个选项只报一次（控制台里同一行常常出现两遍）。
                if (found.Any(f => f.Code == issue.Code && f.Option == issue.Option)) continue;
                found.Add(issue);
            }
        }

        if (found.Count == 0 && CouldNotCreateJvm.IsMatch(consoleOutput))
        {
            found.Add(new JvmArgIssue(
                "jvm-create-failed",
                "Java 虚拟机没能创建出来，游戏代码根本没有开始运行。",
                "这几乎总是 JVM 启动参数的问题。先看上面控制台输出里 Error 前一两行的具体提示；" +
                "最快的办法是清空「自定义 JVM 参数」再启动一次，能起来就说明问题在自定义参数里；" +
                "还不行就把「设置」里的最大内存调小，并确认用的是对应游戏版本的 64 位 Java。",
                JvmIssueSeverity.Fatal, CrashConfidence.Likely));
        }

        return found;
    }

    /// <summary>转成崩溃分析里统一使用的结论格式，让「日志页 / 崩溃弹窗」两处共用同一套结论。</summary>
    public static List<CrashFinding> ToFindings(string? text)
        => Detect(text).Select(i => new CrashFinding(i.Text, i.Confidence)).ToList();

    /// <summary>把问题列表排成“1. xxx\n\n2. xxx”的文本，给弹窗显示。</summary>
    public static string FormatForDialog(IEnumerable<JvmArgIssue> issues)
        => string.Join("\n\n", issues.Select((i, n) => $"{n + 1}. {i.Text}"));

    private static JvmArgIssue BuildUnrecognized(string rawOption)
    {
        var shown = rawOption.Trim();
        // 归一化：去掉前导 - / +、去掉 =值，剩下纯名字，用来查“已被移除”表。
        var name = shown.TrimStart('-', '+');
        var eq = name.IndexOf('=');
        if (eq >= 0) name = name[..eq];
        name = name.TrimStart('+', '-');

        string advice;
        if (ModuleOnlyOption.IsMatch(shown) || ModuleOnlyOption.IsMatch(name))
            advice = "这是 Java 9 以上才有的模块参数，当前用的 Java 太旧（多半是 Java 8）。换成这个游戏版本要求的 Java 17/21，或者删掉这个参数。";
        else if (RemovedOptionHints.TryGetValue(name, out var hint))
            advice = hint;
        else
            advice = "多半是拼写错了（参数名区分大小写），或者当前 Java 版本不支持它。检查自定义 JVM 参数的拼写，" +
                     "或换成这个游戏版本要求的 Java；不确定就把这个参数删掉。";

        return new JvmArgIssue("jvm-unrecognized-option", $"Java 不认识虚拟机参数「{shown}」。", advice,
            JvmIssueSeverity.Fatal, Option: shown);
    }

    private static JvmArgIssue? BuildMainClassIssue(Match m)
    {
        var c = m.Groups["c"].Value;
        if (c.Length == 0) return null;

        // 复制网页上的参数，开头的 - 经常被替换成长破折号 / 全角减号。
        if ("\u2010\u2011\u2012\u2013\u2014\u2015\u2212\uFF0D".IndexOf(c[0]) >= 0)
            return new JvmArgIssue("jvm-fancy-dash",
                $"参数「{c}」开头的“减号”不是普通的 -（是长破折号或全角符号），Java 把它当成了主类名。",
                "把自定义 JVM 参数里每个参数开头的横线都用英文键盘重新输入一遍（半角的 -）。",
                JvmIssueSeverity.Fatal, Option: c);

        // 像 Xmx4G / XX:+UseG1GC / Dfoo=bar / javaagent:xxx 这种：明显是少了开头的 -。
        if (Regex.IsMatch(c, @"^(?:X[A-Za-z]|XX:|D[\w.\-]+(?:=|$)|javaagent:|agentlib:|agentpath:|add-|enable-)"))
            return new JvmArgIssue("jvm-missing-dash",
                $"参数「{c}」前面缺少 -（减号），Java 把它当成了主类名。",
                $"应该写成「-{c}」。每个 JVM 参数都必须以 - 开头，否则游戏完全起不来。到自定义 JVM 参数里补上。",
                JvmIssueSeverity.Fatal, Option: c);

        return null; // 看起来是个正常的类名：多半是 classpath/加载器问题，不是 JVM 参数的锅。
    }

    // ======================================================================
    // 二、事前预检：设置里的内存 + 自定义 JVM 参数 -> 问题列表
    // ======================================================================

    /// <summary>后面紧跟一个独立取值的参数（--add-opens a=b 这种），取值本身不该被当成“缺 - 的参数”。</summary>
    private static readonly HashSet<string> OptionsWithSeparateValue = new(StringComparer.Ordinal)
    {
        "--add-opens", "--add-exports", "--add-reads", "--add-modules", "--module-path", "-p",
        "--upgrade-module-path", "--limit-modules", "--patch-module", "--enable-native-access",
        "-cp", "-classpath", "--class-path",
    };

    /// <summary>这些参数会让 Java 打印一段信息就退出（或不真正运行），游戏不会启动。</summary>
    private static readonly HashSet<string> ExitImmediatelyOptions = new(StringComparer.Ordinal)
    {
        "-version", "--version", "-fullversion", "-help", "--help", "-h", "-?", "-X", "--help-extra",
        "--list-modules", "--dry-run",
    };

    private static readonly Regex SizeOption = new(
        @"^-X(?<k>ms|mx|ss)(?<n>\d+)(?<u>[kKmMgG]?)$", RegexOptions.CultureInvariant);

    private static readonly Regex GcOption = new(
        @"^-XX:\+Use(?<g>Serial|Parallel|G1|ConcMarkSweep|Z|Shenandoah|Epsilon)GC$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 启动前检查。
    /// </summary>
    /// <param name="customJvmArgs">用户写的自定义 JVM 参数原始字符串，可以为空。</param>
    /// <param name="launcherMinMb">设置里的最小内存（启动器会生成 -Xms）。</param>
    /// <param name="launcherMaxMb">设置里的最大内存（启动器会生成 -Xmx）。</param>
    /// <param name="javaMajor">本次要用的 Java 主版本，未知传 null（跳过按版本判断的检查）。</param>
    public static List<JvmArgIssue> Lint(string? customJvmArgs, int launcherMinMb, int launcherMaxMb, int? javaMajor)
    {
        var issues = new List<JvmArgIssue>();
        var tokens = new List<string>();

        if (!string.IsNullOrWhiteSpace(customJvmArgs))
        {
            try { tokens = LauncherService.SplitArgsRespectingQuotes(customJvmArgs); }
            catch (FormatException)
            {
                issues.Add(new JvmArgIssue("lint-unclosed-quote",
                    "自定义 JVM 参数里有未闭合的双引号。",
                    "启动器会因此整段忽略你的自定义参数（不会崩溃，但你写的参数都不会生效）。把引号补全，或去掉多余的引号。",
                    JvmIssueSeverity.Warning));
            }
        }

        long? customXms = null, customXmx = null;
        var collectors = new HashSet<string>(StringComparer.Ordinal);
        var skipNext = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (skipNext) { skipNext = false; continue; }
            if (t.Length == 0) continue;

            // 参数文件 @file：Java 自己会展开，不检查内容。
            if (t[0] == '@') continue;

            // 1) 长破折号 / 全角符号开头
            if (t.Length > 0 && "\u2010\u2011\u2012\u2013\u2014\u2015\u2212\uFF0D".IndexOf(t[0]) >= 0)
            {
                issues.Add(new JvmArgIssue("lint-fancy-dash",
                    $"自定义参数「{t}」开头的“减号”不是普通的 -。",
                    "这是从网页/文档复制时带进来的长破折号或全角符号，Java 不认。用英文键盘把开头的 - 重新输入一遍。",
                    JvmIssueSeverity.Fatal, Option: t));
                continue;
            }

            // 2) 没有以 - 开头
            if (t[0] != '-')
            {
                var hint = Regex.IsMatch(t, @"^(?:X[A-Za-z]|XX:|D[\w.\-]+(?:=|$)|javaagent:|agentlib:|add-|enable-)")
                    ? $"应该写成「-{t}」。" : "";
                issues.Add(new JvmArgIssue("lint-missing-dash",
                    $"自定义参数「{t}」前面缺少 -（减号）。",
                    hint + "Java 会把它当成主类名，游戏会直接起不来（Could not find or load main class）。每个 JVM 参数都必须以 - 开头；" +
                    "如果它是上一个参数的取值，请用 = 连起来或检查是否多打了空格。",
                    JvmIssueSeverity.Fatal, Option: t));
                continue;
            }

            // 3) 取值是独立一个 token 的参数
            if (OptionsWithSeparateValue.Contains(t)) skipNext = true;

            // 4) 会让 Java 打印信息后直接退出的参数
            if (ExitImmediatelyOptions.Contains(t))
                issues.Add(new JvmArgIssue("lint-exit-immediately",
                    $"自定义参数「{t}」会让 Java 只打印信息就退出，游戏不会启动。",
                    "把它从自定义 JVM 参数里删掉。（它退出码通常是 0，所以启动器也不会把这种情况当成崩溃——看起来就是“点了启动没反应”。）",
                    JvmIssueSeverity.Fatal, Option: t));

            if (t == "-jar")
                issues.Add(new JvmArgIssue("lint-jar",
                    "自定义参数里写了 -jar。",
                    "启动器是用 -cp + 主类的方式启动游戏的，加上 -jar 会让 Java 忽略 classpath，游戏无法启动。删掉 -jar 及它后面的文件名。",
                    JvmIssueSeverity.Fatal, Option: t));

            // 5) -Xms / -Xmx / -Xss
            if (t.StartsWith("-Xms", StringComparison.Ordinal) ||
                t.StartsWith("-Xmx", StringComparison.Ordinal) ||
                t.StartsWith("-Xss", StringComparison.Ordinal))
            {
                var sm = SizeOption.Match(t);
                if (!sm.Success)
                {
                    issues.Add(new JvmArgIssue("lint-size-format",
                        $"内存参数「{t}」的格式不对。",
                        "正确写法是数字紧跟单位，如 -Xmx4G 或 -Xmx4096M：中间不能有空格，不能写小数（4.5G 不行，写 4608M）。",
                        JvmIssueSeverity.Fatal, Option: t));
                }
                else if (long.TryParse(sm.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    var factor = UnitFactor(sm.Groups["u"].Value);
                    var bytes = n > long.MaxValue / factor ? long.MaxValue : n * factor;
                    var kind = sm.Groups["k"].Value;
                    if (kind == "ss")
                    {
                        if (bytes < 108 * Kb)
                            issues.Add(new JvmArgIssue("lint-xss-small",
                                $"栈大小参数「{t}」太小（只有 {bytes} 字节）。",
                                "多半是漏了单位，-Xss1M 才是 1 兆。不确定就直接删掉 -Xss。",
                                JvmIssueSeverity.Fatal, Option: t));
                    }
                    else
                    {
                        if (bytes < Mb)
                            issues.Add(new JvmArgIssue("lint-heap-no-unit",
                                $"内存参数「{t}」只有 {bytes} 字节，几乎肯定是漏了单位。",
                                $"改成 -{(kind == "mx" ? "Xmx" : "Xms")}{sm.Groups["n"].Value}M（兆）或 {sm.Groups["n"].Value}G（吉）。",
                                JvmIssueSeverity.Fatal, Option: t));
                        // 只有数值合理时才参与“最终生效值”的比较，漏单位的情况上面已经单独报错了。
                        if (bytes >= Mb)
                        {
                            if (kind == "ms") customXms = bytes; else customXmx = bytes;
                        }
                    }
                }
                continue;
            }

            // 6) -XX 写法
            if (t.StartsWith("-XX:", StringComparison.Ordinal))
            {
                if (Regex.IsMatch(t, @"^-XX:[A-Za-z_]\w*$"))
                    issues.Add(new JvmArgIssue("lint-xx-improper",
                        $"虚拟机参数「{t}」的写法不对：缺少 + / - / =。",
                        $"开关类要写成 -XX:+{t[4..]}（开）或 -XX:-{t[4..]}（关）；数值类要写成 -XX:名称=值。Java 会报 Improperly specified VM option 并拒绝启动。",
                        JvmIssueSeverity.Fatal, Option: t));

                var gm = GcOption.Match(t);
                if (gm.Success) collectors.Add(gm.Groups["g"].Value);
            }

            // 7) 非 ASCII 字符出现在 -X 系参数名里（全角冒号、中文逗号等）
            if (t.StartsWith("-X", StringComparison.Ordinal) &&
                !t.StartsWith("-Xbootclasspath", StringComparison.Ordinal) &&
                !t.StartsWith("-Xlog", StringComparison.Ordinal) &&
                !t.StartsWith("-Xrun", StringComparison.Ordinal))
            {
                var namePart = t.Split('=')[0];
                if (namePart.Any(ch => ch > 127))
                    issues.Add(new JvmArgIssue("lint-non-ascii",
                        $"自定义参数「{t}」的参数名里混进了中文/全角字符。",
                        "常见于输入法没切到英文：全角的“＋”“：”“，”Java 都不认。用英文半角字符重新输入。",
                        JvmIssueSeverity.Fatal, Option: t));
            }

            // 8) -javaagent 文件是否存在（只检查绝对路径）
            if (t.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = t["-javaagent:".Length..];
                var jarEnd = rest.IndexOf(".jar=", StringComparison.OrdinalIgnoreCase);
                var path = jarEnd >= 0 ? rest[..(jarEnd + 4)] : rest;
                try
                {
                    if (Path.IsPathRooted(path) && !File.Exists(path))
                        issues.Add(new JvmArgIssue("lint-agent-missing",
                            $"-javaagent 指定的文件不存在：{path}。",
                            "Java 会报 Error opening zip file or JAR manifest missing 并直接退出。检查路径，或删掉这个参数。",
                            JvmIssueSeverity.Fatal, Option: path));
                }
                catch { /* 路径含非法字符等：交给 Java 自己报错，这里不重复判断 */ }
            }

            // 9) 按 Java 版本判断
            if (javaMajor is int major)
                CheckAgainstJavaVersion(t, major, issues);
        }

        // 多个垃圾回收器
        if (collectors.Count > 1)
            issues.Add(new JvmArgIssue("lint-gc-conflict",
                $"同时指定了多个垃圾回收器（{string.Join("、", collectors.OrderBy(c => c, StringComparer.Ordinal).Select(c => c + "GC"))}）。",
                "Java 会报 Conflicting collector combinations 并拒绝启动。只保留一个 -XX:+Use…GC，其余删掉。",
                JvmIssueSeverity.Fatal));

        // 最终生效的 Xms / Xmx：自定义参数写在后面，会覆盖启动器根据设置生成的值。
        var effectiveXms = customXms ?? launcherMinMb * Mb;
        var effectiveXmx = customXmx ?? launcherMaxMb * Mb;
        if (effectiveXms > effectiveXmx)
        {
            var fromCustom = customXms != null || customXmx != null;
            issues.Add(new JvmArgIssue("lint-xms-gt-xmx",
                $"初始内存 -Xms（{effectiveXms / Mb} MB）比最大内存 -Xmx（{effectiveXmx / Mb} MB）还大。",
                fromCustom
                    ? "Java 会报 Initial heap size set to a larger value than the maximum heap size 并拒绝启动。检查自定义 JVM 参数里的 -Xms / -Xmx，自定义的会覆盖「设置」里的内存。"
                    : "Java 会报 Initial heap size set to a larger value than the maximum heap size 并拒绝启动。到「设置」里把最大内存调大，或把最小内存调小。",
                JvmIssueSeverity.Fatal));
        }

        if (customXmx != null && customXmx.Value != launcherMaxMb * Mb && customXmx.Value < 1024L * 1024 * Mb)
            issues.Add(new JvmArgIssue("lint-xmx-override",
                $"自定义 JVM 参数里的 -Xmx（{customXmx.Value / Mb} MB）会覆盖「设置」里的最大内存（{launcherMaxMb} MB）。",
                "如果这不是你想要的，把自定义参数里的 -Xmx 删掉。",
                JvmIssueSeverity.Warning));

        // 致命的排前面
        return issues.OrderByDescending(i => i.Severity).ToList();
    }

    private static void CheckAgainstJavaVersion(string token, int major, List<JvmArgIssue> issues)
    {
        // Java 8 不认识任何模块参数
        if (major < 9 && ModuleOnlyOption.IsMatch(token))
        {
            issues.Add(new JvmArgIssue("lint-module-opt-old-java",
                $"自定义参数「{token}」是 Java 9 以上才有的模块参数，当前用的是 Java {major}。",
                "Java 会报 Unrecognized option 并拒绝启动。换成 Java 17/21，或删掉这个参数。",
                JvmIssueSeverity.Fatal, Option: token));
            return;
        }

        if (!token.StartsWith("-XX:", StringComparison.Ordinal) && !token.StartsWith("-Xincgc", StringComparison.Ordinal))
            return;

        var name = token.StartsWith("-XX:", StringComparison.Ordinal) ? token[4..] : token[1..];
        name = name.TrimStart('+', '-');
        var eq = name.IndexOf('=');
        if (eq >= 0) name = name[..eq];

        // (参数名, 在哪个 Java 主版本起有问题, 是否确定会失败)
        var verdict = name switch
        {
            "UseZGC" when major < 11 => (true, "ZGC 需要 Java 11 以上。"),
            "ZGenerational" when major < 21 => (true, "分代 ZGC 需要 Java 21 以上。"),
            "UseShenandoahGC" when major < 12 => (true, "Shenandoah 需要 Java 12 以上（且发行版要带它）。"),
            "UseConcMarkSweepGC" or "CMSIncrementalMode" or "CMSParallelRemarkEnabled" or "CMSInitiatingOccupancyFraction"
                or "UseCMSInitiatingOccupancyOnly" or "CMSClassUnloadingEnabled" when major >= 14
                => (false, "CMS 垃圾回收器在 Java 14 起已被移除。"),
            "UseParNewGC" when major >= 10 => (false, "ParNew 在 Java 10 起已被移除。"),
            "AggressiveOpts" when major >= 12 => (false, "这个参数在 Java 12 起已被移除。"),
            "MaxPermSize" or "PermSize" when major >= 8 => (false, "永久代在 Java 8 起已被取代，这个参数不再有效。"),
            "UseBiasedLocking" when major >= 18 => (false, "偏向锁在 Java 18 起已被弃用。"),
            "Xincgc" when major >= 9 => (false, "这个参数在 Java 9 起已被移除。"),
            _ => (false, ""),
        };
        if (verdict.Item2.Length == 0) return;

        var fatal = verdict.Item1;
        issues.Add(new JvmArgIssue("lint-removed-option",
            $"自定义参数「{token}」不适用于当前的 Java {major}：{verdict.Item2}",
            fatal
                ? "Java 会报 Unrecognized VM option 并拒绝启动。换成合适版本的 Java，或删掉这个参数。"
                : "当前 Java 会忽略它，部分版本甚至会直接拒绝启动。建议删掉。",
            fatal ? JvmIssueSeverity.Fatal : JvmIssueSeverity.Warning,
            fatal ? CrashConfidence.Certain : CrashConfidence.Likely,
            token));
    }

    private static long UnitFactor(string unit) => unit switch
    {
        "k" or "K" => Kb,
        "m" or "M" => Mb,
        "g" or "G" => 1024 * Mb,
        _ => 1,
    };
}
