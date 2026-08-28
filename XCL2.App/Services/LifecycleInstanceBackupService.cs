using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 启动器生命周期自动备份：只负责“启动时 / 真正关闭前”两种时机。
/// 定时备份继续由 ScheduledInstanceBackupService 负责，两套开关互不替代。
/// </summary>
public static class LifecycleInstanceBackupService
{
    private static readonly SemaphoreSlim BackupGate = new(1, 1);

    public static async Task<int> CreateBackupsAsync(AppConfig cfg, string reason, CancellationToken ct = default)
    {
        // 启动后立即关闭启动器等极端情况下，启动备份和关闭备份可能撞在一起。
        // 串行化两次生命周期备份，避免同时写同一个实例的 ZIP。
        await BackupGate.WaitAsync(ct);
        try
        {
            var targets = ResolveTargets(cfg);
            int completed = 0;

            foreach (var (folderPath, versionId) in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await InstanceBackupService.CreateBackupAsync(folderPath, versionId, reason, ct: ct);
                    completed++;

                    // 复用“定时备份保留数量”作为生命周期备份的统一保留上限，避免每次启停都无限堆 zip。
                    foreach (var old in InstanceBackupService.ListBackups(folderPath, versionId)
                                 .Skip(Math.Max(1, cfg.ScheduledInstanceBackupRetentionCount)))
                    {
                        InstanceBackupService.DeleteBackup(old.FilePath);
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // 实例可能刚被删掉；生命周期备份不因此阻断启动/关闭流程。
                }
                catch (Exception ex)
                {
                    ErrorPresenter.LogFallback($"{reason}失败：{versionId}", ex);
                }
            }

            return completed;
        }
        finally
        {
            BackupGate.Release();
        }
    }

    private static List<(string FolderPath, string VersionId)> ResolveTargets(AppConfig cfg)
    {
        var folders = cfg.Folders ?? new List<GameFolder>();
        var folder = folders.FirstOrDefault(f =>
                         string.Equals(f.Path, cfg.SelectedFolderPath, StringComparison.OrdinalIgnoreCase))
                     ?? folders.FirstOrDefault();
        if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
            return new();

        var versionsRoot = Path.Combine(folder.Path, "versions");
        if (!Directory.Exists(versionsRoot))
            return new();

        bool Exists(string id) => !string.IsNullOrWhiteSpace(id) && Directory.Exists(Path.Combine(versionsRoot, id));

        if (cfg.LifecycleBackupTargetMode == LifecycleBackupTargetMode.Single)
        {
            var current = cfg.SelectedVersionId?.Trim();
            return current != null && Exists(current)
                ? new() { (folder.Path, current) }
                : new();
        }

        var requested = (cfg.LifecycleBackupVersionIds ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0) return new();

        var existing = requested.Where(Exists).ToList();

        if (cfg.LifecycleBackupTargetMode == LifecycleBackupTargetMode.All && existing.Count != requested.Count)
            return new();
        if (cfg.LifecycleBackupTargetMode == LifecycleBackupTargetMode.Any && existing.Count == 0)
            return new();

        // Multiple：不存在的跳过；Any：只要至少一个存在就备份所有存在项；All：上面已保证全部存在。
        return existing.Select(id => (folder.Path, id)).ToList();
    }
}
