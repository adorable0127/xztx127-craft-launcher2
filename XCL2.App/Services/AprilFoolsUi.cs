using System;
using System.Diagnostics;
using System.Linq;
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
/// ===== 唯一的显示前提 =====
/// 本类里所有对外可见的效果（小白旗、乱窜按钮、乱跳磁贴、乱窜窗口……）最终都要经过
/// <see cref="AprilFoolsService.IsActive"/>，而它的第一个条件就是"今天是不是 4 月 1 日"
/// （见 AprilFoolsService.IsAprilFoolsDate）。也就是说：不是 4 月 1 日的话，
/// <see cref="AprilFoolsService.EnsureTodaysStateLoaded"/> 一开始就会把内部状态清空成
/// "什么都没抽中"，本类这边不管哪个方法被调用到，能查到的都是"没有生效的效果"，
/// 小白旗、乱窜、乱跳等等一律不会出现——不需要在每个效果方法里各自重复判断日期，
/// 只要 Attach 里那一次 Timer 回调按 IsActive 收起白旗，别的地方都已经天然被挡住了。
/// </summary>
public static class AprilFoolsUi
{
    private static DispatcherTimer? _watchdogTimer;
    private static DispatcherTimer? _windowChaosTimer;
    private static readonly Random Rng = new();

    private const string RickRoll1Url = "https://www.bilibili.com/video/BV1GJ411x7h7/";
    private const string RickRoll2Url = "https://www.bilibili.com/video/BV12rMQ6yEP2";

    /// <summary>MainWindow 构造函数里调用一次：挂上小白旗的显隐监听、窗口乱窜效果、
    /// F1 临时恢复热键。首页磁贴相关的效果（3 号鼠标躲避、8 号磁贴乱跳）由 HomePage 自己
    /// 在构造函数里调用 <see cref="AttachHomeTileEffects"/>，不在这里处理
    /// （HomePage 是后创建的 UserControl，这里拿不到它的控件引用）。</summary>
    public static void Attach(MainWindow owner)
    {
        RefreshFlagVisibility(owner);
        AprilFoolsService.StateChanged += () => owner.Dispatcher.BeginInvoke(() => RefreshFlagVisibility(owner));

        // 每 2 秒复查一次：防止某些极端情况下 StateChanged 事件没触发（比如刚好跨天但
        // 进程一直没重启），白旗显隐/窗口乱窜状态还是能自己收敛到正确状态。
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchdogTimer.Tick += (_, _) => RefreshFlagVisibility(owner);
        _watchdogTimer.Start();

        // 5 号：窗口乱窜 + 强制窗口模式。F11 拦截已经在 MainWindow.xaml.cs 里做了，
        // 这里只负责"随机挪动窗口位置"和"F1 临时恢复"。
        _windowChaosTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _windowChaosTimer.Tick += (_, _) =>
        {
            if (!AprilFoolsService.Has(AprilFoolsService.Effect.WindowChaos)) return;
            if (owner.WindowState == WindowState.Maximized) owner.WindowState = WindowState.Normal;
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            owner.Left = Rng.NextDouble() * Math.Max(0, screenWidth - owner.Width);
            owner.Top = Rng.NextDouble() * Math.Max(0, screenHeight - owner.Height);
        };
        _windowChaosTimer.Start();

        // F1：5 号效果生效期间临时"定住"窗口 3 秒，方便用户腾出手去点小白旗
        // （不直接恢复正常，只是给一个喘息窗口——真正恢复还是要点小白旗）。
        owner.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F1) return;
            if (!AprilFoolsService.Has(AprilFoolsService.Effect.WindowChaos)) return;
            e.Handled = true;
            if (_windowChaosTimer == null) return;
            _windowChaosTimer.Stop();
            var resumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            resumeTimer.Tick += (_, _) =>
            {
                resumeTimer.Stop();
                if (AprilFoolsService.Has(AprilFoolsService.Effect.WindowChaos)) _windowChaosTimer.Start();
            };
            resumeTimer.Start();
        };
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
            if (!AprilFoolsService.Has(AprilFoolsService.Effect.DodgeLaunchButton))
            {
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
            // 固定套路破解；跳跃距离本身也比警戒半径大出不少，保证跳完之后立刻脱离危险区。
            var angle = Math.Atan2(dy, dx) + (Rng.NextDouble() - 0.5) * 0.6;
            const double JumpDistance = 150;
            var newX = transform.X + Math.Cos(angle) * JumpDistance;
            var newY = transform.Y + Math.Sin(angle) * JumpDistance;

            // 限制偏移范围，别把按钮跳到 tileGrid 可见区域外面去，那样反而找不到它，
            // 也失去了"看得见点不到"的整蛊效果。
            var maxOffsetX = Math.Max(40, tileGrid.ActualWidth / 2 - launchTile.ActualWidth / 2);
            var maxOffsetY = Math.Max(40, tileGrid.ActualHeight / 2 - launchTile.ActualHeight / 2);
            transform.X = Math.Clamp(newX, -maxOffsetX, maxOffsetX);
            transform.Y = Math.Clamp(newY, -maxOffsetY, maxOffsetY);
        };
        dodgeTimer.Start();

        // 8 号：磁贴乱跳，用一个定时器随机给磁贴总控台里的每个磁贴一个小幅度随机偏移；
        // 点击"启动游戏"磁贴时不再走正常启动，改成弹一句台词。
        var tileChaosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        tileChaosTimer.Tick += (_, _) =>
        {
            if (!AprilFoolsService.Has(AprilFoolsService.Effect.TileChaos))
            {
                foreach (var child in tileGrid.Children.OfType<Button>())
                {
                    EnsureTransform(child);
                    var t = (TranslateTransform)child.RenderTransform;
                    t.X = 0;
                    t.Y = 0;
                }
                return;
            }
            foreach (var child in tileGrid.Children.OfType<Button>())
            {
                EnsureTransform(child);
                var t = (TranslateTransform)child.RenderTransform;
                t.X = (Rng.NextDouble() - 0.5) * 24;
                t.Y = (Rng.NextDouble() - 0.5) * 24;
            }
        };
        tileChaosTimer.Start();

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
