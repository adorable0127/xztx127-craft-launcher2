using System.Windows;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>
/// 界面配色皮肤服务：在运行时修改 App.xaml 里那 10 个具名 SolidColorBrush 资源的
/// Color 值，而不是切换整份 ResourceDictionary。
///
/// 为什么这样做、不是别的方式：
/// - App.xaml 里的画刷都是不带 x:Shared="False" 的普通具名资源，全项目 20+ 个 XAML 文件
///   都通过 {StaticResource XxxBrush} 引用同一份实例。原本的设想是：不换对象、只改现有
///   SolidColorBrush 实例的 .Color 属性，因为 SolidColorBrush 是 Freezable，属性变更会
///   触发内部的 Changed 事件，所有引用同一个画刷实例的地方会自动重新渲染，不需要重启
///   窗口、不需要给每个页面加 INotifyPropertyChanged。
/// - 好处（理想情况下）：不用碰前面那 20+ 个已经写好的 XAML 文件，也不需要引入
///   DynamicResource (DynamicResource 性能更差，且和现有大量 ControlTemplate.Triggers
///   混用容易出显示不同步的坑)。
///
/// 实际踩到的坑（配色切换曾经完全不生效的根因）：
/// App.xaml 里几乎每一个 Style/ControlTemplate（包括 ControlTemplate.Triggers 里的
/// Setter）都通过 StaticResource 引用了这 10 个画刷。WPF 在某个 Style 第一次被套用到
/// 控件上时会 Seal 这个 Style，Seal 的过程会把它引用到的 Freezable 资源值一并冻结
/// (IsFrozen=true) 作为性能优化——这个冻结发生在 XAML 解析/控件首次应用样式阶段，
/// 跟这个服务的代码完全无关，项目里也没有任何地方显式调用过 Freeze()。冻结之后再想
/// 原地改 .Color 会直接抛异常/静默失败，"改现有实例"这条路对这批画刷实际上走不通。
/// 现在的做法（见下面 SetBrushColor）：遇到已冻结的画刷就换一个新的未冻结实例塞回同一个
/// key；同时因为已经渲染、Style 已 Seal 的旧控件不会自动感知这次资源字典替换，Apply
/// 末尾还会遍历当前所有打开的窗口做一次强制刷新，保证肉眼可见的界面立即变化。
///
/// 访客模式与手动选择的皮肤是两层独立状态：
/// - cfg.UiSkin 是用户在设置里手动选的"持久皮肤"(White/Blue/Yellow/Dark)，保存到配置文件。
/// - 访客模式开启期间，无论 cfg.UiSkin 是什么，界面强制显示为 Dark(黑色)，这是"访客模式"这个
///   功能本身的要求（开访客模式的场景往往是在别人电脑上用，黑色主题跟正常模式区分度更高，
///   一眼就能看出"现在是访客模式"，不容易忘记退出）；访客模式关闭后自动恢复回 cfg.UiSkin。
/// - 这两层状态都过 Apply 这一个入口，调用方不需要关心当前到底该显示哪个皮肤，
///   只需要在"访客模式开关变化"和"手动选择皮肤"这两个事件发生时调用 ApplyForCurrentState。
/// </summary>
public static class ThemeService
{
    public const string SkinWhite = "White";
    public const string SkinBlue = "Blue";
    public const string SkinYellow = "Yellow";
    public const string SkinDark = "Dark";

    public static readonly string[] AllSkins = { SkinWhite, SkinBlue, SkinYellow, SkinDark };

    private sealed record Palette(
        string Accent, string AccentHover, string Glow, string GlowSoft,
        string Panel, string Side, string Border, string BorderHover,
        string TextPrimary, string TextSecondary);

    // 每套皮肤只需要定义这 10 个颜色，其余全部界面元素都是从这 10 个资源派生出来的。
    private static readonly Dictionary<string, Palette> Palettes = new()
    {
        // 默认白色：原来 App.xaml 里写死的那套"科技感冷蓝"配色，原样保留。
        [SkinWhite] = new Palette(
            Accent: "#1868E8", AccentHover: "#0F52C4", Glow: "#00C2E8", GlowSoft: "#E3F7FC",
            Panel: "#FFFFFF", Side: "#F4F7FB", Border: "#D6E2F0", BorderHover: "#9DC0EC",
            TextPrimary: "#151B26", TextSecondary: "#6B7686"),

        // 蓝色皮肤：整体基调比白色皮肤更蓝一些，卡片背景带一点蓝灰而不是纯白，
        // 用来和默认白色皮肤做出可辨识的区分度（而不是只换了强调色、背景还是白的）。
        [SkinBlue] = new Palette(
            Accent: "#2F6FE0", AccentHover: "#2557B8", Glow: "#3FD1FF", GlowSoft: "#DCEBFF",
            Panel: "#EEF3FC", Side: "#DCE7FA", Border: "#BBD0F0", BorderHover: "#7FA8E8",
            TextPrimary: "#152238", TextSecondary: "#5A6B8C"),

        // 黄色皮肤：暖色调，强调色用琥珀黄，背景带一点米黄，避免纯白背景配黄色强调色
        // 时对比度不够、看起来像是"没换成功"。
        [SkinYellow] = new Palette(
            Accent: "#E0A020", AccentHover: "#C08010", Glow: "#FFD24D", GlowSoft: "#FFF3D6",
            Panel: "#FFFBF0", Side: "#FDF1D6", Border: "#F0DBA0", BorderHover: "#E8C468",
            TextPrimary: "#332600", TextSecondary: "#8A6D2E"),

        // 黑色/深色：与访客模式自动切换用的是同一套配色，保证"访客模式=黑"这个视觉锚点
        // 全局唯一，不会出现"访客模式黑色"和"手动选的黑色皮肤"其实是两种不同的黑，
        // 让用户混淆到底现在是不是在访客模式下。
        [SkinDark] = new Palette(
            Accent: "#3B8EFF", AccentHover: "#5FA3FF", Glow: "#00D4FF", GlowSoft: "#1B2A38",
            Panel: "#1A1D22", Side: "#121417", Border: "#33383F", BorderHover: "#4A5563",
            TextPrimary: "#E8ECF2", TextSecondary: "#9BA4B2"),
    };

