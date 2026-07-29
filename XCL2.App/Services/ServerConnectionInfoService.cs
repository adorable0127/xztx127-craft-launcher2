using System.IO;
using System.Net;
using System.Net.Sockets;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 解析"如何连接到这个服务器"要展示给用户的信息：局域网 IP + 端口。
///
/// 根因（对应"新增出来的服务器没有 IP 地址"这个问题）：ServerManagerPage 的卡片之前只展示
/// 加载器/MC版本/内存，完全没有读取 server.properties 里的 server-port，也没有取本机局域网 IP，
/// 用户创建完服务器之后不知道要用什么地址连进去，只能自己去翻 server.properties 或猜默认端口。
///
/// 端口来源：server.properties 的 server-port 字段是唯一权威来源——用户可能在创建后手动改过
/// 这个文件，所以每次展示前都重新读取，而不是只在创建时读一次缓存下来。文件不存在（核心还没
/// 下载完成/还没启动过一次生成默认配置）时回退到 Minecraft 服务端的默认值 25565。
///
/// IP 来源：取本机所有网卡里第一个非回环的 IPv4 地址，作为"局域网内其他设备可以用这个地址连接"
/// 的展示值。这不是公网 IP（公网访问需要额外的端口转发/DDNS，超出这个功能的范围，只提示局域网用法）。
/// </summary>
public static class ServerConnectionInfoService
{
    private const int DefaultServerPort = 25565;

    /// <summary>
    /// 从 server.properties 里读取 server-port，读不到（文件不存在/字段缺失/格式不合法）时
    /// 返回 Minecraft 服务端的默认端口 25565，保证任何时候都有一个可以展示给用户的值。
    /// </summary>
    public static int ReadServerPort(string serverDirectory)
    {
        try
        {
            var propsPath = Path.Combine(serverDirectory, "server.properties");
            if (!File.Exists(propsPath)) return DefaultServerPort;

            foreach (var line in File.ReadAllLines(propsPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                var idx = trimmed.IndexOf('=');
                if (idx <= 0) continue;

                var key = trimmed[..idx].Trim();
                if (!string.Equals(key, "server-port", StringComparison.OrdinalIgnoreCase)) continue;

                var value = trimmed[(idx + 1)..].Trim();
                if (int.TryParse(value, out var port) && port is > 0 and <= 65535)
                    return port;

                break; // 找到了 server-port 这一行但值不合法，不再继续找，直接回退默认端口
            }
        }
        catch
        {
            // 读取失败（权限问题/文件被占用等）不应该阻断整个服务器列表的渲染，回退默认端口
        }

        return DefaultServerPort;
    }

    /// <summary>
    /// 取本机第一个非回环 IPv4 地址，用于"局域网内其他设备怎么连接"的展示。
    /// 拿不到（比如没有网络适配器/都是回环）时返回 null，调用方应该展示"本机"或类似提示，
    /// 而不是硬编码一个可能连不通的地址。
    /// </summary>
    public static string? GetLocalLanIPv4()
    {
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    return address.ToString();
            }
        }
        catch
        {
            // 拿不到主机名/DNS 解析失败等：调用方按 null 处理，展示兜底文案
        }

        return null;
    }

    /// <summary>
    /// 组装完整的"连接地址"展示文本，例如 "192.168.1.5:25565"。局域网 IP 拿不到时退化为
    /// "本机:端口"，端口本身永远有值（server.properties 缺失时回退默认端口），保证不会展示空白。
    /// 同时把解析出的端口写回 instance.ServerPort（调用方负责后续持久化），
    /// 让实例列表刷新一次之后端口就跟 server.properties 保持同步。
    /// </summary>
    public static string Resolve(ServerInstance instance)
    {
        var port = ReadServerPort(instance.Directory);
        instance.ServerPort = port;

        var ip = GetLocalLanIPv4();
        return ip != null ? $"{ip}:{port}" : $"本机(localhost):{port}";
    }
}
