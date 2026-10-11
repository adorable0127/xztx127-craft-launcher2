using XCL2.App.Services.Plugins;

// 直接覆盖生产路径策略；不启动宿主、不访问网络、不运行外部程序。
var checks = 0;
foreach (var name in new[] { "1.21.1", "My Fabric 实例", "sodium.jar", "sodium.jar.disabled" })
{ PluginManagedPaths.RequireLeafName(name); checks++; }
foreach (var name in new[] { "", ".", "..", "../outside", "..\\outside", "C:evil", "file:stream.jar", "trailing.", "trailing ", "CON", "NUL.jar", "COM1.zip", "LPT9", "bad?name", "bad|name", "line\nname" })
{ Reject(() => PluginManagedPaths.RequireLeafName(name), "InvalidArgument"); checks++; }
var root = Path.Combine(Path.GetTempPath(), "xcl-plugin-path-test-" + Guid.NewGuid().ToString("N"));
var outside = root + "-outside";
Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
try
{
    PluginManagedPaths.RequireNoLinks(root, root);
    PluginManagedPaths.RequireNoLinks(root, Path.Combine(root, "versions", "1.21.1", "mods", "sodium.jar"));
    Reject(() => PluginManagedPaths.RequireNoLinks(root, Path.Combine(root, "..", "outside", "file.jar")), "InvalidPath");
    Reject(() => PluginManagedPaths.RequireNoLinks(root, Path.Combine(outside, "file.jar")), "InvalidPath");
    checks += 4;
    // Windows 创建联接可能需要系统权限；仅在系统允许创建测试链接时验证，不提升权限。
    var link = Path.Combine(root, "linked");
    var linked = false;
    try { Directory.CreateSymbolicLink(link, outside); linked = true; }
    catch (Exception e) when (e is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { }
    if (linked)
    {
        Reject(() => PluginManagedPaths.RequireNoLinks(root, Path.Combine(link, "file.jar")), "LinkedPath");
        Directory.Delete(link); checks++;
    }
    Console.WriteLine($"Plugin management path checks passed: {checks}");
}
finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
static void Reject(Action action, string code)
{
    try { action(); }
    catch (PluginOperationException e) when (e.Code == code) { return; }
    throw new Exception("Expected rejection: " + code);
}

namespace XCL2.App.Services.Plugins
{
    // 测试宿主桩只提供错误数据契约；被测路径检查直接链接生产源文件。
    internal sealed class PluginOperationException(string code, string message) : Exception(message)
    { internal string Code { get; } = code; }
}
