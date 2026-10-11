using System.IO;

namespace XCL2.App.Services.Worlds;

internal static class WorldCatalog
{
    internal static string[] GameRoots()
    {
        var roots = new List<string>();
        foreach (var folder in ConfigService.Active?.Config.Folders.Select(f => f.Path).ToArray() ?? Array.Empty<string>())
        {
            roots.Add(folder);
            var versions = Path.Combine(folder, "versions");
            try { if (Directory.Exists(versions)) { WorldArchiveService.RejectLinks(versions); roots.AddRange(Directory.EnumerateDirectories(versions).Take(2000)); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static IReadOnlyList<WorldArchive> ReadAll(Action<string>? warning = null)
        => WorldArchiveService.Discover(GameRoots(), warning);
    internal static void RequireKnown(string path)
    {
        var full = Path.GetFullPath(path);
        if (!GameRoots().Any(root => Path.GetDirectoryName(full)?.Equals(Path.GetFullPath(Path.Combine(root, "saves")), StringComparison.OrdinalIgnoreCase) == true))
            throw new UnauthorizedAccessException("插件仅可访问已配置游戏目录中的存档。");
        WorldArchiveService.RejectLinks(full);
    }
}
