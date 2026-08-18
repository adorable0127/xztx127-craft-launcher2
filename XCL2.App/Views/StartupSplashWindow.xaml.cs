using System.Windows;

namespace XCL2.App.Views;

/// <summary>
/// 启动过程中的轻量提示窗——不是完整意义上的"启动画面动画"，只是用来解决
/// "双击图标之后有 1-2 秒完全没有任何反馈，用户以为没点上/程序没反应"这个体验问题。
/// 只依赖最基础的 WPF 控件，不引用 ThemeService/LocalizationService/资源字典，
/// 这样它才能在几乎不需要任何初始化的情况下"立刻"显示出来，不会反过来自己也要
/// 等主题/语言服务加载完才能弹出。
///
/// 生命周期：App.OnStartup 最开始 new 出来并 Show()，中间 MainWindow 的构造 +
/// 首帧渲染完成后由 OnStartup 负责 Close() 掉，全程只存在这一小段时间。
/// </summary>
public partial class StartupSplashWindow : Window
{
    public StartupSplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>更新下方的状态文案，让用户知道现在具体卡在哪一步（不是必须调用，
    /// 不调用的话就一直显示默认的"正在启动…"）。</summary>
    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}
