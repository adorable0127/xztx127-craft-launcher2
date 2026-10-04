using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record ModInstallPlan(string Source, string Destination, IReadOnlyList<string> ReplacedFiles);

/// <summary>按文件名和顶层模组 ID 替换旧包；前置、provides 和嵌套 jar 不作为替换依据。</summary>
public static class ModInstallService
{
    private const string DisabledSuffix = ".disabled";
    private static readonly object InstallGate = new();

    public static List<ModInstallPlan> PrepareBatch(IEnumerable<string> sources, string modsDir)
    {
        var selected = new List<(string Path, HashSet<string> Ids)>();
        foreach (var source in sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ids = ReadModIds(source);
            // 同批次拖入同一模组的多个版本时，以最后一个为准。
            selected.RemoveAll(item => ids.Overlaps(item.Ids) ||
                string.Equals(Path.GetFileName(item.Path), Path.GetFileName(source), StringComparison.OrdinalIgnoreCase));
            selected.Add((source, ids));
        }

        var existing = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir).Where(IsModFile).Select(p => (Path: p, Ids: ReadModIds(p))).ToList()
            : new List<(string Path, HashSet<string> Ids)>();
        return selected.Select(item =>
        {
            var name = Path.GetFileName(item.Path);
            var old = existing.Where(file => item.Ids.Overlaps(file.Ids) ||
                string.Equals(EnabledName(file.Path), name, StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Path).ToArray();
            // 更新已禁用的模组时保留禁用状态。
            var disabled = old.Length > 0 && old.All(p => p.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase));
            return new ModInstallPlan(item.Path, Path.GetFullPath(Path.Combine(modsDir, name + (disabled ? DisabledSuffix : ""))), old);
        }).ToList();
    }

    public static void Install(ModInstallPlan plan)
    {
        lock (InstallGate)
        {
            // 确认期间或另一批安装可能已更新目录，执行时仍按同一替换规则处理当前文件。
            var current = PrepareBatch(new[] { plan.Source }, Path.GetDirectoryName(plan.Destination)!).Single();
            InstallCore(current);
        }
    }

    private static void InstallCore(ModInstallPlan plan)
    {
        var source = Path.GetFullPath(plan.Source);
        var destination = Path.GetFullPath(plan.Destination);
        var modsDir = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(modsDir);
        var staging = Path.Combine(modsDir, ".xcl-mod-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var stagedNew = Path.Combine(staging, "incoming.jar");
        var moved = new List<(string Original, string Backup)>();
        var installed = false;
        var restored = true;
        try
        {
            // 先完整复制新包，再移动旧包，复制失败时旧模组仍在原位。
            File.Copy(source, stagedNew);
            var originals = plan.ReplacedFiles.Append(destination)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
            foreach (var original in originals)
            {
                var backup = Path.Combine(staging, moved.Count + ".old");
                File.Move(original, backup);
                moved.Add((original, backup));
            }
            File.Move(stagedNew, destination);
            installed = true;
            foreach (var old in moved.Where(item => !item.Original.Equals(destination, StringComparison.OrdinalIgnoreCase)))
                Plugins.PluginHostBridge.Current?.NotifyModChange(old.Original, "removed");
            Plugins.PluginHostBridge.Current?.NotifyModChange(destination, "installed");
        }
        catch
        {
            foreach (var file in Enumerable.Reverse(moved))
            {
                try { File.Move(file.Backup, file.Original, overwrite: true); }
                catch { restored = false; }
            }
            if (!restored)
                throw new IOException($"替换失败，部分旧模组无法恢复；旧文件已保留在 {staging}");
            throw;
        }
        finally
        {
            if (installed || restored)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch { /* 清理失败不改变安装结果，也不把临时文件当作模组加载。 */ }
            }
        }
    }

    private static bool IsModFile(string path) => EnabledName(path).EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    private static string EnabledName(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^DisabledSuffix.Length] : name;
    }

    private static HashSet<string> ReadModIds(string path)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonElement element, string key)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                ids.Add(value.GetString()!);
        }
        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var name in new[] { "fabric.mod.json", "quilt.mod.json", "mcmod.info" })
            {
                var entry = archive.GetEntry(name);
                if (entry == null || entry.Length > 1024 * 1024) continue;
                try
                {
                    using var stream = entry.Open();
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;
                    if (name == "fabric.mod.json") Add(root, "id");
                    else if (name == "quilt.mod.json" && root.TryGetProperty("quilt_loader", out var loader)) Add(loader, "id");
                    else if (name == "mcmod.info")
                    {
                        var list = root;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var mods)) list = mods;
                        if (list.ValueKind == JsonValueKind.Array)
                            foreach (var mod in list.EnumerateArray()) Add(mod, "modid");
                        else Add(list, "modid");
                    }
                }
                catch (JsonException) { /* 其他加载器的元数据仍可读取。 */ }
            }
            foreach (var name in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
            {
                var entry = archive.GetEntry(name);
                if (entry == null || entry.Length > 1024 * 1024) continue;
                using var reader = new StreamReader(entry.Open());
                var inMod = false;
                while (reader.ReadLine() is { } line)
                {
                    var text = line.Trim();
                    if (text.StartsWith('[')) inMod = Regex.IsMatch(text, @"^\[\[\s*mods\s*\]\]");
                    if (!inMod) continue;
                    var match = Regex.Match(text, "^modId\\s*=\\s*[\"']([^\"']+)[\"']");
                    if (match.Success) ids.Add(match.Groups[1].Value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 无元数据的包只按同名文件替换，不能猜文件名前缀删掉其他模组。
        }
        return ids;
    }
}
