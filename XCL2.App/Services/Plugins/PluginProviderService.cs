using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace XCL2.App.Services.Plugins;

public sealed record PluginProviderDefinition(string Id, string Name);
public sealed record PluginProviderInfo(string Id, string PluginId, string Name, string Kind);
public sealed record PluginVersionEntry(string Id, string Type, string Url, string ReleaseTime);
public sealed record PluginVersionManifest(string LatestRelease, string LatestSnapshot, ImmutableArray<PluginVersionEntry> Versions);
public sealed record PluginFileRequest(string OriginalUrl, string FileName, string? ExpectedSha1);
public interface IPluginDownloadProvider
{
    ValueTask<PluginVersionManifest> GetManifestAsync(CancellationToken ct);
    ValueTask<string?> ResolveFileAsync(PluginFileRequest request, CancellationToken ct);
}
public sealed record PluginModQuery(string Query, string? GameVersion, string? Loader, int Offset = 0, int Limit = 20);
public sealed record PluginModProject(string Id, string Title, string Description, string Author);
public sealed record PluginModSearchResult(ImmutableArray<PluginModProject> Items, int Total);
public sealed record PluginModFile(string Id, string Name, string GameVersion, string Loader, string FileName, string Url, string? Sha1);
public interface IPluginModProvider
{
    ValueTask<PluginModSearchResult> SearchAsync(PluginModQuery query, CancellationToken ct);
    ValueTask<PluginModProject?> GetDetailsAsync(string projectId, CancellationToken ct);
    ValueTask<ImmutableArray<PluginModFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct);
}
public sealed record PluginLoaderBuild(string Id, string Name, string Version);
public sealed record PluginLoaderInstallRequest(string MinecraftVersion, string BuildId, string InstanceName);
public interface IPluginLoaderInstaller
{
    ValueTask<ImmutableArray<PluginLoaderBuild>> GetBuildsAsync(string minecraftVersion, CancellationToken ct);
    ValueTask InstallAsync(PluginInstallWorkspace workspace, PluginLoaderInstallRequest request, CancellationToken ct);
}
public enum PluginLoginKind { Offline, MicrosoftDeviceCode, Yggdrasil }
public sealed record PluginLoginRequest(PluginLoginKind Kind, string? AuthServerUrl = null);
public sealed record PluginLoginTicket(Guid Id);
public interface IPluginLoginProvider
{
    ValueTask<PluginLoginTicket?> LoginAsync(PluginLoginBroker broker, CancellationToken ct);
}

/// <summary>任意线程注册；实际业务回调在插件后台域执行，宿主停止时自动移除。</summary>
public sealed class PluginProviderService
{
    private readonly PluginContext _context;
    internal PluginProviderService(PluginContext context) { _context = context; }
    public IDisposable RegisterDownloadProvider(PluginProviderDefinition definition, IPluginDownloadProvider provider)
        => Register("download", PluginPermissionNames.DownloadProvider, definition, provider);
    public IDisposable RegisterModProvider(PluginProviderDefinition definition, IPluginModProvider provider)
        => Register("mod", PluginPermissionNames.ModProvider, definition, provider);
    public IDisposable RegisterLoaderInstaller(PluginProviderDefinition definition, IPluginLoaderInstaller provider)
        => Register("loader", PluginPermissionNames.LoaderProvider, definition, provider);
    public IDisposable RegisterLoginProvider(PluginProviderDefinition definition, IPluginLoginProvider provider)
        => Register("login", PluginPermissionNames.AuthProvider, definition, provider);
    private IDisposable Register(string kind, string permission, PluginProviderDefinition definition, object provider)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(provider);
        _ = _context.Host; _context.Permissions.Demand(permission);
        if (!System.Text.RegularExpressions.Regex.IsMatch(definition.Id, "\\A[a-z0-9][a-z0-9._-]{0,127}\\z")) throw new ArgumentException("提供者 Id 无效。");
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        return _context.Track(PluginProviderRegistry.Register(_context, kind, permission, definition, provider));
    }
}

