using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 删除实例的两种模式：
///   1. 从列表中删除（不删文件）——把这个实例加进 <see cref="AppConfig.HiddenInstanceKeys"/>
///      黑名单，下次扫描时过滤掉，磁盘上的 versions/&lt;id&gt; 目录原封不动。用户随时可以在
///      "已隐藏的实例"里把它取消隐藏，重新出现在列表里，找回来的文件不需要任何额外操作
///      （本来就没动过）。
///   2. 从电脑中删除（删除所有文件）——物理删除 versions/&lt;id&gt; 整个目录，并清掉所有跟这个
///      版本相关的全局配置字典条目（Java 覆盖/隔离覆盖/自动加入服务器等），避免这些残留
///      条目将来被同名新实例意外继承。这一步不可撤销，调用方负责在动手之前用
///      DangerousConfirmDialog（xztx127 确认码）拦一道，本服务本身不做确认交互，只负责
///      "确认通过后到底怎么删"这部分。
/// </summary>
public static class InstanceDeletionService
{
    /// <summary>拼出 <see cref="AppConfig.HiddenInstanceKeys"/> 用的复合 key：
    /// "{文件夹路径}|{版本Id}"，统一转小写、路径分隔符标准化，避免大小写/斜杠差异导致
    /// 同一个实例被判定成两个不同的 key。</summary>
    public static string BuildKey(string folderPath, string versionId)
    {
        var normalizedFolder = (folderPath ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var normalizedId = (versionId ?? "").ToLowerInvariant();
        return $"{normalizedFolder}|{normalizedId}";
    }

    /// <summary>某个实例是否已经被"从列表中删除"（隐藏）。</summary>
    public static bool IsHidden(AppConfig config, string folderPath, string versionId)
        => config.HiddenInstanceKeys.Contains(BuildKey(folderPath, versionId), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从列表中删除（不删文件）：把实例加进隐藏黑名单。调用方需要自行调用
    /// ConfigService.Save() 落盘并刷新列表（跟其它设置改动的写回方式保持一致，
    /// 这个方法只改内存里的 config 对象，不负责保存和刷新界面）。
    /// </summary>
    public static void HideFromList(AppConfig config, string folderPath, string versionId)
    {
        var key = BuildKey(folderPath, versionId);
        if (!config.HiddenInstanceKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            config.HiddenInstanceKeys.Add(key);
    }

    /// <summary>取消隐藏，实例重新出现在列表里（文件本来就没动过，这里只是把黑名单条目摘掉）。</summary>
    public static void UnhideFromList(AppConfig config, string folderPath, string versionId)
    {
        var key = BuildKey(folderPath, versionId);
        config.HiddenInstanceKeys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 重命名一个客户端实例：本质是把 versions/&lt;旧id&gt; 目录整个改名成 versions/&lt;新id&gt;。
    /// 之所以能这么做而不用改内部任何文件：FolderService.ScanVersions 解析 json/jar 时已经
    /// 做了"文件夹名对不上就退回读目录里唯一的 json 文件"这层兼容（见该方法内 ResolveVersionJson
    /// 的注释），LauncherService 启动时走的也是同一套兼容逻辑，所以文件夹改名后不需要连带
    /// 改写内部 json/jar 文件名，改名前后都能正常识别、正常启动。
    /// 同时把所有跟旧 id 关联的配置字典条目（Java 覆盖/隔离覆盖/收藏/隐藏黑名单等）原样
    /// 搬到新 id 下，避免改名后这些设置突然"丢失"。
    /// </summary>
    public static void Rename(AppConfig config, string folderPath, string oldVersionId, string newVersionId)
    {
        var oldDir = Path.Combine(folderPath, "versions", oldVersionId);
        var newDir = Path.Combine(folderPath, "versions", newVersionId);

        if (!Directory.Exists(oldDir))
            throw new DirectoryNotFoundException($"找不到实例目录：{oldDir}");
        if (Directory.Exists(newDir))
            throw new IOException($"目标名称已存在：{newDir}");

        Directory.Move(oldDir, newDir);

        MoveDictionaryEntry(config.VersionIsolationOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionResourcePackIsolationOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionJavaOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionJavaIdOverrides, oldVersionId, newVersionId);
        MoveDictionaryEntry(config.VersionAutoJoinServer, oldVersionId, newVersionId);

        for (int i = 0; i < config.FavoriteVersionIds.Count; i++)
            if (string.Equals(config.FavoriteVersionIds[i], oldVersionId, StringComparison.OrdinalIgnoreCase))
                config.FavoriteVersionIds[i] = newVersionId;

        foreach (var fav in config.FavoriteItems)
            if (fav.Type == FavoriteItemType.Version && string.Equals(fav.SourceId, oldVersionId, StringComparison.OrdinalIgnoreCase))
                fav.SourceId = newVersionId;

        if (IsHidden(config, folderPath, oldVersionId))
        {
            UnhideFromList(config, folderPath, oldVersionId);
            HideFromList(config, folderPath, newVersionId);
        }

        if (string.Equals(config.SelectedVersionId, oldVersionId, StringComparison.OrdinalIgnoreCase))
            config.SelectedVersionId = newVersionId;
    }

    private static void MoveDictionaryEntry<TValue>(Dictionary<string, TValue> dict, string oldKey, string newKey)
    {
        if (dict.Remove(oldKey, out var value)) dict[newKey] = value;
    }

    /// <summary>
    /// 从电脑中删除：物理删除 versions/&lt;id&gt; 目录 + 清理所有跟这个版本相关的全局配置
    /// 字典条目。调用方需要先经过 xztx127 二次确认，这里不再重复确认，直接执行。
    /// 目录删除失败（文件被占用等）会抛异常，调用方自行捕获展示错误，不在这里吞掉——
    /// 删除失败但配置字典已经清了一半这种"半成品"状态好过静默失败、用户以为删掉了实际没删。
    /// 为了避免这种半成品，这里先删目录，成功之后才清配置字典。
    /// </summary>
    public static void DeletePermanently(AppConfig config, string folderPath, string versionId)
    {
        var versionDir = Path.Combine(folderPath, "versions", versionId);
        if (Directory.Exists(versionDir))
        {
            Directory.Delete(versionDir, recursive: true);
        }

        // 目录删除成功后再清理配置里跟这个版本相关的所有残留条目，避免文件都没了、
        // 配置里还留着一堆指向不存在版本的 Java 覆盖/隔离覆盖等死数据。
        config.VersionIsolationOverrides.Remove(versionId);
        config.VersionResourcePackIsolationOverrides.Remove(versionId);
        config.VersionJavaOverrides.Remove(versionId);
        config.VersionJavaIdOverrides.Remove(versionId);
        config.VersionAutoJoinServer.Remove(versionId);
        config.FavoriteVersionIds.RemoveAll(id => string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase));
        config.FavoriteItems.RemoveAll(f => f.Type == FavoriteItemType.Version
            && string.Equals(f.SourceId, versionId, StringComparison.OrdinalIgnoreCase));

        // 如果这个版本本来就在隐藏黑名单里（比如用户先隐藏、后来又想彻底删掉），
        // 顺手把黑名单条目也清掉，不留死引用。
        UnhideFromList(config, folderPath, versionId);

        if (string.Equals(config.SelectedVersionId, versionId, StringComparison.OrdinalIgnoreCase))
            config.SelectedVersionId = null;
    }
}
