using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>尚未处于下载队列中的 Java 入口使用此适配器。已由创建服务器/快捷启动登记的任务无需嵌套。</summary>
public static class JavaDownloadQueueService
{
    public static Task<string> DownloadRecommendedAsync(JavaService service,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        DownloadAsync(service, new JavaDownloadRequest(21,
            Environment.Is64BitOperatingSystem ? "x64" : "x86", JavaInstallMode.Portable), progress, ct);

    public static Task<string> DownloadAsync(JavaService service, JavaDownloadRequest request,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        DownloadQueueService.Instance.RunAndWaitAsync($"下载 Java {request.MajorVersion} ({request.Arch})",
            async (item, token) =>
            {
                // 系统安装仍允许调用方在下载阶段取消；MSI 一旦启动，由 JavaService 等它真实退出。
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                var queueProgress = item.CreateProgress();
                var combined = new Progress<ProgressInfo>(info =>
                {
                    queueProgress.Report(info);
                    progress?.Report(info);
                });
                return await service.DownloadJavaAsync(request, combined, linked.Token);
            }, ct, supportsCancel: request.InstallMode != JavaInstallMode.System);
}
