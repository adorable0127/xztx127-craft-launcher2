namespace XCL2.App.Services;

// 测试使用真实页面/XAML/浏览器服务；仅隔离启动器全局通知和磁盘日志。
internal static class ToastService
{
    public static List<string> Warnings { get; } = new();
    public static void ShowWarning(string message) => Warnings.Add(message);
    public static void ShowSuccess(string message) { }
}

internal static class ErrorPresenter
{
    public static void LogTechnicalDetail(string message) { }
}

internal static class MessageBoxDialog
{
    public static void ShowWarning(string message, string title) => throw new InvalidOperationException("不应打开阻塞弹窗：" + message);
    public static void ShowError(string message, string title) => throw new InvalidOperationException("不应打开阻塞弹窗：" + message);
}
