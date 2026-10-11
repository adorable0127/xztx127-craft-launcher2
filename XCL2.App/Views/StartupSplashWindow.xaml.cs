using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XCL2.App.Services;

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
    private double _lastIntensity = -1;
    private double _lastSpeedRatio = -1;

    public StartupSplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAnimations();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) UpdateAnimations();
            else StopAnimations();
        };
        Unloaded += (_, _) => StopAnimations();
        Closed += (_, _) =>
        {
            AnimationSettingsService.Changed -= AnimationSettingsChanged;
            StopAnimations();
        };
        AnimationSettingsService.Changed += AnimationSettingsChanged;
    }

    private void AnimationSettingsChanged()
    {
        if (Dispatcher.CheckAccess()) UpdateAnimations();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(UpdateAnimations));
    }

    private void StopAnimations()
    {
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinnerRotate.Angle = 0;
        foreach (var dot in new UIElement[] { Dot1, Dot2, Dot3 })
        {
            dot.BeginAnimation(UIElement.OpacityProperty, null);
            dot.Opacity = 1;
        }
        StartupProgress.IsIndeterminate = false;
        StartupProgress.Value = 35;
        _lastIntensity = -1;
        _lastSpeedRatio = -1;
    }

    private void UpdateAnimations()
    {
        if (!IsLoaded || !IsVisible) return;
        // 配置加载前先静态显示，已保存的“关闭动画”不会被启动窗口的默认故事板绕过。
        var intensity = ConfigService.Active == null ? 0 : AnimationSettingsService.Intensity(AnimationKind.Loading);
        var speedRatio = AnimationSettingsService.SpeedRatio;
        if (Math.Abs(_lastIntensity - intensity) < 0.0001 && Math.Abs(_lastSpeedRatio - speedRatio) < 0.0001) return;
        StopAnimations();
        _lastIntensity = intensity;
        _lastSpeedRatio = speedRatio;
        if (intensity <= 0) return;
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, AnimationSettingsService.Duration(1500, AnimationKind.Loading))
            { RepeatBehavior = RepeatBehavior.Forever }, HandoffBehavior.SnapshotAndReplace);
        var dots = new UIElement[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
            dots[i].BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1 - 0.16 * intensity, 1, AnimationSettingsService.Duration(850, AnimationKind.Loading))
                {
                    BeginTime = TimeSpan.FromMilliseconds(160 * i * 0.75 / AnimationSettingsService.SpeedRatio),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                }, HandoffBehavior.SnapshotAndReplace);
        StartupProgress.IsIndeterminate = true;
    }

    /// <summary>更新下方的状态文案，让用户知道现在具体卡在哪一步（不是必须调用，
    /// 不调用的话就一直显示默认的"正在启动…"）。</summary>
    public void SetStatus(string text)
    {
        StatusText.Text = text;
        UpdateAnimations();
        // 启动主流程仍有少量必须在 UI 线程完成的同步初始化；这里主动让一次 Render 优先级
        // 的消息通过，使状态文字/旋转环/跳动圆点能在各真实阶段之间继续刷新，而不是一直冻住。
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }
}
