using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XCL2.App.Services;

/// <summary>
/// 统一的界面淡入/淡出过渡动画工具。
///
/// 背景：启动器里"最小化到托盘/从托盘还原""F11 全屏切换""侧边栏收起/展开"这几处操作
/// 之前都是状态一改、UI 立即跳变，被形容成"傻快傻快"——观感生硬。这里补一层轻量的
/// Opacity 淡入淡出，让状态切换看起来是"过渡"而不是"瞬移"。
///
/// 设计上刻意保持简单：
/// - 只动 Opacity，不碰 Width/Height/布局，所以不会跟 Grid 列宽动画那类复杂度纠缠，
///   出问题的面也很小。
/// - 统一受 <see cref="ConfigService"/> 里 EnableUiAnimations 开关控制：关闭时所有方法
///   直接把目标 Opacity 设好、同步执行回调，不播放任何动画，保证"关掉动画"后是真正的
///   零延迟，而不是把 Duration 改成 0 这种掩耳盗铃的做法。
/// </summary>
public static class UiAnimationHelper
{
    private static readonly Duration DefaultDuration = new(TimeSpan.FromMilliseconds(180));

    // 窗口级(打开/关闭/最小化/还原/最大化⇄还原)转场统一用这一套稍慢一点、带缓动的时长——
    // 比上面面板内容淡入淡出的 180ms 略长(220ms)是故意的：窗口级动作幅度更大(整个窗口内容
    // 淡入淡出+位移/缩放)，太快跟没做区别不大；SettleScale 那种"瞬间切换后接一个回弹"用的
    // 是更短的 160ms，因为原生 resize 已经瞬间发生，这里只是给已经变化完的画面一个收尾缓冲，
    // 不需要跟"从无到有"的开合动画一样长。
    private static readonly Duration WindowTransitionDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration SettleDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IEasingFunction WindowEaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction WindowEaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    // ConfigService 只有实例属性 Config，没有静态版本（见 ConfigService.Active 上的注释）；
    // 这类全局工具类要读配置，走的就是 ConfigService.Active?.Config 这条口径。
    // Active 理论上只有极早期启动阶段可能是 null，此时默认按"开启动画"处理。
    private static bool AnimationsEnabled => ConfigService.Active?.Config.EnableUiAnimations ?? true;

    /// <summary>
    /// 动画期间给整棵窗口树临时挂一层 BitmapCache：动画帧只拿这张现成位图做 GPU 合成，
    /// 不必逐帧重走布局+重绘，也避免 WindowState 切换瞬间露出还没画完的空白帧。
    ///
    /// 这里跟旧写法的关键区别是**复用同一个 BitmapCache 实例**，不是每次动画都 new 一个。
    /// 每 new 一次，WPF 都会把它当成一个全新的缓存节点，必须立刻把整棵树重新光栅化一遍；
    /// 而窗口级动画恰好总是紧跟在"刚刚改完窗口状态"之后触发，于是那一次全树光栅化就压在
    /// 切换那一帧上——本来是想消除掉帧，结果自己制造了一次明显的停顿。复用实例后，
    /// 第二次之后的过渡大多能直接命中已有的缓存节点，省掉这一次重建。
    ///
    /// 另外把 SnapsToDevicePixels 显式关掉：动画里元素会被缩放/位移到非整数像素上，
    /// 打开像素对齐会让 WPF 在每一帧重新做一次对齐计算，对这种纯过渡动画没有意义。
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, BitmapCache> WindowCaches = new();

