using System.Collections.Immutable;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Services;

public partial class LauncherService
{
    /// <summary>任何线程可调用；纯参数预览和插件回调完成后才允许导出脚本 / 启动进程。</summary>
    public async Task PrepareLaunchAsync(LaunchOptions options, CancellationToken ct = default)
    {
        options.PreparedPluginLaunch = null;
        options.PreparedPluginIdentity = null;
        if (!PluginInterceptionRegistry.HasLaunchInterceptors) return;
        var identity = LaunchIdentity(options);
        var raw = await Task.Run(() => BuildArguments(options, preview: true).args, ct).ConfigureAwait(false);
        var partition = PartitionArguments(raw);
        var plan = new PluginLaunchPlan(Guid.NewGuid(), PluginHostBridge.InstanceId(options.MinecraftDir, options.VersionId),
            partition.Jvm, partition.Game, ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));
        var chain = await PluginInterceptionRegistry.RunAsync(PluginPermissionNames.LaunchModify, plan,
            (before, after) => before.OperationId == after.OperationId && before.InstanceId == after.InstanceId &&
                PluginInterceptionRegistry.ValidArguments(after.JvmArguments, true) &&
                PluginInterceptionRegistry.ValidArguments(after.GameArguments, false) &&
                PluginInterceptionRegistry.ValidEnvironment(after.EnvironmentVariables), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (identity != LaunchIdentity(options)) throw new PluginOperationException("LaunchChanged", "启动选项已变更，请重新准备启动。");
        chain.Demand();
        options.PreparedPluginLaunch = chain;
        options.PreparedPluginIdentity = identity;
    }
    public async Task<Models.GameProcessInfo> LaunchAsync(LaunchOptions options, CancellationToken ct = default)
    {
        if (options.PreparedPluginLaunch == null) await PrepareLaunchAsync(options, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Launch(options);
    }
    private static string LaunchIdentity(LaunchOptions options) => string.Join("\n", options.MinecraftDir, options.VersionId,
        options.JavaPath, options.Account.Id, options.Account.Type, options.IsolateVersion, options.CustomJvmArgs);
    private static void DemandPreparedLaunch(LaunchOptions options)
    {
        if (options.PreparedPluginLaunch == null)
        {
            if (PluginInterceptionRegistry.HasLaunchInterceptors)
                throw new PluginOperationException("LaunchPreparationRequired", "请先异步准备启动参数。");
            return;
        }
        options.PreparedPluginLaunch.Demand();
        if (options.PreparedPluginIdentity != LaunchIdentity(options)) throw new PluginOperationException("LaunchChanged", "启动选项已变更，请重新准备启动。");
    }
    private sealed record Partition(ImmutableArray<string> Jvm, ImmutableArray<string> ProtectedJvm,
        ImmutableArray<string> Entry, ImmutableArray<string> Game, ImmutableArray<string> ProtectedGame);
    private static Partition PartitionArguments(List<string> arguments)
    {
        var cp = arguments.FindIndex(a => a == "-cp");
        if (cp < 0 || cp + 2 >= arguments.Count) throw new PluginOperationException("InvalidLaunchLayout", "启动入口参数不完整。");
        static (ImmutableArray<string> Public, ImmutableArray<string> Protected) Split(IEnumerable<string> values, bool jvm)
        {
            var tokens = values.ToArray(); var visible = ImmutableArray.CreateBuilder<string>(); var hidden = ImmutableArray.CreateBuilder<string>();
            for (var i = 0; i < tokens.Length; i++)
            {
                var group = new List<string> { tokens[i] };
                if (tokens[i].StartsWith('-') && !tokens[i].Contains('=') && i + 1 < tokens.Length && !tokens[i + 1].StartsWith('-')) group.Add(tokens[++i]);
                var target = PluginInterceptionRegistry.ValidArguments(group.ToImmutableArray(), jvm) ? visible : hidden;
                target.AddRange(group);
            }
            return (visible.ToImmutable(), hidden.ToImmutable());
        }
        var jvm = Split(arguments.Take(cp), true); var game = Split(arguments.Skip(cp + 3), false);
        return new(jvm.Public, jvm.Protected, arguments.Skip(cp).Take(3).ToImmutableArray(), game.Public, game.Protected);
    }
    private static List<string> ApplyPluginLaunchPlan(LaunchOptions options, List<string> arguments)
    {
        if (options.PreparedPluginLaunch == null) return arguments;
        var fixedArguments = PartitionArguments(arguments); var plan = options.PreparedPluginLaunch.Plan;
        var result = new List<string>(); result.AddRange(plan.JvmArguments); result.AddRange(fixedArguments.ProtectedJvm);
        result.AddRange(fixedArguments.Entry); result.AddRange(fixedArguments.ProtectedGame); result.AddRange(plan.GameArguments);
        return result;
    }
}
