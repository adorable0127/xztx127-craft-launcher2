using System.Collections.Concurrent;
using System.Threading.Channels;

namespace XCL2.App.Services.Plugins;

/// <summary>通知在插件的后台域串行分发，订阅返回值可提前注销；停止自动清理。</summary>
public sealed partial class PluginEventService : IDisposable
{
    private sealed record Listener(Type Type, Func<PluginNotification, CancellationToken, ValueTask> Callback);
    private readonly PluginContext _context;
    private readonly List<Listener> _listeners = new();
    private readonly Channel<PluginNotification> _queue = Channel.CreateBounded<PluginNotification>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private bool _started;
    private bool _disposed;
    internal bool IsDisposed => Volatile.Read(ref _disposed);
    private long _lastOverflowLog;
    internal PluginEventService(PluginContext context) { _context = context; }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> callback) where TEvent : PluginNotification
    {
        ArgumentNullException.ThrowIfNull(callback);
        _ = _context.Host;
        _context.Permissions.Demand(PermissionFor(typeof(TEvent)));
        var listener = new Listener(typeof(TEvent), (notification, ct) => callback((TEvent)notification, ct));
        var start = false;
        lock (_listeners)
        {
            _context.EnsureActive();
            _listeners.Add(listener);
            if (!_started)
            {
                _started = true;
                start = true;
            }
        }
        if (start) { PluginEventHub.Add(this); _ = Task.Run(DispatchAsync); }
        // LauncherReady 是“当前宿主已就绪”的状态型生命周期事件，不只是一次性的边沿通知。
        // 插件可能在启动器运行中才被导入/授权；这时首帧广播早已发生，若不回放，
        // 新插件即使正确订阅 lifecycle 也永远等不到 Ready。回放沿用原通知的序号/Revision，
        // 不伪造一次新的全局状态变化。
        if (typeof(TEvent) == typeof(PluginLauncherReady) && PluginEventHub.ReadySnapshot is { } ready) Enqueue(ready);
        return _context.Track(new PluginRegistration(() => { lock (_listeners) _listeners.Remove(listener); }));
    }
    internal static string PermissionFor(Type type) => type.Name switch
    {
        nameof(PluginLauncherReady) or nameof(PluginLauncherStopping) => PluginPermissionNames.Lifecycle,
        nameof(PluginAccountAdded) or nameof(PluginAccountUpdated) or nameof(PluginAccountRemoved) or nameof(PluginAccountSelectionChanged) => PluginPermissionNames.AccountsRead,
        nameof(PluginInstanceCreated) or nameof(PluginInstanceRemoved) or nameof(PluginInstanceRenamed) or
        nameof(PluginInstanceSelectionChanged) or nameof(PluginInstanceVisibilityChanged) or nameof(PluginInstanceCatalogChanged) or nameof(PluginGameStarted) or
        nameof(PluginGameExited) or nameof(PluginGameCrashed) => PluginPermissionNames.InstancesRead,
        nameof(PluginDownloadCreated) or nameof(PluginDownloadProgressChanged) or nameof(PluginDownloadCompleted) or
        nameof(PluginDownloadFailed) or nameof(PluginDownloadCanceled) or nameof(PluginDownloadRemoved) => PluginPermissionNames.DownloadsRead,
        nameof(PluginModInstalled) or nameof(PluginModRemoved) or nameof(PluginModEnabledChanged) or nameof(PluginModCatalogChanged) => PluginPermissionNames.ModsRead,
        nameof(PluginSettingsChanged) => PluginPermissionNames.SettingsRead,
        nameof(PluginPeerUnavailable) => PluginPermissionNames.Messaging,
        _ => throw new ArgumentException("不支持的宿主事件类型。", nameof(type))
    };
    internal void Enqueue(PluginNotification notification)
    {
        if (_disposed || !_context.Permissions.Has(PermissionFor(notification.GetType()))) return;
        lock (_listeners)
            if (!_listeners.Any(l => l.Type.IsInstanceOfType(notification))) return;
        if (_queue.Writer.TryWrite(notification)) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastOverflowLog) < 1000) return;
        Interlocked.Exchange(ref _lastOverflowLog, now);
        _context.Log("EventQueueOverflow: 通知存在序号缺口，请重新查询快照。");
    }
    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                Listener[] listeners;
                lock (_listeners) listeners = _listeners.Where(l => l.Type.IsInstanceOfType(notification)).ToArray();
                var permission = PermissionFor(notification.GetType());
                foreach (var listener in listeners)
                {
                    if (_disposed || !_context.Permissions.Has(permission)) break;
                    lock (_listeners) if (!_listeners.Contains(listener)) continue;
                    await _context.Callbacks.InvokeAsync(permission, notification.GetType().Name, async ct =>
                    {
                        await listener.Callback(notification, ct).ConfigureAwait(false);
                        return true;
                    }, _context.Host.CallbackTimeout, _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PluginEventHub.Remove(this);
        _queue.Writer.TryComplete();
        _stop.Cancel();
        lock (_listeners) _listeners.Clear();
    }
    internal async Task NotifyImmediatelyAsync(PluginNotification notification)
    {
        Listener[] listeners;
        lock (_listeners) listeners = _listeners.Where(l => l.Type.IsInstanceOfType(notification)).ToArray();
        var permission = PermissionFor(notification.GetType());
        foreach (var listener in listeners)
        {
            if (_disposed || !_context.Permissions.Has(permission)) return;
            await _context.Callbacks.InvokeAsync(permission, notification.GetType().Name, async ct =>
            { await listener.Callback(notification, ct).ConfigureAwait(false); return true; }, _context.Host.CallbackTimeout, _context.Stopping).ConfigureAwait(false);
        }
    }
}

