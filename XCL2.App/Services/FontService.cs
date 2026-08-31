using System.Linq;
using System.Windows;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// "字体分层/分块设置"：在 ThemeService.ApplyFontFamily 维护的全局界面字体
/// （AppConfig.AppFontFamily，固定几个预置候选）之上，额外提供"选择本机已安装的
/// 任意系统字体"，并且可以按标题栏 / 侧边栏 / 内容区三个区块分别单独指定，不用
/// 三个区块必须用同一款字体。
///
/// 实现原理：FontFamily 是 WPF 里的继承型依赖属性，DynamicResource 在 Style 里的
/// 查找是"从使用该 Style 的元素本身出发，沿着它的逻辑树往上找同名 key，找到最近的
/// 那一层就用哪一层的值"——不是固定查 Style 定义所在的那份字典。ThemeService 已经
/// 给全局 Control/TextBlock 装了一个隐式 Style，FontFamily 绑定到 DynamicResource
/// "AppFontFamily"（定义在 Application.Resources 里，全应用共享）。
/// 这里只需要在某个区块的根容器（比如 MainWindow.SidebarAreaBorder）自己的
/// Resources 字典里放一份同名的 "AppFontFamily" 局部覆盖，那个区块内部的所有
/// 控件在往上找这个 key 时会先找到区块自己的这一份，而不是继续往上找到全局的那份，
/// 从而实现"只有这个区块换字体，其它区块不受影响"。不需要覆盖时，直接把区块自己
/// Resources 里的这个 key 移除即可，会自动回退成"跟随全局"。
/// </summary>
public static class FontService
{
    /// <summary>本机已安装的系统字体家族名列表，按显示名称排序、去重，供设置页三个
    /// "分区字体"下拉框展示。取 Windows 当前 UI 文化下的本地化名字（比如"微软雅黑"
    /// 而不是"Microsoft YaHei"），用户更容易认得。</summary>
    public static IReadOnlyList<string> GetInstalledFontFamilyNames()
    {
        return Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>启动时 / 设置页保存后统一调用一次，把 AppConfig 里三个分区字段应用到
    /// MainWindow 对应的三个容器上。传入 null/空字符串表示该分区不覆盖，跟随全局字体。</summary>
    public static void ApplyScopedFonts(MainWindow window, AppConfig cfg)
    {
        ApplyToContainer(window.CustomTitleBar, cfg.AppFontFamily_TitleBar);
        ApplyToContainer(window.SidebarAreaBorder, cfg.AppFontFamily_Sidebar);
        ApplyToContainer(window.ContentAreaBorder, cfg.AppFontFamily_Content);
    }

    private static void ApplyToContainer(FrameworkElement container, string? fontFamilyName)
    {
        const string key = "AppFontFamily";
        if (string.IsNullOrWhiteSpace(fontFamilyName))
        {
            // "跟随全局"：这个区块自己的 Resources 里不应该留一份旧的覆盖值，
            // 否则即使用户改回"跟随全局"也不会生效（旧值仍然会被优先找到）。
            if (container.Resources.Contains(key))
            {
                container.Resources.Remove(key);
            }
            return;
        }

        container.Resources[key] = new FontFamily(fontFamilyName);
    }
}
