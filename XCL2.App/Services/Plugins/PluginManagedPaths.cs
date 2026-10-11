using System.IO;

namespace XCL2.App.Services.Plugins;

internal static class PluginManagedPaths
{
    internal static void RequireLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name is "." or ".." ||
            name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.EndsWith(' ') || name.EndsWith('.') || name.Any(c => c < 32) ||
            name.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0)
            throw new PluginOperationException("InvalidArgument", "名称必须是有效的单个 Windows 文件名。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
            throw new PluginOperationException("InvalidArgument", "名称不能使用 Windows 保留设备名。");
    }
    internal static void RequireNoLinks(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new PluginOperationException("InvalidPath", "目标必须位于已登记的游戏目录内。");
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new PluginOperationException("LinkedPath", "插件写入不支持符号链接或目录联接。");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}
