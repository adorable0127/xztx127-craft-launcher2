using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「自定义成就图片生成器」：仿 Minecraft 游戏内"达成进度"提示条的样式，
/// 生成一张静态 PNG，供玩家做截图分享/视频素材。
///
/// ===== 这一版修了什么（"那个物品没有正常的格式"）=====
///
/// 旧实现有三个问题，合起来就是"物品那一块看着不对劲"：
///
/// 1) **物品图标只画了一个字母。**
///    旧代码取 itemId 冒号后那段的首字母画进方块里，所以填 "minecraft:diamond"
///    出来是一个大写的 "D"，完全看不出是钻石——跟原版提示里左边是**物品贴图**的
///    观感差得很远，这是最直观的"格式不正常"。
///
/// 2) **空段会画出 NUL 字符。**
///    旧代码是 `...Last().TrimStart().ToUpperInvariant().FirstOrDefault().ToString()`。
///    FirstOrDefault() 作用在字符串上返回 char，空字符串时返回 '\0'，.ToString() 得到 "\0"。
///    用户只要填成 "minecraft:"（或末尾多打一个冒号），画出来就是个渲染不出的豆腐块，
///    而不是回退成 "?"。
///
/// 3) **物品 ID 完全不做校验/归一化。**
///    Minecraft 资源 ID 有明确规则：namespace:path，命名空间只允许 [a-z0-9_.-]，
///    路径只允许 [a-z0-9_./-]，不写命名空间时默认 minecraft。旧实现原样接收任何输入，
///    用户填 "Diamond Sword" 或"钻石"都照单全收。
///
/// 现在的做法：
/// - NormalizeItemId 把输入规整成合法的 namespace:path（转小写、空格转下划线、
///   补默认命名空间、剔除非法字符），并告诉调用方是否改过，界面可以提示"已自动更正为 xxx"；
/// - 图标**优先用游戏里的原版物品贴图**（从已安装游戏的 version jar 或 assets/objects
///   里读真正的贴图 PNG 画出来）；本机没装对应版本、或读到不到时才退回按物品类别画的
///   矢量图形（剑/镐/斧/锹/锭/宝石/苹果/药水/书/方块），颜色从名字里的材质关键词推断，
///   认不出类别时退回等距像素方块而不是字母。
///
/// 矢量兜底图形都是自己用几何图元画的，不涉及原版素材再分发的问题；原版贴图仅在本机
/// 已安装该游戏版本时从本地游戏文件读取，也不会做任何复制/分发。
/// </summary>
public static class AchievementImageService
{
    // 复用同一个 HttpClient，而不是每尝试一个候选 URL 就 new 一次：new HttpClient() 每次都要
    // 重新做一遍 TCP+TLS 握手，找一张贴图经常要按"版本列表 × item/block 两个子路径"依次试好几个
    // URL 才能命中，逐次握手叠加起来的延迟正是"生成成就图片会卡一下"的主要来源——虽然整个生成
    // 过程已经在后台线程跑（不冻结 UI），但用户仍然要多等这么久。复用连接后，同一台机器上
    // 多次调用之间（尤其是命中同一个 CDN 域名时）能复用底层 TCP 连接，显著缩短等待时间。
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(4) };

    static AchievementImageService()
    {
        SharedHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
    }

    private static readonly Regex NamespaceInvalid = new("[^a-z0-9_.-]", RegexOptions.Compiled);
    private static readonly Regex PathInvalid = new("[^a-z0-9_./-]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>是否含有中文（CJK 统一表意文字）字符——用来判断要不要走中文名称搜索。</summary>
    private static readonly Regex HasChinese = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    /// <summary>归一化结果。WasChanged 供界面提示"已自动更正为 minecraft:diamond_sword"。
    /// MatchedChineseName 不为空时表示这次是靠中文名称搜索出来的结果（界面可以提示"已按『钻石』匹配到 minecraft:diamond"）。</summary>
    public sealed record NormalizedItemId(string FullId, string Namespace, string Path, bool WasChanged, string? MatchedChineseName = null);

    /// <summary>
    /// 把用户随手填的东西规整成合法的 Minecraft 物品 ID。
    /// 例："Diamond Sword" → "minecraft:diamond_sword"；"diamond" → "minecraft:diamond"；
    ///     "钻石剑" → "minecraft:diamond_sword"（中文名称搜索，见 <see cref="ChineseNameLookup"/>）；
    ///     "minecraft:" → "minecraft:air"（路径空了退回 air，不留空段——这正是旧版画出 NUL 的根因）。
    ///
    /// 中文输入且在词典里查不到时，不再一律塌陷成 "air"（那样不管填什么中文，图标都长得一模一样，
    /// 用户会以为"搜索没生效、还是那张预设图"）：改用原始中文文本做稳定哈希，落到一个专属的
    /// "unknown_&lt;hash&gt;" 路径上，保证不同的中文输入至少会画出不同颜色/外观的兜底图标。
    /// </summary>
    public static NormalizedItemId NormalizeItemId(string? raw)
    {
        var original = (raw ?? "").Trim();

        // 中文名称搜索：先按整串、再按去掉常见后缀（方块/矿石/原木等其实已经是词典的一部分，
        // 这里只做"整串直接命中"和"去首尾空白后再命中"两次尝试，词典本身覆盖了绝大多数常见写法）。
        if (HasChinese.IsMatch(original))
        {
            var zhKey = Whitespace.Replace(original, "");
            if (ChineseNameLookup.TryGetValue(zhKey, out var mapped))
            {
                var mns = mapped.Contains(':') ? mapped[..mapped.IndexOf(':')] : "minecraft";
                var mpath = mapped.Contains(':') ? mapped[(mapped.IndexOf(':') + 1)..] : mapped;
                return new NormalizedItemId(mapped, mns, mpath, true, zhKey);
            }

            // 词典没收录：不塌陷成同一个 "air"，而是用原文的稳定哈希生成专属兜底路径，
            // 这样不同的中文搜索词至少会画出不同的图标（颜色/形状），不会看起来像"搜索没生效"。
            var h = 0;
            foreach (var c in zhKey) h = unchecked(h * 31 + c);
            var fallbackPath = "unknown_" + Math.Abs(h);
            return new NormalizedItemId("minecraft:" + fallbackPath, "minecraft", fallbackPath, true, null);
        }

        var s = Whitespace.Replace(original.ToLowerInvariant(), "_");

        string ns, path;
        var colon = s.IndexOf(':');
        if (colon < 0)
        {
            ns = "minecraft";
            path = s;
        }
        else
        {
            ns = s[..colon];
            path = s[(colon + 1)..].Replace(":", ""); // 多余的冒号是非法的，直接去掉
        }

        ns = NamespaceInvalid.Replace(ns, "");
        path = PathInvalid.Replace(path, "");

        if (string.IsNullOrEmpty(ns)) ns = "minecraft";
        if (string.IsNullOrEmpty(path)) path = "air";

        var full = ns + ":" + path;
        return new NormalizedItemId(full, ns, path, !string.Equals(full, original, StringComparison.Ordinal));
    }

    /// <summary>供输入框下拉搜索用：按关键字（中文名或英文 id 片段）在词典里做包含匹配，
    /// 返回 "中文名 → id" 的候选列表，最多 <paramref name="max"/> 条。用户直接点选，
    /// 就不会再填出词典/贴图里都不存在的方块名（那种情况只能退回兜底图标，
    /// 观感上跟"预设没变"一模一样，从入口上帮用户避开这个坑）。</summary>
    public static IReadOnlyList<(string Chinese, string Id)> SearchSuggestions(string? query, int max = 10)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return Array.Empty<(string, string)>();

        var qLower = q.ToLowerInvariant();
        var results = new List<(string Chinese, string Id)>();

        // 中文名包含匹配 + 英文 id 包含匹配，两边都试，按"以关键字开头"优先排序
        foreach (var kv in ChineseNameLookup)
        {
            var zhHit = kv.Key.Contains(q, StringComparison.Ordinal);
            var idHit = kv.Value.Contains(qLower, StringComparison.OrdinalIgnoreCase);
            if (zhHit || idHit)
                results.Add((kv.Key, kv.Value));
        }

        return results
            .OrderByDescending(r => r.Chinese.StartsWith(q, StringComparison.Ordinal)
                                     || r.Id.Contains(":" + qLower, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Chinese.Length)
            .Take(max)
            .ToList();
    }

    /// <summary>中文物品/方块名称 → minecraft ID 词典。覆盖常见方块、矿物/锭/原石、食物、
    /// 红石元件、功能方块，以及六种材质（木/石/铁/金/钻石/下界合金）的全套工具与武器。
    /// 词典可以随时继续加词条，不影响其它逻辑。</summary>
    private static readonly Dictionary<string, string> ChineseNameLookup = BuildChineseNameLookup();

    private static Dictionary<string, string> BuildChineseNameLookup()
    {
        var d = new Dictionary<string, string>();

        void Add(string zh, string id) => d[zh] = id.Contains(':') ? id : "minecraft:" + id;

        // ===== 常见方块 =====
        Add("石头", "stone"); Add("圆石", "cobblestone"); Add("泥土", "dirt"); Add("草方块", "grass_block");
        Add("沙子", "sand"); Add("红沙", "red_sand"); Add("砂砾", "gravel"); Add("黏土", "clay");
        Add("橡木原木", "oak_log"); Add("橡木木板", "oak_planks"); Add("云杉原木", "spruce_log");
        Add("白桦原木", "birch_log"); Add("丛林原木", "jungle_log"); Add("金合欢原木", "acacia_log");
        Add("深色橡木原木", "dark_oak_log"); Add("樱花原木", "cherry_log");
        Add("玻璃", "glass"); Add("黑曜石", "obsidian"); Add("下界岩", "netherrack");
        Add("末地石", "end_stone"); Add("灵魂沙", "soul_sand"); Add("灵魂土", "soul_soil");
        Add("萤石", "glowstone"); Add("雪块", "snow_block"); Add("冰", "ice"); Add("浮冰", "packed_ice");
        Add("蓝冰", "blue_ice"); Add("南瓜", "pumpkin"); Add("西瓜", "melon"); Add("甘蔗", "sugar_cane");
        Add("仙人掌", "cactus"); Add("TNT", "tnt"); Add("书架", "bookshelf"); Add("工作台", "crafting_table");
        Add("熔炉", "furnace"); Add("高炉", "blast_furnace"); Add("烟熏炉", "smoker");
        Add("附魔台", "enchanting_table"); Add("酿造台", "brewing_stand"); Add("砂轮", "grindstone");
        Add("锻造台", "smithing_table"); Add("切石机", "stonecutter"); Add("铁砧", "anvil");
        Add("床", "red_bed"); Add("箱子", "chest"); Add("末影箱", "ender_chest"); Add("陷阱箱", "trapped_chest");
        Add("活塞", "piston"); Add("粘性活塞", "sticky_piston"); Add("红石火把", "redstone_torch");
        Add("红石中继器", "repeater"); Add("红石比较器", "comparator"); Add("观察者", "observer");
        Add("发射器", "dispenser"); Add("投掷器", "dropper"); Add("漏斗", "hopper"); Add("蜘蛛网", "cobweb");
        Add("梯子", "ladder"); Add("栅栏", "oak_fence"); Add("栅栏门", "oak_fence_gate");
        Add("闪长岩", "diorite"); Add("安山岩", "andesite"); Add("花岗岩", "granite");
        Add("深板岩", "deepslate"); Add("方解石", "calcite"); Add("滴水石", "dripstone_block");
        Add("苔藓", "moss_block"); Add("紫珀块", "purpur_block"); Add("龙蛋", "dragon_egg");
        Add("下界疣", "nether_wart_block"); Add("下界石英块", "quartz_block"); Add("下界之星", "nether_star");

        // ===== 矿石 / 原石 / 锭 / 块 =====
        foreach (var (zh, id) in new (string, string)[]
        {
            ("煤炭矿石","coal_ore"), ("铁矿石","iron_ore"), ("金矿石","gold_ore"), ("钻石矿石","diamond_ore"),
            ("绿宝石矿石","emerald_ore"), ("青金石矿石","lapis_ore"), ("红石矿石","redstone_ore"),
            ("铜矿石","copper_ore"), ("下界石英矿石","nether_quartz_ore"), ("下界金矿石","nether_gold_ore"),
        }) Add(zh, id);

        foreach (var (zh, id) in new (string, string)[]
        {
            ("铁块","iron_block"), ("金块","gold_block"), ("钻石块","diamond_block"),
            ("绿宝石块","emerald_block"), ("青金石块","lapis_block"), ("红石块","redstone_block"),
            ("煤炭块","coal_block"), ("铜块","copper_block"), ("下界合金块","netherite_block"),
        }) Add(zh, id);

        Add("煤炭", "coal"); Add("木炭", "charcoal"); Add("铁锭", "iron_ingot"); Add("金锭", "gold_ingot");
        Add("铜锭", "copper_ingot"); Add("钻石", "diamond"); Add("绿宝石", "emerald");
        Add("青金石", "lapis_lazuli"); Add("红石", "redstone"); Add("下界合金锭", "netherite_ingot");
        Add("下界合金碎片", "netherite_scrap"); Add("紫水晶碎片", "amethyst_shard");
        Add("下界石英", "quartz"); Add("萤石粉", "glowstone_dust"); Add("火药", "gunpowder");
        Add("骨头", "bone"); Add("骨粉", "bone_meal"); Add("线", "string"); Add("羽毛", "feather");
        Add("皮革", "leather"); Add("烈焰棒", "blaze_rod"); Add("烈焰粉", "blaze_powder");
        Add("末影珍珠", "ender_pearl"); Add("末影之眼", "ender_eye"); Add("鞘翅", "elytra");

        // ===== 食物 =====
        Add("苹果", "apple"); Add("金苹果", "golden_apple"); Add("附魔金苹果", "enchanted_golden_apple");
        Add("面包", "bread"); Add("胡萝卜", "carrot"); Add("马铃薯", "potato");
        Add("烤马铃薯", "baked_potato"); Add("牛排", "cooked_beef"); Add("猪排", "cooked_porkchop");
        Add("鸡肉", "cooked_chicken"); Add("熟鱼", "cooked_cod"); Add("三叉戟", "trident");
        Add("弓", "bow"); Add("弩", "crossbow"); Add("箭", "arrow"); Add("盾牌", "shield");

        // ===== 木系方块：8 种木材 × 原木/木板/台阶/楼梯/栅栏/栅栏门/门/活板门/告示牌/树叶 =====
        var woods = new (string zh, string id)[]
        {
            ("橡木", "oak"), ("云杉", "spruce"), ("白桦", "birch"), ("丛林", "jungle"),
            ("金合欢", "acacia"), ("深色橡木", "dark_oak"), ("红树", "mangrove"), ("樱花", "cherry"),
        };
        var woodParts = new (string zh, string idSuffix)[]
        {
            ("原木", "log"), ("木板", "planks"), ("木台阶", "slab"), ("木楼梯", "stairs"),
            ("栅栏", "fence"), ("栅栏门", "fence_gate"), ("门", "door"), ("活板门", "trapdoor"),
            ("告示牌", "sign"), ("树叶", "leaves"),
        };
        foreach (var (wzh, wid) in woods)
            foreach (var (pzh, pid) in woodParts)
                Add(wzh + pzh, $"{wid}_{pid}");

        // ===== 16 种颜色：羊毛 / 混凝土 / 陶瓦（染色黏土）/ 玻璃 / 地毯 =====
        var colors = new (string zh, string id)[]
        {
            ("白色", "white"), ("橙色", "orange"), ("品红色", "magenta"), ("淡蓝色", "light_blue"),
            ("黄色", "yellow"), ("黄绿色", "lime"), ("粉红色", "pink"), ("灰色", "gray"),
            ("淡灰色", "light_gray"), ("青色", "cyan"), ("紫色", "purple"), ("蓝色", "blue"),
            ("棕色", "brown"), ("绿色", "green"), ("红色", "red"), ("黑色", "black"),
        };
        var colorParts = new (string zh, string idSuffix)[]
        {
            ("羊毛", "wool"), ("混凝土", "concrete"), ("陶瓦", "terracotta"),
            ("染色玻璃", "stained_glass"), ("地毯", "carpet"), ("混凝土粉末", "concrete_powder"),
        };
        foreach (var (czh, cid) in colors)
            foreach (var (pzh, pid) in colorParts)
                Add(czh + pzh, $"{cid}_{pid}");

        // ===== 深板岩矿石变种 =====
        foreach (var (zh, id) in new (string, string)[]
        {
            ("深板岩煤矿石","deepslate_coal_ore"), ("深板岩铁矿石","deepslate_iron_ore"),
            ("深板岩金矿石","deepslate_gold_ore"), ("深板岩钻石矿石","deepslate_diamond_ore"),
            ("深板岩绿宝石矿石","deepslate_emerald_ore"), ("深板岩青金石矿石","deepslate_lapis_ore"),
            ("深板岩红石矿石","deepslate_redstone_ore"), ("深板岩铜矿石","deepslate_copper_ore"),
        }) Add(zh, id);

        // ===== 建筑石材变种 =====
        foreach (var (zh, id) in new (string, string)[]
        {
            ("石砖","stone_bricks"), ("苔石砖","mossy_stone_bricks"), ("裂纹石砖","cracked_stone_bricks"),
            ("雕纹石砖","chiseled_stone_bricks"), ("平滑石头","smooth_stone"), ("砖块","bricks"),
            ("苔石","mossy_cobblestone"), ("下界砖块","nether_bricks"), ("红色下界砖块","red_nether_bricks"),
            ("玄武岩","basalt"), ("平滑玄武岩","smooth_basalt"), ("黑石","blackstone"),
            ("磨制黑石","polished_blackstone"), ("哭泣的黑曜石","crying_obsidian"),
            ("绯红菌岩","crimson_nylium"), ("诡异菌岩","warped_nylium"), ("绯红木","crimson_stem"),
            ("诡异木","warped_stem"), ("下界疣块","nether_wart_block"), ("扭曲疣块","warped_wart_block"),
            ("砂岩","sandstone"), ("红砂岩","red_sandstone"), ("石英块","quartz_block"),
            ("平滑石英块","smooth_quartz"), ("紫珀块","purpur_block"), ("末地石砖","end_stone_bricks"),
            ("方解石","calcite"), ("凝灰岩","tuff"), ("花岗岩","granite"), ("闪长岩","diorite"), ("安山岩","andesite"),
        }) Add(zh, id);

        // ===== 海洋 / 植物 / 功能方块 =====
        foreach (var (zh, id) in new (string, string)[]
        {
            ("海晶石","prismarine"), ("暗海晶石","dark_prismarine"), ("海晶灯","sea_lantern"),
            ("海泡菜","sea_pickle"), ("海草","seagrass"), ("海带","kelp"), ("海绵","sponge"),
            ("干海绵","dry_sponge"), ("珊瑚块","brain_coral_block"), ("荧光墨囊","glow_ink_sac"),
            ("南瓜灯","jack_o_lantern"), ("雕刻南瓜","carved_pumpkin"), ("干草块","hay_block"),
            ("蜂箱","beehive"), ("蜂巢","bee_nest"), ("蜜脾块","honeycomb_block"),
            ("篝火","campfire"), ("灵魂篝火","soul_campfire"), ("讲台","lectern"),
            ("堆肥桶","composter"), ("炼药锅","cauldron"), ("钟","bell"),
            ("磁石","lodestone"), ("重生锚","respawn_anchor"), ("信标","beacon"),
            ("木桶","barrel"), ("陷阱箱","trapped_chest"), ("拉杆","lever"),
            ("石质按钮","stone_button"), ("木质按钮","oak_button"), ("石质压力板","stone_pressure_plate"),
            ("重质测重压力板","heavy_weighted_pressure_plate"), ("阳光传感器","daylight_detector"),
            ("标靶","target"), ("界伏晶体","end_crystal"), ("下界合金锭","netherite_ingot"),
            ("蜘蛛网","cobweb"), ("末影灯笼","soul_lantern"), ("灯笼","lantern"), ("火把","torch"),
            ("红石灯","redstone_lamp"), ("指南针","compass"), ("时钟","clock"), ("地图","map"),
        }) Add(zh, id);

        // ===== 六种材质的工具与武器 =====
        var materials = new (string zh, string id)[]
        {
            ("木", "wooden"), ("石", "stone"), ("铁", "iron"), ("金", "golden"), ("钻石", "diamond"), ("下界合金", "netherite"),
        };
        var tools = new (string zh, string id)[]
        {
            ("剑", "sword"), ("镐", "pickaxe"), ("斧", "axe"), ("锹", "shovel"), ("锄", "hoe"),
        };
        foreach (var (mzh, mid) in materials)
            foreach (var (tzh, tid) in tools)
                Add(mzh + tzh, $"{mid}_{tid}");

        return d;
    }

    /// <summary>生成成就提示图片。itemId 内部会自动归一化。
    /// 传入本机已安装游戏的 minecraftDir + versionId 时，物品图标用游戏里真实的
    /// 原版贴图（从 version jar 或 assets/objects 里读取）；传入 bedrockGameDir（用户手动
    /// 指定的已安装基岩版所在目录）时，直接从基岩版游戏包里读真实贴图；都读不到时
    /// 退回自绘矢量图标，保证功能始终可用。</summary>
    /// <summary>上一次 Generate 调用是否成功用上了真实贴图（[ThreadStatic]，因为生成现在从
    /// UI 线程挪到了后台线程跑，不能用普通静态字段——那样并发调用会互相踩数据）。
    /// 供界面在生成完后提示"用的是真实贴图"还是"没找到、用的兜底图标"。</summary>
    [ThreadStatic] private static bool _lastUsedRealTexture;
    public static bool LastGenerateUsedRealTexture => _lastUsedRealTexture;

    public static byte[] Generate(string itemId, string achievementName, string firstLine, string? secondLine,
        string? minecraftDir = null, string? versionId = null, string? bedrockGameDir = null)
    {
        _lastUsedRealTexture = false;
        var id = NormalizeItemId(itemId);

        const int width = 520;
        const int height = 80;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bgBrush = new SolidColorBrush(Color.FromArgb(235, 28, 28, 30));
            var borderBrush = new SolidColorBrush(Color.FromArgb(255, 12, 12, 14));
            dc.DrawRoundedRectangle(bgBrush, new Pen(borderBrush, 2), new Rect(1, 1, width - 2, height - 2), 3, 3);

            DrawItemIcon(dc, new Rect(12, 12, 56, 56), id.Path, minecraftDir, versionId, bedrockGameDir);

            const double textX = 84.0;

            var achievementText = new FormattedText(
                string.IsNullOrWhiteSpace(achievementName) ? "Advancement made!" : achievementName,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, new SolidColorBrush(Color.FromRgb(255, 215, 0)), 1.25);
            dc.DrawText(achievementText, new Point(textX, 11));

            var firstLineText = new FormattedText(
                firstLine ?? "",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 20, Brushes.White, 1.25);
            firstLineText.MaxTextWidth = width - textX - 14;
            firstLineText.MaxLineCount = 1;
            firstLineText.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(firstLineText, new Point(textX, 29));

            if (!string.IsNullOrWhiteSpace(secondLine))
            {
                var secondLineText = new FormattedText(
                    secondLine,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 13, new SolidColorBrush(Color.FromRgb(196, 196, 200)), 1.25);
                secondLineText.MaxTextWidth = width - textX - 14;
                secondLineText.MaxLineCount = 1;
                secondLineText.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(secondLineText, new Point(textX, 55));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ==================== 物品图标绘制 ====================

    /// <summary>
    /// 按物品 ID 推断材质颜色。规则来自物品命名本身（diamond_sword / golden_apple 这种
    /// 前缀就是材质）；认不出来时按 ID 稳定哈希取色相——同一个 ID 每次生成颜色一致。
    /// </summary>
    private static (Color Main, Color Dark) GuessMaterialColor(string path)
    {
        static (Color, Color) Pair(byte r, byte g, byte b) =>
            (Color.FromRgb(r, g, b), Color.FromRgb((byte)(r * 0.62), (byte)(g * 0.62), (byte)(b * 0.62)));

        if (path.Contains("netherite")) return Pair(76, 66, 68);
        if (path.Contains("diamond")) return Pair(92, 219, 213);
        if (path.Contains("gold")) return Pair(249, 200, 72);
        if (path.Contains("iron")) return Pair(216, 216, 216);
        if (path.Contains("emerald")) return Pair(63, 191, 118);
        if (path.Contains("lapis")) return Pair(48, 88, 178);
        if (path.Contains("redstone")) return Pair(203, 42, 32);
        if (path.Contains("copper")) return Pair(199, 118, 78);
        if (path.Contains("amethyst")) return Pair(154, 108, 214);
        if (path.Contains("cobble") || path.Contains("stone")) return Pair(130, 130, 130);
        if (path.Contains("plank") || path.Contains("wood") || path.Contains("oak")) return Pair(162, 130, 78);
        if (path.Contains("leather")) return Pair(160, 101, 64);
        if (path.Contains("apple")) return Pair(216, 54, 44);
        if (path.Contains("grass") || path.Contains("leaves")) return Pair(94, 168, 72);
        if (path.Contains("water")) return Pair(60, 110, 220);
        if (path.Contains("lava") || path.Contains("fire") || path.Contains("blaze")) return Pair(232, 116, 32);
        if (path.Contains("ender") || path.Contains("chorus")) return Pair(38, 132, 122);
        if (path.Contains("coal")) return Pair(48, 48, 48);
        if (path.Contains("quartz") || path.Contains("bone")) return Pair(232, 228, 214);

        var h = 0;
        foreach (var c in path) h = unchecked(h * 31 + c);
        var hue = Math.Abs(h) % 360;
        var col = HsvToRgb(hue, 0.55, 0.82);
        return (col, Color.FromRgb((byte)(col.R * 0.62), (byte)(col.G * 0.62), (byte)(col.B * 0.62)));
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>按 ID 里的类别关键词选形状绘制。全部是自绘几何图形，不引用原版贴图。</summary>
    /// <summary>这些是"手持类"物品（剑/镐/斧/锹/锭/食物/药水/书/宝石原石），Minecraft 里它们本来就是
    /// 平面图标，不是方块，所以贴图找到了就直接平铺；除此之外的都按"方块"处理——方块在游戏物品栏里
    /// 从来不是纯色平面，是能看出三个面的等距立体图标，找不到"简单的立方体"这种偷懒画法的借口。</summary>
    private static bool IsHandheldItemShape(string path)
        => path.Contains("sword") || path.Contains("pickaxe") || path.Contains("axe")
           || path.Contains("shovel") || path.Contains("spade") || path.Contains("ingot")
           || path.Contains("apple") || path.Contains("berry") || path.Contains("melon")
           || path.Contains("potion") || path.Contains("bottle") || path.Contains("book")
           || path.Contains("enchant") || path.Contains("diamond") || path.Contains("emerald")
           || path.Contains("amethyst") || path.Contains("gem") || path.Contains("shard");

    private static void DrawItemIcon(DrawingContext dc, Rect box, string path,
        string? minecraftDir, string? versionId, string? bedrockGameDir = null)
    {
        var (main, dark) = GuessMaterialColor(path);
        Brush mainBrush = new SolidColorBrush(main);
        Brush darkBrush = new SolidColorBrush(dark);
        Brush handleBrush = new SolidColorBrush(Color.FromRgb(140, 106, 62));

        // 物品栏格子观感：深色内凹方块
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(255, 58, 58, 62)),
            new Pen(new SolidColorBrush(Color.FromArgb(255, 22, 22, 24)), 2), box);

        var isHandheld = IsHandheldItemShape(path);

        if (!isHandheld)
        {
            // 方块：优先用真实方块贴图渲染成能看出三个面的等距立体图标（不是拍扁的正方形贴图，
            // 更不是随便涂个纯色的"简单立方体"占位）。原木/草方块这类顶面和侧面贴图不一样的，
            // 分别取 "<path>_top" / "<path>_side"；只有单一贴图的方块（石头、矿石块等）三个面
            // 共用同一张贴图，靠明暗差体现立体感——跟原版物品栏里的方块图标观感一致。
            var topTex = LoadItemTexture(minecraftDir, versionId, bedrockGameDir, path + "_top");
            var sideTex = LoadItemTexture(minecraftDir, versionId, bedrockGameDir, path + "_side");
            var flatTex = topTex == null && sideTex == null
                ? LoadItemTexture(minecraftDir, versionId, bedrockGameDir, path)
                : null;

            if (topTex != null || sideTex != null || flatTex != null)
            {
                var t = topTex ?? sideTex ?? flatTex!;
                var l = sideTex ?? flatTex ?? topTex!;
                var rt = sideTex ?? flatTex ?? topTex!;
                DrawTexturedIsoCube(dc, box, t, l, rt);
                _lastUsedRealTexture = true;
                return;
            }
        }
        else
        {
            // 手持类物品本身在游戏里就是平面图标，贴图找到了直接平铺，不用渲染成方块
            var texture = LoadItemTexture(minecraftDir, versionId, bedrockGameDir, path);
            if (texture != null)
            {
                var inner = new Rect(box.X + 6, box.Y + 6, box.Width - 12, box.Height - 12);
                dc.DrawImage(texture, inner);
                _lastUsedRealTexture = true;
                return;
            }
        }

        var r = new Rect(box.X + 8, box.Y + 8, box.Width - 16, box.Height - 16);

        double X(double t) => r.X + r.Width * t;
        double Y(double t) => r.Y + r.Height * t;

        void Poly(Brush brush, params double[] xy)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(xy[0]), Y(xy[1])), true, true);
                for (var i = 2; i < xy.Length; i += 2)
                    ctx.LineTo(new Point(X(xy[i]), Y(xy[i + 1])), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(brush, null, geo);
        }

        void Rect01(Brush brush, double x0, double y0, double x1, double y1) =>
            dc.DrawRectangle(brush, null, new Rect(X(x0), Y(y0), X(x1) - X(x0), Y(y1) - Y(y0)));

        if (path.Contains("sword"))
        {
            Rect01(handleBrush, 0.42, 0.70, 0.58, 0.98);
            Rect01(darkBrush, 0.26, 0.62, 0.74, 0.72);
            Poly(mainBrush, 0.5, 0.02, 0.66, 0.20, 0.62, 0.64, 0.38, 0.64, 0.34, 0.20);
        }
        else if (path.Contains("pickaxe"))
        {
            Rect01(handleBrush, 0.44, 0.30, 0.56, 0.98);
            Poly(mainBrush, 0.06, 0.28, 0.30, 0.08, 0.70, 0.08, 0.94, 0.28,
                            0.80, 0.30, 0.62, 0.20, 0.38, 0.20, 0.20, 0.30);
        }
        else if (path.Contains("axe"))
        {
            Rect01(handleBrush, 0.46, 0.16, 0.58, 0.98);
            Poly(mainBrush, 0.46, 0.10, 0.86, 0.16, 0.88, 0.48, 0.46, 0.54);
        }
        else if (path.Contains("shovel") || path.Contains("spade"))
        {
            Rect01(handleBrush, 0.44, 0.30, 0.56, 0.98);
            Poly(mainBrush, 0.30, 0.06, 0.70, 0.06, 0.66, 0.40, 0.34, 0.40);
        }
        else if (path.Contains("ingot"))
        {
            Poly(mainBrush, 0.16, 0.34, 0.84, 0.34, 0.96, 0.70, 0.04, 0.70);
            Poly(darkBrush, 0.16, 0.34, 0.84, 0.34, 0.78, 0.44, 0.22, 0.44);
        }
        else if (path.Contains("apple") || path.Contains("berry") || path.Contains("melon"))
        {
            dc.DrawEllipse(mainBrush, null, new Point(X(0.5), Y(0.58)), r.Width * 0.36, r.Height * 0.34);
            Rect01(new SolidColorBrush(Color.FromRgb(110, 78, 44)), 0.47, 0.12, 0.53, 0.28);
            Poly(new SolidColorBrush(Color.FromRgb(94, 168, 72)), 0.53, 0.18, 0.80, 0.10, 0.66, 0.28);
        }
        else if (path.Contains("potion") || path.Contains("bottle"))
        {
            Rect01(new SolidColorBrush(Color.FromRgb(190, 190, 195)), 0.42, 0.06, 0.58, 0.24);
            dc.DrawEllipse(mainBrush, new Pen(darkBrush, 1.5),
                new Point(X(0.5), Y(0.66)), r.Width * 0.34, r.Height * 0.30);
        }
        else if (path.Contains("book") || path.Contains("enchant"))
        {
            Rect01(new SolidColorBrush(Color.FromRgb(150, 52, 44)), 0.14, 0.14, 0.86, 0.86);
            Rect01(new SolidColorBrush(Color.FromRgb(238, 232, 214)), 0.24, 0.20, 0.86, 0.80);
            Rect01(new SolidColorBrush(Color.FromRgb(120, 40, 34)), 0.14, 0.14, 0.24, 0.86);
        }
        else if (path.Contains("diamond") || path.Contains("emerald") || path.Contains("amethyst")
                 || path.Contains("gem") || path.Contains("shard"))
        {
            Poly(mainBrush, 0.5, 0.06, 0.92, 0.40, 0.5, 0.94, 0.08, 0.40);
            Poly(darkBrush, 0.5, 0.06, 0.92, 0.40, 0.5, 0.48, 0.08, 0.40);
        }
        else
        {
            // 兜底：等距像素方块，比一个字母像"游戏里的物品"得多
            Poly(mainBrush, 0.5, 0.06, 0.94, 0.30, 0.5, 0.54, 0.06, 0.30);
            Poly(darkBrush, 0.06, 0.30, 0.5, 0.54, 0.5, 0.96, 0.06, 0.72);
            Poly(new SolidColorBrush(Color.FromRgb(
                    (byte)(main.R * 0.80), (byte)(main.G * 0.80), (byte)(main.B * 0.80))),
                0.94, 0.30, 0.94, 0.72, 0.5, 0.96, 0.5, 0.54);
        }
    }

    /// <summary>
    /// 用真实方块贴图画一个能看出三个面的等距立体方块图标（原版物品栏里方块图标的观感），
    /// 而不是拍扁的单张贴图或者随手涂色的"简单立方体"。
    ///
    /// 三个面（顶/左/右）在等距投影下都是平行四边形，平行四边形由一个顶点 + 两条边向量唯一确定，
    /// 所以直接用仿射变换（MatrixTransform）把贴图这张 1x1 的单位正方形映射到每个面的三个角上，
    /// 就是精确的贴图映射，不需要透视——这也是等距方块图标能只靠仿射变换画对的原因。
    /// 顶面用原始亮度，左右两个侧面各叠一层半透明黑色做明暗差，模拟光照，观感和游戏里一致。
    /// </summary>
    private static void DrawTexturedIsoCube(DrawingContext dc, Rect box,
        BitmapSource topTexture, BitmapSource leftTexture, BitmapSource rightTexture)
    {
        var r = new Rect(box.X + 6, box.Y + 4, box.Width - 12, box.Height - 8);
        Point P(double t, double u) => new(r.X + r.Width * t, r.Y + r.Height * u);

        // 面的画法说明：给出平行四边形的三个角 origin/right/down，
        // origin→right 是贴图 U 方向、origin→down 是贴图 V 方向，第四个角自动是 right+down-origin。
        void Face(Point origin, Point right, Point down, BitmapSource tex, double shade)
        {
            var m = new Matrix(
                right.X - origin.X, right.Y - origin.Y,
                down.X - origin.X, down.Y - origin.Y,
                origin.X, origin.Y);

            dc.PushTransform(new MatrixTransform(m));
            dc.DrawImage(tex, new Rect(0, 0, 1, 1));
            dc.Pop();

            if (shade < 1.0)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(origin, true, true);
                    ctx.LineTo(right, true, false);
                    ctx.LineTo(new Point(right.X + down.X - origin.X, right.Y + down.Y - origin.Y), true, false);
                    ctx.LineTo(down, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)((1 - shade) * 200), 0, 0, 0)), null, geo);
            }
        }

        // 顶面：满亮度
        Face(P(0.5, 0.02), P(0.94, 0.26), P(0.06, 0.26), topTexture, 1.0);
        // 左侧面：稍暗
        Face(P(0.06, 0.26), P(0.5, 0.50), P(0.06, 0.68), leftTexture, 0.62);
        // 右侧面：最暗
        Face(P(0.5, 0.50), P(0.94, 0.26), P(0.5, 0.92), rightTexture, 0.78);
    }

    // ==================== 原版贴图读取 ====================

    /// <summary>贴图缓存：key = "{minecraftDir}|{versionId}|{itemPath}"，避免每次预览都重读 jar/对象文件。</summary>
    private static readonly ConcurrentDictionary<string, BitmapSource?> TextureCache = new();

    /// <summary>
    /// 从已安装的原版游戏里读取物品贴图（真正的那张游戏贴图，不是模拟图形）。
    /// 查找顺序：
    ///   1) 用户手动指定的基岩版游戏目录（{dir}/Minecraft.Windows/data/textures/items|blocks/&lt;path&gt;.png
    ///      或 {dir}/data/textures/...）；
    ///   2) 版本 jar 内的 assets/minecraft/textures/item|items|block/&lt;path&gt;.png（老版本贴图都在 jar 里）；
    ///   3) 新版本（jar 里不再带贴图）走 assets/indexes/&lt;id&gt;.json 索引 + assets/objects/xx/&lt;hash&gt; 对象文件。
    /// 找不到/没装游戏时返回 null，由调用方退回自绘图标。
    /// </summary>
    private static BitmapSource? LoadItemTexture(string? minecraftDir, string? versionId, string? bedrockGameDir, string itemPath)
    {
        if (string.IsNullOrWhiteSpace(minecraftDir) && string.IsNullOrWhiteSpace(bedrockGameDir)) return null;

        var cacheKey = $"{minecraftDir}|{versionId}|{bedrockGameDir}|{itemPath}";
        if (TextureCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = LoadItemTextureCore(minecraftDir, versionId, bedrockGameDir, itemPath);
        TextureCache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// 基岩版游戏包内物品贴图的候选路径。已安装的基岩版（UWP/GDK）内容目录结构：
    /// {dir}/Minecraft.Windows/data/textures/items|blocks/&lt;name&gt;.png（1.13+ 统一叫 items，
    /// 老版本叫 blocks 的是方块面），或旧版直接 {dir}/data/textures/...。
    /// 枚举两个根 + items/blocks 两种目录名，全部兜一圈。
    /// </summary>
    private static IEnumerable<string> EnumerateBedrockTextureCandidates(string gameDir, string itemPath)
    {
        var roots = new[] { gameDir, Path.Combine(gameDir, "Minecraft.Windows") };
        var subDirs = new[] { "items", "blocks" };
        var prefixes = new[] { "data\\textures", "textures" };

        foreach (var root in roots)
        {
            foreach (var prefix in prefixes)
            {
                foreach (var sub in subDirs)
                {
                    yield return Path.Combine(root, prefix, sub, $"{itemPath}.png");
                }
            }
        }
    }

    // ==================== 联网贴图下载 ====================

    /// <summary>在线贴图仓库：InventivetalentDev/minecraft-assets（官方资源镜像，按版本 tag 分支）。
    /// 路径格式 assets/minecraft/textures/{item|block}/&lt;name&gt;.png。</summary>
    private static readonly string[] McAssetsVersions = { "1.21.4", "1.21", "1.20.4", "1.19.4" };

    /// <summary>下载一张真实贴图到本地。带磁盘缓存（xcl2/texture_cache/）与总时长保护，
    /// 首次联网最多等约 8 秒，之后直接读缓存不再联网。</summary>
    private static byte[]? DownloadItemTextureFromWeb(string itemPath)
    {
        var cacheDir = Path.Combine(App.DataDir, "texture_cache");
        var cachePath = Path.Combine(cacheDir, $"{itemPath}.png");
        if (File.Exists(cachePath))
        {
            try { return File.ReadAllBytes(cachePath); } catch { }
        }

        var sw = Stopwatch.StartNew();
        foreach (var version in McAssetsVersions)
        {
            foreach (var sub in new[] { "item", "block" })
            {
                foreach (var url in EnumerateMcAssetsUrls(version, sub, itemPath))
                {
                    if (sw.ElapsedMilliseconds > 8000) return null;
                    try
                    {
                        using var resp = SharedHttpClient.GetAsync(url).GetAwaiter().GetResult();
                        if (!resp.IsSuccessStatusCode) continue;
                        var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        if (bytes.Length > 0 && IsPng(bytes))
                        {
                            try
                            {
                                Directory.CreateDirectory(cacheDir);
                                File.WriteAllBytes(cachePath, bytes);
                            }
                            catch { }
                            return bytes;
                        }
                    }
                    catch { /* 试下一个源 */ }
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateMcAssetsUrls(string version, string sub, string itemPath)
    {
        var path = $"assets/minecraft/textures/{sub}/{itemPath}.png";
        yield return $"https://raw.githubusercontent.com/InventivetalentDev/minecraft-assets/{version}/{path}";
        yield return $"https://cdn.jsdelivr.net/gh/InventivetalentDev/minecraft-assets@{version}/{path}";
        yield return $"https://fastly.jsdelivr.net/gh/InventivetalentDev/minecraft-assets@{version}/{path}";
        yield return $"https://gcore.jsdelivr.net/gh/InventivetalentDev/minecraft-assets@{version}/{path}";
        yield return $"https://ghp.ci/https://raw.githubusercontent.com/InventivetalentDev/minecraft-assets/{version}/{path}";
        yield return $"https://ghproxy.com/https://raw.githubusercontent.com/InventivetalentDev/minecraft-assets/{version}/{path}";
        yield return $"https://mirror.ghproxy.com/https://raw.githubusercontent.com/InventivetalentDev/minecraft-assets/{version}/{path}";
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length > 8
           && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G'
           && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    private static BitmapSource? LoadItemTextureCore(string? minecraftDir, string? versionId, string? bedrockGameDir, string itemPath)
    {
        // 1) 用户手动指定的基岩版游戏目录里的真实贴图
        if (!string.IsNullOrWhiteSpace(bedrockGameDir) && Directory.Exists(bedrockGameDir))
        {
            foreach (var texPath in EnumerateBedrockTextureCandidates(bedrockGameDir, itemPath))
                if (File.Exists(texPath))
                {
                    var tex = LoadBitmap(File.ReadAllBytes(texPath));
                    if (tex != null) return tex;
                }
        }

        // 2) 直接从网上下载一份真实贴图（InventivetalentDev/minecraft-assets 镜像仓库，
        //    多个版本 tag + jsdelivr/ghproxy 加速源，成功即落盘缓存，之后不再联网）
        var webBytes = DownloadItemTextureFromWeb(itemPath);
        if (webBytes != null)
        {
            var tex = LoadBitmap(webBytes);
            if (tex != null) return tex;
        }

        if (string.IsNullOrWhiteSpace(minecraftDir) || !Directory.Exists(minecraftDir) || string.IsNullOrWhiteSpace(versionId))
            return null;

        var candidates = new[]
        {
            $"assets/minecraft/textures/item/{itemPath}.png",
            $"assets/minecraft/textures/items/{itemPath}.png",   // 1.13 之前叫 items（复数）
            $"assets/minecraft/textures/block/{itemPath}.png",   // 方块类物品没有 item 贴图，直接用方块面
        };

        // 1) version jar 内贴图
        var versionDir = Path.Combine(minecraftDir, "versions", versionId);
        var jarPath = Path.Combine(versionDir, $"{versionId}.jar");
        if (File.Exists(jarPath))
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var c in candidates)
                {
                    var entry = zip.GetEntry(c);
                    if (entry == null) continue;
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    return LoadBitmap(ms.ToArray());
                }
            }
            catch { /* jar 损坏就跳过，继续试 assets/objects */ }
        }

        // 2) assets/indexes + assets/objects（新版本贴图所在）
        try
        {
            var assetId = ResolveAssetIndexId(minecraftDir, versionId, 0);
            if (string.IsNullOrWhiteSpace(assetId)) return null;

            var indexPath = Path.Combine(minecraftDir, "assets", "indexes", $"{assetId}.json");
            if (!File.Exists(indexPath)) return null;

            var index = JsonSerializer.Deserialize<AssetIndexFile>(File.ReadAllText(indexPath));
            if (index == null) return null;

            foreach (var c in candidates)
            {
                if (!index.Objects.TryGetValue(c, out var obj) || string.IsNullOrWhiteSpace(obj.Hash)) continue;
                var objPath = Path.Combine(minecraftDir, "assets", "objects", obj.Hash[..2], obj.Hash);
                if (File.Exists(objPath))
                    return LoadBitmap(File.ReadAllBytes(objPath));
            }
        }
        catch { /* 读取失败退回自绘 */ }

        return null;
    }

    /// <summary>解析版本 json 的 assetIndex.id（新版在 assetIndex 对象里，老版在 assets 字段；
    /// 加载器版本靠 inheritsFrom 指向原版，逐级向上找）。</summary>
    private static string? ResolveAssetIndexId(string minecraftDir, string versionId, int depth)
    {
        if (depth > 5) return null;

        var versionJson = Path.Combine(minecraftDir, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(versionJson)) return null;

        VersionDetail? detail;
        try { detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(versionJson)); }
        catch { return null; }
        if (detail == null) return null;

        if (!string.IsNullOrWhiteSpace(detail.AssetIndex?.Id)) return detail.AssetIndex.Id;
        if (!string.IsNullOrWhiteSpace(detail.Assets)) return detail.Assets;
        if (!string.IsNullOrWhiteSpace(detail.InheritsFrom)) return ResolveAssetIndexId(minecraftDir, detail.InheritsFrom, depth + 1);
        return null;
    }

    private static BitmapSource? LoadBitmap(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static string SaveToFile(byte[] pngBytes, string saveDir, string fileName)
    {
        Directory.CreateDirectory(saveDir);
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";
        var path = Path.Combine(saveDir, fileName);
        File.WriteAllBytes(path, pngBytes);
        return path;
    }
}
