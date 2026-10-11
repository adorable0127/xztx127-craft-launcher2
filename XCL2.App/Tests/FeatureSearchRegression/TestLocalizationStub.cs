namespace XCL2.App.Services;

// The pure regression project does not load WPF. Production links the actual Loc service.
internal static class Loc
{
    public static string T(string key, string fallback) => fallback;
}
