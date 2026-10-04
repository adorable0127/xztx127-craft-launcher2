namespace XCL2.App.Views;

/// <summary>仅统一悬浮窗生命周期，两个版本分支的输入实现彼此独立。</summary>
public interface ITouchOverlayWindow
{
    event EventHandler? Closed;
    void Show();
    void ShutdownOverlay();
}
