using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

public sealed record LauncherCommandResult(int SchemaVersion, string Operation, string Status, string Code,
    int? Pid = null, string? InstanceId = null, string? AccountId = null, JsonElement? Data = null);
internal static class LauncherCommandResultWriter
{
    internal static async Task WriteAsync(string path, LauncherCommandResult result)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var safe = JsonSerializer.Serialize(result, Plugins.PluginPipeSession.Json);
            safe = Plugins.PluginHostBridge.Current?.Safe(safe) ?? safe;
            await File.WriteAllTextAsync(temporary, safe); File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