internal static class PluginProviderRegistry
{
    internal sealed record Entry(PluginContext Context, PluginProviderInfo Info, string Permission, object Provider);
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    internal static event Action? Changed;
    private static bool Usable(Entry entry) => entry.Context.Permissions.Has(entry.Permission) &&
        (entry.Info.Kind == "login" || entry.Context.Permissions.Has(PluginPermissionNames.Network));
    internal static PluginProviderInfo[] Snapshot(string kind)
    {
        lock (Gate) return Entries.Where(e => e.Info.Kind == kind && Usable(e)).OrderByDescending(e => e.Context.Metadata.Priority)
            .ThenBy(e => e.Info.Id, StringComparer.Ordinal).Select(e => e.Info).ToArray();
    }
    internal static IDisposable Register(PluginContext context, string kind, string permission, PluginProviderDefinition definition, object provider)
    {
        var entry = new Entry(context, new(context.PluginId + "/" + definition.Id, context.PluginId, context.Host.Safe(definition.Name), kind), permission, provider);
        lock (Gate)
        {
            if (Entries.Any(e => e.Info.Id == entry.Info.Id && e.Info.Kind == kind)) throw new ArgumentException("提供者重复注册。");
            Entries.Add(entry);
        }
        context.AuthorizationChanged += Notify;
        Notify();
        return new PluginRegistration(() => { lock (Gate) Entries.Remove(entry); context.AuthorizationChanged -= Notify; Notify(); });
    }
    private static void Notify()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList())
            try { handler(); } catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] ProviderUiRefreshFailed: " + ex.GetType().Name); }
    }
    internal static Entry Get(string id, string kind)
    {
        lock (Gate) return Entries.FirstOrDefault(e => e.Info.Id == id && e.Info.Kind == kind && Usable(e))
            ?? throw new PluginOperationException("ProviderUnavailable", "插件提供者不可用或权限已撤销。");
    }
    internal static async Task<T> InvokeAsync<T>(Entry entry, Func<CancellationToken, ValueTask<T>> callback, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!Usable(entry)) throw new PluginOperationException("ProviderUnavailable", "插件提供者不可用。");
        var revision = Interlocked.Read(ref entry.Context.AuthorizationRevision);
        var result = await entry.Context.Callbacks.InvokeAsync(entry.Permission, "Provider:" + entry.Info.Kind,
            callback, timeout ?? TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!result.Success || revision != Interlocked.Read(ref entry.Context.AuthorizationRevision) || !Usable(entry))
            throw new PluginOperationException("ProviderFailed", "插件提供者未完成操作；可以切换回内置来源。");
        return result.Value!;
    }
    internal static async Task<PluginVersionManifest?> GetSelectedManifestAsync(CancellationToken ct)
    {
        var id = PluginHostBridge.Current?.SelectedDownloadProvider;
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            var entry = Get(id, "download");
            var result = await InvokeAsync(entry, token => ((IPluginDownloadProvider)entry.Provider).GetManifestAsync(token), ct).ConfigureAwait(false);
            if (result.Versions.IsDefault || result.Versions.Length > 10000 || result.Versions.Any(v => v == null || !ValidFileName(v.Id) || !ValidUrl(v.Url)))
                throw new PluginOperationException("InvalidManifest", "插件版本清单无效。");
            return result with { Versions = result.Versions.Select(v => v with { Type = Safe(v.Type), ReleaseTime = Safe(v.ReleaseTime) }).ToImmutableArray() };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] DownloadProviderFallback: " + ex.GetType().Name); return null; }
    }
    internal static async Task<List<string>> AddMirrorAsync(string originalUrl, string fileName, string? sha1, IEnumerable<string> defaults, CancellationToken ct)
    {
        var result = defaults.ToList(); var id = PluginHostBridge.Current?.SelectedDownloadProvider;
        if (string.IsNullOrEmpty(id) || !PluginInterceptionRegistry.IsPublicUrl(originalUrl)) return result;
        try
        {
            var entry = Get(id, "download");
            var url = await InvokeAsync(entry, async token => await ((IPluginDownloadProvider)entry.Provider).ResolveFileAsync(new(originalUrl, fileName, sha1), token) ?? originalUrl, ct).ConfigureAwait(false);
            if (url != originalUrl && ValidUrl(url)) result.Insert(0, url);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] FileProviderFallback: " + ex.GetType().Name); }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }
    internal static string Safe(string text) => PluginHostBridge.Current?.Safe(text) ?? text;
    internal static bool ValidUrl(string? url) => url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && Safe(url) == url && url.Length <= 8192;
    internal static bool ValidFileName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name != "." && name != ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') && Safe(name) == name;
}

/// <summary>仅 GET；不附带宿主账户令牌、Cookie、API Key。任意线程调用，返回不可变文本。</summary>
public sealed class PluginNetworkService
{
    private readonly PluginContext _context;
    internal PluginNetworkService(PluginContext context) { _context = context; }
    public async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.Network);
        if (!PluginProviderRegistry.ValidUrl(url)) throw new PluginOperationException("InvalidUrl", "仅允许无用户信息的 HTTP(S) 地址。");
        var revision = Interlocked.Read(ref _context.AuthorizationRevision);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Stopping); stop.CancelAfter(TimeSpan.FromSeconds(30));
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Plugin/1.9");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
        using var data = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) != 0)
        {
            if (data.Length + read > 4 * 1024 * 1024) throw new PluginOperationException("ResponseTooLarge", "网络响应超过 4 MiB。");
            data.Write(buffer, 0, read);
        }
        _context.Permissions.Demand(PluginPermissionNames.Network);
        if (revision != Interlocked.Read(ref _context.AuthorizationRevision)) throw new PluginOperationException("AuthorizationChanged", "网络读取期间授权已变更。");
        return _context.Host.Safe(System.Text.Encoding.UTF8.GetString(data.ToArray()));
    }
}

