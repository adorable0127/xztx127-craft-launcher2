using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 协议页（四步流程：用户协议 → 隐私协议 → 开源协议 → 基本模式协议）。
///
/// 交互规则（按需求逐条落实）：
/// 1) 第 1、2 页（法律性文本）：「同意并继续」必须同时满足①页面停留满 5 秒
///    （倒计时显示在按钮上）②已把滚动条拖到本页文本的最底部，两者都满足才可点击。
/// 2) 第 3 页（开源协议）：不强制阅读，进入即可点击；但若停留不足 3 秒就点「同意并继续」，
///    页面底部会揭示"用人话说"备注，需再点一次才真正通过。
/// 3) 第 4 页（基本模式协议，约 1000 字）：只有从「不同意」的三选一里选择「使用基本模式」，
///    或点击「去基本模式」快捷按钮才会进入；不强制阅读，进入即可点击「同意」。
/// 4) 每页正文都可以用顶部的「人话版」开关切换成总结全章正常条款的通俗版本。
/// 5) 任意第 1~3 页的「不同意」按钮：弹出三选一——返回重新确认 / 使用基本模式 / 退出软件。
/// 6) 本窗口还支持两种特殊打开方式（见下面的静态工厂方法）：
///    - CreateBasicModeOnly：仅展示第 4 页（基本模式协议），用于"每次以基本模式启动、
///      尚未同意过基本模式协议"时由 MainWindow 弹出。
///    - CreateReadOnly：只读模式，用于设置页「阅读协议」——只能浏览第 1~3 页并前后翻页，
///      没有「不同意」「去基本模式」，也没有停留计时/滚动强制，最后一页按钮变成「关闭」。
/// 7) Esc 无法关闭本窗口（完整流程/基本模式协议页）：调用方以
///    OverlayDialogService.ShowModal(..., dismissOnEsc: false) 锁死；只读模式允许 Esc 关闭。
///
/// 协议正文在 AgreementsText 分部静态类里；人话版总结见 AgreementsText.PlainLanguage.cs
/// 与 AgreementsText.Additional.cs（基本模式协议部分）。
/// </summary>
public partial class AgreementsWindow : OverlayDialogControl
{
    private enum WindowMode
    {
        /// <summary>完整四步流程（首次启动 / AcceptedAgreementVersion 落后时展示）。</summary>
        Full,
        /// <summary>只展示第 4 页《基本模式协议》，用于每次以基本模式启动时的确认。</summary>
        BasicModeOnly,
        /// <summary>只读浏览，用于设置页「阅读协议」，没有任何强制阅读/表态要求。</summary>
        ReadOnly,
    }

    private readonly MainWindow _owner;
    /// <summary>不再是 readonly：GoFullMode_Click 需要把《基本模式协议》页现场切回完整
    /// 四步流程（而不是关掉这个弹窗再另开一个新的），期间要改写这个字段。</summary>
    private WindowMode _mode;

    private int _step = 1;
    private const int TotalSteps = 3;
    /// <summary>第 1、2 页强制"停留满 5 秒 + 滚到最底部"的秒数。</summary>
    private const int RequiredReadSeconds = 5;
    /// <summary>第 3 页（开源协议）停留不足该秒数就点同意时，揭示底部"用人话说"备注。</summary>
    private const int OpenSourceNoteThresholdSeconds = 3;
    private const string AgreeLabel = "同意并继续";

    /// <summary>第 1、2 页各自的"满 5 秒"与"已滚到底部"标记（每页只要求一次，返回再进不重新计时）。</summary>
    private bool _page1Elapsed;
    private bool _page2Elapsed;
    private bool _page1AtBottom;
    private bool _page2AtBottom;

    private int _countdownRemaining;
    private DispatcherTimer? _countdownTimer;

    /// <summary>当前页进入时刻，用于第 3 页"是否满 3 秒"的判断。</summary>
    private DateTime _pageEnteredAt = DateTime.UtcNow;
    private bool _openNoteShown;

    /// <summary>人话版开关的当前状态，全局对所有页生效。</summary>
    private bool _plainLanguage;

