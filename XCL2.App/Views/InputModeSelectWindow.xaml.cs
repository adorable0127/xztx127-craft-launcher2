using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace XCL2.App.Views;

/// <summary>
/// 启动前的"键鼠模式 / 触屏模式"选择框。默认高亮键鼠模式——绝大多数用户仍然是键鼠，
/// 触屏悬浮层属于特定设备(平板/二合一)才需要的附加层，不应该默认打开。
///
/// 现在这个框默认每次启动都会弹（AppConfig.AskInputModeBeforeLaunch 默认 true，
/// 老配置由 ConfigService.Load 里的一次性迁移抬上来），所以必须保证"不选也能继续"：
/// 窗口带 10 秒倒计时，倒数结束自动按键鼠模式放行，不会把"点了启动就走开"的用户卡在这里。
/// 倒计时选键鼠而不是触屏，是因为超时属于"用户没表态"，这时候只能走不改变任何原有行为的那一边。
///
/// 用户只要碰了这个窗口（移动鼠标、按键、点复选框）就说明人在屏幕前，倒计时立刻停掉，
/// 让他慢慢选，不会出现"正要点触屏结果窗口自己关了"这种最糟糕的情况。
/// </summary>
public partial class InputModeSelectWindow : Window
{
    /// <summary>true = 触屏模式；false = 键鼠模式；null = 用户直接关掉了窗口（按键鼠处理）。</summary>
    public bool? UseTouchMode { get; private set; }

    /// <summary>用户是否勾选了"记住这次选择，以后不再询问"。</summary>
    public bool RememberChoice => RememberCheck.IsChecked == true;

    /// <summary>本次是否是倒计时超时自动选的键鼠模式（调用方据此不做"记住选择"之类的持久化）。</summary>
    public bool TimedOut { get; private set; }

    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remainingSeconds;

    public InputModeSelectWindow(bool defaultTouch, int countdownSeconds = 10)
    {
        InitializeComponent();

        // 0 或负数 = 不倒计时，一直等用户选（设置里允许把它关掉）。
        _remainingSeconds = countdownSeconds;

        // 焦点默认落在用户上次用的那个模式上，方便接了键盘时直接回车。
        Loaded += (_, _) => (defaultTouch ? TouchButton : KeyboardButton).Focus();

        if (_remainingSeconds > 0)
        {
            UpdateCountdownText();
            _countdown.Tick += Countdown_Tick;
            Loaded += (_, _) => _countdown.Start();

            // 人一旦在操作就别再催他。用 Preview* 事件是为了在按钮自己处理之前就先收到，
            // 点在窗口任何位置（包括复选框、空白处）都算"人在跟前"。
            PreviewMouseDown += (_, _) => CancelCountdown();
            PreviewKeyDown += (_, _) => CancelCountdown();
            PreviewStylusDown += (_, _) => CancelCountdown();
            PreviewTouchDown += (_, _) => CancelCountdown();
        }
        else
        {
            CountdownText.Visibility = Visibility.Collapsed;
        }

        Closed += (_, _) => _countdown.Stop();
    }

    private void Countdown_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_remainingSeconds > 0)
        {
            UpdateCountdownText();
            return;
        }

        // 超时：按键鼠模式放行。这里不设置 RememberChoice，超时不代表用户做了长期选择。
        _countdown.Stop();
        TimedOut = true;
        UseTouchMode = false;
        DialogResult = true;
    }

    private void CancelCountdown()
    {
        if (!_countdown.IsEnabled) return;
        _countdown.Stop();
        CountdownText.Text = "已暂停自动选择，请选择一种操作方式。";
    }

    private void UpdateCountdownText()
        => CountdownText.Text = $"{_remainingSeconds} 秒后自动以「键鼠模式」启动（点一下窗口即可取消自动选择）。";

    private void Keyboard_Click(object sender, RoutedEventArgs e)
    {
        _countdown.Stop();
        UseTouchMode = false;
        DialogResult = true;
    }

    private void Touch_Click(object sender, RoutedEventArgs e)
    {
        _countdown.Stop();
        UseTouchMode = true;
        DialogResult = true;
    }
}
