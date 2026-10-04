using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>加载器安装的数据契约，可独立验证。</summary>
public static class LoaderInstallContracts
{
    public static void MergeParentMetadata(VersionDetail child, VersionDetail parent)
    {
        static string LibraryKey(LibraryEntry library)
        {
            var parts = library.Name.Split(':');
            return parts.Length >= 2 ? parts[0] + ":" + parts[1] + (parts.Length > 3 ? ":" + parts[3] : "") : library.Name;
        }
        var overrides = child.Libraries.Select(LibraryKey).ToHashSet(StringComparer.Ordinal);
        child.Libraries.AddRange(parent.Libraries.Where(l => !overrides.Contains(LibraryKey(l))));
        if (child.Arguments != null || parent.Arguments != null)
            child.Arguments = new ArgumentsSpec
            {
                Game = (parent.Arguments?.Game ?? new List<object>()).Concat(child.Arguments?.Game ?? new List<object>()).ToList(),
                Jvm = (parent.Arguments?.Jvm ?? new List<object>()).Concat(child.Arguments?.Jvm ?? new List<object>()).ToList()
            };
        child.MinecraftArgumentsLegacy ??= parent.MinecraftArgumentsLegacy;
    }

    public static string ClientProfileUrl(string metaBase, string mcVersion, string loaderVersion)
        => $"{metaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}/{Uri.EscapeDataString(loaderVersion.Trim())}/profile/json";

    public static string[] OptiFinePatcherArguments(string vanillaJar, string installerJar, string outputJar)
        => new[] { "-cp", installerJar, "optifine.Patcher", vanillaJar, installerJar, outputJar };
}
