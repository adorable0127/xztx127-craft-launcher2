using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>让已排队的 Loaded、布局和一次合成提交先完成；不在 Rendering 回调中执行页面业务。</summary>
public static class UiRenderScheduler
{
    public static Task<bool> AfterRenderAsync(FrameworkElement element, CancellationToken cancellationToken = default)
    {
        element.Dispatcher.VerifyAccess();
        if (cancellationToken.IsCancellationRequested || element.Dispatcher.HasShutdownStarted)
            return Task.FromResult(false);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = element.Dispatcher;
        var finished = false;
        var armed = false;
        Window? window = null;
        DispatcherOperation? continuation = null;
        CancellationTokenRegistration registration = default;
        var fallback = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(250) };

        void Finish(bool rendered)
        {
            if (finished) return;
            finished = true;
            CompositionTarget.Rendering -= Rendered;
            element.Loaded -= Loaded;
            element.Unloaded -= Unloaded;
            element.IsVisibleChanged -= VisibilityChanged;
            dispatcher.ShutdownStarted -= Shutdown;
            if (window != null) window.StateChanged -= WindowStateChanged;
            fallback.Stop();
            fallback.Tick -= TimedOut;
            if (continuation?.Status == DispatcherOperationStatus.Pending) continuation.Abort();
            registration.Dispose();
            completion.TrySetResult(rendered && !cancellationToken.IsCancellationRequested);
        }
        void Rendered(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= Rendered;
            // Rendering 位于提交之前；只排续步，先让本次布局/合成调用栈返回。
            continuation = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => Finish(true)));
        }
        void Arm()
        {
            if (finished || armed || !element.IsLoaded) return;
            window = Window.GetWindow(element);
            if (!element.IsVisible || window?.WindowState == WindowState.Minimized)
            {
                Finish(false);
                return;
            }
            armed = true;
            if (window != null) window.StateChanged += WindowStateChanged;
            CompositionTarget.Rendering += Rendered;
            fallback.Start();
        }
        void Loaded(object sender, RoutedEventArgs e) => Arm();
        void Unloaded(object sender, RoutedEventArgs e) => Finish(false);
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (armed && !(bool)e.NewValue) Finish(false);
        }
        void WindowStateChanged(object? sender, EventArgs e)
        {
            if (window?.WindowState == WindowState.Minimized) Finish(false);
        }
        void Shutdown(object? sender, EventArgs e) => Finish(false);
        void TimedOut(object? sender, EventArgs e) => Finish(false);

        element.Loaded += Loaded;
        element.Unloaded += Unloaded;
        element.IsVisibleChanged += VisibilityChanged;
        dispatcher.ShutdownStarted += Shutdown;
        fallback.Tick += TimedOut;
        registration = cancellationToken.Register(() =>
        {
            if (dispatcher.CheckAccess()) Finish(false);
            else if (!dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Finish(false)));
        });
        if (finished) registration.Dispose();
        else
        {
            // 连从未挂载的元素也有界结束，避免 ShutdownStarted 长期保留准备状态。
            fallback.Start();
            Arm();
        }
        return completion.Task;
    }
}
