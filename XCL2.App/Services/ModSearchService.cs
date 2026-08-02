using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 下载中心「Mod」分类的聚合搜索：按用户选择的来源（综合/仅 Modrinth/仅 CurseForge）搜索，
/// 并把结果统一成 UnifiedModItem 供 UI 绑定。MC百科单独查询（见 SearchMcModAsync），因为它
/// 只能提供"中文名 + 百科页面链接"，不具备直接下载能力，混进统一下载结果会让用户误以为能一键装。
///
/// "综合"模式下 Modrinth 和 CurseForge 并发查询，任意一个来源失败不影响另一个来源的结果——
/// 例如用户没配置 CurseForge Key 时，综合模式应该照常展示 Modrinth 的结果，而不是整体报错。
/// </summary>
public class ModSearchService
{
    private readonly ModrinthService _modrinth;
    private readonly CurseForgeService _curseForge;
    private readonly McModService _mcMod = new();

    public ModSearchService(ModrinthService modrinth, CurseForgeService curseForge)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
    }

    /// <summary>
    /// 按来源搜索 Mod。返回统一结果列表 + 各来源是否搜索失败的说明（用于 UI 提示，
    /// 比如"CurseForge 未配置 Key，本次只展示 Modrinth 结果"），而不是直接抛异常打断整个搜索。
    /// </summary>
    /// <summary>
    /// pageIndex 从 0 开始，pageSize 是"每个来源各自的每页条数"。
    ///
    /// 分页语义说明（"综合"来源下的分页是"并列分页"而不是"全局游标分页"）：Modrinth/CurseForge
    /// 是两个完全独立、各自维护游标的搜索接口，没有办法把两边结果先合并排序再切出一个全局第 N 页——
    /// 那需要先把两边所有结果都拉回来在内存里排序，对"浏览热门"这种大结果集不现实。这里采用
    /// 跟视频参照的 CurseForge 官方客户端类似的思路：综合模式下，第 N 页 = Modrinth 第 N 页(pageSize条)
    /// + CurseForge 第 N 页(pageSize条)拼在一起，两边"同步翻页"，总页数取两个来源里页数较多的那个
    /// （较少的一侧翻到底后该页对应位置自然没有它的结果，不会报错，只是这次拼出来的页比 pageSize*2 少）。
    /// 仅单一来源(Modrinth-only/CurseForge-only)时是最简单直接的真分页，一页就是 API 返回的一页。
    /// </summary>
    public async Task<ModSearchOutcome> SearchAsync(ModSource source, string query, string? gameVersion,
        string? modLoader, int pageIndex = 0, int pageSize = 20, CancellationToken ct = default)
    {
        var outcome = new ModSearchOutcome();

        // 中文搜索支持：Modrinth/CurseForge 的搜索接口本身基本不认中文关键词(如直接搜"钠"
        // 大概率没有结果)，这里用移植自 PCL2 的 WikiEntry 全量中文名数据库 + 模糊搜索算法
        // (见 ChineseModSearchTranslator)，命中就把实际发给 Modrinth/CurseForge 的关键词
        // 换成对应的英文名，这样用户搜"钠"能直接搜到 Sodium，而不需要先去 MC百科查到英文名
        // 再回来手动重新输入一遍。查不到就照旧用原始关键词搜（不影响任何已有行为）。
        //
        // 两个平台分开翻译而不是共用一个关键词：Modrinth 和 CurseForge 上同一个 Mod 的 Slug
        // 经常不一样（例如某些 Mod 在两边的项目名拼写不同），各自查各自数据库里的 Slug 才准确，
        // 这也是 PCL2 原版的做法（CurseForgeAltSearchText 和 ModrinthAltSearchText 分开算）。
        var searchModrinth = source is ModSource.Combined or ModSource.Modrinth;
        var searchCurseForge = source is ModSource.Combined or ModSource.CurseForge;
        var offset = pageIndex * pageSize;

        var isChinese = ChineseModSearchTranslator.IsChineseQuery(query);
        string? modrinthQuery = query, curseForgeQuery = query;
        string? translatedForDisplay = null;

        if (isChinese)
        {
            if (searchModrinth)
            {
                var mrTranslated = ChineseModSearchTranslator.Translate(query, ModSource.Modrinth);
                if (mrTranslated.Keyword != null) { modrinthQuery = mrTranslated.Keyword; translatedForDisplay ??= mrTranslated.Keyword; }
            }
            if (searchCurseForge)
            {
                var crTranslated = ChineseModSearchTranslator.Translate(query, ModSource.CurseForge);
                if (crTranslated.Keyword != null) { curseForgeQuery = crTranslated.Keyword; translatedForDisplay ??= crTranslated.Keyword; }
            }
        }
        if (translatedForDisplay != null) outcome.TranslatedFrom = query;

        var modrinthTask = searchModrinth ? SearchModrinthSafe(modrinthQuery ?? query, gameVersion, modLoader, offset, pageSize, ct) : null;
        var curseForgeTask = searchCurseForge ? SearchCurseForgeSafe(curseForgeQuery ?? query, gameVersion, modLoader, offset, pageSize, ct) : null;

        // 用 Task.WhenAll 而不是逐个 await：虽然两个 Task 在上面赋值时已经开始并发执行，
        // 顺序 await 不会真的让第二个任务"等"第一个跑完，但写成 WhenAll 让并发意图在代码上
        // 一目了然，不需要读者靠"C# async 方法调用即执行"这个隐含知识才能确认这里是并发的。
        // 用 Task（非泛型）的 WhenAll 重载而不是把两个不同调用点的具名元组类型硬塞进同一个
        // 数组，避免匿名元组类型推断在某些编译器版本上出现意外的隐式转换问题。
        if (modrinthTask != null && curseForgeTask != null) await Task.WhenAll(modrinthTask, curseForgeTask);
        else if (modrinthTask != null) await modrinthTask;
        else if (curseForgeTask != null) await curseForgeTask;

        if (modrinthTask?.Result is { } mr)
        {
            if (mr.Error != null) outcome.Warnings.Add($"Modrinth 搜索失败：{mr.Error}");
            else outcome.Items.AddRange(mr.Items);
            if (mr.Notice != null) outcome.Warnings.Add(mr.Notice);
            outcome.ModrinthTotal = mr.Total;
        }

        if (curseForgeTask?.Result is { } cr)
        {
            if (cr.Error != null) outcome.Warnings.Add($"CurseForge 搜索失败：{cr.Error}");
            else outcome.Items.AddRange(cr.Items);
            if (cr.Notice != null) outcome.Warnings.Add(cr.Notice);
            outcome.CurseForgeTotal = cr.Total;
        }

        return outcome;
    }

    /// <summary>MC百科单独查询入口：只做中文名搜索辅助，不参与统一下载列表（见类注释）。</summary>
    public Task<List<McModSearchHit>> SearchMcModAsync(string query, CancellationToken ct = default)
        => _mcMod.SearchAsync(query, ct);

    /// <summary>
    /// 材质包/数据包/光影包的聚合搜索（Modrinth + CurseForge），跟 SearchAsync(Mod) 是同一个思路：
    /// 综合模式下两个来源并发查询，互不影响成败。之前"下载中心"这三个分类只查 Modrinth，
    /// CurseForge 的材质包/光影包/数据包完全搜不到——这里补上 CurseForge 一侧。
    /// </summary>
    /// <summary>材质包/数据包/光影包的聚合搜索，分页语义跟 SearchAsync(Mod) 完全一致（见那边的注释）。</summary>
    public async Task<ModSearchOutcome<UnifiedResourceItem>> SearchResourcesAsync(ModSource source,
        ModrinthResourceType type, string query, string? gameVersion, int pageIndex = 0, int pageSize = 20,
        CancellationToken ct = default, string? modLoader = null)
    {
        var outcome = new ModSearchOutcome<UnifiedResourceItem>();

        var searchModrinth = source is ModSource.Combined or ModSource.Modrinth;
        var searchCurseForge = source is ModSource.Combined or ModSource.CurseForge;
        var offset = pageIndex * pageSize;

        // 中文搜索翻译只对数据包生效（跟 PCL2 一致）：材质包/光影包的名称本身大多没有对应的
        // MC 百科中文名条目（WikiEntry 数据库主要收录的是 Mod），贸然套用会经常翻译不出结果
        // 或者翻译错方向；数据包跟 Mod 共用同一套 Slug/中文名体系，可以安全复用。
        var effectiveModrinthQuery = query;
        var effectiveCurseForgeQuery = query;
        string? translatedForDisplay = null;
        if (type == ModrinthResourceType.DataPack && ChineseModSearchTranslator.IsChineseQuery(query))
        {
            if (searchModrinth)
            {
                var mrTranslated = ChineseModSearchTranslator.Translate(query, ModSource.Modrinth);
                if (mrTranslated.Keyword != null) { effectiveModrinthQuery = mrTranslated.Keyword; translatedForDisplay ??= mrTranslated.Keyword; }
            }
            if (searchCurseForge)
            {
                var crTranslated = ChineseModSearchTranslator.Translate(query, ModSource.CurseForge);
                if (crTranslated.Keyword != null) { effectiveCurseForgeQuery = crTranslated.Keyword; translatedForDisplay ??= crTranslated.Keyword; }
            }
        }
        if (translatedForDisplay != null) outcome.TranslatedFrom = translatedForDisplay;

        var modrinthTask = searchModrinth ? SearchModrinthResourceSafe(type, effectiveModrinthQuery, gameVersion, offset, pageSize, ct, modLoader) : null;
        var curseForgeTask = searchCurseForge ? SearchCurseForgeResourceSafe(type, effectiveCurseForgeQuery, gameVersion, offset, pageSize, ct, modLoader) : null;

        if (modrinthTask != null && curseForgeTask != null) await Task.WhenAll(modrinthTask, curseForgeTask);
        else if (modrinthTask != null) await modrinthTask;
        else if (curseForgeTask != null) await curseForgeTask;

        if (modrinthTask?.Result is { } mr)
        {
            if (mr.Error != null) outcome.Warnings.Add($"Modrinth 搜索失败：{mr.Error}");
            else outcome.Items.AddRange(mr.Items);
            if (mr.Notice != null) outcome.Warnings.Add(mr.Notice);
            outcome.ModrinthTotal = mr.Total;
        }

        if (curseForgeTask?.Result is { } cr)
        {
            if (cr.Error != null) outcome.Warnings.Add($"CurseForge 搜索失败：{cr.Error}");
            else outcome.Items.AddRange(cr.Items);
            if (cr.Notice != null) outcome.Warnings.Add(cr.Notice);
            outcome.CurseForgeTotal = cr.Total;
        }

        return outcome;
    }

    private async Task<(List<UnifiedResourceItem> Items, string? Error, string? Notice, int Total)> SearchModrinthResourceSafe(
        ModrinthResourceType type, string query, string? gameVersion, int offset, int pageSize, CancellationToken ct, string? modLoader = null)
    {
        try
        {
            var result = await _modrinth.SearchAsync(type, query, gameVersion, offset, pageSize, ct, modLoader);
            var items = result.Hits.Select(h => new UnifiedResourceItem
            {
                Source = ModSource.Modrinth,
                // 中文优先展示："中文名 (English Title)"；查不到中文对照则保持英文原样，
                // 见 ModDisplayNameResolver 注释（只做精确 Slug 匹配，不做模糊猜测）。
                Title = ModDisplayNameResolver.Resolve(ModSource.Modrinth, h.Slug, h.Title),
                Description = h.Description,
                Author = h.Author,
                IconUrl = h.IconUrl,
                Downloads = h.Downloads,
                SourceId = h.ProjectId,
                RawItem = h
            }).ToList();
            return (items, null, null, result.TotalHits);
        }
        catch (Exception ex)
        {
            return (new List<UnifiedResourceItem>(), ex.Message, null, 0);
        }
    }

    /// <summary>数据包在 CurseForge 上用独立 classId(6945) 搜索，跟 Modrinth "mod + datapack 分类"的
    /// 变通方式不同，不需要额外过滤参数。</summary>
    private async Task<(List<UnifiedResourceItem> Items, string? Error, string? Notice, int Total)> SearchCurseForgeResourceSafe(
        ModrinthResourceType type, string query, string? gameVersion, int offset, int pageSize, CancellationToken ct, string? modLoader = null)
    {
        // Mod 在 CurseForge 侧走的是独立的 SearchModsAsync/classId=6 通道(见 SearchCurseForgeSafe)，
        // 跟这里 SearchResourcesAsync 用的资源型 classId 不是同一套；这个统一资源面板遇到 Mod 类型时
        // 直接跳过 CurseForge（不算错误，只是这个来源对这个类型没有对应实现），只展示 Modrinth 结果。
        if (type == ModrinthResourceType.Mod)
            return (new List<UnifiedResourceItem>(), null, null, 0);

        try
        {
            var kind = type switch
            {
                ModrinthResourceType.ResourcePack => CurseForgeResourceKind.ResourcePack,
                ModrinthResourceType.Shader => CurseForgeResourceKind.Shader,
                ModrinthResourceType.DataPack => CurseForgeResourceKind.DataPack,
                ModrinthResourceType.Plugin => CurseForgeResourceKind.Plugin,
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
            var result = await _curseForge.SearchResourcesAsync(kind, query, gameVersion, offset, pageSize, ct);
            var items = result.Data.Select(m => new UnifiedResourceItem
            {
                Source = ModSource.CurseForge,
                // 注意：这里没有走 ModDisplayNameResolver——CurseForge /mods/search 响应体不带
                // slug 字段，只能查到 Id/Name，没有能安全做精确匹配的键，所以保持英文标题，
                // 不用名称模糊匹配代替（避免把中文名安到不相关的 Mod 上），详见该类的注释。
                Title = m.Name,
                Description = m.Summary,
                Author = m.AuthorsDisplay,
                IconUrl = m.Logo?.ThumbnailUrl,
                Downloads = m.DownloadCount,
                SourceId = m.Id.ToString(),
                RawItem = m
            }).ToList();
            return (items, null, null, result.Pagination?.TotalCount ?? items.Count);
        }
        catch (CurseForgeKeyMissingException)
        {
            // 同 SearchCurseForgeSafe（Mod 版）的修复：不再完全静默，返回一句提示信息，
            // 避免"综合"模式在没配 key 时被误以为是"只显示 Modrinth"的 bug。
            return (new List<UnifiedResourceItem>(), null, "未配置 CurseForge API Key，综合搜索本次只显示 Modrinth 结果（去「设置」页粘贴 key 即可同时显示 CurseForge）。", 0);
        }
        catch (Exception ex)
        {
            return (new List<UnifiedResourceItem>(), ex.Message, null, 0);
        }
    }

    private async Task<(List<UnifiedModItem> Items, string? Error, string? Notice, int Total)> SearchModrinthSafe(
        string query, string? gameVersion, string? modLoader, int offset, int pageSize, CancellationToken ct)
    {
        try
        {
            var result = await _modrinth.SearchAsync(ModrinthResourceType.Mod, query, gameVersion,
                offset, pageSize, ct, modLoader);
            var items = result.Hits.Select(h => new UnifiedModItem
            {
                Source = ModSource.Modrinth,
                // 同上：中文优先展示，见 ModDisplayNameResolver 注释。
                Title = ModDisplayNameResolver.Resolve(ModSource.Modrinth, h.Slug, h.Title),
                Description = h.Description,
                Author = h.Author,
                IconUrl = h.IconUrl,
                Downloads = h.Downloads,
                SourceId = h.ProjectId,
                RawItem = h
            }).ToList();
            return (items, null, null, result.TotalHits);
        }
        catch (Exception ex)
        {
            return (new List<UnifiedModItem>(), ex.Message, null, 0);
        }
    }

    private async Task<(List<UnifiedModItem> Items, string? Error, string? Notice, int Total)> SearchCurseForgeSafe(
        string query, string? gameVersion, string? modLoader, int offset, int pageSize, CancellationToken ct)
    {
        try
        {
            var result = await _curseForge.SearchModsAsync(query, gameVersion, modLoader, offset, pageSize, ct);
            var items = result.Data.Select(m => new UnifiedModItem
            {
                Source = ModSource.CurseForge,
                // 同上（SearchCurseForgeResourceSafe 里的注释）：CurseForge 搜索结果没有 slug，
                // 保持英文标题，不做名称模糊匹配代替。
                Title = m.Name,
                Description = m.Summary,
                Author = m.AuthorsDisplay,
                IconUrl = m.Logo?.ThumbnailUrl,
                Downloads = m.DownloadCount,
                SourceId = m.Id.ToString(),
                RawItem = m
            }).ToList();
            return (items, null, null, result.Pagination?.TotalCount ?? items.Count);
        }
        catch (CurseForgeKeyMissingException)
        {
            // 之前这里完全静默返回空列表，导致"综合"模式在没配置 CurseForge Key 时
            // 会不动声色地退化成"只有 Modrinth 结果"，用户很容易误以为综合搜索本身
            // 有 bug（默认只显示 Modrinth），实际是缺 key 导致的静默降级。
            // 现在改成带一句轻量提示（不算 Error 级别，不会被当成"搜索失败"报错弹窗），
            // 只在用户主动点"手动刷新"时才会通过 Warnings 展示出来，自动搜索时不打扰。
            return (new List<UnifiedModItem>(), null, "未配置 CurseForge API Key，综合搜索本次只显示 Modrinth 结果（去「设置」页粘贴 key 即可同时显示 CurseForge）。", 0);
        }
        catch (Exception ex)
        {
            return (new List<UnifiedModItem>(), ex.Message, null, 0);
        }
    }
}

