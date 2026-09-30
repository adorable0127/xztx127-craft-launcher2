using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services.Plugins;

/// <summary>一次扫描发现的 DLL / EXE 插件及其运行结果，供插件管理界面展示。</summary>
public sealed class LoadedPluginInfo
{
    /// <summary>插件 DLL / EXE 的安装路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>成功加载时的插件实例；加载失败（缺依赖/没有实现 IPlugin/构造函数抛异常等）
    /// 时为 null，具体原因看 LoadError。</summary>
    public IPlugin? Plugin { get; init; }

    /// <summary>加载失败时的异常描述；成功加载时为 null。</summary>
    public string? LoadError { get; init; }

    /// <summary>用户是否已启用这个插件（持久化在 plugins-state.json 里）。
    /// 被禁用的插件文件仍然会被扫描到、显示在列表里，但不会执行 Initialize。</summary>
    public bool Enabled { get; set; }

    /// <summary>该插件对应的 PluginContext，仅在本轮 DLL 成功初始化时非 null，
    /// "配置"按钮据此拿到 ctx 传给 Plugin.CreateSettingsPanel。</summary>
    public PluginContext? Context { get; init; }

    /// <summary>外部进程插件；不会按 .NET IPlugin 接口加载。</summary>
    public bool IsExecutable { get; init; }

    /// <summary>由启动器启动并托管的进程，退出/重新扫描时会结束。</summary>
    public Process? RunningProcess { get; init; }

    /// <summary>仅导入时运行模式下，该插件已成功启动过。</summary>
    public bool StartedOnce { get; init; }
}

/// <summary>安装冲突必须由调用方选择；取消不改文件、不停止插件。</summary>
public enum PluginInstallConflictAction { Cancel, Copy, Replace }
public sealed record PluginOperationResult(string FilePath, bool Success, string? Error = null);

/// <summary>管理独立安装、影子加载、状态与单个插件的停止/删除。</summary>
public sealed class PluginManager
{
    private sealed class PluginState
    {
        public PluginState() { }
        public bool Enabled { get; set; } = true;
        public bool StartedOnce { get; set; }
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _directory;
        public PluginLoadContext(string path) : base($"XCL2Plugin:{Path.GetFileName(path)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(path);
            _directory = Path.GetDirectoryName(path)!;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // 接口必须和宿主共享同一个程序集，否则 IsAssignableFrom 永远为 false。
            if (name.Name == typeof(IPlugin).Assembly.GetName().Name) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path == null && name.Name != null)
            {
                var adjacent = Path.Combine(_directory, name.Name + ".dll");
                if (File.Exists(adjacent)) path = adjacent;
            }
            return path == null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path == null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }


    private sealed class InstallManifest
    {
        public string EntryFile { get; set; } = "";
        public string? InstanceSuffix { get; set; }
    }
    private sealed record Runtime(AssemblyLoadContext Context, string Directory);
    private const string ManifestName = ".xcl-plugin.json";
    private readonly string _pluginsRoot;
    private readonly string _installedDir;
    private readonly string _configDir;
    private readonly string _dataDir;
    private readonly string _stateFilePath;
    private readonly Dictionary<string, Runtime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _logSink;
    private readonly HashSet<string> _activePluginIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<LoadedPluginInfo> Plugins { get; private set; } = Array.Empty<LoadedPluginInfo>();
    public event Action? Changed;
    public PluginLaunchMode StartPolicy { get; set; } = PluginLaunchMode.EachLaunch;

    public PluginManager(Action<string>? logSink = null)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2", "Plugins"), logSink) { }

