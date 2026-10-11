using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using XCL2.App.Models;

namespace XCL2.App.Services.Plugins;

/// <summary>API 2.2：复用宿主资源来源、校验、安装和全局下载队列；不接收调用者提供的下载 URL。</summary>
public sealed class PluginResourceService
{
    private readonly PluginContext _context;
    private readonly ModrinthService _modrinth = new();
    private readonly CurseForgeService _curseForge = new(new CurseForgeKeyService());
    private readonly object _cacheGate = new();
    private readonly ConcurrentDictionary<string, CachedFile> _files = new(StringComparer.Ordinal);
    private sealed record CachedFile(PluginResourceFile File, string SourceId, PluginResourceKind Kind,
        object NativeFile, DateTimeOffset ExpiresAt, long AuthorizationRevision, long? ProviderRevision, PluginContext? ProviderContext);
    internal PluginResourceService(PluginContext context) => _context = context;

    public Task<ImmutableArray<PluginResourceSource>> GetSourcesAsync(CancellationToken ct = default)
    {
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.ResourcesRead);
        var kinds = ImmutableArray.Create(PluginResourceKind.Mod, PluginResourceKind.ResourcePack, PluginResourceKind.Shader);
        var result = new[] { new PluginResourceSource("modrinth", "Modrinth", false, kinds),
            new PluginResourceSource("curseforge", "CurseForge", false, kinds) }
            .Concat(PluginProviderRegistry.Snapshot("mod").Select(p => new PluginResourceSource(p.Id, _context.Safe(p.Name), true,
                ImmutableArray.Create(PluginResourceKind.Mod)))).ToImmutableArray();
        scope.Check(); return Task.FromResult(result);
    }
    public async Task<PluginResourceSearchResult> SearchAsync(PluginResourceQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.ResourcesRead);
        Validate(query.SourceId, query.Kind, query.GameVersion, query.Loader);
        if (query.Query == null || query.Query.Length > 500 || query.Offset is < 0 or > 100000 || query.Limit is < 1 or > 100)
            throw new PluginOperationException("InvalidArgument", "搜索词最多 500 字，offset 为 0～100000，limit 为 1～100。");
        try
        {
            IEnumerable<PluginResourceProject> projects; int total;
            if (query.SourceId == "modrinth")
            {
                var result = await _modrinth.SearchAsync(ToModrinth(query.Kind), query.Query, query.GameVersion,
                    query.Offset, query.Limit, scope.Token, query.Loader).ConfigureAwait(false);
                projects = result.Hits.Take(query.Limit).Select(p => new PluginResourceProject(query.SourceId, p.ProjectId, query.Kind,
                    Safe(p.Title), Safe(p.Description), Safe(p.Author))); total = result.TotalHits;
            }
            else if (query.SourceId == "curseforge")
            {
                var result = query.Kind == PluginResourceKind.Mod
                    ? await _curseForge.SearchModsAsync(query.Query, query.GameVersion, query.Loader, query.Offset, query.Limit, scope.Token).ConfigureAwait(false)
                    : await _curseForge.SearchResourcesAsync(ToCurseForge(query.Kind), query.Query, query.GameVersion, query.Offset, query.Limit, scope.Token).ConfigureAwait(false);
                projects = result.Data.Take(query.Limit).Select(p => new PluginResourceProject(query.SourceId, p.Id.ToString(CultureInfo.InvariantCulture), query.Kind,
                    Safe(p.Name), Safe(p.Summary), Safe(p.AuthorsDisplay))); total = result.Pagination?.TotalCount ?? result.Data.Count;
            }
            else
            {
                var entry = PluginProviderRegistry.Get(query.SourceId, "mod");
                var result = await PluginProviderRegistry.InvokeAsync(entry, token => ((IPluginModProvider)entry.Provider).SearchAsync(
                    new(query.Query, query.GameVersion, query.Loader, query.Offset, query.Limit), token), scope.Token).ConfigureAwait(false);
                if (result.Items.IsDefault || result.Items.Length > 1000) throw new PluginOperationException("InvalidResults", "资源来源返回了无效的搜索结果。");
                projects = result.Items.Where(p => p != null && ValidId(p.Id)).Take(query.Limit).Select(p =>
                    new PluginResourceProject(query.SourceId, p.Id, query.Kind, Safe(p.Title), Safe(p.Description), Safe(p.Author)));
                total = result.Total;
            }
            scope.Check();
            return new(projects.ToImmutableArray(), Math.Max(0, total), query.Offset, query.Limit);
        }
        catch (OperationCanceledException) { throw; }
        catch (PluginOperationException) { throw; }
        catch (Exception ex) { throw SafeFailure("ResourceQueryFailed", ex); }
    }
    public async Task<ImmutableArray<PluginResourceFile>> GetFilesAsync(PluginResourceFileQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.ResourcesRead);
        Validate(query.SourceId, query.Kind, query.GameVersion, query.Loader);
        if (!ValidId(query.ProjectId)) throw new PluginOperationException("InvalidArgument", "项目 ID 无效。");
        try
        {
            var results = new List<CachedFile>();
            if (query.SourceId == "modrinth")
            {
                var versions = await _modrinth.GetVersionsForLoaderAsync(query.ProjectId, query.GameVersion,
                    query.Kind == PluginResourceKind.Mod ? query.Loader : null, scope.Token).ConfigureAwait(false);
                foreach (var version in versions.Take(200))
                {
                    var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
                    if (file == null || !PluginProviderRegistry.ValidUrl(file.Url) || !ValidResourceFile(query.Kind, file.Filename)) continue;
                    results.Add(Create(query, version.Id, version.Name, file.Filename, version.GameVersions, version.Loaders,
                        file.Size, file.Hashes?.Sha1, version, null));
                }
            }
            else if (query.SourceId == "curseforge")
            {
                if (!int.TryParse(query.ProjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var projectId) || projectId <= 0)
                    throw new PluginOperationException("InvalidArgument", "CurseForge 项目 ID 必须是正整数。");
                var files = await _curseForge.GetFilesAsync(projectId, query.GameVersion, scope.Token).ConfigureAwait(false);
                foreach (var file in files.Where(f => ValidResourceFile(query.Kind, f.FileName)).Take(200))
                {
                    var loaders = file.GameVersions.Where(IsLoader).ToArray();
                    if (query.Kind == PluginResourceKind.Mod && !string.IsNullOrWhiteSpace(query.Loader) && !MatchesLoader(query.Loader, loaders)) continue;
                    results.Add(Create(query, file.Id.ToString(CultureInfo.InvariantCulture), file.DisplayName, file.FileName,
                        file.GameVersions.Where(v => !IsLoader(v)), loaders, file.FileLength, null, file, null));
                }
            }
            else
            {
                var entry = PluginProviderRegistry.Get(query.SourceId, "mod");
                var revision = Interlocked.Read(ref entry.Context.AuthorizationRevision);
                var files = await PluginProviderRegistry.InvokeAsync(entry, token => ((IPluginModProvider)entry.Provider)
                    .GetFilesAsync(query.ProjectId, query.GameVersion, query.Loader, token), scope.Token).ConfigureAwait(false);
                if (files.IsDefault || files.Length > 2000) throw new PluginOperationException("InvalidFiles", "资源来源返回了无效的文件列表。");
                foreach (var file in files.Where(f => f != null && PluginProviderRegistry.ValidUrl(f.Url) && ValidResourceFile(query.Kind, f.FileName)).Take(200))
                    results.Add(Create(query, file.Id, file.Name, file.FileName, NonEmpty(file.GameVersion), NonEmpty(file.Loader), null, file.Sha1, file, revision, entry.Context));
            }
            scope.Check();
            // 有界缓存：陈旧令牌不可继续用；新的查询在容量已满时不会无限增长内存。
            lock (_cacheGate)
            {
                scope.Check();
                foreach (var pair in _files.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow)) _files.TryRemove(pair.Key, out _);
                if (_files.Count + results.Count > 1000)
                    foreach (var pair in _files.OrderBy(p => p.Value.ExpiresAt).Take(_files.Count + results.Count - 1000)) _files.TryRemove(pair.Key, out _);
                foreach (var result in results) _files[result.File.Token] = result;
            }
            return results.Select(r => r.File).ToImmutableArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (PluginOperationException) { throw; }
        catch (Exception ex) { throw SafeFailure("ResourceQueryFailed", ex); }
    }
    private CachedFile Create(PluginResourceFileQuery query, string id, string name, string fileName,
        IEnumerable<string> gameVersions, IEnumerable<string> loaders, long? size, string? sha1, object native, long? providerRevision, PluginContext? providerContext = null)
    {
        if (sha1 != null && (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit)))
            throw new PluginOperationException("InvalidHash", "资源来源返回了无效的 SHA1。");
        var file = new PluginResourceFile(Guid.NewGuid().ToString("N"), Safe(id), Safe(name), fileName,
            gameVersions.Where(v => !string.IsNullOrWhiteSpace(v)).Take(200).Select(Safe).ToImmutableArray(),
            loaders.Where(v => !string.IsNullOrWhiteSpace(v)).Take(50).Select(Safe).ToImmutableArray(), size, sha1);
        return new(file, query.SourceId, query.Kind, native, DateTimeOffset.UtcNow.AddMinutes(30),
            Interlocked.Read(ref _context.AuthorizationRevision), providerRevision, providerContext);
    }
    public async Task<PluginDownloadAcceptance> InstallAsync(PluginResourceInstallRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.ResourcesInstall);
        if (request.Token == null || !_files.TryGetValue(request.Token, out var cached) || cached.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new PluginOperationException("FileTokenExpired", "请重新获取此插件的资源文件列表。");
        CheckCached(cached);
        var host = _context.Host;
        await host.GetInstancesAsync(new(true), scope.Token).ConfigureAwait(false);
        return await host.OnUiAsync(() =>
        {
            scope.Check(); CheckCached(cached);
            var instance = host.RequireManagedInstance(request.InstanceId); host.EnsureInstanceWritable(instance);
            ValidateCompatibility(cached, instance.Details.Instance);
            var targetDir = DestinationDirectory(host, instance, cached.Kind);
            ValidateDestination(instance, targetDir, cached.File.FileName, request.ReplaceExisting);
            var item = DownloadQueueService.Instance.StartNew("下载 " + cached.File.Name, async (item, token) =>
            {
                using var running = new PluginOperationScope(_context, token, PluginPermissionNames.ResourcesInstall);
                CheckCached(cached);
                var progress = item.CreateProgress();
                var current = await host.GetInstanceDetailsAsync(request.InstanceId, running.Token).ConfigureAwait(false)
                    ?? throw new PluginResourceNotFoundException();
                if (!current.GameDirectory.Equals(instance.Details.GameDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new PluginOperationException("InstanceChanged", "实例的目录设置已改变，请重新安装。");
                var staging = Path.Combine(_context.DataDirectory, "resource-staging", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    var status = new Progress<string>(message => progress.Report(new ProgressInfo(Safe(message), 0, 1, cached.File.FileName)));
                    string downloaded;
                    if (cached.NativeFile is ModrinthVersion modrinth)
                        downloaded = await _modrinth.DownloadResourceAsync(staging, ToModrinth(cached.Kind), modrinth, status,
                            ct: running.Token, appendCategorySubdir: false).ConfigureAwait(false);
                    else if (cached.NativeFile is CurseForgeFile curseForge)
                        downloaded = cached.Kind == PluginResourceKind.Mod
                            ? await _curseForge.DownloadModAsync(staging, curseForge, status, running.Token, appendCategorySubdir: false).ConfigureAwait(false)
                            : await _curseForge.DownloadResourceAsync(staging, ToCurseForge(cached.Kind), curseForge, status,
                                ct: running.Token, appendCategorySubdir: false).ConfigureAwait(false);
                    else
                    {
                        var pluginFile = (PluginModFile)cached.NativeFile;
                        using var service = new GenericFileDownloadService();
                        downloaded = await service.DownloadAsync(pluginFile.Url, staging, pluginFile.FileName, null, progress, running.Token).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrEmpty(cached.File.Sha1))
                    {
                        await using var file = File.OpenRead(downloaded);
                        var actual = Convert.ToHexString(await SHA1.HashDataAsync(file, running.Token).ConfigureAwait(false));
                        if (!actual.Equals(cached.File.Sha1, StringComparison.OrdinalIgnoreCase))
                            throw new PluginOperationException("HashMismatch", "下载的资源未通过 SHA1 校验。");
                    }
                    await host.OnUiAsync(() =>
                    {
                        running.Check(); CheckCached(cached);
                        var latest = host.RequireManagedInstance(request.InstanceId); host.EnsureInstanceWritable(latest, checkDownloads: false);
                        if (!latest.Details.GameDirectory.Equals(instance.Details.GameDirectory, StringComparison.OrdinalIgnoreCase))
                            throw new PluginOperationException("InstanceChanged", "实例的目录设置已改变，请重新安装。");
                        if (!DestinationDirectory(host, latest, cached.Kind).Equals(targetDir, StringComparison.OrdinalIgnoreCase))
                            throw new PluginOperationException("InstanceChanged", "资源包安装目录设置已改变，请重新安装。");
                        ValidateDestination(latest, targetDir, cached.File.FileName, request.ReplaceExisting);
                        if (cached.Kind == PluginResourceKind.Mod)
                        {
                            var plans = ModInstallService.PrepareBatch(new[] { downloaded }, targetDir);
                            if (!request.ReplaceExisting && plans.Any(p => p.ReplacedFiles.Count > 0 || File.Exists(p.Destination)))
                                throw new PluginOperationException("AlreadyExists", "已安装同名或同 ID 模组；需要明确允许替换。");
                            foreach (var plan in plans) ModInstallService.Install(plan);
                        }
                        else
                        {
                            Directory.CreateDirectory(targetDir);
                            var destination = Path.Combine(targetDir, cached.File.FileName);
                            var temporary = Path.Combine(targetDir, ".xcl-resource-" + Guid.NewGuid().ToString("N") + ".tmp");
                            try
                            {
                                File.Copy(downloaded, temporary);
                                running.Check();
                                File.Move(temporary, destination, request.ReplaceExisting);
                            }
                            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
                        }
                        return true;
                    }, running.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (PluginOperationException) { throw; }
                catch (Exception ex) { throw SafeFailure("ResourceInstallFailed", ex); }
                finally { try { Directory.Delete(staging, true); } catch { } }
            }, instance.VersionId, instance.Root);
            return new PluginDownloadAcceptance(item.Id, "Queued");
        }, scope.Token).ConfigureAwait(false);
    }
    public async Task<ImmutableArray<PluginGameVersion>> GetGameVersionsAsync(CancellationToken ct = default)
    {
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.ResourcesRead);
        try
        {
            using var service = await _context.Host.OnUiAsync(() => DownloadService.CreateFromConfig(_context.Host.Config.Config), scope.Token).ConfigureAwait(false);
            var manifest = await service.GetVersionManifestAsync(scope.Token).ConfigureAwait(false); scope.Check();
            return manifest.Versions.Take(8000).Select(v => new PluginGameVersion(Safe(v.Id), Safe(v.Type), Safe(v.ReleaseTime),
                v.Id == manifest.Latest.Release, v.Id == manifest.Latest.Snapshot)).ToImmutableArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (PluginOperationException) { throw; }
        catch (Exception ex) { throw SafeFailure("VersionQueryFailed", ex); }
    }
    public async Task<PluginDownloadAcceptance> InstallGameVersionAsync(string folderId, string versionId, CancellationToken ct = default)
    {
        PluginManagedPaths.RequireLeafName(versionId);
        using var scope = new PluginOperationScope(_context, ct, PluginPermissionNames.InstancesWrite, PluginPermissionNames.ResourcesInstall);
        var host = _context.Host;
        try
        {
            using var lookup = await host.OnUiAsync(() => DownloadService.CreateFromConfig(host.Config.Config), scope.Token).ConfigureAwait(false);
            var manifest = await lookup.GetVersionManifestAsync(scope.Token).ConfigureAwait(false);
            var selected = manifest.Versions.FirstOrDefault(v => v.Id == versionId) ?? throw new PluginResourceNotFoundException();
            return await host.OnUiAsync(() =>
            {
                scope.Check(); host.EnsureNoRunningGame();
                var folder = host.RequireManagedFolder(folderId); var root = folder.Path;
                var destination = Path.Combine(root, "versions", versionId); PluginManagedPaths.RequireNoLinks(root, destination);
                if (DownloadQueueService.Instance.FindActiveForVersion(root, versionId) != null)
                    throw new PluginOperationException("Busy", "该版本已有下载或补全任务。");
                if (Directory.Exists(destination)) throw new PluginOperationException("AlreadyExists", "实例已存在，请使用实例补全接口。");
                var item = DownloadQueueService.Instance.StartNew("安装 " + versionId, async (item, token) =>
                {
                    using var running = new PluginOperationScope(_context, token, PluginPermissionNames.InstancesWrite, PluginPermissionNames.ResourcesInstall);
                    var progress = item.CreateProgress();
                    using var download = await host.OnUiAsync(() =>
                    {
                        running.Check(); host.EnsureNoRunningGame();
                        if (!host.RequireManagedFolder(folderId).Path.Equals(root, StringComparison.OrdinalIgnoreCase))
                            throw new PluginOperationException("FolderChanged", "游戏目录已改变，请重新安装。");
                        PluginManagedPaths.RequireNoLinks(root, destination);
                        return DownloadService.CreateFromConfig(host.Config.Config);
                    }, running.Token).ConfigureAwait(false);
                    await download.InstallVersionAsync(root, selected, progress, running.Token).ConfigureAwait(false);
                    running.Check(); host.NotifyInstanceCreated(root, versionId);
                }, versionId, root);
                return new PluginDownloadAcceptance(item.Id, "Queued");
            }, scope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PluginOperationException) { throw; }
        catch (Exception ex) { throw SafeFailure("VersionInstallFailed", ex); }
    }
    private void CheckCached(CachedFile cached)
    {
        if (cached.AuthorizationRevision != Interlocked.Read(ref _context.AuthorizationRevision))
            throw new PluginOperationException("AuthorizationChanged", "授权发生过变更，请重新获取文件列表。");
        if (cached.ProviderRevision.HasValue)
        {
            var entry = PluginProviderRegistry.Get(cached.SourceId, "mod");
            entry.Context.Permissions.Demand(PluginPermissionNames.ModProvider); entry.Context.Permissions.Demand(PluginPermissionNames.Network);
            if (!ReferenceEquals(cached.ProviderContext, entry.Context) || cached.ProviderRevision != Interlocked.Read(ref entry.Context.AuthorizationRevision))
                throw new PluginOperationException("ProviderChanged", "资源来源的授权已改变，请重新获取文件列表。");
        }
    }
    private static void ValidateCompatibility(CachedFile file, PluginInstance instance)
    {
        if (file.File.GameVersions.Length > 0 && !file.File.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
            throw new PluginOperationException("IncompatibleResource", "选中文件不支持此实例的 Minecraft 版本。");
        if (file.Kind == PluginResourceKind.Mod && !MatchesLoader(instance.LoaderId, file.File.Loaders.Where(IsLoader)))
            throw new PluginOperationException("IncompatibleResource", "选中模组与此实例的加载器不兼容。");
    }
    private static string DestinationDirectory(PluginHostBridge host, PluginHostBridge.IndexedInstance instance, PluginResourceKind kind) =>
        Path.Combine(kind == PluginResourceKind.Mod ? instance.Details.GameDirectory : host.ManagedResourceDirectory(instance), kind switch { PluginResourceKind.Mod => "mods", PluginResourceKind.ResourcePack => "resourcepacks", _ => "shaderpacks" });
    private static void ValidateDestination(PluginHostBridge.IndexedInstance instance, string directory, string fileName, bool replace)
    {
        PluginManagedPaths.RequireLeafName(fileName); PluginManagedPaths.RequireNoLinks(instance.Root, Path.Combine(directory, fileName));
        if (!replace && File.Exists(Path.Combine(directory, fileName)))
            throw new PluginOperationException("AlreadyExists", "目标文件已存在；需要明确允许替换。");
    }
    private void Validate(string source, PluginResourceKind kind, string? version, string? loader)
    {
        if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(source) || source.Length > 250 ||
            (version != null && !ValidId(version)) || (loader != null && !ValidId(loader)))
            throw new PluginOperationException("InvalidArgument", "资源查询参数无效。");
        if (source is not ("modrinth" or "curseforge"))
        {
            if (kind != PluginResourceKind.Mod) throw new PluginOperationException("UnsupportedResourceKind", "插件资源来源只支持 Mod。");
            _ = PluginProviderRegistry.Get(source, "mod");
        }
    }
    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 200 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '+');
    private static bool IsLoader(string loader) => loader.ToLowerInvariant() is "fabric" or "quilt" or "forge" or "neoforge";
    private static bool MatchesLoader(string? selected, IEnumerable<string> supported)
    {
        var list = supported.ToArray();
        if (list.Length == 0) return true;
        if (string.IsNullOrWhiteSpace(selected)) return false;
        return list.Contains(selected, StringComparer.OrdinalIgnoreCase) ||
            (selected.Equals("quilt", StringComparison.OrdinalIgnoreCase) && list.Contains("fabric", StringComparer.OrdinalIgnoreCase));
    }
    private static bool ValidResourceFile(PluginResourceKind kind, string fileName)
    {
        try { PluginManagedPaths.RequireLeafName(fileName); }
        catch (PluginOperationException) { return false; }
        return fileName.EndsWith(kind == PluginResourceKind.Mod ? ".jar" : ".zip", StringComparison.OrdinalIgnoreCase);
    }
    private static IEnumerable<string> NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };
    private string Safe(string? value) { var text = _context.Safe(value); return text.Length > 2000 ? text[..2000] : text; }
    private PluginOperationException SafeFailure(string code, Exception exception)
    {
        _context.Log(code + ": " + exception.GetType().Name);
        return new PluginOperationException(code, "宿主资源操作失败，请检查来源、网络和目录状态后重试。");
    }
    private static ModrinthResourceType ToModrinth(PluginResourceKind kind) => kind switch
    { PluginResourceKind.Mod => ModrinthResourceType.Mod, PluginResourceKind.ResourcePack => ModrinthResourceType.ResourcePack, _ => ModrinthResourceType.Shader };
    private static CurseForgeResourceKind ToCurseForge(PluginResourceKind kind) => kind == PluginResourceKind.ResourcePack ? CurseForgeResourceKind.ResourcePack : CurseForgeResourceKind.Shader;
}
