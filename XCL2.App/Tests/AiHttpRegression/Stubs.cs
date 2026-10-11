using XCL2.App.Models;
namespace XCL2.App.Services
{
    // Only platform/UI integrations are stubbed; HTTP routing, SSE, accounting and protocols are production code.
    public static class AiPersonas { public static string BuildExtraPromptSection(string id, string? prompt) => ""; }
    public static class DefaultSystemPrompt { public const string Text = "You are a test assistant."; }
    public static class AiQuickAnswers { public static string? TryAnswer(string text) => null; }
    public static class AiShellPolicy { public static string BuildPrompt(AiAssistantConfig config) => ""; }
    public static class BuiltInAiDefaults
    {
        public const string BaseUrl = "https://builtin.example/v1";
        public static int Reads;
        public static string GetApiKey() { Reads++; return "builtin-secret"; }
        public static Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(GetApiKey());
        public static void InvalidateCache() { }
    }
    public static class ToastService
    {
        public static List<string> Warnings { get; } = new();
        public static void ShowWarning(string text) => Warnings.Add(text);
        public static void ShowInfo(string text) { }
    }
    public class ConfigService
    {
        public static ConfigService? Active { get; set; }
        public Holder Config { get; } = new();
        public int Saves;
        public void Save() => Saves++;
        public sealed class Holder { public AiAssistantConfig AiAssistant { get; set; } = new(); }
    }
    public static class AiFileAccessService
    {
        internal static XCL2.App.Services.Workbench.ChangeJournal Changes => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xcl-test-changes"));
        public delegate bool ConfirmCallback(object request, object information);
        public static List<string> GetApprovedRoots() => new();
        public static bool IsFullAccess() => false;
        public static bool IsUnrestrictedAccess() => false;
        public static bool IsAbsoluteAccess() => false;
        public static bool IsSystemControlAccess() => false;
        public static bool HasAccessRequest(string text) => text.Contains("<<XCL2_ACCESS:");
        public static string StripAccessMarkers(string text) => text.Split("<<XCL2_ACCESS:")[0];
        public static int DispatchCalls;
        public static List<AiWorkMode> DispatchedModes { get; } = new();
        public static Task<(string, string?)> ProcessReplyAsync(string text, ConfirmCallback cb,
            Action<AiAssistantActivity>? progress, CancellationToken ct, AiWorkModeContext? workMode = null, Func<XCL2.App.Services.Workbench.FileChange, XCL2.App.Models.AiFileDiff, bool>? review = null)
        {
            DispatchCalls++;
            DispatchedModes.Add(workMode?.EffectiveMode ?? AiWorkMode.Build);
            return Task.FromResult<(string, string?)>((StripAccessMarkers(text), "工具已完成"));
        }
    }
}
namespace System.Windows
{
    public class Application { public static Application? Current => null; public Dispatcher Dispatcher => new(); }
    public class Dispatcher { public bool CheckAccess() => true; public void Invoke(Action action) => action(); }
}
