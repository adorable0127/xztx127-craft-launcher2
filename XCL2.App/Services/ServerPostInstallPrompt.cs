using System.Windows;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 服务器核心下载/安装完成后的统一收尾：服务器的端口不再只能在下载（创建）时一次性填写，
/// 而是下载完成后再到「服务器设置」里随时设置/修改。这里在任务完成时询问是否现在就去设置，
/// 选"是"直接打开服务器设置窗口（端口、避让开关都在里面）。必须在 UI 线程调用。
/// </summary>
public static class ServerPostInstallPrompt
{
    public static void AskConfigurePort(MainWindow owner, ServerInstance instance, string headline)
    {
        owner.EnsureVisibleForDialog();
        var port = ServerPortService.ReadPort(instance);
        var yes = MessageBoxDialog.ShowConfirm(
            $"{headline}\n\n当前服务器端口：{port}（默认，被占用时会自动避让）。\n" +
            "是否现在去设置服务器端口？之后也可以随时在服务器列表的「服务器设置」里修改。",
            "设置服务器端口");
        if (!yes) return;

        var window = new ServerPropertiesWindow(instance, owner.ServerInstanceService, owner.ServerProcessManager);
        window.ShowDialog();
    }
}
