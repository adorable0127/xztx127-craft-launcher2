using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>
/// 百宝箱「数据包互转」的核心逻辑：在 Java 版数据包（data pack）
/// 与基岩版行为包（behavior pack，基岩版里承载函数/战利品表/配方等“数据包”内容的载体）
/// 之间做尽量无损的结构转换。
///
/// 重要说明：两边的数据驱动系统并不是一一对应的。本工具能做到的是：
///   1. 包清单互转（Java 的 pack.mcmeta ↔ 基岩版的 manifest.json）；
///   2. 目录结构互转（data/&lt;命名空间&gt;/xxx ↔ 基岩版的 xxx，或反过来）；
///   3. .mcfunction 里最常见的语法差异做启发式替换（主要是 function 调用的
///      “命名空间:路径” ↔ “路径” 写法）；
///   4. 战利品表 / 配方等 JSON 会原样复制过去，因为两边字段大体接近但不完全相同，
///      复制后仍需要人工核对；
///   5. 进度（advancements）、谓词（predicates）、标签（tags）、结构（.nbt/.mcstructure）、
///      以及基岩版特有的实体/方块/贸易/生成规则等，两边没有直接对应物，本工具只会
///      跳过并在报告里给出提示，不会瞎编内容。
/// 一句话：这是“帮你把体力活做掉 + 告诉你哪里必须手动检查”的工具，不是魔法转换器。
/// </summary>
public static class DatapackConverterService
{
    public enum PackType
    {
        Unknown,
        JavaDatapack,
        BedrockBehaviorPack
    }

    public sealed class ConvertResult
    {
        public List<string> InfoLog { get; } = new();
        public List<string> WarnLog { get; } = new();
        public int FilesConverted { get; set; }
        public int FilesCopiedAsIs { get; set; }
        public int FilesSkipped { get; set; }
        public string? OutputDir { get; set; }
        public bool Success { get; set; }
        public string? FatalError { get; set; }
    }

    // ------------------------------------------------------------------
    // 识别包类型
    // ------------------------------------------------------------------
    public static PackType DetectPackType(string dir)
    {
        if (File.Exists(Path.Combine(dir, "pack.mcmeta")) && Directory.Exists(Path.Combine(dir, "data")))
            return PackType.JavaDatapack;
        if (File.Exists(Path.Combine(dir, "manifest.json")))
            return PackType.BedrockBehaviorPack;
        // 兜底：只有 pack.mcmeta 或者只有 data 目录，也当 Java 数据包处理
        if (File.Exists(Path.Combine(dir, "pack.mcmeta")) || Directory.Exists(Path.Combine(dir, "data")))
            return PackType.JavaDatapack;
        return PackType.Unknown;
    }

    // ------------------------------------------------------------------
    // Java pack_format <-> 基岩版 min_engine_version 的粗略对照表
    // 只保证「差不多」，两边版本节奏本来就不是严格对齐的。
    // ------------------------------------------------------------------
    private static readonly (int PackFormat, int[] Engine)[] FormatTable =
    {
        (4,  new[] {1, 13, 0}),
        (6,  new[] {1, 16, 0}),
        (7,  new[] {1, 17, 0}),
        (8,  new[] {1, 18, 0}),
        (10, new[] {1, 19, 0}),
        (12, new[] {1, 19, 40}),
        (15, new[] {1, 20, 10}),
        (18, new[] {1, 20, 30}),
        (26, new[] {1, 20, 60}),
        (41, new[] {1, 21, 0}),
        (48, new[] {1, 21, 20}),
        (57, new[] {1, 21, 40}),
    };

    private static int[] JavaFormatToEngineVersion(int packFormat)
    {
        var best = FormatTable[0];
        foreach (var entry in FormatTable)
        {
            if (entry.PackFormat <= packFormat) best = entry;
        }
        return best.Engine;
    }

    private static int EngineVersionToJavaFormat(int[] engine)
    {
        int Score(int[] v) => v.Length > 0 ? v[0] * 1_000_000 + (v.Length > 1 ? v[1] * 1000 : 0) + (v.Length > 2 ? v[2] : 0) : 0;
        var target = Score(engine);
        var best = FormatTable[0];
        foreach (var entry in FormatTable)
        {
            if (Score(entry.Engine) <= target) best = entry;
        }
        return best.PackFormat;
    }

