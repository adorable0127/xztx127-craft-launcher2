using System.Collections.Generic;
using System.Linq;
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
    public static void ApplyCustomAccent(Color color)
    {
        // “自定义”是完整主题色系，不只是临时覆盖 AccentBrush。先用白色主题作为中性底色，
        // 再把自定义色派生到按钮/悬停/光晕等资源；最后把 _currentHue 设回 Custom，
        // 这样后续调整透明度时不会把按钮颜色重置成白色主题。
        Apply(SkinWhite, CurrentIsDarkMode);
        _currentHue = SkinCustom;
        _currentCustomAccent = color;
        ApplyCustomAccentResources(color, CurrentIsDarkMode);
    }
    public const string SkinWhite = "White";
    public const string SkinBlue = "Blue";
    public const string SkinYellow = "Yellow";
    public const string SkinPurple = "Purple";
    public const string SkinPink = "Pink";
    public const string SkinCustom = "Custom";
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
    /// <summary>曜石黑：新增，"磨砂高级感"系列之一。对标黑曜石抛光后的哑光质感——不是纯黑，
    /// 而是带一点点冷蓝调的深灰黑，强调色用低饱和度的雾面蓝紫，避免像其它深色系那样
    /// 用高饱和亮色做强调，改用"压住饱和度、拉高灰度"的配色手法制造磨砂/哑光观感。
    /// 浅色版同理：不是纯白，而是浅灰调，模拟磨砂玻璃在光线下的浅灰哑光反光。</summary>
    public const string SkinObsidian = "Obsidian";
    /// <summary>雾灰蓝：新增，"磨砂高级感"系列之一。整体基调是低饱和度的雾蓝灰，视觉上像
    /// 蒙了一层薄雾的磨砂玻璃——背景色刻意选用带灰调而不是纯净的蓝，強调色也压低饱和度，
    /// 跟已有的 Blue/Silver 两个色系区分开：Blue 是鲜艳科技蓝，Silver 是冷调金属灰，
    /// Frostglass 介于两者之间、更"雾面"、更柔和，不追求鲜艳或金属反光而是磨砂朦胧感。</summary>
    public const string SkinFrostglass = "Frostglass";
    /// <summary>香槟灰：新增，"磨砂高级感"系列之一。暖灰调打底、强调色用低饱和度的雾面香槟金，
    /// 定位是"低调奢华"——跟已有的 Gold（鲜亮暖金）区分开，这里刻意把金色的饱和度压得
    /// 更低、混入更多灰度，做出"磨砂哑光金属"而不是"抛光反光金属"的观感，整体更内敛。</summary>
    public const string SkinChampagneFrost = "ChampagneFrost";
    /// <summary>珍珠白：新增，"高级白"系列之一。跟默认 SkinWhite(纯白背景+科技蓝强调色)不同，
    /// 这里背景不用纯白而是带一点点珠光灰调的米白，强调色也压低饱和度用柔和的雾面藕粉紫灰，
    /// 整体像打磨过的珍珠表面，观感更柔和内敛，不做默认皮肤那种高对比度的科技感。</summary>
    public const string SkinPearl = "Pearl";
    /// <summary>云杉白：新增，"高级白"系列之一。走"雪花石膏(Alabaster)"路线——极浅的暖白背景，
    /// 强调色是低饱和度的暖灰米色，几乎不带任何鲜艳色相，是所有色系里最接近"无色"的一个，
    /// 适合喜欢极简、几乎看不出主题色存在感的用户。</summary>
    public const string SkinAlabaster = "Alabaster";
    /// <summary>冰晶白：新增，"高级白"系列之一。冷调玻璃质感——背景带一点点极浅的蓝灰，
    /// 强调色用清冷的冰蓝，比默认白色系的科技蓝更浅、更透，模拟"磨砂冰晶/毛玻璃"在光线下
    /// 泛出的淡蓝冷光，定位介于"清透"和"高级冷淡"之间。</summary>
    public const string SkinGlacier = "Glacier";
    /// <summary>氧化铜绿：新增。对标 Minecraft 铜锭氧化后的经典青绿色（做旧铜/氧化铜方块），
    /// 强调色用略带灰调的青铜绿，背景带一点点暖灰打底，模拟铜器氧化后表面那种斑驳、
    /// 有历史感的哑光青绿，跟已有的 Emerald（鲜亮翠绿）区分开——这里更沉稳、更"做旧"，
    /// 是第一个走"金属氧化做旧"路线的色系。</summary>
    public const string SkinCopper = "Copper";
    /// <summary>深板岩：新增。对标 Minecraft 深板岩(Deepslate)的冷灰黑基调，比曜石黑更"石感"、
    /// 饱和度更低——曜石黑走的是磨砂玻璃/雾面蓝紫强调色的高级感路线，深板岩则是更朴素的
    /// 岩石灰黑，强调色用低饱和度的冷灰蓝，整体观感偏"矿洞/地底"，适合喜欢极简冷淡深色调、
    /// 又不想要曜石黑那种偏"珠光"质感的用户。</summary>
    public const string SkinDeepslate = "Deepslate";
    /// <summary>紫水晶：新增。对标 Minecraft 紫水晶洞的透亮紫粉色，强调色用饱和度较高的
    /// 水晶紫，比已有的 Purple（偏正紫）和 Pink（偏粉）都更"透光晶体感"，背景带一点点
    /// 极淡的紫灰打底模拟晶洞内壁的冷调，是第一个走"半透明水晶"路线而不是纯色块的色系。</summary>
    public const string SkinAmethyst = "Amethyst";
    /// <summary>海晶石：新增。对标 Minecraft 海底遗迹的海晶石方块，强调色是清澈的青绿蓝，
    /// 介于 Blue（纯蓝）和 Emerald（纯绿）之间但更"水感"，背景带浅浅的海水青打底，
    /// 跟偏"金属氧化"的 Copper、偏"科技"的 Blue 都区分开，是色轮上目前唯一的青色系。</summary>
    public const string SkinPrismarine = "Prismarine";
    /// <summary>熔岩：新增。对标 Minecraft 熔岩的炽热橙红，强调色饱和度拉到接近警示色的
    /// 程度，比 Nether（暗红偏橙、克制）和 Orange（鲜橙但不带"燃烧感"）都更强烈、更烫，
    /// 深色版背景压得很暗模拟熔洞环境，衬得强调色像真的在发光，适合喜欢强对比、高辨识度
    /// 界面的用户。</summary>
    public const string SkinLava = "Lava";
    /// <summary>青金石：新增。对标 Minecraft 青金石矿的深邃宝石蓝，比 Blue（明快科技蓝）
    /// 更深、更"矿物感"，背景带一点点靛蓝灰打底，深色版整体偏靛紫蓝，是目前色系里最深、
    /// 最浓郁的一个蓝色调，跟清透的 Prismarine、清冷的 Glacier 都拉开明显差异。</summary>
    public const string SkinLapis = "Lapis";
    /// <summary>水（清澈通透）：新增，专为「Windows 11 高级特效」准备的水主题。跟已有的
    /// Prismarine（海晶石，稳重青绿、面板不透） / Glacier（冰晶白，冷淡玻璃）都不同——
    /// 这里背景/面板本身就带极轻的水蓝透明基调，强调色用清澈的水蓝，专门为配合云母材质 +
    /// 更高透明度（面板透明度下限从 60 放宽到 40、可选整窗全局透明）设计，视觉上最接近
    /// "一整块清水玻璃"。开启 EnableWin11VisualEffects 后设置页会强制默认选中并锁定为
    /// 这一个色系（见 SettingsPage.xaml.cs VisualEffectsToggle_Changed），因为其它色系的
    /// 面板颜色不是按"水感"设计的，跟云母材质叠加在一起观感会很怪。</summary>
    public const string SkinAquatic = "Aquatic";
    /// <summary>历史遗留色系常量：仅用于兼容"旧配置文件里 UiSkin 存的是 Dark"这种数据，
    /// 新增的色系选择 UI（设置页下拉框等）不再把它作为一个可选项列出——现在"要不要深色"
    /// 已经拆到 IsDarkMode 独立控制，不需要再单独占一个"色系"位置。</summary>
    public const string SkinDark = "Dark";

    // ===== 彩蛋皮肤：不在 AllSkins 中列出，仅用于实验性功能区的彩蛋按钮 =====
    public const string SkinEggNote = "EggNote";
    public const string SkinEggDisco = "EggDisco";
    public const string SkinEggWheelchair = "EggWheelchair";
    public const string SkinEggDance = "EggDance";
    public const string SkinEggCantWin = "EggCantWin";
    public const string SkinEggBug = "EggBug";

    /// <summary>提供给设置页"色系"下拉框遍历用的可选值，不包含 SkinDark（见上面注释，
    /// 深色已经拆成 IsDarkMode 独立维度，不再是一个单独的色系选项）。</summary>
    public static readonly string[] AllSkins = { SkinWhite, SkinBlue, SkinYellow, SkinPurple, SkinPink, SkinSilver, SkinGold, SkinEmerald, SkinNether, SkinEndStone, SkinWarmYellow, SkinOrange, SkinObsidian, SkinFrostglass, SkinChampagneFrost, SkinPearl, SkinAlabaster, SkinGlacier, SkinCopper, SkinDeepslate, SkinAmethyst, SkinPrismarine, SkinLava, SkinLapis, SkinAquatic, SkinCustom };

    /// <summary>当前是否深色模式，Apply 每次调用时同步更新。供 WindowChromeService 在
    /// 新窗口刚创建（SourceInitialized）时查询"现在该用深色标题栏还是浅色标题栏"——
    /// 那个时间点可能跟 ThemeService.Apply 的调用时机不同步（比如窗口是在设置页已经
    /// 切换过深色模式之后才新打开的），需要一个随时可查的当前状态，而不是只能被动
    /// 等 RefreshOpenWindows 广播。</summary>
    public static bool CurrentIsDarkMode { get; private set; }

    /// <summary>当前应用中的色系（不含明暗），供 <see cref="ReapplyPanelAlpha"/> 在窗口透明度
    /// 开关/百分比变化、但色系本身没变时，知道该用哪个色系的原始面板颜色重新计算透明度，
    /// 不用每次都重新走一遍完整的 Apply（避免不必要地闪一下）。</summary>
    private static string _currentHue = SkinWhite;

    /// <summary>窗口透明度功能相关状态，见 AppConfig.EnableWindowTransparency 注释。
    /// 默认关闭 / 88%，跟 AppConfig 的默认值保持一致，避免 App.xaml.cs 忘记调用初始化方法时
    /// 出现两边不一致的情况。</summary>
    private static bool _transparencyEnabled;
    private static int _transparencyPercent = 88;

    /// <summary>主窗口是否正在使用用户导入的底图。底图存在时，即使用户没有额外开启
    /// “面板透明度”，主窗口的大面积面板也会自动保留一部分透明度，否则 SideBrush/PanelBrush
    /// 的不透明色会把底图完全盖住，看起来像“毛玻璃背景图片没有生效”。</summary>
    private static bool _customBackgroundActive;
    private static int _customBackgroundMaxPanelOpacityPercent = 78;

    /// <summary>当前 Custom 主题的主色。用于透明度重新计算时继续生成同色系按钮背景，
    /// 避免拖动面板透明度以后 Custom 主题的按钮颜色突然退回默认白色主题。</summary>
    private static Color _currentCustomAccent = Color.FromRgb(0x4C, 0x9A, 0xFF);

    /// <summary>全局（整窗级）透明度相关状态，见 AppConfig.EnableGlobalWindowTransparency 注释。
    /// 跟 _transparencyEnabled/_transparencyPercent（面板透明）是两套独立状态：这套通过
    /// Window.Opacity 让整个窗口（含标题栏、边框）一起变透明，而不是只调面板画刷的 alpha。
    /// 默认关闭 / 80%，跟 AppConfig 默认值保持一致。</summary>
    public static bool CurrentGlobalTransparencyEnabled { get; private set; }
    public static int CurrentGlobalOpacityPercent { get; private set; } = 80;

    private static int _textOpacityPercent = 100;
    public static int CurrentTextOpacityPercent => _textOpacityPercent;

    /// <summary>
    /// AppConfig.EnableWinUi3Design 对应的字体部分。修复"WinUI 3 新设计勾了没有任何效果"：
    /// 这个开关之前只在 SettingsPage 里读写配置字段，从没有任何代码消费过它。WinUI 3/Fluent
    /// 设计语言在排版上最直观、最不依赖 Windows 11 特定 DWM 版本的特征就是全局换用
    /// "Segoe UI Variable"这套可变字重字体（跟经典 Segoe UI 相比字重更细腻、数字更现代），
    /// 这里用一个全局隐式 Style（只在第一次调用时插入一次）把 Control/TextBlock 的
    /// FontFamily 都绑定到一个动态资源上，开关切换时只需要改这一个资源的值，不需要
    /// 逐个控件模板去改——旧版本 Windows 上如果系统没有装 Segoe UI Variable 这套字体，
    /// FontFamily 的候选列表里紧跟着写了 Segoe UI 兜底，不会因为找不到主字体就报错或
    /// 显示成方块字。窗口圆角部分见 Win11EffectsService.CurrentWinUi3Enabled。
    /// </summary>
    private const string WinUi3FontResourceKey = "AppFontFamily";
    private static bool _winUi3FontStyleInstalled;

    /// <summary>
    /// 界面字体的唯一入口：设置页保存时 + 启动时调用。
    ///
    /// 原来只有 WinUi3 开关一种影响字体的途径（切到 Segoe UI Variable）。现在加了独立的
    /// "界面字体"下拉设置（见 SettingsPage AppFontCombo / AppConfig.AppFontFamily），
    /// 两者需要合流成同一个最终 FontFamily 字符串，而不是互相覆盖谁后调用谁生效：
    /// WinUi3 负责最前面加一段"Segoe UI Variable"候选（提升英文/数字观感），用户选的
    /// 字体（或者"跟随系统"的空值）放在后面当主字体+兜底链。
    ///
    /// 需求："修复部分字显示不全"：之前 WinUi3 开启时 FontFamily 候选链是
    /// "Segoe UI Variable Text, Segoe UI Variable, Segoe UI"——这三个全是西文字体，
    /// 一个中文字形都不包含。WPF 找不到字形时会用系统里第一个能显示该字符的字体顶上，
    /// 不同 Windows 语言版本/字体安装情况下顶上来的字体行高、字宽跟界面预留的行高不一致，
    /// 表现出来就是部分中文字符（尤其是带下沉笔画的字，比如"辶"旁的字）在按钮/标签里显得
    /// 被"切掉了一点底"。解决办法是候选链末尾必须显式加一个覆盖全的中文字体兜底
    /// （微软雅黑 UI／微软雅黑），而不是依赖系统"随便找一个"的隐式回退。
    /// </summary>
    private static string _currentAppFontFamily = "";
    private static bool _currentWinUi3FontEnabled;

    public static void ApplyFontFamily(string? appFontFamily, bool winUi3Enabled)
    {
        _currentAppFontFamily = appFontFamily ?? "";
        _currentWinUi3FontEnabled = winUi3Enabled;
        ApplyComposedFontFamily();
    }

    /// <summary>兼容旧调用点：单独切 WinUi3 开关时，沿用上一次设置的"界面字体"选择，
    /// 不需要每个旧调用点都改成传两个参数。</summary>
    public static void ApplyWinUi3Typography(bool enabled) => ApplyFontFamily(_currentAppFontFamily, enabled);

    /// <summary>
    /// "界面字体"下拉的候选值 → 实际 FontFamily 字符串。空字符串/"跟随系统"选项对应
    /// 空字符串，不额外指定主字体，只靠下面统一加的中文兜底避免部分字形丢失。
    /// </summary>
    public static readonly (string Tag, string DisplayName, string FontStack)[] FontFamilyOptions =
    {
        ("", "跟随系统", ""),
        ("MicrosoftYaHeiUI", "微软雅黑 UI", "Microsoft YaHei UI"),
        ("MicrosoftYaHei", "微软雅黑", "Microsoft YaHei"),
        ("DengXian", "等线", "DengXian"),
        ("SourceHanSans", "思源黑体", "Source Han Sans SC, Noto Sans CJK SC"),
        ("PingFang", "苹方", "PingFang SC"),
    };

    /// <summary>
    /// 需求排查："有时修改字体不成功"：根因是 WPF 隐式样式（Implicit Style）按"控件的
    /// 精确类型"匹配，不会顺着继承链往上找、更不会跟基类样式合并——App.xaml 里除了
    /// Control/TextBlock 这两个基类样式外，还给 Button/TextBox/ComboBox/CheckBox/
    /// RadioButton/ListBox/... 等十几个具体类型各自定义了一份不带 x:Key 的隐式样式
    /// （用来统一配色/圆角，见各自 Style 上面的注释）。只要某个类型自己有一份精确匹配的
    /// 隐式样式，WPF 就只会用这一份、完全不会再去找 Control 这个基类样式，之前只往
    /// resources[typeof(Control)] 塞 FontFamily 的写法对这些类型根本不生效——按钮、
    /// 输入框、下拉框这些恰恰是界面里最常见的控件，所以表现出来就是"设置里选了新字体，
    /// 但按钮/输入框上的文字看起来完全没变，只有少数'裸' Control（没有专属隐式样式的
    /// 类型）跟着变了"，很容易被误以为是"有时候不生效"（其实是稳定地对这批类型不生效）。
    ///
    /// 解决方式：不再是"新建一份只含 FontFamily 的样式、整份覆盖掉原有隐式样式"（那样会
    /// 连带丢掉原来该类型样式里已有的 Background/圆角/Trigger 等 Setter），而是把
    /// "当前已经生效的那份样式"存下来当 BasedOn 基类，只追加一条 FontFamily 的
    /// DynamicResource Setter 上去——原有的所有 Setter/Trigger/ControlTemplate 都还在，
    /// 只是多了一条会跟着 AppFontFamily 资源联动的字体规则。覆盖面从原来的 2 个类型
    /// 扩到了 App.xaml 里实际定义过隐式（不带 x:Key）样式的全部类型，保证"选哪个控件
    /// 都能跟着换字体"，不再有遗漏。
    /// </summary>
    private static readonly Type[] FontAwareImplicitStyleTypes =
    {
        typeof(System.Windows.Controls.Control),
        typeof(System.Windows.Controls.TextBlock),
        typeof(System.Windows.Controls.Label),
        typeof(System.Windows.Controls.Button),
        typeof(System.Windows.Controls.TextBox),
        typeof(System.Windows.Controls.PasswordBox),
        typeof(System.Windows.Controls.ComboBox),
        typeof(System.Windows.Controls.ComboBoxItem),
        typeof(System.Windows.Controls.ListBox),
        typeof(System.Windows.Controls.ListBoxItem),
        typeof(System.Windows.Controls.ProgressBar),
        typeof(System.Windows.Controls.TabItem),
        typeof(System.Windows.Controls.TabControl),
        typeof(System.Windows.Controls.CheckBox),
        typeof(System.Windows.Controls.RadioButton),
        typeof(System.Windows.Controls.Expander),
        typeof(System.Windows.Controls.GridViewColumnHeader),
        typeof(System.Windows.Controls.ContextMenu),
        typeof(System.Windows.Controls.MenuItem),
        typeof(System.Windows.Controls.ToolTip),
        typeof(Window),
    };

    private static void ApplyComposedFontFamily()
    {
        var selected = FontFamilyOptions.FirstOrDefault(o => o.Tag == _currentAppFontFamily);
        var userStack = selected.FontStack;

        var parts = new List<string>();
        if (_currentWinUi3FontEnabled) parts.Add("Segoe UI Variable Text, Segoe UI Variable");
        if (!string.IsNullOrEmpty(userStack)) parts.Add(userStack);
        // 兜底链固定放最后：西文字体（Segoe UI）+ 中文字体（微软雅黑 UI/微软雅黑）+ 系统默认，
        // 保证无论前面选了什么，缺失的中/英文字形都有地方补，不会显示成方块或被裁切。
        parts.Add("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

        var resources = Application.Current.Resources;
        resources[WinUi3FontResourceKey] = new FontFamily(string.Join(", ", parts));

        // 只需要"装一次"：字体规则是 DynamicResource，后续切换字体只用改上面那行
        // WinUi3FontResourceKey 对应的值，所有已装好的样式会自动跟着联动，不需要重新
        // 生成/重新挂载样式对象。
        if (_winUi3FontStyleInstalled) return;

        foreach (var type in FontAwareImplicitStyleTypes)
        {
            // 保留该类型原来已经存在的隐式样式（App.xaml 里定义的配色/模板等）当 BasedOn 基类，
            // 只追加字体 Setter；该类型原来没有专属隐式样式时（BasedOn 为 null）则新建一份，
            // 效果等价于"从这个类型开始跟随 AppFontFamily"。
            var existing = resources.Contains(type) ? resources[type] as Style : null;
            var fontStyle = new Style(type, existing);
            fontStyle.Setters.Add(new Setter(System.Windows.Controls.Control.FontFamilyProperty,
                new DynamicResourceExtension(WinUi3FontResourceKey)));
            resources[type] = fontStyle;
        }

        _winUi3FontStyleInstalled = true;
    }

    /// <summary>参与"窗口透明度"效果的面板类画刷 key：只挑背景大面积色块（侧栏、主面板、
    /// 普通按钮背景），不动强调色/文字/危险色这些需要保持高对比度、一眼看清的画刷——
    /// 这些如果也跟着变淡，按钮上的文字、警告色块会变得难以辨认。</summary>
    private static readonly string[] TransparencyParticipatingBrushKeys =
    {
        "PanelBrush", "SideBrush", "ButtonBackgroundBrush"
    };

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
    // 白色系配色方案（复用给彩蛋皮肤）
    private static readonly Palette WhiteLight = new(
        Accent: "#5B9BF2", AccentHover: "#4488EB", Glow: "#00C2E8", GlowSoft: "#E3F7FC",
        Panel: "#FFFFFF", Side: "#F3F6FA", Border: "#BCCBDB", BorderHover: "#7FA7D8",
        TextPrimary: "#0B1220", TextSecondary: "#465568",
        TileBlue: "#E3F7FC", TileIndigo: "#EAF1FF", TileGreen: "#E7F6EC", TileOrange: "#FFF1E3", TilePurple: "#F1E9FF",
        SuccessText: "#1E8E49", WarningText: "#B96513", WarningBanner: "#FFF5DD", Danger: "#C93636", Divider: "#D2DAE4",
        ButtonBackground: "#CDE2FA", ButtonHoverBackground: "#B3D3F7", ButtonForeground: "#0A356F");

    private static readonly Palette WhiteDark = new(
        Accent: "#4C9AFF", AccentHover: "#6BAEFF", Glow: "#22D3F5", GlowSoft: "#1E3A4A",
        Panel: "#20242B", Side: "#16191E", Border: "#454C57", BorderHover: "#5D6675",
        TextPrimary: "#F2F4F8", TextSecondary: "#B7C0CC",
        TileBlue: "#1C3547", TileIndigo: "#212D4A", TileGreen: "#1B4230", TileOrange: "#45311A", TilePurple: "#2E2448",
        SuccessText: "#5FE092", WarningText: "#F5B565", WarningBanner: "#3D3220", Danger: "#F0716F", Divider: "#454C57",
        ButtonBackground: "#2F4E70", ButtonHoverBackground: "#3C6088", ButtonForeground: "#E3F0FF");

    private static readonly Dictionary<(string Hue, bool Dark), Palette> Palettes = new()
    {
        // ------- 白色系：浅色版是原来 App.xaml 里写死的"科技感冷蓝"配色，原样保留 -------
        [(SkinWhite, false)] = WhiteLight,

        // 白色系-深：也就是原来独立的"黑色皮肤"，色相定位为中性/无色相的深色背景，
        // 跟"白色系"配对最自然（白色系本身强调色也是偏中性的科技蓝）。
        [(SkinWhite, true)] = WhiteDark,

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

        // ------- 曜石黑：新增，"磨砂高级感"系列之一。刻意压低饱和度、拉高灰度模拟哑光/
        // 磨砂质感，跟已有 Dark/Silver 深色系的差别是：强调色不追求鲜艳醒目，而是用
        // 灰紫调"雾面蓝紫"，浅色版背景也不是纯白而是浅灰白，制造"隔了一层磨砂玻璃看东西"
        // 的朦胧观感，而不是强对比度的清晰科技感。 -------
        [(SkinObsidian, false)] = new Palette(
            Accent: "#6B6E85", AccentHover: "#565A70", Glow: "#9296AC", GlowSoft: "#EDEDF2",
            Panel: "#F5F5F7", Side: "#E9E9EE", Border: "#D2D2DC", BorderHover: "#AFAFC0",
            TextPrimary: "#232330", TextSecondary: "#6E6E80",
            TileBlue: "#E9EAF0", TileIndigo: "#EAEAF2", TileGreen: "#E6EDE9", TileOrange: "#F0EAE6", TilePurple: "#EBE8F0",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#EDEDF2", Danger: "#C24B4B", Divider: "#D2D2DC",
            ButtonBackground: "#D8D8E0", ButtonHoverBackground: "#C6C6D2", ButtonForeground: "#2A2A38"),

        // 曜石黑-深：接近纯黑但带一点点冷紫调的哑光深色，是全部深色版里饱和度最低的一个，
        // 强调色只比背景亮一档，刻意不做强对比、追求"哑光黑曜石抛光面"那种低调质感。
        [(SkinObsidian, true)] = new Palette(
            Accent: "#9296AC", AccentHover: "#A6AABC", Glow: "#9296AC", GlowSoft: "#26262E",
            Panel: "#1C1C22", Side: "#131316", Border: "#3A3A44", BorderHover: "#4E4E5A",
            TextPrimary: "#E8E8EE", TextSecondary: "#A0A0B0",
            TileBlue: "#26262E", TileIndigo: "#26262E", TileGreen: "#20262A", TileOrange: "#282422", TilePurple: "#26242E",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#282422", Danger: "#EE7E7C", Divider: "#3A3A44",
            ButtonBackground: "#34343E", ButtonHoverBackground: "#40404C", ButtonForeground: "#E4E4EC"),

        // ------- 雾灰蓝：新增，"磨砂高级感"系列之一。低饱和雾蓝灰打底，介于 Blue(鲜艳科技蓝)
        // 和 Silver(冷调金属灰)之间，走"磨砂玻璃蒙雾"路线——背景/边框都刻意混入灰度，
        // 不追求清透或反光，而是柔和朦胧的雾面质感。 -------
        [(SkinFrostglass, false)] = new Palette(
            Accent: "#5E85A8", AccentHover: "#4A6D8C", Glow: "#8FB2CE", GlowSoft: "#EAF0F5",
            Panel: "#F5F8FA", Side: "#E8EEF2", Border: "#CBD8E0", BorderHover: "#A8BDCC",
            TextPrimary: "#20272E", TextSecondary: "#5F6E78",
            TileBlue: "#E7EEF4", TileIndigo: "#E9ECF4", TileGreen: "#E6EEE9", TileOrange: "#F0ECE6", TilePurple: "#EAE9F2",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#EAF0F5", Danger: "#C24B4B", Divider: "#CBD8E0",
            ButtonBackground: "#D2E0E8", ButtonHoverBackground: "#BCD0DC", ButtonForeground: "#26343E"),

        // 雾灰蓝-深：深灰蓝背景配柔和的雾面浅蓝强调色，比 Blue-深更内敛、比 Silver-深更有
        // 一点点色相辨识度，介于两者之间的"低调雾面"深色版本。
        [(SkinFrostglass, true)] = new Palette(
            Accent: "#8FB2CE", AccentHover: "#A4C1D8", Glow: "#8FB2CE", GlowSoft: "#242E36",
            Panel: "#1B2126", Side: "#12171B", Border: "#38424A", BorderHover: "#4C5A64",
            TextPrimary: "#E6EDF2", TextSecondary: "#9DAEB8",
            TileBlue: "#212C34", TileIndigo: "#242A38", TileGreen: "#1E2A26", TileOrange: "#2C2620", TilePurple: "#26242E",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2C2620", Danger: "#EE7E7C", Divider: "#38424A",
            ButtonBackground: "#2E3A42", ButtonHoverBackground: "#3A4852", ButtonForeground: "#E0EAF0"),

        // ------- 香槟灰：新增，"磨砂高级感"系列之一。暖灰调打底，强调色是压低饱和度的
        // 雾面香槟金，跟已有 Gold(抛光暖金、明亮跳跃)刻意区分开——这里追求"低调奢华"，
        // 金色元素只作为点缀、不抢眼，整体灰度更高、更接近哑光金属而不是抛光反光。 -------
        [(SkinChampagneFrost, false)] = new Palette(
            Accent: "#A88C64", AccentHover: "#8C724E", Glow: "#CBB48E", GlowSoft: "#F5F0E6",
            Panel: "#FAF8F4", Side: "#F0EAE0", Border: "#DCD0BC", BorderHover: "#C2AE8E",
            TextPrimary: "#2A2620", TextSecondary: "#7A6E5C",
            TileBlue: "#EDEEF2", TileIndigo: "#EEEDF2", TileGreen: "#E9EFE9", TileOrange: "#F2ECE2", TilePurple: "#EFEAF0",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F5F0E6", Danger: "#C24B4B", Divider: "#DCD0BC",
            ButtonBackground: "#E4D8C4", ButtonHoverBackground: "#D6C6A8", ButtonForeground: "#3A3020"),

        // 香槟灰-深：深棕灰背景配柔和的雾面香槟金强调色，比 Gold-深更沉、更灰调，
        // 是暖色系深色版里饱和度最低、最"哑光"的一个。
        [(SkinChampagneFrost, true)] = new Palette(
            Accent: "#CBB48E", AccentHover: "#D9C6A2", Glow: "#CBB48E", GlowSoft: "#2E2A22",
            Panel: "#211E18", Side: "#17140F", Border: "#48412E", BorderHover: "#5E5540",
            TextPrimary: "#F0EAE0", TextSecondary: "#BCAE94",
            TileBlue: "#22262E", TileIndigo: "#242430", TileGreen: "#1E2822", TileOrange: "#302A1E", TilePurple: "#28242E",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#302A1E", Danger: "#EE7E7C", Divider: "#48412E",
            ButtonBackground: "#3C3628", ButtonHoverBackground: "#4A4232", ButtonForeground: "#F0E6D2"),

        // ------- 珍珠白：新增，"高级白"系列之一。跟默认白色系(纯白+科技蓝)拉开差异——
        // 背景是带一点珠光灰调的米白，强调色压低饱和度用柔和的藕粉紫灰，像打磨过的珍珠，
        // 卡片色块也统一调成低饱和的柔粉调，整体走"温柔高级"路线而不是默认皮肤的清爽科技感。 -------
        [(SkinPearl, false)] = new Palette(
            Accent: "#8C7B94", AccentHover: "#73647C", Glow: "#C9B8D0", GlowSoft: "#F5F0F4",
            Panel: "#FCFAFB", Side: "#F3EEF1", Border: "#E2D6DE", BorderHover: "#C7B4C2",
            TextPrimary: "#2A2530", TextSecondary: "#7A6E78",
            TileBlue: "#EEEDF4", TileIndigo: "#F0EDF4", TileGreen: "#ECF1EC", TileOrange: "#F5EFEA", TilePurple: "#F2EAF0",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F5F0F4", Danger: "#C24B4B", Divider: "#E2D6DE",
            ButtonBackground: "#E8DCE4", ButtonHoverBackground: "#DAC7D4", ButtonForeground: "#3A2E38"),

        // 珍珠白-深：跟珍珠白配对的深色版，深灰背景带一点紫调，强调色是柔和的浅藕紫，
        // 延续"温柔高级"基调，不做强对比度的鲜艳强调色。
        [(SkinPearl, true)] = new Palette(
            Accent: "#C9B8D0", AccentHover: "#D8C8DE", Glow: "#C9B8D0", GlowSoft: "#2A2530",
            Panel: "#211E24", Side: "#17151A", Border: "#453E4A", BorderHover: "#5A5260",
            TextPrimary: "#F0EAF0", TextSecondary: "#B8ADBC",
            TileBlue: "#26242E", TileIndigo: "#28242E", TileGreen: "#20262A", TileOrange: "#2C2622", TilePurple: "#2A2430",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2C2622", Danger: "#EE7E7C", Divider: "#453E4A",
            ButtonBackground: "#3A3440", ButtonHoverBackground: "#484050", ButtonForeground: "#EEE4EE"),

        // ------- 云杉白：新增，"高级白"系列之一。走"雪花石膏"路线——极浅暖白背景，
        // 强调色是低饱和度暖灰米色，是所有色系里色相存在感最弱的一个，追求极简、
        // 几乎看不出主题色、只靠留白和层次做设计的高级感。 -------
        [(SkinAlabaster, false)] = new Palette(
            Accent: "#9C9284", AccentHover: "#7F766A", Glow: "#D6CFC2", GlowSoft: "#F8F6F1",
            Panel: "#FDFCFA", Side: "#F5F2EC", Border: "#E6E0D4", BorderHover: "#CFC6B4",
            TextPrimary: "#28251E", TextSecondary: "#7C7566",
            TileBlue: "#EFEEEA", TileIndigo: "#F0EEEA", TileGreen: "#EDF0E9", TileOrange: "#F4EFE6", TilePurple: "#F0EDEE",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F8F6F1", Danger: "#C24B4B", Divider: "#E6E0D4",
            ButtonBackground: "#EAE4D6", ButtonHoverBackground: "#DCD2BC", ButtonForeground: "#38342A"),

        // 云杉白-深：跟云杉白配对的深色版，暖灰黑背景配柔和米色强调色，同样保持
        // 极低的色相存在感，是所有深色版里最接近"纯中性灰"的一个。
        [(SkinAlabaster, true)] = new Palette(
            Accent: "#D6CFC2", AccentHover: "#E2DCD0", Glow: "#D6CFC2", GlowSoft: "#28251E",
            Panel: "#201E1A", Side: "#161512", Border: "#48443A", BorderHover: "#5E594C",
            TextPrimary: "#F0EDE6", TextSecondary: "#B8B0A0",
            TileBlue: "#24241E", TileIndigo: "#26241E", TileGreen: "#20261C", TileOrange: "#2A2620", TilePurple: "#26241E",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2A2620", Danger: "#EE7E7C", Divider: "#48443A",
            ButtonBackground: "#3A362C", ButtonHoverBackground: "#484236", ButtonForeground: "#F0E9DA"),

        // ------- 冰晶白：新增，"高级白"系列之一。冷调玻璃质感——背景带极浅的蓝灰，
        // 强调色用清冷冰蓝，比默认白色系的科技蓝更浅更透，模拟磨砂冰晶泛出的淡蓝冷光，
        // 定位介于"清透"和"高级冷淡"之间，跟默认皮肤/雾灰蓝都区分开。 -------
        [(SkinGlacier, false)] = new Palette(
            Accent: "#6E93AC", AccentHover: "#57798F", Glow: "#B0D4E8", GlowSoft: "#EEF6FA",
            Panel: "#FBFDFE", Side: "#EEF4F8", Border: "#D2E2EA", BorderHover: "#AECBDA",
            TextPrimary: "#20282E", TextSecondary: "#66767E",
            TileBlue: "#E8F1F6", TileIndigo: "#EAEEF6", TileGreen: "#E7F1EC", TileOrange: "#F1EEE6", TilePurple: "#EDECF4",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#EEF6FA", Danger: "#C24B4B", Divider: "#D2E2EA",
            ButtonBackground: "#D6E8F0", ButtonHoverBackground: "#C0DCE8", ButtonForeground: "#28404A"),

        // 冰晶白-深：跟冰晶白配对的深色版，深蓝灰背景配清透冰蓝强调色，是所有深色版里
        // 最"清冷"的一个，跟雾灰蓝-深(更灰调、雾面)拉开差异，冰晶白-深更透亮清澈。
        [(SkinGlacier, true)] = new Palette(
            Accent: "#B0D4E8", AccentHover: "#C4DFEE", Glow: "#B0D4E8", GlowSoft: "#1E2A30",
            Panel: "#19222A", Side: "#101820", Border: "#374854", BorderHover: "#4A5E6C",
            TextPrimary: "#E8F2F8", TextSecondary: "#9AB0BC",
            TileBlue: "#1E3440", TileIndigo: "#20293E", TileGreen: "#1C2E28", TileOrange: "#2A2620", TilePurple: "#242238",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2A2620", Danger: "#EE7E7C", Divider: "#374854",
            ButtonBackground: "#28404C", ButtonHoverBackground: "#345060", ButtonForeground: "#DCEEF6"),

        // ------- 氧化铜绿：新增。对标 Minecraft 氧化铜方块的青铜绿，暖灰打底，做旧金属质感。 -------
        [(SkinCopper, false)] = new Palette(
            Accent: "#6E9A87", AccentHover: "#5A8371", Glow: "#8FC2AA", GlowSoft: "#EDF4F0",
            Panel: "#FBFAF7", Side: "#F1F0EA", Border: "#DCE0D6", BorderHover: "#B9C7BA",
            TextPrimary: "#22271F", TextSecondary: "#6E7466",
            TileBlue: "#E6EEEA", TileIndigo: "#E9ECE6", TileGreen: "#E3F0E8", TileOrange: "#F3EDE2", TilePurple: "#ECEAE4",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F3EDE2", Danger: "#C24B4B", Divider: "#DCE0D6",
            ButtonBackground: "#D7E6DC", ButtonHoverBackground: "#C2D9CA", ButtonForeground: "#2C4A3C"),

        // 氧化铜绿-深：铜锈深色版，深灰绿背景配偏亮的氧化铜青绿强调色，比 Emerald-深 更沉、
        // 更"哑光"，符合氧化铜"做旧金属"而非"宝石鲜亮"的定位。
        [(SkinCopper, true)] = new Palette(
            Accent: "#8FC2AA", AccentHover: "#A4D0BC", Glow: "#8FC2AA", GlowSoft: "#1E2A24",
            Panel: "#1A211C", Side: "#121712", Border: "#3C4640", BorderHover: "#4E5C52",
            TextPrimary: "#E8F0EA", TextSecondary: "#9CAC9E",
            TileBlue: "#1E322C", TileIndigo: "#20281E", TileGreen: "#1C2E24", TileOrange: "#2A2620", TilePurple: "#242A22",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2A2620", Danger: "#EE7E7C", Divider: "#3C4640",
            ButtonBackground: "#2C4038", ButtonHoverBackground: "#385046", ButtonForeground: "#DCEEE2"),

        // ------- 深板岩：新增。对标 Minecraft 深板岩，冷灰黑岩石基调，低饱和度冷灰蓝强调色，
        // 比曜石黑更朴素、更少"珠光"感，偏"矿洞/地底"的极简冷淡观感。 -------
        [(SkinDeepslate, false)] = new Palette(
            Accent: "#6E7686", AccentHover: "#5A6272", Glow: "#8C96A8", GlowSoft: "#EEEFF2",
            Panel: "#F9FAFB", Side: "#EEEFF1", Border: "#D8DADE", BorderHover: "#B6BAC2",
            TextPrimary: "#22242A", TextSecondary: "#6C7078",
            TileBlue: "#E8EAEE", TileIndigo: "#E9EAEE", TileGreen: "#E6ECE8", TileOrange: "#EEECE8", TilePurple: "#EAE9EE",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#EEECE8", Danger: "#C24B4B", Divider: "#D8DADE",
            ButtonBackground: "#DCDFE4", ButtonHoverBackground: "#CACFD6", ButtonForeground: "#30343C"),

        // 深板岩-深：几乎纯灰黑的背景配冷灰蓝强调色，是所有深色版里饱和度最低、最"岩石感"的一个，
        // 跟曜石黑-深（带雾面蓝紫、更"珠光"）拉开差异。
        [(SkinDeepslate, true)] = new Palette(
            Accent: "#8C96A8", AccentHover: "#A2ACBC", Glow: "#8C96A8", GlowSoft: "#1C1E22",
            Panel: "#17181B", Side: "#101113", Border: "#383A40", BorderHover: "#4A4D54",
            TextPrimary: "#E4E5E8", TextSecondary: "#94989E",
            TileBlue: "#22242A", TileIndigo: "#24242A", TileGreen: "#202622", TileOrange: "#26241F", TilePurple: "#242228",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#26241F", Danger: "#EE7E7C", Divider: "#383A40",
            ButtonBackground: "#2E3036", ButtonHoverBackground: "#3A3D44", ButtonForeground: "#DCDEE2"),

        // ------- 紫水晶：新增。透亮水晶紫粉，比 Purple 更"透光"、比 Pink 更饱和，
        // 背景带极淡的紫灰打底模拟晶洞内壁。 -------
        [(SkinAmethyst, false)] = new Palette(
            Accent: "#9C6FD6", AccentHover: "#8657C4", Glow: "#C79AF0", GlowSoft: "#F5EEFC",
            Panel: "#FCFAFE", Side: "#F4EEFA", Border: "#E2D4F0", BorderHover: "#C9AEE6",
            TextPrimary: "#241E2C", TextSecondary: "#726A80",
            TileBlue: "#EEEAF8", TileIndigo: "#EDE8FA", TileGreen: "#E9F2E9", TileOrange: "#F6EEE4", TilePurple: "#F1E4FA",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F6EEE4", Danger: "#C24B4B", Divider: "#E2D4F0",
            ButtonBackground: "#E6D6F4", ButtonHoverBackground: "#D8C0EE", ButtonForeground: "#4A2C70"),

        // 紫水晶-深：深紫背景配发光水晶紫强调色，是所有深色版里"晶体发光感"最强的一个。
        [(SkinAmethyst, true)] = new Palette(
            Accent: "#C79AF0", AccentHover: "#D6B4F6", Glow: "#C79AF0", GlowSoft: "#241E30",
            Panel: "#1E1826", Side: "#15111C", Border: "#413A50", BorderHover: "#544A66",
            TextPrimary: "#F0E8FA", TextSecondary: "#A99CBC",
            TileBlue: "#26223E", TileIndigo: "#282040", TileGreen: "#1E2E22", TileOrange: "#2C2620", TilePurple: "#302340",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2C2620", Danger: "#EE7E7C", Divider: "#413A50",
            ButtonBackground: "#3C2E52", ButtonHoverBackground: "#4C3A66", ButtonForeground: "#EEDCFA"),

        // ------- 海晶石：新增。清澈青绿蓝，介于 Blue 和 Emerald 之间但更"水感"，
        // 目前唯一的青色系。 -------
        [(SkinPrismarine, false)] = new Palette(
            Accent: "#3FA6A0", AccentHover: "#33908B", Glow: "#6BD4CC", GlowSoft: "#E4F7F5",
            Panel: "#F9FEFD", Side: "#EBF7F5", Border: "#CDE9E5", BorderHover: "#9FD6CE",
            TextPrimary: "#17262A", TextSecondary: "#5E7876",
            TileBlue: "#E1F3F0", TileIndigo: "#E7F0F6", TileGreen: "#E1F3E8", TileOrange: "#F2EEE2", TilePurple: "#E9EEF4",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F2EEE2", Danger: "#C24B4B", Divider: "#CDE9E5",
            ButtonBackground: "#CDEBE6", ButtonHoverBackground: "#B0DED6", ButtonForeground: "#144A46"),

        // 海晶石-深：深青黑背景配发光青绿强调色，模拟海底遗迹的幽光效果。
        [(SkinPrismarine, true)] = new Palette(
            Accent: "#6BD4CC", AccentHover: "#84E0D8", Glow: "#6BD4CC", GlowSoft: "#16282A",
            Panel: "#132220", Side: "#0C1918", Border: "#2E4644", BorderHover: "#3E5C58",
            TextPrimary: "#E0F5F2", TextSecondary: "#8FB0AC",
            TileBlue: "#1A3230", TileIndigo: "#1C2A38", TileGreen: "#18301F", TileOrange: "#2A2620", TilePurple: "#22283A",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2A2620", Danger: "#EE7E7C", Divider: "#2E4644",
            ButtonBackground: "#204440", ButtonHoverBackground: "#2A5450", ButtonForeground: "#D8F4EE"),

        // ------- 熔岩：新增。炽热橙红，饱和度接近警示色，比 Nether/Orange 都更强烈滚烫。 -------
        [(SkinLava, false)] = new Palette(
            Accent: "#E0562A", AccentHover: "#C7451E", Glow: "#FF8A3D", GlowSoft: "#FFEDE0",
            Panel: "#FFFBF8", Side: "#FFF1E8", Border: "#F5D2BC", BorderHover: "#EEAE84",
            TextPrimary: "#2A1810", TextSecondary: "#82624E",
            TileBlue: "#EAEEF4", TileIndigo: "#ECEAF4", TileGreen: "#E7F2E8", TileOrange: "#FFEADC", TilePurple: "#F1E6EE",
            SuccessText: "#1E9E4F", WarningText: "#C7601A", WarningBanner: "#FFEADC", Danger: "#C24B4B", Divider: "#F5D2BC",
            ButtonBackground: "#FBD8BE", ButtonHoverBackground: "#F6BE96", ButtonForeground: "#7A2E10"),

        // 熔岩-深：近黑背景（模拟熔洞暗处）配炽亮橙红强调色，是所有深色版里对比度/发光感最强的一个。
        [(SkinLava, true)] = new Palette(
            Accent: "#FF8A3D", AccentHover: "#FFA25E", Glow: "#FF8A3D", GlowSoft: "#2A1810",
            Panel: "#221410", Side: "#180D0A", Border: "#4A2C1E", BorderHover: "#603A28",
            TextPrimary: "#FCE8DC", TextSecondary: "#C09680",
            TileBlue: "#2A241E", TileIndigo: "#2A2020", TileGreen: "#22261E", TileOrange: "#3A2416", TilePurple: "#2A1E22",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#3A2416", Danger: "#FF6B4A", Divider: "#4A2C1E",
            ButtonBackground: "#4A2E1C", ButtonHoverBackground: "#5E3A22", ButtonForeground: "#FCE4D4"),

        // ------- 青金石：新增。深邃宝石蓝，比 Blue 更深更"矿物感"，是最浓郁的蓝色调。 -------
        [(SkinLapis, false)] = new Palette(
            Accent: "#2C5FBE", AccentHover: "#234DA0", Glow: "#4D7DE0", GlowSoft: "#E4EBFA",
            Panel: "#F9FAFE", Side: "#ECF0FA", Border: "#CCD8F0", BorderHover: "#9FB6E6",
            TextPrimary: "#161C2C", TextSecondary: "#5C6680",
            TileBlue: "#E2E9FA", TileIndigo: "#E5E6FA", TileGreen: "#E4F0E7", TileOrange: "#F4EDE2", TilePurple: "#EBE6F6",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F4EDE2", Danger: "#C24B4B", Divider: "#CCD8F0",
            ButtonBackground: "#CCDBF6", ButtonHoverBackground: "#AEC6F0", ButtonForeground: "#152C60"),

        // 青金石-深：靛紫蓝背景配明亮宝石蓝强调色，是所有深色版里最浓郁厚重的一个蓝调。
        [(SkinLapis, true)] = new Palette(
            Accent: "#4D7DE0", AccentHover: "#6994EA", Glow: "#4D7DE0", GlowSoft: "#181E30",
            Panel: "#141830", Side: "#0D1022", Border: "#303A5C", BorderHover: "#3E4A70",
            TextPrimary: "#E2E7FA", TextSecondary: "#8892B4",
            TileBlue: "#1E2648", TileIndigo: "#20223E", TileGreen: "#1C2A24", TileOrange: "#282420", TilePurple: "#24203E",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#282420", Danger: "#EE7E7C", Divider: "#303A5C",
            ButtonBackground: "#283460", ButtonHoverBackground: "#344278", ButtonForeground: "#DCE4FA"),

        // ------- 水（清澈通透）：新增。专为 Win11 高级特效 + 更高透明度设计，面板/背景本身
        // 就带极轻的水蓝基调，强调色是清澈水蓝，配合云母材质和更大幅度的面板/整窗透明度，
        // 视觉上追求"一整块清水玻璃"的效果，而不是稳重的海晶石或冷淡的冰晶白。 -------
        [(SkinAquatic, false)] = new Palette(
            Accent: "#2FA7E0", AccentHover: "#2390C6", Glow: "#7FD4F5", GlowSoft: "#EAF8FE",
            Panel: "#F3FCFF", Side: "#E4F6FC", Border: "#C6E9F5", BorderHover: "#93D4EE",
            TextPrimary: "#0E2A33", TextSecondary: "#4C7480",
            TileBlue: "#E0F3FC", TileIndigo: "#E4EEFA", TileGreen: "#E1F5EE", TileOrange: "#F2EEE2", TilePurple: "#EBEEF7",
            SuccessText: "#1E9E4F", WarningText: "#B5762B", WarningBanner: "#F2EEE2", Danger: "#C24B4B", Divider: "#C6E9F5",
            ButtonBackground: "#CBEBFA", ButtonHoverBackground: "#A9DEF4", ButtonForeground: "#0C3E4C"),

        // 水-深：近黑靛蓝背景配发光水蓝强调色，模拟深水中透出的光，是水主题的深色版本。
        [(SkinAquatic, true)] = new Palette(
            Accent: "#7FD4F5", AccentHover: "#98DEF8", Glow: "#7FD4F5", GlowSoft: "#12262E",
            Panel: "#0E2028", Side: "#08161C", Border: "#264450", BorderHover: "#325A68",
            TextPrimary: "#E2F5FC", TextSecondary: "#84AEBA",
            TileBlue: "#163040", TileIndigo: "#1A2438", TileGreen: "#16302A", TileOrange: "#2A2620", TilePurple: "#20263C",
            SuccessText: "#5FE092", WarningText: "#F0B368", WarningBanner: "#2A2620", Danger: "#EE7E7C", Divider: "#264450",
            ButtonBackground: "#1C3E4C", ButtonHoverBackground: "#265060", ButtonForeground: "#D6F2FC"),

        // ------- 彩蛋皮肤：全部复用白色系配色 -------
        [(SkinEggNote, false)] = WhiteLight,      [(SkinEggNote, true)] = WhiteDark,
        [(SkinEggDisco, false)] = WhiteLight,     [(SkinEggDisco, true)] = WhiteDark,
        [(SkinEggWheelchair, false)] = WhiteLight,[(SkinEggWheelchair, true)] = WhiteDark,
        [(SkinEggDance, false)] = WhiteLight,     [(SkinEggDance, true)] = WhiteDark,
        [(SkinEggCantWin, false)] = WhiteLight,   [(SkinEggCantWin, true)] = WhiteDark,
        [(SkinEggBug, false)] = WhiteLight,       [(SkinEggBug, true)] = WhiteDark,
    };

    /// <summary>
    /// 根据用户当前的色系 + 明暗选择应用配色。一切以用户当前选择为准：包括访客模式期间
    /// 也不再强制覆盖成任何固定配色（访客模式只影响临时账户/会话清理，跟界面配色完全
    /// 解耦），传进来的 hue/isDark 是什么就显示什么。
    /// hue 找不到/非法值时兜底为 SkinWhite，不让配置文件被手改坏了之后直接崩溃或者
    /// 显示成完全没配色的默认灰；兼容历史数据：hue 等于旧的 SkinDark 常量时按
    /// "SkinWhite + 深色"处理。
    /// </summary>
    public static void ApplyForCurrentState(bool guestModeEnabled, string? persistedSkin, bool isDarkMode, string? customAccentColor = null)
    {
        var hue = persistedSkin;
        if (hue == SkinDark)
        {
            // 兼容旧配置文件：以前 UiSkin=Dark 就代表纯黑深色，现在拆成两个维度后，
            // 等价写法是 "White 色系 + 深色模式"。
            hue = SkinWhite;
            isDarkMode = true;
        }

        if (string.Equals(hue, SkinCustom, StringComparison.Ordinal))
        {
            var color = Color.FromRgb(0x4C, 0x9A, 0xFF);
            if (!string.IsNullOrWhiteSpace(customAccentColor))
            {
                try { color = (Color)ColorConverter.ConvertFromString(customAccentColor)!; }
                catch { /* 配置被手改坏时使用默认自定义蓝，不影响启动。 */ }
            }

            Apply(SkinWhite, isDarkMode);
            _currentHue = SkinCustom;
            _currentCustomAccent = color;
            ApplyCustomAccentResources(color, isDarkMode);
            return;
        }

        if (hue is null || !Palettes.ContainsKey((hue, false)))
        {
            hue = SkinWhite;
        }

        Apply(hue, isDarkMode);
    }

    private static void ApplyCustomAccentResources(Color color, bool isDark)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        var hover = Mix(color, isDark ? Colors.White : Colors.Black, isDark ? 0.16 : 0.12);
        var glow = Mix(color, Colors.White, isDark ? 0.10 : 0.18);
        var glowSoft = Mix(color, isDark ? Color.FromRgb(0x20, 0x24, 0x2B) : Colors.White, isDark ? 0.72 : 0.84);
        var borderHover = Mix(color, isDark ? Colors.White : Color.FromRgb(0x4A, 0x55, 0x68), isDark ? 0.32 : 0.42);
        var buttonBackground = Mix(color, isDark ? Color.FromRgb(0x20, 0x24, 0x2B) : Colors.White, isDark ? 0.58 : 0.76);
        var buttonHover = Mix(color, isDark ? Color.FromRgb(0x20, 0x24, 0x2B) : Colors.White, isDark ? 0.42 : 0.62);
        var buttonForeground = isDark ? Colors.White : Mix(color, Colors.Black, 0.62);

        SetBrushColor(resources, "AccentBrush", ToHex(color));
        SetBrushColor(resources, "AccentHoverBrush", ToHex(hover));
        SetBrushColor(resources, "GlowBrush", ToHex(glow));
        SetBrushColor(resources, "GlowSoftBrush", ToHex(glowSoft));
        SetBrushColor(resources, "BorderHoverBrush", ToHex(borderHover));
        SetBrushColor(resources, "ButtonBackgroundBrush", ToHex(buttonBackground));
        SetBrushColor(resources, "ButtonHoverBackgroundBrush", ToHex(buttonHover));
        SetBrushColor(resources, "ButtonForegroundBrush", ToHex(buttonForeground));
        SetBrushColor(resources, "TileBadgeBlueBrush", ToHex(Mix(color, isDark ? Color.FromRgb(0x20, 0x24, 0x2B) : Colors.White, isDark ? 0.72 : 0.86)));
        SetBrushColor(resources, "TileBadgeIndigoBrush", ToHex(Mix(color, isDark ? Color.FromRgb(0x20, 0x24, 0x2B) : Colors.White, isDark ? 0.66 : 0.82)));

        // Custom 主题的 ButtonBackgroundBrush 也参与面板透明度/底图透出效果；
        // 必须在派生完自定义色后再按当前透明度重算一次。
        ReapplyPanelAlpha();
        ReapplyTextAlpha();
        RefreshOpenWindows();
    }

    private static Color Mix(Color a, Color b, double amountOfB)
    {
        amountOfB = Math.Clamp(amountOfB, 0.0, 1.0);
        byte Blend(byte x, byte y) => (byte)Math.Clamp((int)Math.Round(x + (y - x) * amountOfB), 0, 255);
        return Color.FromRgb(Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B));
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void Apply(string hue, bool isDark)
    {
        CurrentIsDarkMode = isDark;
        _currentHue = hue;

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

        // 社区资源详情页顶部标题条：之前一直硬用 AccentBrush 满铺 + 白字，深色模式下没问题
        // （背景本来就暗，鲜艳强调色做标题条反而醒目），但浅色/白色模式下就是一整条扎眼的纯蓝，
        // 跟页面其它柔和的浅色卡片格格不入（"太突兀"）。这里不新增色相，直接复用每个色系已有的
        // ButtonBackground/ButtonForeground 这一对"柔和强调色块+可读文字"配色：浅色模式下背景
        // 换成这个更淡的色块、文字换成深色可读文字；深色模式下保留原来 AccentBrush 满铺 + 白字，
        // 观感不变。
        SetBrushColor(res, "SoftHeaderBrush", isDark ? p.Accent : p.ButtonBackground);
        SetBrushColor(res, "SoftHeaderForegroundBrush", isDark ? "#FFFFFF" : p.ButtonForeground);

        // 窗口透明度：色系/明暗切换时，用新色系的原始面板颜色重新计算一遍透明度，
        // 不然切换配色会把之前设置的透明效果覆盖回不透明。见 ReapplyPanelAlpha 注释。
        ReapplyPanelAlpha();
        ReapplyTextAlpha();

        RefreshOpenWindows();
    }

    /// <summary>调整主要文字画刷的不透明度；背景透明度很低时可单独把文字保持清晰。</summary>
    public static void ApplyTextOpacity(int percent)
    {
        _textOpacityPercent = Math.Clamp(percent, 50, 100);
        ReapplyTextAlpha();
        RefreshOpenWindows();
    }

    private static void ReapplyTextAlpha()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        var alpha = (byte)Math.Round(_textOpacityPercent / 100.0 * 255.0);
        foreach (var key in new[]
                 {
                     "TextPrimaryBrush", "TextSecondaryBrush", "ButtonForegroundBrush",
                     "SoftHeaderForegroundBrush", "SuccessTextBrush", "WarningTextBrush", "DangerBrush"
                 })
        {
            if (res[key] is SolidColorBrush brush)
                SetBrushColorRgba(res, key, brush.Color, alpha);
        }
    }

    /// <summary>
    /// 窗口透明度功能的唯一入口：设置页保存时调用这个方法（而不是直接改资源字典），
    /// 同时负责记住状态（供色系切换时复用，见 _transparencyEnabled/_transparencyPercent）、
    /// 重新计算面板画刷的透明度、以及刷新已打开窗口。
    /// 默认关闭（percent 参数无意义）；开启时 percent 会被夹在 20~100 之间，让喜欢更通透效果的用户可以继续往下调。
    /// </summary>
    public static void ApplyWindowTransparency(bool enabled, int percent)
    {
        _transparencyEnabled = enabled;
        _transparencyPercent = Math.Clamp(percent, 20, 100);
        ReapplyPanelAlpha();
        RefreshOpenWindows();

        // 修复：\"启用窗口透明度\"开关/\"面板透明度\"滑块保存后，已经导入的毛玻璃背景图片
        // 底图模糊/不透明度、面板穿透上限没有跟着重新计算，看起来像\"设置了没用\"——
        // 这两项其实是分开维护的两套状态：这里的 ReapplyPanelAlpha 只管 Panel/Side/按钮
        // 画刷的 alpha，而底图的模糊半径、图片不透明度、蒙层不透明度是 MainWindow.
        // RefreshCustomBackgroundVisualEffect 单独算的（历史原因是它同时还要参考 Win11
        // 背景材质、低性能模式等 MainWindow 才知道的状态，没法直接搬进 ThemeService）。
        // 以前只有\"导入/清除背景图片\"这两个操作会触发它，其它任何改变透明度效果的入口
        // （包括这里）都不会连带刷新，导致背景图已经导入好之后，单纯调整\"启用窗口透明度\"
        // 或拖动\"面板透明度\"滑块保存，图片本身的通透感完全不会跟着变化。现在改成
        // ApplyWindowTransparency 每次调用都广播一次事件，由持有背景图层的 MainWindow
        // 订阅并重新计算，保证这一类入口（现在有的、以后新增的）都不会再漏刷新。
        CustomBackgroundRefreshRequested?.Invoke();
    }

    /// <summary>见 <see cref="ApplyWindowTransparency"/> 方法体注释：每次窗口透明度状态变化时
    /// 广播，通知持有自定义背景图层的窗口（目前只有 MainWindow）重新计算底图模糊/不透明度。
    /// 放在 ThemeService 而不是直接让 ApplyWindowTransparency 依赖 MainWindow，是为了不引入
    /// Services 层对 Views 层的反向引用。</summary>
    public static event Action? CustomBackgroundRefreshRequested;

    /// <summary>通知主题系统主窗口底层是否有用户背景图。背景图存在时把大面积面板的
    /// 最大不透明度限制在 78%，保证图片确实能从主题卡片/侧栏下面透出来；如果用户本来把
    /// “面板透明度”调得更低，则尊重用户更透明的值。清除背景图时立即恢复原来的透明度设置。
    /// </summary>
    public static void SetCustomBackgroundActive(bool active, int maxPanelOpacityPercent = 78)
    {
        maxPanelOpacityPercent = Math.Clamp(maxPanelOpacityPercent, 55, 90);
        if (_customBackgroundActive == active && _customBackgroundMaxPanelOpacityPercent == maxPanelOpacityPercent) return;
        _customBackgroundActive = active;
        _customBackgroundMaxPanelOpacityPercent = maxPanelOpacityPercent;
        ReapplyPanelAlpha();
        RefreshOpenWindows();
    }

    /// <summary>
    /// 界面亮度的唯一入口：设置页保存时 + 启动时调用。percent 取值 0~200，100 为默认
    /// （不调整）。WPF 桌面应用没有系统级"亮度"API，这里用盖在 MainWindow.BrightnessOverlay
    /// 上的一层纯色遮罩模拟：&lt;100 时盖黑色、数值越小越暗；&gt;100 时盖白色、数值越大越"亮"
    /// （其实是整体发白冲淡对比度，不是真的更亮，只是视觉上接近"调亮"的近似效果——受限于
    /// 没有对每个像素做真正的亮度/伽马运算，这是桌面应用里最简单可靠、不需要额外原生互操作
    /// 的近似方案）。
    /// Opacity 的换算刻意留了上限（黑遮罩最高 85%、白遮罩最高 70%），不管调到 0 还是 200
    /// 界面都还能看清楚在操作什么，不会出现"调完什么都看不见、自己也不知道怎么调回来"的
    /// 死锁状态——用户还能通过设置页把它调回 100，前提是设置页本身得看得见。
    ///
    /// 具体把遮罩颜色/Opacity 套到 BrightnessOverlay 元素上的事交给 MainWindow 订阅
    /// BrightnessChanged 事件自己完成（同 CustomBackgroundRefreshRequested 的理由：
    /// 不让 Services 层反向引用 Views 层），这里只广播算好的目标颜色和 Opacity。
    /// </summary>
    public static event Action<Brush, double>? BrightnessChanged;

    public static void ApplyBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 200);

        if (percent == 100)
        {
            BrightnessChanged?.Invoke(Brushes.Transparent, 0);
        }
        else if (percent < 100)
        {
            // 0 → 0.85，100 → 0，线性插值。
            BrightnessChanged?.Invoke(Brushes.Black, (100 - percent) / 100.0 * 0.85);
        }
        else
        {
            // 100 → 0，200 → 0.70，线性插值。
            BrightnessChanged?.Invoke(Brushes.White, (percent - 100) / 100.0 * 0.70);
        }
    }

    /// <summary>
    /// 全局（整窗级）透明度的唯一入口：设置页保存时调用。跟 ApplyWindowTransparency（只调
    /// 面板画刷 alpha）不同，这里让整个窗口——包括标题栏、边框、所有内容——一起呈现
    /// 半透明效果，能看到窗口后面的桌面/其它窗口透出来。
    /// percent 会被夹在 50~100 之间（下限比面板透明更保守，见 AppConfig.GlobalWindowOpacityPercent
    /// 注释）。关闭时统一恢复成完全不透明。
    /// </summary>
    public static void ApplyGlobalWindowTransparency(bool enabled, int percent)
    {
        CurrentGlobalTransparencyEnabled = enabled;
        CurrentGlobalOpacityPercent = Math.Clamp(percent, 50, 100);

        foreach (Window window in Application.Current.Windows)
        {
            ApplyGlobalOpacityToWindow(window);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;


    private sealed class GlobalOpacityHookState
    {
        public System.Windows.Interop.HwndSource? Source;
        public System.Windows.Interop.HwndSourceHook? Hook;
        // 修复"窗口一直抽搐抖动"：以前这个钩子每次收到 WM_STYLECHANGED / WM_DWMCOMPOSITIONCHANGED
        // 都会无条件排队一次 BeginInvoke -> ApplyNativeGlobalOpacity -> (首次分层时)
        // ForceLayeredResurface -> SetWindowPos。而 SetWindowPos(SWP_FRAMECHANGED) 本身、以及
        // Win11EffectsService 那边的 ForceDwmResurface，在开启了 Win11 视觉效果(Mica/Acrylic)
        // 又同时开启了整窗全局透明时，都会让系统再发一轮 WM_STYLECHANGED/WM_DWMCOMPOSITIONCHANGED
        // 回来——没有任何节流/合并的话，两套"人为触发 WM_SIZE"的手法会互相反复打断对方触发的
        // 消息，形成消息->回调->再发消息->再回调的连续循环，表现成窗口持续小幅度抽搐抖动。
        // 用 PendingApply 合并同一时刻的多次消息（已经排了一次就不用再排)，用 LastAppliedTicks
        // 做一个很短的冷却时间，避免同一个窗口在极短时间内被反复"原地缩放1像素"。
        public bool PendingApply;
        public long LastAppliedTicks;
    }

    // Win11EffectsService.ForceDwmResurface 和这里的 ForceLayeredResurface 都会对同一个 hwnd
    // 做"原地改尺寸再改回去"的操作；如果两边几乎同时触发，SetWindowPos 调用交错执行，视觉上
    // 就是窗口反复抖动。用这个全局忙碌集合让同一个 hwnd 在同一时刻只有一边在做重合成操作。
    internal static readonly HashSet<IntPtr> ResurfaceBusyHandles = new();
    private static readonly object ResurfaceBusyLock = new();
    internal static bool TryEnterResurface(IntPtr hwnd)
    {
        lock (ResurfaceBusyLock)
        {
            if (!ResurfaceBusyHandles.Add(hwnd)) return false;
            return true;
        }
    }
    internal static void ExitResurface(IntPtr hwnd)
    {
        lock (ResurfaceBusyLock) { ResurfaceBusyHandles.Remove(hwnd); }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, GlobalOpacityHookState> GlobalOpacityHooks = new();
    private const int WM_STYLECHANGED = 0x007D;
    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private static void EnsureGlobalOpacityHook(Window window, IntPtr hwnd)
    {
        var state = GlobalOpacityHooks.GetValue(window, _ => new GlobalOpacityHookState());
        if (state.Source != null) return;
        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        if (source == null) return;

        System.Windows.Interop.HwndSourceHook hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_STYLECHANGED || msg == WM_SHOWWINDOW || msg == WM_DPICHANGED || msg == WM_DWMCOMPOSITIONCHANGED)
            {
                // 已经有一次回调排队等着执行，就不再重复排队——避免消息风暴时堆积一堆
                // 几乎同时执行的 ApplyNativeGlobalOpacity 调用互相打断。
                if (state.PendingApply) return IntPtr.Zero;
                // 刚刚（200ms 内）已经应用过一次，这次大概率就是上一次操作自己引发的
                // 连锁反应消息，直接忽略，从根上掐断"消息->操作->再发消息"的循环。
                var nowTicks = Environment.TickCount64;
                if (nowTicks - state.LastAppliedTicks < 200) return IntPtr.Zero;

                state.PendingApply = true;
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        state.PendingApply = false;
                        state.LastAppliedTicks = Environment.TickCount64;
                        var current = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                        if (current != IntPtr.Zero) ApplyNativeGlobalOpacity(current);
                    }));
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        state.Source = source;
        state.Hook = hook;
    }

    private static void ApplyNativeGlobalOpacity(IntPtr hwnd)
    {
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (CurrentGlobalTransparencyEnabled)
        {
            var wasLayered = (exStyle & WS_EX_LAYERED) != 0;
            if (!wasLayered)
            {
                var setResult = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                var lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                // 诊断日志：如果"完全没变化"这个问题在你机器上复现，把 xcl2/logs 目录下
                // 最新那份日志里带 [GlobalOpacity] 前缀的行发给我——setResult==0 且
                // lastError!=0 通常代表 SetWindowLong 本身被系统拒绝（比如权限/句柄已失效），
                // 而不是后面 alpha 混合没生效，两种情况的修法完全不同，不看这行日志的话
                // 我只能继续盲猜。
                try { LauncherLogService.AppendLine($"[GlobalOpacity] SetWindowLong(add layered) result={setResult} lastError={lastError} hwnd={hwnd}"); } catch { }
                exStyle |= WS_EX_LAYERED;
            }
            var alpha = (byte)Math.Round(CurrentGlobalOpacityPercent / 100.0 * 255.0);
            var alphaOk = SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            if (!alphaOk)
            {
                var lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                try { LauncherLogService.AppendLine($"[GlobalOpacity] SetLayeredWindowAttributes FAILED alpha={alpha} lastError={lastError} hwnd={hwnd}"); } catch { }
            }

            // 修复"设置页保存后整窗透明看起来完全没生效，得手动拖一下窗口大小才会更新"：
            // 窗口第一次从"非分层"切到 WS_EX_LAYERED 时，DWM 需要先把这块窗口原本按不透明
            // 方式合成的重定向表面，重新按分层窗口的规则接管一遍；这一步在部分系统/显卡驱动
            // 上不会立刻触发重绘，必须等窗口产生一次真正的尺寸变化（WM_SIZE）才会重新合成，
            // 跟 Win11EffectsService.ForceDwmResurface 注释里说的"Mica/Acrylic 立刻白屏"是完全
            // 同一类问题、同一套成因，这里用同样的手法解决：原地把窗口尺寸改一次再改回去，
            // 人为触发一次 WM_SIZE，逼 DWM 把新的分层表面跟当前画面重新合成。只在"由不透明
            // 切换成分层"的这一刻做一次即可，之后单纯调节 alpha（拖滑块）不需要这一步。
            if (!wasLayered)
            {
                ForceLayeredResurface(hwnd);
                // 部分机器上 DWM 在重合成表面的过程中会连带把刚设置的 layered alpha 冲掉
                // （表现正是"完全没变化"而不是"要拖一下才生效"）——resurface 结束后
                // 立刻重发一次 SetLayeredWindowAttributes 兜底，成本极低，不生效也无副作用。
                SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            }
        }
        else if ((exStyle & WS_EX_LAYERED) != 0)
        {
            // 先恢复 alpha，再摘 layered，避免部分显卡驱动在直接摘样式时留下上一帧缓存。
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
        }
    }

    /// <summary>见 ApplyNativeGlobalOpacity 里的调用点注释：人为制造一次 1 像素的尺寸抖动，
    /// 触发真正的 WM_SIZE，逼 DWM 重新合成刚切换成分层窗口的重定向表面。用户完全看不出这
    /// 1 像素的变化，但足够让画面从"卡在切换前那一帧"变成实时更新。</summary>
    private static void ForceLayeredResurface(IntPtr hwnd)
    {
        // 见 ResurfaceBusyHandles 注释：跟 Win11EffectsService.ForceDwmResurface 共用这道闸门，
        // 防止同一个窗口在同一时刻被两套"原地缩放1像素"逻辑同时操作，那才是真正表现为
        // "窗口一直抽搐抖动"的原因——不是缩放本身有问题，是两边交错执行导致的。
        if (!TryEnterResurface(hwnd)) return;
        try
        {
            if (!GetWindowRect(hwnd, out var rect)) return;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            const uint flags = SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height + 1, flags);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, flags);
        }
        finally { ExitResurface(hwnd); }
    }

    /// <summary>
    /// 把当前全局透明度状态应用到单个窗口——新开窗口在 Loaded 时（见 App.xaml.cs 里
    /// 对应的类处理器，跟 Win11EffectsService.Apply 同一套接线方式）、以及
    /// ApplyGlobalWindowTransparency 遍历已打开窗口时都会调用这个方法。
    ///
    /// 之前直接设 Window.Opacity：WPF 的 Window.Opacity 只有在 AllowsTransparency="True"
    /// （逐像素透明的分层窗口）时才真正生效，而项目里所有窗口都是 AllowsTransparency="False"
    /// （保留硬件加速渲染 + 兼容下面 Win11EffectsService 的 Mica/Acrylic 背景材质，两者跟
    /// AllowsTransparency="True" 都不兼容）。AllowsTransparency="False" 时 Window.Opacity
    /// 被 WPF 直接忽略，实际看到的"变透明"其实是别的画刷 alpha 调整在软件渲染层跟未定义
    /// 背景（近似黑色）做混合的副作用——表现出来就是"只是变暗，没有真的透出桌面"。
    ///
    /// 现在改成不依赖 AllowsTransparency 的原生做法：给窗口句柄追加 WS_EX_LAYERED 扩展样式，
    /// 再用 SetLayeredWindowAttributes + LWA_ALPHA 让 DWM 把整个窗口（连同它硬件加速渲染出的
    /// 全部内容）当成一张位图统一做 alpha 混合——这是 Windows 自带的"整窗透明"机制，
    /// 不需要放弃硬件加速，也不影响同时开启的 Mica/Acrylic 背景材质。
    /// </summary>
    public static void ApplyGlobalOpacityToWindow(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnsureGlobalOpacityHook(window, hwnd);
        ApplyNativeGlobalOpacity(hwnd);
    }

    /// <summary>
    /// 按当前色系(_currentHue)、当前明暗(CurrentIsDarkMode)取出面板类画刷的原始十六进制颜色，
    /// 关闭透明度时按完全不透明(alpha=FF)重设，开启时按 _transparencyPercent 算出的 alpha
    /// 重设——不管开关状态如何，色相/明暗本身都不受影响，只调整这几个画刷的 alpha 通道。
    /// 这个方法本身不触发 RefreshOpenWindows，调用方（Apply / ApplyWindowTransparency）
    /// 各自负责在需要的时候刷新，避免重复刷新两次。
    /// </summary>
    private static void ReapplyPanelAlpha()
    {
        // Custom 主题以 White 的中性面板为底，但按钮背景继续由用户颜色派生。
        // 其它主题则直接使用各自 Palette。
        var paletteHue = _currentHue == SkinCustom ? SkinWhite : _currentHue;
        if (!Palettes.TryGetValue((paletteHue, CurrentIsDarkMode), out var p))
        {
            p = Palettes[(SkinWhite, CurrentIsDarkMode)];
        }

        var res = Application.Current.Resources;
        var effectivePercent = _transparencyEnabled ? _transparencyPercent : 100;
        if (_customBackgroundActive)
            effectivePercent = Math.Min(effectivePercent, _customBackgroundMaxPanelOpacityPercent);
        var alpha = (byte)Math.Round(effectivePercent / 100.0 * 255.0);

        var buttonBackground = p.ButtonBackground;
        if (_currentHue == SkinCustom)
        {
            var customButton = Mix(_currentCustomAccent,
                CurrentIsDarkMode ? Color.FromRgb(0x20, 0x24, 0x2B) : Colors.White,
                CurrentIsDarkMode ? 0.58 : 0.76);
            buttonBackground = ToHex(customButton);
        }

        SetBrushColorWithAlpha(res, "PanelBrush", p.Panel, alpha);
        SetBrushColorWithAlpha(res, "SideBrush", p.Side, alpha);
        SetBrushColorWithAlpha(res, "ButtonBackgroundBrush", buttonBackground, alpha);
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
    /// <summary>
    /// 弹窗/抽屉独立外观的最近一次设置值，跟 <see cref="_transparencyEnabled"/>/
    /// <see cref="_transparencyPercent"/> 是同一种"静态字段缓存调用方传入的配置"模式——
    /// ThemeService 是静态类，不持有 ConfigService 实例，没法自己去读配置文件，只能靠
    /// 调用方（App 启动时 / SettingsPage 保存时）通过 <see cref="SetPopupAppearanceConfig"/>/
    /// <see cref="SetDrawerAppearanceConfig"/> 把当前配置值推进来缓存住，后续每次
    /// RefreshOpenWindows（色系/明暗切换）时才能拿着最新值重新计算，不需要每个 Apply 调用点
    /// 都额外传一遍这四个参数。
    /// </summary>
    private static bool _popupUseCustom;
    private static int _popupOpacityPercent = 92, _popupFrostPercent = 40, _popupTextOpacityPercent = 100;
    private static bool _drawerUseCustom;
    private static int _drawerOpacityPercent = 92, _drawerFrostPercent = 40, _drawerTextOpacityPercent = 100;

    /// <summary>缓存弹窗独立外观配置并立即应用一次。App 启动读完配置后、以及设置页保存/
    /// 拖动滑块实时预览时调用。</summary>
    public static void SetPopupAppearanceConfig(bool useCustom, int opacityPercent, int frostPercent, int textOpacityPercent)
    {
        _popupUseCustom = useCustom;
        _popupOpacityPercent = opacityPercent;
        _popupFrostPercent = frostPercent;
        _popupTextOpacityPercent = textOpacityPercent;
        ApplyPopupAppearance(useCustom, opacityPercent, frostPercent, textOpacityPercent);
    }

    /// <summary>跟 <see cref="SetPopupAppearanceConfig"/> 对应，作用对象是抽屉。</summary>
    public static void SetDrawerAppearanceConfig(bool useCustom, int opacityPercent, int frostPercent, int textOpacityPercent)
    {
        _drawerUseCustom = useCustom;
        _drawerOpacityPercent = opacityPercent;
        _drawerFrostPercent = frostPercent;
        _drawerTextOpacityPercent = textOpacityPercent;
        ApplyDrawerAppearance(useCustom, opacityPercent, frostPercent, textOpacityPercent);
    }

    private static void RefreshOpenWindows()
    {
        // 每次主题相关的 Apply 收尾都顺带用最近一次缓存的配置重算一次弹窗/抽屉的独立外观
        // （见上面 SetPopupAppearanceConfig/SetDrawerAppearanceConfig 的类注释），保证切换
        // 色系/明暗时弹窗/抽屉的基色跟着联动，不需要每条 Apply 路径各自记得调用一次。
        ApplyPopupAppearance(_popupUseCustom, _popupOpacityPercent, _popupFrostPercent, _popupTextOpacityPercent);
        ApplyDrawerAppearance(_drawerUseCustom, _drawerOpacityPercent, _drawerFrostPercent, _drawerTextOpacityPercent);

        foreach (Window window in Application.Current.Windows)
        {
            RefreshVisualTree(window);
            // 标题栏（原生系统绘制部分，见 WindowChromeService 类注释里"顶部白条"的成因）
            // 不在 WPF 资源系统管辖范围内，普通的画刷刷新逻辑碰不到它，这里单独调一次
            // DWM API 让已打开窗口的标题栏立即跟着当前深浅色切换，不需要关闭重开窗口。
            WindowChromeService.ApplyTitleBarTheme(window, CurrentIsDarkMode);

            // 窗口图标（标题栏左上角 / 任务栏 / Alt-Tab）同样不归 WPF 资源系统管，
            // 跟标题栏一样需要在这里主动重设，才能在切换深浅色时立即跟着换。
            // 深色图标文件缺失会自动回退浅色，不会抛异常。见 AppIconService 类头注释。
            AppIconService.ApplyTo(window);

            // MainWindow 自绘标题栏左上角那个 Image（跟 Window.Icon 是两个不同的控件，
            // 后者只影响任务栏/Alt-Tab，不会自动带动前者跟着换）单独刷一次，否则深浅色
            // 切换时任务栏图标换了、自绘标题栏里的图标却停在旧的那张不动。
            if (window is Views.MainWindow main)
                AppIconService.ApplyToTitleBarImage(main, main.TitleBarIconImage);
        }
    }

    /// <summary>
    /// RefreshOpenWindows 的公开入口，专供 LocalizationService（语言切换）复用同一套
    /// "强制所有已打开窗口重新从资源字典取值"的刷新逻辑——语言字符串资源跟配色画刷
    /// 资源遇到的是同一个 WPF Style-Seal 问题（见上面 RefreshVisualTree 的注释），
    /// 没必要在两个服务里各写一份几乎相同的遍历代码。
    /// </summary>
    public static void RefreshOpenWindowsPublic() => RefreshOpenWindows();

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
    /// 按 <see cref="Models.AppConfig.PopupUseCustomAppearance"/> 刷新弹窗（MainWindow.xaml
    /// 里的 OverlayCardBackgroundLayer/OverlayContentHost，见该文件注释）的背景透明度/磨砂/
    /// 文字透明度。关闭"独立设置"时，PopupPanelBrush/PopupTextPrimaryBrush 直接跟当前主题的
    /// PanelBrush/TextPrimaryBrush 保持一致颜色但完全不透明、PopupBlurEffect.Radius=0——
    /// 也就是"看起来跟旧版本行为完全一样"。开启后按用户单独设置的百分比重新计算。
    /// 每次色系/明暗切换（Apply）末尾都会调用一次，保证弹窗颜色跟主题联动；设置页拖动
    /// 滑块时也会单独调用做实时预览。
    /// </summary>
    public static void ApplyPopupAppearance(bool useCustom, int opacityPercent, int frostPercent, int textOpacityPercent)
    {
        var res = Application.Current.Resources;
        var panelColor = (res["PanelBrush"] as SolidColorBrush)?.Color ?? Colors.White;
        var textColor = (res["TextPrimaryBrush"] as SolidColorBrush)?.Color ?? Colors.Black;

        byte bgAlpha = useCustom ? PercentToAlpha(opacityPercent, 20, 100) : (byte)255;
        byte textAlpha = useCustom ? PercentToAlpha(textOpacityPercent, 40, 100) : (byte)255;
        double blurRadius = useCustom ? Math.Clamp(frostPercent, 0, 100) / 100.0 * 28.0 : 0.0;

        SetBrushColorRgba(res, "PopupPanelBrush", panelColor, bgAlpha);
        SetBrushColorRgba(res, "PopupTextPrimaryBrush", textColor, textAlpha);
        SetBlurRadius(res, "PopupBlurEffect", blurRadius);
    }

    /// <summary>跟 <see cref="ApplyPopupAppearance"/> 是同一套逻辑，作用对象是"抽屉"
    /// （AiAssistantPanel 侧栏面板，见该文件里 DrawerBackgroundLayer 的注释），基色取
    /// SideBrush（抽屉默认背景本来就是 SideBrush，不是 PanelBrush）而不是弹窗的 PanelBrush。</summary>
    public static void ApplyDrawerAppearance(bool useCustom, int opacityPercent, int frostPercent, int textOpacityPercent)
    {
        var res = Application.Current.Resources;
        var sideColor = (res["SideBrush"] as SolidColorBrush)?.Color ?? Colors.WhiteSmoke;
        var textColor = (res["TextPrimaryBrush"] as SolidColorBrush)?.Color ?? Colors.Black;

        byte bgAlpha = useCustom ? PercentToAlpha(opacityPercent, 20, 100) : (byte)255;
        byte textAlpha = useCustom ? PercentToAlpha(textOpacityPercent, 40, 100) : (byte)255;
        double blurRadius = useCustom ? Math.Clamp(frostPercent, 0, 100) / 100.0 * 28.0 : 0.0;

        SetBrushColorRgba(res, "DrawerBackgroundBrush", sideColor, bgAlpha);
        SetBrushColorRgba(res, "DrawerTextPrimaryBrush", textColor, textAlpha);
        SetBlurRadius(res, "DrawerBlurEffect", blurRadius);
    }

    /// <summary>百分比(min~100)线性映射到 0~255 的 alpha 通道，供弹窗/抽屉透明度使用。</summary>
    private static byte PercentToAlpha(int percent, int min, int max)
    {
        var clamped = Math.Clamp(percent, min, max);
        return (byte)Math.Round(clamped / 100.0 * 255.0);
    }

    private static void SetBrushColorRgba(ResourceDictionary res, string key, Color baseColor, byte alpha)
    {
        var color = baseColor;
        color.A = alpha;
        if (res[key] is not SolidColorBrush brush) return;
        if (!brush.IsFrozen) { brush.Color = color; return; }
        res[key] = new SolidColorBrush(color);
    }

    /// <summary>跟 SetBrushColor 同样的"已冻结就换新实例"套路，作用对象是 BlurEffect.Radius。</summary>
    private static void SetBlurRadius(ResourceDictionary res, string key, double radius)
    {
        if (res[key] is not System.Windows.Media.Effects.BlurEffect blur) return;
        if (!blur.IsFrozen) { blur.Radius = radius; return; }
        res[key] = new System.Windows.Media.Effects.BlurEffect { Radius = radius };
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

    /// <summary>
    /// 跟 SetBrushColor 是同一套"已冻结画刷换新实例"逻辑，唯一区别是这里额外接受一个
    /// alpha 通道参数——专供窗口透明度功能使用，其余调用方（色系/明暗切换的完整画刷列表）
    /// 继续走上面不带 alpha 的 SetBrushColor，保持完全不透明。
    /// </summary>
    private static void SetBrushColorWithAlpha(ResourceDictionary res, string key, string hex, byte alpha)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        color.A = alpha;

        if (res[key] is not SolidColorBrush brush) return;

        if (!brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        res[key] = new SolidColorBrush(color);
    }

    /// <summary>
    /// 读取 Windows 系统当前的"应用深浅色"主题设置（设置-个性化-颜色-选择您的模式），
    /// 供"跟随系统"功能使用。对应注册表键
    /// HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
    /// 下的 AppsUseLightTheme（DWORD，0=深色，1=浅色，Win10 1809+ 才有）。
    /// 读取失败（键不存在/极老系统/权限问题等）时兜底返回 false（浅色），不抛异常，
    /// 避免"跟随系统"功能因为读取不到系统状态就把整个启动器搞崩溃。
    /// </summary>
    public static bool GetSystemIsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0;
        }
        catch
        {
            // 忽略：兜底返回浅色。
        }
        return false;
    }

    /// <summary>用于设置页"色系"下拉框的中文显示名，UI 展示用，不参与持久化。</summary>
    public static string GetDisplayName(string skin) => skin switch
    {
        SkinWhite => "白色（默认）",
        SkinBlue => "蓝色",
        SkinYellow => "黄色",
        SkinPurple => "紫色",
        SkinPink => "粉色",
        SkinCustom => "自定义…",
        SkinSilver => "银色",
        SkinGold => "金色",
        SkinEmerald => "绿宝石绿",
        SkinNether => "下界红",
        SkinEndStone => "末地石",
        SkinWarmYellow => "暖黄色",
        SkinOrange => "亮橙色",
        SkinObsidian => "曜石黑（磨砂）",
        SkinFrostglass => "雾灰蓝（磨砂）",
        SkinChampagneFrost => "香槟灰（磨砂）",
        SkinPearl => "珍珠白（高级）",
        SkinAlabaster => "云杉白（高级）",
        SkinGlacier => "冰晶白（高级）",
        SkinCopper => "氧化铜绿",
        SkinDeepslate => "深板岩",
        SkinAmethyst => "紫水晶",
        SkinPrismarine => "海晶石",
        SkinLava => "熔岩",
        SkinLapis => "青金石",
        SkinAquatic => "水（清澈通透）",
        SkinDark => "黑色",
        SkinEggNote => "注意：此按钮以及以下的按钮均为彩蛋，均为白色系",
        SkinEggDisco => "(彩蛋)雷霆动物集体蹦迪",
        SkinEggWheelchair => "(彩蛋)轮椅老头鬼火漂移",
        SkinEggDance => "(彩蛋)隔壁的正太靠扭腰吸引了很多**",
        SkinEggCantWin => "(彩蛋)我们都扭不过他",
        SkinEggBug => "(彩蛋)（正在被bug改疯）",
        _ => skin
    };
}
