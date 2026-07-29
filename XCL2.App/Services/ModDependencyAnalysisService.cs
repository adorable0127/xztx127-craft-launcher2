using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>一个已安装 mod 声明的一条"前置模组"依赖。</summary>
public class ModDependencyRequirement
{
    /// <summary>依赖的 mod id（fabric.mod.json 里 depends 的 key，比如 "fabricloader"、"fabric"）。</summary>
    public string DependencyModId { get; init; } = "";

    /// <summary>版本要求原文（比如 ">=0.90.0"），只用于展示，不做真正的语义化版本比较——
    /// 语义化比较需要完整解析 Fabric 的版本范围语法，这里只做"有没有装"的有无判断，
    /// 版本是否满足交给游戏加载器自己在启动时校验并报错，避免重复实现一遍容易出错的版本比较逻辑。</summary>
    public string VersionRange { get; init; } = "*";
}

/// <summary>一个"缺失前置模组"的分析结果条目：谁需要它、它是什么。</summary>
public class MissingDependency
{
    /// <summary>缺失的依赖 mod id，例如 "fabric-api"。</summary>
    public string DependencyModId { get; init; } = "";

    /// <summary>展示用的友好名称（尽量识别常见 mod id 给出中文习惯称呼，否则退回 mod id 本身）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>版本要求原文，供展示（"某版本"这种粗粒度提示时可以不展示具体数字）。</summary>
    public string VersionRange { get; init; } = "*";

    /// <summary>因为缺这个依赖而无法正常加载的 mod（可能不止一个）。</summary>
    public List<string> RequiredByModNames { get; init; } = new();

    /// <summary>Modrinth 上对应项目的 slug（如果能从内置映射表识别出来），用于一键搜索下载。
    /// 识别不出来时为 null，UI 上退化为只展示提示、不提供一键下载按钮。</summary>
    public string? ModrinthSlug { get; init; }
}

public class ModDependencyAnalysisResult
{
    public List<MissingDependency> MissingDependencies { get; init; } = new();
    public bool HasMissingDependencies => MissingDependencies.Count > 0;
}

