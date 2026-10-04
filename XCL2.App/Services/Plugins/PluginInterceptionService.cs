using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;

namespace XCL2.App.Services.Plugins;

public sealed record PluginLaunchPlan(Guid OperationId, string InstanceId, ImmutableArray<string> JvmArguments,
    ImmutableArray<string> GameArguments, ImmutableDictionary<string, string> EnvironmentVariables);
public sealed record PluginDownloadPlan(Guid OperationId, string Url, string FileName, string? ExpectedSha1);
public sealed record PluginInstanceCreationPlan(Guid OperationId, string InstanceId, string Name, string Kind);
public sealed record PluginInterception<T>(bool IsCanceled, string? Reason, T? Replacement)
{
    public static PluginInterception<T> Continue(T? replacement = default) => new(false, null, replacement);
    public static PluginInterception<T> Cancel(string reason) => new(true, reason, default);
}
public sealed class PluginFlowCanceledException : PluginOperationException
{
    public string PluginId { get; }
    public string Reason { get; }
    internal PluginFlowCanceledException(string pluginId, string reason)
        : base("CanceledByPlugin", Loc.F("Str_Plugin_FlowCanceled", "被插件 {0} 取消：{1}", pluginId, reason))
    { PluginId = pluginId; Reason = reason; }
}

public sealed partial class PluginEventService
{
    public IDisposable InterceptLaunch(Func<PluginLaunchPlan, CancellationToken, ValueTask<PluginInterception<PluginLaunchPlan>>> callback)
        => RegisterInterception(PluginPermissionNames.LaunchModify, callback);
    public IDisposable InterceptDownload(Func<PluginDownloadPlan, CancellationToken, ValueTask<PluginInterception<PluginDownloadPlan>>> callback)
        => RegisterInterception(PluginPermissionNames.DownloadIntercept, callback);
    public IDisposable InterceptInstanceCreation(Func<PluginInstanceCreationPlan, CancellationToken, ValueTask<PluginInterception<PluginInstanceCreationPlan>>> callback)
        => RegisterInterception(PluginPermissionNames.InstanceValidate, callback);
    private IDisposable RegisterInterception<T>(string permission, Func<T, CancellationToken, ValueTask<PluginInterception<T>>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _ = _context.Host;
        _context.Permissions.Demand(permission);
        if (IsDisposed) throw new ObjectDisposedException(nameof(PluginEventService));
        return _context.Track(PluginInterceptionRegistry.Register(this, _context, permission, callback));
    }
}

internal sealed record PluginAuthorization(PluginContext Context, long Revision, string Permission)
{
    internal void Demand()
    {
        Context.Permissions.Demand(Permission);
        if (Revision != Interlocked.Read(ref Context.AuthorizationRevision))
            throw new PluginOperationException("AuthorizationChanged", "插件授权已变更，请重新执行此操作。");
    }
}
internal sealed record PluginChain<T>(T Plan, ImmutableArray<PluginAuthorization> Authors)
{
    internal void Demand() { foreach (var author in Authors) author.Demand(); }
}

