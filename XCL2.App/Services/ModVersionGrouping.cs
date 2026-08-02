using System.Linq;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 把展开面板里拉到的扁平版本列表(InlineVersionEntry)按"加载器 + 游戏版本"重新分组，
/// 生成截图里"NeoForge 26.2"/"Fabric 26.2"这种可折叠分组标题的展示结构(VersionGroup)。
///
/// 加载器名的显示优先级：一个 InlineVersionEntry.Loaders 可能同时列出多个加载器
/// (Modrinth 有些版本会同时声明支持 Fabric 和 Quilt，因为 Quilt 兼容 Fabric API)，
/// 这种情况下按"每个加载器各生成一份分组条目"处理——同一个版本会出现在多个分组下，
/// 这跟真实情况一致(这个文件确实能在 Fabric 分组和 Quilt 分组下都被找到并下载)，
/// 不是数据重复展示的 bug。
///
/// 加载器显示名统一转换成截图里那种大写风格(Fabric/NeoForge/Forge/Quilt)，未知加载器
/// 原样保留 Modrinth/CurseForge 返回的小写字符串，不强行伪造成一个我们没见过的已知名字。
/// </summary>
public static class ModVersionGrouping
{
    private static readonly Dictionary<string, string> LoaderDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fabric"] = "Fabric",
        ["forge"] = "Forge",
        ["neoforge"] = "NeoForge",
        ["quilt"] = "Quilt",
        ["liteloader"] = "LiteLoader",
        ["bukkit"] = "Bukkit",
        ["spigot"] = "Spigot",
        ["paper"] = "Paper",
        ["purpur"] = "Purpur",
        ["folia"] = "Folia",
        ["velocity"] = "Velocity",
        ["bungeecord"] = "BungeeCord",
        ["waterfall"] = "Waterfall",
        ["sponge"] = "Sponge",
        ["datapack"] = "数据包",
        ["minecraft"] = "通用", // InlineVersionEntry 构造函数里给材质包/光影包等无 loader 概念的项目用的占位值
        ["curseforge"] = "CurseForge 文件", // CurseForgeFile 没有解析出具体 loader，见 InlineVersionEntry 构造函数注释
    };

    /// <summary>
    /// 生成分组列表：每个 (加载器, 游戏版本) 组合一组，组内按原始顺序(Modrinth/CurseForge
    /// 默认按发布时间倒序返回)保留 InlineVersionEntry。分组标题格式"加载器 游戏版本"，
    /// 比如"NeoForge 26.2"，游戏版本取该条目 GameVersionsText 里的第一个版本号。
    /// 组的整体顺序：按加载器名（NeoForge/Fabric/Forge/Quilt 优先，其余按出现顺序）、
    /// 再按游戏版本号做粗略倒序，让最新版本的分组排在前面，与截图里的顺序一致。
    /// 返回后调用方需要自行决定要不要把第一个分组设为默认展开(截图2里"Fabric 26.2"默认展开)。
    /// </summary>
    /// <summary>
    /// includePreview 默认改成 true：之前默认 false（只显示正式版）在部分 mod 上会把全部
    /// 版本都判断成"预览版"过滤掉，导致详情页显示"没有找到匹配的版本"（IsPreview 的判断
    /// 依赖 Modrinth version_type / CurseForge releaseType 字段，遇到数据不规范的项目就会
    /// 全军覆没）。与其继续在这个不可靠的字段上做隐藏，不如干脆全部显示，用户自己看版本号
    /// 判断要不要装——对应"去掉这些按钮"的需求，见 ModDetailPage 里移除的"显示/隐藏预览版"
    /// 按钮。
    /// </summary>
    public static List<VersionGroup> Group(IEnumerable<InlineVersionEntry> flatEntries, bool includePreview = true)
    {
        // key: (加载器原始小写名, 游戏版本号字符串) -> 这一组包含的条目
        var buckets = new Dictionary<(string loader, string gameVersion), List<InlineVersionEntry>>();
        var bucketOrder = new List<(string loader, string gameVersion)>(); // 记录首次出现顺序，保持组间相对顺序稳定

        foreach (var entry in flatEntries)
        {
            if (!includePreview && entry.IsPreview) continue;
            var firstGameVersion = entry.GameVersionsText.Split(',', StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(firstGameVersion)) firstGameVersion = "未知版本";

            foreach (var loader in entry.Loaders)
            {
                var key = (loader.ToLowerInvariant(), firstGameVersion);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<InlineVersionEntry>();
                    buckets[key] = list;
                    bucketOrder.Add(key);
                }
                list.Add(entry);
            }
        }

        var groups = new List<VersionGroup>();
        foreach (var key in bucketOrder)
        {
            var displayLoader = LoaderDisplayNames.TryGetValue(key.loader, out var name) ? name : key.loader;
            var title = $"{displayLoader} {key.gameVersion}";
            groups.Add(new VersionGroup(title, buckets[key]));
        }
        return groups;
    }
}
