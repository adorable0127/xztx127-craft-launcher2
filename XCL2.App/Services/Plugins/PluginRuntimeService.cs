using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services.Plugins;

public sealed record PluginLaunchRequest(PluginContext Plugin, LauncherService.LaunchOptions Options);
public sealed record PluginAvatarRenderRequest(PluginContext Plugin, byte[] SkinPngBytes, int OutputSize, bool IncludeHatLayer);
public delegate GameProcessInfo PluginLaunchMiddleware(PluginLaunchRequest request, Func<GameProcessInfo> next);
public delegate byte[] PluginAvatarRenderer(PluginAvatarRenderRequest request, Func<byte[]> next);

/// <summary>启动内核和头像渲染的真实调用链扩展。返回的句柄可撤销；停止插件自动注销。</summary>
public sealed class PluginRuntimeService : IDisposable
{
    private readonly PluginContext _context;
    private bool _disposed;
    internal PluginRuntimeService(PluginContext context) { _context = context; }
    public IDisposable RegisterLaunchMiddleware(string id, PluginLaunchMiddleware middleware, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return Register("launch", id, middleware, order);
    }
    public IDisposable RegisterAvatarRenderer(string id, PluginAvatarRenderer renderer, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return Register("avatar", id, renderer, order);
    }
    /// <summary>替换皮肤模型的整个渲染后端；可返回 WPF、D3DImage 或原生窗口宿主。</summary>
    public IDisposable RegisterSkinModelRenderer(string id, PluginSkinModelRendererFactory factory, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Register("skin-model", id, factory, order);
    }

    private IDisposable Register(string kind, string id, Delegate callback, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (PluginRuntimeRegistry.Gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PluginRuntimeService));
            return PluginRuntimeRegistry.Register(this, _context, kind, id, callback, order);
        }
    }
    public void Dispose()
    {
        lock (PluginRuntimeRegistry.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            PluginRuntimeRegistry.Remove(this);
        }
    }
}

internal static partial class PluginRuntimeRegistry
{
    internal static readonly object Gate = new();
    private sealed record Entry(PluginRuntimeService Owner, PluginContext Plugin, string Kind, string Id, Delegate Callback, int Order);
    private static readonly List<Entry> Entries = new();
    [ThreadStatic] private static bool _launching;
    [ThreadStatic] private static bool _rendering;
    internal static IDisposable Register(PluginRuntimeService owner, PluginContext plugin, string kind, string id, Delegate callback, int order)
    {
        if (Entries.Any(e => e.Owner == owner && e.Kind == kind && e.Id == id)) throw new ArgumentException($"重复注册 {kind}:{id}");
        var entry = new Entry(owner, plugin, kind, id, callback, order);
        Entries.Add(entry);
        if (kind == "skin-model") QueueRenderersChanged();
        return new PluginRegistration(() =>
        {
            lock (Gate)
            {
                if (Entries.Remove(entry) && kind == "skin-model") QueueRenderersChanged();
            }
        });
    }
    internal static void Remove(PluginRuntimeService owner)
    {
        var renderers = Entries.Any(e => e.Owner == owner && e.Kind == "skin-model");
        Entries.RemoveAll(e => e.Owner == owner);
        if (renderers) QueueRenderersChanged();
    }
    private static Entry[] Snapshot(string kind)
    {
        lock (Gate) return Entries.Where(e => e.Kind == kind && !e.Plugin.Stopping.IsCancellationRequested)
            .OrderBy(e => e.Order).ThenBy(e => e.Plugin.PluginId, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
    }
    internal static GameProcessInfo Launch(LauncherService.LaunchOptions options, Func<GameProcessInfo> builtIn)
    {
        if (_launching) throw new InvalidOperationException("启动扩展不能递归调用 Launch；请使用 next() 调用后续内核。");
        _launching = true;
        try
        {
            Func<GameProcessInfo> pipeline = builtIn;
            foreach (var entry in Enumerable.Reverse(Snapshot("launch")))
            {
                var next = pipeline;
                pipeline = () =>
                {
                    if (entry.Plugin.Stopping.IsCancellationRequested) return next();
                    var called = false;
                    GameProcessInfo Next()
                    {
                        if (called) throw new InvalidOperationException("每次启动中 next() 只能调用一次，避免重复启动游戏。");
                        called = true;
                        return next();
                    }
                    try { return ((PluginLaunchMiddleware)entry.Callback)(new(entry.Plugin, options), Next)
                        ?? throw new InvalidOperationException("启动内核返回 null。"); }
                    catch (Exception ex)
                    {
                        entry.Plugin.Log($"启动内核扩展 {entry.Id} 失败：{ex.Message}");
                        throw; // 内核可能已启动进程，不能自动再运行默认内核。
                    }
                };
            }
            return pipeline();
        }
        finally { _launching = false; }
    }
    internal static byte[] RenderAvatar(byte[] bytes, int size, bool hat, Func<byte[]> builtIn)
    {
        if (_rendering) throw new InvalidOperationException("渲染扩展不能递归调用 RenderFaceAvatar；请使用 next()。");
        _rendering = true;
        try
        {
            Func<byte[]> pipeline = builtIn;
            foreach (var entry in Enumerable.Reverse(Snapshot("avatar")))
            {
                var next = pipeline;
                pipeline = () =>
                {
                    if (entry.Plugin.Stopping.IsCancellationRequested) return next();
                    byte[]? cached = null;
                    bool called = false;
                    byte[] Next()
                    {
                        if (!called) { cached = next(); called = true; }
                        return cached!;
                    }
                    try
                    {
                        var result = ((PluginAvatarRenderer)entry.Callback)(new(entry.Plugin, bytes, size, hat), Next);
                        if (result == null || result.Length < 8 || !result.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                            throw new InvalidDataException("渲染器必须返回 PNG 字节。");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        entry.Plugin.Log($"渲染扩展 {entry.Id} 失败，回退后续渲染器：{ex.Message}");
                        return Next();
                    }
                };
            }
            return pipeline();
        }
        finally { _rendering = false; }
    }
}
