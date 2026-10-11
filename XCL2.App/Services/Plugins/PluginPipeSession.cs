using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32.SafeHandles;

namespace XCL2.App.Services.Plugins;

/// <summary>协议 1：4 字节小端长度 + UTF-8 JSON；仅当前 Windows 用户与受托管 EXE PID。</summary>
internal sealed class PluginPipeSession : IDisposable
{
    internal string PipeName { get; } = "XCL2.plugin." + Guid.NewGuid().ToString("N");
    internal string SessionToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly PluginContext _context;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly SemaphoreSlim _requests = new(8, 8);
    private readonly TaskCompletionSource<int> _processId = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginInstallWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginLoginBroker> _brokers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeRequestIds = new(StringComparer.Ordinal);
    private readonly Action _disconnected;
    private int _disposed;
    private long _counter;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    internal PluginPipeSession(PluginContext context, Action disconnected)
    {
        _context = context; _disconnected = disconnected;
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _ = RunAsync();
    }
    internal void SetProcess(int pid) { _processId.TrySetResult(pid); }
    private async Task RunAsync()
    {
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); connect.CancelAfter(TimeSpan.FromSeconds(30));
            await _pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            var expectedPid = await _processId.Task.WaitAsync(connect.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var actualPid) || actualPid != expectedPid)
                throw new PluginOperationException("InvalidPeer", "插件通信端点身份不匹配。");
            using var hello = await ReadAsync(connect.Token).ConfigureAwait(false);
            var first = hello.RootElement;
            if (first.GetProperty("protocol").GetInt32() != 1 || first.GetProperty("kind").GetString() != "request" ||
                first.GetProperty("method").GetString() != "hello" || !Authenticate(first.GetProperty("params").GetProperty("token").GetString()))
                throw new PluginOperationException("HandshakeRejected", "插件通信握手失败。");
            await WriteAsync(new { protocol = 1, kind = "response", id = first.GetProperty("id").GetString(), result = new { apiVersion = _context.ApiVersion.ToString(), pluginId = _context.PluginId } }, connect.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                using var document = await ReadAsync(_stop.Token).ConfigureAwait(false); var message = document.RootElement;
                if (message.GetProperty("protocol").GetInt32() != 1) throw new PluginOperationException("ProtocolMismatch", "插件协议版本不兼容。");
                var kind = message.GetProperty("kind").GetString();
                if (kind == "response")
                {
                    var responseId = message.GetProperty("id").GetString() ?? "";
                    if (_pending.TryRemove(responseId, out var completion))
                    {
                        if (message.TryGetProperty("error", out _)) completion.TrySetException(new PluginOperationException("RemoteCallbackFailed", "EXE 回调失败。"));
                        else completion.TrySetResult(message.GetProperty("result").Clone());
                    }
                }
                else if (kind == "request")
                {
                    var id = message.GetProperty("id").GetString() ?? "";
                    if (id.Length == 0 || id.Length > 128 || !_activeRequestIds.TryAdd(id, 0))
                        throw new PluginOperationException("InvalidRequestId", "请求 Id 为空、过长或重复。");
                    if (!await _requests.WaitAsync(0, _stop.Token).ConfigureAwait(false))
                    { _activeRequestIds.TryRemove(id, out _); await ErrorAsync(id, "Busy", "同时请求超过 8 个。").ConfigureAwait(false); continue; }
                    var copy = message.Clone(); _ = HandleAsync(id, copy);
                }
                else throw new PluginOperationException("InvalidMessage", "不支持的插件消息类型。");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _context.Log("PipeDisconnected: " + ex.GetType().Name); }
        finally
        {
            if (!IsDisposed) { Dispose(); _disconnected(); }
        }
    }
    private bool Authenticate(string? token) => token != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(SessionToken), Encoding.UTF8.GetBytes(token));
    private async Task<JsonDocument> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4]; await _pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false); var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > 1048576) throw new PluginOperationException("FrameTooLarge", "插件通信帧超过 1 MiB。");
        var data = new byte[length]; await _pipe.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
    }
    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > 1048576) throw new PluginOperationException("FrameTooLarge", "插件通信帧超过 1 MiB。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try { await _pipe.WriteAsync(header, ct).ConfigureAwait(false); await _pipe.WriteAsync(bytes, ct).ConfigureAwait(false); await _pipe.FlushAsync(ct).ConfigureAwait(false); }
        finally { _writes.Release(); }
    }
    private Task ErrorAsync(string id, string code, string message) => WriteAsync(new { protocol = 1, kind = "response", id, error = new { code, message } }, _stop.Token);
    private async Task HandleAsync(string id, JsonElement message)
    {
        try
        {
            _context.EnsureActive(); var method = message.GetProperty("method").GetString() ?? "";
            var arguments = message.TryGetProperty("params", out var supplied) ? supplied : JsonSerializer.SerializeToElement(new { });
            var result = await DispatchAsync(method, arguments, _stop.Token).ConfigureAwait(false);
            await WriteAsync(new { protocol = 1, kind = "response", id, result }, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户取消一个业务确认不应让 EXE 永久等待该请求的响应。
            if (!_stop.IsCancellationRequested)
            {
                try { await ErrorAsync(id, "Canceled", "操作已取消。").ConfigureAwait(false); }
                catch (Exception ex) { _context.Log("PipeWriteFailed: " + ex.GetType().Name); }
            }
        }
        catch (Exception ex)
        {
            try { await ErrorAsync(id, ex is PluginOperationException operation ? operation.Code : "RequestFailed", ex is PluginOperationException safe ? safe.SafeMessage : "插件请求失败。").ConfigureAwait(false); }
            catch (Exception writeFailure) { _context.Log("PipeWriteFailed: " + writeFailure.GetType().Name); }
        }
        finally { _activeRequestIds.TryRemove(id, out _); _requests.Release(); }
    }
    private static string String(JsonElement args, string name) => args.GetProperty(name).GetString() ?? throw new ArgumentException("字段不能为空：" + name);
    private string Keep(IDisposable registration)
    { var id = Guid.NewGuid().ToString("N"); _registrations[id] = registration; return id; }
    private async Task<object?> DispatchAsync(string method, JsonElement args, CancellationToken ct)
    {
        if (method.StartsWith("workbench.", StringComparison.Ordinal))
            return await PluginWorkbenchEndpoints.InvokeAsync(_context, method, args, ct).ConfigureAwait(false);
        switch (method)
        {
            case "folders.list": return await _context.Management.GetFoldersAsync(ct).ConfigureAwait(false);
            case "instances.directories": return await _context.Management.GetInstanceDirectoriesAsync(String(args, "instanceId"), ct).ConfigureAwait(false);
            case "instances.setHidden": return await _context.Management.SetInstanceHiddenAsync(String(args, "instanceId"), args.GetProperty("hidden").GetBoolean(), ct).ConfigureAwait(false);
            case "instances.rename": return await _context.Management.RenameInstanceAsync(String(args, "instanceId"), String(args, "newName"), ct).ConfigureAwait(false);
            case "instances.setIsolation": return await _context.Management.SetInstanceIsolationAsync(String(args, "instanceId"), args.TryGetProperty("isolated", out var isolated) && isolated.ValueKind != JsonValueKind.Null ? isolated.GetBoolean() : null, ct).ConfigureAwait(false);
            case "instances.repair": return await _context.Management.RepairInstanceAsync(String(args, "instanceId"), ct).ConfigureAwait(false);
            case "mods.setEnabled": return await _context.Management.SetModEnabledAsync(String(args, "instanceId"), String(args, "fileName"), args.GetProperty("enabled").GetBoolean(), ct).ConfigureAwait(false);
            case "java.list": return await _context.Management.GetJavaRuntimesAsync(ct).ConfigureAwait(false);
            case "java.select": return await _context.Management.SelectJavaAsync(args.TryGetProperty("javaId", out var javaId) ? javaId.GetString() : null, args.TryGetProperty("instanceId", out var javaInstance) ? javaInstance.GetString() : null, ct).ConfigureAwait(false);
            case "downloads.get": return await _context.Management.GetDownloadAsync(args.GetProperty("downloadId").GetGuid(), ct).ConfigureAwait(false);
            case "downloads.pause": return await _context.Management.PauseDownloadAsync(args.GetProperty("downloadId").GetGuid(), ct).ConfigureAwait(false);
            case "downloads.resume": return await _context.Management.ResumeDownloadAsync(args.GetProperty("downloadId").GetGuid(), ct).ConfigureAwait(false);
            case "downloads.cancel": return await _context.Management.CancelDownloadAsync(args.GetProperty("downloadId").GetGuid(), ct).ConfigureAwait(false);
            case "downloads.clearFinished": return await _context.Management.ClearFinishedDownloadsAsync(ct).ConfigureAwait(false);
            case "resources.sources": return await _context.Resources.GetSourcesAsync(ct).ConfigureAwait(false);
            case "resources.search": return await _context.Resources.SearchAsync(args.GetProperty("query").Deserialize<PluginResourceQuery>(Json) ?? throw new ArgumentException("query"), ct).ConfigureAwait(false);
            case "resources.files": return await _context.Resources.GetFilesAsync(args.GetProperty("query").Deserialize<PluginResourceFileQuery>(Json) ?? throw new ArgumentException("query"), ct).ConfigureAwait(false);
            case "resources.install": return await _context.Resources.InstallAsync(args.GetProperty("request").Deserialize<PluginResourceInstallRequest>(Json) ?? throw new ArgumentException("request"), ct).ConfigureAwait(false);
            case "gameVersions.list": return await _context.Resources.GetGameVersionsAsync(ct).ConfigureAwait(false);
            case "gameVersions.install": return await _context.Resources.InstallGameVersionAsync(String(args, "folderId"), String(args, "versionId"), ct).ConfigureAwait(false);
            case "launcher.pages": return await _context.Control.GetPagesAsync(ct).ConfigureAwait(false);
            case "launcher.navigate": return await _context.Control.NavigateAsync(String(args, "pageId"), ct).ConfigureAwait(false);
            case "launcher.snapshot": return await _context.Control.GetSnapshotAsync(ct).ConfigureAwait(false);
            case "launcher.selectAccount": return await _context.Control.SelectAccountAsync(String(args, "accountId"), ct).ConfigureAwait(false);
            case "launcher.selectInstance": return await _context.Control.SelectInstanceAsync(String(args, "instanceId"), ct).ConfigureAwait(false);
            case "launcher.launch": return await _context.Control.LaunchSelectedAsync(ct).ConfigureAwait(false);
            case "launcher.cancelLaunch": return await _context.Control.CancelLaunchAsync(ct).ConfigureAwait(false);
            case "window.snapshot": return await _context.Control.GetWindowAsync(ct).ConfigureAwait(false);
            case "window.update": return await _context.Control.UpdateWindowAsync(args.GetProperty("patch").Deserialize<PluginWindowPatch>(Json) ?? throw new ArgumentException("patch"), ct).ConfigureAwait(false);
            case "window.setState": return await _context.Control.SetWindowStateAsync(args.GetProperty("action").Deserialize<PluginWindowAction>(Json), ct).ConfigureAwait(false);
            case "appearance.snapshot": return await _context.Control.GetAppearanceAsync(ct).ConfigureAwait(false);
            case "settings.update": return await _context.Control.UpdateSettingsAsync(args.GetProperty("patch").Deserialize<PluginSettingsPatch>(Json) ?? throw new ArgumentException("patch"), ct).ConfigureAwait(false);
            case "worlds.list": return await _context.Worlds.GetSnapshotAsync(ct).ConfigureAwait(false);
            case "worlds.get": return await _context.Worlds.GetAsync(String(args, "path"), ct).ConfigureAwait(false);
            case "worlds.schema": return _context.Worlds.GetEditSchema();
            case "worlds.copy": return await _context.Worlds.GenerateCopyAsync(String(args, "path"), String(args, "destinationPath"), args.GetProperty("edit").Deserialize<XCL2.App.Services.Worlds.WorldEdit>(Json) ?? throw new ArgumentException("edit"), ct).ConfigureAwait(false);
            case "worlds.update": return await _context.Worlds.UpdateAsync(String(args, "path"), args.GetProperty("edit").Deserialize<XCL2.App.Services.Worlds.WorldEdit>(Json) ?? throw new ArgumentException("edit"), ct).ConfigureAwait(false);
            case "preview.registerCard": return new { registrationId = Keep(_context.Preview.RegisterCard(String(args, "id"), String(args, "title"), String(args, "description"), String(args, "destination"), String(args, "accent"))) };
            case "preview.updateCard": _context.Preview.UpdateCard(String(args, "id"), String(args, "title"), String(args, "description"), String(args, "accent")); return true;
            case "preview.setAppearance": return new { registrationId = Keep(_context.Preview.SetAppearance(String(args, "title"), String(args, "accent"), args.TryGetProperty("compact", out var compact) && compact.GetBoolean())) };
            case "permissions.snapshot": return _context.Permissions.GetSnapshot();
            case "accounts.list": return await _context.Launcher.Accounts.GetSnapshotAsync(ct).ConfigureAwait(false);
            case "accounts.selected": return await _context.Launcher.Accounts.GetSelectedAsync(ct).ConfigureAwait(false);
            case "instances.list": return await _context.Launcher.Instances.GetSnapshotAsync(args.TryGetProperty("query", out var query) ? query.Deserialize<PluginInstanceQuery>(Json) : null, ct).ConfigureAwait(false);
            case "instances.selected": return await _context.Launcher.Instances.GetSelectedAsync(ct).ConfigureAwait(false);
            case "instances.details": return await _context.Launcher.Instances.GetDetailsAsync(String(args, "instanceId"), ct).ConfigureAwait(false);
            case "mods.list": return await _context.Launcher.Mods.GetSnapshotAsync(String(args, "instanceId"), ct).ConfigureAwait(false);
            case "downloads.list": return await _context.Launcher.Downloads.GetSnapshotAsync(ct).ConfigureAwait(false);
            case "settings.snapshot": return await _context.Launcher.Settings.GetSnapshotAsync(ct).ConfigureAwait(false);
            case "environment.snapshot": return await _context.Launcher.Environment.GetSnapshotAsync(ct).ConfigureAwait(false);
            case "config.get": return _context.Config.Snapshot();
            case "config.set": _context.Config.Set(String(args, "key"), args.GetProperty("value").Clone()); return true;
            case "config.remove": _context.Config.Remove(String(args, "key")); return true;
            case "log": _context.Log(String(args, "message")); return true;
            case "network.getText": return await _context.Network.GetTextAsync(String(args, "url"), ct).ConfigureAwait(false);
            case "registrations.remove": return _registrations.TryRemove(String(args, "registrationId"), out var handle) && Remove(handle);
            case "events.subscribe":
            {
                var type = typeof(PluginNotification).Assembly.GetTypes().FirstOrDefault(t => t.Name == String(args, "event") && t.IsSealed && typeof(PluginNotification).IsAssignableFrom(t))
                    ?? throw new PluginOperationException("UnknownEvent", "未知事件。");
                _ = PluginEventService.PermissionFor(type);
                var id = Guid.NewGuid().ToString("N");
                var subscription = (IDisposable)typeof(PluginPipeSession).GetMethod(nameof(SubscribeEvent), BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(type).Invoke(this, new object[] { id })!;
                _registrations[id] = subscription; return new { registrationId = id };
            }
            case "events.interceptLaunch": return new { registrationId = Keep(_context.Events.InterceptLaunch((plan, token) => RemoteAsync<PluginInterception<PluginLaunchPlan>>(String(args, "callbackId"), plan, token))) };
            case "events.interceptDownload": return new { registrationId = Keep(_context.Events.InterceptDownload((plan, token) => RemoteAsync<PluginInterception<PluginDownloadPlan>>(String(args, "callbackId"), plan, token))) };
            case "events.interceptInstanceCreation": return new { registrationId = Keep(_context.Events.InterceptInstanceCreation((plan, token) => RemoteAsync<PluginInterception<PluginInstanceCreationPlan>>(String(args, "callbackId"), plan, token))) };
            case "messages.peers": return _context.Messages.GetPeers();
            case "messages.send": return await _context.Messages.SendAsync(String(args, "targetPluginId"), String(args, "channel"), args.GetProperty("payload"), ct).ConfigureAwait(false);
            case "messages.subscribe":
            {
                var subscriptionId = Guid.NewGuid().ToString("N");
                var subscription = _context.Messages.Subscribe(String(args, "channel"),
                    (notification, token) => SendEventAsync(subscriptionId, notification, token));
                _registrations[subscriptionId] = subscription;
                return new { registrationId = subscriptionId };
            }
            case "ui.showMessage": return await _context.Ui.ShowMessageAsync(String(args, "title"), String(args, "message")).ConfigureAwait(false);
            case "ui.confirm": return await _context.Ui.ConfirmAsync(String(args, "title"), String(args, "message")).ConfigureAwait(false);
            case "ui.dialogTarget": return _context.Ui.GetDialogTarget(String(args, "id"));
            case "ui.showDialog": return await _context.Ui.ShowDialogAsync(String(args, "id"),
                args.TryGetProperty("parameter", out var parameter) ? parameter.Clone() : null).ConfigureAwait(false);
            case "ui.subscribeDialogs":
            {
                var subscriptionId = Guid.NewGuid().ToString("N");
                var subscription = _context.Ui.SubscribeDialogs(notification => SendEventFireAndForget(subscriptionId, notification));
                _registrations[subscriptionId] = subscription;
                return new { registrationId = subscriptionId };
            }
            case "ui.registerDialog":
            {
                var content = args.GetProperty("content").Clone();
                var definition = new PluginDialogDefinition
                {
                    Id = String(args, "id"), Title = String(args, "title"),
                    Width = args.TryGetProperty("width", out var width) ? width.GetDouble() : 560,
                    DismissOnEscape = !args.TryGetProperty("dismissOnEscape", out var esc) || esc.GetBoolean(),
                    DismissOnBackgroundClick = args.TryGetProperty("dismissOnBackgroundClick", out var backgroundClick) && backgroundClick.GetBoolean(),
                    CreateContent = _ => DeclarativeContent(content, PluginPermissionNames.UiBasic, "PipeDialogButton")
                };
                return new { registrationId = Keep(_context.Ui.RegisterDialog(definition)) };
            }
            case "ui.registerButton":
            {
                var callbackId = String(args, "callbackId");
                var buttonId = String(args, "id");
                var definition = new PluginButtonDefinition
                {
                    Id = buttonId, Text = String(args, "text"),
                    Target = args.TryGetProperty("target", out var target) ? target.GetString() ?? PluginUiTargets.PluginSettings : PluginUiTargets.PluginSettings,
                    Description = args.TryGetProperty("description", out var description) ? description.GetString() : null,
                    Order = args.TryGetProperty("order", out var order) ? order.GetInt32() : 0,
                    OnClick = async ui =>
                    {
                        await _context.Callbacks.InvokeAsync(PluginPermissionNames.UiBasic, "PipeUiButton:" + buttonId,
                            token => RemoteAsync<bool>(callbackId, new { target = ui.Target }, token), _context.Host.CallbackTimeout, ui.Stopping).ConfigureAwait(false);
                    }
                };
                return new { registrationId = Keep(_context.Ui.RegisterButton(definition)) };
            }
            case "ui.registerPanel":
            {
                var content = args.GetProperty("content").Clone();
                var definition = new PluginPanelDefinition
                {
                    Id = String(args, "id"),
                    Target = args.TryGetProperty("target", out var target) ? target.GetString() ?? PluginUiTargets.PluginSettings : PluginUiTargets.PluginSettings,
                    Order = args.TryGetProperty("order", out var order) ? order.GetInt32() : 0,
                    CreateContent = _ => DeclarativeContent(content, PluginPermissionNames.UiBasic, "PipePanelButton")
                };
                return new { registrationId = Keep(_context.Ui.RegisterPanel(definition)) };
            }
            case "ui.registerBackground": return new { registrationId = Keep(_context.Ui.RegisterBackground(args.Deserialize<PluginBackgroundDefinition>(Json)!)) };
            case "ui.registerInstanceAction":
                return new { registrationId = Keep(_context.Ui.RegisterInstanceAction(new(String(args, "id"), String(args, "title"),
                    async (instance, token) => { await RemoteAsync<bool>(String(args, "callbackId"), instance, token); }))) };
            case "ui.registerSetting":
            case "ui.registerOption":
            {
                var kind = args.TryGetProperty("kind", out var optionKind) ? optionKind.Deserialize<PluginOptionKind>(Json) : PluginOptionKind.Toggle;
                object? defaultValue = null;
                if (args.TryGetProperty("defaultValue", out var value)) defaultValue = kind switch
                { PluginOptionKind.Toggle => value.GetBoolean(), PluginOptionKind.Number => value.GetDouble(), _ => value.GetString() };
                var definition = new PluginOptionDefinition { Id = String(args, "id"), Title = String(args, "title"), Kind = kind, DefaultValue = defaultValue,
                    ConfigKey = args.TryGetProperty("configKey", out var configKey) ? configKey.GetString() : null,
                    Target = args.TryGetProperty("target", out var target) ? target.GetString() ?? PluginUiTargets.PluginSettings : PluginUiTargets.PluginSettings,
                    Description = args.TryGetProperty("description", out var description) ? description.GetString() : null,
                    Order = args.TryGetProperty("order", out var order) ? order.GetInt32() : 0,
                    Choices = args.TryGetProperty("choices", out var choices) ? choices.Deserialize<PluginChoice[]>(Json) ?? Array.Empty<PluginChoice>() : Array.Empty<PluginChoice>(),
                    Minimum = args.TryGetProperty("minimum", out var minimum) ? minimum.GetDouble() : double.MinValue,
                    Maximum = args.TryGetProperty("maximum", out var maximum) ? maximum.GetDouble() : double.MaxValue };
                return new { registrationId = Keep(method == "ui.registerSetting" ? _context.Ui.RegisterSetting(definition) : _context.Ui.RegisterOption(definition)) };
            }
            case "ui.importBackgroundImage": return await _context.Ui.ImportBackgroundImageAsync(ct).ConfigureAwait(false);
            case "ui.registerPage":
            {
                var content = args.GetProperty("content").Clone(); var pageId = String(args, "id");
                return new { registrationId = Keep(_context.Ui.RegisterNavigationPage(new(pageId, String(args, "title"), _ => DeclarativeContent(content, PluginPermissionNames.UiNavigation, "PipePageButton"),
                    args.TryGetProperty("showInSimplifiedMode", out var simple) && simple.GetBoolean(),
                    args.TryGetProperty("languages", out var languages) ? languages.Deserialize<ImmutableArray<string>>(Json) : default))) };
            }
            case "providers.register":
            {
                var definition = args.GetProperty("definition").Deserialize<PluginProviderDefinition>(Json)!;
                var callbacks = args.GetProperty("callbacks").Deserialize<ImmutableDictionary<string, string>>(Json)!;
                var proxy = new RemoteProviders(this, callbacks);
                var kind = String(args, "kind");
                var registration = kind switch
                {
                    "download" => _context.Providers.RegisterDownloadProvider(definition, proxy),
                    "mod" => _context.Providers.RegisterModProvider(definition, proxy),
                    "loader" => _context.Providers.RegisterLoaderInstaller(definition, proxy),
                    "login" => _context.Providers.RegisterLoginProvider(definition, proxy),
                    _ => throw new PluginOperationException("UnknownProvider", "未知提供者类型。")
                };
                return new { registrationId = Keep(registration) };
            }
            case "workspace.writeVersionJson": await Workspace(args).WriteVersionJsonAsync(String(args, "json"), ct).ConfigureAwait(false); return true;
            case "workspace.downloadFile": await Workspace(args).DownloadFileAsync(String(args, "url"), String(args, "relativePath"), args.TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null, ct).ConfigureAwait(false); return true;
            case "workspace.runJavaInstaller": return await Workspace(args).RunJavaInstallerAsync(String(args, "relativeJar"), args.GetProperty("arguments").Deserialize<ImmutableArray<string>>(Json), ct).ConfigureAwait(false);
            case "auth.authorize":
                if (!_brokers.TryGetValue(String(args, "brokerId"), out var broker)) throw new PluginOperationException("InvalidBroker", "登录代理已失效。");
                return await broker.AuthorizeAsync(args.GetProperty("request").Deserialize<PluginLoginRequest>(Json)!, ct).ConfigureAwait(false);
            default: throw new PluginOperationException("UnknownMethod", "未开放的插件方法。");
        }
    }
    private PluginInstallWorkspace Workspace(JsonElement args) => _workspaces.TryGetValue(String(args, "workspaceId"), out var workspace)
        ? workspace : throw new PluginOperationException("InvalidWorkspace", "安装工作区已失效。");
    private static bool Remove(IDisposable handle) { handle.Dispose(); return true; }
    private IDisposable SubscribeEvent<T>(string id) where T : PluginNotification => _context.Events.Subscribe<T>((notification, ct) => SendEventAsync(id, notification, ct));
    private void SendEventFireAndForget(string subscriptionId, object notification)
    {
        _ = Task.Run(async () =>
        {
            try { await SendEventAsync(subscriptionId, notification, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) _context.Log("PipeEventWriteFailed: " + ex.GetType().Name); }
        });
    }
    private async ValueTask SendEventAsync(string subscriptionId, object notification, CancellationToken ct)
        => await WriteAsync(new { protocol = 1, kind = "event", subscriptionId, @event = notification.GetType().Name, payload = notification }, ct).ConfigureAwait(false);
    private async ValueTask<T> RemoteAsync<T>(string callbackId, object? payload, CancellationToken ct)
    {
        var id = "host-" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); _pending[id] = completion;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            await WriteAsync(new { protocol = 1, kind = "request", id, method = "callbacks.invoke", @params = new { callbackId, payload } }, stop.Token).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            return result.Deserialize<T>(Json)!;
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private FrameworkElement DeclarativeContent(JsonElement content, string callbackPermission = PluginPermissionNames.UiNavigation, string callbackAction = "PipeUiButton")
    {
        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() > 64) throw new PluginOperationException("InvalidUi", "页面内容必须是最多 64 项的数组。");
        var panel = new StackPanel { Margin = new Thickness(20) };
        foreach (var node in content.EnumerateArray())
        {
            var text = _context.Host.Safe(String(node, "text"));
            if (String(node, "type") == "text") panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
            else if (String(node, "type") == "button")
            {
                var callbackId = String(node, "callbackId"); var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
                button.Click += async (_, _) =>
                {
                    try { await _context.Callbacks.InvokeAsync(callbackPermission, callbackAction, token => RemoteAsync<bool>(callbackId, null, token), _context.Host.CallbackTimeout, _context.Stopping); }
                    catch (OperationCanceledException) { }
                };
                panel.Children.Add(button);
            }
            else throw new PluginOperationException("InvalidUi", "EXE 页面仅支持 text 和 button。");
        }
        return panel;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _pipe.Dispose();
        foreach (var pending in _pending.Values) pending.TrySetCanceled(); _pending.Clear();
        foreach (var registration in _registrations.Values) try { registration.Dispose(); } catch (Exception ex) { _context.Log("PipeCleanupFailed: " + ex.GetType().Name); }
        _registrations.Clear(); _workspaces.Clear(); _brokers.Clear();
    }
    private sealed class RemoteProviders : IPluginDownloadProvider, IPluginModProvider, IPluginLoaderInstaller, IPluginLoginProvider
    {
        private readonly PluginPipeSession _session; private readonly ImmutableDictionary<string, string> _callbacks;
        internal RemoteProviders(PluginPipeSession session, ImmutableDictionary<string, string> callbacks) { _session = session; _callbacks = callbacks; }
        private ValueTask<T> Call<T>(string method, object? payload, CancellationToken ct) => _callbacks.TryGetValue(method, out var id)
            ? _session.RemoteAsync<T>(id, payload, ct) : throw new PluginOperationException("MissingCallback", "EXE 提供者缺少回调：" + method);
        public ValueTask<PluginVersionManifest> GetManifestAsync(CancellationToken ct) => Call<PluginVersionManifest>("getManifest", null, ct);
        public ValueTask<string?> ResolveFileAsync(PluginFileRequest request, CancellationToken ct) => Call<string?>("resolveFile", request, ct);
        public ValueTask<PluginModSearchResult> SearchAsync(PluginModQuery query, CancellationToken ct) => Call<PluginModSearchResult>("search", query, ct);
        public ValueTask<PluginModProject?> GetDetailsAsync(string projectId, CancellationToken ct) => Call<PluginModProject?>("getDetails", new { projectId }, ct);
        public ValueTask<ImmutableArray<PluginModFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct)
            => Call<ImmutableArray<PluginModFile>>("getFiles", new { projectId, gameVersion, loader }, ct);
        public ValueTask<ImmutableArray<PluginLoaderBuild>> GetBuildsAsync(string minecraftVersion, CancellationToken ct) => Call<ImmutableArray<PluginLoaderBuild>>("getBuilds", new { minecraftVersion }, ct);
        public async ValueTask InstallAsync(PluginInstallWorkspace workspace, PluginLoaderInstallRequest request, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N"); _session._workspaces[id] = workspace;
            try { await Call<bool>("install", new { workspaceId = id, request }, ct); }
            finally { _session._workspaces.TryRemove(id, out _); }
        }
        public async ValueTask<PluginLoginTicket?> LoginAsync(PluginLoginBroker broker, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N"); _session._brokers[id] = broker;
            try { return await Call<PluginLoginTicket?>("login", new { brokerId = id }, ct); }
            finally { _session._brokers.TryRemove(id, out _); }
        }
    }
}