internal static class PluginInterceptionRegistry
{
    private sealed record Entry(long Order, PluginEventService Owner, PluginContext Context, string Permission, Delegate Callback);
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static long _order;
    internal static IDisposable Register(PluginEventService owner, PluginContext context, string permission, Delegate callback)
    {
        var entry = new Entry(Interlocked.Increment(ref _order), owner, context, permission, callback);
        lock (Gate) Entries.Add(entry);
        return new PluginRegistration(() => { lock (Gate) Entries.Remove(entry); });
    }
    private static Entry[] Snapshot(string permission)
    {
        lock (Gate) return Entries.Where(e => e.Permission == permission && !e.Owner.IsDisposed && e.Context.Permissions.Has(permission))
            .OrderByDescending(e => e.Context.Metadata.Priority).ThenBy(e => e.Context.PluginId, StringComparer.Ordinal).ThenBy(e => e.Order).ToArray();
    }
    internal static bool HasLaunchInterceptors => Snapshot(PluginPermissionNames.LaunchModify).Length > 0;
    internal static bool HasInstanceInterceptors => Snapshot(PluginPermissionNames.InstanceValidate).Length > 0;
    internal static async Task<PluginChain<T>> RunAsync<T>(string permission, T plan, Func<T, T, bool> validate, CancellationToken ct)
        where T : class
    {
        var authors = ImmutableArray.CreateBuilder<PluginAuthorization>();
        foreach (var entry in Snapshot(permission))
        {
            ct.ThrowIfCancellationRequested();
            var before = plan;
            var revision = Interlocked.Read(ref entry.Context.AuthorizationRevision);
            var result = await entry.Context.Callbacks.InvokeAsync(permission, "Intercept:" + typeof(T).Name,
                token => ((Func<T, CancellationToken, ValueTask<PluginInterception<T>>>)entry.Callback)(before, token),
                entry.Context.CallbackTimeout, ct).ConfigureAwait(false);
            if (!result.Success || result.Value == null) continue;
            if (result.Value.IsCanceled)
            {
                var reason = entry.Context.Safe(result.Value.Reason ?? Loc.T("Str_Plugin_NoReason", "未提供原因"));
                if (reason.Length > 512) reason = reason[..512];
                entry.Context.Log("InterventionCanceled: " + typeof(T).Name + "; " + reason);
                throw new PluginFlowCanceledException(entry.Context.PluginId, reason);
            }
            if (result.Value.Replacement == null) continue;
            if (!validate(before, result.Value.Replacement))
            { entry.Context.Log("InterventionRejected: " + typeof(T).Name + "; InvalidPlan"); continue; }
            plan = result.Value.Replacement;
            authors.Add(new(entry.Context, revision, permission));
            entry.Context.Log("InterventionApplied: " + typeof(T).Name + "; OperationRecorded");
        }
        return new(plan, authors.ToImmutable());
    }

    internal static async Task<string> PrepareDownloadAsync(string url, string fileName, string? sha1, CancellationToken ct)
    {
        // 有查询串、用户信息或已知凭据的地址不进入插件事件，防止签名 URL / 身份信息泄露。
        if (!IsPublicUrl(url)) return url;
        var plan = new PluginDownloadPlan(Guid.NewGuid(), url, PluginProviderRegistry.Safe(Path.GetFileName(fileName)), sha1);
        var result = await RunAsync(PluginPermissionNames.DownloadIntercept, plan,
            (before, after) => before.OperationId == after.OperationId && before.FileName == after.FileName &&
                before.ExpectedSha1 == after.ExpectedSha1 && IsPublicUrl(after.Url), ct).ConfigureAwait(false);
        result.Demand();
        return result.Plan.Url;
    }
    internal static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        (PluginHostBridge.Current == null || PluginHostBridge.Current.Safe(url) == url);
    internal static async Task ValidateInstanceAsync(string root, string name, string kind, CancellationToken ct)
    {
        var plan = new PluginInstanceCreationPlan(Guid.NewGuid(), PluginHostBridge.InstanceId(root, name), PluginProviderRegistry.Safe(name), kind);
        var result = await RunAsync(PluginPermissionNames.InstanceValidate, plan, (before, after) => before == after, ct).ConfigureAwait(false);
        result.Demand();
    }
    internal static bool ValidArguments(ImmutableArray<string> args, bool jvm)
    {
        if (args.IsDefault || args.Length > 4096) return false;
        return args.All(a => a != null && a.Length <= 8192 && a.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0 &&
            (PluginHostBridge.Current == null || PluginHostBridge.Current.Safe(a) == a) &&
            PluginArgumentSyntax.IsPublicArgument(a) && (!jvm || !a.StartsWith('@')) &&
            !(jvm ? new[] { "-cp", "-classpath", "--class-path", "-jar", "-m", "--module" } : ProtectedGameKeys)
                .Any(key => a.Equals(key, StringComparison.OrdinalIgnoreCase) || a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)));
    }
    internal static readonly string[] ProtectedGameKeys = { "--username", "--version", "--gameDir", "--assetsDir", "--assetIndex", "--uuid", "--accessToken", "--userType", "--userProperties", "--profileProperties", "--clientId", "--xuid" };
    internal static bool ValidEnvironment(ImmutableDictionary<string, string> values)
    {
        return values != null && values.Count <= 64 && values.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count && values.All(pair =>
            Regex.IsMatch(pair.Key, "\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z", RegexOptions.CultureInvariant) &&
            PluginArgumentSyntax.IsPublicArgument(pair.Key) &&
            !new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH", "PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR" }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) &&
            pair.Value != null && pair.Value.Length <= 8192 && pair.Value.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0 &&
            (PluginHostBridge.Current == null || PluginHostBridge.Current.Safe(pair.Value) == pair.Value));
    }
}