    public AgreementsWindow(MainWindow owner) : this(owner, WindowMode.Full)
    {
    }

    /// <summary>只展示《基本模式协议》，用于每次以基本模式启动、尚未同意过该协议时弹出。</summary>
    public static AgreementsWindow CreateBasicModeOnly(MainWindow owner) => new(owner, WindowMode.BasicModeOnly);

    /// <summary>只读浏览完整三份协议，用于设置页「阅读协议」，没有任何表态/计时要求。</summary>
    public static AgreementsWindow CreateReadOnly(MainWindow owner) => new(owner, WindowMode.ReadOnly);

    private AgreementsWindow(MainWindow owner, WindowMode mode)
    {
        _owner = owner;
        _mode = mode;
        InitializeComponent();

        Page1Text.Text = AgreementsText.UserAgreementText;
        Page2Text.Text = AgreementsText.PrivacyAgreementText;
        Page3Text.Text = AgreementsText.OpenSourceAgreementText;
        Page4Text.Text = AgreementsText.BasicModeAgreementText;
        Page1PlainText.Text = AgreementsText.UserAgreementPlainLanguage;
        Page2PlainText.Text = AgreementsText.PrivacyAgreementPlainLanguage;
        Page3PlainText.Text = AgreementsText.OpenSourceAgreementPlainLanguage;
        Page4PlainText.Text = AgreementsText.BasicModeAgreementPlainLanguage;

        // 滚动检测：第 1、2 页需要"滚到最底部"才算读完。每次滚动都重算一次当前页是否
        // 已到底（只置 true 不置回 false——一旦看到过底部，往回滚也算已读）。只读模式
        // 不设置该限制，但挂着这个处理器不影响功能，简单起见不特殊分支。
        DocScroll.ScrollChanged += (_, _) =>
        {
            var scroll = DocScroll;
            var atBottom = scroll.ExtentHeight <= 0
                || scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 4;
            if (atBottom)
            {
                if (_step == 1) _page1AtBottom = true;
                if (_step == 2) _page2AtBottom = true;
            }
            UpdateAgreeState();
        };

        // 控件拆掉时停掉倒计时，避免 Timer 在对话框销毁后空转。
        Unloaded += (_, _) =>
        {
            _countdownTimer?.Stop();
            _countdownTimer = null;
        };

        ApplyModeChrome();

        if (_mode == WindowMode.BasicModeOnly)
        {
            GoToStep(4);
        }
        else
        {
            GoToStep(1);
        }
    }

    /// <summary>按打开模式调整标题文案、步骤指示条、以及哪些按钮可见。</summary>
    private void ApplyModeChrome()
    {
        switch (_mode)
        {
            case WindowMode.BasicModeOnly:
                HeaderTitle.Text = "基本模式协议";
                HeaderSubtitle.Text = "你目前以「基本模式」使用 XCL2，本页是这一模式专属的简短协议（约 1000 字），不强制阅读，进入即可点击「同意」。";
                StepDots.Visibility = Visibility.Collapsed;
                GoBasicModeBtn.Visibility = Visibility.Collapsed;
                // 「去完整模式」：不想只看这份基本模式协议、想直接走完整四步流程的快捷入口，
                // 放在协议旁边（跟「同意」「不同意」同一行），见 GoFullMode_Click。
                GoFullModeBtn.Visibility = Visibility.Visible;
                DisagreeBtn.Content = "不同意（继续保持受限）";
                break;
            case WindowMode.ReadOnly:
                HeaderTitle.Text = "阅读协议";
                HeaderSubtitle.Text = "只读浏览模式：可以前后翻页查看《用户协议》《隐私协议》《开源协议》全文，无需重新表态。";
                DisagreeBtn.Visibility = Visibility.Collapsed;
                // 「切换到基本模式」：只读浏览时如果用户看完发现不想接受完整协议，也能直接从这里
                // 一步切到基本模式，不用先关掉只读窗口、再去主界面走「不同意」三选一。
                // 复用 GoBasicModeBtn/GoBasicMode_Click 现成的「跳到第 4 页 + 同意即写入
                // RestrictedMode」逻辑（见 EnterBasicModeStep/BasicModeAgree_Click），
                // 这里只是不再把它折叠隐藏、换一个更贴切只读场景的文案。
                GoBasicModeBtn.Content = "切换到基本模式";
                StepDot4.Visibility = Visibility.Collapsed;
                break;
            default:
                break;
        }
    }

