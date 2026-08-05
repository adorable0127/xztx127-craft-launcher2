using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 负责根据版本 json、账户信息、Java 路径拼装启动参数并启动游戏进程，
/// 同时支持把等价的启动命令导出为 .bat 脚本（"加入到导出启动脚本"需求）。
/// </summary>
/// <summary>
/// 启动时发现依赖库文件缺失（常见于远古版本如 1.8 及更早）时抛出，携带结构化的
/// VersionId，让 UI 层能直接调用 DownloadService.DownloadLibrariesOnlyAsync
/// 做"一键补全依赖库后自动重试启动"，不需要用户手动去"版本选择"页重装整个版本。
/// </summary>
public class MissingLibrariesException : InvalidOperationException
{
    public IReadOnlyList<string> MissingLibraryNames { get; }
    public string VersionId { get; }

    public MissingLibrariesException(string versionId, List<string> missingLibraryNames, string message)
        : base(message)
    {
        VersionId = versionId;
        MissingLibraryNames = missingLibraryNames;
    }
}

public class LauncherService
{
    public class LaunchOptions
    {
        public string MinecraftDir { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string JavaPath { get; set; } = "";
        public Account Account { get; set; } = new();
        public int MinMemoryMb { get; set; } = 1024;
        public int MaxMemoryMb { get; set; } = 4096;
        public int WindowWidth { get; set; } = 854;
        public int WindowHeight { get; set; } = 480;

        /// <summary>是否额外弹出一个独立 CMD 窗口，实时镜像游戏控制台输出，方便在命令行里直接查看日志。</summary>
        public bool ShowConsoleWindow { get; set; } = false;

        /// <summary>
        /// 是否对这个版本启用"版本隔离"：开启后 --gameDir 会指向
        /// .minecraft/versions/&lt;VersionId&gt;/ 而不是 .minecraft 根目录，
        /// 这样 mods、resourcepacks、saves、config、shaderpacks 等就都是每个版本各自独立的，
        /// 不会跟其他版本共用一份、互相污染。这跟官方启动器/HMCL/PCL 的默认行为一致。
        /// </summary>
        public bool IsolateVersion { get; set; } = true;

        /// <summary>实际使用的游戏运行目录 (--gameDir)。由 BuildArguments 根据 IsolateVersion 计算后写入，
        /// 供 Launch() 之后用于设置进程工作目录，调用方不需要自己算。</summary>
        public string EffectiveGameDir { get; internal set; } = "";

        /// <summary>
        /// 游戏内语言，格式为 Minecraft options.txt 认识的 lang 值，如 "zh_cn"、"en_us"。
        /// 为空/null 时不写入、不干预，保留玩家在游戏内自己选过的语言。
        /// </summary>
        public string? GameLanguage { get; set; }

        /// <summary>
        /// 用户自定义 JVM 启动参数（原始字符串，未切分），仅高手模式下由 UI 传入非空值。
        /// 拼接位置在 BuildArguments 里位于官方 arguments.jvm 解析出的参数之后、"-cp" 之前。
        /// </summary>
        public string? CustomJvmArgs { get; set; }

        /// <summary>
        /// 离线自定义皮肤所需的额外 JVM 参数(-javaagent 挂载 authlib-injector 等)，由调用方
        /// 通过 <see cref="SkinService.BuildSkinJvmArgs"/> 预先算好后传入——LauncherService 本身
        /// 不负责判断"这个账户是不是该用万能皮肤补丁"，只负责在合适的位置原样拼接这些参数。
        /// 拼接位置在 CustomJvmArgs 之前(官方参数之后)，同样遵循"后面覆盖前面"的 JVM 惯例，
        /// 让用户的 CustomJvmArgs 如果凑巧也设置了同名 -D 属性时能够覆盖这里的默认值。
        /// </summary>
        public List<string>? SkinJvmArgs { get; set; }

        /// <summary>
        /// 游戏内左下角的"版本类型"水印文字（对应 --versionType 参数，Minecraft 客户端
        /// 原生就会把这个参数值渲染在主菜单/游戏内左下角，官方启动器传的是 "release"/
        /// "snapshot" 等，第三方启动器习惯借用这个位置显示"启动器品牌+版本"，比如
        /// PCL2 传的是 "Plain Craft Launcher 2"、HMCL 传的是 "HMCL"。
        /// 见 AppConfig.GameVersionTypeLabel 的注释——由用户在设置页决定显示什么文字，
        /// 默认 "XCL2"，留空则退回官方原始的 "release"（完全不显示品牌水印）。
        /// </summary>
        public string? VersionTypeLabel { get; set; }

        /// <summary>
        /// 启动前执行的命令（高手模式可选，例如启动前先跑一个脚本备份存档/同步配置）。
        /// 原始命令行字符串，会通过系统默认 shell(cmd /c) 执行，工作目录设为 EffectiveGameDir。
        /// 执行失败/返回非 0 不会阻止游戏启动——这只是一个辅助钩子，不是启动的前置条件，
        /// 失败了应该让用户自己发现（通过日志），而不是把游戏也一起卡住。
        /// </summary>
        public string? PreLaunchCommand { get; set; }

        /// <summary>
        /// 启动后自动加入的服务器地址（形如 "play.example.com" 或 "1.2.3.4:25565"），
        /// 对应 Minecraft 1.20+ 支持的 --quickPlayMultiplayer 启动参数：游戏加载完主菜单后
        /// 会跳过手动进多人游戏列表点服务器这一步，直接尝试连接这个地址。
        /// 为空/null 时不传这个参数，行为等同于之前（游戏正常进主菜单，不自动连接任何服务器）。
        /// 只有当前正在启动的这个 version json 的 arguments.game 里真的声明了
        /// --quickPlayMultiplayer 这个键时才会生效（1.20.6 以前的版本没有这个参数，
        /// 传了也不会有任何效果，BuildArguments 内部会自动判断，不需要调用方关心版本号）。
        /// </summary>
        public string? AutoJoinServerAddress { get; set; }
    }

    /// <summary>
    /// 读取指定版本要求的 Java 主版本号，用于启动前"自动匹配 Java"。
    /// 综合两个来源，取两者中较大的一个（更严格的那个）：
    ///   1) version json (含 inheritsFrom 继承链) 里的 javaVersion.majorVersion——游戏本体的要求。
    ///   2) mods 目录下每个 mod jar 内 fabric.mod.json 的 depends["java"] 字段——
    ///      Fabric mod 生态里常见的写法是 "depends": { "java": ">=25" }，这是 mod 自己声明的
    ///      最低 Java 版本要求，跟游戏本体的要求是两回事、经常比游戏本体的要求更新更高
    ///      (典型例子：游戏本体 1.20.4 只要求 Java 17，但某个 mod 用到了 Java 21 才有的新特性，
    ///      在 fabric.mod.json 里声明了 "java": ">=21")。
    ///      之前的实现完全没有这一步，只看 version json，导致："游戏本体能用 Java 17 启动"，
    ///      于是自动匹配就下载/选用了 17，但装的 Fabric mod 实际要求 21+，
    ///      一进游戏 Fabric Loader 就报 "Incompatible mods found"。
    ///
    ///      注意：1.26.2 这类新版本的 Java 25 要求属于游戏本体自己的官方要求(Mojang 在
    ///      version json 的 javaVersion.majorVersion 字段里发布的)，不是 mod 声明的，
    ///      走的是上面 fromVersionJson 这一路——只要客户端拿到的 version json 是 Mojang
    ///      官方最新数据，这里不需要任何针对 26.2 的硬编码特判就能读到正确的 25。
    ///      如果 26.2 仍然选用了 Java 21，先检查本地缓存/写入的 26.2 version json 里
    ///      javaVersion.majorVersion 是不是 21（旧缓存/手动编辑残留），而不是来查这段
    ///      mod 检测逻辑——服务端没有 version json 可读，所以另外维护了一张版本估算表
    ///      (见 ServerJavaRequirement)，那张表之前没跟上 26.2，已在那边单独修复。
    /// 两个来源都没有效信息时返回 null，调用方应回退到"不限定版本，随便找一个"的旧逻辑。
    /// </summary>
    public static int? GetRequiredJavaMajorVersion(string minecraftDir, string versionId, bool isolateVersion = true)
    {
        int? fromVersionJson = null;
        try
        {
            var versionDir = Path.Combine(minecraftDir, "versions", versionId);
            var versionJsonPath = ResolveVersionFile(versionDir, versionId, "json");
            if (versionJsonPath != null)
            {
                var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(versionJsonPath));

                var ownMajor = detail?.JavaVersion?.MajorVersion;
                if (ownMajor is int v && v > 0) fromVersionJson = v;

                // 当前版本自己没写 javaVersion 时，很多 Fabric/Forge 等加载器版本继承自原版，
                // 原版 json 里才有这个字段，需要顺着 inheritsFrom 往上找一层。
                if (fromVersionJson == null && !string.IsNullOrEmpty(detail?.InheritsFrom))
                {
                    var parentDir = Path.Combine(minecraftDir, "versions", detail.InheritsFrom);
                    var parentJsonPath = ResolveVersionFile(parentDir, detail.InheritsFrom, "json");
                    if (parentJsonPath != null)
                    {
                        var parent = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
                        var parentMajor = parent?.JavaVersion?.MajorVersion;
                        if (parentMajor is int pv && pv > 0) fromVersionJson = pv;
                    }
                }
            }
        }
        catch { /* 读取/解析失败时静默忽略这一路来源 */ }

