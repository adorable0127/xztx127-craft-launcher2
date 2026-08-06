using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 主流程通知类弹窗的分类。跟原来 MessageBoxImage 的用途一样——只影响图标和一点点配色
/// 强调（比如 Error 用 DangerBrush 强调标题），不影响弹窗结构本身。
/// </summary>
public enum XclMessageKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// 按钮方案，对应原来 MessageBoxButton 里实际用到的几种。YesNoCancel 是后续补充的——
/// DownloadCenterPage.PromptSaveDirectory("是"=用默认目录/"否"=另选目录/"取消"=放弃下载)
/// 这个三态选择在最初写这个类时项目里还没有实际调用点，所以当时没实现；现在这个是唯一
/// 用到三态的地方，才补上，避免引入用不到的复杂度这条原则被违反。
/// </summary>
public enum XclMessageButtons
{
    OK,
    OKCancel,
    YesNo,
    YesNoCancel,
}

/// <summary>
/// YesNoCancel 弹窗的结果——三态场景下 bool? 已经不够用（true/false/null 三个值全部
/// 有实际业务含义：是/否/取消，而不是"取消=没有点任何按钮就关闭"那种默认语义），
/// 所以单独定义一个结果枚举，跟 System.Windows.MessageBoxResult 的这三个成员同名，
/// 方便照着原来的 MessageBoxResult.Yes/No/Cancel 判断逻辑直接改写用法。
/// </summary>
public enum XclMessageResult
{
    Yes,
    No,
    Cancel,
}

/// <summary>
/// 通用消息提示弹窗 —— 进程内 Overlay 版本的 MessageBox.Show()替代品。
///
/// ===== 为什么要做这个 =====
/// 系统原生 MessageBox 是 Win32 对话框，样式完全由操作系统决定，不认识本项目的
/// PanelBrush/AccentBrush 这套皮肤系统——用户切换到粉色皮肤后，主界面全是粉色，
/// 结果"游戏已启动"这种最常见的提示弹窗跳出来却是一个跟皮肤毫不相关的系统灰白框，
/// 观感割裂，也是"一体化启动器"体验里最显眼的短板之一。这个类提供的静态方法
/// （Show/ShowInfo/ShowWarning/ShowError/ShowConfirm）签名尽量贴近原来的
/// MessageBox.Show()，方便逐步替换调用点。
///
/// ===== 使用建议（给下一个改动这份代码的人）=====
/// 项目里还有大量 MessageBox.Show(...) 调用点没有全部替换完——这是有意为之的分阶段
/// 迁移，不是遗漏：这一轮优先替换的是"游戏启动成功""需要登录账户""错误提示"
/// （ErrorPresenter.ShowFriendlyError，几乎是全项目错误弹窗的统一出口）以及"调试信息"
/// 相关的弹窗，因为这些是玩家最高频会看到的几类。其余分散在设置页/服务器管理等次要
/// 操作确认框里的 MessageBox.Show 调用，后续按同样的套路（把 MessageBox.Show(...) 换成
/// XclMessageBox.Show(...)，参数基本一一对应）逐步替换即可，不需要一次性改完。
/// （注：类名实际是 MessageBoxDialog，不是文档里早先设想的 XclMessageBox；调用点一律
/// 写 MessageBoxDialog.ShowXxx(...)，枚举名 XclMessageKind/XclMessageButtons/XclMessageResult
/// 保留 Xcl 前缀不受影响。）
/// 三态的 YesNoCancel（见 XclMessageButtons.YesNoCancel/XclMessageResult/ShowYesNoCancel）
/// 是后来在替换 DownloadCenterPage.PromptSaveDirectory 时才补上的，之前项目里没有
/// 实际调用点用到三态，所以最初没实现；这也是"按需补充，不预先引入用不到的复杂度"
/// 这套原则的一次实际应用。
/// </summary>
public partial class MessageBoxDialog : OverlayDialogControl
{
    /// <summary>用户点击的是哪个按钮（用于 YesNo/OKCancel 场景）。true=Yes/OK，
    /// false=No/Cancel，null=没有点任何按钮就关闭了（比如按 Esc/点遮罩）。</summary>
    public bool? Result { get; private set; }

