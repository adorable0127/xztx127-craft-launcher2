using System.Windows;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 完全无动画模式的资源策略：仅复用无后台任务的展示页，不持有账户、下载、设置或实例页面。
/// 正常模式恢复原背景缓存，并释放本策略额外持有的页面和缓存对象。
/// </summary>
public sealed class UiPerformancePolicy : IDisposable
{
    public const string MorePageKey = "more";
    public const string AboutHelpPageKey = "about-help";

    private sealed record PageEntry(UIElement Page, LinkedListNode<string> Node);
    private readonly Window _owner;
    private readonly UIElement _background;
    private readonly CacheMode? _originalBackgroundCache;
    private readonly Dictionary<string, PageEntry> _pages = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recentPages = new();
    private BitmapCache? _managedBackgroundCache;
    private double _managedCacheScale;
    private bool _backgroundIsStatic;
    private bool _disposed;

    public UiPerformancePolicy(Window owner, UIElement background)
    {
        _owner = owner;
        _background = background;
        _originalBackgroundCache = background.CacheMode;
        AnimationSettingsService.Changed += SettingsChanged;
        LocalizationService.LanguageChanged += InvalidatePages;
        ApplyCurrentPolicy();
    }

    private int PageCapacity => !AnimationSettingsService.NoAnimationModeEnabled ? 0
        : AnimationSettingsService.OptimizationPreference switch
        {
            NoAnimationOptimization.Cpu => 2,
            NoAnimationOptimization.Balanced => 1,
            _ => 0
        };

    private void SettingsChanged()
    {
        if (_disposed) return;
        void Update()
        {
            if (_disposed) return;
            InvalidatePages();
            ApplyCurrentPolicy();
        }
        if (_owner.Dispatcher.CheckAccess()) Update();
        else if (!_owner.Dispatcher.HasShutdownStarted)
            _owner.Dispatcher.BeginInvoke(new Action(Update));
    }

    public void SetBackgroundIsStatic(bool isStatic)
    {
        _backgroundIsStatic = isStatic;
        ApplyCurrentPolicy();
    }

    public bool TryGetPage(string? key, out UIElement? page)
    {
        ApplyCurrentPolicy();
        page = null;
        if (_disposed || PageCapacity == 0 || key == null || !_pages.TryGetValue(key, out var entry))
            return false;
        _recentPages.Remove(entry.Node);
        _recentPages.AddLast(entry.Node);
        page = entry.Page;
        return true;
    }

    public void RememberPage(string? key, UIElement page)
    {
        ApplyCurrentPolicy();
        // 明确白名单同时检查实际类型，未来其他入口不能误把业务页面加入缓存。
        var safe = key switch
        {
            MorePageKey => page is MorePage,
            AboutHelpPageKey => page is AboutHelpPage,
            _ => false
        };
        if (_disposed || PageCapacity == 0 || !safe || key == null) return;
        if (_pages.Remove(key, out var previous)) _recentPages.Remove(previous.Node);
        var node = _recentPages.AddLast(key);
        _pages.Add(key, new PageEntry(page, node));
        TrimPages(PageCapacity);
    }

    private void TrimPages(int capacity)
    {
        while (_pages.Count > capacity && _recentPages.First is { } oldest)
        {
            _pages.Remove(oldest.Value);
            _recentPages.RemoveFirst();
        }
    }

    private void InvalidatePages()
    {
        _pages.Clear();
        _recentPages.Clear();
    }

    private void ApplyCurrentPolicy()
    {
        if (_disposed) return;
        TrimPages(PageCapacity);
        if (!AnimationSettingsService.NoAnimationModeEnabled)
        {
            _background.SetCurrentValue(UIElement.CacheModeProperty, _originalBackgroundCache);
            _managedBackgroundCache = null;
            _managedCacheScale = 0;
            return;
        }

        var preference = AnimationSettingsService.OptimizationPreference;
        if (!_backgroundIsStatic || preference == NoAnimationOptimization.Memory)
        {
            // 动图逐帧改变、视频另有解码表面，额外整屏位图缓存没有复用收益。
            _background.SetCurrentValue(UIElement.CacheModeProperty, null);
            _managedBackgroundCache = null;
            _managedCacheScale = 0;
            return;
        }

        var scale = preference == NoAnimationOptimization.Cpu ? 1.0 : 0.75;
        if (_managedBackgroundCache == null || _managedCacheScale != scale)
        {
            _managedBackgroundCache = new BitmapCache(scale) { SnapsToDevicePixels = false };
            _managedCacheScale = scale;
        }
        if (!ReferenceEquals(_background.CacheMode, _managedBackgroundCache))
            _background.SetCurrentValue(UIElement.CacheModeProperty, _managedBackgroundCache);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AnimationSettingsService.Changed -= SettingsChanged;
        LocalizationService.LanguageChanged -= InvalidatePages;
        InvalidatePages();
        _background.SetCurrentValue(UIElement.CacheModeProperty, _originalBackgroundCache);
        _managedBackgroundCache = null;
    }
}
