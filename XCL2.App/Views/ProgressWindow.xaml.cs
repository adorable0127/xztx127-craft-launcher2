using System.Collections.Generic;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ProgressWindow : Window
{
    // 修复"切换大界面时下载窗口还在"：ProgressWindow 是独立弹出的顶层窗口，跟发起
    // 下载的 Page 实例没有强绑定——用户在下载途中把 MainWindow 的 MainContent 切到
    // 另一个页面时，旧 Page 实例被替换掉了，但它 new 出来的这个 ProgressWindow 是
    // 单独 Show() 的顶层窗口，不会跟着消失，会一直悬浮在桌面上。
    // 用静态注册表记录所有当前存活的实例，构造时注册、关闭时自动注销；
    // MainWindow 在每次切换主界面前调用 CloseAll()，保证"打断操作后旧窗口应该消失"。
    private static readonly List<ProgressWindow> _liveWindows = new();

    public IProgress<ProgressInfo> Progress { get; }

    public ProgressWindow(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
        Progress = new System.Progress<ProgressInfo>(info =>
        {
            Dispatcher.Invoke(() =>
            {
                var pct = info.Total > 0 ? (double)info.Done / info.Total * 100 : 0;
                Bar.Value = Math.Min(100, pct);
                DetailText.Text = $"{info.Stage}: {info.Done}/{info.Total}  {info.CurrentFile}";
            });
        });

        _liveWindows.Add(this);
        Closed += (_, _) => _liveWindows.Remove(this);
    }

    /// <summary>关闭当前所有存活的下载/安装类进度窗口。在主界面切换前调用，避免
    /// 旧页面遗留的进度弹窗继续悬浮在桌面上。</summary>
    public static void CloseAll()
    {
        foreach (var w in _liveWindows.ToArray())
        {
            try { w.Close(); } catch { /* 可能已在关闭中，忽略 */ }
        }
    }
}
