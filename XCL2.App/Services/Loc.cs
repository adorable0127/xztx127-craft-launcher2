using System.Windows;

namespace XCL2.App.Services;

/// <summary>
/// C# 代码里取本地化文案的入口。
///
/// ===== 为什么 XAML 用 DynamicResource、C# 却需要这个类 =====
/// XAML 里写 {DynamicResource Str_Xxx} 由 WPF 自己负责查表和语言切换时刷新。
/// 但 code-behind 里的 `SomeText.Text = "未选择账户"` 这类赋值是**一次性**的，
/// 没有绑定关系，切换语言时不会自己更新，也没法直接写 DynamicResource。
/// 这个类提供两件事：
///   1. T(key) —— 按当前语言查表取字符串；
///   2. 查不到时**回退到传入的中文原文**，而不是显示 key 名或空白。
///
/// ===== 回退设计（关键）=====
/// 全项目 C# 里还有一千多条中文没抽出来，翻译是分批推进的。
/// 每个调用点都写成 `Loc.T("Str_Xxx", "未选择账户")` 的形式：
///   - key 已经翻译 → 显示当前语言的译文
///   - key 还没加进语言文件 → 显示第二个参数里的中文原文
/// 这样**替换过程中的任何中间状态都不会让界面出现空白或 key 名**，
/// 可以放心一批一批改，不需要一次性全部改完才敢编译。
///
/// 这跟 LocalizationService.Apply 里"英文打底"的回退链是两层不同的兜底：
/// 那一层管的是"语言文件之间"的缺失，这一层管的是"key 压根还没建"的缺失。
/// </summary>
public static class Loc
{
    /// <summary>
    /// 取本地化字符串。key 查不到时返回 fallback（一般传中文原文）。
    ///
    /// 注意这里用 Application.Current.TryFindResource 而不是 FindResource：
    /// 后者查不到会抛 ResourceReferenceKeyNotFoundException，
    /// 而"key 还没建"在分批翻译期间是**正常状态**，不该抛异常。
    /// </summary>
    public static string T(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is string s && !string.IsNullOrEmpty(s))
                return s;
        }
        catch
        {
            // 极早期（Application 还没建好）或资源字典异常时静默回退，
            // 绝不能因为取一句文案失败就把主流程带崩。
        }
        return fallback;
    }

    /// <summary>
    /// 带格式化参数的版本，对应原来的 $"..." 插值字符串。
    ///
    /// 插值字符串没法直接做本地化（不同语言的语序不同，占位符位置会变），
    /// 所以抽 key 时要把 $"游戏已启动：{name}" 改写成
    /// Loc.F("Str_X", "游戏已启动：{0}", name)，让译文自己决定 {0} 放哪。
    /// 格式化失败（译文里占位符数量对不上）时回退用 fallback 再格式化一次，
    /// 再失败就直接返回 fallback 原文——宁可显示未替换的模板，也不要抛异常。
    /// </summary>
    public static string F(string key, string fallbackFormat, params object?[] args)
    {
        var fmt = T(key, fallbackFormat);
        try
        {
            return string.Format(fmt, args);
        }
        catch (FormatException)
        {
            try { return string.Format(fallbackFormat, args); }
            catch { return fallbackFormat; }
        }
    }
}
