namespace System.Windows
{
    public class Application
    {
        public static Application? Current => null;
        public object? MainWindow => null;
        public Dispatcher? Dispatcher => null;
    }
    public class Dispatcher
    {
        public bool CheckAccess() => true;
        public void Invoke(Action action) => action();
        public void BeginInvoke(Action action) => action();
    }
}
namespace XCL2.App.Models
{
    public class AppConfig
    {
        public int DownloadPopupSizeDisplayMode { get; set; }
        public bool GameVersionNoPopup { get; set; }
        public bool CommunityResourceNoPopup { get; set; }
        public bool ModpackNoPopup { get; set; }
        public int DownloadNotifyMode { get; set; }
    }
}
namespace XCL2.App.Services
{
    public record ProgressInfo(string Stage, int Done, int Total, string CurrentFile);
    public class ConfigService { public Models.AppConfig Config { get; } = new(); }
    public static class ToastService
    {
        public static void ShowInfo(string text) { }
        public static void ShowSuccess(string text) { }
        public static void ShowSystemNotification(string text, string title) { }
    }
}
namespace XCL2.App.Views
{
    public class MainWindow { public Services.ConfigService ConfigService { get; } = new(); }
}