/// <summary>
/// 泛型化：原来只服务于 Mod 搜索(固定装 UnifiedModItem)，现在材质包/光影包/数据包的聚合搜索
/// (SearchResourcesAsync)也要用同一套"多来源结果 + 警告列表"的容器，所以加一个类型参数。
/// ModSearchOutcome（不带类型参数）保留作为 UnifiedModItem 场景的别名，SearchAsync(Mod) 的调用方
/// 不需要跟着改。
/// </summary>
public class ModSearchOutcome<T>
{
    public List<T> Items { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>非空表示这次搜索命中了中文名词典，实际搜索关键词已从这个原始中文词换成了英文名
    /// (具体英文词在 Items 结果里能看到)。UI 可以用这个字段提示用户"已按 XX 搜索"，避免用户
    /// 看到结果里全是英文标题却不知道为什么中文关键词能搜到东西。</summary>
    public string? TranslatedFrom { get; set; }

    /// <summary>Modrinth 这一侧这次查询命中的总条数（不是这一页的条数），没有查询这个来源时为 0。
    /// 用于分页条计算"翻页语义说明"（见 SearchAsync 类注释）——综合模式下总页数取两个来源里
    /// 页数较多的那个：PageCount = max(ceil(ModrinthTotal/pageSize), ceil(CurseForgeTotal/pageSize))。</summary>
    public int ModrinthTotal { get; set; }

    /// <summary>CurseForge 这一侧这次查询命中的总条数，含义同 ModrinthTotal。</summary>
    public int CurseForgeTotal { get; set; }
}

public class ModSearchOutcome : ModSearchOutcome<UnifiedModItem>
{
}