    // ---------- 人话版开关 ----------

    private void PlainLanguageToggle_Click(object sender, RoutedEventArgs e)
    {
        _plainLanguage = PlainLanguageToggle.IsChecked == true;
        ApplyPlainLanguageVisibility();
    }

    /// <summary>按当前页 + 人话版开关状态，切换"正式条文全文"与"人话总结"两块文本的显隐。
    /// 切换后滚动位置可能不再对应，统一滚回顶部，避免半截内容对不上。</summary>
    private void ApplyPlainLanguageVisibility()
    {
        Page1Text.Visibility = _plainLanguage ? Visibility.Collapsed : Visibility.Visible;
        Page1PlainText.Visibility = _plainLanguage ? Visibility.Visible : Visibility.Collapsed;
        Page2Text.Visibility = _plainLanguage ? Visibility.Collapsed : Visibility.Visible;
        Page2PlainText.Visibility = _plainLanguage ? Visibility.Visible : Visibility.Collapsed;
        Page3Text.Visibility = _plainLanguage ? Visibility.Collapsed : Visibility.Visible;
        Page3PlainText.Visibility = _plainLanguage ? Visibility.Visible : Visibility.Collapsed;
        Page4Text.Visibility = _plainLanguage ? Visibility.Collapsed : Visibility.Visible;
        Page4PlainText.Visibility = _plainLanguage ? Visibility.Visible : Visibility.Collapsed;
        DocScroll.ScrollToTop();
    }

