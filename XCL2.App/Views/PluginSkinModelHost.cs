using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>可热替换的模型渲染宿主。插件禁用/移除、窗口卸载和后端异常均释放原生资源。</summary>
public sealed class PluginSkinModelHost : ContentControl
{
    private FrameworkElement? _builtIn;
    private IPluginSkinModelRenderer? _renderer;
    private BitmapSource? _skin;
    private bool _slim;
    private string _username = "";
    private string? _motion;
    private long _lastFrame;
    private bool _pluginRenderingEnabled = true;
    private bool _subscribed;

    public PluginSkinModelHost()
    {
        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            _subscribed = true;
            PluginRuntimeRegistry.RenderersChanged += RefreshRenderer;
            RefreshRenderer();
        };
        Unloaded += (_, _) =>
        {
            if (!_subscribed) return;
            _subscribed = false;
            PluginRuntimeRegistry.RenderersChanged -= RefreshRenderer;
            RestoreBuiltIn();
        };
    }

    public void Configure(BitmapSource skin, bool slim, string username)
    {
        _builtIn ??= Content as FrameworkElement;
        _skin = skin; _slim = slim; _username = username;
        if (IsLoaded) RefreshRenderer();
    }

    public void SetSkin(BitmapSource skin, bool slim)
    {
        _skin = skin; _slim = slim;
        try { _renderer?.SetSkin(skin, slim); }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>插件接口不支持幅度/滤镜控制时，回退内置模型并释放插件的自有渲染循环。</summary>
    public void SetPluginRenderingEnabled(bool enabled)
    {
        if (_pluginRenderingEnabled == enabled) return;
        _pluginRenderingEnabled = enabled;
        if (enabled) RefreshRenderer();
        else RestoreBuiltIn();
    }

    private void RefreshRenderer()
    {
        RestoreBuiltIn();
        if (!_pluginRenderingEnabled || !IsLoaded || _skin == null || _builtIn == null) return;
        _renderer = PluginRuntimeRegistry.CreateSkinModelRenderer(_skin, _slim, _username);
        if (_renderer == null) return;
        try
        {
            SetCurrentValue(ContentProperty, _renderer.View);
            _motion = null;
            _lastFrame = Environment.TickCount64;
        }
        catch (Exception ex) { Fail(ex); }
    }

    // 保留原有宿主入口，外部代码无需随本次内置动画修改而迁移。
    public bool RenderFrame(string motion)
    {
        var now = Environment.TickCount64;
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, now - _lastFrame));
        _lastFrame = now;
        return RenderFrame(motion, elapsed);
    }

    public bool RenderFrame(string motion, TimeSpan elapsed)
    {
        if (_renderer == null) return false;
        try
        {
            if (_motion != motion) { _renderer.SetMotion(motion); _motion = motion; }
            // 同一套单调时钟给出实际经过时间；封顶只用于避免外部物理引擎的大步长。
            _renderer.RenderFrame(TimeSpan.FromMilliseconds(Math.Clamp(elapsed.TotalMilliseconds, 0, 250)));
            _lastFrame = Environment.TickCount64;
            return true;
        }
        catch (Exception ex) { Fail(ex); return false; }
    }

    private void Fail(Exception ex)
    {
        ErrorPresenter.LogTechnicalDetail($"[模型渲染后端异常，恢复内置渲染]\n{ex}");
        RestoreBuiltIn();
    }
    private void RestoreBuiltIn()
    {
        var renderer = _renderer;
        _renderer = null;
        if (_builtIn != null && !ReferenceEquals(Content, _builtIn)) SetCurrentValue(ContentProperty, _builtIn);
        try { renderer?.Dispose(); }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail($"[释放模型渲染后端]\n{ex}"); }
        _motion = null;
    }
}
