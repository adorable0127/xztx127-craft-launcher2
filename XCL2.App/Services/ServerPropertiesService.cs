using System.IO;
using System.Text;

namespace XCL2.App.Services;

/// <summary>
/// server.properties 的读写：这是标准 Java 版 properties 文本格式（key=value，一行一条，
/// # 开头是注释），不是 JSON/XML，没有现成的 .NET 内置解析器，这里手写一个最小实现——
/// 只按行 Split('=', 2) 处理，不支持 properties 格式里的转义序列（\: \= \\uXXXX 等），
/// Minecraft 官方生成的 server.properties 实际内容里没有用到这些转义，够用。
///
/// 设计上是"读取全部 -> 按 key 覆盖用户改过的字段 -> 写回全部"，而不是只写用户改过的几行：
/// 这样可以完整保留文件里 XCL2 不认识的其它 key（比如某些 Mod/插件会自己往这个文件里加
/// 私有配置项），不会因为编辑几个字段就把用户手动加过的其它配置冲掉。
/// </summary>
public static class ServerPropertiesService
{
    public const string FileName = "server.properties";

    public static Dictionary<string, string> Load(string serverDir)
    {
        var path = Path.Combine(serverDir, FileName);
        var result = new Dictionary<string, string>();
        if (!File.Exists(path)) return result;

        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var idx = line.IndexOf('=');
            if (idx < 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// 用 updates 里的键值覆盖已有文件内容后整体写回。updates 里某个 key 对应 null 表示
    /// "不改这一项"（用于"游戏模式/难度分两套字段兼容不同 MC 版本"这种场景——UI 上只显示
    /// 当前版本适用的那一套，另一套字段维持文件里原样不动，不强行清空）。
    /// 文件不存在时会新建一个只包含 updates 内容的最小文件（覆盖安装刚下载完、还没启动过一次
    /// 生成默认 server.properties 时，允许用户提前把想要的设置存进去，服务器首次启动会
    /// 沿用这个文件而不是覆盖它——这是 Minecraft 服务端本身的行为，不是本方法做的）。
    /// </summary>
    public static void Save(string serverDir, Dictionary<string, string?> updates)
    {
        Directory.CreateDirectory(serverDir);
        var path = Path.Combine(serverDir, FileName);

        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
        var remaining = new Dictionary<string, string?>(updates);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
            var idx = trimmed.IndexOf('=');
            if (idx < 0) continue;
            var key = trimmed[..idx].Trim();
            if (remaining.TryGetValue(key, out var newValue))
            {
                if (newValue != null) lines[i] = $"{key}={newValue}";
                remaining.Remove(key);
            }
        }

        // updates 里剩下没在原文件出现过的 key（新字段/文件本来不存在），追加到末尾。
        foreach (var kv in remaining)
        {
            if (kv.Value == null) continue;
            lines.Add($"{kv.Key}={kv.Value}");
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }
}
