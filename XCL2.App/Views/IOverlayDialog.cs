using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>
/// 进程内弹窗（Overlay 弹窗）统一契约。
///
/// 背景 / 为什么要有这个接口：
/// 迁移前，每个弹窗是独立的 <see cref="System.Windows.Window"/> 子类，天然自带
/// DialogResult、ShowDialog()、Close()、Owner 这些 WPF 内置能力。迁移成内嵌
/// UserControl 之后这些能力全部消失——UserControl 没有"关闭"和"返回值"的概念，
/// 需要我们自己补一套等价物，否则 24 个弹窗迁移完之后每个调用点都要单独发明一套
/// "怎么知道弹窗关掉了、用户点的是确定还是取消"的机制，会比原来的独立 Window 更乱。
///
/// 这个接口就是那套"等价物"的最小集合，只规定两件事：
/// 1. 弹窗自己知道什么时候该关闭——通过触发 <see cref="RequestClose"/> 事件，
///    携带一个 bool? 结果（语义完全对应原来的 Window.DialogResult：
///    true=确定/成功，false=取消，null=直接叉掉/无结果地关闭）。
///    弹窗内部原来写"DialogResult = true; Close();"的地方，现在改成
///    "RequestClose?.Invoke(this, true);"，就是这么大的改动量，其余业务逻辑
///    （校验、赋值输出属性等）完全不用动。
/// 2. 宿主（OverlayDialogService）负责监听这个事件、把 UserControl 从
///    OverlayContentHost 里摘掉、隐藏遮罩、把结果通过 Task&lt;bool?&gt; 或者
///    嵌套 DispatcherFrame 的方式传回调用方——这部分是一次性写好、所有弹窗共用的
///    宿主逻辑，不需要每个弹窗自己关心"怎么被移除"。
///
/// 不强制弹窗基类必须继承某个抽象 UserControl 子类（比如 OverlayDialogBase），
/// 是有意的：项目里已有的 UserControl 弹窗如果将来想复用这套机制，只需要实现这一个
/// 接口，不需要改变继承链，兼容性更好、侵入性更小。
/// </summary>
public interface IOverlayDialog
{
    /// <summary>弹窗请求关闭时触发，携带的 bool? 语义等价于原 Window.DialogResult：
    /// true=确定，false=取消，null=无结果关闭（比如按 Esc 或点遮罩）。</summary>
    event EventHandler<bool?>? RequestClose;
}

/// <summary>
/// <see cref="IOverlayDialog"/> 的一个方便基类：大多数迁移后的弹窗直接继承这个，
/// 只需要在"确定/取消"按钮的 Click 里调用 <see cref="CloseWith"/> 即可，
/// 不用自己声明和触发事件。
/// </summary>
public class OverlayDialogControl : UserControl, IOverlayDialog
{
    public event EventHandler<bool?>? RequestClose;

    /// <summary>请求宿主关闭当前弹窗并带上结果，等价于原来的
    /// "DialogResult = xxx; Close();"两行。</summary>
    protected void CloseWith(bool? result) => RequestClose?.Invoke(this, result);

    // ==================== Window 兼容层 ====================
    //
    // 16 个原本是独立 Window 的弹窗迁移到 Overlay 之后，全项目有 30 多个调用点在用
    // 熟悉的 Window 三件套：`dlg.ShowDialog()`、`dlg.Show()`、`dlg.Close()`。
    //
    // 与其把这 30 多处逐个改写成 OverlayDialogService.ShowModal(dlg)（改动面大、
    // 容易漏、而且以后每加一个弹窗都要记得用新写法），不如在基类上补三个同名成员，
    // 让原有调用写法**原样继续可用**。这样迁移对调用方是完全透明的：
    // 调用点一行都不用动，行为从"弹出一个独立的系统窗口"变成"在主窗口内部弹出遮罩层"。
    //
    // 三个成员的语义跟 Window 上的同名成员一一对应：
    //   ShowDialog() → 模态，阻塞到关闭，返回 bool?（对应原 DialogResult）
    //   Show()       → 非模态，立即返回
    //   Close()      → 关闭自己（结果为 null，等价于原来"直接叉掉窗口"）
    //
    // Close() 必须是 public：DeviceCodeWindow / MicrosoftLoginWindow 这类弹窗
    // 是由**外部**代码（登录流程拿到 token 之后）调用 popup.Close() 关掉的，
    // 而 CloseWith 是 protected，外部够不着。

    /// <summary>模态显示并等待结果，等价于原 Window.ShowDialog()。</summary>
    public bool? ShowDialog() => OverlayDialogService.ShowModal(this);

    /// <summary>非模态显示，等价于原 Window.Show()。</summary>
    public void Show() => OverlayDialogService.ShowNonModal(this);

    /// <summary>关闭自己，等价于原 Window.Close()（结果为 null）。</summary>
    public void Close() => RequestClose?.Invoke(this, null);
}
