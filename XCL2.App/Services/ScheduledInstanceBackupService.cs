using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed class ScheduledInstanceBackupService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly System.Threading.Timer _timer;
    private int _running;

    public ScheduledInstanceBackupService(ConfigService configService)
    {
        _configService = configService;
        _timer = new System.Threading.Timer(_ => _ = RunAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
    }

    private async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var cfg = _configService.Config;
            if (!cfg.ScheduledInstanceBackupEnabled) return;
            var versionId = cfg.ScheduledInstanceBackupVersionId ?? cfg.SelectedVersionId;
            var folder = cfg.Folders.FirstOrDefault(f => string.Equals(f.Path, cfg.SelectedFolderPath, StringComparison.OrdinalIgnoreCase))
                         ?? cfg.Folders.FirstOrDefault();
            if (folder == null || string.IsNullOrWhiteSpace(versionId)) return;

            var backups = InstanceBackupService.ListBackups(folder.Path, versionId);
            var last = backups.FirstOrDefault()?.CreatedAtUtc;
            if (last != null && DateTime.UtcNow - last.Value < TimeSpan.FromHours(Math.Max(1, cfg.ScheduledInstanceBackupIntervalHours))) return;

            await InstanceBackupService.CreateBackupAsync(folder.Path, versionId, "定时备份");
            foreach (var old in InstanceBackupService.ListBackups(folder.Path, versionId).Skip(Math.Max(1, cfg.ScheduledInstanceBackupRetentionCount)))
                InstanceBackupService.DeleteBackup(old.FilePath);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("实例定时备份失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
