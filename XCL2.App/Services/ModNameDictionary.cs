namespace XCL2.App.Services;

/// <summary>
/// 常见 Mod 的中文名 -> 英文名/关键词 对照词典。
///
/// 背景：Modrinth/CurseForge 的搜索接口本身只认英文/项目原名，中文关键词大概率搜不到东西
/// (如搜"钠"完全没有结果)，用户只能先去 MC百科 用中文名查到对应的英文名，再回来手动输入
/// 英文名重新搜一遍——这个词典就是为了跳过这一步，直接在综合搜索里把命中的中文关键词
/// 翻译/追加成对应的英文名，让"搜索钠 -> 直接出现 Sodium"成为可能。
///
/// 这是一份手工维护的静态词典，覆盖社区里最常被中文玩家提及、认知度最高的一批 Mod
/// (优化类、显示类、大型内容类等)，不追求覆盖 Modrinth 上的全部项目——那需要一个真正的
/// 中文全文搜索/翻译服务，超出"轻量搜索辅助"的范畴。词典可以随时按需追加新条目。
///
/// 匹配策略：查询词精确等于某个中文名，或查询词是中文名的子串/中文名是查询词的子串
/// (照顾"钠模组""装个钠"这类带前后缀的说法)，命中后返回对应英文名，交给调用方追加到
/// 实际发给 Modrinth/CurseForge 的搜索关键词里。
/// </summary>
public static class ModNameDictionary
{
    /// <summary>中文名 -> 英文名（搜索关键词）。key 统一小写/去空格不做处理，直接按原样中文匹配即可。</summary>
    private static readonly Dictionary<string, string> Entries = new()
    {
        // ---- 性能优化类 ----
        ["钠"] = "Sodium",
        ["光哈"] = "Iris",
        ["虹膜"] = "Iris",
        ["幻翼优化"] = "Phosphor",
        ["磷"] = "Phosphor",
        ["模组菜单"] = "Mod Menu",
        ["锂"] = "Lithium",
        ["织女星"] = "Starlight",
        ["星光"] = "Starlight",
        ["快速异步"] = "FerriteCore",
        ["铁氧体核心"] = "FerriteCore",
        ["入口传送优化"] = "Lazy DFU",
        ["懒加载"] = "Lazy DFU",
        ["实体视野裁剪"] = "Entity Culling",
        ["实体剔除"] = "Entity Culling",
        ["净化"] = "C2ME",
        ["区块并发"] = "C2ME",
        ["杯子"] = "Sodium Extra",
        ["钠扩展"] = "Sodium Extra",
        ["奥库勒斯"] = "Oculus",
        ["眼球"] = "Oculus",
        ["体素地图"] = "Voxelmap",
        ["小地图"] = "Xaero's Minimap",
        ["之矿"] = "Xaero's World Map",
        ["世界地图"] = "Xaero's World Map",

        // ---- 加载器/API 类 ----
        ["织"] = "Fabric API",
        ["纤维"] = "Fabric API",
        ["锻造"] = "Forge",
        ["新锻造"] = "NeoForge",
        ["石英"] = "Quilt",

        // ---- 大型内容/科技/魔法类 ----
        ["工业2"] = "IndustrialCraft 2",
        ["IC2"] = "IndustrialCraft 2",
        ["格雷科技"] = "GregTech",
        ["热力膨胀"] = "Thermal Expansion",
        ["应用能源2"] = "Applied Energistics 2",
        ["AE2"] = "Applied Energistics 2",
        ["工程师工具箱"] = "Immersive Engineering",
        ["沉浸工程"] = "Immersive Engineering",
        ["匠魂"] = "Tinkers' Construct",
        ["神秘时代"] = "Thaumcraft",
        ["植物魔法"] = "Botania",
        ["魔法金属"] = "Astral Sorcery",
        ["星辰魔法"] = "Astral Sorcery",
        ["末影接口"] = "EnderIO",
        ["末影修改"] = "EnderIO",
        ["生存扩展"] = "Tough As Nails",
        ["更多生物"] = "Alex's Mobs",
        ["爱丽丝的生物"] = "Alex's Mobs",
        ["暮色森林"] = "The Twilight Forest",
        ["咆哮深渊"] = "Roguelike Dungeons",
        ["地牢"] = "Roguelike Dungeons",
        ["人偶"] = "Iron Golems",
        ["直Obs"] = "JEI",
        ["整合视觉"] = "JEI",
        ["合成表"] = "JEI",
        ["方块信息"] = "JEI",
        ["模组集成"] = "JEI",
        ["单机联机"] = "LAN Server Properties",
        ["更多村庄"] = "Villager Names",
        ["精粹"] = "Quark",
        ["夸克"] = "Quark",
        ["工艺"] = "Create",
        ["建筑"] = "Create",
        ["机械动力"] = "Create",

        // ---- 存档/备份/工具类 ----
        ["存档管理"] = "AppleSkin",
        ["苹果皮"] = "AppleSkin",
        ["背包整理"] = "Inventory Sorter",
        ["物品栏排序"] = "Inventory Sorter",
        ["坐标显示"] = "Xaero's Minimap",
        ["自动保存"] = "Auto Save",
        ["多人聊天"] = "Chat Heads",
        ["聊天头像"] = "Chat Heads",

        // ---- 本轮补充：更多高知名度 Mod（覆盖优化/内容/工具三大类里常被提及但词典里还没有的） ----
        // 性能优化类
        ["泰坦内存优化"] = "MemoryLeakFix",
        ["内存泄漏修复"] = "MemoryLeakFix",
        ["异步区块"] = "Concurrent Chunk Management Engine",
        ["模型修正"] = "Enhanced Block Entities",
        ["更快的服务器"] = "Krypton",
        ["网络优化"] = "Krypton",
        ["动态视距"] = "Dynamic FPS",
        ["动态帧数"] = "Dynamic FPS",
        ["更快的叶子渲染"] = "FastLeafDecay",
        ["快速掉叶"] = "FastLeafDecay",
        ["粒子优化"] = "Particle Core",
        ["方块实体优化"] = "Enhanced Block Entities",

        // 显示/UI/QoL 类
        ["生物血条"] = "Better Mob Attack Indicator",
        ["伤害数字"] = "Damage Indicators",
        ["伤害提示"] = "Damage Indicators",
        ["血条显示"] = "Damage Indicators",
        ["更多按键"] = "Controlling",
        ["按键修改"] = "Controlling",
        ["快捷背包"] = "Inventory Profiles Next",
        ["盔甲显示"] = "Armor Status HUD",
        ["盔甲状态"] = "Armor Status HUD",
        ["物品栏管理"] = "Inventory Profiles Next",
        ["合成书"] = "Just Enough Items",
        ["合成公式"] = "Just Enough Items",
        ["配方查看"] = "Just Enough Items",
        ["工具提示"] = "Wthit",
        ["方块提示"] = "Wthit",
        ["视锥修剪"] = "Sodium",
        ["截图工具"] = "Not Enough Screenshots",
        ["小地图之矿"] = "Xaero's World Map",
        ["实时地图"] = "Journeymap",
        ["旅程地图"] = "Journeymap",

        // 大型内容/科技/魔法/冒险类
        ["工业时代"] = "IndustrialCraft 2",
        ["现代工业"] = "Mekanism",
        ["梅卡传动"] = "Mekanism",
        ["核能"] = "Mekanism",
        ["应用元件"] = "Applied Energistics 2",
        ["精致仓储"] = "Refined Storage",
        ["精炼存储"] = "Refined Storage",
        ["模块化机械"] = "Immersive Engineering",
        ["建筑小工具"] = "Create",
        ["旋转动力"] = "Create",
        ["自然生态"] = "Biomes O' Plenty",
        ["生态多样化"] = "Biomes O' Plenty",
        ["更多生物群系"] = "Biomes O' Plenty",
        ["龙之研究"] = "Ice and Fire",
        ["冰与火之歌"] = "Ice and Fire",
        ["神龙"] = "Ice and Fire",
        ["巫术"] = "Botania",
        ["自然魔法"] = "Botania",
        ["魔法门"] = "Blood Magic",
        ["血魔法"] = "Blood Magic",
        ["魔导书"] = "Ars Nouveau",
        ["法术书"] = "Ars Nouveau",
        ["双持"] = "Tinkers' Construct",
        ["匠魂工具"] = "Tinkers' Construct",
        ["造物"] = "Create",
        ["起源"] = "Origins",
        ["天赋起源"] = "Origins",
        ["史诗对决"] = "Epic Fight",
        ["动作战斗"] = "Epic Fight",
        ["格斗战斗"] = "Epic Fight",
        ["宝可梦"] = "Cobblemon",
        ["口袋妖怪"] = "Cobblemon",
        ["神奇宝贝"] = "Cobblemon",
        ["虫图鉴"] = "Cobblemon",

        // 服务器/管理类
        ["权限管理"] = "LuckPerms",
        ["经济系统"] = "EssentialsX",
        ["传送点"] = "EssentialsX",
        ["领地保护"] = "GriefPrevention",
        ["领地"] = "WorldGuard",
        ["世界编辑"] = "WorldEdit",
        ["地皮保护"] = "Lands",

        // ---- 本轮补充：进一步扩大覆盖面（社区常见叫法/别名/缩写/拼音简写），
        // 提高"无法使用中文搜索"这个高频抱怨的命中率。命名依据：Modrinth/CurseForge 上
        // 下载量靠前、中文玩家社区（NGA/贴吧/QQ群）里高频出现的中文俗名。 ----
        // 性能优化类（继续补）
        ["幻翼"] = "Phosphor",
        ["视觉效果优化"] = "Sodium",
        ["帧数优化"] = "Sodium",
        ["提升帧数"] = "Sodium",
        ["渲染优化"] = "Sodium",
        ["更好的光影"] = "Iris",
        ["光影核心"] = "Iris",
        ["区块加载优化"] = "C2ME",
        ["异步区块加载"] = "C2ME",
        ["物理内存优化"] = "FerriteCore",
        ["降低内存占用"] = "FerriteCore",
        ["快速启动"] = "Lazy DFU",
        ["模组加载优化"] = "Lazy DFU",

        // UI/HUD/QoL 类（继续补）
        ["血条条"] = "Damage Indicators",
        ["更好的背包"] = "Inventory Profiles Next",
        ["背包排序"] = "Inventory Sorter",
        ["自动整理背包"] = "Inventory Sorter",
        ["方块Tooltip"] = "Wthit",
        ["What Is This"] = "Wthit",
        ["地图指南针"] = "Xaero's Minimap",
        ["小地图指南针"] = "Xaero's Minimap",
        ["聊天记录"] = "Chat Heads",
        ["物品预览"] = "Just Enough Items",
        ["JEI合成表"] = "Just Enough Items",
        ["图鉴"] = "Just Enough Items",
        ["截图美化"] = "Not Enough Screenshots",
        ["自定义按键"] = "Controlling",
        ["按键绑定"] = "Controlling",

        // 加载器/开发/API 类（继续补）
        ["架构API"] = "Architectury API",
        ["跨加载器API"] = "Architectury API",
        ["谜题模组加载器"] = "Quilt",
        ["福吉"] = "Forge",
        ["福杰"] = "Forge",
        ["新福吉"] = "NeoForge",
        ["布料"] = "Fabric",
        ["布料API"] = "Fabric API",

        // 大型内容/科技/魔法/冒险类（继续补）
        ["工业2扩展"] = "IndustrialCraft 2",
        ["梅卡"] = "Mekanism",
        ["机械"] = "Mekanism",
        ["工业时代2"] = "Mekanism",
        ["流浪商人"] = "Wandering Trades",
        ["更多末影龙"] = "Ice and Fire",
        ["巨龙"] = "Ice and Fire",
        ["神话生物"] = "Ice and Fire",
        ["血魔"] = "Blood Magic",
        ["献祭魔法"] = "Blood Magic",
        ["咒法学徒"] = "Ars Nouveau",
        ["新咒语书"] = "Ars Nouveau",
        ["起源模组"] = "Origins",
        ["天赋种族"] = "Origins",
        ["物种起源"] = "Origins",
        ["战斗动作"] = "Epic Fight",
        ["魂系战斗"] = "Epic Fight",
        ["动作游戏战斗"] = "Epic Fight",
        ["宝可梦模组"] = "Cobblemon",
        ["精灵宝可梦"] = "Cobblemon",
        ["抓宝可梦"] = "Cobblemon",
        ["造物模组"] = "Create",
        ["机械动力模组"] = "Create",
        ["蒸汽朋克"] = "Create",
        ["匠魂2"] = "Tinkers' Construct",
        ["工具锻造"] = "Tinkers' Construct",
        ["自定义工具"] = "Tinkers' Construct",
        ["神秘时代6"] = "Thaumcraft",
        ["魔法研究"] = "Thaumcraft",
        ["植物魔法学"] = "Botania",
        ["自然魔药"] = "Botania",
        ["天顶星"] = "Astral Sorcery",
        ["占星魔法"] = "Astral Sorcery",
        ["末影接口2"] = "EnderIO",
        ["自动化管道"] = "EnderIO",
        ["能量传输"] = "EnderIO",
        ["应用能源"] = "Applied Energistics 2",
        ["无线存储"] = "Applied Energistics 2",
        ["数字化存储"] = "Refined Storage",
        ["存储抽屉"] = "Storage Drawers",
        ["方块抽屉"] = "Storage Drawers",
        ["更多食物"] = "Farmer's Delight",
        ["农夫的喜悦"] = "Farmer's Delight",
        ["料理模组"] = "Farmer's Delight",
        ["自然生态多样"] = "Biomes O' Plenty",
        ["生物群系扩展"] = "Biomes O' Plenty",
        ["暮色森林2"] = "The Twilight Forest",
        ["黄昏森林"] = "The Twilight Forest",
        ["地下城模组"] = "Roguelike Dungeons",
        ["随机地牢"] = "Roguelike Dungeons",
        ["更好的村庄"] = "Villager Names",
        ["村民改名"] = "Villager Names",
        ["精粹模组"] = "Quark",
        ["夸克模组"] = "Quark",
        ["小惊喜"] = "Quark",

        // 服务器/管理类（继续补）
        ["权限系统"] = "LuckPerms",
        ["权限插件"] = "LuckPerms",
        ["经济插件"] = "EssentialsX",
        ["基础插件"] = "EssentialsX",
        ["世界编辑器"] = "WorldEdit",
        ["防抓虫保护"] = "GriefPrevention",
        ["防破坏"] = "GriefPrevention",
        ["领地插件"] = "WorldGuard",
        ["地图保护"] = "WorldGuard",
    };

    /// <summary>
    /// 尝试把中文关键词翻译成对应的英文搜索词。命中返回英文名，未命中返回 null。
    /// 匹配规则：精确匹配优先；其次做"query 包含词典 key"或"词典 key 包含 query"的模糊匹配
    /// (处理"装个钠模组""钠"这类变体)，模糊匹配取命中中文名最长的一条，减少误匹配短词的概率。
    /// </summary>
    public static string? TryTranslate(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var q = query.Trim();

        if (Entries.TryGetValue(q, out var exact)) return exact;

        string? bestKey = null;
        foreach (var key in Entries.Keys)
        {
            if (q.Contains(key, StringComparison.Ordinal) || key.Contains(q, StringComparison.Ordinal))
            {
                if (bestKey == null || key.Length > bestKey.Length) bestKey = key;
            }
        }

        return bestKey != null ? Entries[bestKey] : null;
    }
}
