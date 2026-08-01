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
    /// <summary>银色系：新增。强调色用冷灰蓝调的金属银，浅色版带一点金属光泽感的冷灰。</summary>
    public const string SkinSilver = "Silver";
    /// <summary>金色系：新增。强调色用暖金色，浅色版带一点香槟金的暖调背景。</summary>
    public const string SkinGold = "Gold";
    /// <summary>绿宝石绿：新增。对标 Minecraft 游戏内绿宝石的鲜亮翠绿，比普通"绿色"更游戏感、
    /// 饱和度更高，跟已有色系里唯一沾绿的 TileGreen（仅用于卡片背景色块）区分开——
    /// 这是第一个把绿色作为主强调色的色系。</summary>
    public const string SkinEmerald = "Emerald";
    /// <summary>下界红：新增。对标 Minecraft 下界(Nether)的暗红偏橙基调，比常规大红更暗、更耐看，
    /// 补上现有色系里"暖色但不是黄/金"这一块空缺。</summary>
    public const string SkinNether = "Nether";
    /// <summary>末地石：新增。对标 Minecraft 末地(End)的浅黄绿偏灰基调，介于黄色系和绿宝石绿
    /// 之间的冷调过渡色，比现有任何一个色系都更"苍白/疏离"，符合末地那种空旷诡异的氛围。</summary>
    public const string SkinEndStone = "EndStone";
    /// <summary>暖黄色：新增。跟已有 SkinYellow(琥珀黄，偏冷一点的金黄)区分开，走更浓郁、
    /// 更偏橙调的"暖黄"路线，视觉上更接近向日葵/蜂蜜色而不是金属光泽的琥珀色。</summary>
    public const string SkinWarmYellow = "WarmYellow";
    /// <summary>亮橙色：新增。饱和度拉满的鲜橙色，比下界红更亮更跳、比暖黄更偏红，
    /// 补上"高饱和暖色但独立于红/黄两端"这个位置，适合喜欢高对比度界面的用户。</summary>
    public const string SkinOrange = "Orange";
    /// <summary>历史遗留色系常量：仅用于兼容"旧配置文件里 UiSkin 存的是 Dark"这种数据，
    /// 新增的色系选择 UI（设置页下拉框等）不再把它作为一个可选项列出——现在"要不要深色"
    /// 已经拆到 IsDarkMode 独立控制，不需要再单独占一个"色系"位置。</summary>
    public const string SkinDark = "Dark";

    /// <summary>提供给设置页"色系"下拉框遍历用的可选值，不包含 SkinDark（见上面注释，
    /// 深色已经拆成 IsDarkMode 独立维度，不再是一个单独的色系选项）。</summary>
    public static readonly string[] AllSkins = { SkinWhite, SkinBlue, SkinYellow, SkinPurple, SkinPink, SkinSilver, SkinGold, SkinEmerald, SkinNether, SkinEndStone, SkinWarmYellow, SkinOrange };

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

        // ------- 银色系：新增。强调色用冷灰蓝调的金属银，整体走"金属感"路线而不是某个
        // 鲜艳色相——浅色版背景带一点冷灰而不是纯白，强调色是偏蓝的银灰，区别于白色系
        // 那种纯科技蓝，视觉上更接近"拉丝金属"质感。 -------
        [(SkinSilver, false)] = new Palette(
            Accent: "#8A96A6", AccentHover: "#6E7A8C", Glow: "#C4CDD9", GlowSoft: "#EEF1F5",
            Panel: "#F7F8FA", Side: "#ECEEF2", Border: "#D3D8E0", BorderHover: "#AEB6C2",
            TextPrimary: "#20242B", TextSecondary: "#6B7280",
            TileBlue: "#EAEDF2", TileIndigo: "#ECEEF5", TileGreen: "#E7F0EA", TileOrange: "#F5EFE7", TilePurple: "#EDEAF2",
            SuccessText: "#1E9E4F", WarningText: "#D9822B", WarningBanner: "#F3F4F6", Danger: "#D64545", Divider: "#D3D8E0",
            ButtonBackground: "#DCE0E6", ButtonHoverBackground: "#CBD0D8", ButtonForeground: "#2A2F38"),

        // 银色系-深：深灰黑背景配亮银强调色，比白色系-深更冷、更"金属"，避免跟白色系-深
        // 那种偏中性的深色混淆——银色系-深的强调色明确带一点冷蓝灰，突出"抛光金属"质感。
        [(SkinSilver, true)] = new Palette(
            Accent: "#B8C2D0", AccentHover: "#CBD3DE", Glow: "#DCE3EC", GlowSoft: "#2A2E36",
            Panel: "#22252B", Side: "#17191E", Border: "#454B55", BorderHover: "#5C636F",
            TextPrimary: "#F0F2F5", TextSecondary: "#B4BAC4",
            TileBlue: "#2A3038", TileIndigo: "#2A2D38", TileGreen: "#213028", TileOrange: "#332C22", TilePurple: "#2C2A38",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#332C22", Danger: "#F0716F", Divider: "#454B55",
            ButtonBackground: "#3A4048", ButtonHoverBackground: "#484F59", ButtonForeground: "#E8ECF2"),

        // ------- 金色系：新增。强调色用暖金色，浅色版带一点香槟金背景，整体走"奢华暖调"路线。 -------
        [(SkinGold, false)] = new Palette(
            Accent: "#C8962C", AccentHover: "#A87A1E", Glow: "#F0C468", GlowSoft: "#FBF0D8",
            Panel: "#FFFCF4", Side: "#FBF1DA", Border: "#EAD9A8", BorderHover: "#DDBE70",
            TextPrimary: "#2E2308", TextSecondary: "#8A7440",
            TileBlue: "#EAF1FF", TileIndigo: "#EEF1FF", TileGreen: "#EAF6E2", TileOrange: "#FBEBD2", TilePurple: "#F2E9F8",
            SuccessText: "#1E9E4F", WarningText: "#B5651D", WarningBanner: "#FBF0D8", Danger: "#D64545", Divider: "#EAD9A8",
            ButtonBackground: "#F2E0AC", ButtonHoverBackground: "#EBD188", ButtonForeground: "#5C4310"),

        // 金色系-深：深棕黑背景配亮金强调色，比黄色系-深更沉稳、更接近"暗金属光泽"而不是
        // 明黄，强调色饱和度略降、亮度提高，保证在深背景上依然清晰可辨又不刺眼。
        [(SkinGold, true)] = new Palette(
            Accent: "#E0B454", AccentHover: "#EAC578", Glow: "#F0C468", GlowSoft: "#382C12",
            Panel: "#26200F", Side: "#191408", Border: "#4E4426", BorderHover: "#665A34",
            TextPrimary: "#F7F0DE", TextSecondary: "#C9BC93",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#213028", TileOrange: "#443616", TilePurple: "#2C2438",
            SuccessText: "#B8D45A", WarningText: "#F5B565", WarningBanner: "#443616", Danger: "#F0716F", Divider: "#4E4426",
            ButtonBackground: "#544620", ButtonHoverBackground: "#6C5B2A", ButtonForeground: "#F8ECC4"),

        // ------- 绿宝石绿：新增。对标游戏内绿宝石的鲜亮翠绿，比 TileGreen 那种柔和薄荷绿更
        // 饱和、更"宝石感"，浅色版背景带一点淡绿，强调色是接近游戏内绿宝石矿石的翠绿色。 -------
        [(SkinEmerald, false)] = new Palette(
            Accent: "#17A362", AccentHover: "#0F8650", Glow: "#4CD98A", GlowSoft: "#DFF6E9",
            Panel: "#F5FCF8", Side: "#E3F5EA", Border: "#BEE5CE", BorderHover: "#8AD1AC",
            TextPrimary: "#0F2A1C", TextSecondary: "#5C8A70",
            TileBlue: "#E3F0FF", TileIndigo: "#E9EEFF", TileGreen: "#D9F2E3", TileOrange: "#FFF1E3", TilePurple: "#F1E9FF",
            SuccessText: "#0F8650", WarningText: "#D9822B", WarningBanner: "#FFF7E6", Danger: "#D64545", Divider: "#BEE5CE",
            ButtonBackground: "#C0EAD3", ButtonHoverBackground: "#9ADDB9", ButtonForeground: "#0C4A2C"),

        // 绿宝石绿-深：深绿黑背景配亮翠绿强调色，保持"宝石在暗处发光"的视觉联想，
        // 比一般深色系多一分饱和度，避免显得像普通深灰绿而失去"宝石感"。
        [(SkinEmerald, true)] = new Palette(
            Accent: "#3ED88A", AccentHover: "#5EE6A0", Glow: "#4CD98A", GlowSoft: "#12301F",
            Panel: "#17241C", Side: "#0F1912", Border: "#2E4A38", BorderHover: "#3C614A",
            TextPrimary: "#E8F7EE", TextSecondary: "#A8CBB6",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#1B4230", TileOrange: "#45311A", TilePurple: "#2E2448",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#3D3220", Danger: "#F0716F", Divider: "#2E4A38",
            ButtonBackground: "#28503A", ButtonHoverBackground: "#356848", ButtonForeground: "#DEF7E7"),

        // ------- 下界红：新增。对标 Minecraft 下界(Nether)的暗红偏橙基调，比常规大红更暗、
        // 更耐看，浅色版背景带一点淡橙红（类似下界岩的暖调），强调色是深砖红而不是刺眼的
        // 正红，避免长时间使用显得过于警示/刺激。 -------
        [(SkinNether, false)] = new Palette(
            Accent: "#B8422E", AccentHover: "#96341F", Glow: "#E8703F", GlowSoft: "#FBE6DC",
            Panel: "#FFF9F6", Side: "#FBEBE3", Border: "#F0CDBB", BorderHover: "#E2A488",
            TextPrimary: "#331A10", TextSecondary: "#8C6250",
            TileBlue: "#E7F0FF", TileIndigo: "#EAF1FF", TileGreen: "#E7F6EC", TileOrange: "#FBE3D2", TilePurple: "#F1E9FF",
            SuccessText: "#1E9E4F", WarningText: "#B5651D", WarningBanner: "#FBEBE3", Danger: "#B8422E", Divider: "#F0CDBB",
            ButtonBackground: "#F2C7B0", ButtonHoverBackground: "#EAAD8C", ButtonForeground: "#5C2414"),

        // 下界红-深：深红棕黑背景（接近下界岩石缝里透出的暗光），强调色提亮成更明亮的
        // 橙红，保证深色背景下依然醒目，同时不撞常规 Danger 红——两者色相接近时特意
        // 让 Danger 保持独立的鲜红，跟 Accent 的暗橙红拉开区分度，避免"到底哪个是警告"混淆。
        [(SkinNether, true)] = new Palette(
            Accent: "#E8703F", AccentHover: "#F08858", Glow: "#F0955F", GlowSoft: "#3D2015",
            Panel: "#281A14", Side: "#1B110C", Border: "#54382A", BorderHover: "#6E4A38",
            TextPrimary: "#FAEEE6", TextSecondary: "#D6B3A0",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#1B4230", TileOrange: "#4A3018", TilePurple: "#2E2448",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#4A3018", Danger: "#F0716F", Divider: "#54382A",
            ButtonBackground: "#5C3A28", ButtonHoverBackground: "#744A32", ButtonForeground: "#FCE3D4"),

        // ------- 末地石：新增。对标 Minecraft 末地(End)那种苍白偏黄绿的石头基调，冷调、
        // 略带疏离感，跟黄色系(暖)、绿宝石绿(饱和)都拉开区分度——浅色版背景接近末地石本身
        // 的浅米黄灰，强调色是低饱和度的黄绿，刻意不做得鲜艳，符合末地空旷诡异的氛围。 -------
        [(SkinEndStone, false)] = new Palette(
            Accent: "#A8A468", AccentHover: "#8C8850", Glow: "#D6D2A0", GlowSoft: "#F3F1E0",
            Panel: "#FBFAF3", Side: "#F1EFDF", Border: "#DEDABE", BorderHover: "#C4BE94",
            TextPrimary: "#26241A", TextSecondary: "#7A7660",
            TileBlue: "#E7EEF0", TileIndigo: "#EAEEE8", TileGreen: "#EAF0DC", TileOrange: "#F5EEDA", TilePurple: "#EEEBE0",
            SuccessText: "#1E9E4F", WarningText: "#D9822B", WarningBanner: "#F5F2DE", Danger: "#D64545", Divider: "#DEDABE",
            ButtonBackground: "#E4DFB8", ButtonHoverBackground: "#D6D094", ButtonForeground: "#4A4626"),

        // 末地石-深：深灰绿黑背景，接近末地维度那种昏暗虚空的观感，强调色保留低饱和度的
        // 苍黄绿、不提亮太多，避免"末地石"这个疏离冷调的定位被做成普通鲜艳深色系。
        [(SkinEndStone, true)] = new Palette(
            Accent: "#C4BE7C", AccentHover: "#D2CC90", Glow: "#D6D2A0", GlowSoft: "#2C2A1E",
            Panel: "#212019", Side: "#161510", Border: "#48452F", BorderHover: "#5E5A3F",
            TextPrimary: "#F0EEE0", TextSecondary: "#B8B396",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#2A331E", TileOrange: "#3D3420", TilePurple: "#2E2448",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#3D3420", Danger: "#F0716F", Divider: "#48452F",
            ButtonBackground: "#4A4630", ButtonHoverBackground: "#5E5940", ButtonForeground: "#F2EFD8"),

        // ------- 暖黄色：新增。跟已有黄色系(琥珀色、偏金属光泽)区分开，走更浓郁的向日葵/
        // 蜂蜜暖调，浅色版背景更暖、更接近奶油黄而不是米黄，强调色饱和度更高、更偏橙一点。 -------
        [(SkinWarmYellow, false)] = new Palette(
            Accent: "#E8940F", AccentHover: "#C87A08", Glow: "#FFC94D", GlowSoft: "#FFEEC2",
            Panel: "#FFFAEE", Side: "#FEF0C8", Border: "#F5D889", BorderHover: "#EEBE50",
            TextPrimary: "#332400", TextSecondary: "#8C6A1E",
            TileBlue: "#FFF3D6", TileIndigo: "#FFF6E0", TileGreen: "#EDF3C8", TileOrange: "#FFE0B0", TilePurple: "#F5E6C8",
            SuccessText: "#6B8F1E", WarningText: "#B5651D", WarningBanner: "#FFE9B8", Danger: "#D64545", Divider: "#F5D889",
            ButtonBackground: "#FADB94", ButtonHoverBackground: "#F5C868", ButtonForeground: "#5C3D00"),

        // 暖黄色-深：深褐黑背景配明亮蜂蜜黄强调色，比黄色系-深更暖、更浓郁，强调色饱和度
        // 拉得更高一点，避免在深背景下显得跟黄色系-深太像。
        [(SkinWarmYellow, true)] = new Palette(
            Accent: "#FFB93D", AccentHover: "#FFC966", Glow: "#FFC94D", GlowSoft: "#40300F",
            Panel: "#2A2010", Side: "#1D1509", Border: "#5C4A22", BorderHover: "#78622E",
            TextPrimary: "#FAF0D8", TextSecondary: "#D6BE8C",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#3A3A17", TileOrange: "#4A3410", TilePurple: "#2E2448",
            SuccessText: "#B8D45A", WarningText: "#F5B565", WarningBanner: "#4A3410", Danger: "#F0716F", Divider: "#5C4A22",
            ButtonBackground: "#5C4614", ButtonHoverBackground: "#785C1E", ButtonForeground: "#FCEAB8"),

        // ------- 亮橙色：新增。饱和度拉满的鲜橙，浅色版背景带一点淡橙而不是暖黄那种奶油调，
        // 强调色比下界红更亮更跳、比暖黄更偏红，定位是"高对比度、精神抖擞"的橙色。 -------
        [(SkinOrange, false)] = new Palette(
            Accent: "#F0641A", AccentHover: "#D2500E", Glow: "#FF8F4D", GlowSoft: "#FFE4D2",
            Panel: "#FFFAF7", Side: "#FFEBDE", Border: "#F7C7A8", BorderHover: "#F0A470",
            TextPrimary: "#331A08", TextSecondary: "#8C5A38",
            TileBlue: "#E7F0FF", TileIndigo: "#EAF1FF", TileGreen: "#E7F6EC", TileOrange: "#FFE0C8", TilePurple: "#F1E9FF",
            SuccessText: "#1E9E4F", WarningText: "#B5651D", WarningBanner: "#FFE8D6", Danger: "#D64545", Divider: "#F7C7A8",
            ButtonBackground: "#FAC89E", ButtonHoverBackground: "#F5AC70", ButtonForeground: "#5C2E0C"),

        // 亮橙色-深：深棕黑背景配明亮橙强调色，是所有暖色系里最跳、最高对比度的深色版，
        // 跟下界红-深(暗红偏橙、更沉稳)明确区分开——亮橙色-深更纯粹地偏橙、亮度更高。
        [(SkinOrange, true)] = new Palette(
            Accent: "#FF8A4D", AccentHover: "#FFA370", Glow: "#FF8F4D", GlowSoft: "#3D2415",
            Panel: "#28190F", Side: "#1B1009", Border: "#543724", BorderHover: "#6E4A32",
            TextPrimary: "#FAECE2", TextSecondary: "#D6AF94",
            TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#1B4230", TileOrange: "#4A3018", TilePurple: "#2E2448",
            SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#4A3018", Danger: "#F0716F", Divider: "#543724",
            ButtonBackground: "#5C3A20", ButtonHoverBackground: "#744A2A", ButtonForeground: "#FCE3D0"),
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
        SkinSilver => "银色",
        SkinGold => "金色",
        SkinEmerald => "绿宝石绿",
        SkinNether => "下界红",
        SkinEndStone => "末地石",
        SkinWarmYellow => "暖黄色",
        SkinOrange => "亮橙色",
        SkinDark => "黑色",
        _ => skin
    };
}
