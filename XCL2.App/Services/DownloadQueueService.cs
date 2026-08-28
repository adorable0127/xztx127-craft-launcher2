using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using XCL2.App.Views;

namespace XCL2.App.Services;

public enum DownloadQueueItemStatus
{
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// 标题栏下载列表里的单个条目。暂停/继续 不是真正的"字节级暂停"（那需要把
/// DownloadService 内部按 chunk 写盘的循环整个改成可挂起状态机，改动面太大、
/// 风险也高），而是复用 DownloadService 本来就有的行为："文件已存在且校验通过
/// 就跳过，不重新下载"——所以这里的"暂停"实际是：取消当前正在跑的这次下载
/// （已经完整下载并校验通过的文件不会被删，只有正在写的那个 .part 分片会被
/// DownloadService 自己清理掉），"继续"则是用同样的参数重新调用一次安装方法，
/// 已经装好的部分会被跳过，效果上等价于"从中断点继续"，只是没有字节级精确续传。
/// </summary>
public sealed class DownloadQueueItem : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnChanged(); }
    }

    private string _statusText = "等待中...";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnChanged(); }
    }

    private DownloadQueueItemStatus _status = DownloadQueueItemStatus.Downloading;
    public DownloadQueueItemStatus Status
    {
        get => _status;
        set { _status = value; OnChanged(); OnChanged(nameof(CanPause)); OnChanged(nameof(CanResume)); }
    }

    public bool CanPause => Status == DownloadQueueItemStatus.Downloading;
    public bool CanResume => Status == DownloadQueueItemStatus.Paused || Status == DownloadQueueItemStatus.Failed;

    /// <summary>驱动本次下载的取消令牌源。暂停/取消都通过 Cancel() 它来打断当前
    /// await 中的下载调用；暂停之后如果要继续，会 new 一个新的 Cts 重新跑。</summary>
    internal CancellationTokenSource Cts;

    /// <summary>重新发起这个条目对应的下载调用（拿新的 CancellationToken 以及条目自身，
    /// 后者主要是方便调用方拿 CreateProgress() 把进度接回来），由 DownloadCenterPage、
    /// BedrockPage 等发起下载的地方在注册条目时提供，这里不关心具体是"游戏版本"
    /// "整合包""基岩版客户端"还是别的什么类型的下载。</summary>
    internal readonly Func<DownloadQueueItem, CancellationToken, Task> ResumeAction;

    public DownloadQueueItem(string name, CancellationTokenSource cts, Func<DownloadQueueItem, CancellationToken, Task> resumeAction)
    {
        Name = name;
        Cts = cts;
        ResumeAction = resumeAction;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>把这个条目包成一个 IProgress&lt;ProgressInfo&gt;，方便直接传给下载/解压/安装
    /// 这类本来就接受 ProgressInfo 回调的方法（DownloadService、BedrockClientDownloadService、
    /// BedrockContentService 等全项目统一用的这个类型），不需要调用方自己再手写一遍
    /// "算百分比 + 切 UI 线程 + 更新 StatusText" 的样板代码。</summary>
    public IProgress<ProgressInfo> CreateProgress() => new Progress<ProgressInfo>(info =>
    {
        var pct = info.Total > 0 ? (double)info.Done / info.Total * 100 : 0;
        ProgressPercent = pct;
        StatusText = info.Total > 0
            ? $"{info.Stage} {info.Done}/{info.Total}" + (string.IsNullOrEmpty(info.CurrentFile) ? "" : $" {info.CurrentFile}")
            : info.Stage;
    });
}

/// <summary>
/// 全局单例：所有"游戏版本下载"任务在这里登记，驱动标题栏的下载按钮+下拉列表。
/// 暂停/取消这两个操作被明确设计成跟"启动游戏"按钮完全独立——启动游戏走的是
/// LauncherService 自己的即时补全下载逻辑，不经过这个队列，两者互不阻塞，
/// 所以"下载中依然可以点启动"这条需求不需要额外加锁，天然成立。
/// </summary>
public sealed class DownloadQueueService
{
    public static DownloadQueueService Instance { get; } = new();

    public ObservableCollection<DownloadQueueItem> Items { get; } = new();

