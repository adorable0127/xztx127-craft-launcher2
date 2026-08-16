using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XCL2.App.Views;

/// <summary>
/// 进程内弹窗（Overlay）宿主服务。
///
/// ===== 整体设计 =====
/// 迁移前：项目里 24 个弹窗都是独立的 System.Windows.Window 子类，每个都是一个真正的
/// Win32 顶层窗口，会在任务栏/Alt-Tab 里单独出现、有自己的标题栏、可以被拖到主窗口
/// 外面、甚至可以最小化后主窗口还在——这些跟"一体化启动器"（参照 PCL2 等主流第三方
/// 启动器的做法）的体验是矛盾的，所以要把它们全部改成挂在 MainWindow 内部的遮罩层
/// 弹出层（Overlay），行为上更接近网页/移动端 App 里的"Modal"，而不是操作系统层面的
/// 独立窗口。
///
/// 迁移后：每个原来的 Window 子类被拆成一个 UserControl（实现 IOverlayDialog，
/// 见 IOverlayDialog.cs 顶部注释），通过这个服务显示到 MainWindow.xaml 里预先
/// 搭好的 OverlayRoot/OverlayScrim/OverlayCard/OverlayContentHost 几个元素中
/// （见 MainWindow.xaml 对应位置的注释）。
///
/// ===== 为什么弹窗会自动跟随皮肤（配色方案）=====
/// 不需要任何额外代码。原因：
/// 1. 所有弹窗 UserControl 的 XAML 用的都是 {DynamicResource XxxBrush}（PanelBrush/
///    AccentBrush/TextPrimaryBrush 等），跟原来独立 Window 版本用的是完全同一套画刷
///    Key，没有改名字、没有另起一套弹窗专属配色。
/// 2. ThemeService.Apply 切换皮肤时，是直接替换 Application.Current.Resources 里
///    这些画刷 Key 对应的颜色（见 ThemeService.SetBrushColor），是应用级别的全局资源，
///    不是挂在某个特定 Window 底下的资源。
/// 3. Overlay 弹窗现在是 MainWindow 可视化树的一部分（挂在 OverlayContentHost 下面），
///    跟主窗口内容区共享同一个 Application.Resources 查找链，所以 DynamicResource
///    在皮肤切换时会像主窗口其它任何控件一样自动重新取值——不需要给弹窗单独接线。
///    这跟原来"独立 Window 也要靠 ThemeService.RefreshOpenWindows 遍历所有已打开窗口
///    强制刷新"是同一个道理，现在弹窗生命周期更短（用完即摘除），效果一样。
///
/// ===== 兼容旧调用写法 =====
/// 原来的调用点几乎全部是这个模式（同步阻塞、直接判断返回值）：
///     var dlg = new XxxWindow(...) { Owner = ... };
///     if (dlg.ShowDialog() != true) return;
///     ...用 dlg.SomeOutputProperty...
///
/// 为了让 24 个调用点尽量少改代码（只改"new 出来的是什么、怎么等结果"这两行，
/// 中间的业务逻辑、输出属性读取全部不用动），这个服务提供 ShowModal&lt;TDialog&gt;：
/// - 接收一个"构造好的弹窗 UserControl 实例"（调用方仍然用 new XxxDialog(...) 传参数，
///   跟以前一模一样，只是 XxxWindow 变成 XxxDialog、不再需要 Owner=）。
/// - 内部通过嵌套 DispatcherFrame（WPF 标准的"局部消息泵"技术，ShowDialog() 本身也是
///   基于这个机制实现的）实现"看起来是同步阻塞、实际不冻结 UI 线程消息循环"的效果，
///   弹窗关闭前这个方法不会返回，跟原来 ShowDialog() 的调用体验完全一致。
/// - 返回值是 bool?，语义对应 IOverlayDialog.RequestClose 传出的结果，调用方原来怎么
///   判断 dlg.ShowDialog() != true，现在就怎么判断 OverlayDialogService.ShowModal(...)
///   != true，等号两边类型不变。
///
/// 如果个别新代码更适合用现代 async/await 风格，也可以用 ShowModalAsync&lt;TDialog&gt;，
/// 两者共用同一套内部实现（ShowModal 只是在 ShowModalAsync 外面包了一层
/// DispatcherFrame 等待）。
///
/// ===== 弹窗堆叠（弹窗里再弹弹窗）=====
/// 少数场景需要在一个弹窗内再弹出另一个弹窗（比如"新手向导"里途中调出"选择 Java"）。
/// 这里用一个 Stack&lt;OverlayEntry&gt; 记录层级：新弹窗显示时把当前弹窗内容"暂存"
/// 而不是丢弃，新弹窗关闭后自动恢复上一层，视觉上通过卡片轻微缩放动画区分层级变化，
/// 避免"看起来完全换了一个弹窗、以为上一个已经关掉"的误解。
/// </summary>
public static class OverlayDialogService
{
    private sealed class OverlayEntry
    {
        public required object Content;
        public required TaskCompletionSource<bool?> Tcs;
        public required bool DismissOnBackgroundClick;
        /// <summary>是否允许按 Esc 关闭本弹窗。默认 true（跟系统对话框习惯一致）；
        /// 法律协议等"不允许跳过"的弹窗传 false 锁死 Esc，避免用户一个按键
        /// 就把必须显式表态的流程强行关掉。</summary>
        public required bool DismissOnEsc;
    }

