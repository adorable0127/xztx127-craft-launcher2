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

/// <summary>
/// 插件系统的核心服务：负责在启动器启动时扫描插件目录、用可回收的 AssemblyLoadContext
/// 逐个加载 dll、按用户之前保存的启用/禁用状态决定是否调用 Initialize，以及在"插件管理"
/// 界面里响应启用/禁用/重新扫描等操作。
///
/// 插件目录：%AppData%/XCL2/Plugins/installed/<文件名>/<文件名>.dll 或 .exe。
/// 每个插件的依赖文件可放在自己的子目录；兼容旧版 installed/*.dll 根目录布局。
///
/// 加载隔离：每个插件 dll 都用独立的 AssemblyLoadContext（isCollectible: true）加载，
/// 一是避免不同插件之间如果各自带了不同版本的同名依赖 dll 时互相冲突，二是"重新扫描/
/// 重新加载插件"时能把旧的 ALC 连同它加载过的类型一起卸载掉，不会在同一个进程里
/// 越攒越多僵尸程序集。当前版本的卸载走的是"整个 PluginManager 重新扫描时销毁所有旧 ALC
/// 再重建"的简单策略，不支持单独热卸载一个插件而不影响其它插件——这个限制在 PLUGIN_GUIDE.md
/// 里也跟插件作者说明了（Shutdown 不代表这个插件的程序集立刻从内存释放，取决于 GC 时机）。
/// </summary>
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

    private readonly string _pluginsRoot;
    private readonly string _installedDir;
    private readonly string _configDir;
    private readonly string _dataDir;
    private readonly string _stateFilePath;
    private readonly List<AssemblyLoadContext> _loadContexts = new();
    private readonly Action<string> _logSink;

    public IReadOnlyList<LoadedPluginInfo> Plugins { get; private set; } = Array.Empty<LoadedPluginInfo>();

    public PluginLaunchMode StartPolicy { get; set; } = PluginLaunchMode.EachLaunch;

    public PluginManager(Action<string>? logSink = null)
    {
        _logSink = logSink ?? (_ => { });
        _pluginsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2", "Plugins");
        _installedDir = Path.Combine(_pluginsRoot, "installed");
        _configDir = Path.Combine(_pluginsRoot, "config");
        _dataDir = Path.Combine(_pluginsRoot, "data");
        _stateFilePath = Path.Combine(_pluginsRoot, "plugins-state.json");
        EnsureDirectories();
    }

    /// <summary>插件安装根目录，"插件管理"的"打开插件目录"按钮使用此路径。</summary>
    public string InstalledDirectory => _installedDir;

    /// <summary>导入用户确认过的独立 DLL/EXE。每个插件放进同名子目录，依赖可以
    /// 与主 DLL 放在该目录而不被扫描成另一插件。导入后由调用方 ScanAndLoad。</summary>
    public string InstallFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("插件文件不存在。", sourcePath);
        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件必须是 .dll 或 .exe 文件。");
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            _ = AssemblyName.GetAssemblyName(sourcePath); // 只读元数据，拒绝非 .NET DLL
        else
        {
            using var input = File.OpenRead(sourcePath);
            if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                throw new InvalidDataException("EXE 文件没有有效的可执行文件头。");
        }

        var name = SafeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            throw new InvalidDataException("插件文件名无效。");
        var destinationDir = Path.Combine(_installedDir, name);
        var destination = Path.Combine(destinationDir, Path.GetFileName(sourcePath));
        if (File.Exists(destination)) throw new IOException("同名插件已安装；请先在插件目录中移除旧版。");
        Directory.CreateDirectory(destinationDir);
        File.Copy(sourcePath, destination);
        return destination;
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_installedDir);
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_dataDir);
    }

    /// <summary>
    /// 扫描 InstalledDirectory 下根目录及同名子目录中的 DLL / EXE，
    /// 按 StartPolicy 运行符合条件的插件。
    /// 单个插件加载失败/构造失败/Initialize 抛异常都只记录在它自己的 LoadedPluginInfo 里，
    /// 不会中断其它插件的加载——一个写坏了的插件不应该让其它插件、乃至整个"插件管理"
    /// 页面一起用不了。重复调用会先对上一轮已启用的插件调用 Shutdown、卸载旧的
    /// AssemblyLoadContext，再重新扫描一遍，用于"重新加载插件"这个开发命令。
    /// </summary>
    public void ScanAndLoad()
    {
        ShutdownAll();
        Plugins = Array.Empty<LoadedPluginInfo>();

        var state = LoadState();
        var results = new List<LoadedPluginInfo>();
        EnsureDirectories();

        var entries = Directory.EnumerateFiles(_installedDir)
            .Where(IsPluginFile)
            .Concat(Directory.EnumerateDirectories(_installedDir).SelectMany(dir =>
                Directory.EnumerateFiles(dir).Where(file => IsPluginFile(file) &&
                    Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in entries)
        {
            var key = StateKey(file);
            var saved = state.TryGetValue(key, out var existing) ? existing : new PluginState();
            results.Add(LoadOne(file, saved));
        }

        Plugins = results;
        SaveState();
    }

    private static bool IsPluginFile(string file)
        => Path.GetExtension(file) is { } ext &&
           (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase));

    private string StateKey(string path) => Path.GetRelativePath(_installedDir, path);

    private LoadedPluginInfo LoadOne(string filePath, PluginState state)
    {
        var fileName = Path.GetFileName(filePath);
        var executable = Path.GetExtension(filePath).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var shouldStart = state.Enabled && (StartPolicy == PluginLaunchMode.EachLaunch || !state.StartedOnce);
        try
        {
            if (executable)
            {
                Process? process = null;
                if (shouldStart)
                {
                    var psi = new ProcessStartInfo(filePath)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(filePath)!
                    };
                    psi.Environment["XCL2_PLUGIN_DATA_DIR"] = EnsurePluginDataDir("exe." + Path.GetFileNameWithoutExtension(filePath));
                    process = Process.Start(psi) ?? throw new InvalidOperationException("EXE 插件无法启动。");
                    state.StartedOnce = true;
                }
                return new LoadedPluginInfo { FilePath = filePath, IsExecutable = true, Enabled = state.Enabled,
                    RunningProcess = process, StartedOnce = state.StartedOnce };
            }

            // 已禁用或仅导入运行一次且已经运行过：不再装载 DLL，也不执行其构造函数。
            if (!shouldStart)
                return new LoadedPluginInfo { FilePath = filePath, Enabled = state.Enabled, StartedOnce = state.StartedOnce };

            var alc = new PluginLoadContext(filePath);
            _loadContexts.Add(alc);
            var asm = alc.LoadFromAssemblyPath(filePath);

            var pluginType = asm.GetTypes().FirstOrDefault(t =>
                typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract &&
                t.GetConstructor(Type.EmptyTypes) != null);

            if (pluginType == null)
                return new LoadedPluginInfo { FilePath = filePath, LoadError = "这个 dll 里没有找到任何实现 IPlugin 接口、且带无参构造函数的公共类。", Enabled = state.Enabled };

            var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
            if (string.IsNullOrWhiteSpace(plugin.Id)) throw new InvalidDataException("插件 Id 不能为空。");
            var ctx = new PluginContext(
                plugin.Id,
                new PluginConfigService(Path.Combine(_configDir, SafeFileName(plugin.Id) + ".json")),
                EnsurePluginDataDir(plugin.Id),
                line => _logSink($"[Plugin:{plugin.Id}] {line}"));
            plugin.Initialize(ctx);
            state.StartedOnce = true;
            return new LoadedPluginInfo { FilePath = filePath, Plugin = plugin, Enabled = state.Enabled,
                Context = ctx, StartedOnce = state.StartedOnce };
        }
        catch (Exception ex)
        {
            _logSink($"[PluginManager] 加载 {fileName} 失败：{ex.Message}");
            return new LoadedPluginInfo { FilePath = filePath, LoadError = ex.Message, Enabled = state.Enabled,
                IsExecutable = executable, StartedOnce = state.StartedOnce };
        }
    }

    private string EnsurePluginDataDir(string pluginId)
    {
        var dir = Path.Combine(_dataDir, SafeFileName(pluginId));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>把插件 Id 里可能出现的路径非法字符替换掉，用作配置/数据目录的文件名——
    /// 插件 Id 建议用"作者.插件名"这种纯 ASCII 短字符串，这里只是兜底，不强制校验格式。</summary>
    private static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(id.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
        return safe is "." or ".." ? "_" : safe;
    }

    /// <summary>切换某个插件的启用状态并立即持久化，但不会自动重新调用 Initialize/Shutdown——
    /// 调用方（PluginsPage）需要在切换之后自己调用一次 ScanAndLoad() 让改动生效，
    /// 这里只负责记账，避免把"改配置"和"重新加载"这两件事绑死。</summary>
    public void SetEnabled(string filePath, bool enabled)
    {
        var state = LoadState();
        var key = StateKey(filePath);
        if (!state.TryGetValue(key, out var entry)) state[key] = entry = new PluginState();
        entry.Enabled = enabled;
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Dictionary<string, PluginState> LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(_stateFilePath));
            var entries = new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                entries[entry.Name] = entry.Value.ValueKind switch
                {
                    JsonValueKind.True => new PluginState { Enabled = true },
                    JsonValueKind.False => new PluginState { Enabled = false },
                    JsonValueKind.Object => entry.Value.Deserialize<PluginState>() ?? new PluginState(),
                    _ => new PluginState()
                };
            }
            return entries;
        }
        catch { return new(); }
    }

    private void SaveState()
    {
        var state = Plugins.ToDictionary(p => StateKey(p.FilePath),
            p => new PluginState { Enabled = p.Enabled, StartedOnce = p.StartedOnce },
            StringComparer.OrdinalIgnoreCase);
        try { File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _logSink($"[PluginManager] 保存插件启用状态失败：{ex.Message}"); }
    }

    /// <summary>对所有当前已启用且加载成功的插件调用一次 Shutdown，并释放所有
    /// AssemblyLoadContext——用于"重新加载插件"和启动器整体退出两个场景。</summary>
    public void ShutdownAll()
    {
        foreach (var p in Plugins)
        {
            if (p is { Enabled: true, Plugin: not null, Context: not null })
            {
                try { p.Plugin.Shutdown(); }
                catch (Exception ex) { _logSink($"[Plugin:{p.Plugin.Id}] Shutdown 抛出异常：{ex.Message}"); }
            }
            if (p.RunningProcess is { } process)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logSink($"[PluginManager] 停止 {Path.GetFileName(p.FilePath)} 失败：{ex.Message}"); }
                finally { process.Dispose(); }
            }
        }
        foreach (var alc in _loadContexts)
        {
            try { alc.Unload(); } catch { /* 卸载失败不影响启动器继续运行，忽略 */ }
        }
        _loadContexts.Clear();
    }
}
