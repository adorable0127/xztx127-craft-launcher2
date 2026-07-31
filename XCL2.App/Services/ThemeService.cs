using System.Windows;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>
/// 界面配色服务：在运行时修改 App.xaml 里那批具名 SolidColorBrush 资源的 Color 值，
/// 而不是切换整份 ResourceDictionary。
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
/// Setter）都通过 StaticResource 引用了这批画刷。WPF 在某个 Style 第一次被套用到
/// 控件上时会 Seal 这个 Style，Seal 的过程会把它引用到的 Freezable 资源值一并冻结
/// (IsFrozen=true) 作为性能优化——这个冻结发生在 XAML 解析/控件首次应用样式阶段，
/// 跟这个服务的代码完全无关，项目里也没有任何地方显式调用过 Freeze()。冻结之后再想
/// 原地改 .Color 会直接抛异常/静默失败，"改现有实例"这条路对这批画刷实际上走不通。
/// 现在的做法（见下面 SetBrushColor）：遇到已冻结的画刷就换一个新的未冻结实例塞回同一个
/// key；同时因为已经渲染、Style 已 Seal 的旧控件不会自动感知这次资源字典替换，Apply
/// 末尾还会遍历当前所有打开的窗口做一次强制刷新，保证肉眼可见的界面立即变化。
///
/// 配色现在拆成"色系(Hue)"+"明暗(IsDarkMode)"两个独立维度，而不是过去那种
/// White/Blue/Yellow/Dark 四选一的扁平列表：
/// - cfg.UiSkin 只决定色相：White/Blue/Yellow/Purple/Pink（Dark 仍作为色系常量保留，
///   兼容"旧配置文件里 UiSkin=Dark"这种历史数据，效果等同于 White 色相 + 深色模式）。
/// - cfg.IsDarkMode 独立决定这个色系显示浅色版还是深色版，双方组合、不互相覆盖——
///   比如色系选 Blue、IsDarkMode=true，就是"蓝色系-深"，色相还是蓝，只是背景/文字
///   对比度换成夜间友好的深色版本。
/// - 应用一切以用户当前的选择为准：包括访客模式期间也不再强制覆盖成任何固定配色——
///   访客模式只影响临时账户/会话清理这些行为，跟界面配色完全解耦，用户在访客模式下
///   开的是浅色就按浅色显示，开的是深色就按深色显示。
/// </summary>
public static class ThemeService
{
    public const string SkinWhite = "White";
    public const string SkinBlue = "Blue";
    public const string SkinYellow = "Yellow";
    public const string SkinPurple = "Purple";
    public const string SkinPink = "Pink";
    /// <summary>历史遗留色系常量：仅用于兼容"旧配置文件里 UiSkin 存的是 Dark"这种数据，
    /// 新增的色系选择 UI（设置页下拉框等）不再把它作为一个可选项列出——现在"要不要深色"
    /// 已经拆到 IsDarkMode 独立控制，不需要再单独占一个"色系"位置。</summary>
    public const string SkinDark = "Dark";

    /// <summary>提供给设置页"色系"下拉框遍历用的可选值，不包含 SkinDark（见上面注释，
    /// 深色已经拆成 IsDarkMode 独立维度，不再是一个单独的色系选项）。</summary>
    public static readonly string[] AllSkins = { SkinWhite, SkinBlue, SkinYellow, SkinPurple, SkinPink };

    private sealed record Palette(
        string Accent, string AccentHover, string Glow, string GlowSoft,
        string Panel, string Side, string Border, string BorderHover,
        string TextPrimary, string TextSecondary,
        string TileBlue, string TileIndigo, string TileGreen, string TileOrange, string TilePurple,
        string SuccessText, string WarningText, string WarningBanner, string Danger, string Divider,
        string ButtonBackground, string ButtonHoverBackground, string ButtonForeground);

