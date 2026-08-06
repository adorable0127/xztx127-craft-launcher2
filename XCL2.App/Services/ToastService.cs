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

    /// <summary>MainWindow 构造完成后注册一次，跟 OverlayDialogService.Register 同一个时机。</summary>
    public static void Register(MainWindow host) => _host = host;

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
            _host.ToastHost.Items.Add(card);

            // 同屏最多留 4 条，超出就把最老的挤掉——否则批量操作时会糊满整个右侧。
            while (_host.ToastHost.Items.Count > 4)
                _host.ToastHost.Items.RemoveAt(0);

            // 淡入 + 从右侧滑入
            var slide = new TranslateTransform(24, 0);
            card.RenderTransform = slide;
            card.Opacity = 0;
            card.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            // 到点淡出并移除
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) =>
                {
                    if (_host?.ToastHost != null && _host.ToastHost.Items.Contains(card))
                        _host.ToastHost.Items.Remove(card);
                };
                card.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        }
        catch
        {
            // 提示层出任何问题都不该影响主流程。
        }
    }

    public static void ShowSuccess(string message) => Show(message, ToastKind.Success);
    public static void ShowInfo(string message) => Show(message, ToastKind.Info);
    public static void ShowWarning(string message) => Show(message, ToastKind.Warning, 5);

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
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = 0.22,
            BlurRadius = 16,
            ShadowDepth = 2,
        };

        return card;
    }
}