    private static MainWindow? _host;
    private static readonly Stack<OverlayEntry> _stack = new();

    /// <summary>MainWindow 构造完成后调用一次，把自己注册为宿主。整个进程只有一个
    /// MainWindow 实例，不需要支持多宿主。</summary>
    public static void Register(MainWindow host) => _host = host;

    /// <summary>
    /// 显示一个进程内弹窗并同步阻塞等待结果（兼容原 Window.ShowDialog() 的调用习惯）。
    /// dismissOnBackgroundClick：点击遮罩（弹窗卡片以外区域）是否等价于取消关闭。
    ///
    /// 需求："能不能让所有弹窗都不可以点击灰色区域退出？"——默认值从 true 改成 false。
    /// 原来的设计意图是"多数确认框/选择框允许点外面取消，少数必须显式选择的弹窗
    /// （比如首次启动向导）单独传 false"，但这类小尺寸弹窗在桌面端很容易被"点到卡片
    /// 外面一点点"误触，跟原生 Window 的独立弹窗（点外面完全无效，只能点按钮/Alt+F4）
    /// 体验不一致，容易造成用户没注意到就把弹窗关掉、丢失已经填好的内容。统一改成默认
    /// 不允许点遮罩关闭，弹窗只能通过"点弹窗自己的按钮"或者 Esc 键关闭（Esc 关闭走的是
    /// MainWindow.PreviewKeyDown 里对 OverlayDialogService.RequestDismissTopByEscape
    /// 的调用，跟这里的 dismissOnBackgroundClick 是两套独立开关，不受这次改动影响，
    /// 仍然保留"按 Esc 可以关闭"这个符合系统对话框习惯的退出路径）。
    /// 参数仍然保留（没有直接删掉“背景点击可关闭”这条能力）：以后如果某个具体弹窗
    /// 确实需要"点外面 = 取消"这种体验（比如某个纯展示性的气泡提示），调用方可以在
    /// 各自的调用点显式传 true 单独开启，不需要再改这个服务本身。
    /// </summary>
    public static bool? ShowModal(IOverlayDialog dialog, bool dismissOnBackgroundClick = false, bool dismissOnEsc = true)
    {
        if (_host is null)
            throw new InvalidOperationException("OverlayDialogService 尚未 Register 宿主 MainWindow。");

        var tcs = new TaskCompletionSource<bool?>();
        var entry = new OverlayEntry { Content = dialog, Tcs = tcs, DismissOnBackgroundClick = dismissOnBackgroundClick, DismissOnEsc = dismissOnEsc };

        void OnRequestClose(object? s, bool? result) => CloseTop(result);
        dialog.RequestClose += OnRequestClose;

        Push(entry, () => dialog.RequestClose -= OnRequestClose);

        // 局部消息泵：跟 Window.ShowDialog() 内部机制一致，这里手动跑一个 DispatcherFrame，
        // 在弹窗关闭（tcs 完成）之前一直"假装同步阻塞"，实际上 UI 线程消息循环没有真的
        // 停止，用户仍然能跟 Overlay 内容交互、动画能正常播放。
        var frame = new DispatcherFrame();
        tcs.Task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);