internal static class PluginEventHub
{
    private static readonly object Gate = new();
    private static readonly HashSet<PluginEventService> Services = new();
    private static long _sequence;
    private static PluginLauncherReady? _readySnapshot;
    internal static long Revision;
    internal static PluginLauncherReady? ReadySnapshot { get { lock (Gate) return _readySnapshot; } }
    internal static void Add(PluginEventService service) { lock (Gate) if (!service.IsDisposed) Services.Add(service); }
    internal static void Remove(PluginEventService service) { lock (Gate) Services.Remove(service); }
    internal static void Publish(PluginNotification notification)
    {
        // 只有宿主调用 Publish；插件没有伪造业务事件的公开入口。
        lock (Gate)
        {
            notification = notification with { Sequence = ++_sequence, Revision = Interlocked.Increment(ref Revision), OccurredAtUtc = DateTimeOffset.UtcNow };
            if (notification is PluginLauncherReady ready) _readySnapshot = ready;
            foreach (var service in Services) service.Enqueue(notification);
        }
    }
    internal static async Task PublishStoppingAsync(TimeSpan timeout)
    {
        PluginEventService[] services; PluginNotification notification;
        lock (Gate)
        {
            services = Services.ToArray();
            notification = new PluginLauncherStopping { Sequence = ++_sequence, Revision = Interlocked.Increment(ref Revision), OccurredAtUtc = DateTimeOffset.UtcNow };
        }
        try { await Task.WhenAll(services.Select(service => service.NotifyImmediatelyAsync(notification))).WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] ShutdownNotificationIncomplete: " + ex.GetType().Name); }
    }
}

internal readonly record struct PluginInvocation<T>(bool Success, T? Value);

/// <summary>同步入口在本插件专用后台线程执行；超时只放弃结果，不宣称能强杀 DLL。</summary>
internal sealed class PluginCallbackRunner : IDisposable
{
    private readonly PluginContext _context;
    private readonly BlockingCollection<Action> _work = new(1);
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Thread _thread;
    internal static readonly AsyncLocal<PluginContext?> Active = new();
    internal PluginCallbackRunner(PluginContext context)
    {
        _context = context;
        _thread = new Thread(() =>
        {
            try { foreach (var action in _work.GetConsumingEnumerable()) action(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }) { IsBackground = true, Name = "XCL2.Plugin." + context.PluginId };
        _thread.Start();
    }
    internal async Task<PluginInvocation<T>> InvokeAsync<T>(string permission, string action,
        Func<CancellationToken, ValueTask<T>> callback, TimeSpan timeout, CancellationToken ct)
    {
        if (!_context.Permissions.Has(permission) || ReferenceEquals(Active.Value, _context)) return new(false, default);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping);
        if (!await _slot.WaitAsync(timeout, stop.Token).ConfigureAwait(false))
        {
            _context.Log("CallbackBusy: " + action);
            return new(false, default);
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorization = Interlocked.Read(ref _context.AuthorizationRevision);
        using var callbackStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        try
        {
            _context.Permissions.Demand(permission);
            if (!_work.TryAdd(() =>
            {
                var previous = Active.Value;
                Active.Value = _context;
                try { _ = CompleteAsync(callback(callbackStop.Token), completion); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { Active.Value = previous; }
            })) return new(false, default);
            var value = await completion.Task.WaitAsync(timeout, stop.Token).ConfigureAwait(false);
            _context.Permissions.Demand(permission);
            if (authorization != Interlocked.Read(ref _context.AuthorizationRevision)) return new(false, default);
            return new(true, value);
        }
        catch (TimeoutException)
        {
            callbackStop.Cancel();
            _context.Log("CallbackTimeout: " + action);
            if (!completion.Task.IsCompleted) _context.DisableBusiness();
            return new(false, default);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, default); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _context.Log("CallbackFailed: " + action + "; " + ex.GetType().Name);
            return new(false, default);
        }
        finally
        {
            // 观察迟到异常，迟到结果永远不会回写宿主计划。
            _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _slot.Release();
        }
    }
    private static async Task CompleteAsync<T>(ValueTask<T> result, TaskCompletionSource<T> completion)
    {
        try { completion.TrySetResult(await result.ConfigureAwait(false)); }
        catch (OperationCanceledException) { completion.TrySetCanceled(); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    public void Dispose() { _work.CompleteAdding(); }
}