    /// <summary>YesNoCancel 场景专用的三态结果，见 XclMessageResult 注释。只有用
    /// XclMessageButtons.YesNoCancel 弹出时才会被设置为 Yes/No/Cancel 三者之一；
    /// 用户按 Esc/点遮罩关闭（没有点任何按钮）时归类为 Cancel——跟原生 MessageBox
    /// 在 YesNoCancel 模式下"叉掉窗口"等价于点了 Cancel 的行为一致。</summary>
    public XclMessageResult Result3 { get; private set; } = XclMessageResult.Cancel;

    private MessageBoxDialog(string message, string title, XclMessageKind kind, XclMessageButtons buttons)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        IconText.Text = kind switch
        {
            XclMessageKind.Success => "✔",
            XclMessageKind.Warning => "⚠",
            XclMessageKind.Error => "✖",
            _ => "ℹ",
        };
        IconText.Foreground = kind switch
        {
            XclMessageKind.Success => (System.Windows.Media.Brush)FindResource("SuccessTextBrush"),
            XclMessageKind.Warning => (System.Windows.Media.Brush)FindResource("WarningTextBrush"),
            XclMessageKind.Error => (System.Windows.Media.Brush)FindResource("DangerBrush"),
            _ => (System.Windows.Media.Brush)FindResource("AccentBrush"),
        };

