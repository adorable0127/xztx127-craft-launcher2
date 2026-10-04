using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record ServerPortAssignment(int GamePort, IReadOnlyCollection<int> Ports);

/// <summary>配置端口、操作系统占用和启动中进程的端口都参与避让。</summary>
public static class ServerPortService
{
    public const int DefaultPort = 25565;
    public static bool IsValid(int port) => port is >= 1 and <= 65535;

    public static int ReadPort(ServerInstance instance)
    {
        var fallback = instance.ServerPort is { } port && IsValid(port) ? port : DefaultPort;
        if (instance.CoreType == ServerCoreType.Velocity)
        {
            var path = Path.Combine(instance.Directory, "velocity.toml");
            if (File.Exists(path))
            {
                var match = Regex.Match(File.ReadAllText(path), "(?m)^\\s*bind\\s*=\\s*\"([^\"\\r\\n]+)\"");
                if (match.Success && TryReadAddressPort(match.Groups[1].Value, out var value)) return value;
            }
            return fallback;
        }
        if (instance.CoreType == ServerCoreType.Waterfall)
        {
            var path = Path.Combine(instance.Directory, "config.yml");
            if (File.Exists(path))
            {
                var match = Regex.Match(File.ReadAllText(path), @"(?m)^\s*(?:-\s*)?host:\s*([^\r\n#]+)");
                if (match.Success && TryReadAddressPort(match.Groups[1].Value, out var value)) return value;
            }
            return fallback;
        }
        return ReadPropertyPort(ServerPropertiesService.Load(instance.Directory), "server-port", fallback);
    }

    public static int FindAvailablePort(int preferred, ServerInstance instance,
        IEnumerable<ServerInstance> instances, IEnumerable<ServerProcessInfo> processes)
        => Resolve(preferred, CollectUnavailable(instance, instances, processes), instance.AutoAvoidPort);

    /// <summary>自动避让：找下一个可用端口；严格模式：端口不可用就抛出带原因的异常。</summary>
    private static int Resolve(int preferred, HashSet<int> occupied, bool autoAvoid)
    {
        if (autoAvoid) return FindAvailable(preferred, occupied);
        if (!IsValid(preferred)) throw new ArgumentOutOfRangeException(nameof(preferred), "端口必须在 1–65535 之间。");
        if (occupied.Contains(preferred) || !CanBind(preferred))
            throw new InvalidOperationException(
                $"端口 {preferred} 已被占用或与其他服务器冲突。请换一个端口，或在设置里开启“端口被占用时自动避让”。");
        return preferred;
    }

    public static ServerPortAssignment PrepareForStart(ServerInstance instance,
        IEnumerable<ServerInstance> instances, IEnumerable<ServerProcessInfo> processes)
    {
        var occupied = CollectUnavailable(instance, instances, processes);
        var props = ServerPropertiesService.Load(instance.Directory);
        var proxy = instance.CoreType is ServerCoreType.Velocity or ServerCoreType.Waterfall;
        var queryEnabled = !proxy && IsTrue(props, "enable-query");
        var rconEnabled = !proxy && IsTrue(props, "enable-rcon");
        var previousGame = ReadPort(instance);
        // 给本实例已有的附加端口留位，避免主端口占掉自己的 RCON。
        var previousQuery = ReadPropertyPort(props, "query.port", previousGame);
        var previousRcon = ReadPropertyPort(props, "rcon.port", 25575);
        var gameUnavailable = new HashSet<int>(occupied);
        if (rconEnabled) gameUnavailable.Add(previousRcon);
        if (queryEnabled && previousQuery != previousGame) gameUnavailable.Add(previousQuery);
        var proxyBindings = ReadProxyBindings(instance);
        foreach (var binding in proxyBindings.Skip(1)) gameUnavailable.Add(binding);
        var game = Resolve(previousGame, gameUnavailable, instance.AutoAvoidPort);
        occupied.Add(game);
        var ports = new HashSet<int> { game };
        var updates = new Dictionary<string, string?>();
        if (queryEnabled)
        {
            // Query 的 UDP 端口可与同一服务端的 TCP 游戏端口同号。
            var query = previousQuery == previousGame ? game : FindAvailable(previousQuery, occupied);
            updates["query.port"] = query.ToString(CultureInfo.InvariantCulture);
            occupied.Add(query);
            ports.Add(query);
        }
        if (rconEnabled)
        {
            var rcon = FindAvailable(previousRcon, occupied);
            updates["rcon.port"] = rcon.ToString(CultureInfo.InvariantCulture);
            ports.Add(rcon);
        }
        var listenerPorts = new List<int> { game };
        foreach (var previous in proxyBindings.Skip(1))
        {
            var available = FindAvailable(previous, occupied);
            occupied.Add(available);
            ports.Add(available);
            listenerPorts.Add(available);
        }
        WritePort(instance, game, updates, listenerPorts);
        return new ServerPortAssignment(game, ports.ToArray());
    }

