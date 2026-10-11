using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace XCL2.App.Services;

/// <summary>
/// 页面只持有这个不引用 WebView2 SDK 类型的接口。SDK 缺失时，页面本身和 Unloaded
/// 仍能被 JIT 编译；实际 SDK 类型只会在用户主动打开网页后的独立调用边界内解析。
/// </summary>
internal interface IEmbeddedWebView : IDisposable
{
    FrameworkElement View { get; }
    Task InitializeAsync();
    void Navigate(string url);
}

internal static class EmbeddedWebView
{
    public const string MissingComponentsMessage =
        "启动器的 WebView2 组件缺失或损坏。请重新解压完整发布包，保留 Microsoft.Web.WebView2.Core.dll、Microsoft.Web.WebView2.Wpf.dll 和 runtimes 目录，然后重启。安装 WebView2 Runtime 不能补齐这些程序 DLL。当前仍可使用系统浏览器。";

    /// <summary>按本程序编译时引用的完整程序集名称探测，兼容普通发布和单文件发布。</summary>
    public static bool TryCheckComponents(out string? detail)
    {
        try
        {
            var references = typeof(EmbeddedWebView).Assembly.GetReferencedAssemblies();
            foreach (var name in new[] { "Microsoft.Web.WebView2.Core", "Microsoft.Web.WebView2.Wpf" })
            {
                var reference = references.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                Assembly.Load(reference ?? new AssemblyName(name));
            }
            detail = null;
            return true;
        }
        catch (Exception ex) when (IsComponentLoadFailure(ex))
        {
            detail = ex.ToString();
            return false;
        }
    }

    public static bool IsComponentLoadFailure(Exception ex) =>
        ex is FileNotFoundException or FileLoadException or BadImageFormatException or DllNotFoundException or TypeLoadException
        || ex.InnerException != null && IsComponentLoadFailure(ex.InnerException);

    // 不能内联：否则 JIT 可能在调用方进入 try/catch 之前加载 SDK 类型。
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IEmbeddedWebView Create(Action<bool, string> navigationCompleted, bool enableDevTools = false) =>
        new WebViewImplementation(navigationCompleted, enableDevTools);

    private sealed class WebViewImplementation : IEmbeddedWebView
    {
        private readonly WebView2 _view;
        private readonly Action<bool, string> _navigationCompleted;
        private readonly bool _enableDevTools;
        private bool _disposed;
        private bool _subscribed;

        public WebViewImplementation(Action<bool, string> navigationCompleted, bool enableDevTools)
        {
            _navigationCompleted = navigationCompleted;
            _enableDevTools = enableDevTools;
            _view = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 560
            };
        }

        public FrameworkElement View => _view;

        public async Task InitializeAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _view.EnsureCoreWebView2Async();
            // 初始化期间页面可能已经切换。不能访问已释放的 CoreWebView2，也不能复活页面。
            ObjectDisposedException.ThrowIf(_disposed, this);
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _view.CoreWebView2.Settings.AreDevToolsEnabled = _enableDevTools;
            _view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _subscribed = true;
        }

        public void Navigate(string url)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _view.CoreWebView2.Navigate(url);
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!_disposed) _navigationCompleted(e.IsSuccess, e.WebErrorStatus.ToString());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_subscribed && _view.CoreWebView2 != null)
                    _view.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
            finally
            {
                _subscribed = false;
                _view.Dispose();
            }
        }
    }
}
