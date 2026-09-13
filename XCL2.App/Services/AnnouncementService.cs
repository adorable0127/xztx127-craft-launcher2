using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 启动公告：跟"首次启动协议/新手引导"是两回事——那一套是"只在真正首次启动时走一遍"，
/// 这里是"只要有新公告、用户还没点确定，每次打开启动器(包括更新后重新打开)都继续提醒"，
/// 语义更接近很多软件更新后弹出的"更新说明"/"重要通知"。
///
/// 设计：
/// - 每条公告一个稳定不变的 Id，写在这个类里作为源码常量，不走配置文件/远程下发——公告
///   内容跟随启动器版本一起发布，不需要额外的服务端。
/// - 用户点"确定"后，这条公告的 Id 被写进 AppConfig.SeenAnnouncementIds，之后不再弹出；
///   只要没点，不管启动器重启多少次、有没有更新，都会继续弹，直到点了为止。
/// - 以后要加新公告，只需要在 <see cref="All"/> 里追加一条新的、Id 从未用过的记录——
///   旧公告的已读状态不受影响，新公告会单独出现，不会把旧的"已确定"状态一起冲掉。
/// </summary>
public static class AnnouncementService
{
    public sealed record Announcement(string Id, string Title, string Content);

    /// <summary>当前生效的全部公告，按顺序展示。</summary>
    public static readonly IReadOnlyList<Announcement> All = new[]
    {
        new Announcement(
            Id: "2026-09-openrouter-switch",
            Title: "AI 助手：模型来源变更",
            Content:
                "当前因为 Open Code 平台的模型出现异常（抽风），AI 助手已经换成 OpenRouter 提供的模型。\n" +
                "如果某个模型带有“可能出现问题”的标注，请不要使用它；另外，OpenRouter 上的英伟达（NVIDIA）" +
                "模型有它自己的一套使用政策，请务必仔细阅读模型选择界面上的黄色字体说明。"),
        new Announcement(
            Id: "2026-09-mc-1164-1165-offline-multiplayer-bug",
            Title: "已知问题：1.16.4 / 1.16.5 离线账户无法进入多人游戏",
            Content:
                "由于游戏本身存在 Bug，使用离线账户启动 1.16.4 或 1.16.5 版本时，会出现无法进入多人游戏和 " +
                "Realms 的问题。这是游戏自身的问题，1.17 及以后版本、或 1.16.4 以前的版本都不受影响。\n" +
                "如果你也遇到“无法使用多人游戏”的情况，请不要为此提交 Bug 报告——启动器作者本人也在想办法解决这个问题。"),
    };

    /// <summary>当前配置下还没被用户点"确定"确认过的公告，按 <see cref="All"/> 顺序返回。
    /// 返回空集合表示没有需要弹出的公告。</summary>
    public static IReadOnlyList<Announcement> GetPending(AppConfig cfg)
    {
        var seen = cfg.SeenAnnouncementIds;
        var pending = new List<Announcement>();
        foreach (var a in All)
        {
            if (!seen.Contains(a.Id))
                pending.Add(a);
        }
        return pending;
    }

    /// <summary>把给定公告标记为"已确定"，写入 cfg（调用方负责随后 ConfigService.Save()）。</summary>
    public static void MarkSeen(AppConfig cfg, IEnumerable<Announcement> announcements)
    {
        foreach (var a in announcements)
        {
            if (!cfg.SeenAnnouncementIds.Contains(a.Id))
                cfg.SeenAnnouncementIds.Add(a.Id);
        }
    }
}
