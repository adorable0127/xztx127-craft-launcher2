using System.Collections.Immutable;
using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services.Plugins;

internal sealed partial class PluginHostBridge
{
    internal ImmutableArray<PluginGameFolder> CopyManagedFolders() => _config.Config.Folders
        .GroupBy(f => InstanceDeletionService.BuildKey(f.Path, ""), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
        .Select(f => new PluginGameFolder(InstanceId(f.Path, ""), Safe(f.Name), Safe(f.Path),
            string.Equals(f.Path, _config.Config.SelectedFolderPath, StringComparison.OrdinalIgnoreCase))).ToImmutableArray();
    internal GameFolder RequireManagedFolder(string id) => _config.Config.Folders.FirstOrDefault(f => InstanceId(f.Path, "") == id)
        ?? throw new PluginResourceNotFoundException();
    internal IndexedInstance RequireManagedInstance(string id) => _instances.TryGetValue(id, out var instance) && Directory.Exists(instance.Details.InstanceDirectory) &&
        _config.Config.Folders.Any(f => InstanceDeletionService.BuildKey(f.Path, "").Equals(InstanceDeletionService.BuildKey(instance.Root, ""), StringComparison.OrdinalIgnoreCase))
        ? instance : throw new PluginResourceNotFoundException();
    internal PluginInstanceDirectories CopyInstanceDirectories(IndexedInstance instance)
    {
        var detail = instance.Details;
        return new(detail.Instance.Id, detail.InstanceDirectory, detail.GameDirectory, Path.Combine(detail.GameDirectory, "mods"),
            Path.Combine(ManagedResourceDirectory(instance), "resourcepacks"), Path.Combine(ManagedResourceDirectory(instance), "shaderpacks"), Path.Combine(detail.GameDirectory, "saves"));
    }
    internal string ManagedResourceDirectory(IndexedInstance instance)
    {
        var cfg = _config.Config;
        var isolated = cfg.VersionResourcePackIsolationOverrides.TryGetValue(instance.VersionId, out var value)
            ? value : cfg.IsolateResourcePacksByDefault;
        return isolated ? instance.Details.InstanceDirectory : instance.Root;
    }
    internal void EnsureNoRunningGame()
    {
        if (_owner.PluginLaunchInProgress || _gameHooks.Keys.Any(p => { try { return !p.HasExited; } catch { return false; } }))
            throw new PluginOperationException("Busy", "请先结束游戏启动或退出运行中的游戏。");
    }
    internal void EnsureInstanceWritable(IndexedInstance instance, bool checkDownloads = true)
    {
        EnsureNoRunningGame();
        if (checkDownloads && DownloadQueueService.Instance.FindActiveForVersion(instance.Root, instance.VersionId) != null)
            throw new PluginOperationException("Busy", "该实例已有下载或补全任务。");
        PluginManagedPaths.RequireNoLinks(instance.Root, instance.Details.InstanceDirectory);
    }
    private void EnsureUniqueInstanceSettings(IndexedInstance instance)
    {
        if (_instances.Values.Count(i => i.VersionId.Equals(instance.VersionId, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new PluginOperationException("AmbiguousInstanceSettings", "多个游戏目录中存在同名实例，当前版本按名称保存覆盖设置，请先消除同名再操作。");
    }
    internal bool SetInstanceHidden(IndexedInstance instance, bool hidden)
    {
        _owner.EnsurePluginSettingsWritable();
        if (hidden) InstanceDeletionService.HideFromList(_config.Config, instance.Root, instance.VersionId);
        else InstanceDeletionService.UnhideFromList(_config.Config, instance.Root, instance.VersionId);
        _config.Save(); _owner.RefreshSidebar(); return hidden;
    }
    internal string RenameManagedInstance(IndexedInstance instance, string newName)
    {
        PluginManagedPaths.RequireLeafName(newName);
        EnsureInstanceWritable(instance); EnsureUniqueInstanceSettings(instance);
        _owner.EnsurePluginSettingsWritable();
        if (newName.Equals(instance.VersionId, StringComparison.Ordinal)) return instance.Details.Instance.Id;
        if (_instances.Values.Any(i => i.VersionId.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            throw new PluginOperationException("AlreadyExists", "已有同名实例，请使用不同名称。");
        InstanceDeletionService.Rename(_config.Config, instance.Root, instance.VersionId, newName);
        _config.Save(); _owner.RefreshSidebar();
        return InstanceId(instance.Root, newName);
    }
    internal bool SetInstanceIsolation(IndexedInstance instance, bool? isolated)
    {
        EnsureInstanceWritable(instance); EnsureUniqueInstanceSettings(instance); _owner.EnsurePluginSettingsWritable();
        var settings = InstanceConfigService.TryLoad(instance.Details.InstanceDirectory) ?? new InstanceSettings();
        settings.IsolateVersion = isolated;
        PluginManagedPaths.RequireNoLinks(instance.Root, InstanceConfigService.GetSettingsPath(instance.Details.InstanceDirectory));
        InstanceConfigService.Save(instance.Details.InstanceDirectory, settings);
        if (isolated.HasValue) _config.Config.VersionIsolationOverrides[instance.VersionId] = isolated.Value;
        else _config.Config.VersionIsolationOverrides.Remove(instance.VersionId);
        _config.Save(); return isolated ?? _config.Config.IsolateVersionsByDefault;
    }
    internal ImmutableArray<PluginJavaRuntime> CopyManagedJavas() => _config.Config.InstalledJavas.OrderBy(j => j.Priority)
        .Select(j => new PluginJavaRuntime(Safe(j.Id), Safe(j.Name), Safe(j.JavawPath), j.MajorVersion, Safe(j.Source), j.Priority,
            j.Id == _config.Config.SelectedJavaId, File.Exists(j.JavawPath))).ToImmutableArray();
    internal bool SelectManagedJava(string? javaId, IndexedInstance? instance)
    {
        EnsureNoRunningGame(); _owner.EnsurePluginSettingsWritable();
        InstalledJava? runtime = null;
        if (javaId != null)
        {
            runtime = _config.Config.InstalledJavas.FirstOrDefault(j => j.Id == javaId) ?? throw new PluginResourceNotFoundException();
            if (!File.Exists(runtime.JavawPath)) throw new PluginOperationException("JavaMissing", "已登记的 Java 文件不存在。");
        }
        if (instance != null)
        {
            EnsureInstanceWritable(instance); EnsureUniqueInstanceSettings(instance);
            var settings = InstanceConfigService.TryLoad(instance.Details.InstanceDirectory) ?? new InstanceSettings();
            settings.JavaId = javaId;
            PluginManagedPaths.RequireNoLinks(instance.Root, InstanceConfigService.GetSettingsPath(instance.Details.InstanceDirectory));
            InstanceConfigService.Save(instance.Details.InstanceDirectory, settings);
            if (javaId == null) _config.Config.VersionJavaIdOverrides.Remove(instance.VersionId);
            else _config.Config.VersionJavaIdOverrides[instance.VersionId] = javaId;
            _config.Config.VersionJavaOverrides.Remove(instance.VersionId);
        }
        else
        {
            _config.Config.SelectedJavaId = javaId;
            _config.Config.JavaPath = runtime?.JavawPath ?? "";
        }
        _config.Save(); return true;
    }
    internal PluginMod SetManagedModEnabled(IndexedInstance instance, string fileName, bool enabled)
    {
        PluginManagedPaths.RequireLeafName(fileName); EnsureInstanceWritable(instance);
        if (!(fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
            throw new PluginOperationException("InvalidModFile", "只能操作模组列表中的 JAR 文件。");
        var mods = Path.Combine(instance.Details.GameDirectory, "mods");
        var path = Path.Combine(mods, fileName); PluginManagedPaths.RequireNoLinks(instance.Root, path);
        if (!File.Exists(path)) throw new PluginResourceNotFoundException();
        var isEnabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var destination = enabled ? path[..^".disabled".Length] : path + ".disabled";
        if (isEnabled != enabled)
        {
            if (File.Exists(destination)) throw new PluginOperationException("AlreadyExists", "目标状态已有同名模组，请先处理冲突。");
            path = enabled ? new LocalModService().Enable(path) : new LocalModService().Disable(path);
        }
        return ReadMods(instance.Details.GameDirectory, CancellationToken.None).First(m => m.FileName.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
    }
    private DownloadQueueItem RequireDownload(Guid id) => DownloadQueueService.Instance.Items.FirstOrDefault(i => i.Id == id)
        ?? throw new PluginResourceNotFoundException();
    internal PluginDownloadDetails CopyDownloadDetails(Guid id)
    {
        var item = RequireDownload(id);
        return new(CopyDownload(item), item.CanPause, item.CanResume, item.CanCancel, item.IsRunning);
    }
    internal PluginDownloadDetails ControlDownload(Guid id, string action)
    {
        var item = RequireDownload(id);
        if (action == "pause")
        {
            if (!item.SupportsPause) throw new PluginOperationException("PauseUnsupported", "这个下载任务不支持暂停。");
            if (!item.CanPause) throw new PluginOperationException("InvalidDownloadState", "只有正在下载的任务可以暂停。");
            DownloadQueueService.Instance.Pause(item);
        }
        else
        {
            if (!item.CanResume) throw new PluginOperationException("InvalidDownloadState", "只有暂停或失败的任务可以继续。");
            var active = DownloadQueueService.Instance.FindActiveForVersion(item.TargetFolderPath, item.TargetVersionId);
            if (active != null && active != item) throw new PluginOperationException("Busy", "该实例已有另一个活动任务。");
            DownloadQueueService.Instance.Resume(item);
        }
        return CopyDownloadDetails(id);
    }
    internal bool CancelManagedDownload(Guid id)
    {
        var item = RequireDownload(id);
        if (!item.CanCancel) throw new PluginOperationException("CancelUnsupported", "当前安装阶段不能安全取消，请等待完成。");
        DownloadQueueService.Instance.Cancel(item); return true;
    }
    internal int ClearFinishedDownloads()
    {
        var items = DownloadQueueService.Instance.Items.Where(i => !i.IsRunning && i.Status is DownloadQueueItemStatus.Completed or DownloadQueueItemStatus.Failed or DownloadQueueItemStatus.Canceled).ToArray();
        foreach (var item in items) DownloadQueueService.Instance.Items.Remove(item);
        return items.Length;
    }
}

