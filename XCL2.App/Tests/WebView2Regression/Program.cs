using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Views.Tools;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--child")
            {
                TestControls(args[1] != "complete");
                return 0;
            }

            // 每种情况在独立进程里跑，避免此前已经加载的程序集掩盖缺 DLL 的问题。
            foreach (var removed in new[] { "complete", "Microsoft.Web.WebView2.Wpf.dll", "Microsoft.Web.WebView2.Core.dll" })
                RunIsolated(removed);
            Console.WriteLine("PASS: WebView2 正常依赖、缺 Wpf.dll、缺 Core.dll；真实 XAML 页面创建/反复卸载/失败提示。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunIsolated(string removed)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "xcl2-webview2-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(source), removed, StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(fixture, Path.GetRelativePath(AppContext.BaseDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            var executable = Path.Combine(fixture, "WebView2Regression.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = fixture };
            start.ArgumentList.Add("--child");
            start.ArgumentList.Add(removed);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动测试子进程。");
            if (!child.WaitForExit(30000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new InvalidOperationException("测试超时：" + removed);
            }
            Check(child.ExitCode == 0, "隔离依赖测试失败：" + removed);
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void TestControls(bool expectMissing)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["PrimaryButton"] = new Style(typeof(Button));
        Check(EmbeddedWebView.TryCheckComponents(out var detail) != expectMissing, "组件探测结果错误：" + detail);
        // 即使 Core.dll 缺失，探测方法也应在 SDK 调用边界内部处理错误。
        _ = WebView2RuntimeDetector.IsAvailable();

        for (var i = 0; i < 8; i++)
        {
            var seed = new SeedStructureQueryTool();
            var web = new Web2BrowserTool();
            seed.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            web.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

            if (expectMissing)
            {
                ((TextBox)seed.FindName("SeedBox")).Text = "12345";
                AssertMissingComponents(seed, "OpenMapButton", "SeedQueryStatusText");
                AssertMissingComponents(web, "OpenEmbeddedButton", "Web2StatusText");
            }

            AssertCleanup(seed);
            AssertCleanup(web);
        }
        app.Shutdown();
    }

    private static void AssertMissingComponents(UserControl control, string buttonName, string statusName)
    {
        var button = (Button)control.FindName(buttonName);
        var warnings = ToastService.Warnings.Count;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!button.IsEnabled, "缺组件后应禁用内嵌按钮，避免重复尝试。");
        Check(((TextBlock)control.FindName(statusName)).Text.Contains("完整发布包"), "缺组件后没有清晰的修复说明。");
        Check(ToastService.Warnings.Count == warnings + 1, "首次缺组件应该有气泡提醒。");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ToastService.Warnings.Count == warnings + 1, "重复请求不应反复显示气泡。");
    }

    private static void AssertCleanup(UserControl control)
    {
        var fake = new FakeBrowser();
        control.GetType().GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, fake);
        ((Grid)control.FindName("BrowserHost")).Children.Add(fake.View);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(fake.DisposeCount == 1, "卸载时浏览器应仅释放一次。");
        Check(((Grid)control.FindName("BrowserHost")).Children.Count == 0, "卸载后浏览器视图应被移除。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeBrowser : IEmbeddedWebView
    {
        public FrameworkElement View { get; } = new Border();
        public int DisposeCount { get; private set; }
        public Task InitializeAsync() => Task.CompletedTask;
        public void Navigate(string url) { }
        public void Dispose() => DisposeCount++;
    }
}
