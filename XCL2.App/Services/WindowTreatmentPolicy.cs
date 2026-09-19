using System.Windows;

namespace XCL2.App.Services;

/// <summary>
/// 哪些窗口**必须**被全局窗口外观处理（深色标题栏 / Win11 云母·亚克力材质 / 整窗 alpha /
/// 主题刷新遍历）跳过。
///
/// 为什么需要这个东西——这是"触屏悬浮层显示成一整块灰色方块、按钮和透明度全没了"的根因：
///
/// App.xaml.cs 里用 EventManager.RegisterClassHandler 在 Window 这一级注册了三个类处理器，
/// 项目里**每一个** Window 子类 Loaded 时都会被套上：
///   ① WindowChromeService.ApplyTitleBarTheme（DWM 深色标题栏）
///   ② Win11EffectsService.Apply（DWMWA_SYSTEMBACKDROP_TYPE + DwmExtendFrameIntoClientArea(-1)）
///   ③ ThemeService.ApplyGlobalOpacityToWindow（WS_EX_LAYERED + SetLayeredWindowAttributes）
///
/// 这三条的注释里都写着同一个前提假设："项目里所有窗口都保持 AllowsTransparency=False"。
/// TouchOverlayWindow 是全项目唯一一个 AllowsTransparency=True 的窗口（它必须逐像素透明，
/// 否则没法只显示按钮、让游戏画面从空白处透出来），这个前提在它身上不成立，于是：
///
/// ②会让 DWM 把整个客户区当成材质区域去合成，直接在悬浮层底下铺一层不透明的系统背景板
///   （还带上了 DWMWCP_ROUND 圆角——截图里那个灰块四角是圆的，就是这一步的指纹）。
/// ③更致命：AllowsTransparency=True 的窗口，WPF 自己就是靠 WS_EX_LAYERED + UpdateLayeredWindow
///   做逐像素 alpha 的。全局透明关闭时，ApplyNativeGlobalOpacity 走的是 else 分支——它看到
///   WS_EX_LAYERED 存在，就"好心"地把 alpha 重置成 255 再把 WS_EX_LAYERED 样式摘掉，
///   等于把 WPF 赖以工作的透明机制连根拔了。结果窗口既不透明、也没有任何内容被合成上去，
///   显示出来就是一整块没有画面的灰色方块——正是用户截图里的样子。
///
/// 所以这里不是"给悬浮层加特例"，而是修正一条本来就该有的边界：逐像素透明的分层窗口
/// 跟这三套基于 DWM/Win32 整窗合成的处理天生互斥，必须被全部跳过。用 AllowsTransparency
/// 而不是写死类型名来判断，是为了以后再加任何透明浮层窗口时自动受保护，不会重蹈覆辙。
/// </summary>
public static class WindowTreatmentPolicy
{
    /// <summary>该窗口是否应当被所有全局窗口外观处理跳过。</summary>
    public static bool IsExempt(Window? window)
    {
        if (window == null) return true;
        try
        {
            // 逐像素透明窗口（AllowsTransparency=True）永远跳过，原因见类注释。
            if (window.AllowsTransparency) return true;
        }
        catch { /* 极端情况下读 DP 失败，按不跳过处理，保持原有行为 */ }

        // 显式声明豁免的窗口（即使将来因为某些原因不再需要 AllowsTransparency，
        // 也不希望被全局处理插手）。
        return window is IGlobalWindowTreatmentExempt;
    }
}

/// <summary>
/// 显式声明"不要对我套用任何全局窗口外观处理"的标记接口，语义见
/// <see cref="WindowTreatmentPolicy"/>。TouchOverlayWindow 实现它。
/// </summary>
public interface IGlobalWindowTreatmentExempt
{
}
