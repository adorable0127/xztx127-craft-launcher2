using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「服务器测速」：实现 Minecraft 现代版(1.7+) Server List Ping 协议——
/// 就是多人游戏列表里每个服务器条目显示的那行 MOTD、在线人数、延迟数字，背后走的
/// 就是这套协议。这里独立实现一份客户端，不依赖任何第三方库：
///
///   1. 建立 TCP 连接
///   2. 发送 Handshake 包(带上目标地址/端口，next_state=1 表示接下来要 Status 请求)
///   3. 发送空的 Status Request 包
///   4. 服务器回应一个 JSON 字符串(含 version/players/description 等字段)——这就是
///      "服务器状态"，MOTD、人数、版本号都在这里面
///   5. 再发一个带时间戳的 Ping 包，服务器原样回一个 Pong，往返耗时就是延迟(ms)
///
/// 所有数据包长度都用 VarInt 编码(每字节最高位表示"后面还有没有字节")，这是这套协议
/// 唯一稍微不直观的地方，其余就是纯粹的 TCP 收发。
/// </summary>
public static class ServerPingService
{
    public record PingResult(
        bool Success,
        string? MotdPlain,
        string? VersionName,
        int? OnlinePlayers,
        int? MaxPlayers,
        long? LatencyMs,
        string? ErrorMessage);

    private const int ConnectTimeoutMs = 5000;
    private const int ReadTimeoutMs = 5000;

    public static async Task<PingResult> PingAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(ConnectTimeoutMs, ct);
            var completed = await Task.WhenAny(connectTask, timeoutTask);
            if (completed == timeoutTask)
                return Fail("连接超时，请确认地址/端口正确，且对方服务器在线。");
            await connectTask; // 把连接阶段真正的异常(拒绝连接/DNS 解析失败等)重新抛出

            using var stream = client.GetStream();
            stream.ReadTimeout = ReadTimeoutMs;
            stream.WriteTimeout = ReadTimeoutMs;

            // 1) Handshake：packet id 0x00，字段依次是 protocol version(用 -1 表示"不关心，
            //    只是查状态")、目标 host、目标 port、next_state=1(status)
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, -1);
            WriteString(handshake, host);
            WriteUShort(handshake, (ushort)port);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake, ct);

            // 2) Status Request：packet id 0x00，没有其它字段
            var statusRequest = new MemoryStream();
            WriteVarInt(statusRequest, 0x00);
            await WritePacketAsync(stream, statusRequest, ct);

            // 3) 读取 Status Response，取出里面的 JSON 字符串
            var (_, responsePayload) = await ReadPacketAsync(stream, ct);
            using var respReader = new BinaryReader(new MemoryStream(responsePayload));
            var jsonStr = ReadString(respReader);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            string? versionName = null;
            if (root.TryGetProperty("version", out var versionEl) &&
                versionEl.TryGetProperty("name", out var nameEl))
                versionName = nameEl.GetString();

            int? online = null, max = null;
            if (root.TryGetProperty("players", out var playersEl))
            {
                if (playersEl.TryGetProperty("online", out var onlineEl)) online = onlineEl.GetInt32();
                if (playersEl.TryGetProperty("max", out var maxEl)) max = maxEl.GetInt32();
            }

            string? motd = null;
            if (root.TryGetProperty("description", out var descEl))
            {
                // description 可能是纯字符串，也可能是 {"text": "...", "extra": [...]} 这种
                // 富文本对象——这里只提取纯文本拼起来给用户看，不需要还原颜色/格式代码，
                // 那属于另一个"MOTD 颜色代码编辑器"工具的职责范围。
                motd = ExtractPlainText(descEl);
            }

            // 4) Ping/Pong 测延迟：发送带当前时间戳的 Ping 包，服务器原样回传，
            //    往返耗时(ms)就是给玩家看的那个延迟数字。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pingPacket = new MemoryStream();
            WriteVarInt(pingPacket, 0x01);
            WriteLong(pingPacket, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await WritePacketAsync(stream, pingPacket, ct);
            await ReadPacketAsync(stream, ct); // Pong，内容不需要，只关心耗时
            sw.Stop();

            return new PingResult(true, motd, versionName, online, max, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (SocketException sockEx)
        {
            return Fail(sockEx.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝，服务器可能已关闭或端口不对。",
                SocketError.HostNotFound => "找不到这个地址，请检查域名/IP 有没有写错。",
                SocketError.TimedOut => "连接超时，请确认地址/端口正确，且对方服务器在线。",
                _ => $"网络错误：{sockEx.Message}"
            });
        }
        catch (Exception ex)
        {
            return Fail($"测速失败：{ex.Message}");
        }
    }

    private static PingResult Fail(string message) => new(false, null, null, null, null, null, message);

    /// <summary>把 description 字段(可能是字符串/对象/数组嵌套的富文本结构)递归拍平成纯文本。</summary>
    private static string ExtractPlainText(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString() ?? "";
            case JsonValueKind.Object:
                var sb = new StringBuilder();
                if (el.TryGetProperty("text", out var textEl))
                    sb.Append(textEl.GetString());
                if (el.TryGetProperty("extra", out var extraEl) && extraEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in extraEl.EnumerateArray())
                        sb.Append(ExtractPlainText(part));
                }
                return sb.ToString();
            default:
                return "";
        }
    }

    // ---- 协议底层编解码：VarInt / 字符串 / 数据包封装，都是 SLP 协议规定的标准格式 ----

    private static void WriteVarInt(Stream s, int value)
    {
        var v = unchecked((uint)value);
        while (true)
        {
            if ((v & ~0x7Fu) == 0) { s.WriteByte((byte)v); return; }
            s.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUShort(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteLong(Stream s, long value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        s.Write(bytes, 0, bytes.Length);
    }

    private static async Task WritePacketAsync(NetworkStream stream, MemoryStream payload, CancellationToken ct)
    {
        var lengthPrefixed = new MemoryStream();
        WriteVarInt(lengthPrefixed, (int)payload.Length);
        payload.Position = 0;
        payload.CopyTo(lengthPrefixed);
        var buffer = lengthPrefixed.ToArray();
        await stream.WriteAsync(buffer, ct);
    }

    private static async Task<(int packetId, byte[] payload)> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct);
            if (n == 0) throw new IOException("连接被对方提前关闭，读取到的数据不完整。");
            read += n;
        }
        using var ms = new MemoryStream(buffer);
        var packetId = ReadVarIntSync(ms);
        var payload = new byte[buffer.Length - ms.Position];
        Array.Copy(buffer, (int)ms.Position, payload, 0, payload.Length);
        return (packetId, payload);
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            var b = new byte[1];
            var n = await stream.ReadAsync(b.AsMemory(0, 1), ct);
            if (n == 0) throw new IOException("连接被对方提前关闭。");
            result |= (b[0] & 0x7F) << shift;
            if ((b[0] & 0x80) == 0) break;
            shift += 7;
            if (shift >= 35) throw new IOException("VarInt 数据格式异常(超长)。");
        }
        return result;
    }

    private static int ReadVarIntSync(Stream s)
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            var b = s.ReadByte();
            if (b == -1) throw new IOException("连接被对方提前关闭。");
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 35) throw new IOException("VarInt 数据格式异常(超长)。");
        }
        return result;
    }

    private static string ReadString(BinaryReader reader)
    {
        var ms = (MemoryStream)reader.BaseStream;
        var length = ReadVarIntSync(ms);
        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }
}