    public bool HasActive => Items.Any(i => i.Status is DownloadQueueItemStatus.Downloading or DownloadQueueItemStatus.Paused);

    private DownloadQueueService() { }

    /// <summary>登记一个新的下载条目并立即开始跑。调用方只需要把"怎么下载"包成
    /// resumeAction，其余的状态流转、异常吞掉、UI 线程调度都在这里统一处理，
    /// 不需要每个调用点各自重复一遍 try/catch。</summary>
    public DownloadQueueItem StartNew(string name, Func<DownloadQueueItem, CancellationToken, Task> resumeAction)
    {
        var cts = new CancellationTokenSource();
        var item = new DownloadQueueItem(name, cts, resumeAction);
        RunOnUi(() => Items.Add(item));
        _ = RunAsync(item);
        return item;
    }

    private async Task RunAsync(DownloadQueueItem item)
    {
        RunOnUi(() => { item.Status = DownloadQueueItemStatus.Downloading; item.StatusText = "下载中..."; });
        try
        {
            await item.ResumeAction(item, item.Cts.Token);
            RunOnUi(() =>
            {
                item.Status = DownloadQueueItemStatus.Completed;
                item.StatusText = "已完成";
                item.ProgressPercent = 100;
            });
            // 完成的条目不用一直占着下拉列表，几秒后自动摘除，跟大多数下载器"完成后自动
            // 从进行中列表消失"的习惯一致；用户如果手速快想看结果，这几秒足够看到"已完成"。
            await Task.Delay(TimeSpan.FromSeconds(4));
            RunOnUi(() => Items.Remove(item));
        }
        catch (OperationCanceledException)
        {
            // Cancel() 方法会先把 Status 置为 Canceled 再触发取消，这里如果发现已经是
            // "暂停"状态说明是 Pause() 打断的，不是真正取消，不应该被这段兜底逻辑覆盖状态；
            // 真正的取消(Canceled)由 Cancel() 自己负责从列表移除，这里也不重复处理。
            RunOnUi(() =>
            {
                if (item.Status == DownloadQueueItemStatus.Downloading)
                {
                    item.Status = DownloadQueueItemStatus.Paused;
                    item.StatusText = "已暂停";
                }
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                item.Status = DownloadQueueItemStatus.Failed;
                item.StatusText = $"失败：{ex.Message}";
            });
        }
        finally
        {
            // 下载完成后的通知处理
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                RunOnUi(() => ShowCompletionNotification(item));
            });
        }
    }

    private void ShowCompletionNotification(DownloadQueueItem item)
    {
        try
        {
            if (item.Status != DownloadQueueItemStatus.Completed) return;

            var cfg = (Application.Current?.MainWindow as MainWindow)?.ConfigService.Config;
            if (cfg == null) return;

            bool noPopup = false;
            if (item.Name.StartsWith("安装 ") && cfg.GameVersionNoPopup) noPopup = true;
            else if (item.Name.StartsWith("下载 ") && cfg.CommunityResourceNoPopup) noPopup = true;
            else if (item.Name.StartsWith("整合包 ") && cfg.ModpackNoPopup) noPopup = true;

            if (noPopup)
            {
                if (cfg.DownloadNotifyMode == 1) // 角标
                {
                    ToastService.ShowSuccess($"{item.Name} 完成");
                }
                else if (cfg.DownloadNotifyMode == 2) // Windows 通知
                {
                    ToastService.ShowSystemNotification($"{item.Name} 已完成", "下载完成");
                }
                // 3 = 不提示，什么都不做
            }
            else
            {
                ToastService.ShowSuccess($"{item.Name} 完成");
            }
        }
        catch { }
    }

    public void Pause(DownloadQueueItem item)
    {
        if (!item.CanPause) return;
        item.Cts.Cancel();
    }

    public void Resume(DownloadQueueItem item)
    {
        if (!item.CanResume) return;
        item.Cts = new CancellationTokenSource();
        _ = RunAsync(item);
    }

    public void Cancel(DownloadQueueItem item)
    {
        item.Status = DownloadQueueItemStatus.Canceled;
        item.Cts.Cancel();
        Items.Remove(item);
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher == null || app.Dispatcher.CheckAccess())
            action();
        else
            app.Dispatcher.Invoke(action);
    }
}
