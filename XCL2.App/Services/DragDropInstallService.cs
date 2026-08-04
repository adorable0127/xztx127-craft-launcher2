using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services;

/// <summary>
/// 拖拽安装：把用户从资源管理器拖进启动器窗口的文件，按类型自动装到**当前选中的游戏实例**里。
///
/// 支持的类型和去向：
///   .jar                      → &lt;实例&gt;/mods/              （Mod）
///   .mrpack                   → 整合包导入（Modrinth 格式）
///   .xclpack                  → 整合包导入（本启动器格式）
///   .zip                      → 需要开包判断，见 ClassifyZip：
///                               含 pack.mcmeta + assets/  → resourcepacks/（材质包）
///                               含 pack.mcmeta + data/    → saves/&lt;存档&gt;/datapacks/（数据包）
///                               含 shaders/               → shaderpacks/（光影包）
///                               含 modrinth.index.json    → 当成 mrpack 整合包
///                               含 mods/ 或 manifest.json → 当成整合包
///                               含 level.dat              → saves/（存档）
///   .mcworld/.mcpack/.mcaddon → 基岩版内容（交给 BedrockContentService）
///   文件夹                     → 递归展开后按上面的规则逐个处理
///
/// ===== 为什么要"开包判断"而不是只看扩展名 =====
/// 材质包、光影包、数据包、存档、整合包**全都是 .zip**，扩展名完全一样。
/// 只看扩展名的话，用户拖一个材质包进来，十有八九被装成整合包，把 mods 目录搅乱。
/// 所以这里必须读一下 zip 的顶层结构再决定。判断依据都是各自格式的**强制文件**
/// （材质包/数据包必须有 pack.mcmeta，存档必须有 level.dat，mrpack 必须有
/// modrinth.index.json），不是靠猜文件名。
///
/// ===== 路径穿越防护 =====
/// 所有解压路径都要经过 EnsureInside 校验，防止 zip 里构造 "../../xxx" 的条目
/// 写到实例目录之外（跟 ModpackService.Import 里已有的防护同一个道理）。
/// </summary>
public class DragDropInstallService
{
    /// <summary>一次拖拽的处理结果，供界面汇总提示"装了几个、去了哪、有哪些没认出来"。</summary>
    public sealed class DropResult
    {
        public List<string> Installed { get; } = new();
        public List<string> Skipped { get; } = new();
        public List<string> Modpacks { get; } = new();
        public List<string> BedrockItems { get; } = new();
        public bool AnythingHappened => Installed.Count > 0 || Modpacks.Count > 0 || BedrockItems.Count > 0;
    }

    public enum DropKind
    {
        Unknown,
        Mod,
        ResourcePack,
        DataPack,
        ShaderPack,
        World,
        Modpack,
        BedrockContent,
        /// <summary>服务端 jar：由 MainWindow 单独处理（装进服务器实例的 mods/），
        /// 不经过 InstallMany 的客户端实例目录。Classify 永远不会自己产出这个值，
        /// 它只会作为"上层按页面/设置决定的覆盖"传进来。</summary>
        ServerJar,
    }

    /// <summary>
    /// 一个 .zip 是不是"内容特征不明确、需要问用户"。
    /// ClassifyZip 会先找各格式的强制文件（pack.mcmeta / level.dat / shaders/ /
    /// modrinth.index.json），绝大多数包都能自动认出来；返回 Unknown 的才需要问。
    /// </summary>
    public bool IsAmbiguousZip(string path)
        => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
           && ClassifyZip(path) == DropKind.Unknown;

