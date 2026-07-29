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
    public async Task<ModSearchOutcome> SearchAsync(ModSource source, string query, string? gameVersion,
        string? modLoader, CancellationToken ct = default)
    {
        var outcome = new ModSearchOutcome();

        // 中文搜索支持：Modrinth/CurseForge 的搜索接口本身基本不认中文关键词(如直接搜"钠"
        // 大概率没有结果)，这里先查内置词典，命中就把实际发给 Modrinth/CurseForge 的关键词
        // 换成对应的英文名，这样用户搜"钠"能直接搜到 Sodium，而不需要先去 MC百科查到英文名
        // 再回来手动重新输入一遍。查不到词典就照旧用原始关键词搜（不影响任何已有行为）。
        var translated = ModNameDictionary.TryTranslate(query);
        var effectiveQuery = translated ?? query;
        if (translated != null) outcome.TranslatedFrom = query;

        var searchModrinth = source is ModSource.Combined or ModSource.Modrinth;
        var searchCurseForge = source is ModSource.Combined or ModSource.CurseForge;

        var modrinthTask = searchModrinth ? SearchModrinthSafe(effectiveQuery, gameVersion, modLoader, ct) : null;
        var curseForgeTask = searchCurseForge ? SearchCurseForgeSafe(effectiveQuery, gameVersion, modLoader, ct) : null;

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
        }

        if (curseForgeTask?.Result is { } cr)
        {
            if (cr.Error != null) outcome.Warnings.Add($"CurseForge 搜索失败：{cr.Error}");
            else outcome.Items.AddRange(cr.Items);
            if (cr.Notice != null) outcome.Warnings.Add(cr.Notice);
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
    public async Task<ModSearchOutcome<UnifiedResourceItem>> SearchResourcesAsync(ModSource source,
        ModrinthResourceType type, string query, string? gameVersion, CancellationToken ct = default)
    {
        var outcome = new ModSearchOutcome<UnifiedResourceItem>();

        var searchModrinth = source is ModSource.Combined or ModSource.Modrinth;
        var searchCurseForge = source is ModSource.Combined or ModSource.CurseForge;

        var modrinthTask = searchModrinth ? SearchModrinthResourceSafe(type, query, gameVersion, ct) : null;
        var curseForgeTask = searchCurseForge ? SearchCurseForgeResourceSafe(type, query, gameVersion, ct) : null;

        if (modrinthTask != null && curseForgeTask != null) await Task.WhenAll(modrinthTask, curseForgeTask);
        else if (modrinthTask != null) await modrinthTask;
        else if (curseForgeTask != null) await curseForgeTask;

        if (modrinthTask?.Result is { } mr)
        {
            if (mr.Error != null) outcome.Warnings.Add($"Modrinth 搜索失败：{mr.Error}");
            else outcome.Items.AddRange(mr.Items);
            if (mr.Notice != null) outcome.Warnings.Add(mr.Notice);
        }

        if (curseForgeTask?.Result is { } cr)
        {
            if (cr.Error != null) outcome.Warnings.Add($"CurseForge 搜索失败：{cr.Error}");
            else outcome.Items.AddRange(cr.Items);
            if (cr.Notice != null) outcome.Warnings.Add(cr.Notice);
        }

        return outcome;
    }

    private async Task<(List<UnifiedResourceItem> Items, string? Error, string? Notice)> SearchModrinthResourceSafe(
        ModrinthResourceType type, string query, string? gameVersion, CancellationToken ct)
    {
        try
        {
            var result = await _modrinth.SearchAsync(type, query, gameVersion, ct: ct);
            var items = result.Hits.Select(h => new UnifiedResourceItem
            {
                Source = ModSource.Modrinth,
                Title = h.Title,
                Description = h.Description,
                Author = h.Author,
                IconUrl = h.IconUrl,
                Downloads = h.Downloads,
                SourceId = h.ProjectId,
                RawItem = h
            }).ToList();
            return (items, null, null);
        }
        catch (Exception ex)
        {
            return (new List<UnifiedResourceItem>(), ex.Message, null);
        }
    }

    /// <summary>数据包在 CurseForge 上用独立 classId(6945) 搜索，跟 Modrinth "mod + datapack 分类"的
    /// 变通方式不同，不需要额外过滤参数。</summary>
    private async Task<(List<UnifiedResourceItem> Items, string? Error, string? Notice)> SearchCurseForgeResourceSafe(
        ModrinthResourceType type, string query, string? gameVersion, CancellationToken ct)
    {
        try
        {
            var kind = type switch
            {
                ModrinthResourceType.ResourcePack => CurseForgeResourceKind.ResourcePack,
                ModrinthResourceType.Shader => CurseForgeResourceKind.Shader,
                ModrinthResourceType.DataPack => CurseForgeResourceKind.DataPack,
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
            var result = await _curseForge.SearchResourcesAsync(kind, query, gameVersion, ct: ct);
            var items = result.Data.Select(m => new UnifiedResourceItem
            {
                Source = ModSource.CurseForge,
                Title = m.Name,
                Description = m.Summary,
                Author = m.AuthorsDisplay,
                IconUrl = m.Logo?.ThumbnailUrl,
                Downloads = m.DownloadCount,
                SourceId = m.Id.ToString(),
                RawItem = m
            }).ToList();
            return (items, null, null);
        }
        catch (CurseForgeKeyMissingException)
        {
            // 同 SearchCurseForgeSafe（Mod 版）的修复：不再完全静默，返回一句提示信息，
            // 避免"综合"模式在没配 key 时被误以为是"只显示 Modrinth"的 bug。
            return (new List<UnifiedResourceItem>(), null, "未配置 CurseForge API Key，综合搜索本次只显示 Modrinth 结果（去「设置」页粘贴 key 即可同时显示 CurseForge）。");
        }
        catch (Exception ex)
        {
            return (new List<UnifiedResourceItem>(), ex.Message, null);
        }
    }

    private async Task<(List<UnifiedModItem> Items, string? Error, string? Notice)> SearchModrinthSafe(
        string query, string? gameVersion, string? modLoader, CancellationToken ct)
    {
        try
        {
            var result = await _modrinth.SearchAsync(ModrinthResourceType.Mod, query, gameVersion,
                ct: ct, modLoader: modLoader);
            var items = result.Hits.Select(h => new UnifiedModItem
            {
                Source = ModSource.Modrinth,
                Title = h.Title,
                Description = h.Description,
                Author = h.Author,
                IconUrl = h.IconUrl,
                Downloads = h.Downloads,
                SourceId = h.ProjectId,
                RawItem = h
            }).ToList();
            return (items, null, null);
        }
        catch (Exception ex)
        {
            return (new List<UnifiedModItem>(), ex.Message, null);
        }
    }

    private async Task<(List<UnifiedModItem> Items, string? Error, string? Notice)> SearchCurseForgeSafe(
        string query, string? gameVersion, string? modLoader, CancellationToken ct)
    {
        try
        {
            var result = await _curseForge.SearchModsAsync(query, gameVersion, modLoader, ct: ct);
            var items = result.Data.Select(m => new UnifiedModItem
            {
                Source = ModSource.CurseForge,
                Title = m.Name,
                Description = m.Summary,
                Author = m.AuthorsDisplay,
                IconUrl = m.Logo?.ThumbnailUrl,
                Downloads = m.DownloadCount,
                SourceId = m.Id.ToString(),
                RawItem = m
            }).ToList();
            return (items, null, null);
        }
        catch (CurseForgeKeyMissingException)
        {
            // 之前这里完全静默返回空列表，导致"综合"模式在没配置 CurseForge Key 时
            // 会不动声色地退化成"只有 Modrinth 结果"，用户很容易误以为综合搜索本身
            // 有 bug（默认只显示 Modrinth），实际是缺 key 导致的静默降级。
            // 现在改成带一句轻量提示（不算 Error 级别，不会被当成"搜索失败"报错弹窗），
            // 只在用户主动点"手动刷新"时才会通过 Warnings 展示出来，自动搜索时不打扰。
            return (new List<UnifiedModItem>(), null, "未配置 CurseForge API Key，综合搜索本次只显示 Modrinth 结果（去「设置」页粘贴 key 即可同时显示 CurseForge）。");
        }
        catch (Exception ex)
        {
            return (new List<UnifiedModItem>(), ex.Message, null);
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
}

public class ModSearchOutcome : ModSearchOutcome<UnifiedModItem>
{
}
