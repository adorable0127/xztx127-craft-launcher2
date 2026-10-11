using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 轻量通知（Toast）：右下角浮出、几秒后自动消失、不阻塞任何操作。
///
/// ===== 为什么要有这个东西 =====
/// "游戏已启动"、"下载完成"、"已复制到剪贴板"这类**纯告知性**提示，过去走的是
/// MessageBoxDialog.ShowSuccess —— 那是个模态弹窗，会盖住整个界面、必须点"确定"才能继续。
/// 用户点了启动游戏、等游戏起来，回头还要再点一次确定，纯属多余的一步。
/// PCL 的做法是右下角冒一条、自己消失，这里对齐同样的体验。
///
/// 什么该用 Toast、什么该用模态弹窗，判断标准就一条：
///   **需要用户做决定的 → 模态弹窗；只是告诉用户一声的 → Toast。**
/// 具体说，只有三类该保留模态：
///   1) 需要用户在多个选项里选一个（选账户、选 Java、选整合包装到哪）
///   2) 破坏性操作的二次确认（删除版本、清空数据）
///   3) 必须让用户读完的错误（启动失败并附带原因）
/// 其余一律 Toast。
///
/// ===== 层次 =====
/// Toast 挂在 MainWindow.xaml 最后声明的 ToastHost 上，是整个可视化树的最顶层，
/// 压在 OverlayRoot（弹窗+遮罩）之上。这是有意的：弹窗开着的时候产生的操作反馈
/// 如果被弹窗遮住，用户就完全看不到了。
/// ToastHost 本身 IsHitTestVisible=False，不会挡住下面任何点击。
/// </summary>
public static class ToastService
{
    private static MainWindow? _host;
    private sealed class CardState
    {
        public required ItemsControl Owner;
        public DispatcherTimer? Timer;
        public int Generation;
        public bool Dismissing;
        public bool SlideOutLeft;
    }
    private static readonly Dictionary<Border, CardState> Cards = new();

    static ToastService()
    {
        AnimationSettingsService.Changed += () =>
        {
            if (AnimationSettingsService.IsEnabled(AnimationKind.Toast) || _host == null) return;
            void FinishTransitions()
            {
                foreach (var pair in Cards.ToArray())
                {
                    if (pair.Value.Dismissing) RemoveCard(pair.Key);
                    else StopAnimations(pair.Key, pair.Value);
                }
            }
            if (_host.Dispatcher.CheckAccess()) FinishTransitions();
            else if (!_host.Dispatcher.HasShutdownStarted)
                _host.Dispatcher.BeginInvoke(new Action(FinishTransitions));
        };
    }

    /// <summary>MainWindow 构造完成后注册一次，跟 OverlayDialogService.Register 同一个时机。</summary>
    public static void Register(MainWindow host)
    {
        if (_host != null && !ReferenceEquals(_host, host))
            foreach (var card in Cards.Keys.ToArray()) RemoveCard(card);
        _host = host;
    }

    private static void TrackCard(Border card, ItemsControl owner)
    {
        Cards.Add(card, new CardState { Owner = owner, SlideOutLeft = ReferenceEquals(owner, _host?.ActionToastHost) });
        card.Unloaded += Card_Unloaded;
    }