        return tcs.Task.Result;
    }

    /// <summary>ShowModal 的 async/await 版本，供不需要兼容旧同步调用写法的新代码使用。
    /// dismissOnBackgroundClick 默认值同 ShowModal，见该方法上的注释。</summary>
    public static Task<bool?> ShowModalAsync(IOverlayDialog dialog, bool dismissOnBackgroundClick = false, bool dismissOnEsc = true)
    {
        if (_host is null)
            throw new InvalidOperationException("OverlayDialogService 尚未 Register 宿主 MainWindow。");

        var tcs = new TaskCompletionSource<bool?>();
        var entry = new OverlayEntry { Content = dialog, Tcs = tcs, DismissOnBackgroundClick = dismissOnBackgroundClick, DismissOnEsc = dismissOnEsc };

        void OnRequestClose(object? s, bool? result) => CloseTop(result);
        dialog.RequestClose += OnRequestClose;

        Push(entry, () => dialog.RequestClose -= OnRequestClose);

        return tcs.Task;
    }

    /// <summary>非模态、无返回值的纯展示型 Overlay（极少数场景，比如设备码登录窗口那种
    /// "只是展示信息，用户自己去外部完成操作后再手动关闭"的情况）。dismissOnBackgroundClick
    /// 默认值同 ShowModal（默认 false，不允许点遮罩关闭），仍然支持 Esc 关闭，只是不强制
    /// 调用方等待结果。</summary>
    public static void ShowNonModal(IOverlayDialog dialog, bool dismissOnBackgroundClick = false, bool dismissOnEsc = true)
    {
        _ = ShowModalAsync(dialog, dismissOnBackgroundClick, dismissOnEsc);
    }

    private static readonly Dictionary<object, Action> _unsubscribers = new();

    private static void Push(OverlayEntry entry, Action unsubscribe)
    {
        _unsubscribers[entry.Content] = unsubscribe;

        // 已经有弹窗在显示：先把当前这个"藏起来"（从 ContentHost 摘掉但保留在栈里），
        // 新弹窗压栈显示，形成"弹窗里弹弹窗"的层级观感。
        _stack.Push(entry);
        _host!.OverlayRenderEntry(entry.Content, animateIn: true);
    }

    /// <summary>关闭当前最顶层的弹窗，把结果回传给对应的 TaskCompletionSource，
    /// 并恢复显示上一层（如果有的话），否则收起整个 OverlayRoot。</summary>
    private static void CloseTop(bool? result)
    {
        if (_stack.Count == 0) return;

        var top = _stack.Pop();
        if (_unsubscribers.Remove(top.Content, out var unsubscribe)) unsubscribe();

        _host!.OverlayDismissEntry(top.Content, () =>
        {
            top.Tcs.TrySetResult(result);

            if (_stack.Count > 0)
            {
                var previous = _stack.Peek();
                _host.OverlayRenderEntry(previous.Content, animateIn: false);
            }
            else
            {
                _host.OverlayHideRoot();
            }
        });
    }

    /// <summary>供 MainWindow 里遮罩点击事件调用：只有当前最顶层弹窗允许"点遮罩关闭"时
    /// 才生效，返回 null 结果（对应原来 Window 的"没有点确定/取消、直接叉掉"语义）。</summary>
    internal static void RequestDismissTopByBackgroundClick()
    {
        if (_stack.Count == 0) return;
        if (!_stack.Peek().DismissOnBackgroundClick) return;
        CloseTop(null);
    }

    /// <summary>供 MainWindow 里 Esc 按键处理调用，语义同上。多数弹窗允许 Esc 取消，
    /// 跟 Windows 系统对话框的通行习惯一致；但个别"必须显式表态、不允许跳过"的弹窗
    /// （例如首次启动的协议页）通过 ShowModal 的 dismissOnEsc: false 锁死 Esc，
    /// 此时按 Esc 不做任何事——修复"按 Esc 会强行关闭协议窗口"这类问题。</summary>
    internal static void RequestDismissTopByEscape()
    {
        if (_stack.Count == 0) return;
        if (!_stack.Peek().DismissOnEsc) return;
        CloseTop(null);
    }

    internal static bool HasActiveOverlay => _stack.Count > 0;
}
