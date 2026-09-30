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

    private void RefreshRenderer()
    {
        RestoreBuiltIn();
        if (!IsLoaded || _skin == null || _builtIn == null) return;
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

    public bool RenderFrame(string motion)
    {
        if (_renderer == null) return false;
        try
        {
            if (_motion != motion) { _renderer.SetMotion(motion); _motion = motion; }
            var now = Environment.TickCount64;
            _renderer.RenderFrame(TimeSpan.FromMilliseconds(Math.Clamp(now - _lastFrame, 0, 250)));
            _lastFrame = now;
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