    /// <summary>Key 是 (色系, 是否深色) 的组合；每个色系都各自有浅色版和深色版，
    /// 深色版只调整背景/文字/边框这些跟"看不看得清"直接相关的层次，强调色(Accent)/
    /// 光晕色(Glow)尽量保留原色相的辨识度，让人一眼看出"这仍然是蓝色系，只是深色模式"。</summary>
    private static readonly Dictionary<(string Hue, bool Dark), Palette> Palettes = new()
    {
        // ------- 白色系：浅色版是原来 App.xaml 里写死的"科技感冷蓝"配色，原样保留 -------
        [(SkinWhite, false)] = new Palette(
            Accent: "#1868E8", AccentHover: "#0F52C4", Glow: "#00C2E8", GlowSoft: "#E3F7FC",
            Panel: "#FFFFFF", Side: "#F4F7FB", Border: "#D6E2F0", BorderHover: "#9DC0EC",
            TextPrimary: "#151B26", TextSecondary: "#6B7686",
            TileBlue: "#E3F7FC", TileIndigo: "#EAF1FF", TileGreen: "#E7F6EC", TileOrange: "#FFF1E3", TilePurple: "#F1E9FF",
            SuccessText: "#1E9E4F", WarningText: "#D9822B", WarningBanner: "#FFF7E6", Danger: "#D64545", Divider: "#E0E0E0",
            ButtonBackground: "#D3E6FC", ButtonHoverBackground: "#B9D8FA", ButtonForeground: "#0F3F8C"),

        // 白色系-深：也就是原来独立的"黑色皮肤"，色相定位为中性/无色相的深色背景，
        // 跟"白色系"配对最自然（白色系本身强调色也是偏中性的科技蓝）。
        [(SkinWhite, true)] = new Palette(
            Accent: "#4C9AFF", AccentHover: "#6BAEFF", Glow: "#22D3F5", GlowSoft: "#1E3A4A",
            Panel: "#20242B", Side: "#16191E", Border: "#454C57", BorderHover: "#5D6675",
            TextPrimary: "#F2F4F8", TextSecondary: "#B7C0CC",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#1B4230", TileOrange: "#45311A", TilePurple: "#2E2448",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#3D3220", Danger: "#F0716F", Divider: "#454C57",
            ButtonBackground: "#2F4E70", ButtonHoverBackground: "#3C6088", ButtonForeground: "#E3F0FF"),

        // ------- 蓝色系：浅色版比白色系更蓝一些，卡片背景带一点蓝灰而不是纯白 -------
        [(SkinBlue, false)] = new Palette(
            Accent: "#2F6FE0", AccentHover: "#2557B8", Glow: "#3FD1FF", GlowSoft: "#DCEBFF",
            Panel: "#EEF3FC", Side: "#DCE7FA", Border: "#BBD0F0", BorderHover: "#7FA8E8",
            TextPrimary: "#152238", TextSecondary: "#5A6B8C",
            TileBlue: "#DCEBFF", TileIndigo: "#E2EBFF", TileGreen: "#DFF3E6", TileOrange: "#FFEBDA", TilePurple: "#EBE2FF",
            SuccessText: "#1E9E4F", WarningText: "#D9822B", WarningBanner: "#FFF3E0", Danger: "#D64545", Divider: "#BBD0F0",
            ButtonBackground: "#C7DDFA", ButtonHoverBackground: "#ADCBF5", ButtonForeground: "#173A73"),

        // 蓝色系-深：背景换成深蓝黑，强调色/光晕保留蓝色系的辨识度（比白色系-深更蓝一点，
        // 而不是跟白色系-深共用同一套中性灰黑，否则"蓝色系"选了深色模式后就看不出色相了）。
        [(SkinBlue, true)] = new Palette(
            Accent: "#4C8CFF", AccentHover: "#6BA2FF", Glow: "#3FD1FF", GlowSoft: "#1A2F52",
            Panel: "#1A2236", Side: "#12182A", Border: "#39456A", BorderHover: "#4E5D8A",
            TextPrimary: "#EEF2FA", TextSecondary: "#AEBBDA",
            TileBlue: "#1F3560", TileIndigo: "#232D57", TileGreen: "#1B4230", TileOrange: "#45311A", TilePurple: "#2E2448",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#3D3220", Danger: "#F0716F", Divider: "#39456A",
            ButtonBackground: "#2C4A80", ButtonHoverBackground: "#375C9C", ButtonForeground: "#E3EEFF"),

        // ------- 黄色系：暖色调，强调色琥珀黄，背景带一点米黄 -------
        [(SkinYellow, false)] = new Palette(
            Accent: "#E0A020", AccentHover: "#C08010", Glow: "#FFD24D", GlowSoft: "#FFF3D6",
            Panel: "#FFFBF0", Side: "#FDF1D6", Border: "#F0DBA0", BorderHover: "#E8C468",
            TextPrimary: "#332600", TextSecondary: "#8A6D2E",
            TileBlue: "#FFF3D6", TileIndigo: "#FFF6E0", TileGreen: "#EAF3D0", TileOrange: "#FFE7C2", TilePurple: "#F5E6C8",
            SuccessText: "#6B8F1E", WarningText: "#B5651D", WarningBanner: "#FFF0CC", Danger: "#D64545", Divider: "#F0DBA0",
            ButtonBackground: "#FBE6B0", ButtonHoverBackground: "#F7D888", ButtonForeground: "#5C3F00"),

        // 黄色系-深：深棕黑背景配暖黄强调色，避免直接用中性灰黑导致"黄色系"色相消失。
        [(SkinYellow, true)] = new Palette(
            Accent: "#F0B93D", AccentHover: "#F5CA66", Glow: "#FFD24D", GlowSoft: "#3D3016",
            Panel: "#28210F", Side: "#1B1608", Border: "#544A2A", BorderHover: "#6E6238",
            TextPrimary: "#F7F1E1", TextSecondary: "#CDBF9B",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#3A3A17", TileOrange: "#4A3416", TilePurple: "#2E2448",
            SuccessText: "#B8D45A", WarningText: "#F5B565", WarningBanner: "#4A3416", Danger: "#F0716F", Divider: "#544A2A",
            ButtonBackground: "#5C4A20", ButtonHoverBackground: "#725D2A", ButtonForeground: "#FCECC0"),

        // ------- 紫色系：新增。强调色用紫罗兰，背景带一点淡紫灰 -------
        [(SkinPurple, false)] = new Palette(
            Accent: "#8A4FD6", AccentHover: "#7038B8", Glow: "#C77DFF", GlowSoft: "#F1E7FF",
            Panel: "#FBF7FF", Side: "#F1E7FD", Border: "#E0CCF5", BorderHover: "#C8A2EA",
            TextPrimary: "#251A33", TextSecondary: "#77678C",
            TileBlue: "#E7F0FF", TileIndigo: "#ECE4FF", TileGreen: "#E7F6EC", TileOrange: "#FFF1E3", TilePurple: "#F1E7FF",
            SuccessText: "#1E9E4F", WarningText: "#D9822B", WarningBanner: "#FFF3E0", Danger: "#D64545", Divider: "#E0CCF5",
            ButtonBackground: "#E4D2F7", ButtonHoverBackground: "#D5B8F2", ButtonForeground: "#4A2A78"),

        // 紫色系-深：深紫黑背景，强调色提亮一档保证在深背景上足够醒目。
        [(SkinPurple, true)] = new Palette(
            Accent: "#B87CF0", AccentHover: "#C994F5", Glow: "#C77DFF", GlowSoft: "#33224A",
            Panel: "#241A30", Side: "#181022", Border: "#4A3861", BorderHover: "#614A7E",
            TextPrimary: "#F3EDFA", TextSecondary: "#C3B3D6",
            TileBlue: "#1C3547", TileIndigo: "#2B2450", TileGreen: "#1B4230", TileOrange: "#45311A", TilePurple: "#3A2C54",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#3D3220", Danger: "#F0716F", Divider: "#4A3861",
            ButtonBackground: "#4A3868", ButtonHoverBackground: "#5D4780", ButtonForeground: "#F0E3FF"),

        // ------- 粉色系：新增。强调色用玫瑰粉，背景带一点淡粉 -------
        [(SkinPink, false)] = new Palette(
            Accent: "#E0518F", AccentHover: "#C23C74", Glow: "#FF8FBE", GlowSoft: "#FFE7F1",
            Panel: "#FFF7FA", Side: "#FDE7F0", Border: "#F5C8DC", BorderHover: "#EDA0C4",
            TextPrimary: "#33121F", TextSecondary: "#8C6274",
            TileBlue: "#E7F0FF", TileIndigo: "#EAF1FF", TileGreen: "#E7F6EC", TileOrange: "#FFF1E3", TilePurple: "#F5E4F0",
            SuccessText: "#1E9E4F", WarningText: "#D9822B", WarningBanner: "#FFF3E0", Danger: "#D64545", Divider: "#F5C8DC",
            ButtonBackground: "#F7CEE0", ButtonHoverBackground: "#F2B0CE", ButtonForeground: "#7A1F4C"),

        // 粉色系-深：深紫红黑背景，强调色提亮保证辨识度。
        [(SkinPink, true)] = new Palette(
            Accent: "#F080AF", AccentHover: "#F49BC0", Glow: "#FF8FBE", GlowSoft: "#4A2233",
            Panel: "#2E1A24", Side: "#211018", Border: "#5C3A4A", BorderHover: "#764A5E",
            TextPrimary: "#FAEDF2", TextSecondary: "#D6B3C2",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#1B4230", TileOrange: "#45311A", TilePurple: "#3A2438",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#3D3220", Danger: "#F0716F", Divider: "#5C3A4A",
            ButtonBackground: "#5C3A4E", ButtonHoverBackground: "#744A62", ButtonForeground: "#FCE3EE"),
    };