        BuildButtons(buttons);
    }

    private void BuildButtons(XclMessageButtons buttons)
    {
        switch (buttons)
        {
            case XclMessageButtons.OK:
                ButtonPanel.Children.Add(MakeButton("确定", true, isPrimary: true, isDefault: true));
                break;
            case XclMessageButtons.OKCancel:
                ButtonPanel.Children.Add(MakeButton("取消", false, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton("确定", true, isPrimary: true, isDefault: true));
                break;
            case XclMessageButtons.YesNo:
                ButtonPanel.Children.Add(MakeButton("否", false, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton("是", true, isPrimary: true, isDefault: true));
                break;
            case XclMessageButtons.YesNoCancel:
                // 三个按钮从左到右：取消/否/是——跟原生 MessageBox 在 Windows 上的
                // 从左到右顺序（是/否/取消）刻意不同，这里沿用本项目其它 OKCancel/YesNo
                // 场景"次要操作靠左、主操作靠右且带 PrimaryButton 高亮"的既有布局习惯，
                // 保持这个类内部风格统一，而不是逐字照搬系统对话框的布局。
                ButtonPanel.Children.Add(MakeButton3("取消", XclMessageResult.Cancel, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton3("否", XclMessageResult.No, isPrimary: false, isDefault: false));
                ButtonPanel.Children.Add(MakeButton3("是", XclMessageResult.Yes, isPrimary: true, isDefault: true));
                break;
        }
    }

    private Button MakeButton(string content, bool result, bool isPrimary, bool isDefault)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            Style = isPrimary ? (Style)FindResource("PrimaryButton") : (Style)FindResource("SecondaryButton"),
        };
        btn.Click += (_, _) =>
        {
            Result = result;
            CloseWith(result);
        };
        return btn;
    }

    /// <summary>YesNoCancel 专用的按钮工厂：跟 MakeButton 的差异只在于写入 Result3
    /// （三态）而不是 Result（bool?），并且 CloseWith 统一传 null——YesNoCancel 场景下
    /// 调用方应该读 Result3，不应该继续依赖 bool? 的 IOverlayDialog.RequestClose 语义,
    /// 这里传 null 只是满足 CloseWith 的签名要求，不代表"没有选择"。</summary>
    private Button MakeButton3(string content, XclMessageResult result, bool isPrimary, bool isDefault)
    {
        var btn = new Button
        {
            Content = content,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            Style = isPrimary ? (Style)FindResource("PrimaryButton") : (Style)FindResource("SecondaryButton"),
        };
        btn.Click += (_, _) =>
        {
            Result3 = result;
            CloseWith(null);
        };
        return btn;
    }

    // ===== 静态入口：签名尽量贴近 System.Windows.MessageBox.Show，方便替换调用点 =====

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information)。</summary>
    public static void ShowInfo(string message, string title = "提示") => Show(message, title, XclMessageKind.Info, XclMessageButtons.OK);

    /// <summary>ShowInfo 的 async/await 版本。专供已经身处 async 方法（尤其是
    /// await Task.Run(...) 之后的延续里）的调用点使用——见 MainWindow.
    /// ScanMinecraftFoldersInBackgroundAsync 上的注释：在异步延续内部调用
    /// ShowModal（内部手动 PushFrame 起一个局部消息泵）容易跟外层已经在排队的
    /// SynchronizationContext 延续相互竞争，导致弹窗关闭后 Overlay 没能正常收起、
    /// 卡住吃掉后续所有点击。这个方法改用 ShowModalAsync + await，不再嵌套消息泵。</summary>
    public static Task ShowInfoAsync(string message, string title = "提示")
        => ShowAsync(message, title, XclMessageKind.Info, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information)，
    /// 专用于"操作成功完成"类通知（游戏启动成功、下载完成等），图标用 ✔ 而不是 ℹ，
    /// 视觉上更明确地传达"这是好消息"。</summary>
    public static void ShowSuccess(string message, string title = "成功") => Show(message, title, XclMessageKind.Success, XclMessageButtons.OK);

    /// <summary>ShowSuccess 的 async/await 版本，同 ShowInfoAsync 的适用场景。</summary>
    public static Task ShowSuccessAsync(string message, string title = "成功")
        => ShowAsync(message, title, XclMessageKind.Success, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning)。</summary>
    public static void ShowWarning(string message, string title = "提示") => Show(message, title, XclMessageKind.Warning, XclMessageButtons.OK);

    /// <summary>ShowWarning 的 async/await 版本，同 ShowInfoAsync 的适用场景。</summary>
    public static Task ShowWarningAsync(string message, string title = "提示")
        => ShowAsync(message, title, XclMessageKind.Warning, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error)。</summary>
    public static void ShowError(string message, string title = "出了点问题") => Show(message, title, XclMessageKind.Error, XclMessageButtons.OK);

    /// <summary>ShowError 的 async/await 版本，同 ShowInfoAsync 的适用场景。</summary>
    public static Task ShowErrorAsync(string message, string title = "出了点问题")
        => ShowAsync(message, title, XclMessageKind.Error, XclMessageButtons.OK);

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)，
    /// 返回 true 表示用户点了"是"。</summary>
    public static bool ShowConfirm(string message, string title = "确认")
        => Show(message, title, XclMessageKind.Info, XclMessageButtons.YesNo) == true;

    /// <summary>等价于 MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question)，
    /// 返回值直接对应原来的 MessageBoxResult.Yes/No/Cancel 三态，调用方原来怎么判断
    /// choice == MessageBoxResult.Xxx，现在就怎么判断 result == XclMessageResult.Xxx。</summary>
    public static XclMessageResult ShowYesNoCancel(string message, string title = "确认")
    {
        var dlg = new MessageBoxDialog(message, title, XclMessageKind.Info, XclMessageButtons.YesNoCancel);
        OverlayDialogService.ShowModal(dlg);
        return dlg.Result3;
    }

    /// <summary>最通用的入口，其它 ShowXxx 静态方法都是对这个的语义化包装。</summary>
    public static bool? Show(string message, string title, XclMessageKind kind, XclMessageButtons buttons)
    {
        var dlg = new MessageBoxDialog(message, title, kind, buttons);
        return OverlayDialogService.ShowModal(dlg);
    }

    /// <summary>Show 的 async/await 版本，其它 ShowXxxAsync 静态方法都是对这个的语义化包装。
    /// 见 ShowInfoAsync 上的注释：专供调用点自己已经身处 async 延续（比如
    /// await Task.Run(...) 之后）时使用，避免嵌套 PushFrame 消息泵。</summary>
    public static Task ShowAsync(string message, string title, XclMessageKind kind, XclMessageButtons buttons)
    {
        var dlg = new MessageBoxDialog(message, title, kind, buttons);
        return OverlayDialogService.ShowModalAsync(dlg);
    }
}