    // ------------------------------------------------------------------
    // Java 数据包 -> 基岩版行为包
    // ------------------------------------------------------------------
    public static ConvertResult ConvertJavaToBedrock(string sourceDir, string outputDir)
    {
        var result = new ConvertResult { OutputDir = outputDir };
        try
        {
            Directory.CreateDirectory(outputDir);

            string description = "Converted from Java datapack";
            int packFormat = 41;
            var mcmetaPath = Path.Combine(sourceDir, "pack.mcmeta");
            if (File.Exists(mcmetaPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(mcmetaPath));
                    if (doc.RootElement.TryGetProperty("pack", out var packEl))
                    {
                        if (packEl.TryGetProperty("description", out var d))
                            description = d.ValueKind == JsonValueKind.String ? d.GetString() ?? description : description;
                        if (packEl.TryGetProperty("pack_format", out var pf) && pf.ValueKind == JsonValueKind.Number)
                            packFormat = pf.GetInt32();
                    }
                    result.InfoLog.Add("已读取 pack.mcmeta。");
                }
                catch (Exception ex)
                {
                    result.WarnLog.Add($"pack.mcmeta 解析失败，将使用默认值：{ex.Message}");
                }
            }
            else
            {
                result.WarnLog.Add("没找到 pack.mcmeta，包名/描述使用了默认值，请转换完之后自己去 manifest.json 里改一下。");
            }

            var engineVersion = JavaFormatToEngineVersion(packFormat);
            WriteBedrockManifest(outputDir, description, engineVersion);
            result.InfoLog.Add($"已生成 manifest.json（min_engine_version 按 pack_format {packFormat} 粗略换算为 {string.Join('.', engineVersion)}，建议手动核对）。");

            var dataDir = Path.Combine(sourceDir, "data");
            if (!Directory.Exists(dataDir))
            {
                result.WarnLog.Add("源目录下没有 data 文件夹，除了包清单以外没有别的内容可以转换。");
                result.Success = true;
                return result;
            }

            foreach (var nsDir in Directory.GetDirectories(dataDir))
            {
                var ns = Path.GetFileName(nsDir);

                ConvertFolderJavaToBedrock(nsDir, "functions", outputDir, "functions", ns, result,
                    convertContent: true, isFunction: true);

                ConvertFolderJavaToBedrock(nsDir, "loot_tables", outputDir, "loot_tables", ns, result,
                    convertContent: false, isFunction: false,
                    warnOnce: "战利品表（loot_tables）字段结构两边大体接近但不完全相同（例如部分函数/条件名不一样），已原样复制，请对照基岩版文档核对每一份战利品表。");

                ConvertFolderJavaToBedrock(nsDir, "recipes", outputDir, "recipes", ns, result,
                    convertContent: false, isFunction: false,
                    warnOnce: "配方（recipes）两边 JSON 结构差异较大（基岩版需要 format_version 等字段），已原样复制到对应目录，基本都需要手动改写才能生效。");

                SkipFolder(nsDir, "advancements", result, "进度（advancements）是 Java 版特有系统，基岩版没有对应功能，已跳过。若要在基岩版实现类似效果，通常需要改用计分板 + 函数来模拟。");
                SkipFolder(nsDir, "predicates", result, "谓词（predicates）基岩版没有对应机制，已跳过。");
                SkipFolder(nsDir, "tags", result, "标签（tags）系统两边实现方式不同，已跳过，请手动处理相关引用。");
                SkipFolder(nsDir, "structures", result, "结构文件（.nbt）与基岩版的 .mcstructure 格式不兼容，已跳过。需要用结构方块在基岩版里重新导出。");
                SkipFolder(nsDir, "worldgen", result, "自定义世界生成（worldgen）基岩版的实现方式完全不同，已跳过。");
                SkipFolder(nsDir, "item_modifiers", result, "物品修饰符（item_modifiers）基岩版没有对应机制，已跳过。");
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.FatalError = ex.Message;
            result.Success = false;
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 基岩版行为包 -> Java 数据包
    // ------------------------------------------------------------------
    public static ConvertResult ConvertBedrockToJava(string sourceDir, string outputDir, string namespaceName)
    {
        var result = new ConvertResult { OutputDir = outputDir };
        namespaceName = string.IsNullOrWhiteSpace(namespaceName) ? "converted" : SanitizeNamespace(namespaceName);
        try
        {
            Directory.CreateDirectory(outputDir);

            string description = "Converted from Bedrock behavior pack";
            var engineVersion = new[] { 1, 21, 0 };
            var manifestPath = Path.Combine(sourceDir, "manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (doc.RootElement.TryGetProperty("header", out var header))
                    {
                        if (header.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                            description = d.GetString() ?? description;
                        if (header.TryGetProperty("min_engine_version", out var mev) && mev.ValueKind == JsonValueKind.Array)
                            engineVersion = mev.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    }
                    result.InfoLog.Add("已读取 manifest.json。");
                }
                catch (Exception ex)
                {
                    result.WarnLog.Add($"manifest.json 解析失败，将使用默认值：{ex.Message}");
                }
            }
            else
            {
                result.WarnLog.Add("没找到 manifest.json，包名/描述使用了默认值。");
            }

            var packFormat = EngineVersionToJavaFormat(engineVersion);
            WriteJavaPackMcmeta(outputDir, description, packFormat);
            result.InfoLog.Add($"已生成 pack.mcmeta（pack_format 按 min_engine_version {string.Join('.', engineVersion)} 粗略换算为 {packFormat}，建议手动核对）。");

            var dataRoot = Path.Combine(outputDir, "data", namespaceName);

            ConvertFolderBedrockToJava(sourceDir, "functions", dataRoot, "functions", namespaceName, result,
                convertContent: true, isFunction: true);

            ConvertFolderBedrockToJava(sourceDir, "loot_tables", dataRoot, "loot_tables", namespaceName, result,
                convertContent: false, isFunction: false,
                warnOnce: "战利品表字段结构两边接近但不完全相同，已原样复制，请对照 Java 版文档核对。");

            ConvertFolderBedrockToJava(sourceDir, "recipes", dataRoot, "recipes", namespaceName, result,
                convertContent: false, isFunction: false,
                warnOnce: "配方两边 JSON 结构差异较大，已原样复制到对应目录，基本都需要手动改写才能在 Java 版生效。");

            SkipFolder(sourceDir, "trading", result, "贸易表（trading）Java 版没有直接对应的数据包目录，已跳过。");
            SkipFolder(sourceDir, "spawn_rules", result, "生成规则（spawn_rules）Java 版没有对应的数据包机制，已跳过。");
            SkipFolder(sourceDir, "entities", result, "自定义实体定义 Java 版数据包无法承载（需要模组），已跳过。");
            SkipFolder(sourceDir, "items", result, "自定义物品定义 Java 版数据包无法承载（1.21+ 的物品组件系统与此不同），已跳过，请手动对照迁移。");
            SkipFolder(sourceDir, "blocks", result, "自定义方块定义 Java 版数据包无法承载，已跳过。");
            SkipFolder(sourceDir, "animations", result, "动画/动画控制器是基岩版特有系统，已跳过。");
            SkipFolder(sourceDir, "animation_controllers", result, "动画/动画控制器是基岩版特有系统，已跳过。");
            SkipFolder(sourceDir, "features", result, "自定义地物生成（features/feature_rules）两边实现方式完全不同，已跳过。");
            SkipFolder(sourceDir, "feature_rules", result, "自定义地物生成（features/feature_rules）两边实现方式完全不同，已跳过。");
            SkipFolder(sourceDir, "structures", result, "结构文件（.mcstructure）与 Java 版的 .nbt 格式不兼容，已跳过。");

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.FatalError = ex.Message;
            result.Success = false;
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 辅助：清单文件生成
    // ------------------------------------------------------------------
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private static void WriteBedrockManifest(string outputDir, string description, int[] engineVersion)
    {
        var manifest = new
        {
            format_version = 2,
            header = new
            {
                name = "Converted Datapack",
                description,
                uuid = Guid.NewGuid().ToString(),
                version = new[] { 1, 0, 0 },
                min_engine_version = engineVersion
            },
            modules = new object[]
            {
                new
                {
                    type = "data",
                    uuid = Guid.NewGuid().ToString(),
                    version = new[] { 1, 0, 0 }
                }
            }
        };
        File.WriteAllText(Path.Combine(outputDir, "manifest.json"), JsonSerializer.Serialize(manifest, PrettyJson));
    }

    private static void WriteJavaPackMcmeta(string outputDir, string description, int packFormat)
    {
        var mcmeta = new
        {
            pack = new
            {
                pack_format = packFormat,
                description
            }
        };
        File.WriteAllText(Path.Combine(outputDir, "pack.mcmeta"), JsonSerializer.Serialize(mcmeta, PrettyJson));
    }

    private static string SanitizeNamespace(string ns)
    {
        ns = ns.Trim().ToLowerInvariant();
        ns = Regex.Replace(ns, "[^a-z0-9_.-]", "_");
        return string.IsNullOrEmpty(ns) ? "converted" : ns;
    }

    // ------------------------------------------------------------------
    // 辅助：整个子目录搬运（Java -> Bedrock）
    // ------------------------------------------------------------------
    private static void ConvertFolderJavaToBedrock(string nsDir, string subFolder, string outputDir, string outSubFolder,
        string ns, ConvertResult result, bool convertContent, bool isFunction, string? warnOnce = null)
    {
        var src = Path.Combine(nsDir, subFolder);
        if (!Directory.Exists(src)) return;

        var dst = Path.Combine(outputDir, outSubFolder, ns);
        Directory.CreateDirectory(dst);

        bool warned = false;
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (convertContent && isFunction && file.EndsWith(".mcfunction", StringComparison.OrdinalIgnoreCase))
            {
                var lines = File.ReadAllLines(file);
                var converted = lines.Select(JavaFunctionLineToBedrock).ToArray();
                File.WriteAllLines(target, converted);
                result.FilesConverted++;
            }
            else
            {
                File.Copy(file, target, overwrite: true);
                result.FilesCopiedAsIs++;
                if (warnOnce != null && !warned)
                {
                    result.WarnLog.Add(warnOnce);
                    warned = true;
                }
            }
        }
        result.InfoLog.Add($"已处理 data/{ns}/{subFolder} -> {outSubFolder}/{ns}");
    }

    private static void ConvertFolderBedrockToJava(string sourceDir, string subFolder, string dataRoot, string outSubFolder,
        string ns, ConvertResult result, bool convertContent, bool isFunction, string? warnOnce = null)
    {
        var src = Path.Combine(sourceDir, subFolder);
        if (!Directory.Exists(src)) return;

        var dst = Path.Combine(dataRoot, outSubFolder);
        Directory.CreateDirectory(dst);

        bool warned = false;
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (convertContent && isFunction && file.EndsWith(".mcfunction", StringComparison.OrdinalIgnoreCase))
            {
                var lines = File.ReadAllLines(file);
                var converted = lines.Select(l => BedrockFunctionLineToJava(l, ns)).ToArray();
                File.WriteAllLines(target, converted);
                result.FilesConverted++;
            }
            else
            {
                File.Copy(file, target, overwrite: true);
                result.FilesCopiedAsIs++;
                if (warnOnce != null && !warned)
                {
                    result.WarnLog.Add(warnOnce);
                    warned = true;
                }
            }
        }
        result.InfoLog.Add($"已处理 {subFolder} -> data/{ns}/{outSubFolder}");
    }

    private static void SkipFolder(string parentDir, string subFolder, ConvertResult result, string reason)
    {
        var src = Path.Combine(parentDir, subFolder);
        if (!Directory.Exists(src)) return;
        var count = Directory.GetFiles(src, "*", SearchOption.AllDirectories).Length;
        if (count == 0) return;
        result.FilesSkipped += count;
        result.WarnLog.Add($"[{subFolder}] 共 {count} 个文件未转换：{reason}");
    }

    // ------------------------------------------------------------------
    // .mcfunction 行内容的启发式转换。只处理最常见、最有把握的几种写法，
    // 拿不准的一律原样保留 —— 转换错比不转换更糟。
    // ------------------------------------------------------------------
    private static readonly Regex JavaFunctionCallRegex =
        new(@"^(\s*)function\s+([a-z0-9_.\-]+):([a-z0-9_/\-]+)(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BedrockFunctionCallRegex =
        new(@"^(\s*)function\s+([a-z0-9_/\-]+)(\s.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] JavaOnlyCommands = { "advancement", "data", "predicate", "loot" };

    internal static string JavaFunctionLineToBedrock(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            return line;

        var m = JavaFunctionCallRegex.Match(line);
        if (m.Success)
        {
            // function ns:path/to/func -> function ns/path/to/func
            return $"{m.Groups[1].Value}function {m.Groups[2].Value}/{m.Groups[3].Value}{m.Groups[4].Value}";
        }

        var trimmed = line.TrimStart();
        var firstWord = trimmed.Split(' ', 2)[0];
        if (JavaOnlyCommands.Contains(firstWord, StringComparer.OrdinalIgnoreCase))
        {
            return $"# [基岩版不支持该指令，需手动处理] {line}";
        }

        return line;
    }

    internal static string BedrockFunctionLineToJava(string line, string ns)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            return line;

        var m = BedrockFunctionCallRegex.Match(line);
        if (m.Success && !m.Groups[2].Value.Contains(':'))
        {
            // function path/to/func -> function ns:path/to/func
            return $"{m.Groups[1].Value}function {ns}:{m.Groups[2].Value}{m.Groups[3].Value}";
        }

        return line;
    }
}