    public static void WritePort(ServerInstance instance, int port, Dictionary<string, string?>? updates = null,
        IReadOnlyList<int>? listenerPorts = null)
    {
        if (!IsValid(port)) throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1–65535 之间。");
        Directory.CreateDirectory(instance.Directory);
        if (instance.CoreType == ServerCoreType.Velocity)
        {
            var path = Path.Combine(instance.Directory, "velocity.toml");
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var pattern = new Regex("(?m)^(?<prefix>\\s*bind\\s*=\\s*\")(?<address>[^\"\\r\\n]+)(?<suffix>\"[^\\r\\n]*)");
            text = pattern.IsMatch(text) ? pattern.Replace(text, m => m.Groups["prefix"].Value +
                ReplaceAddressPort(m.Groups["address"].Value, port) + m.Groups["suffix"].Value, 1)
                : "bind = \"0.0.0.0:" + port + "\"\n" + text;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        else if (instance.CoreType == ServerCoreType.Waterfall)
        {
            var path = Path.Combine(instance.Directory, "config.yml");
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var pattern = new Regex(@"(?m)^(?<prefix>\s*(?:-\s*)?host:\s*)(?<address>[^\r\n#]+)(?<suffix>[^\r\n]*)");
            if (pattern.IsMatch(text))
            {
                var index = 0;
                text = pattern.Replace(text, m =>
                {
                    var currentIndex = index++;
                    if (listenerPorts == null && currentIndex > 0) return m.Value;
                    var listenerPort = listenerPorts != null && currentIndex < listenerPorts.Count
                        ? listenerPorts[currentIndex] : port;
                    return m.Groups["prefix"].Value + ReplaceAddressPort(m.Groups["address"].Value, listenerPort) + m.Groups["suffix"].Value;
                });
            }
            else text = "listeners:\n- host: 0.0.0.0:" + port + "\n" + text;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        else
        {
            updates ??= new Dictionary<string, string?>();
            updates["server-port"] = port.ToString(CultureInfo.InvariantCulture);
            ServerPropertiesService.Save(instance.Directory, updates);
        }
        instance.ServerPort = port;
    }

    private static HashSet<int> CollectUnavailable(ServerInstance instance,
        IEnumerable<ServerInstance> instances, IEnumerable<ServerProcessInfo> processes)
    {
        var ports = new HashSet<int>();
        foreach (var other in instances)
        {
            if (other.Id == instance.Id) continue;
            if (SameDirectory(other.Directory, instance.Directory))
                throw new InvalidOperationException($"服务器「{other.DisplayName}」使用同一目录，请为每个服务器选择独立目录。");
            ports.Add(ReadPort(other));
            ports.UnionWith(ReadProxyBindings(other));
            var props = ServerPropertiesService.Load(other.Directory);
            if (other.CoreType is not (ServerCoreType.Velocity or ServerCoreType.Waterfall))
            {
                if (IsTrue(props, "enable-query")) ports.Add(ReadPropertyPort(props, "query.port", ReadPort(other)));
                if (IsTrue(props, "enable-rcon")) ports.Add(ReadPropertyPort(props, "rcon.port", 25575));
            }
        }
        foreach (var process in processes.Where(p => !p.HasExited)) ports.UnionWith(process.ListeningPorts);
        var system = IPGlobalProperties.GetIPGlobalProperties();
        ports.UnionWith(system.GetActiveTcpListeners().Select(p => p.Port));
        ports.UnionWith(system.GetActiveUdpListeners().Select(p => p.Port));
        return ports;
    }

    private static int FindAvailable(int preferred, HashSet<int> occupied)
    {
        if (!IsValid(preferred)) throw new ArgumentOutOfRangeException(nameof(preferred), "端口必须在 1–65535 之间。");
        for (var offset = 0; offset < 65535; offset++)
        {
            var candidate = (preferred - 1 + offset) % 65535 + 1;
            if (!occupied.Contains(candidate) && CanBind(candidate)) return candidate;
        }
        throw new InvalidOperationException("没有可用的服务器端口，请停止不需要的服务器后再试。");
    }

    private static bool CanBind(int port)
    {
        var sockets = new List<Socket>();
        try
        {
            foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
            {
                if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
                foreach (var protocol in new[] { ProtocolType.Tcp, ProtocolType.Udp })
                {
                    var socket = new Socket(family, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol)
                        { ExclusiveAddressUse = true };
                    sockets.Add(socket);
                    if (family == AddressFamily.InterNetworkV6) socket.DualMode = false;
                    socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, port));
                }
            }
            return true;
        }
        catch (SocketException) { return false; }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    private static bool SameDirectory(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static List<int> ReadProxyBindings(ServerInstance instance)
    {
        if (instance.CoreType != ServerCoreType.Waterfall) return new List<int>();
        var path = Path.Combine(instance.Directory, "config.yml");
        if (!File.Exists(path)) return new List<int>();
        var ports = new List<int>();
        foreach (Match match in Regex.Matches(File.ReadAllText(path), @"(?m)^\s*(?:-\s*)?host:\s*([^\r\n#]+)"))
            if (TryReadAddressPort(match.Groups[1].Value, out var port)) ports.Add(port);
        return ports;
    }
    private static bool IsTrue(Dictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    private static int ReadPropertyPort(Dictionary<string, string> props, string key, int fallback) =>
        props.TryGetValue(key, out var value) && int.TryParse(value, out var port) && IsValid(port) ? port : fallback;
    private static bool TryReadAddressPort(string address, out int port) =>
        int.TryParse(address.Trim().Trim('"', '\'').Split(':')[^1], out port) && IsValid(port);
    private static string ReplaceAddressPort(string address, int port)
    {
        var value = address.Trim();
        var quote = value.Length > 0 && value[0] is '"' or '\'' ? value[0].ToString() : "";
        value = value.Trim('"', '\'');
        var colon = value.LastIndexOf(':');
        return quote + (colon >= 0 ? value[..(colon + 1)] : "0.0.0.0:") + port + quote;
    }
}
