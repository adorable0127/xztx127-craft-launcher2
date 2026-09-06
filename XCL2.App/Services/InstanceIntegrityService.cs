using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 启动前主动检查一个版本是否完整可用——解决"下载了一半，用户点取消或关掉启动器窗口，
/// 导致版本文件残缺，之前完全没有拦截，直接就被当成正常版本启动，结果各种纹理/声音/
/// 世界生成方面的诡异 bug"这一类问题的根源。
///
/// 跟 LauncherService.CheckMissingLibraries 是互补关系，不是重复实现：
/// - CheckMissingLibraries 只覆盖 libraries + natives，是"启动前最后一道防线"，
///   面向的是"启动一定会用到、缺了就直接崩"的东西。
/// - 这里额外覆盖 version json 本身、client jar、以及 assets（原来完全没人查），
///   是"版本列表要不要展示这个版本 / 该不该在启动前拦下来给用户选择"这一层更早的判断，
///   两者的检查结果最终会合并展示给用户（见 MainWindow 里调用处）。
/// </summary>
public class InstanceIntegrityService
{
    public class IntegrityCheckResult
    {
        /// <summary>version json 本身缺失/解析失败——这种情况下面所有其它检查都无意义，直接判定不完整。</summary>
        public bool VersionJsonMissing { get; set; }
        /// <summary>client jar 缺失或 0 字节。原版直接检查自己的 jar；有 inheritsFrom 的（Forge/NeoForge 等）检查父版本的 jar。</summary>
        public bool ClientJarMissing { get; set; }
        /// <summary>复用 LauncherService.CheckMissingLibraries 的结果，原样透传，不重复实现一遍。</summary>
        public List<string> MissingLibraries { get; set; } = new();
        /// <summary>assets 缺失的描述文本；Simple 模式下最多是一条"数量对不上"的汇总，Strict 模式下可能是很多条。</summary>
        public List<string> MissingAssets { get; set; } = new();

        public bool HasProblems => VersionJsonMissing || ClientJarMissing
            || MissingLibraries.Count > 0 || MissingAssets.Count > 0;

        /// <summary>解析出来的 VersionDetail，供调用方后续"继续启动并补全"时直接复用，不用重新读一次 json。</summary>
        public VersionDetail? Detail { get; set; }
    }

    private readonly LauncherService _launcherService;

    public InstanceIntegrityService(LauncherService launcherService)
    {
        _launcherService = launcherService;
    }

