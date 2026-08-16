using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services;

/// <summary>
/// 实例级备份/还原服务。
///
/// 设计出发点：加载器的增删/升级/降级/重装本质上都是"把 versions/&lt;id&gt; 这个实例目录
/// 里的内容整体替换"，而存档、模组(mods)、资源包(resourcepacks)、配置(config) 只要用户是
/// 独立实例(隔离)模式，也都放在这同一个目录树下。所以备份粒度就定在"整个实例目录打包成一个
/// zip"——既不会漏掉存档/模组/资源包，也不需要针对"加载器专属文件"单独识别哪些该备份、
/// 哪些不该备份，最简单也最不容易出错。
///
/// 备份 zip 统一放在 "&lt;实例目录&gt;.backups\" 这个跟实例目录同级、以 ".backups" 结尾的
/// 独立文件夹下（而不是塞进 versions/ 里面），避免：
///   1. 打包过程中把自己正在写的 zip 也一起扫描进去（自引用）；
///   2. 被 FolderService.ScanVersions 误扫描成一个新的"版本"出现在版本列表里。
/// </summary>
public static class InstanceBackupService
{
    public class BackupRecord
    {
        public string FilePath { get; set; } = "";
        public string FileName => Path.GetFileName(FilePath);
        public DateTime CreatedAtUtc { get; set; }
        public long SizeBytes { get; set; }
    }

    private static string GetBackupsDir(string folderPath)
        => Path.Combine(folderPath, "versions", ".instance_backups");

    /// <summary>
    /// 备份 versions/&lt;versionId&gt; 整个目录到 zip。
    /// reason 会拼进文件名里，方便用户在备份列表里一眼看出"这是升级前/降级前/重装前/卸载加载器前"
    /// 哪次操作留下的备份，不用逐个打开才知道用途。
    /// </summary>
    public static async Task<string> CreateBackupAsync(string folderPath, string versionId, string reason,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sourceDir = Path.Combine(folderPath, "versions", versionId);
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"找不到实例目录：{sourceDir}");

        var backupsDir = GetBackupsDir(folderPath);
        Directory.CreateDirectory(backupsDir);

        var safeReason = string.Concat((reason ?? "backup").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var zipPath = Path.Combine(backupsDir, $"{versionId}_{safeReason}_{timestamp}.zip");

        // 大目录压缩比较耗时，丢到线程池跑，避免卡 UI 线程；ZipFile 本身不支持进度回调，
        // 这里退化成"开始/结束"两段式进度，能满足"备份中…"这种忙碌指示的需求即可，
        // 没必要为了细粒度进度条自己手写 ZipArchive 逐文件循环。
        await Task.Run(() =>
        {
            progress?.Report(0);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            progress?.Report(1);
        }, ct);

        return zipPath;
    }

    /// <summary>列出某个实例的所有备份，按时间倒序（最新的排最前面）。</summary>
    public static List<BackupRecord> ListBackups(string folderPath, string versionId)
    {
        var backupsDir = GetBackupsDir(folderPath);
        if (!Directory.Exists(backupsDir)) return new List<BackupRecord>();

        return Directory.GetFiles(backupsDir, $"{versionId}_*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupRecord { FilePath = f.FullName, CreatedAtUtc = f.LastWriteTimeUtc, SizeBytes = f.Length })
            .ToList();
    }

    /// <summary>
    /// 从备份还原：先把当前 versions/&lt;versionId&gt; 目录整个删掉，再从 zip 解压回去。
    /// 调用方必须在还原前先给出一次明确的二次确认（这是破坏性操作，会覆盖当前所有改动），
    /// 本方法不做交互，只负责执行。
    /// </summary>
    public static async Task RestoreBackupAsync(string folderPath, string versionId, string backupZipPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupZipPath))
            throw new FileNotFoundException("备份文件不存在", backupZipPath);

        var targetDir = Path.Combine(folderPath, "versions", versionId);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);
            ZipFile.ExtractToDirectory(backupZipPath, targetDir, overwriteFiles: true);
        }, ct);
    }

    /// <summary>
    /// 删除一份备份文件。建议调用方遵循"先启动验证，确认游戏能正常进入后，再让用户选择是否
    /// 删除备份"这个顺序——本方法本身不做这个判断，只负责删除动作，具体的提示时机由 UI 层
    /// （ToastService 通知里的"删除备份"按钮）控制。
    /// </summary>
    public static void DeleteBackup(string backupZipPath)
    {
        if (File.Exists(backupZipPath)) File.Delete(backupZipPath);
    }

    /// <summary>删除某个实例的全部历史备份，用于备份文件夹清理/主动"垃圾清理"功能接入。</summary>
    public static void DeleteAllBackups(string folderPath, string versionId)
    {
        foreach (var b in ListBackups(folderPath, versionId))
            DeleteBackup(b.FilePath);
    }

    /// <summary>
    /// 操作成功后统一弹出的"是否删除本次备份"提示，左下角、不自动消失。
    /// 明确建议用户先启动游戏验证一次、确认没问题了再删除——真出问题时这份备份就是
    /// 唯一的后悔药，不该在操作一完成就诱导立刻删掉。调用方只需要传新鲜出炉的备份 zip
    /// 路径，不需要自己拼文案/处理按钮点击。
    /// </summary>
    public static void NotifyBackupCreated(string backupZipPath, string operationDescription)
    {
        ToastService.ShowActionPrompt(
            message: $"{operationDescription}成功，已自动生成一份操作前备份。",
            hint: "建议先启动游戏验证一切正常后，再删除这份备份。",
            primaryText: "删除备份",
            primaryAction: () =>
            {
                try { DeleteBackup(backupZipPath); ToastService.ShowInfo("备份已删除"); }
                catch { ToastService.ShowWarning("备份删除失败，可稍后在实例设置里手动清理"); }
            },
            secondaryText: "保留",
            secondaryAction: null);
    }
}
