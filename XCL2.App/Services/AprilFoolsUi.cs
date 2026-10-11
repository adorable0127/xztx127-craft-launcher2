using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// <see cref="AprilFoolsService"/> 的 WPF 落地层：具体"怎么弹窗、怎么挪按钮、怎么改窗口位置"
/// 都在这里，AprilFoolsService 本身不引用任何 WPF 类型。
///
/// 彩蛋的激活状态由 AprilFoolsService 决定，窗口、按钮和磁贴移动同时遵守统一动画设置。
/// 动效定时器只在对应效果激活、界面已加载且可见时运行；业务提醒倒计时保持原有行为。
/// </summary>
public static class AprilFoolsUi
{
    private static readonly ConditionalWeakTable<MainWindow, object> WindowAttachments = new();
    private static readonly ConditionalWeakTable<Panel, object> HomeAttachments = new();
    private static DispatcherTimer? _recoveryNotificationTimer;
    private static DispatcherTimer? _recoveryWin32Timer;
    private static readonly Random Rng = new();

    private const string RickRoll1Url = "https://www.bilibili.com/video/BV1GJ411x7h7/";
    private const string RickRoll2Url = "https://www.bilibili.com/video/BV12rMQ6yEP2";

    /// <summary>MainWindow 构造函数里调用一次：挂上小白旗的显隐监听、窗口乱窜效果、
    /// F1 紧急关闭热键以及 10 秒/60 秒恢复提示。首页磁贴相关的效果（3 号鼠标躲避、8 号磁贴乱跳）由 HomePage 自己
    /// 在构造函数里调用 <see cref="AttachHomeTileEffects"/>，不在这里处理
    /// （HomePage 是后创建的 UserControl，这里拿不到它的控件引用）。</summary>
    public static void Attach(MainWindow owner)
    {
        if (WindowAttachments.TryGetValue(owner, out _)) return;
        WindowAttachments.Add(owner, new object());
        var closed = false;
        var subscribed = false;
        var watchdog = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
            { Interval = TimeSpan.FromSeconds(2) };
        var windowChaos = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
            { Interval = TimeSpan.FromSeconds(3) };

        bool CanMove() => !closed && subscribed && owner.IsLoaded && owner.IsVisible
            && owner.WindowState != WindowState.Minimized
            && AnimationSettingsService.IsEnabled(AnimationKind.WindowFullscreen)
            && AprilFoolsService.Has(AprilFoolsService.Effect.WindowChaos);

        void UpdateMotion()
        {
            if (CanMove()) windowChaos.Start();
            else windowChaos.Stop();
        }
        void Dispatch(Action action)
        {
            if (closed || owner.Dispatcher.HasShutdownStarted) return;
            if (owner.Dispatcher.CheckAccess()) action();
            else owner.Dispatcher.BeginInvoke(new Action(() => { if (!closed && subscribed) action(); }));
        }
        void StateChanged() => Dispatch(() =>
        {
            if (!subscribed) return;
            RefreshFlagVisibility(owner);
            UpdateMotion();
            ScheduleRecoveryPrompts(owner);
        });
        void SettingsChanged() => Dispatch(UpdateMotion);
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateMotion();
        void WindowStateChanged(object? sender, EventArgs e) => UpdateMotion();
        void Loaded(object sender, RoutedEventArgs e)
        {
            if (closed || subscribed) return;
            subscribed = true;
            AprilFoolsService.StateChanged += StateChanged;
            AnimationSettingsService.Changed += SettingsChanged;
            watchdog.Start();
            RefreshFlagVisibility(owner);
            UpdateMotion();
            ScheduleRecoveryPrompts(owner);
        }
        void Unloaded(object sender, RoutedEventArgs e)
        {
            subscribed = false;
            AprilFoolsService.StateChanged -= StateChanged;
            AnimationSettingsService.Changed -= SettingsChanged;
            watchdog.Stop();
            windowChaos.Stop();
            _recoveryNotificationTimer?.Stop();
            _recoveryWin32Timer?.Stop();
            _recoveryNotificationTimer = null;
            _recoveryWin32Timer = null;
        }
        void PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F1 || !AprilFoolsService.IsActive) return;
            e.Handled = true;
            AprilFoolsService.DismissForToday();
            owner.AprilFoolsFlagButton.Visibility = Visibility.Collapsed;
        }
        void Closed(object? sender, EventArgs e)
        {
            closed = true;
            Unloaded(owner, new RoutedEventArgs());
            owner.Loaded -= Loaded;
            owner.Unloaded -= Unloaded;
            owner.IsVisibleChanged -= VisibilityChanged;
            owner.StateChanged -= WindowStateChanged;
            owner.PreviewKeyDown -= PreviewKeyDown;
            owner.Closed -= Closed;
            WindowAttachments.Remove(owner);
        }

        // 跨日复查是状态维护，保持两秒周期；只有窗口实际移动受动画开关约束。
        watchdog.Tick += (_, _) =>
        {
            if (!closed && subscribed) { RefreshFlagVisibility(owner); UpdateMotion(); }
        };
        windowChaos.Tick += (_, _) =>
        {
            if (!CanMove()) { UpdateMotion(); return; }
            if (owner.WindowState == WindowState.Maximized) owner.WindowState = WindowState.Normal;
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            var intensity = AnimationSettingsService.Intensity(AnimationKind.WindowFullscreen);
            var targetLeft = Rng.NextDouble() * Math.Max(0, screenWidth - owner.Width);
            var targetTop = Rng.NextDouble() * Math.Max(0, screenHeight - owner.Height);
            owner.Left += (targetLeft - owner.Left) * intensity;
            owner.Top += (targetTop - owner.Top) * intensity;
        };
        owner.Loaded += Loaded;
        owner.Unloaded += Unloaded;
        owner.IsVisibleChanged += VisibilityChanged;
        owner.StateChanged += WindowStateChanged;
        owner.PreviewKeyDown += PreviewKeyDown;
        owner.Closed += Closed;
        RefreshFlagVisibility(owner);
        if (owner.IsLoaded) Loaded(owner, new RoutedEventArgs());
    }

    private static void ScheduleRecoveryPrompts(MainWindow owner)
    {
        _recoveryNotificationTimer?.Stop();
        _recoveryWin32Timer?.Stop();
        _recoveryNotificationTimer = null;
        _recoveryWin32Timer = null;

        if (!AprilFoolsService.IsActive) return;

        // 10 秒提醒：需求原话是"如果用户 10 秒内没有关闭这个彩蛋，就会弹窗提示点击右下角
        // 小白旗来关闭彩蛋"——所以这条提醒必须明确指向小白旗，不能只说 F1（F1 是额外补的
        // 紧急恢复键，按钮点不到时的兜底，但不是用户被告知的主要恢复方式）。
        _recoveryNotificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _recoveryNotificationTimer.Tick += (_, _) =>
        {
            _recoveryNotificationTimer?.Stop();
            if (AprilFoolsService.IsActive)
                MessageBoxDialog.ShowInfo("点击右下角的白旗🏳️就可以关闭这个彩蛋，恢复正常啦。\n\n（如果按钮一直点不到，按 F1 也能立刻恢复。）", "愚人节彩蛋");
        };
        _recoveryNotificationTimer.Start();

        // 60 秒兜底：万一 10 秒那次提醒被用户不小心划走/没注意到，用系统原生 MessageBox
        // 再提醒一次——用原生弹窗而不是启动器自己皮肤化的对话框，是为了在"3/5/8 号效果
        // 正在乱窜/乱跳"的极端情况下，这条提醒本身也不会被牵连着一起变得点不到。
        _recoveryWin32Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _recoveryWin32Timer.Tick += (_, _) =>
        {
            _recoveryWin32Timer?.Stop();
            if (!AprilFoolsService.IsActive) return;
            System.Windows.MessageBox.Show(
                "点击右下角的白旗就可以关闭这个彩蛋，恢复正常。按不到的话，按 F1 也可以。",
                "XCL2 愚人节彩蛋",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        };
        _recoveryWin32Timer.Start();
    }

    private static void RefreshFlagVisibility(MainWindow owner)
    {
        // AprilFoolsService.IsActive 内部第一步就是"今天是不是 4 月 1 日"，
        // 平时（其余 364 天）这里恒为 false，小白旗恒为 Collapsed——不需要在这个方法里
        // 再额外写一次日期判断。
        owner.AprilFoolsFlagButton.Visibility = AprilFoolsService.IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (!AprilFoolsService.IsActive)
        {
            owner.WindowState = owner.WindowState; // no-op，保留位置；乱窜已经被 Timer 里的 Has() 判断挡住
        }
    }

    /// <summary>小白旗点击：恢复 + 弹说明 + （AprilFoolsService 内部）落盘，一次性做完。</summary>
    public static void OnWhiteFlagClicked(MainWindow owner)
    {
        var descriptions = AprilFoolsService.ClickWhiteFlag();
        owner.AprilFoolsFlagButton.Visibility = Visibility.Collapsed;
        if (descriptions.Count == 0) return;

        var text = "今天的整蛊到此为止，刚才你遇到的是：\n\n" + string.Join("\n\n", descriptions);
        MessageBoxDialog.ShowInfo(text, "愚人节彩蛋");
    }

    /// <summary>「启动游戏」「下载」类按钮的统一钩子。返回 true 表示这次点击已经被彩蛋
    /// 劫持（跳转恶搞视频 / 弹假报错 / 提示拒绝启动），调用方应直接 return，不再执行
    /// 原本的启动/下载逻辑。</summary>
    public static bool TryInterceptLaunchOrDownload(Window owner)
    {
        if (!AprilFoolsService.TryGetLaunchOrDownloadInterception(out var which)) return false;

        switch (which)
        {
            case AprilFoolsService.Effect.RickRoll1:
                OpenUrl(RickRoll1Url);
                break;
            case AprilFoolsService.Effect.RickRoll2:
                OpenUrl(RickRoll2Url);
                break;
            case AprilFoolsService.Effect.FakeWin32Error:
                ShowFakeWin32Error();
                break;
            case AprilFoolsService.Effect.LauncherRefuses:
                MessageBoxDialog.ShowInfo(AprilFoolsService.RandomRefusalText(), "提示");
                break;
            // 修复：3/8/5 号效果原本只在"鼠标碰到按钮/磁贴"这个交互路径里表现（躲避、
            // 乱跳），从来没处理过"点击本身被劫持"这一步——只要这次点击是从不经过鼠标
            // 交互的入口（典型如托盘菜单"启动游戏"）打进来的，之前会直接放过去正常启动。
            // 这里补上对应的提示文案，跟 AprilFoolsService.Catalog 里的效果描述保持
            // 语气一致，同时也保证调用方 return 后确实没有执行真正的启动逻辑。
            case AprilFoolsService.Effect.DodgeLaunchButton:
                MessageBoxDialog.ShowInfo("按钮好像又躲开了，你好像没点到「启动游戏」。", "提示");
                break;
            case AprilFoolsService.Effect.TileChaos:
                MessageBoxDialog.ShowInfo("磁贴今天不开心，很不高兴为你启动游戏。", "提示");
                break;
            case AprilFoolsService.Effect.WindowChaos:
                MessageBoxDialog.ShowInfo("窗口今天坐不住，先别急着启动游戏。", "提示");
                break;
        }
        return true;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 用户环境没有默认浏览器之类的极端情况，静默失败即可，不应该在愚人节彩蛋
            // 这种非核心功能上抛出未处理异常影响主流程。
        }
    }

    /// <summary>2 号手段：假的 Win32 错误弹窗——文案模仿"某文件某行某列出现语法错误"这种
    /// 常见的编译器/脚本报错措辞，配上原生 MessageBox（故意用最朴素的系统对话框风格，
    /// 制造"像是真的系统报错"的错觉，跟启动器自己皮肤化的 MessageBoxDialog 刻意区分开）。</summary>
    private static void ShowFakeWin32Error()
    {
        string[] files = ["launcher.dll", "minecraft_launcher_core.dll", "xcl2_native.dll", "game_profile.json"];
        var file = files[Rng.Next(files.Length)];
        var line = Rng.Next(1, 9999);
        var col = Rng.Next(1, 200);
        var message = $"应用程序错误\n\n文件 {file} 第 {line} 行，第 {col} 列出现异常：\n0xC0000005: 无法读取位于 0x{Rng.Next(0x10000000, 0x7FFFFFFF):X8} 的内存。\n\n程序将继续运行，但相关功能可能不可用。";
        System.Windows.MessageBox.Show(message, "XCL2.App - 应用程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>3 号（鼠标靠近就乱窜）+ 8 号（磁贴乱跳 + 磁贴点击台词）在首页构造函数里调用。
    /// <paramref name="launchTile"/> 是"启动游戏"磁贴，<paramref name="quickStartTile"/> 是
    /// "一键开始游戏"磁贴（普通模式下两者在磁贴总控台里可能互换位置，这里按传入的控件本身
    /// 操作，不关心当前具体排在第几格）。</summary>
    public static void AttachHomeTileEffects(Panel tileGrid, Button launchTile, Button quickStartTile)
    {
        if (HomeAttachments.TryGetValue(tileGrid, out _)) return;
        HomeAttachments.Add(tileGrid, new object());
        var subscribed = false;
        Window? homeWindow = null;
        bool CanAnimate(AnimationKind kind) => subscribed && tileGrid.IsLoaded && tileGrid.IsVisible
            && homeWindow is { IsVisible: true } && homeWindow.WindowState != WindowState.Minimized
            && AnimationSettingsService.IsEnabled(kind);

        EnsureTransform(launchTile);
        EnsureTransform(quickStartTile);

        // 3 号：鼠标靠近按钮就持续躲避，模拟"永远点不到"。
        //
        // ===== 修复：原来只在 MouseEnter 那一瞬间跳一次 =====
        // 问题在于 MouseEnter 只会在鼠标"从外面移进按钮范围"这个边界穿越的瞬间触发一次；
        // 跳开之后鼠标继续朝新位置移动，只要中途没有真的先离开再进入（而是直接划过去、
        // 或者本来移动速度就快到一步到位），下一次 MouseEnter 根本不会触发，按钮会安安静静
        // 待在原地任由用户点——这就是"移速并不快，并非宣传中的'点不到'"的根本原因：
        // 不是跳得慢，是压根没有持续跟踪鼠标、只跳了那一下。
        //
        // 现在改成一个 16ms 一次的高频 Timer（接近一帧的量级），持续算"鼠标当前位置"到
        // "按钮当前（含偏移后）中心点"的距离——只要进入警戒半径，立刻往鼠标反方向跳开一大段
        // 距离，且每次跳的角度带一点随机扰动，避免被"预判跳跃方向"套路化。这样不管鼠标怎么
        // 移动、多快移动，按钮都会在用户手指按下去之前的每一帧持续重新判定并躲开，
        // 真正做到"点不到"。
        var launchOriginalCenter = (Point?)null;
        launchTile.Loaded += (_, _) =>
        {
            var transform = (TranslateTransform)launchTile.RenderTransform;
            var savedX = transform.X;
            var savedY = transform.Y;
            transform.X = 0;
            transform.Y = 0;
            var topLeft = launchTile.TranslatePoint(new Point(0, 0), tileGrid);
            launchOriginalCenter = new Point(topLeft.X + launchTile.ActualWidth / 2, topLeft.Y + launchTile.ActualHeight / 2);
            transform.X = savedX;
            transform.Y = savedY;
        };

        var dodgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        dodgeTimer.Tick += (_, _) =>
        {
            var transform = (TranslateTransform)launchTile.RenderTransform;
            if (!CanAnimate(AnimationKind.ControlFeedback) || !AprilFoolsService.Has(AprilFoolsService.Effect.DodgeLaunchButton))
            {
                dodgeTimer.Stop();
                transform.X = 0;
                transform.Y = 0;
                return;
            }
            if (launchOriginalCenter == null) return;

            var mouse = Mouse.GetPosition(tileGrid);
            var currentCenter = new Point(launchOriginalCenter.Value.X + transform.X, launchOriginalCenter.Value.Y + transform.Y);
            var dx = currentCenter.X - mouse.X;
            var dy = currentCenter.Y - mouse.Y;
            const double DangerRadius = 90; // 鼠标进入这个半径就判定为"要点到了"，必须立刻跳开
            if (dx * dx + dy * dy > DangerRadius * DangerRadius) return;

            // 往远离鼠标的方向跳开一大步，角度加一点随机扰动，防止被"卡在角落等它跳"这种
            // 固定套路破解；默认强度的跳跃距离大于警戒半径，降低强度时按比例减小。
            var angle = Math.Atan2(dy, dx) + (Rng.NextDouble() - 0.5) * 0.6;
            var intensity = AnimationSettingsService.Intensity(AnimationKind.ControlFeedback);
            var jumpDistance = 150 * intensity;
            var newX = transform.X + Math.Cos(angle) * jumpDistance;
            var newY = transform.Y + Math.Sin(angle) * jumpDistance;

            // 限制偏移范围，别把按钮跳到 tileGrid 可见区域外面去，那样反而找不到它，
            // 也失去了"看得见点不到"的整蛊效果。
            // 同时缩小累计活动范围，避免低强度连续跳动后仍跑到满幅度的位置。
            var maxOffsetX = Math.Max(40, tileGrid.ActualWidth / 2 - launchTile.ActualWidth / 2) * intensity;
            var maxOffsetY = Math.Max(40, tileGrid.ActualHeight / 2 - launchTile.ActualHeight / 2) * intensity;
            transform.X = Math.Clamp(newX, -maxOffsetX, maxOffsetX);
            transform.Y = Math.Clamp(newY, -maxOffsetY, maxOffsetY);
        };

        // 8 号：磁贴乱跳，用一个定时器随机给磁贴总控台里的每个磁贴一个小幅度随机偏移；
        // 点击"启动游戏"磁贴时不再走正常启动，改成弹一句台词。
        var tileChaosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        tileChaosTimer.Tick += (_, _) =>
        {
            if (!CanAnimate(AnimationKind.Page) || !AprilFoolsService.Has(AprilFoolsService.Effect.TileChaos))
            {
                tileChaosTimer.Stop();
                foreach (var child in tileGrid.Children.OfType<Button>())
                {
                    EnsureTransform(child);
                    var t = (TranslateTransform)child.RenderTransform;
                    t.X = 0;
                    t.Y = 0;
                }
                return;
            }
            var intensity = AnimationSettingsService.Intensity(AnimationKind.Page);
            foreach (var child in tileGrid.Children.OfType<Button>())
            {
                EnsureTransform(child);
                var t = (TranslateTransform)child.RenderTransform;
                t.X = (Rng.NextDouble() - 0.5) * 24 * intensity;
                t.Y = (Rng.NextDouble() - 0.5) * 24 * intensity;
            }
        };

        void ResetTransform(Button button)
        {
            if (button.RenderTransform is not TranslateTransform transform) return;
            transform.X = 0;
            transform.Y = 0;
        }
        void UpdateTimers()
        {
            var dodge = CanAnimate(AnimationKind.ControlFeedback)
                && AprilFoolsService.Has(AprilFoolsService.Effect.DodgeLaunchButton);
            var chaos = CanAnimate(AnimationKind.Page)
                && AprilFoolsService.Has(AprilFoolsService.Effect.TileChaos);
            if (dodge) dodgeTimer.Start();
            else dodgeTimer.Stop();
            if (chaos) tileChaosTimer.Start();
            else
            {
                tileChaosTimer.Stop();
                foreach (var child in tileGrid.Children.OfType<Button>())
                    if (!dodge || child != launchTile) ResetTransform(child);
            }
            if (!dodge && !chaos) ResetTransform(launchTile);
        }
        void PreferencesChanged()
        {
            if (!subscribed || tileGrid.Dispatcher.HasShutdownStarted) return;
            if (tileGrid.Dispatcher.CheckAccess()) UpdateTimers();
            else tileGrid.Dispatcher.BeginInvoke(new Action(() => { if (subscribed) UpdateTimers(); }));
        }
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTimers();
        void WindowStateChanged(object? sender, EventArgs e) => UpdateTimers();
        void OwnerClosed(object? sender, EventArgs e) => Unloaded(tileGrid, new RoutedEventArgs());
        void Loaded(object sender, RoutedEventArgs e)
        {
            if (subscribed) return;
            subscribed = true;
            homeWindow = Window.GetWindow(tileGrid);
            AprilFoolsService.StateChanged += PreferencesChanged;
            AnimationSettingsService.Changed += PreferencesChanged;
            if (homeWindow != null)
            {
                homeWindow.IsVisibleChanged += VisibilityChanged;
                homeWindow.StateChanged += WindowStateChanged;
                homeWindow.Closed += OwnerClosed;
            }
            UpdateTimers();
        }
        void Unloaded(object sender, RoutedEventArgs e)
        {
            subscribed = false;
            AprilFoolsService.StateChanged -= PreferencesChanged;
            AnimationSettingsService.Changed -= PreferencesChanged;
            dodgeTimer.Stop();
            tileChaosTimer.Stop();
            foreach (var child in tileGrid.Children.OfType<Button>()) ResetTransform(child);
            ResetTransform(launchTile);
            if (homeWindow != null)
            {
                homeWindow.IsVisibleChanged -= VisibilityChanged;
                homeWindow.StateChanged -= WindowStateChanged;
                homeWindow.Closed -= OwnerClosed;
                homeWindow = null;
            }
        }
        tileGrid.Loaded += Loaded;
        tileGrid.Unloaded += Unloaded;
        tileGrid.IsVisibleChanged += VisibilityChanged;
        if (tileGrid.IsLoaded) Loaded(tileGrid, new RoutedEventArgs());

        launchTile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!AprilFoolsService.Has(AprilFoolsService.Effect.TileChaos)) return;
            e.Handled = true;
            MessageBoxDialog.ShowInfo("磁贴今天不开心，很不高兴为你启动游戏。", "提示");
        };
    }

    private static void EnsureTransform(Button button)
    {
        if (button.RenderTransform is not TranslateTransform)
        {
            button.RenderTransform = new TranslateTransform();
        }
    }
}