    private static void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border card) RemoveCard(card);
    }

    private static void StopAnimations(Border card, CardState state)
    {
        state.Generation++;
        card.BeginAnimation(UIElement.OpacityProperty, null);
        card.Opacity = 1;
        if (card.RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = 0;
        }
    }

    private static void ReleaseCard(Border card)
    {
        if (!Cards.Remove(card, out var state)) return;
        state.Timer?.Stop();
        state.Timer = null;
        StopAnimations(card, state);
        card.Unloaded -= Card_Unloaded;
    }

    private static void RemoveCard(Border card)
    {
        if (!Cards.TryGetValue(card, out var state))
        {
            _host?.ToastHost.Items.Remove(card);
            _host?.ActionToastHost.Items.Remove(card);
            return;
        }
        var owner = state.Owner;
        ReleaseCard(card);
        owner.Items.Remove(card);
    }

    private static TranslateTransform ShowCard(Border card, double direction)
    {
        var slide = new TranslateTransform();
        card.RenderTransform = slide;
        var state = Cards[card];
        StopAnimations(card, state);
        var intensity = AnimationSettingsService.Intensity(AnimationKind.Toast);
        if (intensity <= 0) return slide;

        var generation = state.Generation;
        var duration = AnimationSettingsService.Duration(180, AnimationKind.Toast);
        var fade = new DoubleAnimation(1 - 0.6 * intensity, 1, duration) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (Cards.TryGetValue(card, out var current) && current.Generation == generation)
                StopAnimations(card, current);
        };
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(direction * 20 * intensity, 0, duration)
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            }, HandoffBehavior.SnapshotAndReplace);
        card.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        return slide;
    }

    private static void StartLifetime(Border card, double seconds)
    {
        if (!Cards.TryGetValue(card, out var state)) return;
        state.Timer?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background, card.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0.05, seconds) : 3.5)
        };
        state.Timer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Cards.TryGetValue(card, out var current)) current.Timer = null;
            DismissCard(card);
        };
        timer.Start();
    }

    private static void DismissCard(Border card, double? slideTarget = null)
    {
        if (!Cards.TryGetValue(card, out var state) || state.Dismissing) return;
        state.Dismissing = true;
        card.IsHitTestVisible = false;
        if (card.IsMouseCaptured) card.ReleaseMouseCapture();
        card.ReleaseAllTouchCaptures();
        state.Timer?.Stop();
        state.Timer = null;
        if (!slideTarget.HasValue && state.SlideOutLeft)
            slideTarget = -Math.Max(card.ActualWidth, card.DesiredSize.Width) - 24;
        var intensity = AnimationSettingsService.Intensity(AnimationKind.Toast);
        if (intensity <= 0) { RemoveCard(card); return; }
        var from = card.Opacity;
        var fromX = (card.RenderTransform as TranslateTransform)?.X ?? 0;
        StopAnimations(card, state);
        var generation = state.Generation;
        var duration = AnimationSettingsService.Duration(150, AnimationKind.Toast);
        // 强度控制退场幅度；即使只轻微淡出，完成后也照常移除通知。
        var fade = new DoubleAnimation(from, from * (1 - intensity), duration)
        { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (Cards.TryGetValue(card, out var current) && current.Generation == generation)
                RemoveCard(card);
        };
        if (slideTarget.HasValue && card.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(fromX, fromX + (slideTarget.Value - fromX) * intensity, duration)
                {
                    FillBehavior = FillBehavior.Stop,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                }, HandoffBehavior.SnapshotAndReplace);
        card.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    public enum ToastKind { Info, Success, Warning, Error }

    /// <summary>显示一条 Toast。durationSeconds 到点后自动淡出移除。</summary>
    public static void Show(string message, ToastKind kind = ToastKind.Info, double durationSeconds = 3.5)
    {
        // 宿主还没准备好（比如启动过程中就有人报消息）时静默丢弃，
        // 绝不能因为一条提示没地方显示就抛异常打断主流程。
        if (_host?.ToastHost == null) return;

        if (!_host.Dispatcher.CheckAccess())
        {
            _host.Dispatcher.Invoke(() => Show(message, kind, durationSeconds));
            return;
        }

        try
        {
            var card = BuildCard(message, kind);
            TrackCard(card, _host.ToastHost);
            _host.ToastHost.Items.Add(card);

            // 同屏最多留 4 条，超出就把最老的挤掉——否则批量操作时会糊满整个右侧。
            while (_host.ToastHost.Items.Count > 4)
                if (_host.ToastHost.Items[0] is Border oldest) RemoveCard(oldest);
                else _host.ToastHost.Items.RemoveAt(0);

            ShowCard(card, 1);
            StartLifetime(card, durationSeconds);
        }
        catch
        {
            // 提示层出任何问题都不该影响主流程。
        }
    }

    public static void ShowSuccess(string message) => Show(message, ToastKind.Success);
    public static void ShowInfo(string message) => Show(message, ToastKind.Info);
    public static void ShowWarning(string message) => Show(message, ToastKind.Warning, 5);

    /// <summary>显示 Windows 系统原生通知（右下角系统通知中心）。</summary>
    public static void ShowSystemNotification(string message, string title = "XCL2")
    {
        try
        {
            if (_host?.Dispatcher.CheckAccess() == false)
            {
                _host.Dispatcher.Invoke(() => ShowSystemNotification(message, title));
                return;
            }

            // 使用 Windows 10+ Toast 通知
            var toastXml = $@"
<toast>
  <visual>
    <binding template='ToastGeneric'>
      <text>{System.Security.SecurityElement.Escape(title)}</text>
      <text>{System.Security.SecurityElement.Escape(message)}</text>
    </binding>
  </visual>
</toast>";

            var xmlDoc = new Windows.Data.Xml.Dom.XmlDocument();
            xmlDoc.LoadXml(toastXml);
            var toast = new Windows.UI.Notifications.ToastNotification(xmlDoc);
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("XCL2").Show(toast);
        }
        catch
        {
            // 降级到普通 Toast
            Show(message, ToastKind.Info);
        }
    }

    /// <summary>
    /// 左下角"操作类"通知：带 1-2 个按钮，点了按钮或点"关闭"之前不会自动消失
    /// （跟右下角纯告知性 Toast 的关键区别——这里的选择有实际后果，不该在用户还没看到、
    /// 还没决定之前就被计时器自动划走）。
    ///
    /// 典型用途：加载器版本转换/升级降级成功后，问"要不要删除这次操作前的自动备份"——
    /// 按钳2要求，这里默认文案会建议用户"先启动游戏验证一次，确认没问题了再删除"，
    /// 而不是鼓励用户一转换完就立刻删掉备份。
    /// </summary>
    /// <summary>同一个 key 的提示同时只保留一张卡片——比如"设置已保存"这种在短时间内
    /// 可能被连续触发好几次的提示，不应该每次都在原有那张还没消失时再叠一张新的上去，
    /// 那样会越堆越高、糊住下面的下拉框/按钮，甚至因为 ActionToastHost 本身可点击，
    /// 用户点下面的控件时其实点到了压在上面的卡片，导致"选择被打断"。
    /// key 为 null 时保持旧行为（允许堆叠），只有明确传了 key 的调用点才去重。</summary>
    public static void ShowActionPrompt(string message, string primaryText, Action primaryAction,
        string? secondaryText = null, Action? secondaryAction = null, string? hint = null,
        double? autoDismissSeconds = null, string? key = null, bool dismissible = false)
    {
        if (_host?.ActionToastHost == null) return;
        if (!_host.Dispatcher.CheckAccess())
        {
            _host.Dispatcher.Invoke(() => ShowActionPrompt(message, primaryText, primaryAction, secondaryText, secondaryAction, hint, autoDismissSeconds, key, dismissible));
            return;
        }

        try
        {
            // 去重：同 key 的旧卡片直接原地移除（不走淡出动画，避免和新卡片的淡入
            // 同时播放显得很跳），让新卡片顶替它的位置。
            if (key != null)
            {
                for (int i = _host.ActionToastHost.Items.Count - 1; i >= 0; i--)
                {
                    if (_host.ActionToastHost.Items[i] is Border b && Equals(b.Tag, key))
                        RemoveCard(b);
                }
            }

            var card = BuildActionCard(message, hint, primaryText, primaryAction, secondaryText, secondaryAction, dismissible);
            card.Tag = key;
            TrackCard(card, _host.ActionToastHost);
            _host.ActionToastHost.Items.Add(card);

            // 同屏最多留 3 条，避免用户攒了一堆没处理的通知糊满左下角。
            while (_host.ActionToastHost.Items.Count > 3)
                if (_host.ActionToastHost.Items[0] is Border oldest) RemoveCard(oldest);
                else _host.ActionToastHost.Items.RemoveAt(0);

            var slide = ShowCard(card, -1);
            if (dismissible) EnableSwipeToDismiss(card, slide);

            // 操作卡片默认不会自动消失；只有某个调用点明确传入 autoDismissSeconds 时才挂计时器。
            // 设置页的“自动保存 / 回退”和“未保存 / 保存 / 撤销”都不传该参数，因此会一直保留
            // 到用户主动操作，避免回退入口在用户来得及点击前消失。
            if (autoDismissSeconds is > 0)
                StartLifetime(card, autoDismissSeconds.Value);
        }
        catch (Exception ex)
        {
            // 提示层出任何问题都不该影响主流程，但绝不能悄无声息地吞掉——之前这里是空 catch，
            // "设置已修改"卡片如果因为某个资源/绑定问题在 BuildActionCard 阶段抛异常，
            // 用户会看到的现象就是"改了设置但什么提示都没弹"，且日志里完全没有痕迹，
            // 排查起来无从下手。改成记日志，方便定位类似问题。
            LauncherLogService.AppendLine($"[ToastService] ShowActionPrompt(key={key ?? "null"}) 失败：{ex}");
        }
    }

    /// <summary>按 key 主动移除一张操作类提示。用于设置页通过底部按钮、离页确认等
    /// “不是点击提示卡自身按钮”的路径保存/放弃后，清掉已经过期的 settings-dirty 卡片。</summary>
    public static void DismissActionPrompt(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || _host?.ActionToastHost == null) return;
        if (!_host.Dispatcher.CheckAccess())
        {
            _host.Dispatcher.Invoke(() => DismissActionPrompt(key));
            return;
        }

        for (int i = _host.ActionToastHost.Items.Count - 1; i >= 0; i--)
        {
            if (_host.ActionToastHost.Items[i] is Border b && Equals(b.Tag, key))
                DismissActionCard(b);
        }
    }

    private static void DismissActionCard(Border card)
        => DismissCard(card);

    /// <summary>支持按住卡片向左拖动（从右往左滑）关闭：拖过约 1/4 宽度松手即关闭，否则弹回。
    /// 按下后移动超过阈值才开始接管鼠标，所以卡片上的按钮仍可正常点击。</summary>
    private static void EnableSwipeToDismiss(Border card, TranslateTransform slide)
    {
        Point start = default;
        bool pressed = false, dragging = false;
        int? touchId = null;

        void RestorePosition()
        {
            if (!Cards.TryGetValue(card, out var state) || state.Dismissing) return;
            var fromX = slide.X;
            var fromOpacity = card.Opacity;
            StopAnimations(card, state);
            var intensity = AnimationSettingsService.Intensity(AnimationKind.Toast);
            if (intensity <= 0) return;
            var generation = state.Generation;
            var duration = AnimationSettingsService.Duration(150, AnimationKind.Toast);
            var restore = new DoubleAnimation(1 - (1 - fromOpacity) * intensity, 1, duration)
                { FillBehavior = FillBehavior.Stop };
            restore.Completed += (_, _) =>
            {
                if (Cards.TryGetValue(card, out var current) && current.Generation == generation)
                    StopAnimations(card, current);
            };
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(fromX * intensity, 0, duration)
                {
                    FillBehavior = FillBehavior.Stop,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                }, HandoffBehavior.SnapshotAndReplace);
            card.BeginAnimation(UIElement.OpacityProperty, restore, HandoffBehavior.SnapshotAndReplace);
        }

        bool Move(Point point)
        {
            if (!Cards.TryGetValue(card, out var state) || state.Dismissing) return false;
            var dx = point.X - start.X;
            var dy = point.Y - start.Y;
            if (!dragging)
            {
                // 垂直滚动和普通点击不应被卡片误判成关闭手势。
                if (dx > -8 || Math.Abs(dx) < Math.Abs(dy) * 1.25) return false;
                StopAnimations(card, state);
                dragging = true;
            }
            slide.X = Math.Min(0, dx);
            card.Opacity = Math.Max(0.3, 1 + slide.X / Math.Max(card.ActualWidth, 1));
            return true;
        }

        void FinishDrag()
        {
            if (slide.X < -Math.Max(card.ActualWidth, 1) / 4)
                DismissCard(card, -card.ActualWidth - 24);
            else
                RestorePosition();
        }

        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (touchId.HasValue) return;
            if (!Cards.TryGetValue(card, out var state) || state.Dismissing) return;
            pressed = true; dragging = false;
            start = e.GetPosition(state.Owner);
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (touchId.HasValue || !pressed || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed ||
                !Cards.TryGetValue(card, out var state) || state.Dismissing) return;
            // 用不随卡片移动的宿主坐标计算，避免平移卡片后 dx 反复归零而抖动。
            if (Move(e.GetPosition(state.Owner)))
            {
                if (!card.IsMouseCaptured) card.CaptureMouse();
                e.Handled = true;
            }
        };
        card.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var wasDragging = dragging;
            pressed = false; dragging = false;
            if (!wasDragging) return;
            card.ReleaseMouseCapture();
            e.Handled = true;
            FinishDrag();
        };
        card.LostMouseCapture += (_, e) =>
        {
            // 子按钮捕获转交给卡片时也会冒泡，只有卡片自身丢失捕获才取消手势。
            if (!ReferenceEquals(e.OriginalSource, card)) return;
            var restore = dragging;
            pressed = false; dragging = false;
            if (restore) RestorePosition();
        };
        card.PreviewTouchDown += (_, e) =>
        {
            if ((touchId.HasValue && dragging) || !Cards.TryGetValue(card, out var state) || state.Dismissing) return;
            touchId = e.TouchDevice.Id;
            start = e.GetTouchPoint(state.Owner).Position;
            pressed = false; dragging = false;
        };
        card.PreviewTouchMove += (_, e) =>
        {
            if (touchId != e.TouchDevice.Id || !Cards.TryGetValue(card, out var state)) return;
            if (Move(e.GetTouchPoint(state.Owner).Position))
            {
                e.TouchDevice.Capture(card);
                e.Handled = true;
            }
        };
        card.PreviewTouchUp += (_, e) =>
        {
            if (touchId != e.TouchDevice.Id) return;
            var wasDragging = dragging;
            touchId = null; dragging = false;
            if (!wasDragging) return; // 保留点按按钮的 WPF 鼠标提升行为。
            e.TouchDevice.Capture(null);
            e.Handled = true;
            FinishDrag();
        };
        card.LostTouchCapture += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, card) || touchId != e.TouchDevice.Id) return;
            var restore = dragging;
            touchId = null; dragging = false;
            if (restore) RestorePosition();
        };
    }

    private static Border BuildActionCard(string message, string? hint,
        string primaryText, Action primaryAction, string? secondaryText, Action? secondaryAction, bool dismissible = false)
    {
        var accent = (Brush)Application.Current.FindResource("SuccessTextBrush");

        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            MaxWidth = 320,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var content = new StackPanel();
        content.Children.Add(text);

        if (!string.IsNullOrEmpty(hint))
        {
            var hintBlock = new TextBlock
            {
                Text = hint,
                FontSize = 12,
                MaxWidth = 320,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(hintBlock);
        }

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

        Border card = null!;

        var primaryBtn = new Button { Content = primaryText, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        primaryBtn.SetResourceReference(Button.StyleProperty, "SecondaryButton");
        primaryBtn.Click += (_, _) =>
        {
            try { primaryAction(); } finally { DismissActionCard(card); }
        };
        buttonRow.Children.Add(primaryBtn);

        if (!string.IsNullOrEmpty(secondaryText))
        {
            var secondaryBtn = new Button { Content = secondaryText, Padding = new Thickness(12, 4, 12, 4) };
            secondaryBtn.SetResourceReference(Button.StyleProperty, "SecondaryButton");
            secondaryBtn.Click += (_, _) =>
            {
                try { secondaryAction?.Invoke(); } finally { DismissActionCard(card); }
            };
            buttonRow.Children.Add(secondaryBtn);
        }

        content.Children.Add(buttonRow);

        UIElement cardChild = content;
        if (dismissible)
        {
            // 右上角关闭按钮：只是收起提示卡，不保存也不撤销（未保存的修改仍保留，离开页面时会再确认）。
            content.Margin = new Thickness(0, 0, 22, 0);
            var closeBtn = new Button
            {
                Content = "✕",
                FontSize = 12,
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -8, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "关闭",
            };
            closeBtn.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            closeBtn.Click += (_, _) => DismissActionCard(card);
            var grid = new Grid();
            grid.Children.Add(content);
            grid.Children.Add(closeBtn);
            cardChild = grid;
        }

        card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 16, 12),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(3, 1, 1, 1),
            Child = cardChild,
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.BorderBrush = accent;

        return card;
    }

    private static Border BuildCard(string message, ToastKind kind)
    {
        var (glyph, brushKey) = kind switch
        {
            ToastKind.Success => ("✔", "SuccessTextBrush"),
            ToastKind.Warning => ("!", "WarningTextBrush"),
            ToastKind.Error => ("×", "DangerBrush"),
            _ => ("i", "AccentBrush"),
        };

        var accent = (Brush)Application.Current.FindResource(brushKey);

        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            MinWidth = 14,
            TextAlignment = TextAlignment.Center,
        };

        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            MaxWidth = 340,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 11, 16, 11),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 1),
            Child = row,
            // 左边一条 3px 的强调色竖线，用来一眼区分成功/警告/错误
            // （用 BorderThickness 的左边加粗 + BorderBrush 实现，不额外加元素）
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.BorderBrush = accent;
        card.BorderThickness = new Thickness(3, 1, 1, 1);

        return card;
    }
}
