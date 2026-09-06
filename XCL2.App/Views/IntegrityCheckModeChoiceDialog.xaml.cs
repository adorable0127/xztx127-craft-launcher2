using System.Windows;
using System.Windows.Threading;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>
/// "启动前完整性检查要多严格"的首选项弹窗：用户没手动选过时（cfg.IntegrityCheckMode == null）
/// 触发一次，10 秒没点任何按钮就自动按 Simple 处理并关闭——不能让这个次要的偏好设置卡住
/// 用户"我只是想启动游戏"这个主线程流程。
/// </summary>
public partial class IntegrityCheckModeChoiceDialog : OverlayDialogControl
{
    /// <summary>用户的选择，或超时后的默认值 Simple。ShowModal 无论如何关闭都保证有值。</summary>
    public IntegrityCheckMode Result { get; private set; } = IntegrityCheckMode.Simple;

    private readonly DispatcherTimer _timer;
    private int _secondsLeft = 10;

    public IntegrityCheckModeChoiceDialog()
    {
        InitializeComponent();
        UpdateCountdownText();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _secondsLeft--;
        if (_secondsLeft <= 0)
        {
            _timer.Stop();
            Result = IntegrityCheckMode.Simple;
            CloseWith(true);
            return;
        }
        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        CountdownText.Text = $"{_secondsLeft} 秒内不选择将自动使用「普通」";
    }

    private void Simple_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Result = IntegrityCheckMode.Simple;
        CloseWith(true);
    }

    private void Strict_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Result = IntegrityCheckMode.Strict;
        CloseWith(true);
    }
}