    public IntegrityCheckResult Check(string minecraftDir, string versionId, IntegrityCheckMode mode)
    {
        var result = new IntegrityCheckResult();
        var versionDir = Path.Combine(minecraftDir, "versions", versionId);

        // 版本文件夹被用户改过名，内部 json/jar 文件名不一定等于文件夹名——跟 FolderService.ScanVersions
        // 用的是同一套"文件夹里唯一 json 就认它"兜底规则，两边口径保持一致。
        var jsonPath = ResolveVersionFile(versionDir, versionId, "json");
        if (jsonPath == null)
        {
            result.VersionJsonMissing = true;
            return result;
        }

        VersionDetail detail;
        try
        {
            detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jsonPath)) ?? new VersionDetail();
        }
        catch
        {
            result.VersionJsonMissing = true;
            return result;
        }
        if (string.IsNullOrEmpty(detail.Id)) detail.Id = versionId;
        result.Detail = detail;

        // client jar：原版查自己，继承类（Forge/NeoForge/Fabric profile）查父版本的 jar，
        // 跟 FolderService.ScanVersions 判定 IsInstalled 用的是同一条逻辑。
        var jarBaseName = string.IsNullOrEmpty(detail.Id) ? versionId : detail.Id;
        var ownJarPath = Path.Combine(versionDir, $"{jarBaseName}.jar");
        var ownJarOk = File.Exists(ownJarPath) && new FileInfo(ownJarPath).Length > 0;
        var inheritedJarOk = false;
        if (!ownJarOk && !string.IsNullOrEmpty(detail.InheritsFrom))
        {
            var parentDir = Path.Combine(minecraftDir, "versions", detail.InheritsFrom);
            var parentJsonPath = ResolveVersionFile(parentDir, detail.InheritsFrom, "json");
            if (parentJsonPath != null)
            {
                try
                {
                    var parentDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
                    var parentJarBaseName = string.IsNullOrEmpty(parentDetail?.Id) ? detail.InheritsFrom : parentDetail!.Id;
                    var parentJarPath = Path.Combine(parentDir, $"{parentJarBaseName}.jar");
                    inheritedJarOk = File.Exists(parentJarPath) && new FileInfo(parentJarPath).Length > 0;
                }
                catch { /* 父版本 json 本身损坏，视为继承链不完整，下面按缺失处理 */ }
            }
        }
        result.ClientJarMissing = !ownJarOk && !inheritedJarOk;

        // libraries + natives：直接复用已有实现，不重复一遍同样的解析逻辑。
        try
        {
            result.MissingLibraries = _launcherService.CheckMissingLibraries(minecraftDir, versionId);
        }
        catch
        {
            // 复用的方法内部已经有自己的兜底(读取失败就返回空列表)，这里理论上不会抛，多包一层只是防御。
        }

        // assets：原来完全没有人检查过，这里是新增的部分。
        CheckAssets(minecraftDir, detail, mode, result.MissingAssets);

        return result;
    }

    private static void CheckAssets(string minecraftDir, VersionDetail detail, IntegrityCheckMode mode, List<string> missingAssets)
    {
        if (detail.AssetIndex == null) return; // 极少数远古版本没有独立资源索引，没有可查的东西

        var indexPath = Path.Combine(minecraftDir, "assets", "indexes", $"{detail.AssetIndex.Id}.json");
        if (!File.Exists(indexPath))
        {
            missingAssets.Add($"资源索引文件缺失: assets/indexes/{detail.AssetIndex.Id}.json");
            return;
        }

        AssetIndexFile index;
        try
        {
            index = JsonSerializer.Deserialize<AssetIndexFile>(File.ReadAllText(indexPath)) ?? new AssetIndexFile();
        }
        catch (Exception ex)
        {
            missingAssets.Add($"资源索引文件已损坏，无法解析: {indexPath}（{ex.Message}）");
            return;
        }

        var objectsDir = Path.Combine(minecraftDir, "assets", "objects");

        if (mode == IntegrityCheckMode.Strict)
        {
            // 严格模式：逐个核对每一个 object 是否存在、大小是否匹配。数量可能有几千个，
            // 调用方（UI 线程）应当在后台线程里跑这个方法，避免卡界面。
            foreach (var (name, obj) in index.Objects)
            {
                if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
                var path = Path.Combine(objectsDir, obj.Hash[..2], obj.Hash);
                if (!File.Exists(path))
                {
                    missingAssets.Add($"资源文件缺失: {name} ({obj.Hash})");
                }
                else if (new FileInfo(path).Length != obj.Size)
                {
                    missingAssets.Add($"资源文件大小不匹配(可能是半成品): {name} ({obj.Hash})");
                }
            }
        }
        else
        {
            // 简单模式：只按"实际已存在的 object 文件数量"和"索引里应该有的数量"做粗略比对，
            // 不逐个核对哈希/大小——几千个小文件的存在性检查本身也有一定 IO 开销，
            // 用总数对不上来快速判断"大概率没下完"，而不追求发现"个别文件损坏但数量凑巧还对"
            // 这种边缘情况（这种情况留给用户手动"重新安装"或以后切到 Strict 模式再查）。
            var expected = index.Objects.Count;
            if (expected == 0) return;

            var existing = 0;
            if (Directory.Exists(objectsDir))
            {
                foreach (var (_, obj) in index.Objects)
                {
                    if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
                    if (File.Exists(Path.Combine(objectsDir, obj.Hash[..2], obj.Hash))) existing++;
                }
            }

            if (existing < expected)
            {
                missingAssets.Add($"资源文件数量不足：索引要求 {expected} 个，实际找到 {existing} 个，" +
                    $"缺少 {expected - existing} 个（简单模式仅按数量粗查，不保证已存在的文件本身没有损坏）。");
            }
        }
    }

    /// <summary>跟 LauncherService/FolderService 里同名私有方法保持一致的兜底规则：
    /// 优先按"文件夹名.json"找，找不到再退而求其次找文件夹里唯一的 .json。</summary>
    private static string? ResolveVersionFile(string dir, string id, string ext)
    {
        var exact = Path.Combine(dir, $"{id}.{ext}");
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        var candidates = Directory.GetFiles(dir, $"*.{ext}");
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
