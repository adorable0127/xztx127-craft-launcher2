namespace XCL2.App.Models
{
    public sealed class VersionDetail
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
    }
}
namespace XCL2.App.Services
{
    internal static class LauncherService
    {
        internal static string? ResolveVersionFile(string dir, string id, string extension)
        { var path = System.IO.Path.Combine(dir, id + "." + extension); return System.IO.File.Exists(path) ? path : null; }
    }
    internal static class ErrorPresenter { internal static void LogFallback(string message, Exception ex) { } }
}