    /// <summary>
    /// 根据用户当前的色系 + 明暗选择应用配色。一切以用户当前选择为准：包括访客模式期间
    /// 也不再强制覆盖成任何固定配色（访客模式只影响临时账户/会话清理，跟界面配色完全
    /// 解耦），传进来的 hue/isDark 是什么就显示什么。
    /// hue 找不到/非法值时兜底为 SkinWhite，不让配置文件被手改坏了之后直接崩溃或者
    /// 显示成完全没配色的默认灰；兼容历史数据：hue 等于旧的 SkinDark 常量时按
    /// "SkinWhite + 深色"处理。
    /// </summary>
    public static void ApplyForCurrentState(bool guestModeEnabled, string? persistedSkin, bool isDarkMode)
    {
        var hue = persistedSkin;
        if (hue == SkinDark)
        {
            // 兼容旧配置文件：以前 UiSkin=Dark 就代表纯黑深色，现在拆成两个维度后，
            // 等价写法是 "White 色系 + 深色模式"。
            hue = SkinWhite;
            isDarkMode = true;
        }
        if (hue is null || !Palettes.ContainsKey((hue, false)))
        {
            hue = SkinWhite;
        }

        Apply(hue, isDarkMode);
    }

    private static void Apply(string hue, bool isDark)
    {
        if (!Palettes.TryGetValue((hue, isDark), out var p))
        {
            p = Palettes[(SkinWhite, isDark)];
        }

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
        SetBrushColor(res, "TileBadgeBlueBrush", p.TileBlue);
        SetBrushColor(res, "TileBadgeIndigoBrush", p.TileIndigo);
        SetBrushColor(res, "TileBadgeGreenBrush", p.TileGreen);
        SetBrushColor(res, "TileBadgeOrangeBrush", p.TileOrange);
        SetBrushColor(res, "TileBadgePurpleBrush", p.TilePurple);
        SetBrushColor(res, "SuccessTextBrush", p.SuccessText);
        SetBrushColor(res, "WarningTextBrush", p.WarningText);
        SetBrushColor(res, "WarningBannerBrush", p.WarningBanner);
        SetBrushColor(res, "DangerBrush", p.Danger);
        SetBrushColor(res, "DividerBrush", p.Divider);
        SetBrushColor(res, "ButtonBackgroundBrush", p.ButtonBackground);
        SetBrushColor(res, "ButtonHoverBackgroundBrush", p.ButtonHoverBackground);
        SetBrushColor(res, "ButtonForegroundBrush", p.ButtonForeground);

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
    ///
    /// 这个方法是"设置项保存后必须在一秒内看到界面刷新"这条要求能够成立的关键：
    /// 每一次 ApplyForCurrentState 调用最终都会走到这里，保证配色/明暗相关的设置一保存、
    /// Apply 一执行完，当前所有已打开的窗口立即重新取到最新画刷，不需要用户切页/重启
    /// 才能看到效果。
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
    /// 画刷"的判断在实际运行时会把这批画刷全部跳过去——配色皮肤选了、保存了，也不会有
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

    /// <summary>用于设置页"色系"下拉框的中文显示名，UI 展示用，不参与持久化。</summary>
    public static string GetDisplayName(string skin) => skin switch
    {
        SkinWhite => "白色（默认）",
        SkinBlue => "蓝色",
        SkinYellow => "黄色",
        SkinPurple => "紫色",
        SkinPink => "粉色",
        SkinDark => "黑色",
        _ => skin
    };
}
