using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XCL2.App.Services.Plugins;

/// <summary>只提供渲染必需的皮肤与模型信息，不传递账户令牌。工厂与所有方法均在 UI 线程调用。</summary>
public sealed record PluginSkinModelRenderRequest(PluginContext Plugin, BitmapSource Skin, bool SlimModel, string Username);
public delegate IPluginSkinModelRenderer PluginSkinModelRendererFactory(PluginSkinModelRenderRequest request);

public interface IPluginSkinModelRenderer : IDisposable
{
    FrameworkElement View { get; }
    void SetSkin(BitmapSource skin, bool slimModel);
    void SetMotion(string motion);
    /// <summary>实际经过时间；可驱动自有引擎。使用独立渲染循环的实现可以留空。</summary>
    void RenderFrame(TimeSpan elapsed);
}

internal static partial class PluginRuntimeRegistry
{
    internal static event Action? RenderersChanged;
    private static bool _rendererNotificationQueued;
    private static void QueueRenderersChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || _rendererNotificationQueued) return;
        _rendererNotificationQueued = true;
        dispatcher.BeginInvoke(new Action(() =>
        {
            lock (Gate) _rendererNotificationQueued = false;
            foreach (var handler in RenderersChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { ((Action)handler)(); }
                catch (Exception ex) { LauncherLogService.AppendLine($"[插件渲染器更新] {ex.Message}"); }
        }));
    }

    internal static IPluginSkinModelRenderer? CreateSkinModelRenderer(BitmapSource skin, bool slim, string username)
    {
        foreach (var entry in Snapshot("skin-model"))
        {
            IPluginSkinModelRenderer? renderer = null;
            try
            {
                renderer = ((PluginSkinModelRendererFactory)entry.Callback)(new(entry.Plugin, skin, slim, username));
                if (renderer?.View == null || renderer.View.Parent != null || VisualTreeHelper.GetParent(renderer.View) != null)
                    throw new InvalidOperationException("渲染器须返回尚未挂载的 View。");
                renderer.SetSkin(skin, slim);
                return renderer;
            }
            catch (Exception ex)
            {
                try { renderer?.Dispose(); } catch { }
                entry.Plugin.Log($"模型渲染器 {entry.Id} 加载失败：{ex.Message}");
            }
        }
        return null;
    }
}
