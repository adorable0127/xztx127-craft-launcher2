using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 账户头像的"在线皮肤缓存"。
///
/// 需求：皮肤站(AuthServer)账户、正版(Microsoft)账户，以及"设置过皮肤"的离线账户
/// (SkinType != None，即用户主动选过史蒂夫/艾利克斯/自定义皮肤，不是全新的空白离线账户)，
/// 「账户」页头像都应该去查一次服务器当前公开皮肤，而不是只看本地一次性绑定的
/// CustomSkinPath、更不该直接停在默认头像上——服务器端皮肤随时可能被玩家自己换掉，
/// AccountAvatarConverter 之前完全没有这条查询链路，只能看本地文件。
///
/// 做法：在「账户」页每次刷新账户列表时，为符合条件的账户各发起一次后台查询/下载，
/// 命中后把皮肤 PNG 存到 xcl2/skins/onlineAvatarCache/&lt;accountId&gt;.png，随后回调
/// 通知调用方刷新界面。AccountAvatarConverter 读取头像时会优先看这份缓存文件，比
/// CustomSkinPath 更新，查不到/查询失败都静默放弃，退回原有的"本地皮肤 → 默认头像"
/// 优先级链条，不会打断账户列表的正常显示，也不会弹出任何错误提示。
/// </summary>
public static class AccountAvatarCacheService
{
    public static string CacheDir => Path.Combine(App.DataDir, "skins", "onlineAvatarCache");

    public static string GetCachePath(Account account) => Path.Combine(CacheDir, account.Id + ".png");

    /// <summary>是否属于"应该去 API 查一次当前皮肤"的范围。</summary>
    public static bool ShouldFetch(Account account) => account.Type switch
    {
        AccountType.Microsoft => true,
        AccountType.AuthServer => !string.IsNullOrWhiteSpace(account.AuthServerApiRoot) && !string.IsNullOrWhiteSpace(account.Uuid),
        // 离线账户没有账户体系认证，只能按用户名去正版接口"猜"一次——猜中说明这确实是某个
        // 正版玩家常用的用户名，猜不中（绝大多数情况）就静默放弃，不影响本地皮肤/默认头像显示。
        // 只对"已经主动设置过皮肤"的离线账户做这件事，全新、什么都没选过的离线账户不打扰。
        AccountType.Offline => account.SkinType != OfflineSkinType.None,
        _ => false
    };

    /// <summary>
    /// 后台发起一次皮肤查询 + 下载；成功则把 PNG 写入缓存文件并调用 onUpdated（调用方负责
    /// 切回 UI 线程刷新头像绑定）。查询失败、下载失败、账户在服务器上查不到正版档案等任何
    /// 一步出错，都原样吞掉异常、不抛出、不提示——如果解析/下载到的皮肤文件本身有问题
    /// （尺寸不对/损坏），交给 AccountAvatarConverter 已有的"皮肤文件读取失败退回默认头像"
    /// 兜底逻辑处理，这里不重复做皮肤合法性校验（同一套裁剪/合成逻辑见
    /// <see cref="SkinAvatarRenderService"/>，「百宝箱」-「皮肤头像生成器」用的也是它）。
    /// </summary>
    public static async Task RefreshAsync(Account account, Action onUpdated)
    {
        if (!ShouldFetch(account)) return;

        try
        {
            using var service = new OfficialSkinFetchService();
            OfficialSkinFetchService.OfficialSkinInfo info = account.Type switch
            {
                AccountType.Microsoft when !string.IsNullOrWhiteSpace(account.Uuid)
                    => await service.LookupByUuidAsync(account.Uuid, account.Username),
                AccountType.Microsoft
                    => await service.LookupAsync(account.Username),
                AccountType.AuthServer
                    => await service.LookupByUuidFromYggdrasilAsync(account.AuthServerApiRoot!, account.Uuid, account.Username),
                _ => await service.LookupAsync(account.Username)
            };

            var bytes = await service.DownloadSkinBytesAsync(info);
            if (bytes.Length == 0) return;

            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(GetCachePath(account), bytes);
            onUpdated();
        }
        catch
        {
            // 静默失败：保持原有的 本地皮肤 / 默认头像 优先级链条不变，不打断账户列表显示。
        }
    }
}