        int? fromMods = null;
        try
        {
            fromMods = GetMaxRequiredJavaMajorFromMods(minecraftDir, versionId, isolateVersion);
        }
        catch { /* 读取/解析失败时静默忽略这一路来源 */ }

        // 修复：1.17 之前的原版 version json 根本不会写 javaVersion 字段(Mojang 从 1.17 起
        // 才开始发布这个字段)，也不一定顺着 inheritsFrom 能找到，导致 fromVersionJson 和
        // fromMods 都是 null，上层就完全不限定版本，随手抓到系统里第一个 Java(常见是新装的
        // Java 21+)去启动 1.16 及更早的游戏，结果崩溃。这里在两路都拿不到结果时，兜底按
        // Mojang 官方"最低 Java 主版本要求"历史表估算一个版本号，跟 ServerJavaRequirement
        // 用的是同一套换算规则(1.16 及更早 -> Java 8；1.17 -> Java 16；1.18~1.20.4 -> Java 17；
        // 1.20.5+ -> Java 21 等)，不再让旧版本"裸奔"去匹配任意 Java。
        if (fromVersionJson == null && fromMods == null)
        {
            var baseMcVersion = ExtractBaseMinecraftVersion(versionId);
            if (baseMcVersion != null)
                return ServerJavaRequirement.EstimateMajorVersionForMcVersion(baseMcVersion);
            return null;
        }

        if (fromVersionJson == null) return fromMods;
        if (fromMods == null) return fromVersionJson;
        return Math.Max(fromVersionJson.Value, fromMods.Value);
    }