    /// <summary>
    /// 判断一个拖进来的文件属于哪一类。不做任何写操作，纯识别——
    /// 界面可以先用它做拖拽悬停时的高亮提示（"松手安装 3 个 Mod"），再决定要不要真的装。
    /// </summary>
    public DropKind Classify(string path)
    {
        if (Directory.Exists(path)) return DropKind.Unknown; // 文件夹交给 InstallMany 递归展开

        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".jar":
                return DropKind.Mod;
            case ".mrpack":
            case ".xclpack":
                return DropKind.Modpack;
            case ".mcworld":
            case ".mcpack":
            case ".mcaddon":
            case ".mctemplate":
                return DropKind.BedrockContent;
            case ".zip":
                return ClassifyZip(path);
            default:
                return DropKind.Unknown;
        }
    }

    /// <summary>
    /// 开包看顶层结构，区分材质包/数据包/光影包/存档/整合包。
    /// 读不开（损坏/加密）时返回 Unknown，交由调用方提示用户，而不是硬塞进某个目录。
    /// </summary>
    private static DropKind ClassifyZip(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            bool Has(string name) => archive.Entries.Any(e =>
                e.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));

            bool HasTopFolder(string folder) => archive.Entries.Any(e =>
            {
                var p = e.FullName.Replace('\\', '/');
                return p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
            });

            // 整合包优先判断：mrpack 的 modrinth.index.json / CurseForge 的 manifest.json
            // 都是顶层唯一标识，误判成材质包的后果最严重（整包内容会散进材质包目录）。
            if (Has("modrinth.index.json")) return DropKind.Modpack;
            if (Has("manifest.json") && HasTopFolder("overrides")) return DropKind.Modpack;
            if (Has("xcl2-modpack.json")) return DropKind.Modpack;

            // 存档：level.dat 是存档的强制文件
            if (Has("level.dat")) return DropKind.World;
            // 有的存档压缩时多包了一层文件夹，level.dat 在二级目录
            if (archive.Entries.Any(e => e.Name.Equals("level.dat", StringComparison.OrdinalIgnoreCase)))
                return DropKind.World;

            // 光影包：OptiFine/Iris 光影包必须有 shaders/ 目录
            if (HasTopFolder("shaders")) return DropKind.ShaderPack;

            // 材质包 vs 数据包：都靠 pack.mcmeta，区别在于伴随的是 assets/ 还是 data/
            if (Has("pack.mcmeta"))
            {
                if (HasTopFolder("assets")) return DropKind.ResourcePack;
                if (HasTopFolder("data")) return DropKind.DataPack;
                return DropKind.ResourcePack; // 只有 pack.mcmeta 时按更常见的材质包处理
            }

            // 只有 mods/ 目录的 zip，按整合包处理（不少人手工打的包就是这样）
            if (HasTopFolder("mods")) return DropKind.Modpack;

            return DropKind.Unknown;
        }
        catch
        {
            return DropKind.Unknown;
        }
    }

    /// <summary>
    /// 把一批拖进来的路径装到指定实例目录。
    /// 整合包和基岩版内容**不在这里直接装**——它们需要额外的用户决策
    /// （整合包要选"新建实例还是并入现有实例"，基岩版要选目标），
    /// 这里只把它们挑出来放进结果里，交给界面弹对应的流程。
    /// </summary>
    /// <param name="instanceDir">目标实例的游戏目录（版本隔离开启时是 versions/&lt;id&gt;，
    /// 关闭时是 .minecraft 根目录——调用方要跟实际启动时用的目录保持一致，否则装了游戏读不到）。</param>
    /// <param name="kindOverrides">按文件路径指定的类型覆盖表。上层会把两种来源的决定
    /// 放进来：①用户在 DropTypeChoiceDialog 里选的；②按当前页面/设置项决定的默认去向
    /// （比如在服务端管理页拖 jar，上层会把它标成"给服务器装"而不是 Mod）。
    /// 没有覆盖的文件走 Classify 自动识别。</param>
    public DropResult InstallMany(IEnumerable<string> paths, string instanceDir,
        IProgress<string>? progress = null,
        IReadOnlyDictionary<string, DropKind>? kindOverrides = null)
    {
        var result = new DropResult();

        foreach (var path in Expand(paths))
        {
            var kind = kindOverrides != null && kindOverrides.TryGetValue(path, out var forced)
                ? forced
                : Classify(path);
            var name = Path.GetFileName(path);

            try
            {
                switch (kind)
                {
                    case DropKind.Mod:
                        CopyInto(path, Path.Combine(instanceDir, "mods"));
                        result.Installed.Add($"{name} → mods");
                        progress?.Report($"已安装 Mod：{name}");
                        break;

                    case DropKind.ResourcePack:
                        CopyInto(path, Path.Combine(instanceDir, "resourcepacks"));
                        result.Installed.Add($"{name} → resourcepacks");
                        progress?.Report($"已安装材质包：{name}");
                        break;

                    case DropKind.ShaderPack:
                        CopyInto(path, Path.Combine(instanceDir, "shaderpacks"));
                        result.Installed.Add($"{name} → shaderpacks");
                        progress?.Report($"已安装光影包：{name}");
                        break;

                    case DropKind.World:
                        ExtractWorld(path, Path.Combine(instanceDir, "saves"));
                        result.Installed.Add($"{name} → saves");
                        progress?.Report($"已导入存档：{name}");
                        break;

                    case DropKind.DataPack:
                        // 数据包必须装进**某一个存档**的 datapacks/，不是实例根目录。
                        // 这里不擅自替用户选存档——只有一个存档时直接装，多个存档时交回界面去问。
                        var saves = SafeListSaves(instanceDir);
                        if (saves.Count == 1)
                        {
                            CopyInto(path, Path.Combine(saves[0], "datapacks"));
                            result.Installed.Add($"{name} → {Path.GetFileName(saves[0])}/datapacks");
                            progress?.Report($"已安装数据包：{name}");
                        }
                        else
                        {
                            result.Skipped.Add($"{name}（数据包需要选择目标存档，当前有 {saves.Count} 个存档）");
                        }
                        break;

                    case DropKind.Modpack:
                        result.Modpacks.Add(path);
                        break;

                    case DropKind.BedrockContent:
                        result.BedrockItems.Add(path);
                        break;

                    case DropKind.ServerJar:
                        // 上层（MainWindow.InstallJarsToSelectedServer）已经装过了，这里跳过，
                        // 不要再往客户端实例的 mods 里复制一份。
                        break;

                    default:
                        result.Skipped.Add($"{name}（认不出是什么类型）");
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Skipped.Add($"{name}（安装失败：{ex.Message}）");
            }
        }

        return result;
    }

    /// <summary>文件夹展开成里面的文件（只展开一层子目录，避免用户误拖整个 D 盘导致遍历爆炸）。</summary>
    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                yield return p;
            }
            else if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(p, "*", SearchOption.TopDirectoryOnly); }
                catch { continue; }
                foreach (var f in files) yield return f;
            }
        }
    }

    private static void CopyInto(string sourceFile, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, Path.GetFileName(sourceFile));

        // 同名文件已存在时不静默覆盖——加序号另存，避免用户手滑把已有的同名 mod 顶掉。
        if (File.Exists(dest))
        {
            var baseName = Path.GetFileNameWithoutExtension(dest);
            var ext = Path.GetExtension(dest);
            var i = 2;
            while (File.Exists(dest))
            {
                dest = Path.Combine(targetDir, $"{baseName} ({i}){ext}");
                i++;
            }
        }

        File.Copy(sourceFile, dest);
    }

    /// <summary>存档 zip 解压到 saves/ 下。带路径穿越防护。</summary>
    private static void ExtractWorld(string zipPath, string savesDir)
    {
        Directory.CreateDirectory(savesDir);

        var worldName = Path.GetFileNameWithoutExtension(zipPath);
        var target = Path.Combine(savesDir, worldName);
        var i = 2;
        while (Directory.Exists(target))
        {
            target = Path.Combine(savesDir, $"{worldName} ({i})");
            i++;
        }
        Directory.CreateDirectory(target);

        using var archive = ZipFile.OpenRead(zipPath);

        // 有的存档 zip 顶层直接就是 level.dat，有的多包了一层同名文件夹。
        // 多包一层时要把那一层剥掉，否则会变成 saves/世界名/世界名/level.dat，游戏读不到。
        var topLevelDat = archive.Entries.Any(e =>
            e.FullName.Replace('\\', '/').Equals("level.dat", StringComparison.OrdinalIgnoreCase));
        string? stripPrefix = null;
        if (!topLevelDat)
        {
            var datEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("level.dat", StringComparison.OrdinalIgnoreCase));
            if (datEntry != null)
            {
                var p = datEntry.FullName.Replace('\\', '/');
                var idx = p.LastIndexOf('/');
                if (idx > 0) stripPrefix = p[..(idx + 1)];
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var rel = entry.FullName.Replace('\\', '/');
            if (stripPrefix != null)
            {
                if (!rel.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                rel = rel[stripPrefix.Length..];
            }
            if (rel.Length == 0) continue;

            var destPath = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!EnsureInside(destPath, target)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    /// <summary>解出来的路径必须落在目标目录内部，挡住 zip 里的 "../" 路径穿越。</summary>
    private static bool EnsureInside(string destPath, string rootDir)
    {
        var full = Path.GetFullPath(destPath);
        var root = Path.GetFullPath(rootDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SafeListSaves(string instanceDir)
    {
        try
        {
            var savesDir = Path.Combine(instanceDir, "saves");
            if (!Directory.Exists(savesDir)) return new List<string>();
            return Directory.GetDirectories(savesDir).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