    private static void BeginWindowCache(UIElement element)
    {
        if (element.CacheMode is BitmapCache) return;
        if (!WindowCaches.TryGetValue(element, out var cache))
        {
            cache = new BitmapCache();
            WindowCaches.Add(element, cache);
        }

        // RenderAtScale 必须跟当前显示器的 DPI 缩放一致，不能写死 1.0。
        //
        // 这是"开了动画之后文字会乱晃/发虚"的根因：在 125%/150% 缩放的屏幕上，
        // RenderAtScale=1.0 意味着整棵窗口树先按 100% 光栅化成一张位图，再被 WPF 放大到
        // 实际尺寸显示。文字在这一放大过程中会被重采样，笔画粗细和位置都跟非动画状态下
        // 对不上；动画每一帧的位移又都是非整数像素，于是看起来就是"字在抖"。
        // 按真实 DPI 光栅化之后，缓存位图和正常渲染是同一个像素网格，字不再重采样。
        //
        // SnapsToDevicePixels 也改回不强制关闭（留默认值）：关掉它省下的那点对齐计算，
        // 远不如文字糊掉的代价大。
        var scale = 1.0;
        try
        {
            // 元素还没挂到任何 PresentationSource 上时 GetDpi 会抛；那种情况按 1.0 处理即可，
            // 反正此时也还没有真正显示出来的画面需要保真。
            var dpi = VisualTreeHelper.GetDpi(element);
            if (dpi.DpiScaleX > 0) scale = dpi.DpiScaleX;
        }
        catch { }
        if (Math.Abs(cache.RenderAtScale - scale) > 0.001) cache.RenderAtScale = scale;

        element.CacheMode = cache;
    }

    /// <summary>动画播完（或被打断）后关掉临时缓存，恢复日常交互的正常渲染路径。
    /// 缓存对象本身留在 <see cref="WindowCaches"/> 里等下次复用，不销毁。</summary>
    private static void EndWindowCache(UIElement element)
    {
        element.CacheMode = null;
    }