    /// <summary>
    /// 扫描这个版本 mods 目录下所有 jar，读取里面 fabric.mod.json 的 "depends": { "java": "..." }
    /// (以及少数 mod 用 "requires" 字段写同样的意思)，解析出其中声明的最低 Java 主版本号，
    /// 返回所有 mod 里要求的最大值(最严格的那个)。
    /// mods 目录位置跟"版本隔离"开关一致：开启时是 versions/&lt;id&gt;/mods，
    /// 关闭时是 .minecraft 根目录下的 mods（这里传入的 isolateVersion 需要和实际启动时
    /// 使用的隔离设置保持一致，否则会扫到错误的 mods 目录）。
    /// </summary>
    private static int? GetMaxRequiredJavaMajorFromMods(string minecraftDir, string versionId, bool isolateVersion)
    {
        var gameDir = isolateVersion ? Path.Combine(minecraftDir, "versions", versionId) : minecraftDir;
        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir)) return null;

        int? max = null;
        foreach (var jarPath in Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            int? require;
            try { require = ReadFabricModJavaRequirement(jarPath); }
            catch { continue; } // 单个 mod jar 读取失败(损坏/非法 zip 等)不应该影响其他 mod 的判断

            if (require is int r && (max == null || r > max)) max = r;
        }
        return max;
    }

    /// <summary>从版本 ID 里提取出基础原版 Minecraft 版本号，用于兜底估算 Java 要求。
    /// 兼容常见命名：纯原版 "1.16.5"、带加载器后缀 "1.16.5-forge-36.2.34"、
    /// "fabric-loader-0.15.0-1.16.5"、"1.16.5-Fabric 0.15.0" 等——按顺序在整个字符串里找
    /// 第一段形如 "1.x" 或 "1.x.y" 的数字序列即可，不需要认出具体是哪个加载器。</summary>
    internal static string? ExtractBaseMinecraftVersion(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;

        // 修复：原来的正则写死成 `1\.\d{1,2}`，只认 1.x 这一种命名。
        // Minecraft 从 26 起改成了年份制版本号（26.1 / 26.2，见下载中心和资源详情页的版本列表），
        // 这类版本 ID 在旧正则下一个都匹配不到 → ExtractBaseMinecraftVersion 返回 null
        // → GetRequiredJavaMajorVersion 直接 return null → 上层完全不限定 Java 版本，
        // 随手抓到系统里第一个 Java 就去启动。这正是"Java 自动匹配对新版本失效"的根因。
        //
        // 改用 VersionInfoResolver.ExtractAnyVersion：它同时认三种命名——
        // 传统 1.16.5 / 年份制 26.2 / 快照 24w14a / 预发布 1.21-pre1，
        // 且优先匹配带后缀的形态，避免 "1.21-pre1" 被截成 "1.21"。
        // 这样版本号解析在全项目只有一套口径（导出整合包那边用的也是它）。
        return VersionInfoResolver.ExtractAnyVersion(versionId);
    }

    /// <summary>从单个 mod jar 里读取 fabric.mod.json 的 depends.java (或 requires.java) 字段，
    /// 解析出其中声明的最低 Java 主版本号。字段值形如 ">=25"、"&gt;=17"、"25" 等，
    /// 这里只关心"最低版本号"这个数字本身，不需要精确实现完整的版本范围语法。</summary>
    private static int? ReadFabricModJavaRequirement(string jarPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(jarPath);
        var entry = archive.GetEntry("fabric.mod.json");
        if (entry == null) return null;

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        foreach (var section in new[] { "depends", "requires" })
        {
            if (!root.TryGetProperty(section, out var depends)) continue;
            if (!depends.TryGetProperty("java", out var javaReq)) continue;
            if (javaReq.ValueKind != JsonValueKind.String) continue;

            var parsed = ParseMinJavaMajorFromRequirementString(javaReq.GetString() ?? "");
            if (parsed != null) return parsed;
        }
        return null;
    }

    /// <summary>从形如 ">=25"、"&gt;=17"、"25"、">=1.8" 这样的版本要求字符串里提取出
    /// "最低需要的 Java 主版本号"这一个数字。兼容两种历史写法(跟 JavaService.ParseJavaMajorVersion
    /// 用的是同一套规则)：
    /// - 新式(Java 9+): "25"/"&gt;=25"/"25.x" -> 主版本号就是第一段数字 "25"
    /// - 旧式(Java 8 及以前): "1.8"/"&gt;=1.8" -> 第一段数字是 "1"，真正的主版本号在第二段 "8"
    /// 只取字符串里的数字段，足以覆盖 fabric.mod.json "java" 字段的实际写法(一个简单的最低版本
    /// 声明，不是完整的 semver 比较符组合表达式)。</summary>
    internal static int? ParseMinJavaMajorFromRequirementString(string requirement)
    {
        // 按 . 拆分后逐段提取数字，忽略比较符(>= <= > < ~ ^ 空格等非数字字符)。
        var segments = requirement.Split('.')
            .Select(seg => new string(seg.Where(char.IsDigit).ToArray()))
            .Where(seg => seg.Length > 0)
            .Select(seg => int.TryParse(seg, out var n) ? n : (int?)null)
            .Where(n => n != null)
            .Select(n => n!.Value)
            .ToList();

        if (segments.Count == 0) return null;

        // "1.8" 这种旧格式第一段永远是 1，真正版本号在第二段；否则第一段就是主版本号。
        if (segments[0] == 1 && segments.Count > 1) return segments[1];
        return segments[0];
    }

    /// <summary>启动游戏，返回封装了进程 + 实时输出缓冲的 GameProcessInfo，供进程管理/日志面板使用。</summary>
    public GameProcessInfo Launch(LaunchOptions opts)
    {
        var (mainClass, args) = BuildArguments(opts); // 内部会设置 opts.EffectiveGameDir

        // 启动前执行命令：best-effort，失败只记录不阻断——见 LaunchOptions.PreLaunchCommand 上的注释。
        if (!string.IsNullOrWhiteSpace(opts.PreLaunchCommand))
        {
            try
            {
                var preLaunchPsi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {opts.PreLaunchCommand}",
                    WorkingDirectory = Directory.Exists(opts.EffectiveGameDir) ? opts.EffectiveGameDir : opts.MinecraftDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var preProcess = Process.Start(preLaunchPsi);
                preProcess?.WaitForExit(10_000); // 最多等 10 秒，避免一个卡住的脚本把启动流程无限期挂起
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogTechnicalDetail($"[启动前执行命令失败] 命令：{opts.PreLaunchCommand}\n{ex}");
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = opts.JavaPath,
            WorkingDirectory = opts.EffectiveGameDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var info = new GameProcessInfo(process, opts.VersionId, opts.Account.DisplayLabel, opts.EffectiveGameDir);
        info.BeginReadOutput();
        return info;
    }

    /// <summary>导出等效的 .bat 启动脚本到 xcl2/scripts/ 下，方便用户脱离启动器直接双击运行。</summary>
    public string ExportLaunchScript(LaunchOptions opts)
    {
        var (_, args) = BuildArguments(opts); // 内部会设置 opts.EffectiveGameDir
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("REM 由 XCL2 (xztx127-craft-launcher 2) 自动生成的启动脚本");
        // 显式切到 UTF-8 代码页(65001)，配合下面写文件时用的 UTF-8 BOM，
        // 双击运行时中文注释/路径不会因为系统默认 GBK 代码页而乱码。
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"cd /d \"{opts.EffectiveGameDir}\"");
        sb.Append('"').Append(opts.JavaPath).Append('"');
        foreach (var a in args)
        {
            sb.Append(' ');
            sb.Append(a.Contains(' ') ? $"\"{a}\"" : a);
        }
        sb.AppendLine();
        sb.AppendLine("pause");

        var scriptsDir = Path.Combine(App.DataDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        var path = Path.Combine(scriptsDir, $"launch_{opts.VersionId}.bat");

        // 之前这里用 Encoding.GetEncoding("GBK")：.NET 8 默认不注册 GBK 等代码页
        // (需要额外引入 System.Text.Encoding.CodePages 包并调用 RegisterProvider)，
        // 直接调用会抛出 "'GBK' is not a supported encoding name"，导致整个启动流程崩溃、
        // 游戏完全无法启动。改为写带 BOM 的 UTF-8：Windows 10+ 的 cmd.exe 能正确识别
        // UTF-8 BOM 并按 UTF-8 显示中文注释，不再依赖 GBK 代码页。
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>
    /// 优先按预期文件名查找；改名后的版本文件夹里，内部的 json/jar 文件名不会跟着文件夹名一起变，
    /// 所以找不到时退而求其次——如果该目录下这种后缀的文件就唯一一个，直接认它就是。
    /// </summary>
    private static string? ResolveVersionFile(string dir, string preferredBaseName, string extension)
    {
        var exact = Path.Combine(dir, $"{preferredBaseName}.{extension}");
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        var matches = Directory.GetFiles(dir, $"*.{extension}");
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// 构造传给游戏 "--version" 参数的展示用字符串。
    ///
    /// 背景：曾经尝试过在这个参数后面统一追加 " XCL2" 水印，期望主菜单左下角那行版本
    /// 文字（如 "Minecraft 26.2（已修改）"）能变成带 XCL2 后缀的样子，参考 PCL 等
    /// 主流第三方启动器打品牌标识的做法。但用户实测后发现新版本(26.2)完全没有效果。
    ///
    /// 经排查确认：Minecraft 主菜单这行版本文字的来源，会因版本新旧而不同——
    /// 约 18w47b(1.13 前后)开始，client.jar 内部自带一份独立的 version.json 元数据，
    /// 新版本客户端优先读取这份内嵌数据来显示版本号，完全不采信启动参数里的
    /// "--version"；而更老的版本没有这套内嵌机制，主菜单文字就是直接显示 "--version"
    /// 参数原文，因此追加水印在老版本上是真实有效的，这也和用户"PCL 低版本能做到、
    /// 高版本做不到"的实测结果完全吻合。
    ///
    /// 由于我们外部管理的 version json 里，较新版本会额外带一个 clientVersion 字段
    /// (老版本没有这个字段，见 VersionDetail.ClientVersion 的说明)，用它的有无作为
    /// "这是不是新版本机制"的判断依据：老版本(没有 clientVersion)才追加水印，新版本
    /// 直接原样传版本号，不做实际上无效、还可能干扰崩溃报告/存档记录的多余改动。
    /// Fabric/Forge 等 loader 版本会 inheritsFrom 原版，clientVersion 字段实际写在
    /// 原版(parent) json 里，所以要同时检查 detail 和 parent 两者。
    /// </summary>
    private static string BuildDisplayVersion(string versionId, VersionDetail detail, VersionDetail? parent)
    {
        var isModernClient = !string.IsNullOrEmpty(detail.ClientVersion) || !string.IsNullOrEmpty(parent?.ClientVersion);
        return isModernClient ? versionId : $"{versionId} XCL2";
    }

    /// <summary>
    /// 把用户手写的一整行 JVM 参数字符串切分成参数数组，正确处理双引号包裹的带空格参数
    /// （例如 -Dfoo="some value with spaces"）。不能用简单的 string.Split(' ')，
    /// 否则带引号的参数会被错误地拆成多段。
    /// 规则：不在引号内的空白是分隔符；引号本身不保留在结果里；未闭合的引号视为解析失败
    /// （抛异常，由调用方 catch 后忽略这段自定义参数，避免拼出语法错误的命令行）。
    /// </summary>
    internal static List<string> SplitArgsRespectingQuotes(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasContent = true;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasContent)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasContent = false;
                }
                continue;
            }
            current.Append(c);
            hasContent = true;
        }

        if (inQuotes)
            throw new FormatException("自定义 JVM 参数里有未闭合的引号");

        if (hasContent)
            result.Add(current.ToString());

        return result;
    }

    private (string mainClass, List<string> args) BuildArguments(LaunchOptions opts)
    {
        var versionDir = Path.Combine(opts.MinecraftDir, "versions", opts.VersionId);

        // 版本隔离：开启时 mods/resourcepacks/saves/config/shaderpacks 等都放在这个版本自己的
        // 文件夹下 (.minecraft/versions/<版本号>/)，不跟其他版本共用；关闭时则沿用旧行为，
        // 所有版本共用 .minecraft 根目录下同一份 mods 等文件夹。
        var gameDir = opts.IsolateVersion ? versionDir : opts.MinecraftDir;
        Directory.CreateDirectory(gameDir);
        opts.EffectiveGameDir = gameDir;

        // 写入游戏语言 + 跳过官方首次启动引导：Minecraft 客户端只认 <gameDir>/options.txt
        // 里的字段，早期版本才支持的 --lang 启动参数目前基本已被客户端忽略，只靠命令行参数
        // 完全无法可靠控制语言——这正是"设置里选了中文，进游戏还是英文"这个 bug 的根因，
        // 而不是资源下载损坏（那个问题已经在 DownloadService 里通过 SHA1 校验修复了）。
        //
        // 同时，Minecraft 判断"是否首次启动、要不要弹 Welcome to Minecraft / 辅助功能引导页"
        // 的依据是 options.txt 里的 onboardAccessibility 字段(为 true 或文件不存在时会弹出)，
        // 不受 lang 字段控制。参考 PCL 等主流第三方启动器的做法：预先生成一份"看起来已经
        // 跑过一次"的 options.txt(onboardAccessibility:false + 一批配套的新手引导已完成
        // 标记字段)，游戏检测到这些字段就会认为不是第一次启动，直接进主菜单，不再弹出引导页。
        if (!string.IsNullOrWhiteSpace(opts.GameLanguage))
        {
            try { ApplyGameLanguage(gameDir, opts.GameLanguage!); }
            catch { /* options.txt 写入失败不应该阻止游戏启动，语言只是体验问题不是功能性问题 */ }
        }

        // 版本文件夹允许被用户改名(不少第三方启动器都支持这么整理)；改名只影响"文件夹"这一层，
        // 文件夹里面的 .json 文件名本身不会跟着变，所以这里做改名容错查找。
        var versionJsonPath = ResolveVersionFile(versionDir, opts.VersionId, "json");
        if (versionJsonPath == null)
            throw new InvalidOperationException(
                $"找不到版本「{opts.VersionId}」对应的 version json 文件（文件夹：{versionDir}）。" +
                "如果你手动改过这个版本文件夹的名字，请确认文件夹里的 .json 文件本身没有被误删。");
        var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(versionJsonPath)) ?? new VersionDetail();

        // 支持 inheritsFrom（Fabric/Forge/NeoForge/Quilt 的版本 json 通常继承原版）
        VersionDetail? parent = null;
        string? parentDir = null;
        if (!string.IsNullOrEmpty(detail.InheritsFrom))
        {
            parentDir = Path.Combine(opts.MinecraftDir, "versions", detail.InheritsFrom);
            var parentJsonPath = ResolveVersionFile(parentDir, detail.InheritsFrom, "json");
            if (parentJsonPath != null)
                parent = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
        }

        var mainClass = detail.MainClass;
        if (string.IsNullOrEmpty(mainClass) && parent != null) mainClass = parent.MainClass;

        var librariesDir = Path.Combine(opts.MinecraftDir, "libraries");
        var classpath = new List<string>();
        var missingLibraries = new List<string>();

        void AddLibs(VersionDetail d)
        {
            // 版本 json 里经常混杂 Linux/macOS 专用的 native 库条目（用 rules 按操作系统限定），
            // 这些条目在下载阶段本来就不会为 Windows 用户下载。之前这里没有做同样的平台过滤，
            // 导致把"压根没打算下载"的 Linux/macOS 库也当成"缺失"，纯净原版都会报依赖库缺失。
            foreach (var lib in d.Libraries.Where(l => l.IsApplicableToCurrentOs()))
            {
                // 远古版本(1.8 及更早)的 lwjgl-platform / jinput-platform / twitch-platform 等条目，
                // 是"纯 natives 聚合库"：完全没有 downloads.artifact（不需要放进 classpath 的普通
                // jar），只有 natives + downloads.classifiers（给 DownloadService 提取成 windows
                // natives 目录下的 dll）。PCL/HMCL 对这类条目的处理方式是：只管 natives 是否解压到位，
                // 不要求它们有独立的 classpath jar。
                //
                // 之前这里只要 Downloads?.Artifact 缺失，就无差别地走 GetMavenPath() 兜底，把它当成
                // Fabric/Quilt 风格的 "name+url" 库条目处理——但 lwjgl-platform 这类条目根本没有
                // "url" 字段，GetMavenPath() 硬凑出的 "lwjgl-platform-2.9.0.jar" 这种路径在
                // Mojang/BMCLAPI 仓库里从来就不存在、也从未被 DownloadService 下载过，于是被误判成
                // "缺失"，导致所有远古版本（哪怕库文件全部下载完整）启动前都会被拦下来报错。
                if (lib.Downloads?.Artifact == null && lib.Natives != null && lib.Natives.Count > 0)
                    continue;

                string? relativePath = null;
                if (lib.Downloads?.Artifact is { } art && !string.IsNullOrEmpty(art.Path))
                {
                    relativePath = art.Path;
                }
                else if (!string.IsNullOrEmpty(lib.Url))
                {
                    // Fabric/Quilt 等：没有 downloads 对象，只有 "name" (Maven坐标) + "url"。
                    // 之前这里直接跳过，导致 loader 自身的 jar 从未进入 classpath，
                    // mainClass（如 net.fabricmc.loader.impl.launch.knot.KnotClient）根本找不到，
                    // Java 进程会在启动瞬间就因 "Could not find or load main class" 退出。
                    //
                    // 加上 "lib.Url 非空" 这个前提，避免跟上面 natives-only 条目的判断重叠：
                    // lwjgl-platform 这类条目既没有 downloads.artifact 也没有 url，不该落到这个分支
                    // 用 GetMavenPath() 硬凑一个从未存在过的路径。
                    relativePath = lib.GetMavenPath();
                }

                if (string.IsNullOrEmpty(relativePath)) continue;

                var p = Path.Combine(librariesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p))
                    classpath.Add(p);
                else
                    missingLibraries.Add($"{lib.Name} (期望路径: {p})");
            }
        }
        if (parent != null) AddLibs(parent);
        AddLibs(detail);

        // clientJar 所在的文件夹：有 inheritsFrom 就是父版本(原版)的文件夹，没有就是当前选中的文件夹。
        var clientJarDir = parent != null ? parentDir! : versionDir;
        // clientJar 的文件名：理想情况下等于文件夹名，但文件夹被改名后 jar 文件名不会跟着变——
        // 这时优先用 json 内部自带的 "id" 字段（不受文件夹改名影响）来定位真正的文件。
        var clientJarFolderName = parent != null ? detail.InheritsFrom! : opts.VersionId;
        var clientJarBaseName = parent != null
            ? (!string.IsNullOrEmpty(parent.Id) ? parent.Id : clientJarFolderName)
            : (!string.IsNullOrEmpty(detail.Id) ? detail.Id : clientJarFolderName);
        var clientJar = ResolveVersionFile(clientJarDir, clientJarBaseName, "jar");

        var nativesDir = Path.Combine(clientJarDir, "natives");
        var assetsId = detail.Assets ?? parent?.Assets ?? "legacy";

        if (string.IsNullOrEmpty(mainClass))
            throw new InvalidOperationException(
                $"版本 {opts.VersionId} 的 version json 中没有 mainClass，且父版本也没有，无法确定启动入口类。");

        if (missingLibraries.Count > 0)
        {
            throw new MissingLibrariesException(opts.VersionId, missingLibraries,
                $"以下 {missingLibraries.Count} 个依赖库文件在本地不存在，无法启动（很可能是远古版本的 lwjgl/jinput/twitch 等" +
                "natives 库或 Fabric/Forge 等加载器的库没有下载完整）：\n" +
                string.Join("\n", missingLibraries.Take(10)) +
                (missingLibraries.Count > 10 ? $"\n...等共 {missingLibraries.Count} 个" : "") +
                "\n\n可以点「自动补全」尝试自动下载补齐这些库。");
        }

        if (clientJar == null)
        {
            throw new InvalidOperationException(
                $"找不到版本主 jar 文件（文件夹：{clientJarDir}，期望文件名：{clientJarBaseName}.jar）。" +
                "请确认该版本文件夹里的主 jar 文件没有被误删或改名。");
        }
        classpath.Add(clientJar);

        // 读取 version json 里官方声明的 arguments.jvm（较新版本，约 1.13/17w43a 起才有这个字段，
        // 更老版本没有就是 null，正常回退到下面手写的固定参数集）。
        //
        // 根因（26.2 及其他较新版本一启动就闪退）：这里之前完全没有读取这个字段，只用一套
        // 手写的固定 JVM 参数。但从较新的 Java 版本开始（这次触发点是 Java 17+ 的模块系统，
        // 在 Java 25 上表现尤其明显），JVM 默认对未显式开放的内部包做了访问限制；Mojang 从
        // 对应版本起就把启动客户端所必需的 "--add-opens java.base/xxx=ALL-UNNAMED"、
        // "--add-exports"、以及 macOS 专用的 "-XstartOnFirstThread" 等参数写进了官方
        // version json 的 arguments.jvm 里，指望启动器原样透传。缺了这些参数时，Java 进程会
        // 在触发被限制的反射调用那一刻（往往是刚进入 mainClass 的几十毫秒内）抛
        // InaccessibleObjectException 并直接退出——从用户角度看就是"点启动，窗口一闪就没了"，
        // 且不会像缺库/缺 mainClass 那样在这个方法里提前抛出可读的异常，因为参数本身是"合法"的，
        // 只是"不够"，问题要到 JVM 真正跑起来后才会暴露。
        //
        // 有 inheritsFrom 时，Fabric/Forge/NeoForge 自己的 json 通常只补充 loader 相关的
        // jvm 参数（如果有），原版必需的那批要从父版本(原版) json 里拿，所以父子两份都要读、
        // 都要应用，不能只读当前选中版本这一份。
        var jvmArgsFromJson = new List<string>();
        if (parent?.Arguments?.Jvm != null) jvmArgsFromJson.AddRange(ParseArgumentEntries(parent.Arguments.Jvm));
        if (detail.Arguments?.Jvm != null) jvmArgsFromJson.AddRange(ParseArgumentEntries(detail.Arguments.Jvm));

        // 根因（"java.lang.module.ResolutionException: Module minecraft contains package
        // com.mojang.blaze3d.systems, module xxx exports package com.mojang.blaze3d.systems to
        // minecraft" —— Forge/NeoForge 用 securejarhandler+bootstraplauncher 的新版本
        // 一律必现，1.20.1 Forge 及以后普遍中招）：
        //
        // 这类 loader 的官方 version json 在 arguments.jvm 里声明了
        // "--add-modules" "ALL-MODULE-PATH"，让 JVM 把 --module-path/-p 底下所有 jar
        // 都当模块解析。但 bootstraplauncher 真正启动时，vanilla client jar（模块名
        // "minecraft"）和 Forge 的 "patched" client jar（同样打包了一份被 patch 过的
        // com.mojang.blaze3d.systems 等包）会被 securejarhandler 同时扫描到——两者都在
        // module-info 里"导出"同一个包给 "minecraft" 这个模块消费，JPMS resolver 判定
        // 这是非法的重复导出，直接在 ModuleLayerHandler.buildLayer 这一步炸掉，
        // 游戏窗口能起一瞬间（早期显示窗口先起来了）随即抛异常退出，正是"一闪而过"的表现。
        //
        // 官方启动器（PCL/HMCL/官方 Launcher）都不会遇到这个问题，因为它们都会额外拼接两个
        // securejarhandler/bootstraplauncher 自己认的 -D 系统属性，让底层扫描时提前排除掉
        // 不该参与模块解析的那批 jar：
        //   -DignoreList=<以逗号分隔的一批 jar 文件名片段，用"包含"匹配>
        //   -DmergeModules=<需要合并成同一个模块的 jar 文件名，用分号或逗号分隔均可>
        // 这两个属性不是 version json 里会写的字段（Mojang/Forge 的 json 里根本没有这两个
        // key），是 bootstraplauncher/securejarhandler 这两个库自己读取的、只在启动参数
        // 里靠约定生成的 -D 属性；PCL/HMCL 都是在本地按 classpath 里实际存在的 jar 文件名
        // 现算出来的，不是从网络下载或写死在某个版本号对应表里的固定值——这样才能对任意
        // Forge/NeoForge 版本通用，不需要为每个新版本号单独维护一份忽略列表。
        //
        // 计算规则（对照真实抓包到的官方 PCL 启动命令行核对过）：
        //   ignoreList: securejarhandler/bootstraplauncher/asm 系列(asm, asm-commons,
        //     asm-util, asm-analysis, asm-tree)/JarJarFileSystems/client-extra/
        //     fmlcore/javafmllanguage/lowcodelanguage/mclanguage/"forge-"(前缀片段，
        //     匹配所有 forge-<mc版本>-<forge版本>-xxx.jar)/当前版本主 jar 文件名本身。
        //     这批 jar 要么是 bootstrap 阶段之前就已经手动加载过的启动器自身依赖，要么是
        //     会被 FML 自己的 JarInJarDependencyLocator 在运行时以 union classloader
        //     方式重新处理的语言适配层 jar，不应该被 securejarhandler 当独立模块解析。
        //   mergeModules: classpath 里所有 jna / jna-platform 的 jar（不论具体版本号），
        //     因为 oshi-core 依赖的 jna 版本和 Mojang 官方库列表里独立声明的 jna 版本
        //     经常不一致，两份都在 classpath 上时会被当成同一个模块名的两个不同版本，
        //     同样触发 JPMS 冲突，需要显式告诉 securejarhandler 把它们合并成一个模块处理。
        //
        // 只在这是一个走 bootstraplauncher/securejarhandler 模块化启动的 Forge/NeoForge
        // 版本时才生成——纯原版、Fabric、Quilt 走传统 -cp 全类路径加载，从不使用
        // --add-modules，不需要也不应该加这两个属性。
        //
        // 判断依据改成 mainClass 而不是 InheritsFrom：早先这里用 "detail.InheritsFrom 是否非空"
        // 来判断"是不是 loader 版本"，隐含假设是"loader 版本都有 inheritsFrom 指向原版"。
        // 但 ClientLoaderInstallService 为了实现"完全隔离/独立实例"，会在装完 Forge/NeoForge
        // 之后把版本 json 里的 inheritsFrom 主动置空（把原版 jar 拷贝进 loader 自己的版本文件夹，
        // 不再依赖父版本文件夹），这导致同一份代码在"隔离模式"下失去了 InheritsFrom 这个信号，
        // ignoreList/mergeModules 两个关键属性不会被加进启动参数，于是每次都必现这里注释里
        // 描述的 ResolutionException("Module minecraft contains package ... exports package ...
        // to minecraft")——本次用户贴的崩溃日志就是这个问题，游戏窗口刚起来就直接退出。
        //
        // mainClass 是更可靠的信号：不管 inheritsFrom 是否被置空，只要这是 Forge/NeoForge 的
        // bootstraplauncher 启动方式，detail.MainClass（或继承自父版本的 mainClass）就一定是
        // "cpw.mods.bootstraplauncher.BootstrapLauncher"（对照本次用户日志里
        // "cpw.mods.bootstraplauncher@1.1.2/cpw.mods.bootstraplauncher.BootstrapLauncher.main"
        // 这一行确认），这是官方安装器生成 json 时写死的类名，不会因为我们后处理 json（去掉
        // inheritsFrom、补字段）而改变，因此拿它做判断依据比 InheritsFrom 更稳。
        var isBootstrapLauncherModular = string.Equals(
            mainClass, "cpw.mods.bootstraplauncher.BootstrapLauncher", StringComparison.Ordinal);
        if (isBootstrapLauncherModular)
        {
            var ignoreListFragments = new List<string>
            {
                "bootstraplauncher", "securejarhandler",
                "asm-commons", "asm-util", "asm-analysis", "asm-tree", "asm",
                "JarJarFileSystems", "client-extra",
                "fmlcore", "javafmllanguage", "lowcodelanguage", "mclanguage",
                "forge-",
            };
            // 当前版本主 jar 的文件名片段（不含扩展名即可，ignoreList 是"包含"匹配）：
            // 用 clientJarBaseName 而不是 opts.VersionId，因为文件夹可能被改过名，
            // 真正参与 classpath/模块扫描的是 json 内部 id 对应的那个物理文件名。
            if (!string.IsNullOrEmpty(clientJarBaseName))
                ignoreListFragments.Add(clientJarBaseName);

            var mergeModuleJars = classpath
                .Select(Path.GetFileName)
                .Where(f => f != null &&
                            (f.StartsWith("jna-", StringComparison.OrdinalIgnoreCase) ||
                             f.StartsWith("jna-platform-", StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();

            jvmArgsFromJson.Add($"-DignoreList={string.Join(",", ignoreListFragments)}");
            if (mergeModuleJars.Count > 0)
                jvmArgsFromJson.Add($"-DmergeModules={string.Join(",", mergeModuleJars)}");
        }

        var variables = new Dictionary<string, string>
        {
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "XCL2",
            ["launcher_version"] = "1",
            ["classpath"] = string.Join(";", classpath.Distinct()),
            // 根因（NeoForge "Your NeoForge installation is corrupted. Please try to reinstall
            // NeoForge." / 日志里 "Libraries directory is not readable: ${library_directory}"）：
            // 现代 NeoForge(以及新版 Forge)的 version json 用模块路径(-p/--module-path)启动，
            // JVM 参数里会有 "-DlibraryDirectory=${library_directory}"、"-p" "${library_directory}/..."
            // 这类条目，NeoForge 的 bootstrap 启动阶段要靠这个目录去定位/校验它自己那批模块化的
            // jar。之前 variables 字典里完全没有这个 key，替换完还是原样的字符串
            // "${library_directory}"，NeoForge 拿着这个不存在的路径去读目录，读不到就直接判定
            // "installation is corrupted"——实际上库文件本身可能一个都没少，纯粹是启动器没把
            // 这个变量填进去。值就是 .minecraft/libraries 的绝对路径，跟上面 librariesDir 是同一个。
            ["library_directory"] = librariesDir,
            // 同一批新版 loader 的模块路径参数里还会出现 "${classpath_separator}"，用来拼接
            // 多个模块路径条目——Windows 上是分号 ';'，直接给固定值即可，不需要跟着 classpath
            // 变量走(那个是给 -cp 用的完整分类路径字符串，两者含义不同，不能混用)。
            ["classpath_separator"] = ";",
        };
        var resolvedJvmArgsFromJson = jvmArgsFromJson
            .Select(a => SubstituteVariables(a, variables))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            // -cp/-classpath 和内存参数我们自己在下面手写指定，避免跟 json 里的重复/冲突。
            .Where(a => a != "-cp" && a != "-classpath" &&
                        !a.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) &&
                        !a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        for (var i = resolvedJvmArgsFromJson.Count - 1; i >= 0; i--)
        {
            if (resolvedJvmArgsFromJson[i] == variables["classpath"])
                resolvedJvmArgsFromJson.RemoveAt(i);
        }

        var args = new List<string>
        {
            $"-Xms{opts.MinMemoryMb}M",
            $"-Xmx{opts.MaxMemoryMb}M",
            $"-Djava.library.path={nativesDir}",
            // -Dfile.encoding 只影响文件读写用的默认编码；JDK 18+ 的控制台 stdout/stderr
            // 编码由单独的 stdout.encoding/stderr.encoding 决定（JEP 400），如果不显式指定，
            // 在中文 Windows 上 JVM 仍然会用系统 ANSI 代码页(GBK)写控制台输出，
            // 跟我们这边 StandardOutputEncoding=UTF8 对不上，日志面板/控制台镜像就会整体乱码。
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
            // 标识"这局游戏是被哪个启动器启动的"，参考 PCL 等主流第三方启动器的做法
            // (-Dminecraft.launcher.brand=PCL)。不影响游戏运行，只是让 Minecraft 自己生成的
            // 崩溃报告(crash-reports/*.txt)里能正确显示 "Launched Version: XCL2"，
            // 而不是显示空白/unknown——方便以后根据用户提交的崩溃报告定位问题。
            "-Dminecraft.launcher.brand=XCL2",
            "-Dminecraft.launcher.version=1",
        };
        // 官方 version json 里声明的 --add-opens/--add-exports 等模块系统参数必须加在这里
        // （-cp 和 mainClass 之前），顺序错了 java.exe 会把它们当成程序参数而不是 JVM 参数。
        // 这是修复 26.2 等较新版本在较新 Java 上一启动就闪退的关键：详见上面 resolvedJvmArgsFromJson
        // 的计算过程和注释。
        args.AddRange(resolvedJvmArgsFromJson);

        // 离线自定义皮肤(万能皮肤补丁 authlib-injector)：放在官方参数之后、用户自定义 JVM 参数
        // 之前，这样如果用户自己在 CustomJvmArgs 里也写了同名 -D 属性，用户的设置优先生效。
        // 只有离线账户选了"自定义皮肤"才会有内容，史蒂夫/艾利克斯/微软账户都是空列表。
        if (opts.SkinJvmArgs is { Count: > 0 })
            args.AddRange(opts.SkinJvmArgs);

        // 用户自定义 JVM 参数（仅高手模式下 UI 才会传入非空值）：放在官方参数之后、-cp 之前，
        // 遵循"后面的覆盖前面的"这一 JVM 惯例——如果用户自己也传了跟官方参数冲突的 flag
        // （比如自己也想设置某个 --add-opens），用户的自定义意图优先生效。
        // 校验/解析失败时只记录日志、跳过自定义参数，不让程序崩溃或拼出语法错误的命令行。
        if (!string.IsNullOrWhiteSpace(opts.CustomJvmArgs))
        {
            try
            {
                var customArgs = SplitArgsRespectingQuotes(opts.CustomJvmArgs);
                if (customArgs.Count > 0) args.AddRange(customArgs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[XCL2] 自定义 JVM 参数解析失败，已忽略: {ex.Message}");
            }
        }

        args.Add("-cp");
        args.Add(string.Join(";", classpath.Distinct()));
        args.Add(mainClass);

        args.AddRange(new List<string>
        {
            "--username", opts.Account.Username,
            "--version", BuildDisplayVersion(opts.VersionId, detail, parent),
            "--gameDir", gameDir,
            "--assetsDir", Path.Combine(opts.MinecraftDir, "assets"),
            "--assetIndex", assetsId,
            "--uuid", opts.Account.Uuid.Replace("-", ""),
            "--accessToken", opts.Account.Type is AccountType.Microsoft or AccountType.AuthServer
                ? (opts.Account.MinecraftAccessToken ?? "0")
                : "0",
            "--userType", opts.Account.Type == AccountType.Microsoft ? "msa" : "legacy",
            // 游戏内左下角水印文字：见 LaunchOptions.VersionTypeLabel 注释。为空/null 时
            // 退回官方原始的 "release"，跟这个功能加入之前的行为完全一致，不影响没有
            // 设置过这项的老用户/未传这个字段的调用方（比如服务端相关流程如果复用了
            // 同一套参数构建逻辑，未来接入时不传这个字段也不会出现空水印或异常）。
            "--versionType", string.IsNullOrWhiteSpace(opts.VersionTypeLabel) ? "release" : opts.VersionTypeLabel,
            "--width", opts.WindowWidth.ToString(),
            "--height", opts.WindowHeight.ToString()
        });

        // 根因（Forge/NeoForge 26.2 等较新版本能起进程但立刻 NullPointerException 崩溃，
        // "launchTarget" is null）：跟上面 arguments.jvm 完全一样的问题，只是发生在 game 参数这一侧，
        // 之前完全没读过。上面这批 --game 参数是手写的固定集合，只覆盖了原版 Minecraft 自己认的参数，
        // 但 Forge/NeoForge 的 version json（loader 自己那份，不是父版本原版那份）在 arguments.game
        // 里额外声明了 FML 启动流程必需的参数，典型的比如 "--launchTarget" "forgeclient"、
        // "--fml.forgeVersion" "xxx"、"--fml.mcVersion" "xxx" 等——这些参数名启动器不需要认识具体
        // 含义，原样透传即可，FML 的 ImmediateWindowHandler/Bootstrap 会自己解析。因为之前完全没有
        // 读取合并这个字段，"launchTarget" 这个键在 FML 收到的参数里根本不存在，读出来是 null，
        // 触发它内部 "launchTarget.contains(...)" 那行 NPE，进程随即以退出码 1 终止——现象上是
        // "游戏窗口一闪就没了"，比缺 arguments.jvm 那种更隐蔽，因为连 JVM 都正常起来了、
        // ModLauncher 也正常打印了几行日志，崩溃点在业务逻辑层而不是 JVM 启动层。
        // 处理方式对齐上面 arguments.jvm 的写法：父版本(原版) + 当前版本(loader) 都读一遍、
        // 变量替换一遍，然后去掉跟上面手写参数重复的键（按"参数名+紧跟其后的值"为一组识别，
        // 避免 loader json 里如果也重复声明了 --username 之类的键导致参数出现两次、后一个覆盖前一个
        // 这种依赖顺序的脆弱行为）。
        // 显式声明当前 features 状态：is_demo_user 恒为 false——我们从来不是官方那种
        // "未购买游戏、Mojang 服务器判定为试玩用户"的场景，离线模式的本意是跳过在线校验、
        // 完整解锁游戏，不应该被 version json 里 features.is_demo_user 相关规则命中，
        // 见 ParseArgumentEntries 内的详细说明。
        var currentFeatures = new Dictionary<string, bool> { ["is_demo_user"] = false };
        var gameArgsFromJson = new List<string>();
        if (parent?.Arguments?.Game != null) gameArgsFromJson.AddRange(ParseArgumentEntries(parent.Arguments.Game, currentFeatures));
        if (detail.Arguments?.Game != null) gameArgsFromJson.AddRange(ParseArgumentEntries(detail.Arguments.Game, currentFeatures));

        // 已经手写过的这些键（连同各自的值）不再从 json 里重复追加，避免同一个参数出现两次。
        var alreadyHandledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--username", "--version", "--gameDir", "--assetsDir", "--assetIndex", "--uuid",
            "--accessToken", "--userType", "--versionType", "--width", "--height"
        };

        // 根因（"Only one quick play option can be specified" 崩溃，1.20.6+ 的 Fabric/Forge/
        // NeoForge/Quilt/纯原版都会中招）：Mojang 从 1.20.6 起在 version json 的 arguments.game
        // 里声明了 --quickPlayPath/--quickPlaySingleplayer/--quickPlayMultiplayer/--quickPlayRealms
        // 这四个参数，值统一写成 "${quickPlayPath}" 这种占位符，指望启动器要么把它替换成真正的值、
        // 要么在没有值的时候把这一对 "--xxx value" 整体从命令行里去掉。SubstituteVariables
        // 只认识 variables 字典里那几个 key（natives_directory 等），quickPlay 相关的占位符
        // 根本不在里面，替换完还是原样的字符串 "${quickPlayPath}"——这是一个非空字符串，
        // Minecraft 收到参数后一看"这四个 quickPlay 选项全都有值(哪怕值是一串占位符文本)"，
        // 直接判定"同时指定了多个 quickPlay 选项"并抛异常崩溃，游戏窗口能起来、JVM/Loader
        // 都正常初始化完，但一进 Main.main() 解析参数这一步就死。
        // 修复：值仍然包含未被替换掉的 "${...}" 占位符，说明这个参数我们给不出真实值，
        // 按官方规范应该整体不传这一对参数（key 和它的 value 都不加），而不是把占位符原样
        // 传过去。quickPlay 系列参数本身就是可选功能（游戏内手动选存档/服务器仍然完全正常），
        // 不传不影响正常进入游戏，只是不会自动跳转到指定存档/服务器——这个功能本来就没做，
        // 现在只是让它"什么都不做"而不是"把游戏炸掉"。
        static bool HasUnresolvedPlaceholder(string s) =>
            s.Contains("${") && s.Contains('}');

        var quickPlayMultiplayerInjected = false;
        var resolvedGameArgsFromJson = new List<string>();
        {
            var rawTokens = gameArgsFromJson
                .Select(a => SubstituteVariables(a, variables))
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            // 自动进入服务器：如果调用方提供了地址，把 --quickPlayMultiplayer 后面那个
            // 仍是 "${quickPlayMultiplayer}" 占位符的 token 原地替换成真实地址，这样它就不再
            // 满足下面 HasUnresolvedPlaceholder 的判定条件，不会被当成"给不出真实值"而丢弃。
            // 只处理这一个键，其余三个 quickPlay 系列参数（Path/Singleplayer/Realms）保持
            // 原有"没有值就整对丢弃"的行为不变——这个功能目前只做了"进入指定服务器"这一种用法。
            if (!string.IsNullOrWhiteSpace(opts.AutoJoinServerAddress))
            {
                for (var i = 0; i < rawTokens.Count - 1; i++)
                {
                    if (string.Equals(rawTokens[i], "--quickPlayMultiplayer", StringComparison.OrdinalIgnoreCase)
                        && HasUnresolvedPlaceholder(rawTokens[i + 1]))
                    {
                        rawTokens[i + 1] = opts.AutoJoinServerAddress.Trim();
                        quickPlayMultiplayerInjected = true;
                        break; // 这个键在 arguments.game 里只会出现一次，找到就不用继续扫了
                    }
                }
            }

            for (var i = 0; i < rawTokens.Count; i++)
            {
                var token = rawTokens[i];
                if (token.StartsWith("--"))
                {
                    var hasValue = i + 1 < rawTokens.Count && !rawTokens[i + 1].StartsWith("--");
                    var value = hasValue ? rawTokens[i + 1] : null;
                    if (value != null && HasUnresolvedPlaceholder(value))
                    {
                        // 整对 key+value 都跳过：只丢 value、留下裸的 "--quickPlayPath" 这种 key
                        // 同样会让部分版本的参数解析报错(缺值)，必须连 key 一起丢。
                        i++; // 跳过 value，下一轮循环从 value 之后的 token 开始
                        continue;
                    }
                }
                resolvedGameArgsFromJson.Add(token);
            }
        }

        for (var i = 0; i < resolvedGameArgsFromJson.Count; i++)
        {
            var token = resolvedGameArgsFromJson[i];
            if (token.StartsWith("--") && alreadyHandledKeys.Contains(token))
            {
                // 这个键上面已经手写过了，不再追加键本身；紧跟其后的值（如果有且不是另一个
                // "--xxx" 键）属于这个键，一起跳过，避免它被当成下一轮循环的独立 token 误加进去。
                if (i + 1 < resolvedGameArgsFromJson.Count &&
                    !resolvedGameArgsFromJson[i + 1].StartsWith("--"))
                {
                    i++;
                }
                continue;
            }
            args.Add(token);
        }

        // 修复"进入服务器功能出现问题"的一类常见根因：上面那段替换逻辑完全依赖 version json 的
        // arguments.game 里已经声明好 "--quickPlayMultiplayer" "${quickPlayMultiplayer}" 这一对
        // token，再原地把占位符替换成真实地址——如果这个 json 根本没有声明这个参数（常见于
        // 1.20.6 之前的老版本、或者一些经过裁剪/魔改的第三方客户端 json，参数集跟官方不完全
        // 一致），前面的替换逻辑就无从下手，用户在「版本选择」页明明勾选并保存了自动进服务器，
        // 实际启动时这个参数却完全没有被传递，游戏永远只会停在主菜单——不是设置没保存上，
        // 是这个参数从一开始就没有被加进最终的启动命令行。
        // 这里做一次兜底：只要用户开了这个功能、且上面的替换没有命中（json 里没有这个键），
        // 就直接在参数列表末尾主动补上 "--quickPlayMultiplayer <地址>"。对官方 1.20.6+ 的
        // 版本这只是双保险（正常情况下上面已经处理过，这里不会重复添加）；对不认识这个参数的
        // 客户端（老版本、部分魔改客户端），多出来的参数会被直接忽略，不会影响正常启动——
        // 跟 --lang 在新版本里被忽略是同一种"传了也无害，不传就是完全没有这个能力"的关系，
        // 至少给"客户端本身认识这个参数、只是 json 没declare"的情况留一条生路。
        if (!quickPlayMultiplayerInjected && !string.IsNullOrWhiteSpace(opts.AutoJoinServerAddress))
        {
            args.Add("--quickPlayMultiplayer");
            args.Add(opts.AutoJoinServerAddress!.Trim());
        }

        // --lang 只在很老的版本(约 1.12 以前)里被读取，新版本已不认这个参数，
        // 但加上也无害，作为老版本的兼容兜底一起传。真正生效的是上面写的 options.txt。
        if (!string.IsNullOrWhiteSpace(opts.GameLanguage))
        {
            args.Add("--lang");
            args.Add(opts.GameLanguage!);
        }

        return (mainClass, args);
    }

    /// <summary>
    /// 解析 version json 的 arguments.jvm（或 arguments.game）数组。数组元素有两种形态：
    /// 1) 纯字符串："-Djava.library.path=${natives_directory}" —— 直接使用；
    /// 2) 条件对象：{"rules":[{"action":"allow","os":{"name":"windows"}}], "value": "xxx" 或 ["xxx","yyy"]}
    ///    —— 只有 rules 判定为适用当前系统时才展开 value（value 可能是单个字符串，也可能是字符串数组，
    ///    比如 --add-opens 相关的参数官方经常一次性给两三条）。
    /// 因为 JsonPropertyName("jvm") 反序列化成 List&lt;object&gt;（System.Text.Json 处理"数组元素类型
    /// 不固定"的惯用做法），运行时这里元素实际类型是 JsonElement，需要按 JsonValueKind 区分处理。
    /// </summary>
    /// <summary>
    /// 传入正在使用的 feature 开关：目前只需要 is_demo_user（我们从不以试玩模式启动，
    /// 永远是 false）。参数保留扩展性，未来如果要支持 has_custom_resolution 等其它
    /// features 规则可以继续往这个字典里加。
    /// </summary>
    private static List<string> ParseArgumentEntries(List<object> entries, IReadOnlyDictionary<string, bool>? features = null)
    {
        var result = new List<string>();
        foreach (var entry in entries)
        {
            if (entry is not JsonElement el) continue;

            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
                continue;
            }

            if (el.ValueKind != JsonValueKind.Object) continue;

            // 条件对象：先判断 rules 是否允许当前系统(Windows)+当前 features 组合。
            //
            // 根因修复（"离线账户/微软正版账户启动出来的游戏窗口标题栏带星号、左下角写
            // 'Demo（已修改）'，进游戏还只有『开始试玩世界』"）：
            // Mojang 从 1.16 前后的 version json 起，在 arguments.game 里用这种写法声明
            // --demo 参数：
            //   {"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"}
            // 也就是说这条 --demo 只应该在 features.is_demo_user 为 true（也就是账户没有
            // 购买游戏、Mojang 判定为试玩用户）时才生效。但下面这段规则判断之前只看了
            // rule.os，完全没读 rule.features 这个键——一条只带 features、不带 os 的规则，
            // matchesOs 会因为"没有 os 字段"直接维持默认值 true，于是无论真实的 features
            // 状态是什么，这条规则永远被判定为"匹配"，allow 直接被 action=="allow" 决定，
            // 等于完全无视了 is_demo_user 这个开关本身。结果是：不管账户是正版微软账户还是
            // 离线账户，只要游戏版本的 arguments.game 里有这一条（绝大多数现代版本都有），
            // --demo 都会被无条件加进最终启动参数，Minecraft 收到 --demo 会直接强制进入
            // 试玩模式——这跟账户到底是否登录、token 是否有效完全无关，是启动参数拼接这一层
            // 的规则解析漏洞，之前误以为是账户/token 问题，实际上启动参数在离开这一层之前
            // 就已经被错误地塞进了 --demo。
            // 修复：rules 里除了 os 还要看 features，只有 features 字典跟调用方传入的
            // 当前 features 状态完全匹配（我们目前唯一关心的是 is_demo_user，值必须匹配传入
            // 的 features["is_demo_user"]，且我们永远传 false，因为启动器里"没有正版校验"
            // 不等于"这是一个试玩账户"——离线模式的本意是跳过在线校验、完整解锁游戏，
            // 不是官方那种功能阉割的 Demo）才算匹配；规则没声明 features 键则视为不限制。
            var allow = true;
            if (el.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
            {
                allow = false;
                foreach (var rule in rulesEl.EnumerateArray())
                {
                    var matches = true;
                    if (rule.TryGetProperty("os", out var osEl) && osEl.ValueKind == JsonValueKind.Object &&
                        osEl.TryGetProperty("name", out var osNameEl))
                    {
                        matches = osNameEl.GetString() == "windows";
                    }
                    if (matches && rule.TryGetProperty("features", out var featuresEl) && featuresEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var featureProp in featuresEl.EnumerateObject())
                        {
                            var expected = featureProp.Value.ValueKind == JsonValueKind.True;
                            var actual = features != null && features.TryGetValue(featureProp.Name, out var v) && v;
                            if (expected != actual) { matches = false; break; }
                        }
                    }
                    if (matches)
                    {
                        var action = rule.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : "allow";
                        allow = action == "allow";
                    }
                }
            }
            if (!allow) continue;

            if (!el.TryGetProperty("value", out var valueEl)) continue;

            if (valueEl.ValueKind == JsonValueKind.String)
            {
                var s = valueEl.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            else if (valueEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s)) result.Add(s);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>把 "${xxx}" 形式的占位符替换成 variables 里对应的值；变量表里没有的占位符原样保留
    /// （官方 arguments.jvm 里偶尔会出现启动器不需要处理的占位符，保留原文比抛异常更安全）。</summary>
    private static string SubstituteVariables(string input, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("${")) return input;
        foreach (var kv in variables)
            input = input.Replace("${" + kv.Key + "}", kv.Value);
        return input;
    }

    /// <summary>
    /// 把游戏语言写入 &lt;gameDir&gt;/options.txt 的 "lang:" 字段，文件不存在(真正首次启动)时
    /// 一并写入跳过官方欢迎/辅助功能引导页所需的字段（见 SkipFirstRunLines）。
    ///
    /// 关键点 1：options.txt 是一份很长的、玩家自己会在游戏内修改的配置文件(几十上百行，
    /// 包含视距、音量、按键绑定等等)。这里绝不能整份覆盖重写——否则每次启动都会把玩家
    /// 上一次在游戏里改过的所有设置全部重置掉。正确做法是：已存在的文件只替换/插入
    /// "lang:" 这一行，其余行原样保留、原样顺序写回；只有文件原本完全不存在时才写入
    /// 一份包含 lang + 跳过引导字段的最小文件。
    ///
    /// 关键点 2：写法参考了 HMCL（Hello Minecraft! Launcher）的真实实现
    /// (HMCLGameLauncher.generateOptionsTxt())：不需要(也不应该)编造 "version:" 等
    /// Minecraft 自己内部用的数据版本号字段——那是上一版的一个错误猜测，已经改回来。
    /// Minecraft 对残缺的 options.txt 是宽容的，缺失字段会用内置默认值，不会因为
    /// "文件不完整"就整体忽略掉已经写进去的字段。
    ///
    /// 关于"新装游戏第一次打开弹出 Welcome to Minecraft / 辅助功能引导页"：这不是
    /// bug，是 Minecraft 自身行为，但可以像 PCL 等主流第三方启动器一样，通过预先写入
    /// onboardAccessibility:false 等字段让游戏认为"不是第一次启动"，从而跳过这个页面
    /// （见 SkipFirstRunLines 的详细说明）。
    /// </summary>
    /// <summary>
    /// 不带 BOM 的 UTF-8 编码。Minecraft 自己的 Options.load() 是逐行按 "key:value" 做
    /// 纯文本分割解析的，不会探测/剥离 UTF-8 BOM（EF BB BF）。
    ///
    /// 之前这里用的是 .NET 的静态属性 Encoding.UTF8，它有一个很容易踩的坑：
    /// GetPreamble() 会返回 3 字节 BOM，File.WriteAllText/WriteAllLines 用它写文件时，
    /// 会在文件最开头自动插入这 3 个不可见字节。Minecraft 读取这份文件时，第一行的
    /// 第一个字段名前面就会被拼上这 3 个 BOM 字节，导致这一行(以及后续依赖行号/顺序的
    /// datafixer 逻辑)解析异常——这正好能解释实际抓到的崩溃：Options.load() 在
    /// OptionsKeyLwjgl3Fix 这个"旧版整数键码 -&gt; 新版字符串键名"的按键迁移逻辑里，
    /// 本该读到一个数字，却读到了字符串 "key.keyboard.g"，说明字段被错位拼接了，
    /// 与"文件开头/某一行前面多了几个不可见字节导致行内容错位"完全吻合。
    /// 一旦 Options.load() 抛异常，整个 options.txt 都会被当成加载失败，游戏
    /// 直接回退到全部默认值(含语言)，表现为"lang:zh_cn 明明写进去了，进游戏还是英文"。
    ///
    /// 修复：读写全部改用不带 BOM 的 UTF8Encoding(false)，与 Minecraft/Java 自身
    /// 读写这份文件时的行为保持一致，不再往文件里注入这 3 个多余字节。
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 跳过 Minecraft 官方"Welcome to Minecraft / 辅助功能引导"欢迎页所需的字段。
    ///
    /// 触发条件（据 Minecraft Wiki "Title Screen" 词条）：用户第一次进入游戏，
    /// 或者 options.txt 里 onboardAccessibility 字段为 true 时，就会弹出这个引导页；
    /// 删除 options.txt 也会让这个页面重新出现。也就是说只要有一份存在的、
    /// onboardAccessibility:false 的 options.txt，游戏就会认为"不是第一次启动"，
    /// 直接跳过引导页进入主菜单——这正是 PCL 等主流第三方启动器的做法。
    ///
    /// 只在"文件原本不存在"（真正意义上的首次启动/全新安装）这个分支写入这些字段：
    /// 已存在的 options.txt 完全不动这些字段，避免覆盖玩家自己在游戏里已经做过的选择
    /// (比如玩家其实想看引导、或者已经手动调整过叙述者等设置)。
    ///
    /// 字段值参考真实的、由 Minecraft 客户端自己生成的现代版 options.txt 样本：
    /// onboardAccessibility:false（跳过引导页本身）、narrator:0（不强制开叙述者，
    /// 引导页第一步就是"按回车开启叙述者"的提示音，关掉更安静）、
    /// tutorialStep:none（跳过新手教程提示）、skipMultiplayerWarning:true（跳过多人游戏警告弹窗，
    /// 属于同一类"首次进入某功能会弹一次"的提示，一并跳过体验更连贯）。
    /// </summary>
    private static readonly string[] SkipFirstRunLines =
    {
        "onboardAccessibility:false",
        "narrator:0",
        "tutorialStep:none",
        "skipMultiplayerWarning:true"
    };

    private static void ApplyGameLanguage(string gameDir, string lang)
    {
        var optionsPath = Path.Combine(gameDir, "options.txt");
        var langLine = $"lang:{lang}";

        if (!File.Exists(optionsPath))
        {
            var initialLines = new List<string> { langLine };
            initialLines.AddRange(SkipFirstRunLines);
            File.WriteAllText(optionsPath, string.Join("\n", initialLines) + "\n", Utf8NoBom);
            LogLanguageWrite(gameDir, lang, "created-minimal-skip-onboarding");
            return;
        }

        // File.ReadAllLines(path, Encoding) 在文件实际带 BOM 时会自动识别并剥离 BOM
        // （不论调用方传入的是哪个 Encoding 实例，BOM 检测是文件读取层做的），
        // 所以这里即使原文件是之前版本用带 BOM 的 Encoding.UTF8 写出来的坏文件，
        // 重新读取时也能正确去掉 BOM、按正常内容解析；真正需要修的是"写"这一步，
        // 确保重写之后不再产生新的 BOM，把污染源头断掉。
        var lines = File.ReadAllLines(optionsPath, Encoding.UTF8).ToList();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            // options.txt 每行形如 "key:value"，用冒号分隔；只匹配行首的 "lang:" 避免误伤
            // 其他可能包含 "lang" 子串的无关字段。
            if (lines[i].StartsWith("lang:", StringComparison.Ordinal))
            {
                lines[i] = langLine;
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add(langLine);

        File.WriteAllLines(optionsPath, lines, Utf8NoBom);
        LogLanguageWrite(gameDir, lang, replaced ? "updated-existing-line" : "appended-new-line");
    }

    /// <summary>
    /// 诊断日志：每次启动实际写了什么语言、走了哪条分支，追加到 xcl2/logs/language-debug.log。
    /// 如果之后还有人反馈"设了中文还是英文"，看这份日志就能立刻确定问题出在
    /// "根本没写进去/写错了内容"还是"确实写对了、但游戏没读这个文件"(后者会是游戏本身的问题，
    /// 需要换排查方向，比如检查是不是有多个 options.txt 或者账户/服务器强制了语言)。
    /// </summary>
    private static void LogLanguageWrite(string gameDir, string lang, string branch)
    {
        try
        {
            var logPath = Path.Combine(App.DataDir, "logs", "language-debug.log");
            var optionsPath = Path.Combine(gameDir, "options.txt");
            var actualFirstLines = File.Exists(optionsPath)
                ? string.Join(" | ", File.ReadAllLines(optionsPath, Encoding.UTF8).Take(3))
                : "(文件不存在)";
            File.AppendAllText(logPath,
                $"[{DateTime.Now}] gameDir={gameDir} lang={lang} branch={branch} 写入后前3行=[{actualFirstLines}]\n");
        }
        catch { /* 诊断日志失败不应该影响正常启动流程 */ }
    }
}
