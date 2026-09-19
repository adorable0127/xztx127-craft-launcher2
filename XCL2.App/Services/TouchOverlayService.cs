using System.Windows;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 触屏模式的接入口：负责在游戏窗口真正出现之后，把"套娃层"(<see cref="TouchOverlayWindow"/>)
/// 贴上去，并在游戏进程退出时自动收掉。
///
/// 为什么要轮询等窗口：Process.Start 返回的时候 java 进程刚起来，游戏窗口还不存在
/// （要等 JVM 起来、资源加载完，慢的机器上几十秒都有可能），MainWindowHandle 这时是 0。
/// 直接建悬浮层会贴到一个不存在的窗口上。这里最多等 3 分钟，每 500ms 探一次，
/// 拿到有效句柄再建层；期间游戏要是崩了/被关了就直接放弃，不留任何残留窗口。
///
/// 这是纯附加功能：任何一步失败都只记日志，绝不能影响游戏本身的启动和运行。
/// </summary>
public class TouchOverlayService
{
    private static readonly List<TouchOverlayWindow> Active = new();

    /// <summary>给一个刚启动的游戏进程挂上触屏悬浮层（异步等待窗口出现，不阻塞调用方）。</summary>
    public static void Attach(GameProcessInfo info, AppConfig cfg)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                IntPtr hwnd = IntPtr.Zero;

                while (DateTime.UtcNow < deadline)
                {
                    if (info.HasExited) return; // 游戏已经退出/崩了，不用贴了

                    try
                    {
                        info.Process.Refresh();
                        hwnd = info.Process.MainWindowHandle;
                    }
                    catch { hwnd = IntPtr.Zero; }

                    if (hwnd != IntPtr.Zero) break;
                    await Task.Delay(500);
                }

                if (hwnd == IntPtr.Zero)
                {
                    LauncherLogService.AppendLine("[触屏模式] 等待游戏窗口超时，未能加载触屏悬浮层。");
                    return;
                }

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var overlay = new TouchOverlayWindow(
                        hwnd,
                        cfg.TouchOverlayButtonScale,
                        cfg.TouchOverlayOpacityPercent,
                        cfg.TouchOverlayLookSensitivity);
                    overlay.Show();
                    Active.Add(overlay);
                    LauncherLogService.AppendLine($"[触屏模式] 已为版本 {info.VersionId} 加载触屏悬浮层。");

                    // 游戏退出后自动关掉悬浮层（Exited 事件在后台线程触发，切回 UI 线程操作窗口）。
                    info.Process.Exited += (_, _) =>
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            overlay.ShutdownOverlay();
                            Active.Remove(overlay);
                        });
                    };
                });
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogTechnicalDetail($"[触屏悬浮层加载失败，不影响游戏运行]\n{ex}");
            }
        });
    }

    /// <summary>当前是否有至少一个触屏悬浮层正在运行——关闭启动器前用来判断要不要弹出
    /// 触屏模式专属的关闭确认，见 MainWindow.MainWindow_Closing。</summary>
    public static bool HasActive => Active.Count > 0;

    /// <summary>关闭当前所有悬浮层（启动器退出时调用，避免留下孤儿置顶窗口）。</summary>
    public static void CloseAll()
    {
        foreach (var w in Active.ToList())
        {
            try { w.ShutdownOverlay(); } catch { /* 尽力而为 */ }
        }
        Active.Clear();
    }
}