    /// <summary>
    /// 根据"访客模式是否开启"+"用户手动选的持久皮肤"这两个状态，计算出当前应该显示
    /// 哪套配色并应用。访客模式开启时无条件覆盖为 Dark，不管 cfg.UiSkin 是什么；
    /// 关闭时用回 cfg.UiSkin（找不到/非法值时兜底为 White，不让配置文件被手改坏了之后
    /// 直接崩溃或者显示成完全没配色的默认灰）。
    /// </summary>
    public static void ApplyForCurrentState(bool guestModeEnabled, string? persistedSkin)
    {
        var effective = guestModeEnabled
            ? SkinDark
            : (Palettes.ContainsKey(persistedSkin ?? "") ? persistedSkin! : SkinWhite);
        Apply(effective);
    }

    private static void Apply(string skinName)
    {
        if (!Palettes.TryGetValue(skinName, out var p)) p = Palettes[SkinWhite];

        var res = Application.Current.Resources;
        SetBrushColor(res, "AccentBrush", p.Accent);
        SetBrushColor(res, "AccentHoverBrush", p.AccentHover);
        SetBrushColor(res, "GlowBrush", p.Glow);
        SetBrushColor(res, "GlowSoftBrush", p.GlowSoft);
        SetBrushColor(res, "PanelBrush", p.Panel);
        SetBrushColor(res, "SideBrush", p.Side);
        SetBrushColor(res, "BorderBrush2", p.Border);
        SetBrushColor(res, "BorderHoverBrush", p.BorderHover);
        SetBrushColor(res, "TextPrimaryBrush", p.TextPrimary);
        SetBrushColor(res, "TextSecondaryBrush", p.TextSecondary);

        RefreshOpenWindows();
    }

    /// <summary>
    /// 资源字典里的画刷换了新实例之后，已经渲染出来的窗口/控件光靠重绘（InvalidateVisual）
    /// 是不够的：Style 早就 Seal 过、Setter 解析出来的画刷引用已经作为"有效值"缓存在每个
    /// 控件的属性系统里，不会因为资源字典换了条目就自动重新查找。真正能让控件"回头重新
    /// 查一遍资源"的办法，是把它当前生效的 Style 摘掉再原样装回去——这会让 WPF 判定该
    /// 控件的样式发生了变化，从而重新走一遍 Setter 求值，拿到资源字典里现在最新的画刷。
    /// 对没有 Style 的元素（比如 TextBlock 直接用 Foreground="{StaticResource ...}"）额外
    /// 补一次 InvalidateVisual，覆盖它们直接引用画刷、但没有走 Style.Setter 的情况。
    /// </summary>
    private static void RefreshOpenWindows()
    {
        foreach (Window window in Application.Current.Windows)
        {
            RefreshVisualTree(window);
        }
    }

    private static void RefreshVisualTree(DependencyObject node)
    {
        if (node is FrameworkElement fe && fe.Style != null)
        {
            var style = fe.Style;
            fe.Style = null;
            fe.Style = style;
        }

        if (node is UIElement element) element.InvalidateVisual();

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            RefreshVisualTree(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>
    /// 就地修改已存在的 SolidColorBrush 实例的 Color，而不是往资源字典里塞一个新对象
    /// 替换掉旧的引用——见类注释，这是能不碰 XAML 文件、达到全局刷新效果的关键。
    /// 如果某个 key 因为版本差异等原因不存在，直接跳过，不影响其余画刷正常切换。
    ///
    /// 坑（配色切换不生效的根因）：App.xaml 里这些画刷同时被大量 Style/ControlTemplate 的
    /// Setter（含 ControlTemplate.Triggers 里的 Setter）通过 StaticResource 引用。WPF 在
    /// Style 第一次被使用时会 Seal 这个 Style，Seal 过程中会顺带把它引用到的 Freezable
    /// 资源值一起冻结掉（StyleHelper 的性能优化），这发生在 XAML 解析/控件首次应用样式的
    /// 阶段，跟这里的代码完全无关，也没有任何地方显式调用过 Freeze()。结果就是：只要某个
    /// 画刷被任何一个 Style.Setter 用过一次，brush.IsFrozen 就会变成 true，原来"跳过已冻结
    /// 画刷"的判断在实际运行时会把这 10 个画刷全部跳过去——配色皮肤选了、保存了，也不会有
    /// 任何视觉变化，这正是"皮肤没有应用"这个问题的根因。
    ///
    /// 修法：发现资源已被冻结时不再尝试原地改它（改不动了），而是换一个全新的、未冻结的
    /// SolidColorBrush 实例塞回资源字典的同一个 key。
    /// </summary>
    private static void SetBrushColor(ResourceDictionary res, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;

        if (res[key] is not SolidColorBrush brush) return;

        if (!brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        res[key] = new SolidColorBrush(color);
    }

    /// <summary>用于设置页"配色皮肤"下拉框的中文显示名，UI 展示用，不参与持久化。</summary>
    public static string GetDisplayName(string skin) => skin switch
    {
        SkinWhite => "白色（默认）",
        SkinBlue => "蓝色",
        SkinYellow => "黄色",
        SkinDark => "黑色",
        _ => skin
    };
}
