using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>
/// 本地已安装 Mod 管理：扫描 &lt;minecraftDir&gt;/mods 文件夹，支持启用/禁用/删除。
///
/// 禁用实现方式：业界通用做法（HMCL/PCL 等主流启动器都这样做）——给文件名加 ".disabled" 后缀，
/// 游戏加载器只认 .jar 结尾的文件，改了后缀游戏就读不到它，等同于禁用；重新去掉后缀即恢复启用。
/// 不用"移到别的文件夹再移回来"这种方式，是因为改后缀是原子的单步操作，不会有"移动到一半崩溃
/// 导致文件彻底丢失引用"的中间状态风险。
/// </summary>
public class LocalModService
{
    private const string DisabledSuffix = ".disabled";

    /// <summary>扫描 mods 文件夹，返回每个 mod 文件的基本信息（含是否已禁用）。文件夹不存在时返回空列表，不抛异常。</summary>
    public List<LocalModInfo> ScanMods(string minecraftDir)
    {
        var result = new List<LocalModInfo>();
        var modsDir = Path.Combine(minecraftDir, "mods");
        if (!Directory.Exists(modsDir)) return result;

        foreach (var file in Directory.GetFiles(modsDir))
        {
            var fileName = Path.GetFileName(file);
            var isDisabled = fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var effectiveName = isDisabled
                ? fileName[..^DisabledSuffix.Length]
                : fileName;

            // 只认 .jar / .jar.disabled，其他文件（比如用户手滑放进 mods 文件夹的说明文档）跳过，
            // 不当成 mod 展示，避免用户对着一个 txt 文件点"禁用"却什么都没发生的困惑。
            if (!effectiveName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

            FileInfo? info = null;
            try { info = new FileInfo(file); } catch { /* 忽略单个文件读取失败，不影响其他文件展示 */ }

            result.Add(new LocalModInfo
            {
                FilePath = file,
                DisplayName = TryReadModName(file, isDisabled) ?? effectiveName,
                FileName = effectiveName,
                IsEnabled = !isDisabled,
                FileSizeBytes = info?.Length ?? 0
            });
        }

        return result.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 尝试从 jar 包内的元数据文件读出 mod 的显示名（比文件名更友好），失败就返回 null 让调用方回退用文件名。
    /// 依次尝试 Fabric (fabric.mod.json) / Forge 新版 (META-INF/mods.toml，简单正则取 name) /
    /// Forge 旧版 (mcmod.info)。任何一步失败都吞掉异常，因为这只是"锦上添花"的展示优化，
    /// 不应该因为个别 mod 包结构异常导致整个扫描列表出不来。
    /// </summary>
    /// <summary>
    /// 公开出来供 CrashAnalyzerService 复用：崩溃分析在把某个 mod jar 认定为"元凶"之后，
    /// 想展示给用户看的是它注册的友好名字（比如"Just Enough Items"），而不是一串
    /// jar 文件名或包名，跟这里 mod 管理页面用的是同一套元数据读取逻辑，没必要另写一份。
    /// </summary>
    public static string? TryReadModName(string jarPath, bool isDisabled)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            var fabricEntry = archive.GetEntry("fabric.mod.json");
            if (fabricEntry != null)
            {
                using var stream = fabricEntry.Open();
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }

            var forgeTomlEntry = archive.GetEntry("META-INF/mods.toml");
            if (forgeTomlEntry != null)
            {
                using var stream = forgeTomlEntry.Open();
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                var match = System.Text.RegularExpressions.Regex.Match(text, "displayName\\s*=\\s*\"([^\"]+)\"");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch
        {
            // jar 可能损坏、被占用、或结构不标准（比如禁用后缀导致某些工具误判），静默回退用文件名
        }

        return null;
    }

    /// <summary>启用一个已禁用的 mod（去掉 .disabled 后缀）。返回操作后的新路径。</summary>
    public string Enable(string filePath)
    {
        if (!filePath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)) return filePath;
        var newPath = filePath[..^DisabledSuffix.Length];
        MoveWithoutOverwrite(filePath, newPath);
        return newPath;
    }

    /// <summary>禁用一个已启用的 mod（加上 .disabled 后缀）。返回操作后的新路径。</summary>
    public string Disable(string filePath)
    {
        if (filePath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)) return filePath;
        var newPath = filePath + DisabledSuffix;
        MoveWithoutOverwrite(filePath, newPath);
        return newPath;
    }

    /// <summary>删除一个 mod 文件（无论是否处于禁用状态）。调用方负责在删除前跟用户做二次确认。</summary>
    public void Delete(string filePath)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    private static void MoveWithoutOverwrite(string source, string dest)
    {
        if (File.Exists(dest))
            throw new IOException($"目标文件已存在，无法重命名：{Path.GetFileName(dest)}");
        File.Move(source, dest);
    }
}

public class LocalModInfo
{
    public string FilePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool IsEnabled { get; set; }
    public long FileSizeBytes { get; set; }

    public string SizeDisplay => FileSizeBytes < 1024 * 1024
        ? $"{FileSizeBytes / 1024.0:0.#} KB"
        : $"{FileSizeBytes / 1024.0 / 1024.0:0.#} MB";

    public string StatusLabel => IsEnabled ? "已启用" : "已禁用";
}
