namespace XCL2.App.Models
{
    public enum DownloadSource { Official, BMCLAPI }
    public sealed class AppConfig
    {
        public DownloadSource Source { get; set; }
        public DownloadMirrorConfig DownloadMirrors { get; set; } = new();
        public int DownloadSpeedLimitKBps { get; set; }
    }
}
namespace XCL2.App.Services
{
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; set; } = new();
        public Models.AppConfig Config { get; } = new();
    }
    public record ProgressInfo(string Stage, int Done, int Total, string CurrentFile);
}
namespace XCL2.App.Services.Plugins
{
    public sealed class PluginFlowCanceledException : Exception { }
    public static class PluginProviderRegistry
    {
        public static Task<IReadOnlyList<string>> AddMirrorAsync(string url, string name, string? sha1, IReadOnlyList<string> urls, CancellationToken ct)
            => Task.FromResult(urls);
    }
    public static class PluginInterceptionRegistry
    {
        public static Task<string> PrepareDownloadAsync(string url, string name, string? sha1, CancellationToken ct) => Task.FromResult(url);
    }
}