/// <summary>安装专用临时工作区；宿主验证完整性后才提交到实例目录。API 不返回宿主内部对象。</summary>
public sealed class PluginInstallWorkspace
{
    private readonly PluginContext _context;
    private readonly CancellationToken _operation;
    private int _closed;
    internal string Root { get; }
    public string VersionId { get; }
    internal PluginInstallWorkspace(PluginContext context, string versionId, CancellationToken operation)
    {
        _context = context; VersionId = versionId; _operation = operation;
        Root = Path.Combine(context.DataDirectory, "install", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
    }
    private string PathFor(string relative)
    {
        if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(PluginInstallWorkspace));
        _operation.ThrowIfCancellationRequested(); _context.EnsureActive();
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new PluginOperationException("InvalidPath", "工作区路径必须是相对路径。");
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new PluginOperationException("InvalidPath", "不能访问工作区之外。");
        for (var parent = full; parent != Root && parent != null; parent = Path.GetDirectoryName(parent))
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new PluginOperationException("InvalidPath", "工作区不允许链接。");
        return full;
    }
    public async Task WriteVersionJsonAsync(string json, CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.InstanceFilesWrite);
        if (json.Length > 4 * 1024 * 1024 || _context.Host.Safe(json) != json) throw new PluginOperationException("InvalidVersionJson", "版本 JSON 过大或包含受保护凭据。");
        using var parsed = JsonDocument.Parse(json);
        if (!parsed.RootElement.TryGetProperty("id", out var id) || id.GetString() != VersionId)
            throw new PluginOperationException("InvalidVersionJson", "版本 JSON 的 id 必须匹配请求的实例名称。");
        var path = PathFor(Path.Combine("versions", VersionId, VersionId + ".json")); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _operation, _context.Stopping);
        await File.WriteAllTextAsync(path, json, stop.Token).ConfigureAwait(false);
        _context.Permissions.Demand(PluginPermissionNames.InstanceFilesWrite);
    }
    public async Task DownloadFileAsync(string url, string relativePath, string? sha1 = null, CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.InstanceFilesWrite); _context.Permissions.Demand(PluginPermissionNames.Network);
        if (!PluginProviderRegistry.ValidUrl(url)) throw new PluginOperationException("InvalidUrl", "下载地址无效。");
        var path = PathFor(relativePath);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _operation, _context.Stopping);
        using var download = new GenericFileDownloadService();
        await download.DownloadAsync(url, Path.GetDirectoryName(path)!, Path.GetFileName(path), null, ct: stop.Token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(sha1))
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, stop.Token).ConfigureAwait(false));
            if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase)) { File.Delete(path); throw new PluginOperationException("HashMismatch", "下载文件 SHA1 校验失败。"); }
        }
        _context.Permissions.Demand(PluginPermissionNames.InstanceFilesWrite); _context.Permissions.Demand(PluginPermissionNames.Network);
    }
    internal void Close() { Interlocked.Exchange(ref _closed, 1); }

    public async Task<int> RunJavaInstallerAsync(string relativeJar, ImmutableArray<string> arguments, CancellationToken ct = default)
    {
        _context.Permissions.Demand(PluginPermissionNames.ProcessInstall); _context.Permissions.Demand(PluginPermissionNames.InstanceFilesWrite);
        if (arguments.IsDefault || arguments.Length > 128 || arguments.Any(a => a == null || a.Length > 8192 || a.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0))
            throw new PluginOperationException("InvalidArguments", "安装器参数无效。");
        var jar = PathFor(relativeJar);
        if (!relativeJar.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || !File.Exists(jar)) throw new PluginResourceNotFoundException();
        var java = await _context.Host.OnUiAsync(() => _context.Host.Config.ResolveJavaPath(_context.Host.Config.Config.SelectedJavaId)
            ?? _context.Host.Config.Config.JavaPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(java) || !File.Exists(java)) throw new PluginOperationException("JavaUnavailable", "请先在启动器中选择 Java。");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _operation, _context.Stopping); stop.CancelAfter(TimeSpan.FromMinutes(10));
        var start = new System.Diagnostics.ProcessStartInfo(java) { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-jar"); start.ArgumentList.Add(jar);
        foreach (var argument in arguments) start.ArgumentList.Add(argument.Replace("${workspace}", Root, StringComparison.Ordinal));
        using var process = System.Diagnostics.Process.Start(start) ?? throw new PluginOperationException("InstallerStartFailed", "安装器进程无法启动。");
        try { await process.WaitForExitAsync(stop.Token).ConfigureAwait(false); return process.ExitCode; }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
    }
}