/// <summary>
/// 前置模组依赖分析：扫描已安装（且处于"已启用"状态）的 Fabric mod，读取每个 jar 内
/// fabric.mod.json 的 depends 字段，跟当前已安装的全部 mod id 集合做比对，找出"被依赖但没装"的项。
///
/// 只覆盖 Fabric（fabric.mod.json 的 depends 是结构化 JSON，可靠解析）。Forge/NeoForge 的
/// mods.toml 里也有 [[dependencies.xxx]] 依赖声明，但格式更松散、真实项目里字段缺失/写法不规范
/// 的情况更常见，误报风险更高，这一轮只做 Fabric，未来如果需要再扩展。
///
/// 典型触发场景（交接文档原文）：装了 Sodium 但没装 Fabric API，读取 sodium 的 fabric.mod.json
/// 发现它 depends 里有 fabric-api（或更细的子模块 fabric-rendering-*），而已安装 mod 列表里
/// 没有任何一个 mod id 匹配这个依赖，判定为"缺失"。
/// </summary>
public class ModDependencyAnalysisService
{
    /// <summary>
    /// 常见 mod id -> (中文展示名, Modrinth slug) 的映射表，只收录高频出现在"缺前置"场景里的项目，
    /// 不追求覆盆盖全网 mod（那样维护成本太高，且大部分 mod id 本身已经足够可读）。
    /// 命中不了的依赖仍然会展示，只是用 mod id 原文当展示名，且没有一键下载按钮。
    /// </summary>
    private static readonly Dictionary<string, (string DisplayName, string ModrinthSlug)> KnownMods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fabric"] = ("Fabric API", "fabric-api"),
        ["fabric-api"] = ("Fabric API", "fabric-api"),
        ["fabricloader"] = ("Fabric Loader", "fabric-api"), // loader 本身不是 mod，不能靠装 mod 解决，但仍给出识别名方便提示文案
        ["cloth-config"] = ("Cloth Config API", "cloth-config"),
        ["cloth-config2"] = ("Cloth Config API", "cloth-config"),
        ["modmenu"] = ("Mod Menu", "modmenu"),
        ["architectury"] = ("Architectury API", "architectury-api"),
        ["fabric_language_kotlin"] = ("Fabric Language Kotlin", "fabric-language-kotlin"),
        ["geckolib"] = ("GeckoLib", "geckolib"),
        ["playeranimator"] = ("Player Animator", "playeranimator"),
    };

    /// <summary>
    /// 分析指定游戏目录下的已启用 mod，返回缺失的前置依赖列表。
    /// 只分析"已启用"的 mod（.jar 结尾，不含 .disabled 的）——被用户主动禁用的 mod
    /// 本来就不会被加载器加载，它声明的依赖缺不缺都不影响这次启动，不该被当成问题报出来。
    /// </summary>
    public ModDependencyAnalysisResult Analyze(List<LocalModInfo> enabledMods)
    {
        var result = new ModDependencyAnalysisResult();
        if (enabledMods.Count == 0) return result;

        // 第一步：读出每个 jar 的 modid（自身）+ 它声明的 depends。
        var installedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perModDependencies = new List<(string ModDisplayName, List<ModDependencyRequirement> Deps)>();

        foreach (var mod in enabledMods)
        {
            var (selfId, deps) = TryReadFabricMetadata(mod.FilePath);
            if (selfId != null) installedModIds.Add(selfId);
            if (deps.Count > 0) perModDependencies.Add((mod.DisplayName, deps));
        }

        // Minecraft 本体和 java 本身也经常出现在 depends 里（比如要求 minecraft ">=1.20"），
        // 这两个不是"能装的 mod"，天然排除，不然会被误报成"缺失前置"。
        var builtinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "minecraft", "java", "fabricloader" };

        // 第二步：汇总"被依赖但没装"的项，同一个依赖被多个 mod 要求时合并成一条，
        // RequiredByModNames 里列出所有需要它的 mod，而不是分别报好几条重复提示。
        var missing = new Dictionary<string, MissingDependency>(StringComparer.OrdinalIgnoreCase);

        foreach (var (modName, deps) in perModDependencies)
        {
            foreach (var dep in deps)
            {
                if (builtinIds.Contains(dep.DependencyModId)) continue;
                if (installedModIds.Contains(dep.DependencyModId)) continue;

                if (!missing.TryGetValue(dep.DependencyModId, out var entry))
                {
                    KnownMods.TryGetValue(dep.DependencyModId, out var known);
                    entry = new MissingDependency
                    {
                        DependencyModId = dep.DependencyModId,
                        DisplayName = known.DisplayName ?? dep.DependencyModId,
                        VersionRange = dep.VersionRange,
                        ModrinthSlug = known.ModrinthSlug
                    };
                    missing[dep.DependencyModId] = entry;
                }
                if (!entry.RequiredByModNames.Contains(modName))
                    entry.RequiredByModNames.Add(modName);
            }
        }

        result.MissingDependencies.AddRange(missing.Values);
        return result;
    }

    /// <summary>
    /// 读取一个 jar 内的 fabric.mod.json，返回 (自身 mod id, 依赖列表)。
    /// 不是 Fabric mod（没有这个文件，比如纯 Forge mod）或解析失败时返回 (null, 空列表)，
    /// 静默跳过——这只是"锦上添花"的辅助诊断，不该因为个别 jar 结构异常打断整个分析流程。
    /// </summary>
    private static (string? SelfId, List<ModDependencyRequirement> Deps) TryReadFabricMetadata(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entry = archive.GetEntry("fabric.mod.json");
            if (entry == null) return (null, new List<ModDependencyRequirement>());

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            string? selfId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            var deps = new List<ModDependencyRequirement>();
            if (root.TryGetProperty("depends", out var dependsProp) && dependsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dependsProp.EnumerateObject())
                {
                    // 版本要求可能是字符串("*"/">=0.90.0")，也可能是数组(["*", ">=1.0.0"])——
                    // 数组形式取第一个元素展示即可，这里只做展示用途，不做真正的范围求交。
                    string versionRange = "*";
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        versionRange = prop.Value.GetString() ?? "*";
                    else if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                        versionRange = prop.Value[0].GetString() ?? "*";

                    deps.Add(new ModDependencyRequirement { DependencyModId = prop.Name, VersionRange = versionRange });
                }
            }

            return (selfId, deps);
        }
        catch
        {
            return (null, new List<ModDependencyRequirement>());
        }
    }
}
