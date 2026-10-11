using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using XCL2.App.Services.Workbench;

namespace XCL2.App.Services.Plugins;

public sealed record PluginProject(string Id, string Name, string Root);
public sealed class PluginProjectService
{
    private readonly PluginContext _context;
    private readonly ConcurrentDictionary<string, PluginProject> _projects = new(StringComparer.Ordinal);
    internal PluginProjectService(PluginContext context)
    {
        _context = context;
        context.AuthorizationChanged += () => { if (!context.Permissions.Has(PluginPermissionNames.ProjectsRead)) _projects.Clear(); };
    }
    internal string Root(string projectId)
    {
        _context.Permissions.Demand(PluginPermissionNames.ProjectsRead);
        return _projects.TryGetValue(projectId, out var project) ? project.Root : throw new ArgumentException("项目未打开或授权已撤销。");
    }
    private T Read<T>(string projectId, Func<string, T> operation)
    {
        using var scope = new PluginOperationScope(_context, default, PluginPermissionNames.ProjectsRead);
        var result = operation(Root(projectId)); scope.Check(); return result;
    }
    private PluginProject Add(string path)
    {
        ProjectFiles.Resolve(path, "", true);
        var project = new PluginProject(Guid.NewGuid().ToString("N"), Path.GetFileName(path), Path.GetFullPath(path));
        if (_projects.Count >= 32) throw new InvalidOperationException("最多打开 32 个项目。");
        _projects[project.Id] = project; return project;
    }
    [WorkbenchEndpoint("workbench.projects.chooseFolder")]
    public async Task<PluginProject?> ChooseFolderAsync(CancellationToken ct = default)
    {
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.ProjectsRead);
        return await _context.Host.OnUiAsync(() =>
        {
            scope.Check();
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "允许插件 “" + _context.Metadata.Name + "” 访问此项目", Multiselect = false };
            if (dialog.ShowDialog() != true) return null;
            scope.Check(); return Add(dialog.FolderName);
        }, scope.Token).ConfigureAwait(false);
    }
    [WorkbenchEndpoint("workbench.projects.openDataProject")]
    public PluginProject OpenDataProject(string name)
    {
        _context.Permissions.Demand(PluginPermissionNames.ProjectsRead); _context.Permissions.Demand(PluginPermissionNames.ProjectsWrite);
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9_-]{1,80}$")) throw new ArgumentException("项目名格式无效。");
        var path = ProjectFiles.Resolve(_context.DataDirectory, "projects/" + name); Directory.CreateDirectory(path); return Add(path);
    }
    [WorkbenchEndpoint("workbench.projects.list")]
    public IReadOnlyList<PluginProject> GetOpenProjects() { _context.Permissions.Demand(PluginPermissionNames.ProjectsRead); return _projects.Values.OrderBy(p => p.Name).ToArray(); }
    [WorkbenchEndpoint("workbench.projects.get")]
    public PluginProject GetProject(string projectId) { _ = Root(projectId); return _projects[projectId]; }
    [WorkbenchEndpoint("workbench.projects.close")]
    public bool CloseProject(string projectId) { _context.Permissions.Demand(PluginPermissionNames.ProjectsRead); return _projects.TryRemove(projectId, out _); }
    [WorkbenchEndpoint("workbench.projects.files")]
    public IReadOnlyList<ProjectEntry> GetFiles(string projectId, string glob = "*", int limit = 1000) => Read(projectId, root => ProjectFiles.Enumerate(root, glob, limit, _context.Stopping).ToArray());
    [WorkbenchEndpoint("workbench.projects.directories")]
    public IReadOnlyList<string> GetDirectories(string projectId, string path = "") => Read(projectId, root => Directory.EnumerateDirectories(ProjectFiles.Resolve(root, path, true)).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).Take(1000).Select(p => Path.GetRelativePath(root, p)).ToArray());
    [WorkbenchEndpoint("workbench.projects.searchText")]
    public ProjectSearchResult SearchText(string projectId, string query, string glob = "*", int limit = 200) => Read(projectId, root => ProjectFiles.Search(root, query, glob, maxResults: limit, ct: _context.Stopping));
    [WorkbenchEndpoint("workbench.projects.searchRegex")]
    public ProjectSearchResult SearchRegex(string projectId, string pattern, string glob = "*", int limit = 200) => Read(projectId, root => ProjectFiles.Search(root, pattern, glob, regex: true, maxResults: limit, ct: _context.Stopping));
    [WorkbenchEndpoint("workbench.projects.readRange")]
    public ProjectTextPage ReadRange(string projectId, string path, int startLine = 1, int lineCount = 200) => Read(projectId, root => ProjectFiles.ReadPage(root, path, startLine, lineCount, _context.Stopping));
    [WorkbenchEndpoint("workbench.projects.readHead")]
    public ProjectTextPage ReadHead(string projectId, string path, int lineCount = 100) => ReadRange(projectId, path, 1, lineCount);
    [WorkbenchEndpoint("workbench.projects.readTail")]
    public string ReadTail(string projectId, string path, int lineCount = 100)
    { if (lineCount is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(lineCount)); return Read(projectId, root => string.Join("\n", Text(root, path).Split('\n').TakeLast(lineCount)) is var text && text.Length <= 80000 ? text : throw new IOException("结果超过 80000 字符。")); }
    private static string Text(string root, string path) => ProjectFiles.Decode(ProjectFiles.ReadBytes(ProjectFiles.Resolve(root, path))).Text;
    [WorkbenchEndpoint("workbench.projects.readText")]
    public string ReadText(string projectId, string path) => Read(projectId, root => Text(root, path) is var text && text.Length <= 80000 ? text : throw new IOException("请使用分段读取。"));
    [WorkbenchEndpoint("workbench.projects.readBytes")]
    public byte[] ReadBytes(string projectId, string path, long offset = 0, int count = 4096) => Read(projectId, root =>
    { if (offset < 0 || count is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(count)); using var stream = File.OpenRead(ProjectFiles.Resolve(root, path)); stream.Position = offset; var data = new byte[Math.Min(count, (int)Math.Clamp(stream.Length - offset, 0, count))]; stream.ReadExactly(data); return data; });
    [WorkbenchEndpoint("workbench.projects.hash")]
    public string GetHash(string projectId, string path) => Read(projectId, root => ProjectFiles.HashFile(ProjectFiles.Resolve(root, path)));
    [WorkbenchEndpoint("workbench.projects.fileInfo")]
    public ProjectEntry GetFileInfo(string projectId, string path) => Read(projectId, root => { var info = new FileInfo(ProjectFiles.Resolve(root, path)); return new ProjectEntry(path, info.Length, info.LastWriteTimeUtc); });
    [WorkbenchEndpoint("workbench.projects.fileExists")]
    public bool FileExists(string projectId, string path) => Read(projectId, root => File.Exists(ProjectFiles.Resolve(root, path)));
    [WorkbenchEndpoint("workbench.projects.extensionStats")]
    public IReadOnlyDictionary<string, int> GetExtensionStats(string projectId) => GetFiles(projectId, limit: 20000).GroupBy(f => Path.GetExtension(f.Path).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
    [WorkbenchEndpoint("workbench.projects.largestFiles")]
    public IReadOnlyList<ProjectEntry> GetLargestFiles(string projectId, int count = 20) => GetFiles(projectId, limit: 20000).OrderByDescending(f => f.Bytes).Take(Math.Clamp(count, 1, 200)).ToArray();
    [WorkbenchEndpoint("workbench.projects.findFiles")]
    public IReadOnlyList<ProjectEntry> FindFiles(string projectId, string name) => GetFiles(projectId, limit: 20000).Where(f => f.Path.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(1000).ToArray();
    [WorkbenchEndpoint("workbench.projects.fileCount")]
    public int GetFileCount(string projectId) => GetFiles(projectId, limit: 20000).Count;
    [WorkbenchEndpoint("workbench.projects.totalBytes")]
    public long GetTotalBytes(string projectId) => GetFiles(projectId, limit: 20000).Sum(f => f.Bytes);
    [WorkbenchEndpoint("workbench.projects.newline")]
    public string GetNewlineStyle(string projectId, string path) => Read(projectId, root => { var text = Text(root, path); return text.Contains("\r\n") ? text.Replace("\r\n", "").Contains('\n') ? "mixed" : "CRLF" : "LF"; });
    [WorkbenchEndpoint("workbench.projects.encoding")]
    public string GetEncoding(string projectId, string path) => Read(projectId, root => { var data = ProjectFiles.Decode(ProjectFiles.ReadBytes(ProjectFiles.Resolve(root, path))); return data.Encoding.WebName + (data.Bom ? "+BOM" : ""); });
    [WorkbenchEndpoint("workbench.projects.readJson")]
    public JsonElement ReadJson(string projectId, string path) { using var doc = JsonDocument.Parse(ReadText(projectId, path)); return doc.RootElement.Clone(); }
    [WorkbenchEndpoint("workbench.projects.jsonProperties")]
    public IReadOnlyList<string> GetJsonProperties(string projectId, string path) => ReadJson(projectId, path).EnumerateObject().Select(p => p.Name).ToArray();
    [WorkbenchEndpoint("workbench.projects.jsonPointer")]
    public JsonElement ReadJsonPointer(string projectId, string path, string pointer)
    {
        var node = ReadJson(projectId, path); if (pointer.Length == 0) return node;
        if (!pointer.StartsWith('/')) throw new ArgumentException("JSON Pointer 必须以 / 开始。");
        foreach (var part in pointer[1..].Split('/')) { var key = part.Replace("~1", "/").Replace("~0", "~"); node = node.ValueKind == JsonValueKind.Array ? node[int.Parse(key)] : node.GetProperty(key); }
        return node.Clone();
    }
}
