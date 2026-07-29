using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 访客模式：
/// - 开启后，启动器不使用 accounts.json 里保存的任何账户，而是每次运行时在内存里生成一个
///   临时离线账户(IsGuest=true)。这个账户只存在于本次进程运行期间——不调用
///   ConfigService.AddOrUpdateAccount/SaveAccounts，从不落盘，进程退出后不留任何痕迹。
/// - 关闭启动器时，会清理"本次会话新产生"的游戏日志/启动器下载缓存文件，具体是：
///   1) xcl2/logs 下这次运行开始之后新增/修改的文件（游戏控制台输出日志、崩溃报告缓存等）。
///   2) xcl2/downloads-temp 下的所有内容（下载过程中的临时/中间文件，正常情况下下载完成后
///      本来就会被移走或删除，这里只是兜底清理残留）。
///   明确不清理的范围：已安装的游戏本体、版本文件夹、mods、资源包、存档、Java 运行时、
///   已下载的 authlib-injector.jar 等——这些是"游戏数据"而不是"本次会话的痕迹"，
///   访客模式的诉求是"不留下这次登录/操作的痕迹"，不是"每次都清空整个启动器"。
/// </summary>
public class GuestModeService
{
    /// <summary>本次进程启动的时间，用于区分"这次会话新产生的日志"和"之前就存在的日志"。</summary>
    private readonly DateTime _sessionStartUtc = DateTime.UtcNow;

    /// <summary>生成一个仅本次会话有效的临时离线账户，用户名固定为"访客"（不与真实账户混淆），
    /// 每次调用都会生成一个新的随机后缀，避免多次开关访客模式时 UUID 冲突导致的皮肤/存档误关联。</summary>
    public Account CreateGuestAccount()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var username = $"访客{suffix}";
        var uuid = OfflineAuthService.GenerateOfflineUuid(username);
        return new Account
        {
            Type = AccountType.Offline,
            Username = username,
            Uuid = uuid,
            IsSelected = true,
            IsGuest = true
        };
    }

    /// <summary>
    /// 应用退出前调用：清理本次会话产生的日志/临时下载文件。所有清理动作都用 try/catch 单独
    /// 包裹，某个文件清理失败(比如仍被占用)不应该阻止应用正常退出或影响其余文件的清理。
    /// </summary>
    public void CleanupSessionArtifacts()
    {
        CleanupNewLogFiles();
        CleanupTempDownloads();
    }

    private void CleanupNewLogFiles()
    {
        var logsDir = Path.Combine(App.DataDir, "logs");
        if (!Directory.Exists(logsDir)) return;

        foreach (var file in Directory.EnumerateFiles(logsDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                // 只清理本次会话开始之后才被创建或修改过的文件，不动会话开始前就已经存在的
                // 历史日志（那些不是"这次"产生的痕迹，用户可能还需要留着排查之前的问题）。
                var lastWrite = File.GetLastWriteTimeUtc(file);
                var created = File.GetCreationTimeUtc(file);
                if (lastWrite >= _sessionStartUtc || created >= _sessionStartUtc)
                    File.Delete(file);
            }
            catch { /* 单个文件清理失败(可能仍被占用)不影响其余文件，也不阻止应用退出 */ }
        }
    }

    private void CleanupTempDownloads()
    {
        var tempDir = Path.Combine(App.DataDir, "downloads-temp");
        if (!Directory.Exists(tempDir)) return;

        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* 清理失败不阻止应用退出，残留的临时文件下次启动时会被覆盖/复用 */ }
    }
}