    /// <summary>把 <paramref name="element"/> 从当前 Opacity 淡出到 0，完成后调用
    /// <paramref name="onCompleted"/>（典型用法：淡出完成后再真正 Hide()/Minimize()，
    /// 避免"内容还没淡完，窗口已经消失了"这种动画被打断的观感）。</summary>
    public static void FadeOut(UIElement element, Action? onCompleted = null, double from = 1.0)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = 0;
            onCompleted?.Invoke();
            return;
        }

        element.Opacity = from;
        var anim = new DoubleAnimation(from, 0, DefaultDuration) { FillBehavior = FillBehavior.HoldEnd };
        if (onCompleted != null)
            anim.Completed += (_, _) => onCompleted();
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>把 <paramref name="element"/> 从 0（或当前 Opacity）淡入到 <paramref name="to"/>。
    /// 典型用法：窗口/面板已经 Show()/Visible 之后立即调用，让内容"浮现"而不是直接满屏出现。</summary>
    public static void FadeIn(UIElement element, double to = 1.0, Action? onCompleted = null)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = to;
            onCompleted?.Invoke();
            return;
        }

        element.Opacity = 0;
        var anim = new DoubleAnimation(0, to, DefaultDuration) { FillBehavior = FillBehavior.HoldEnd };
        if (onCompleted != null)
            anim.Completed += (_, _) => onCompleted();
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>"先淡出旧内容、再淡入新内容"的组合动画，用于侧边栏收起/展开这类
    /// "内容本身要变、但容器还在"的场景：先把 <paramref name="element"/> 淡到 0，
    /// 在动画播放到一半（视觉上已经看不见）时执行 <paramref name="applyChange"/>
    /// （真正切换宽度/文案/图标等），随后再淡回 <paramref name="to"/>。</summary>
    public static void CrossFade(UIElement element, Action applyChange, double to = 1.0)
    {
        if (!AnimationsEnabled)
        {
            applyChange();
            element.Opacity = to;
            return;
        }

        FadeOut(element, onCompleted: () =>
        {
            applyChange();
            FadeIn(element, to);
        });
    }

    /// <summary>窗口"打开/从最小化恢复"用的淡入+从下方归位动画：Opacity 0→1 的同时，
    /// <paramref name="move"/>(挂在同一元素 RenderTransform 上的 TranslateTransform)的 Y
    /// 从一个小的正偏移(内容在正常位置下方一点)缓动回 0，制造"浮上来落位"的感觉，
    /// 而不是纯淡入那种"原地凭空出现"。跟 <see cref="FadeOutMove"/> 是一对，共用同一个
    /// TranslateTransform：开合时只动这一个变换，不会互相打架。</summary>
    public static void FadeInMove(UIElement element, TranslateTransform move, double offset = 16, Action? onCompleted = null)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            move.Y = 0;
            onCompleted?.Invoke();
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 0;
        move.Y = offset;

        BeginWindowCache(element);

        var opacityAnim = new DoubleAnimation(0, 1, WindowTransitionDuration)
            { EasingFunction = WindowEaseOut, FillBehavior = FillBehavior.HoldEnd };
        var moveAnim = new DoubleAnimation(offset, 0, WindowTransitionDuration)
            { EasingFunction = WindowEaseOut, FillBehavior = FillBehavior.HoldEnd };
        opacityAnim.Completed += (_, _) =>
        {
            EndWindowCache(element);
            onCompleted?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        move.BeginAnimation(TranslateTransform.YProperty, moveAnim);
    }

    /// <summary>窗口"关闭/最小化"前的淡出+向下沉动画，跟 <see cref="FadeInMove"/> 对称。
    /// 典型用法：先播这个动画，动画播完的回调里再真正 Close()/MinimizeWindow()，避免
    /// "按钮一点，窗口瞬间消失"这种硬切——用户反馈里说的"傻快傻快"就是指这种情况。</summary>
    public static void FadeOutMove(UIElement element, TranslateTransform move, double offset = 16, Action? onCompleted = null)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = 0;
            onCompleted?.Invoke();
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);

        BeginWindowCache(element);

        var opacityAnim = new DoubleAnimation(element.Opacity, 0, WindowTransitionDuration)
            { EasingFunction = WindowEaseIn, FillBehavior = FillBehavior.HoldEnd };
        var moveAnim = new DoubleAnimation(move.Y, offset, WindowTransitionDuration)
            { EasingFunction = WindowEaseIn, FillBehavior = FillBehavior.HoldEnd };
        opacityAnim.Completed += (_, _) =>
        {
            // onCompleted 之前执行：Minimize 场景下 SystemCommands.MinimizeWindow 会立刻
            // 触发原生最小化，紧接着的 StateChanged 里 UpdateMaximizeRestoreIcon 等逻辑
            // 不依赖 CacheMode，先清后调用顺序不影响功能，保持跟 FadeInMove 对称、动画
            // 结束就清缓存，避免忘记清理导致缓存意外常驻。
            EndWindowCache(element);
            onCompleted?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        move.BeginAnimation(TranslateTransform.YProperty, moveAnim);
    }

    /// <summary>最大化⇄还原(以及 Aero 贴靠/双击标题栏触发的同类状态切换)用的"回弹"收尾动画。
    /// WPF 没法真正给原生窗口的尺寸变化本身做平滑过渡——WindowState 一改，DWM 是整帧瞬间
    /// 切换新尺寸的，中间不存在能拿来插值的过程帧。这里退而求其次：状态切换完成后(新尺寸已经
    /// 生效那一刻)立刻让内容从"稍微缩小+半透明"回弹到正常的 Opacity=1/Scale=1，用短促的
    /// 淡入+缩放收尾衔接住这一下瞬间跳变，观感上从"硬切"变成"切换后有一下回弹落位"，
    /// 不是完整的移动动画，但足以缓解"傻快傻快"的生硬感。</summary>
    public static void SettleScale(UIElement element, ScaleTransform scale, Action? onCompleted = null)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            onCompleted?.Invoke();
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        // 起始不透明度从 0.55 提到 0.82、缩放从 0.975 提到 0.988：最大化/还原本来就是
        // "瞬间换尺寸"，这个动画的作用只是给那一下跳变一个收尾缓冲。幅度太大反而会让
        // 人觉得"整个界面闪了一下又长回来"，在本来就掉帧的机器上尤其明显；收窄幅度后
        // 需要合成的差异更小，观感更贴近系统原生窗口。
        element.Opacity = 0.82;
        scale.ScaleX = 0.988;
        scale.ScaleY = 0.988;

        BeginWindowCache(element);

        var opacityAnim = new DoubleAnimation(0.82, 1, SettleDuration)
            { EasingFunction = WindowEaseOut, FillBehavior = FillBehavior.HoldEnd };
        var scaleAnim = new DoubleAnimation(0.988, 1, SettleDuration)
            { EasingFunction = WindowEaseOut, FillBehavior = FillBehavior.HoldEnd };
        opacityAnim.Completed += (_, _) =>
        {
            EndWindowCache(element);
            onCompleted?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }
}
