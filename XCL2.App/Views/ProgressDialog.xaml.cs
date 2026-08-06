using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 通用"正在下载/安装/扫描..."进度弹窗，被下载中心、Java 安装、皮肤补丁安装、
/// 整合包导入导出等十几处场景复用。
///
/// 迁移记录：原来是独立 Window（ProgressWindow），用 win.Show() 弹出、后台任务跑完后
/// win.Close() 关掉，中间全程非模态（不调用 ShowDialog，不阻塞调用方）。现在改成
/// Overlay 弹窗后，为了让原来遍布十几个文件的 "new ProgressWindow(...); Show(); ... Close();"
/// 调用点尽量不用大改，这里特意保留了同名的 Show()/Close() 方法作为兼容外壳——
/// 内部实际调用的是 OverlayDialogService.ShowNonModal/RequestSelfClose，
/// 效果上完全等价（显示到 Overlay 层 / 从 Overlay 层摘除），只是不再对应真正的
/// Win32 窗口。
/// </summary>
public partial class ProgressDialog : OverlayDialogControl
{
    // 修复"切换大界面时下载弹窗还在"：原 ProgressWindow 是独立顶层窗口，跟发起下载的
    // Page 实例没有强绑定，用户中途切换主界面页面时旧弹窗不会跟着消失。迁移成 Overlay
    // 后弹窗本身已经是 MainWindow 可视化树的一部分，理论上主界面整体还在、Overlay 也还在，
    // 但"切换页面时应该打断旧的进度提示"这个产品语义不变，继续用静态注册表 + CloseAll()
    // 保留这个行为，调用方式（MainWindow 在切换主界面前调用 ProgressDialog.CloseAll()）
    // 完全不用改。
    private static readonly List<ProgressDialog> _liveDialogs = new();

    public IProgress<ProgressInfo> Progress { get; }

    private bool _isShowing;

    public ProgressDialog(string title)
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
    }

    /// <summary>兼容原 Window.Show() 调用习惯：把自己显示到 Overlay 层，非模态，
    /// 不阻塞调用方后续代码（后台下载/安装 Task 该怎么跑还怎么跑）。</summary>
    // 'new'：基类 OverlayDialogControl 现在也有同名的 Window 兼容成员（见 IOverlayDialog.cs），
    // ProgressDialog 这两个是更早就写好的、语义相同但实现更贴合进度条场景的版本，
    // 显式加 new 表明是有意隐藏基类实现，不是漏写（否则编译器会报 CS0108 警告）。
    public new void Show()
    {
        if (_isShowing) return;
        _isShowing = true;
        _liveDialogs.Add(this);
        OverlayDialogService.ShowNonModal(this, dismissOnBackgroundClick: false);
    }

    /// <summary>兼容原 Window.Close() 调用习惯：把自己从 Overlay 层摘除。</summary>
    public new void Close()
    {
        if (!_isShowing) return;
        _isShowing = false;
        _liveDialogs.Remove(this);
        CloseWith(null);
    }

    /// <summary>关闭当前所有存活的下载/安装类进度弹窗。在主界面切换前调用，避免
    /// 旧页面遗留的进度弹窗继续悬浮在 Overlay 层上。</summary>
    public static void CloseAll()
    {
        foreach (var d in _liveDialogs.ToArray())
        {
            try { d.Close(); } catch { /* 可能已在关闭中，忽略 */ }
        }
    }
}
