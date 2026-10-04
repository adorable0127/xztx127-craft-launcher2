using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services.Plugins;

/// <summary>宿主内部桥接；不向插件交出 Config、Account、窗口、进程等可变对象。</summary>
internal sealed class PluginHostBridge : IDisposable
{
    internal static PluginHostBridge? Current { get; private set; }
    private readonly MainWindow _owner;
    private readonly ConfigService _config;
    private readonly DispatcherTimer _poll;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, long> _progressTimes = new();
    private readonly Dictionary<DownloadQueueItem, PropertyChangedEventHandler> _downloads = new();
    private readonly ConcurrentDictionary<System.Diagnostics.Process, EventHandler> _gameHooks = new();
    private ImmutableDictionary<string, string> _modFingerprints = ImmutableDictionary<string, string>.Empty;
    private ImmutableHashSet<string> _hidden;
    private ImmutableDictionary<string, IndexedInstance> _instances = ImmutableDictionary<string, IndexedInstance>.Empty;
    private ImmutableArray<PluginAccount> _accounts = ImmutableArray<PluginAccount>.Empty;
    private ImmutableArray<string> _secrets = ImmutableArray<string>.Empty;
    private PluginSettingsSnapshot _settings;
    private string? _selectedInstance;
    private string? _selectedDownloadProvider;
    internal string? SelectedDownloadProvider => Volatile.Read(ref _selectedDownloadProvider);
    private bool _polling;
    private bool _disposed;
    private bool _announcedStopping;
    internal sealed record IndexedInstance(string Root, string VersionId, PluginInstanceDetails Details);
    internal TimeSpan CallbackTimeout => TimeSpan.FromSeconds(double.IsFinite(_config.Config.PluginCallbackTimeoutSeconds)
        ? Math.Clamp(_config.Config.PluginCallbackTimeoutSeconds, 0.1, 30) : 3);
    internal MainWindow Owner => _owner;
    internal ConfigService Config => _config;

