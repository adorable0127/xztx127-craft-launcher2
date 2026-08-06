using System.Windows;
using System.Windows.Threading;

namespace XCL2.App.Views;

/// <summary>
/// "实验性功能"入口前的强制等待确认窗口。需求原话："用户强制阅读 10 秒之后，才可以打开
/// 实验性功能"——这里用一个不能被跳过的倒计时实现："我已阅读，进入实验性功能"按钮在
/// 窗口打开的头 10 秒内始终 IsEnabled=false，倒计时归零后才启用，且没有提供任何"跳过
/// 等待"的入口（没有可点击的方式提前结束倒计时，关掉窗口重开也会重新计时，不会记住
/// "上次等过了"这种状态——见 AppConfig.ExperimentalFeaturesUnlocked 的注释：
/// 那个字段记的是"确认过一次"，不是"倒计时进度"，所以第一次真正点了"我已阅读"之后，
/// 以后打开实验性功能面板本身不需要再经过这个网关窗口）。
///
/// 只用于"用户第一次解锁实验性功能"这一次性流程；解锁之后由 SettingsPage 直接打开
/// ExperimentalFeaturesWindow，不会再经过这里。
/// </summary>
public partial class ExperimentalGateWindow : OverlayDialogControl
{
    private const int CountdownSeconds = 10;
    private int _remaining = CountdownSeconds;
    private readonly DispatcherTimer _timer;

    /// <summary>用户是否点击了"我已阅读，进入实验性功能"（倒计时结束后才可能为 true）。
    /// 调用方据此决定是否把 AppConfig.ExperimentalFeaturesUnlocked 置 true 并打开实验性功能面板。</summary>
    public bool Confirmed { get; private set; }

    public ExperimentalGateWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        RequestClose += OnRequestClose;

        UpdateCountdownText();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remaining--;

        if (_remaining <= 0)
        {
            _timer.Stop();
            CountdownText.Text = "现在可以继续了。";
            ConfirmBtn.IsEnabled = true;
            ConfirmBtn.Content = "我已阅读，进入实验性功能";
            return;
        }

        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        CountdownText.Text = $"请等待 {_remaining} 秒...（请趁这段时间看完上面的说明）";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // 双重保险：即便有极端情况下按钮的 IsEnabled 状态被绕过（理论上不应该发生），
        // 这里再校验一次剩余秒数，不满足条件就直接忽略这次点击，不设 Confirmed=true。
        if (_remaining > 0) return;

        Confirmed = true;
        _timer.Stop();
        CloseWith(null);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        _timer.Stop();
        CloseWith(null);
    }

    // 同 MicrosoftLoginWindow：OverlayDialogControl 继承 UserControl，没有 Window.OnClosed
    // 可以重写。改用 IOverlayDialog.RequestClose，逻辑跟原来完全一致——弹窗关闭时把倒计时
    // 计时器停掉，避免它在弹窗已经摘除之后还继续触发 Tick。
    private void OnRequestClose(object? sender, bool? result)
    {
        _timer.Stop();
    }
}