    // ---------- 步骤切换 ----------

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        GoToStep(_step - 1);
    }

    private void GoToStep(int step)
    {
        _step = _mode == WindowMode.BasicModeOnly ? 4 : Math.Clamp(step, 1, TotalSteps);

        Page1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Page4Panel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var dim = System.Windows.Media.Brushes.LightGray;
        StepDot1.Background = _step >= 1 ? accent : dim;
        StepDot2.Background = _step >= 2 ? accent : dim;
        StepDot3.Background = _step >= 3 ? accent : dim;
        StepDot4.Background = _step >= 4 ? accent : dim;

        BackBtn.IsEnabled = _mode == WindowMode.ReadOnly ? _step > 1 : _step > 1 && _step != 4;
        // ReadOnly 也要显示（文案已在 ApplyModeChrome 里换成"切换到基本模式"），否则只读模式
        // 一旦从第 1 页翻到第 2/3 页，这里会把它重新收起来。
        GoBasicModeBtn.Visibility = ((_mode == WindowMode.Full || _mode == WindowMode.ReadOnly) && _step is 1 or 2 or 3)
            ? Visibility.Visible : Visibility.Collapsed;

        // 进入下一页自动返回页面顶部（每份协议 10000+ 字，直接继承上一页的滚动位置
        // 会让用户以为内容没换）。
        DocScroll.ScrollToTop();
        _pageEnteredAt = DateTime.UtcNow;

        if (_mode == WindowMode.Full && ((_step == 1 && !_page1Elapsed) || (_step == 2 && !_page2Elapsed)))
        {
            StartCountdown(RequiredReadSeconds);
        }
        else
        {
            // 只读模式 / 第 3、4 页 / 已读过一遍返回再进：都不需要重新计时。
            UpdateAgreeState();
        }
    }

    // ---------- 同意 / 不同意 ----------

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == WindowMode.ReadOnly)
        {
            if (_step < TotalSteps)
            {
                GoToStep(_step + 1);
                return;
            }
            // 只读模式最后一页：按钮文案已经变成「关闭」，直接收起窗口，不改动任何配置。
            CloseWith(true);
            return;
        }

        if (_mode == WindowMode.BasicModeOnly)
        {
            _owner.ConfigService.Config.BasicAgreementAccepted = true;
            _owner.ConfigService.Save();
            CloseWith(true);
            return;
        }

        if (_step < TotalSteps)
        {
            GoToStep(_step + 1);
            return;
        }

        // 第 3 页：停留不足 3 秒就点同意 → 揭示"用人话说"备注并滚动到底部，
        // 要求再点一次（确认看过备注）才真正通过。人话版开关已经打开时，本来就在看
        // 通俗总结，不需要再额外揭示这段备注。
        if (!_plainLanguage && !_openNoteShown
            && (DateTime.UtcNow - _pageEnteredAt) < TimeSpan.FromSeconds(OpenSourceNoteThresholdSeconds))
        {
            _openNoteShown = true;
            OpenSourceNote.Visibility = Visibility.Visible;
            DocScroll.ScrollToBottom();
            UpdateAgreeState();
            return;
        }

        // 三份协议全部同意，按当前协议版本号落盘（见 AgreementsText.AgreementsVersion 的
        // 注释：版本号比对是"每次协议更新都重新弹出确认"的实现基础）。
        _owner.ConfigService.Config.AgreementsAccepted = true;
        _owner.ConfigService.Config.AcceptedAgreementVersion = AgreementsText.AgreementsVersion;
        _owner.ConfigService.Config.RestrictedMode = false;
        _owner.ConfigService.Config.BasicAgreementAccepted = true;
        _owner.ConfigService.Save();
        CloseWith(true);
    }

    private void Disagree_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == WindowMode.BasicModeOnly)
        {
            // 基本模式协议本身可以不同意：继续保持受限状态，不阻塞使用，下次启动重新问一遍。
            CloseWith(true);
            return;
        }

        // 任意第 1~3 页点「不同意」：弹三选一——返回重新确认 / 使用基本模式 / 退出软件。
        var choice = MessageBoxDialog.ShowThreeChoice(
            "你选择了「不同意」协议，请选择接下来的操作：\n\n" +
            "· 返回重新确认：关闭这个提示，留在协议页继续阅读；\n" +
            "· 使用基本模式：切换为基本模式（只能启动游戏、选择游戏文件夹，其余功能置灰）后继续使用，随时可以「重新阅读协议并同意」恢复全部功能；\n" +
            "· 退出软件：直接关闭本软件。",
            "不同意协议",
            "返回重新确认",
            "退出软件",
            "使用基本模式");

        switch (choice)
        {
            case XclMessageResult.Cancel: // 返回重新确认
                return;
            case XclMessageResult.No: // 退出软件
                Application.Current.Shutdown(0);
                return;
            case XclMessageResult.Yes: // 使用基本模式
                EnterBasicModeStep();
                return;
        }
    }

    private void GoBasicMode_Click(object sender, RoutedEventArgs e) => EnterBasicModeStep();

    /// <summary>「去完整模式」：只在 WindowMode.BasicModeOnly 下可见（见 ApplyModeChrome）。
    /// 不关掉这个弹窗再另开一个新的，而是现场把 _mode 切回 Full 并把第 1~3 页的标题/步骤条/
    /// 按钮都恢复原样，从第 1 页开始走完整四步流程——Agree_Click/Disagree_Click 本来就是
    /// 按 _mode 分支处理的（XAML 里一直是它俩的 Click 事件，BasicModeOnly 场景下也没有被
    /// EnterBasicModeStep 换成 BasicModeAgree_Click 那一套，只有"从完整流程走到第 4 页"才会
    /// 换），所以这里改完 _mode 后不需要再重新接线按钮事件。</summary>
    private void GoFullMode_Click(object sender, RoutedEventArgs e)
    {
        _mode = WindowMode.Full;
        HeaderTitle.Text = "欢迎使用 XCL2";
        HeaderSubtitle.Text = "首次使用前，请逐一阅读并同意以下四步：用户协议 → 隐私协议 → 开源协议 → 基本模式（如适用）。前两份需要拖到页面最底部并完整阅读 5 秒后即可继续；第三份（开源协议）不强制。";
        StepDots.Visibility = Visibility.Visible;
        DisagreeBtn.Content = "不同意";
        GoFullModeBtn.Visibility = Visibility.Collapsed;
        GoToStep(1);
    }

    /// <summary>跳到第 4 页《基本模式协议》，并把这一页的按钮改为直接写入"基本模式"配置
    /// （不复用 Agree_Click/Disagree_Click 里针对第 1~3 页的语义，避免互相干扰）。</summary>
    private void EnterBasicModeStep()
    {
        _step = 4;
        Page1Panel.Visibility = Visibility.Collapsed;
        Page2Panel.Visibility = Visibility.Collapsed;
        Page3Panel.Visibility = Visibility.Collapsed;
        Page4Panel.Visibility = Visibility.Visible;
        DocScroll.ScrollToTop();
        _pageEnteredAt = DateTime.UtcNow;
        GoBasicModeBtn.Visibility = Visibility.Collapsed;
        BackBtn.IsEnabled = false;
        StepDot4.Visibility = Visibility.Visible;

        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        StepDot1.Background = accent; StepDot2.Background = accent; StepDot3.Background = accent; StepDot4.Background = accent;

        AgreeBtn.Content = "同意，进入基本模式";
        AgreeBtn.IsEnabled = true;
        DisagreeBtn.Content = "仍要退出软件";

        AgreeBtn.Click -= Agree_Click;
        AgreeBtn.Click += BasicModeAgree_Click;
        DisagreeBtn.Click -= Disagree_Click;
        DisagreeBtn.Click += BasicModeDisagree_Click;
    }

    private void BasicModeAgree_Click(object sender, RoutedEventArgs e)
    {
        _owner.ConfigService.Config.RestrictedMode = true;
        _owner.ConfigService.Config.BasicAgreementAccepted = true;
        // 完整协议仍未同意：AgreementsAccepted/AcceptedAgreementVersion 保持原值，
        // 下次用户主动「重新阅读协议并同意」时才会真正写入。
        _owner.ConfigService.Save();
        CloseWith(true);
    }

    private void BasicModeDisagree_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown(0);

    // ---------- 倒计时与按钮状态 ----------

    private void StartCountdown(int seconds)
    {
        _countdownTimer?.Stop();

        _countdownRemaining = seconds;
        UpdateAgreeState();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer = timer;
        timer.Tick += (_, _) =>
        {
            _countdownRemaining--;
            if (_countdownRemaining <= 0)
            {
                timer.Stop();
                // 该页已满 5 秒（_pageXElapsed 标记），返回再进不再重新计时。
                if (_step == 1) _page1Elapsed = true;
                if (_step == 2) _page2Elapsed = true;
            }
            UpdateAgreeState();
        };
        timer.Start();
    }

    /// <summary>按当前页的完成条件刷新按钮文案与可用状态。只读模式恒可用（浏览不需要表态）。</summary>
    private void UpdateAgreeState()
    {
        if (_mode == WindowMode.ReadOnly)
        {
            AgreeBtn.Content = _step < TotalSteps ? "下一步" : "关闭";
            AgreeBtn.IsEnabled = true;
            return;
        }

        if (_mode == WindowMode.BasicModeOnly || _step == 4)
        {
            AgreeBtn.IsEnabled = true;
            return;
        }

        if (_step == 1 || _step == 2)
        {
            var atBottom = _step == 1 ? _page1AtBottom : _page2AtBottom;
            var elapsed = _step == 1 ? _page1Elapsed : _page2Elapsed;

            if (!atBottom)
            {
                AgreeBtn.Content = "请先滚动阅读到页面最底部";
                AgreeBtn.IsEnabled = false;
            }
            else if (!elapsed)
            {
                AgreeBtn.Content = $"请完整阅读（剩余 {_countdownRemaining} 秒）";
                AgreeBtn.IsEnabled = false;
            }
            else
            {
                AgreeBtn.Content = AgreeLabel;
                AgreeBtn.IsEnabled = true;
            }
            return;
        }

        // 第 3 页：默认即可点；揭示备注后要求再确认一次。
        if (_openNoteShown)
        {
            AgreeBtn.Content = "我已阅读并理解上面的说明，同意并继续";
            AgreeBtn.IsEnabled = true;
        }
        else
        {
            AgreeBtn.Content = AgreeLabel;
            AgreeBtn.IsEnabled = true;
        }
    }
}