    internal PluginHostBridge(MainWindow owner)
    {
        _owner = owner; _config = owner.ConfigService; Current = this;
        CopySecrets(); _accounts = CopyAccounts(); _settings = CopySettings();
        _hidden = _config.Config.HiddenInstanceKeys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedInstance = SelectedInstanceId();
        _selectedDownloadProvider = _config.Config.PluginDownloadProviderId;
        _config.Saved += ConfigurationSaved;
        _config.AccountsSaved += ConfigurationSaved;
        DownloadQueueService.Instance.Items.CollectionChanged += DownloadsChanged;
        foreach (var item in DownloadQueueService.Instance.Items) WatchDownload(item, false);
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _poll.Tick += async (_, _) =>
        {
            if (_polling || _disposed) return;
            _polling = true;
            try { await GetInstancesAsync(new(true), _stop.Token); await PollModsAsync(_stop.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] CatalogReadFailed: " + ex.GetType().Name); }
            finally { _polling = false; }
        };
    }
    internal async Task MarkReadyAsync()
    {
        try { await GetInstancesAsync(new(true), _stop.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] InitialCatalogReadFailed: " + ex.GetType().Name); }
        if (_disposed) return;
        _poll.Start();
        PluginEventHub.Publish(new PluginLauncherReady { LauncherVersion = typeof(IPlugin).Assembly.GetName().Version?.ToString() ?? "unknown" });
    }
    internal void AnnounceStopping()
    {
        if (_announcedStopping) return;
        _announcedStopping = true;
        PluginEventHub.Publish(new PluginLauncherStopping());
    }
    internal async Task PrepareExitAsync()
    {
        if (_announcedStopping) return;
        _announcedStopping = true;
        await PluginEventHub.PublishStoppingAsync(CallbackTimeout).ConfigureAwait(false);
    }
    internal Task<T> OnUiAsync<T>(Func<T> action, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed) throw new PluginHostUnavailableException();
        return _owner.Dispatcher.CheckAccess() ? Task.FromResult(action()) :
            _owner.Dispatcher.InvokeAsync(action, DispatcherPriority.Background, ct).Task;
    }
    private void OnUi(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted) return;
        if (_owner.Dispatcher.CheckAccess()) action();
        else _ = _owner.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }
    internal string Safe(string? text)
    {
        var result = text ?? "";
        foreach (var secret in _secrets)
            if (secret.Length > 0) result = result.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return result.Length > 16384 ? result[..16384] : result;
    }
    private void CopySecrets()
    {
        _secrets = _secrets.Concat(_config.Accounts.SelectMany(a => new[] { a.MsRefreshToken, a.MinecraftAccessToken, a.AuthServerClientToken }))
            .Concat(new[] { _config.Config.AiAssistant?.ApiKey }).Concat(_config.Config.AiAssistant?.CustomModels.Select(m => m.ProviderApiKey) ?? Enumerable.Empty<string?>())
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length).ToImmutableArray();
    }
    internal static string InstanceId(string root, string versionId) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(InstanceDeletionService.BuildKey(root, versionId)))).ToLowerInvariant();
    private string? SelectedInstanceId() => string.IsNullOrEmpty(_config.Config.SelectedFolderPath) || string.IsNullOrEmpty(_config.Config.SelectedVersionId)
        ? null : InstanceId(_config.Config.SelectedFolderPath, _config.Config.SelectedVersionId);
    private ImmutableArray<PluginAccount> CopyAccounts()
    {
        var selected = _config.GetSelectedAccount();
        var accounts = _config.Config.GuestModeEnabled && _config.GuestAccount != null ? new[] { _config.GuestAccount } : _config.Accounts.ToArray();
        return accounts.Select(a => new PluginAccount(Safe(a.Id), a.Type switch { AccountType.Microsoft => PluginAccountKind.Microsoft,
            AccountType.AuthServer => PluginAccountKind.AuthServer, _ => PluginAccountKind.Offline }, Safe(a.Username),
            string.IsNullOrEmpty(a.Uuid) ? null : Safe(a.Uuid), a.Id == selected?.Id, a.IsGuest)).ToImmutableArray();
    }
    internal Task<PluginListSnapshot<PluginAccount>> GetAccountsAsync(CancellationToken ct) => OnUiAsync(() =>
        new PluginListSnapshot<PluginAccount>(Interlocked.Read(ref PluginEventHub.Revision), DateTimeOffset.UtcNow, CopyAccounts()), ct);
    internal PluginSettingsSnapshot CopySettings()
    {
        var cfg = _config.Config;
        return new(Interlocked.Read(ref PluginEventHub.Revision), Safe(cfg.LauncherLanguage), Safe(cfg.UiSkin), cfg.IsDarkMode,
            cfg.SimplifiedModeEnabled, cfg.EnablePageAnimations, cfg.EnableUiAnimations, cfg.UiZoomPercent, cfg.MinMemoryMb,
            cfg.MaxMemoryMb, cfg.IsolateVersionsByDefault, cfg.Source == DownloadSource.BMCLAPI ? "bmclapi" : "official");
    }
    internal PluginEnvironmentSnapshot CopyEnvironment() => new(typeof(IPlugin).Assembly.GetName().Version?.ToString() ?? "unknown",
        PluginApi.Version, Safe(Path.GetDirectoryName(_config.ConfigPath)), Safe(_config.Config.LauncherLanguage));
    private void ConfigurationSaved() => OnUi(() =>
    {
        Volatile.Write(ref _selectedDownloadProvider, _config.Config.PluginDownloadProviderId);
        CopySecrets();
        var accounts = CopyAccounts();
        foreach (var account in accounts.Where(a => !_accounts.Any(old => old.Id == a.Id))) PluginEventHub.Publish(new PluginAccountAdded(account));
        foreach (var account in _accounts.Where(a => !accounts.Any(current => current.Id == a.Id))) PluginEventHub.Publish(new PluginAccountRemoved(account.Id));
        foreach (var account in accounts)
        {
            var old = _accounts.FirstOrDefault(a => a.Id == account.Id);
            if (old == null) continue;
            var fields = ImmutableArray.CreateBuilder<string>();
            if (old.Kind != account.Kind) fields.Add(nameof(account.Kind));
            if (old.DisplayName != account.DisplayName) fields.Add(nameof(account.DisplayName));
            if (old.Uuid != account.Uuid) fields.Add(nameof(account.Uuid));
            if (old.IsGuest != account.IsGuest) fields.Add(nameof(account.IsGuest));
            if (fields.Count != 0) PluginEventHub.Publish(new PluginAccountUpdated(account, fields.ToImmutable()));
        }
        if (_accounts.FirstOrDefault(a => a.IsSelected)?.Id != accounts.FirstOrDefault(a => a.IsSelected)?.Id)
            PluginEventHub.Publish(new PluginAccountSelectionChanged(accounts.FirstOrDefault(a => a.IsSelected)));
        _accounts = accounts;
        var hidden = _config.Config.HiddenInstanceKeys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in hidden.Except(_hidden)) PluginEventHub.Publish(new PluginInstanceVisibilityChanged(HiddenKeyId(key), true));
        foreach (var key in _hidden.Except(hidden)) PluginEventHub.Publish(new PluginInstanceVisibilityChanged(HiddenKeyId(key), false));
        _hidden = hidden;
        var selected = SelectedInstanceId();
        if (_selectedInstance != selected) { _selectedInstance = selected; PluginEventHub.Publish(new PluginInstanceSelectionChanged(selected)); }
        var settings = CopySettings();
        var keys = new List<string>();
        if (_settings.LauncherLanguage != settings.LauncherLanguage) keys.Add(nameof(settings.LauncherLanguage));
        if (_settings.UiSkin != settings.UiSkin) keys.Add(nameof(settings.UiSkin));
        if (_settings.IsDarkMode != settings.IsDarkMode) keys.Add(nameof(settings.IsDarkMode));
        if (_settings.SimplifiedModeEnabled != settings.SimplifiedModeEnabled) keys.Add(nameof(settings.SimplifiedModeEnabled));
        if (_settings.EnablePageAnimations != settings.EnablePageAnimations) keys.Add(nameof(settings.EnablePageAnimations));
        if (_settings.EnableUiAnimations != settings.EnableUiAnimations) keys.Add(nameof(settings.EnableUiAnimations));
        if (_settings.UiZoomPercent != settings.UiZoomPercent) keys.Add(nameof(settings.UiZoomPercent));
        if (_settings.MinMemoryMb != settings.MinMemoryMb) keys.Add(nameof(settings.MinMemoryMb));
        if (_settings.MaxMemoryMb != settings.MaxMemoryMb) keys.Add(nameof(settings.MaxMemoryMb));
        if (_settings.IsolateVersionsByDefault != settings.IsolateVersionsByDefault) keys.Add(nameof(settings.IsolateVersionsByDefault));
        if (_settings.BuiltInDownloadSource != settings.BuiltInDownloadSource) keys.Add(nameof(settings.BuiltInDownloadSource));
        _settings = settings;
        if (keys.Count != 0) PluginEventHub.Publish(new PluginSettingsChanged(keys.ToImmutableArray()));
    });
    private async Task<ImmutableArray<IndexedInstance>> ScanInstancesAsync(CancellationToken ct)
    {
        var state = await OnUiAsync(() =>
        {
            var cfg = _config.Config;
            return (Roots: cfg.Folders.GroupBy(f => InstanceDeletionService.BuildKey(f.Path, ""), StringComparer.OrdinalIgnoreCase).Select(g => g.First().Path).ToArray(),
                Selected: SelectedInstanceId(), Hidden: cfg.HiddenInstanceKeys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                Isolation: cfg.VersionIsolationOverrides.ToImmutableDictionary(), DefaultIsolation: cfg.IsolateVersionsByDefault,
                Jvm: cfg.CustomJvmArgs, Java: cfg.JavaPath,
                JavaOverrides: cfg.VersionJavaIdOverrides.ToImmutableDictionary(), SelectedJava: cfg.SelectedJavaId,
                Javas: cfg.InstalledJavas.ToImmutableDictionary(j => j.Id, j => j.JavawPath));
        }, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var instances = ImmutableArray.CreateBuilder<IndexedInstance>();
            foreach (var root in state.Roots)
            {
                ct.ThrowIfCancellationRequested();
                List<GameVersion> versions;
                try { versions = new FolderService().ScanVersions(root); }
                catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] InstanceDirectoryReadFailed: " + ex.GetType().Name); continue; }
                foreach (var version in versions)
                {
                    ct.ThrowIfCancellationRequested();
                    var id = InstanceId(root, version.Id);
                    var hidden = state.Hidden.Contains(InstanceDeletionService.BuildKey(root, version.Id));
                    var instance = new PluginInstance(id, Safe(version.Id), Safe(version.McVersion), version.ModLoader?.ToLowerInvariant(),
                        version.ModLoaderVersion == null ? null : Safe(version.ModLoaderVersion), version.IsInstalled, id == state.Selected, hidden);
                    var isolated = state.Isolation.TryGetValue(version.Id, out var isolate) ? isolate : state.DefaultIsolation;
                    var javaId = state.JavaOverrides.TryGetValue(version.Id, out var overrideId) ? overrideId : state.SelectedJava;
                    var java = javaId != null && state.Javas.TryGetValue(javaId, out var javaPath) && File.Exists(javaPath) ? javaPath :
                        !string.IsNullOrEmpty(state.Java) && File.Exists(state.Java) ? state.Java : null;
                    var settings = InstanceConfigService.TryLoad(version.FolderPath);
                    ImmutableArray<string> jvm;
                    try { jvm = PluginArgumentSyntax.Parse((state.Jvm ?? "") + " " + (settings?.CustomJvmArgs ?? ""))
                        .Where(PluginArgumentSyntax.IsPublicArgument).Select(Safe).ToImmutableArray(); }
                    catch (PluginOperationException) { jvm = ImmutableArray<string>.Empty; }
                    var details = new PluginInstanceDetails(instance, Safe(version.FolderPath), Safe(isolated ? version.FolderPath : root), isolated,
                        java == null ? null : Safe(java), java == null ? PluginJavaResolutionState.Unresolved : PluginJavaResolutionState.Configured, jvm);
                    instances.Add(new(root, version.Id, details));
                }
            }
            return instances.ToImmutable();
        }, ct).ConfigureAwait(false);
    }
    internal async Task<PluginListSnapshot<PluginInstance>> GetInstancesAsync(PluginInstanceQuery query, CancellationToken ct)
    {
        var captured = DateTimeOffset.UtcNow;
        var revision = Interlocked.Read(ref PluginEventHub.Revision);
        var instances = await ScanInstancesAsync(ct).ConfigureAwait(false);
        var indexed = instances.ToImmutableDictionary(i => i.Details.Instance.Id);
        var previous = Interlocked.Exchange(ref _instances, indexed);
        if (previous.Count != indexed.Count || indexed.Any(pair => !previous.TryGetValue(pair.Key, out var old) || !SameDetails(old.Details, pair.Value.Details)))
            PluginEventHub.Publish(new PluginInstanceCatalogChanged("Rescan"));
        return new(revision, captured, instances.Select(i => i.Details.Instance)
            .Where(i => (query.IncludeHidden || !i.IsHidden) && (query.IncludeIncomplete || i.IsInstalled)).ToImmutableArray());
    }
    internal async Task<PluginInstanceDetails?> GetInstanceDetailsAsync(string id, CancellationToken ct) =>
        (await ScanInstancesAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.Details.Instance.Id == id)?.Details;
    private static bool SameDetails(PluginInstanceDetails a, PluginInstanceDetails b) => a.Instance == b.Instance &&
        a.InstanceDirectory == b.InstanceDirectory && a.GameDirectory == b.GameDirectory && a.Isolated == b.Isolated &&
        a.ConfiguredJavaPath == b.ConfiguredJavaPath && a.JavaResolutionState == b.JavaResolutionState &&
        a.ConfiguredJvmArguments.SequenceEqual(b.ConfiguredJvmArguments);
    private static string HiddenKeyId(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()))).ToLowerInvariant();
    private async Task PollModsAsync(CancellationToken ct)
    {
        var instances = _instances.Values.ToArray();
        var fingerprints = await Task.Run(() =>
        {
            var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var result = ImmutableDictionary.CreateBuilder<string, string>();
            foreach (var instance in instances)
            {
                ct.ThrowIfCancellationRequested();
                var dir = Path.Combine(instance.Details.Isolated ? Path.Combine(instance.Root, "versions", instance.VersionId) : instance.Root, "mods");
                if (!directories.TryGetValue(dir, out var fingerprint))
                {
                    try
                    {
                        fingerprint = !Directory.Exists(dir) ? "" : string.Join("|", Directory.EnumerateFiles(dir)
                            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f =>
                            { var info = new FileInfo(f); return info.Name + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks; }));
                    }
                    catch { continue; }
                    directories[dir] = fingerprint;
                }
                result[instance.Details.Instance.Id] = fingerprint;
            }
            return result.ToImmutable();
        }, ct).ConfigureAwait(false);
        foreach (var pair in fingerprints)
            if (_modFingerprints.TryGetValue(pair.Key, out var old) && old != pair.Value) PluginEventHub.Publish(new PluginModCatalogChanged(pair.Key));
        _modFingerprints = fingerprints;
    }
    internal async Task<PluginListSnapshot<PluginMod>> GetModsAsync(string instanceId, CancellationToken ct)
    {
        var instance = (await ScanInstancesAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.Details.Instance.Id == instanceId)
            ?? throw new PluginResourceNotFoundException();
        var gameDir = instance.Details.Isolated ? Path.Combine(instance.Root, "versions", instance.VersionId) : instance.Root;
        var captured = DateTimeOffset.UtcNow; var revision = Interlocked.Read(ref PluginEventHub.Revision);
        var mods = await Task.Run(() => ReadMods(gameDir, ct), ct).ConfigureAwait(false);
        return new(revision, captured, mods);
    }
    private ImmutableArray<PluginMod> ReadMods(string gameDir, CancellationToken ct)
    {
        return new LocalModService().ScanMods(gameDir).Select(mod =>
        {
            ct.ThrowIfCancellationRequested();
            var metadata = ReadModMetadata(mod.FilePath);
            return new PluginMod(Safe(mod.FileName), Safe(mod.DisplayName), metadata.Id == null ? null : Safe(metadata.Id),
                metadata.Version == null ? null : Safe(metadata.Version), mod.IsEnabled, mod.FileSizeBytes);
        }).ToImmutableArray();
    }
    private static (string? Id, string? Version) ReadModMetadata(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json") ?? zip.GetEntry("mcmod.info");
            if (entry != null && entry.Length <= 2 * 1024 * 1024)
            {
                using var stream = entry.Open(); using var document = JsonDocument.Parse(stream);
                var json = document.RootElement;
                if (json.ValueKind == JsonValueKind.Array) json = json.EnumerateArray().FirstOrDefault();
                if (json.ValueKind != JsonValueKind.Object) return (null, null);
                if (json.TryGetProperty("quilt_loader", out var quilt)) json = quilt;
                var id = json.TryGetProperty("id", out var modId) ? modId : json.TryGetProperty("modid", out modId) ? modId : default;
                var version = json.TryGetProperty("version", out var modVersion) ? modVersion : default;
                return (id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                    version.ValueKind == JsonValueKind.String ? LiteralVersion(version.GetString()) : null);
            }
            entry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (entry == null || entry.Length > 2 * 1024 * 1024) return (null, null);
            using var reader = new StreamReader(entry.Open()); var toml = reader.ReadToEnd();
            var block = Regex.Match(toml, @"\[\[mods\]\](.*?)(?=\n\s*\[|\z)", RegexOptions.Singleline).Groups[1].Value;
            string? Field(string key)
            {
                var match = Regex.Match(block, "(?m)^\\s*" + key + "\\s*=\\s*[\"']([^\"']*)[\"']");
                return match.Success ? match.Groups[1].Value : null;
            }
            return (Field("modId"), LiteralVersion(Field("version")));
        }
        catch { return (null, null); }
    }
    private static string? LiteralVersion(string? value) => string.IsNullOrWhiteSpace(value) || value.Contains("${", StringComparison.Ordinal) ? null : value;
    private PluginDownload CopyDownload(DownloadQueueItem item) => new(item.Id, Safe(item.Name), item.Status switch
    {
        DownloadQueueItemStatus.Paused => PluginDownloadState.Paused, DownloadQueueItemStatus.Completed => PluginDownloadState.Completed,
        DownloadQueueItemStatus.Failed => PluginDownloadState.Failed, DownloadQueueItemStatus.Canceled => PluginDownloadState.Canceled,
        _ => PluginDownloadState.Downloading
    }, double.IsFinite(item.ProgressPercent) ? Math.Clamp(item.ProgressPercent, 0, 100) : 0, null,
        string.IsNullOrEmpty(item.TargetFolderPath) || string.IsNullOrEmpty(item.TargetVersionId) ? null : InstanceId(item.TargetFolderPath, item.TargetVersionId),
        item.Status == DownloadQueueItemStatus.Failed ? "DownloadFailed" : null);
    internal Task<PluginListSnapshot<PluginDownload>> GetDownloadsAsync(CancellationToken ct) => OnUiAsync(() =>
        new PluginListSnapshot<PluginDownload>(Interlocked.Read(ref PluginEventHub.Revision), DateTimeOffset.UtcNow,
            DownloadQueueService.Instance.Items.Select(CopyDownload).ToImmutableArray()), ct);
    private void DownloadsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnUi(() =>
    {
        if (e.OldItems != null) foreach (DownloadQueueItem item in e.OldItems) UnwatchDownload(item);
        if (e.NewItems != null) foreach (DownloadQueueItem item in e.NewItems) WatchDownload(item, true);
        if (e.Action != NotifyCollectionChangedAction.Reset) return;
        foreach (var item in _downloads.Keys.ToArray()) UnwatchDownload(item);
        foreach (var item in DownloadQueueService.Instance.Items) WatchDownload(item, false);
    });
    private void WatchDownload(DownloadQueueItem item, bool created)
    {
        if (_downloads.ContainsKey(item)) return;
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadQueueItem.ProgressPercent))
            {
                var now = Environment.TickCount64;
                if (_progressTimes.TryGetValue(item.Id, out var previous) && now - previous < 250) return;
                if (!_progressTimes.TryAdd(item.Id, now) && !_progressTimes.TryUpdate(item.Id, now, previous)) return;
            }
            else if (e.PropertyName != nameof(DownloadQueueItem.Status)) return;
            var download = CopyDownload(item);
            OnUi(() =>
            {
                PluginEventHub.Publish(new PluginDownloadProgressChanged(download));
                if (e.PropertyName != nameof(DownloadQueueItem.Status)) return;
                if (download.State == PluginDownloadState.Completed) PluginEventHub.Publish(new PluginDownloadCompleted(download));
                if (download.State == PluginDownloadState.Failed) PluginEventHub.Publish(new PluginDownloadFailed(download));
                if (download.State == PluginDownloadState.Canceled) PluginEventHub.Publish(new PluginDownloadCanceled(item.Id, "Canceled"));
            });
        };
        _downloads.Add(item, handler); item.PropertyChanged += handler;
        if (created) PluginEventHub.Publish(new PluginDownloadCreated(CopyDownload(item)));
    }
    private void UnwatchDownload(DownloadQueueItem item)
    {
        if (_downloads.Remove(item, out var handler)) item.PropertyChanged -= handler;
        if (!_disposed) PluginEventHub.Publish(new PluginDownloadRemoved(item.Id));
        _progressTimes.TryRemove(item.Id, out _);
    }
    internal void NotifyInstanceCreated(string root, string versionId) => OnUi(async () =>
    {
        try
        {
            await GetInstancesAsync(new(true), _stop.Token);
            if (_instances.TryGetValue(InstanceId(root, versionId), out var instance)) PluginEventHub.Publish(new PluginInstanceCreated(instance.Details.Instance));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] InstanceNotificationFailed: " + ex.GetType().Name); }
    });
    internal void NotifyInstanceRemoved(string root, string versionId)
        => PluginEventHub.Publish(new PluginInstanceRemoved(InstanceId(root, versionId)));
    internal void NotifyInstanceRenamed(string root, string oldName, string newName)
        => PluginEventHub.Publish(new PluginInstanceRenamed(InstanceId(root, oldName), InstanceId(root, newName), Safe(oldName), Safe(newName)));
    internal void NotifyModChange(string filePath, string kind, bool? enabled = null) => OnUi(async () =>
    {
        try
        {
            var gameDir = Path.GetDirectoryName(Path.GetDirectoryName(filePath));
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(filePath)), "mods", StringComparison.OrdinalIgnoreCase)) return;
            var instances = await ScanInstancesAsync(_stop.Token);
            foreach (var instance in instances.Where(i => string.Equals(i.Details.Isolated ? Path.Combine(i.Root, "versions", i.VersionId) : i.Root,
                gameDir, StringComparison.OrdinalIgnoreCase)))
            {
                var id = instance.Details.Instance.Id;
                var name = Path.GetFileName(filePath);
                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name[..^9];
                if (kind == "removed") PluginEventHub.Publish(new PluginModRemoved(id, Safe(name)));
                else if (enabled != null) PluginEventHub.Publish(new PluginModEnabledChanged(id, Safe(name), enabled.Value));
                else
                {
                    var mod = await Task.Run(() => ReadMods(gameDir!, _stop.Token).FirstOrDefault(m => m.FileName == name), _stop.Token);
                    if (mod != null) PluginEventHub.Publish(new PluginModInstalled(id, mod));
                }
                PluginEventHub.Publish(new PluginModCatalogChanged(id));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] ModNotificationFailed: " + ex.GetType().Name); }
    });
    internal void WatchGame(GameProcessInfo info, string root)
    {
        var id = InstanceId(root, info.VersionId); var pid = info.Pid; var claimed = 0;
        PluginEventHub.Publish(new PluginGameStarted(id, pid));
        async void Exited(object? sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return;
            info.Process.Exited -= Exited;
            _gameHooks.TryRemove(info.Process, out _);
            try
            {
                await info.WaitForOutputDrainAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                if (_disposed) return;
                int? exit = null; try { exit = info.Process.ExitCode; } catch { }
                PluginEventHub.Publish(new PluginGameExited(id, pid, exit, info.UserRequestedClose));
                if (exit != null && GameExitClassifier.Assess(info, exit.Value).Kind == GameExitKind.Crash)
                    PluginEventHub.Publish(new PluginGameCrashed(id, pid, exit));
            }
            catch (Exception ex) { LauncherLogService.AppendLine("[PluginHost] ProcessNotificationFailed: " + ex.GetType().Name); }
        }
        info.Process.Exited += Exited;
        _gameHooks[info.Process] = Exited;
        if (info.HasExited) Exited(info.Process, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _poll.Stop(); _stop.Cancel();
        _config.Saved -= ConfigurationSaved; _config.AccountsSaved -= ConfigurationSaved;
        DownloadQueueService.Instance.Items.CollectionChanged -= DownloadsChanged;
        foreach (var item in _downloads.Keys.ToArray()) UnwatchDownload(item);
        foreach (var hook in _gameHooks) hook.Key.Exited -= hook.Value;
        _gameHooks.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}

internal static class PluginArgumentSyntax
{
    internal static ImmutableArray<string> Parse(string text)
    {
        var result = ImmutableArray.CreateBuilder<string>(); var value = new StringBuilder(); var quoted = false; var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                var start = i; while (i < text.Length && text[i] == '\\') i++;
                var count = i - start;
                if (i < text.Length && text[i] == '"')
                {
                    value.Append('\\', count / 2);
                    if (count % 2 != 0) value.Append('"'); else quoted = !quoted;
                }
                else { value.Append('\\', count); i--; }
                started = true;
            }
            else if (text[i] == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(text[i]) && !quoted)
            {
                if (started) { result.Add(value.ToString()); value.Clear(); started = false; }
            }
            else { value.Append(text[i]); started = true; }
        }
        if (quoted) throw new PluginOperationException("InvalidArguments", "参数引号没有闭合。");
        if (started) result.Add(value.ToString());
        return result.ToImmutable();
    }
    internal static bool IsPublicArgument(string value) => !Regex.IsMatch(value,
        @"(?i)access.?token|refresh.?token|client.?token|password|authorization|cookie|api.?key|authlib|auth_access_token|auth_session|^-javaagent");
}
