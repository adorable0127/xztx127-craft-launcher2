namespace XCL2.App.Services;
public static class BuiltInAiDefaults
{
    public const string BaseUrl = "https://builtin.example/v1";
    public static int Fetches { get; private set; }
    public static string GetApiKey() { Fetches++; return "builtin-test-only"; }
    public static Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(GetApiKey());
}
