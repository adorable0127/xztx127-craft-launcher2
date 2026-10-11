using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services.Plugins;

/// <summary>插件声明的依赖；版本范围使用空格连接的 SemVer 比较项。</summary>
public sealed record PluginDependency(string Id, string VersionRange, bool Optional = false);
public sealed record PluginLoadDiagnostic(string Code, ImmutableArray<string> RelatedPluginIds, string SafeMessage);
/// <summary>加载前读取的不可变清单；InstanceId 包含独立安装副本后缀。</summary>
public sealed record PluginMetadata(string DeclaredId, string InstanceId, string Name, string Version,
    string Author, string Description, Version MinApiVersion, Version? MaxApiVersion, string? MinLauncherVersion,
    ImmutableArray<PluginDependency> Dependencies, ImmutableArray<string> Conflicts,
    ImmutableArray<string> Permissions, int Priority, bool IsLegacy);

internal static class PluginApi
{
    internal static readonly Version Version = new(2, 3);
}

internal sealed record PluginSemanticVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string[] Prerelease)
    : IComparable<PluginSemanticVersion>
{
    private static readonly Regex Pattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
    internal static PluginSemanticVersion Parse(string value)
    {
        var match = Pattern.Match(value);
        if (!match.Success) throw new InvalidDataException("插件版本必须是语义化版本，例如 1.0.0。");
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        if (pre.Any(p => p.All(char.IsAsciiDigit) && p.Length > 1 && p[0] == '0'))
            throw new InvalidDataException("预发布版本的数字标识不能有前导零。");
        return new(BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value), BigInteger.Parse(match.Groups[3].Value), pre);
    }

    public int CompareTo(PluginSemanticVersion? other)
    {
        if (other == null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return Prerelease.Length == other.Prerelease.Length ? 0 : Prerelease.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var a = Prerelease[i]; var b = other.Prerelease[i];
            var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
            result = an && bn ? BigInteger.Parse(a).CompareTo(BigInteger.Parse(b)) :
                an != bn ? an ? -1 : 1 : string.CompareOrdinal(a, b);
            if (result != 0) return result;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    internal static bool InRange(string version, string range)
    {
        var parsed = Parse(version);
        var comparisons = range.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (comparisons.Length == 0) throw new InvalidDataException("依赖版本范围不能为空。");
        var includePrerelease = false;
        var matches = true;
        foreach (var term in comparisons)
        {
            var op = term.StartsWith(">=", StringComparison.Ordinal) || term.StartsWith("<=", StringComparison.Ordinal) ? term[..2] :
                term[0] is '>' or '<' or '=' ? term[..1] : "=";
            var target = Parse(op == "=" && term[0] != '=' ? term : term[op.Length..]);
            includePrerelease |= target.Prerelease.Length != 0;
            var comparison = parsed.CompareTo(target);
            matches &= op switch { ">=" => comparison >= 0, "<=" => comparison <= 0,
                ">" => comparison > 0, "<" => comparison < 0, _ => comparison == 0 };
        }
        return matches && (parsed.Prerelease.Length == 0 || includePrerelease);
    }
}

internal static class PluginManifestReader
{
    internal const string FileName = "plugin.json";
    private sealed class Manifest
    {
        public int SchemaVersion { get; init; } = 1;
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string Author { get; init; } = "";
        public string Description { get; init; } = "";
        public string Entry { get; init; } = "";
        public string Kind { get; init; } = "dll";
        public string MinApiVersion { get; init; } = "1.4";
        public string? MaxApiVersion { get; init; }
        public string? MinLauncherVersion { get; init; }
        public PluginDependency[] Dependencies { get; init; } = Array.Empty<PluginDependency>();
        public string[] Conflicts { get; init; } = Array.Empty<string>();
        public string[] Permissions { get; init; } = Array.Empty<string>();
        public int Priority { get; init; }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex IdPattern = new(@"\A[a-z0-9][a-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);
    internal static void ValidateId(string id)
    {
        if (!IdPattern.IsMatch(id)) throw new InvalidDataException("插件 Id 必须使用小写字母、数字、点、下划线或连字符。");
    }
    internal static PluginMetadata? Read(string entryPath, string suffix)
    {
        var path = Path.Combine(Path.GetDirectoryName(entryPath)!, FileName);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("插件清单过大。");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("插件清单为空。");
        ValidateId(manifest.Id);
        if (manifest.SchemaVersion is not (1 or 2)) throw new InvalidDataException("不支持此插件清单版本。");
        if (manifest.SchemaVersion == 2) _ = PluginPackageSignature.Verify(Path.GetDirectoryName(entryPath)!);
        if (string.IsNullOrWhiteSpace(manifest.Name)) throw new InvalidDataException("插件清单缺少名称。");
        _ = PluginSemanticVersion.Parse(manifest.Version);
        if (manifest.Entry != Path.GetFileName(entryPath) || manifest.Entry.Contains('\\') || manifest.Entry.Contains('/') ||
            manifest.Kind != (Path.GetExtension(entryPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "exe" : "dll"))
            throw new InvalidDataException("插件清单 entry/kind 与安装入口不一致。");
        if ((File.GetAttributes(entryPath) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("插件入口和清单不能是链接文件。");
        if (manifest.Priority is < -1000 or > 1000) throw new InvalidDataException("插件 priority 必须在 -1000 到 1000 之间。");
        if (!System.Version.TryParse(manifest.MinApiVersion, out var min) || min.Build >= 0 ||
            (manifest.MaxApiVersion != null && (!System.Version.TryParse(manifest.MaxApiVersion, out _) ||
             System.Version.Parse(manifest.MaxApiVersion).Build >= 0))) throw new InvalidDataException("API 版本必须使用 major.minor。");
        var max = manifest.MaxApiVersion == null ? null : System.Version.Parse(manifest.MaxApiVersion);
        if (max != null && max < min) throw new InvalidDataException("最大 API 版本不能小于最低版本。");
        if (manifest.MinLauncherVersion != null && !System.Version.TryParse(manifest.MinLauncherVersion, out _))
            throw new InvalidDataException("启动器最低版本格式无效。");
        if (manifest.Dependencies == null || manifest.Conflicts == null || manifest.Permissions == null)
            throw new InvalidDataException("清单集合不能为 null。");
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency == null) throw new InvalidDataException("依赖项不能为空。");
            ValidateId(dependency.Id);
            _ = PluginSemanticVersion.InRange("0.0.0", dependency.VersionRange);
        }
        foreach (var conflict in manifest.Conflicts) ValidateId(conflict);
        if (manifest.Dependencies.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Dependencies.Length)
            throw new InvalidDataException("插件依赖 Id 重复。");
        foreach (var permission in manifest.Permissions)
            if (!PluginPermissionNames.All.Contains(permission, StringComparer.Ordinal))
                throw new InvalidDataException("未知插件权限：" + permission);
        return new(manifest.Id, manifest.Id + suffix, manifest.Name, manifest.Version, manifest.Author, manifest.Description,
            min, max, manifest.MinLauncherVersion, manifest.Dependencies.ToImmutableArray(), manifest.Conflicts.Distinct().ToImmutableArray(),
            manifest.Permissions.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray(), manifest.Priority, false);
    }
    internal static string ReadEntry(string directory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, FileName)));
        var entry = json.RootElement.GetProperty("entry").GetString();
        if (string.IsNullOrWhiteSpace(entry) || Path.GetFileName(entry) != entry || entry.Contains('\\') || entry.Contains(':') ||
            !(entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("插件包入口必须是包根目录的 DLL 或 EXE。");
        _ = Read(Path.Combine(directory, entry), "");
        return entry;
    }
    internal static PluginMetadata Legacy(string id, string name, string version) => new(id, id, name, version,
        "", "", new Version(1, 1), null, null, ImmutableArray<PluginDependency>.Empty,
        ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, 0, true);
}

/// <summary>权限字符串与清单共用；授权仅限制通过宿主 API 的访问。</summary>
public static class PluginPermissionNames
{
    public const string ProjectsRead = "projects.read", ProjectsWrite = "projects.write", TasksManage = "tasks.manage";
    public const string DownloadsControl = "downloads.control", InstancesWrite = "instances.write", ModsWrite = "mods.write",
        ResourcesRead = "resources.read", ResourcesInstall = "resources.install", JavaRead = "java.read", JavaSelect = "java.select";
    public const string LauncherControl = "launcher.control", SettingsWrite = "settings.write", UiWindow = "ui.window";
    public const string UiPreview = "ui.preview", WorldsRead = "worlds.read", WorldsWrite = "worlds.write";
    public const string AccountsRead = "accounts.read", InstancesRead = "instances.read", ModsRead = "mods.read",
        DownloadsRead = "downloads.read", SettingsRead = "settings.read", EnvironmentRead = "environment.read",
        Lifecycle = "events.lifecycle", LaunchModify = "launch.modify", DownloadIntercept = "downloads.intercept",
        InstanceValidate = "instances.validate", Network = "network", DownloadProvider = "providers.download",
        ModProvider = "providers.mod", LoaderProvider = "providers.loader", AuthProvider = "auth.provider",
        InstanceFilesRead = "files.instances.read", InstanceFilesWrite = "files.instances.write",
        ExternalFilesRead = "files.external.read", ExternalFilesWrite = "files.external.write",
        ProcessInstall = "process.install", UiBasic = "ui.basic", UiComponents = "ui.components",
        UiNavigation = "ui.navigation", UiInstanceActions = "ui.instanceActions", UiSettings = "ui.settings",
        UiBackground = "ui.background", Messaging = "plugin.messaging";
    public static ImmutableArray<string> All { get; } = ImmutableArray.Create(ProjectsRead, ProjectsWrite, TasksManage, DownloadsControl, InstancesWrite, ModsWrite, ResourcesRead, ResourcesInstall, JavaRead, JavaSelect, LauncherControl, SettingsWrite, UiWindow, UiPreview, WorldsRead, WorldsWrite, AccountsRead, InstancesRead, ModsRead, DownloadsRead,
        SettingsRead, EnvironmentRead, Lifecycle, LaunchModify, DownloadIntercept, InstanceValidate, Network,
        DownloadProvider, ModProvider, LoaderProvider, AuthProvider, InstanceFilesRead, InstanceFilesWrite,
        ExternalFilesRead, ExternalFilesWrite, ProcessInstall, UiBasic, UiComponents, UiNavigation, UiInstanceActions,
        UiSettings, UiBackground, Messaging);
}

internal sealed class PluginLoadCandidate
{
    internal required string FilePath { get; init; }
    internal PluginMetadata? Metadata { get; init; }
    internal bool WillRun { get; init; }
    internal List<PluginLoadDiagnostic> Errors { get; } = new();
    internal Dictionary<string, PluginLoadCandidate> Dependencies { get; } = new(StringComparer.Ordinal);
    internal string SortId => Metadata?.InstanceId ?? FilePath;
    internal void Error(string code, string message, params string[] ids) => Errors.Add(new(code, ids.ToImmutableArray(), message));
}

internal static class PluginDependencyPlanner
{
    internal static PluginLoadCandidate[] Sort(IEnumerable<PluginLoadCandidate> source)
    {
        var all = source.OrderBy(c => c.SortId, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in all.Where(c => c.Metadata != null).GroupBy(c => c.Metadata!.InstanceId).Where(g => g.Count() > 1))
            foreach (var candidate in duplicate) candidate.Error("InvalidManifest", "重复的插件运行 Id：" + duplicate.Key);
        foreach (var item in all.Where(c => c.Metadata != null))
        {
            var metadata = item.Metadata!;
            if (metadata.MinApiVersion > PluginApi.Version || metadata.MaxApiVersion != null && metadata.MaxApiVersion < PluginApi.Version)
                item.Error("ApiIncompatible", $"需要 API {metadata.MinApiVersion} 至 {metadata.MaxApiVersion?.ToString() ?? "不限"}，宿主为 {PluginApi.Version}。");
            var launcherVersion = typeof(IPlugin).Assembly.GetName().Version ?? new Version(0, 0);
            if (metadata.MinLauncherVersion != null && System.Version.Parse(metadata.MinLauncherVersion) > launcherVersion)
                item.Error("LauncherIncompatible", "启动器版本低于插件要求：" + metadata.MinLauncherVersion);
            foreach (var conflict in metadata.Conflicts)
            {
                var other = all.FirstOrDefault(c => c != item && c.WillRun && c.Metadata?.DeclaredId == conflict);
                if (!item.WillRun || other == null) continue;
                item.Error("Conflict", "与插件冲突：" + conflict, conflict);
                other.Error("Conflict", "与插件冲突：" + metadata.DeclaredId, metadata.DeclaredId);
            }
            foreach (var dependency in metadata.Dependencies)
            {
                var declared = all.Where(c => c.Metadata?.DeclaredId == dependency.Id).ToArray();
                var compatible = declared.Where(c => PluginSemanticVersion.InRange(c.Metadata!.Version, dependency.VersionRange)).ToArray();
                var main = compatible.FirstOrDefault(c => c.Metadata!.InstanceId == dependency.Id);
                var parent = main ?? (compatible.Length == 1 ? compatible[0] : null);
                if (parent == null)
                {
                    if (dependency.Optional && declared.Length == 0) continue;
                    item.Error(compatible.Length > 1 ? "DependencyAmbiguous" : declared.Length == 0 ? "DependencyMissing" : "DependencyVersionMismatch",
                        "依赖不可解析：" + dependency.Id + "（" + dependency.VersionRange + "）", dependency.Id);
                }
                else if (parent == item) item.Error("DependencyCycle", "插件不能依赖自身。", dependency.Id);
                else item.Dependencies.Add(dependency.Id, parent);
            }
        }

        var state = new Dictionary<PluginLoadCandidate, int>();
        var stack = new List<PluginLoadCandidate>();
        void Visit(PluginLoadCandidate item)
        {
            if (state.TryGetValue(item, out var value))
            {
                if (value == 1)
                    foreach (var cyclic in stack.Skip(stack.IndexOf(item)))
                        cyclic.Error("DependencyCycle", "存在必需依赖循环。", stack.Skip(stack.IndexOf(item)).Select(c => c.SortId).ToArray());
                return;
            }
            state[item] = 1; stack.Add(item);
            foreach (var pair in item.Dependencies)
                if (item.Metadata!.Dependencies.Any(d => d.Id == pair.Key && !d.Optional)) Visit(pair.Value);
            stack.RemoveAt(stack.Count - 1); state[item] = 2;
        }
        foreach (var item in all) Visit(item);
        bool changed;
        do
        {
            changed = false;
            foreach (var item in all.Where(c => c.WillRun && c.Errors.Count == 0 && c.Metadata != null))
            {
                var missing = item.Dependencies.FirstOrDefault(pair => item.Metadata!.Dependencies.Any(d => d.Id == pair.Key && !d.Optional) &&
                    (!pair.Value.WillRun || pair.Value.Errors.Count != 0));
                if (missing.Value == null) continue;
                item.Error("DependencyUnavailable", "必需依赖当前不可运行：" + missing.Key, missing.Key); changed = true;
            }
        } while (changed);
        var ordered = new List<PluginLoadCandidate>();
        var adding = new HashSet<PluginLoadCandidate>();
        var added = new HashSet<PluginLoadCandidate>();
        void Add(PluginLoadCandidate item)
        {
            if (added.Contains(item) || !adding.Add(item)) return; // 可选依赖的排序环不阻止运行。
            foreach (var parent in item.Dependencies.Values.OrderBy(c => c.SortId, StringComparer.Ordinal)) Add(parent);
            adding.Remove(item); added.Add(item); ordered.Add(item);
        }
        foreach (var item in all) Add(item);
        return ordered.ToArray();
    }
}
