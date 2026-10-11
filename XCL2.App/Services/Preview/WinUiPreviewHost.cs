using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using XCL2.App.Services.Plugins;
using XCL2.App.Views;
using XCL2.Preview;

namespace XCL2.App.Services.Preview;

internal sealed class WinUiPreviewHost : IDisposable
{
    private readonly MainWindow _owner;
    private readonly Action _onClosed;
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private Process? _process;
    internal WinUiPreviewHost(MainWindow owner, Action onClosed)
    {
        _owner = owner; _onClosed = onClosed;
        var exe = Path.Combine(AppContext.BaseDirectory, "WinUI.Preview", "XCL2.WinUI.Preview.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到 WinUI 3 预览程序。请在 Windows 上重新构建 XCL2.App（默认会一并构建其 WinUI.Preview 子项目），或双击源码根目录 build.cmd 发布。请勿使用 SkipNativePreview=true，并分发生成的完整目录（包含 WinUI.Preview）。", exe);
        var name = "XCL2.Preview." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
        try { _process = Process.Start(start) ?? throw new IOException("无法启动 WinUI 3 预览。"); }
        catch { _pipe.Dispose(); _stop.Dispose(); throw; }
        _process.EnableRaisingEvents = true; _process.Exited += (_, _) => _stop.Cancel();
        _ = ServeAsync();
    }
    internal bool IsRunning => !_stop.IsCancellationRequested && _process is { HasExited: false };
    private async Task ServeAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            while (!_stop.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_stop.Token); if (line == null) break;
                object response;
                try
                {
                    if (line.Length > 32768) throw new IOException("请求过长。");
                    var request = JsonSerializer.Deserialize<PreviewRequest>(line) ?? throw new IOException("无效请求。");
                    var result = await HandleAsync(request); response = new { Ok = true, Data = result };
                }
                catch (Exception ex) { response = new { Ok = false, Error = ex.Message }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LauncherLogService.AppendLine("[WinUI Preview] " + ex.Message); }
        finally { _stop.Cancel(); if (!_owner.Dispatcher.HasShutdownStarted) _ = _owner.Dispatcher.BeginInvoke(_onClosed); }
    }
    private async Task<object> HandleAsync(PreviewRequest request)
    {
        if (request.Action == "state")
        {
            var input = await _owner.Dispatcher.InvokeAsync(() =>
            {
                var config = _owner.ConfigService.Config;
                return (Roots: config.Folders.Select(f => f.Path).ToArray(), config.SelectedFolderPath, config.SelectedVersionId,
                    config.EnablePreviewPluginApi, config.EnableWorldPluginApi, Ui: PluginPreviewService.Snapshot());
            });
            var instances = await Task.Run(() => input.Roots.SelectMany(root =>
            {
                try { return new FolderService().ScanVersions(root).Select(v => new PreviewInstance(v.Id, root, v.McVersion ?? "未知", v.ModLoader ?? "Vanilla", v.IsInstalled,
                    root.Equals(input.SelectedFolderPath, StringComparison.OrdinalIgnoreCase) && v.Id == input.SelectedVersionId)).ToArray(); }
                catch { return Array.Empty<PreviewInstance>(); }
            }).ToArray(), _stop.Token);
            return new PreviewSnapshot(instances, input.Ui.Cards, input.Ui.Appearance, input.EnablePreviewPluginApi, input.EnableWorldPluginApi, input.Roots);
        }
        return await _owner.Dispatcher.InvokeAsync<object>(() =>
        {
            switch (request.Action)
            {
                case "navigate":
                    var action = request.Page switch
                    {
                        "worlds" => (Action)_owner.NavigateToWorlds, "instances" => _owner.NavigateToVersions,
                        "settings" => _owner.NavigateToSettings, "downloads" => _owner.NavigateToDownloadCenter,
                        "api" => _owner.NavigateToApiDocumentation,
                        "ai" => _owner.NavigateToAiAssistant, "plugins" => () => OverlayDialogService.ShowModal(new PluginManagerDialog(_owner)),
                        "home" => _owner.NavigateToHome, _ => throw new ArgumentException("未知导航目标。")
                    };
                    _owner.Show(); _owner.Activate(); action(); return "已打开";
                case "launch":
                    if (!_owner.CanLaunchFromNativePreview) throw new InvalidOperationException("已有启动任务或按钮冷却中，请在启动器中查看进度。");
                    if (!_owner.ConfigService.Config.Folders.Any(f => f.Path.Equals(request.Root, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("游戏目录已移除。");
                    if (!new FolderService().ScanVersions(request.Root!).Any(v => v.Id == request.Version && v.IsInstalled)) throw new ArgumentException("实例已移除或未安装完成。");
                    _owner.ConfigService.Config.SelectedFolderPath = request.Root; _owner.ConfigService.Config.SelectedVersionId = request.Version;
                    _owner.ConfigService.Save(); _owner.Launch_Click(_owner, new RoutedEventArgs()); return "已提交启动请求，请查看启动器进度。";
                case "api-settings":
                    _owner.ConfigService.Config.EnablePreviewPluginApi = request.PreviewApiEnabled == true;
                    _owner.ConfigService.Config.EnableWorldPluginApi = request.WorldsApiEnabled == true;
                    _owner.ConfigService.Save(); return "API 设置已保存，插件仍需逐项授权。";
                default: throw new ArgumentException("未知预览 API。");
            }
        });
    }
    public void Dispose()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        _pipe.Dispose();
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        _process?.Dispose(); _process = null;
    }
}
