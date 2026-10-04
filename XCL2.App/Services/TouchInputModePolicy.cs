namespace XCL2.App.Services;

/// <summary>鼠标抓取优先于系统残留光标；未知样本不切换模式。</summary>
public static class TouchInputModePolicy
{
    public static bool? Classify(bool confined, bool wholeDesktop, bool hasCursor,
        bool showing, bool suppressed, bool allowFullscreenCapture)
    {
        // 触摸/WPF 可能留下箭头句柄甚至 SHOWING 标志，客户区抓取仍表示游戏视角。
        if (confined && !wholeDesktop) return false;
        if (confined && wholeDesktop && !hasCursor && allowFullscreenCapture) return false;
        // SUPPRESSED 表示 Windows 因触摸隐藏光标，不能拿箭头句柄推断游戏菜单。
        if (suppressed) return hasCursor ? null : false;
        // LWJGL 2 / 旧 GLFW 会隐藏并不断回中光标，不一定使用 ClipCursor。
        if (!showing) return false;
        if (hasCursor && showing && (!confined || wholeDesktop)) return true;
        return null;
    }
}
