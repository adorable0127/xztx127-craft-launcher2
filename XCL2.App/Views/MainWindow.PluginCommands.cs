using System.IO;
using System.Text.Json;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private TaskCompletionSource<LauncherCommandResult>? _startupLaunchResult;
    private async Task ExecuteStructuredStartupAsync(CommandLineService.ParsedArgs args)
    {
        var path = args.ResultFile ?? Path.Combine(Environment.CurrentDirectory, "xcl2-result.json");
        try
        {
            if (args.ListInstances || args.ListAccounts)
            {
                var host = PluginHostBridge.Current ?? throw new PluginHostUnavailableException();
                object data;
                if (args.ListInstances && args.ListAccounts)
                    data = new { instances = await host.GetInstancesAsync(new(true), CancellationToken.None), accounts = await host.GetAccountsAsync(CancellationToken.None) };
                else if (args.ListInstances) data = await host.GetInstancesAsync(new(true), CancellationToken.None);
                else data = await host.GetAccountsAsync(CancellationToken.None);
                await LauncherCommandResultWriter.WriteAsync(path, new(1, "query", "Completed", "Ok", Data: JsonSerializer.SerializeToElement(data, PluginPipeSession.Json)));
                return;
            }
            if (args.LaunchGame)
            {
                if (args.ResultFile == null) { ApplyStartupLaunch(args.AccountName, args.InstanceName); return; }
                var completion = new TaskCompletionSource<LauncherCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                ApplyStartupLaunch(args.AccountName, args.InstanceName, completion);
                await LauncherCommandResultWriter.WriteAsync(path, await completion.Task);
            }
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[Automation] ResultFailed: " + ex.GetType().Name);
            try { await LauncherCommandResultWriter.WriteAsync(path, new(1, "automation", "Failed", ex is PluginOperationException operation ? operation.Code : "OperationFailed")); }
            catch (Exception writeError) { LauncherLogService.AppendLine("[Automation] ResultWriteFailed: " + writeError.GetType().Name); }
        }
    }
}
