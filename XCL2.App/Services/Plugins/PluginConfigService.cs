using System.IO;
using System.Text.Json;

namespace XCL2.App.Services.Plugins;

/// <summary>
/// 单个插件的配置读写：本质是落盘在
/// %AppData%/XCL2/Plugins/config/{PluginId}.json 的一份 string-&gt;JsonElement 字典，
/// 插件用 Get&lt;T&gt;/Set 存取任意可 JSON 序列化的值，不需要自己定义配置文件格式、
/// 也不需要自己处理"文件不存在""JSON 损坏"这些边界情况——这里已经统一兜底成
/// "读不到就当空配置，不会抛异常炸掉整个插件加载流程"。
///
/// 每个插件拿到的实例只认自己那一份文件（由 PluginManager 按 Id 各自 new 一份，
/// 宿主按插件 Id 为正常使用提供独立文件；这种路径区分不是权限隔离，
/// 第三方插件进程仍可按当前 Windows 用户权限访问其它文件。
/// </summary>
public sealed class PluginConfigService
{
    private readonly string _filePath;
    private readonly Dictionary<string, JsonElement> _values;
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    internal PluginConfigService(string filePath)
    {
        _filePath = filePath;
        _values = Load(filePath);
    }

    private static Dictionary<string, JsonElement> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
        }
        catch
        {
            // 配置文件损坏/被手动改坏都不应该导致插件整体加载失败，退化成空配置，
            // 插件自己的 Get&lt;T&gt; 会按"没有这个 key"的路径走默认值。
            return new();
        }
    }

    /// <summary>读取一个配置项，key 不存在或者反序列化失败时返回 fallback，
    /// 不抛异常——插件不需要为"第一次运行、配置文件还没任何内容"这种正常情况
    /// 专门写 try/catch。</summary>
    public T Get<T>(string key, T fallback)
    {
        if (!_values.TryGetValue(key, out var element)) return fallback;
        try
        {
            return element.Deserialize<T>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>写入一个配置项并立即落盘。落盘是同步的、按整份文件重写，配置文件通常很小，
    /// 不需要为此做增量写入或者异步优化。</summary>
    public void Set<T>(string key, T value)
    {
        var existed = _values.TryGetValue(key, out var previous);
        _values[key] = JsonSerializer.SerializeToElement(value);
        try { Save(); }
        catch
        {
            if (existed) _values[key] = previous;
            else _values.Remove(key);
            throw;
        }
    }

    /// <summary>删除一个配置项并落盘；key 不存在时静默忽略。</summary>
    public void Remove(string key)
    {
        if (!_values.Remove(key, out var previous)) return;
        try { Save(); }
        catch { _values[key] = previous; throw; }
    }

    /// <summary>当前已保存的所有 key，供"插件管理"里给不提供图形配置界面的插件
    /// 生成一份只读的通用 JSON 预览用，插件自己一般用不上这个方法。</summary>
    public IReadOnlyDictionary<string, JsonElement> Snapshot() => _values;

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_values, SerializeOptions));
    }
}
