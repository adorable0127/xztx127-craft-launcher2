using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Services.Plugins;

/// <summary>Persistent verified backup of plugin installation, configuration, data and enablement state.</summary>
internal static class PluginUpgradeBackup
{
    internal static void Restore(string backup, string pluginRoot)
    {
        var staging = Path.Combine(pluginRoot, "staging", "restore-" + Guid.NewGuid().ToString("N"));
        var rollback = staging + "-old";
        Directory.CreateDirectory(staging); Directory.CreateDirectory(rollback);
        var moved = new List<string>(); var installed = new List<string>(); bool completed = false;
        try
        {
            using (var zip = ZipFile.OpenRead(backup))
            {
                var index = zip.GetEntry("backup-sha256.json") ?? throw new InvalidDataException("不是 XCL2 插件备份。");
                if (index.Length > 16 * 1024 * 1024 || zip.Entries.Count > 100001) throw new InvalidDataException("备份清单过大。");
                using var manifest = index.Open();
                var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest) ?? throw new InvalidDataException("备份清单为空。");
                if (zip.Entries.Count != hashes.Count + 1) throw new InvalidDataException("备份包含未校验文件。");
                long size = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in hashes)
                {
                    var name = pair.Key;
                    if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Split('/').Any(p => p is "." or ".." or "") ||
                        !(name.StartsWith("installed/", StringComparison.Ordinal) || name.StartsWith("config/", StringComparison.Ordinal) || name.StartsWith("data/", StringComparison.Ordinal) || name == "plugins-state.json") || !names.Add(name))
                        throw new InvalidDataException("备份包含不允许的路径。");
                    var entry = zip.GetEntry(name) ?? throw new InvalidDataException("备份缺少文件。");
                    size += entry.Length;
                    if (size > 4L * 1024 * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("备份过大或含链接。");
                    var target = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
                    using var verify = File.OpenRead(target);
                    if (Convert.ToHexString(SHA256.HashData(verify)) != pair.Value) throw new InvalidDataException("备份校验失败，未恢复任何文件。");
                }
            }
            static void Move(string source, string target)
            { if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target); }
            foreach (var name in new[] { "installed", "config", "data", "plugins-state.json" })
            {
                var current = Path.Combine(pluginRoot, name); WorldArchiveService.RejectLinks(current);
                if (Directory.Exists(current) || File.Exists(current)) { Move(current, Path.Combine(rollback, name)); moved.Add(name); }
                var next = Path.Combine(staging, name);
                if (Directory.Exists(next) || File.Exists(next)) { Move(next, current); installed.Add(name); }
            }
            completed = true;
        }
        catch
        {
            foreach (var name in installed.AsEnumerable().Reverse())
            { var path = Path.Combine(pluginRoot, name); if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); }
            foreach (var name in moved.AsEnumerable().Reverse())
            { var old = Path.Combine(rollback, name); var current = Path.Combine(pluginRoot, name); if (Directory.Exists(old)) Directory.Move(old, current); else File.Move(old, current); }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            // Never delete a remaining rollback snapshot if rollback itself failed.
            if (Directory.Exists(rollback) && (completed || !Directory.EnumerateFileSystemEntries(rollback).Any())) Directory.Delete(rollback, true);
        }
    }
    internal static string Create(string pluginRoot)
    {
        var directory = Path.Combine(pluginRoot, "backups"); WorldArchiveService.RejectLinks(directory); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"plugins-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var temporary = target + ".tmp";
        try
        {
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal); long bytes = 0;
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                void Add(string file)
                {
                    WorldArchiveService.RejectLinks(file); bytes += new FileInfo(file).Length;
                    if (bytes > 4L * 1024 * 1024 * 1024 || hashes.Count >= 100000) throw new IOException("插件备份超过 4 GiB / 10 万文件上限；未修改插件。请先整理插件数据。");
                    var relative = Path.GetRelativePath(pluginRoot, file).Replace('\\', '/');
                    using var input = File.OpenRead(file); hashes.Add(relative, Convert.ToHexString(SHA256.HashData(input))); input.Position = 0;
                    using var output = zip.CreateEntry(relative, CompressionLevel.Fastest).Open(); input.CopyTo(output);
                }
                void Walk(string dir)
                { WorldArchiveService.RejectLinks(dir); foreach (var file in Directory.EnumerateFiles(dir)) Add(file); foreach (var child in Directory.EnumerateDirectories(dir)) Walk(child); }
                foreach (var name in new[] { "installed", "config", "data" }) { var dir = Path.Combine(pluginRoot, name); if (Directory.Exists(dir)) Walk(dir); }
                var state = Path.Combine(pluginRoot, "plugins-state.json"); if (File.Exists(state)) Add(state);
                using var manifest = zip.CreateEntry("backup-sha256.json").Open(); JsonSerializer.Serialize(manifest, hashes);
            }
            using (var verify = ZipFile.OpenRead(temporary))
                foreach (var pair in hashes)
                { using var stream = verify.GetEntry(pair.Key)!.Open(); if (Convert.ToHexString(SHA256.HashData(stream)) != pair.Value) throw new IOException("备份校验失败，未修改插件。"); }
            File.Move(temporary, target); return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
