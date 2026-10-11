using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using XCL2.Preview;
namespace XCL2.WinUI.Preview;
internal sealed class PreviewRequestException(string message) : Exception(message) { }
internal sealed class PreviewClient : IDisposable
{
    private readonly string? _name;
    private readonly SemaphoreSlim _gate = new(1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly CancellationTokenSource _stop = new();
    internal PreviewClient(string? name) { _name = name; }
    internal async Task<T> SendAsync<T>(PreviewRequest request)
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            if (_pipe == null)
            {
                if (string.IsNullOrWhiteSpace(_name)) throw new IOException("请从 XCL2 首页的“新界面预览”打开此程序。");
                _pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await _pipe.ConnectAsync(deadline.Token);
                _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            }
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            var line = await _reader!.ReadLineAsync(deadline.Token) ?? throw new IOException("启动器连接已关闭。");
            using var result = JsonDocument.Parse(line);
            if (!result.RootElement.GetProperty("Ok").GetBoolean()) throw new PreviewRequestException(result.RootElement.GetProperty("Error").GetString() ?? "预览请求失败。");
            return result.RootElement.GetProperty("Data").Deserialize<T>()!;
        }
        catch (Exception ex) when (ex is not PreviewRequestException) { _reader?.Dispose(); _writer?.Dispose(); _pipe?.Dispose(); _pipe = null; throw; }
        finally { _gate.Release(); }
    }
    public void Dispose() { _stop.Cancel(); _pipe?.Dispose(); }
}