    internal PluginManager(string rootDirectory, Action<string>? logSink = null)
    {
        _logSink = logSink ?? (_ => { });
        _pluginsRoot = Path.GetFullPath(rootDirectory);
        _installedDir = Path.Combine(_pluginsRoot, "installed");
        _configDir = Path.Combine(_pluginsRoot, "config");
        _dataDir = Path.Combine(_pluginsRoot, "data");
        _stateFilePath = Path.Combine(_pluginsRoot, "plugins-state.json");
        EnsureDirectories();
    }
    public string InstalledDirectory => _installedDir;
    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_installedDir);
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_dataDir);
    }
    private static bool IsPluginFile(string file) =>
        Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    private string StateKey(string path) => Path.GetRelativePath(_installedDir, path);
    private static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(id.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".." ? "_" : safe;
    }
    private static InstallManifest? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestName);
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path));
        if (manifest == null || !IsPluginFile(manifest.EntryFile) ||
            Path.GetFileName(manifest.EntryFile) != manifest.EntryFile || manifest.EntryFile.Contains('\\'))
            throw new InvalidDataException("插件安装清单中的入口无效。");
        return manifest;
    }
    private IEnumerable<string> Entries()
    {
        foreach (var file in Directory.EnumerateFiles(_installedDir).Where(IsPluginFile)) yield return file;
        foreach (var dir in Directory.EnumerateDirectories(_installedDir))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            InstallManifest? manifest;
            try { manifest = ReadManifest(dir); }
            catch (Exception ex) { _logSink($"[PluginManager] 安装清单损坏：{dir}：{ex.Message}"); manifest = null; }
            if (manifest != null)
            {
                var entry = Path.Combine(dir, manifest.EntryFile);
                if (File.Exists(entry)) yield return entry;
            }
            else foreach (var file in Directory.EnumerateFiles(dir).Where(f => IsPluginFile(f) &&
                Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase)))
                yield return file;
        }
    }
    public string? FindInstallConflict(string sourcePath)
    {
        var name = SafeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        var directory = Path.Combine(_installedDir, name);
        if (Directory.Exists(directory)) return directory;
        return Directory.EnumerateFiles(_installedDir).FirstOrDefault(f => IsPluginFile(f) &&
            Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("插件目录不能包含目录链接。");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("插件目录不能包含文件链接。");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source)) CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
    private static void ValidateSource(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("插件文件不存在。", path);
        if (!IsPluginFile(path)) throw new InvalidDataException("插件必须是 .dll 或 .exe 文件。");
        if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)) _ = AssemblyName.GetAssemblyName(path);
        else
        {
            using var input = File.OpenRead(path);
            if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new InvalidDataException("EXE 文件没有有效的可执行文件头。");
        }
    }

    /// <summary>旧的一参数接口仍然保留；有冲突时由三参数调用方显式决定。</summary>
    public string InstallFromFile(string sourcePath) => InstallFromFile(sourcePath, PluginInstallConflictAction.Cancel)
        ?? throw new IOException("同名插件已安装，请选择复制为副本、替换或取消。");

    public string? InstallFromFile(string sourcePath, PluginInstallConflictAction action)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var conflict = FindInstallConflict(sourcePath);
        if (conflict != null && action == PluginInstallConflictAction.Cancel) return null;
        ValidateSource(sourcePath);
        var name = SafeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        var destinationDir = Path.Combine(_installedDir, name);
        string? suffix = null;
        var copyDependenciesFrom = action == PluginInstallConflictAction.Copy && conflict != null && Directory.Exists(conflict) ? conflict : null;
        if (conflict != null && action == PluginInstallConflictAction.Copy)
        {
            var number = 1;
            do { destinationDir = Path.Combine(_installedDir, $"{name}-copy-{number++}"); }
            while (Directory.Exists(destinationDir) || File.Exists(destinationDir));
            suffix = Guid.NewGuid().ToString("N");
            conflict = null;
        }
        var stage = Path.Combine(_pluginsRoot, "staging", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(_pluginsRoot, "staging", Guid.NewGuid().ToString("N"));
        var oldEntries = conflict == null ? Array.Empty<string>() : Entries().Where(f =>
            f.Equals(conflict, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(f)!.Equals(conflict, StringComparison.OrdinalIgnoreCase)).ToArray();
        var state = LoadState();
        var movedOld = false;
        var movedNew = false;
        try
        {
            // 先完整写入暂存区，再停止旧实例；依赖与原 DLL/EXE 文件名保持不变。
            if (conflict != null && Directory.Exists(conflict))
            {
                CopyDirectory(conflict, stage);
                suffix = ReadManifest(conflict)?.InstanceSuffix;
            }
            else if (copyDependenciesFrom != null) CopyDirectory(copyDependenciesFrom, stage);
            else Directory.CreateDirectory(stage);
            var entryName = Path.GetFileName(sourcePath);
            File.Copy(sourcePath, Path.Combine(stage, entryName), true);
            File.WriteAllText(Path.Combine(stage, ManifestName), JsonSerializer.Serialize(new InstallManifest
            { EntryFile = entryName, InstanceSuffix = suffix }));
            foreach (var old in oldEntries) StopOne(old);
            if (conflict != null)
            {
                if (Directory.Exists(conflict)) Directory.Move(conflict, backup);
                else File.Move(conflict, backup);
                movedOld = true;
            }
            Directory.Move(stage, destinationDir);
            movedNew = true;
            var destination = Path.Combine(destinationDir, entryName);
            foreach (var old in oldEntries) state.Remove(StateKey(old));
            state[StateKey(destination)] = new PluginState(); // 替换也算新导入，OnceOnImport 可运行新版本。
            WriteState(state);
            TryDelete(backup);
            return destination;
        }
        catch
        {
            if (movedNew) TryDelete(destinationDir);
            if (movedOld && conflict != null)
            {
                if (Directory.Exists(backup)) Directory.Move(backup, conflict);
                else if (File.Exists(backup)) File.Move(backup, conflict);
            }
            throw;
        }
        finally { TryDelete(stage); }
    }

    public void ScanAndLoad()
    {
        EnsureDirectories();
        var state = LoadState();
        ShutdownAll();
        Plugins = Entries().OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(file => LoadOne(file, state.TryGetValue(StateKey(file), out var saved) ? saved : new PluginState())).ToArray();
        foreach (var p in Plugins) state[StateKey(p.FilePath)] = new PluginState { Enabled = p.Enabled, StartedOnce = p.StartedOnce };
        WriteState(state);
        NotifyChanged();
    }
    private LoadedPluginInfo LoadOne(string filePath, PluginState state)
    {
        var executable = Path.GetExtension(filePath).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var shouldStart = state.Enabled && (StartPolicy == PluginLaunchMode.EachLaunch || !state.StartedOnce);
        PluginContext? context = null;
        IPlugin? plugin = null;
        string? reservedId = null;
        try
        {
            var parent = Path.GetDirectoryName(filePath)!;
            var manifest = parent.Equals(_installedDir, StringComparison.OrdinalIgnoreCase) ? null : ReadManifest(parent);
            var suffix = manifest?.InstanceSuffix is { Length: > 0 } id ? "@" + SafeFileName(id) : "";
            if (executable)
            {
                Process? process = null;
                if (shouldStart)
                {
                    var psi = new ProcessStartInfo(filePath) { UseShellExecute = false, WorkingDirectory = parent };
                    psi.Environment["XCL2_PLUGIN_DATA_DIR"] = EnsurePluginDataDir("exe." + Path.GetFileNameWithoutExtension(filePath) + suffix);
                    process = Process.Start(psi) ?? throw new InvalidOperationException("EXE 插件无法启动。");
                    state.StartedOnce = true;
                }
                return new LoadedPluginInfo { FilePath = filePath, IsExecutable = true, Enabled = state.Enabled,
                    RunningProcess = process, StartedOnce = state.StartedOnce };
            }
            if (!shouldStart) return new LoadedPluginInfo { FilePath = filePath, Enabled = state.Enabled, StartedOnce = state.StartedOnce };
            // 从影子目录加载，安装文件不会被 ALC 锁住，删除/替换无需强制 GC。
            var shadow = Path.Combine(_pluginsRoot, "runtime", Guid.NewGuid().ToString("N"));
            try
            {
                if (parent.Equals(_installedDir, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(shadow);
                    foreach (var adjacent in Directory.EnumerateFiles(parent)) File.Copy(adjacent, Path.Combine(shadow, Path.GetFileName(adjacent)));
                }
                else CopyDirectory(parent, shadow);
                var shadowEntry = Path.Combine(shadow, Path.GetFileName(filePath));
                var alc = new PluginLoadContext(shadowEntry);
                _runtimes[filePath] = new Runtime(alc, shadow);
                var asm = alc.LoadFromAssemblyPath(shadowEntry);
                var types = asm.GetTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t) && t.IsPublic && !t.IsAbstract &&
                    !t.IsInterface && t.GetConstructor(Type.EmptyTypes) != null).ToArray();
                if (types.Length != 1) throw new InvalidDataException("DLL 必须包含且仅包含一个公共 IPlugin 实现（公共无参构造）。");
                plugin = (IPlugin)Activator.CreateInstance(types[0])!;
            }
            catch { if (!_runtimes.ContainsKey(filePath)) TryDelete(shadow); throw; }
            if (string.IsNullOrWhiteSpace(plugin.Id)) throw new InvalidDataException("插件 Id 不能为空。");
            var instanceId = plugin.Id + suffix;
            reservedId = SafeFileName(instanceId);
            if (!_activePluginIds.Add(reservedId))
            {
                reservedId = null;
                throw new InvalidDataException($"插件 Id 或配置路径重复：{plugin.Id}；请通过“复制为副本”安装独立实例。");
            }
            context = new PluginContext(instanceId, new PluginConfigService(Path.Combine(_configDir, SafeFileName(instanceId) + ".json")),
                EnsurePluginDataDir(instanceId), line => _logSink($"[Plugin:{instanceId}] {line}"));
            plugin.Initialize(context);
            state.StartedOnce = true;
            return new LoadedPluginInfo { FilePath = filePath, Plugin = plugin, Enabled = state.Enabled, Context = context, StartedOnce = state.StartedOnce };
        }
        catch (Exception ex)
        {
            try { context?.Dispose(); } catch (Exception cleanup) { _logSink(cleanup.Message); }
            if (context != null) try { plugin?.Shutdown(); } catch (Exception cleanup) { _logSink(cleanup.Message); }
            if (reservedId != null) _activePluginIds.Remove(reservedId);
            ReleaseRuntime(filePath);
            _logSink($"[PluginManager] 加载 {Path.GetFileName(filePath)} 失败：{ex.Message}");
            return new LoadedPluginInfo { FilePath = filePath, LoadError = ex.Message, Enabled = state.Enabled,
                IsExecutable = executable, StartedOnce = state.StartedOnce };
        }
    }
    private string EnsurePluginDataDir(string id)
    {
        var path = Path.Combine(_dataDir, SafeFileName(id));
        Directory.CreateDirectory(path);
        return path;
    }
    private string RequireInstalledEntry(string path)
    {
        path = Path.GetFullPath(path);
        if (!Entries().Any(e => e.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("只能操作 installed 目录内已发现的插件入口。");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能操作链接文件。");
        return path;
    }
    public void SetEnabled(string filePath, bool enabled) => SetEnabled(new[] { filePath }, enabled);
    public void SetEnabled(IEnumerable<string> filePaths, bool enabled)
    {
        var paths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(RequireInstalledEntry).ToArray();
        var state = LoadState();
        foreach (var path in paths)
        {
            var key = StateKey(path);
            if (!state.TryGetValue(key, out var entry)) state[key] = entry = new PluginState();
            entry.Enabled = enabled;
        }
        WriteState(state);
        foreach (var plugin in Plugins.Where(p => paths.Contains(p.FilePath, StringComparer.OrdinalIgnoreCase))) plugin.Enabled = enabled;
    }
    /// <summary>实际移除安装目录和状态；配置/数据保留，避免丢失用户内容。批量逐项报告。</summary>
    public IReadOnlyList<PluginOperationResult> Uninstall(IEnumerable<string> filePaths)
    {
        var results = new List<PluginOperationResult>();
        foreach (var source in filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            try
            {
                var path = RequireInstalledEntry(source);
                var parent = Path.GetDirectoryName(path)!;
                var rootEntry = parent.Equals(_installedDir, StringComparison.OrdinalIgnoreCase);
                var target = rootEntry ? path : parent;
                var entries = Entries().Where(e => e.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    (!rootEntry && Path.GetDirectoryName(e)!.Equals(parent, StringComparison.OrdinalIgnoreCase))).ToArray();
                var state = LoadState();
                foreach (var entry in entries) StopOne(entry);
                var removed = Path.Combine(_pluginsRoot, "removed", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
                if (rootEntry) File.Move(target, removed); else Directory.Move(target, removed);
                try
                {
                    foreach (var entry in entries) state.Remove(StateKey(entry));
                    WriteState(state);
                }
                catch
                {
                    if (rootEntry) File.Move(removed, target); else Directory.Move(removed, target);
                    throw;
                }
                var warning = TryDelete(removed) ? null : "已卸载；占用中的残留文件位于 Plugins/removed，关闭占用后可清理。";
                results.Add(new PluginOperationResult(source, true, warning));
            }
            catch (Exception ex) { results.Add(new PluginOperationResult(source, false, ex.Message)); }
        }
        // 只刷新清单，不重启无关插件；失败项也继续可见，可再次删除或重新加载。
        var stateNow = LoadState();
        var current = Plugins.ToDictionary(p => p.FilePath, StringComparer.OrdinalIgnoreCase);
        Plugins = Entries().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(path => current.TryGetValue(path, out var info)
            ? info : new LoadedPluginInfo { FilePath = path, Enabled = stateNow.TryGetValue(StateKey(path), out var s) ? s.Enabled : true,
                IsExecutable = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) }).ToArray();
        NotifyChanged();
        return results;
    }
    private void StopOne(string path)
    {
        var p = Plugins.FirstOrDefault(p => p.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (p?.RunningProcess is { } process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new IOException("插件进程未停止，未删除或替换文件。");
            }
            process.Dispose();
        }
        try { p?.Context?.Dispose(); } catch (Exception ex) { _logSink($"[PluginManager] 清理扩展失败：{ex.Message}"); }
        if (p?.Context != null && p.Plugin != null)
        {
            try { p.Plugin.Shutdown(); } catch (Exception ex) { _logSink($"[PluginManager] Shutdown 失败：{ex.Message}"); }
            _activePluginIds.Remove(SafeFileName(p.Context.PluginId));
        }
        ReleaseRuntime(path);
        Plugins = Plugins.Where(p => !p.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void ReleaseRuntime(string path)
    {
        if (!_runtimes.Remove(path, out var runtime)) return;
        try { runtime.Context.Unload(); } catch (Exception ex) { _logSink(ex.Message); }
        TryDelete(runtime.Directory); // ALC 卸载受 GC 时机影响；不影响安装目录操作。
    }
    public void ShutdownAll()
    {
        foreach (var p in Plugins.ToArray())
            try { StopOne(p.FilePath); } catch (Exception ex) { _logSink($"[PluginManager] 停止失败：{ex.Message}"); }
        foreach (var path in _runtimes.Keys.ToArray()) ReleaseRuntime(path);
        if (Plugins.Count > 0) throw new IOException("有插件进程无法停止，请结束该进程后重试。");
        _activePluginIds.Clear();
    }
    private bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) { _logSink($"[PluginManager] 清理 {path} 失败：{ex.Message}"); return false; }
    }
    private Dictionary<string, PluginState> LoadState()
    {
        if (!File.Exists(_stateFilePath)) return new(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(_stateFilePath));
        var state = new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.EnumerateObject()) state[entry.Name] = entry.Value.ValueKind switch
        {
            JsonValueKind.True => new PluginState(),
            JsonValueKind.False => new PluginState { Enabled = false },
            JsonValueKind.Object => entry.Value.Deserialize<PluginState>() ?? new PluginState(),
            _ => throw new InvalidDataException("插件状态格式无效。")
        };
        return state;
    }
    private void WriteState(Dictionary<string, PluginState> state)
    {
        var temporary = _stateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _stateFilePath, true);
        }
        finally { TryDelete(temporary); }
    }
    private void NotifyChanged()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList())
            try { handler(); } catch (Exception ex) { _logSink($"[PluginManager] 刷新列表失败：{ex.Message}"); }
    }
}
