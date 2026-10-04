using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>加载器安装与窗口解耦；同一游戏目录的安装依次执行，避免共享原版或库文件冲突。</summary>
public static class LoaderInstallQueue
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FolderLocks = new(StringComparer.OrdinalIgnoreCase);

    public static DownloadQueueItem Start(string name, string minecraftDir, AppConfig config,
        Func<ClientLoaderInstallService, DownloadService, IProgress<ProgressInfo>, CancellationToken, Task<string>> install,
        Action<string>? completed = null, string? parentVersionId = null)
    {
        // 固定这次任务的下载设置；设置页之后的修改不影响已经登记的任务。
        var snapshot = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config)) ?? config;
        var folder = Path.GetFullPath(minecraftDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gate = FolderLocks.GetOrAdd(folder, _ => new SemaphoreSlim(1, 1));
        return DownloadQueueService.Instance.StartNew(name, async (item, ct) =>
        {
            if (parentVersionId != null) item.RegisterInstallationTarget(parentVersionId);
            var progress = item.CreateProgress();
            progress.Report(new ProgressInfo("等待同目录的安装任务完成", 0, 0, ""));
            await gate.WaitAsync(ct);
            try
            {
                using var service = new ClientLoaderInstallService(snapshot);
                using var vanilla = DownloadService.CreateFromConfig(snapshot);
                service.InstallationTargetResolved += item.RegisterInstallationTarget;
                var versionId = await install(service, vanilla, progress, ct);
                ct.ThrowIfCancellationRequested();
                item.RegisterInstallationTarget(versionId);
                completed?.Invoke(versionId);
            }
            finally { gate.Release(); }
        }, targetVersionId: parentVersionId, targetFolderPath: folder, supportsPause: false);
    }
}
