using System.Diagnostics;
using System.Windows;

namespace XCL2.App.Services;

/// <summary>
/// 启动器界面语言（不是游戏内语言，见 AppConfig.GameLanguage 上的注释和
/// Resources/Lang/README.md 的"这是什么，不是什么"一节，两者不要混）。
///
/// 实现思路跟 ThemeService（配色/明暗切换）保持一致，保证同一个项目里"切换视觉状态"
/// 这件事只有一套心智模型：
/// - 每种语言是一份独立的 ResourceDictionary（Resources/Lang/Lang.&lt;code&gt;.xaml），
///   key 相同、value 是该语言的文案。
/// - 切换语言 = 把 Application.Resources.MergedDictionaries 里"语言那一份"整个替换掉，
///   而不是逐个修改字符串资源的值——WPF 的 DynamicResource 在整份字典被替换时会重新
///   查找，这是最简单可靠、不需要额外 Freeze/未 Freeze 处理的方式（字符串本身不是
///   Freezable，不存在 ThemeService 里画刷被 Seal 冻结那个坑，比配色切换还简单一些）。
/// - 换语言之后同样调用 ThemeService 里那套"遍历所有已打开窗口强制刷新"的逻辑，
///   保证已经渲染出来的窗口能立即看到新文案，不需要用户重新打开页面/重启程序。
///
/// 当前覆盖范围：只有首页(HomePage) + 顶部窗口标题/侧边导航(MainWindow) +
/// 语言选择弹窗自身 + 设置页新增的"启动器界面语言"区块这几处用了资源键。
/// 其余几十个页面/弹窗目前仍是硬编码中文，尚未资源化——这是当前阶段有意为之的范围，
/// 不是遗漏。后续要继续扩展覆盖范围，请看 Resources/Lang/README.md 的操作步骤。
/// </summary>
public static class LocalizationService
{
    /// <summary>一种受支持的启动器界面语言：Code 是 BCP-47 风格的语言代码（同时也是
    /// Lang.&lt;Code&gt;.xaml 文件名的一部分），NativeName 是"用该语言自己书写"的显示名
    /// （语言选择弹窗按这个展示，不做二次翻译——参照 Windows/主流系统的通行做法，
    /// 用户找自己母语时认的是母语拼写本身，不是当前界面语言翻译过的名字）。</summary>
    public sealed record LanguageOption(string Code, string NativeName);

    /// <summary>
    /// 受支持的语言列表，顺序即语言选择弹窗里的显示顺序。
    /// 新增一种语言时：① 在这里加一行，② 在 Resources/Lang/ 下新建 Lang.&lt;code&gt;.xaml
    /// 且 key 集合要跟 Lang.zh-Hans.xaml 完全一致（否则下面的调试期校验会报警告）。
    /// </summary>
    public static readonly LanguageOption[] SupportedLanguages =
    {
        new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁體中文"),
        new("yue-Hant", "粵語（繁體）"),
        new("en-US", "English (United States)"),
        new("en-GB", "English (United Kingdom)"),
        new("de-DE", "Deutsch (Deutschland)"),
        new("fr-FR", "Français (France)"),
        new("it-IT", "Italiano (Italia)"),
        new("sv-SE", "Svenska (Sverige)"),
        new("ja-JP", "日本語（日本）"),
        new("ko-KR", "한국어(대한민국)"),
    };

    public const string DefaultLanguageCode = "zh-Hans";

    /// <summary>
    /// 只有这个语言代码的界面，「实验性功能」入口才会显示（见 HomePage/MainWindow 里
    /// 对 Str_Nav_Experimental 相关按钮显隐的判断）。原话："那个实验性功能仅对简体中文
    /// 开放，其他语言不翻译"——不是禁止其它语言用户使用该功能，只是这批功能本身还没有
    /// 多语言界面，贸然展示给不懂中文的用户体验反而更差，所以先隐藏而不是显示未翻译的中文。
    /// </summary>
    public const string ExperimentalFeaturesLanguageGate = "zh-Hans";

    /// <summary>当前生效的语言代码。默认简体中文；ApplyForCurrentState 会同步更新这个值。</summary>
    public static string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

    /// <summary>语言切换完成后触发（在窗口刷新之后）。code-behind 里如果自己缓存过某个
    /// 字符串资源的值（没有用 DynamicResource 绑定，而是手动 GetString 取了一次），
    /// 应该订阅这个事件、在回调里重新取值刷新自己缓存的那份。</summary>
    public static event Action? LanguageChanged;

    /// <summary>兜底语言：任何语言缺 key 时都用英文补（见 Apply 里的回退链）。\n    /// 选英文而不是中文，是为了让非中文用户在遇到未翻译条目时至少能读懂。</summary>
    private const string FallbackLanguageCode = "en-US";

    private const string DictPathPrefix = "/Resources/Lang/Lang.";
    private const string DictPathSuffix = ".xaml";

    /// <summary>
    /// 根据配置里持久化的语言代码应用界面语言。传入非法/不支持的代码时兜底为简体中文，
    /// 不让配置文件被手改坏了之后直接崩溃或者显示成一堆资源键找不到的占位符。
    /// </summary>
    public static void ApplyForCurrentState(string? persistedLanguageCode)
    {
        var code = persistedLanguageCode;
        if (code is null || Array.TrueForAll(SupportedLanguages, l => l.Code != code))
        {
            code = DefaultLanguageCode;
        }

        Apply(code);
    }

    private static void Apply(string code)
    {
        var dictUri = new Uri($"{DictPathPrefix}{code}{DictPathSuffix}", UriKind.Relative);
        ResourceDictionary newDict;
        try
        {
            newDict = new ResourceDictionary { Source = dictUri };
        }
        catch
        {
            // 万一某个语言的 xaml 文件丢失/损坏，兜底回简体中文，而不是让整个启动器崩溃在
            // 语言切换这一步——界面语言这种非核心功能不应该有能力让程序完全打不开。
            if (code == DefaultLanguageCode) throw; // 连默认语言都加载不了，那是真正的部署问题，不应该静默吞掉
            Apply(DefaultLanguageCode);
            return;
        }

        DebugValidateKeys(newDict, code);

        // ===== 缺失 key 的回退链：目标语言 → 英文(en-US) → 简体中文 =====
        // 全项目有 1700+ 条界面文案，翻译分批推进，非中文语言必然长期存在
        // "某些 key 还没翻"的中间状态。不处理的话 DynamicResource 查不到 key，
        // 界面上会直接显示成**空白**（比显示 key 名还糟——用户看到没字的按钮）。
        //
        // 回退顺序是有意这样排的（按用户要求：其他语言兜底用英文）：
        //   1) 先铺简体中文做最底层——它是 key 集合的权威基准，保证任何 key 都有值，
        //      永远不会出现空白控件；
        //   2) 再铺英文覆盖上去——对一个看德语/日语界面的用户来说，
        //      看到英文远比看到中文有用；
        //   3) 最后铺目标语言覆盖——翻了的显示本语言。
        // 结果：翻了→本语言，没翻但有英文→英文，英文也没有→中文。
        //
        // en-US 自己加载时跳过第 2 步（自己覆盖自己没意义）。
        if (code != DefaultLanguageCode)
        {
            try
            {
                var mergedDict = new ResourceDictionary();

                void Overlay(string langCode)
                {
                    try
                    {
                        var d = new ResourceDictionary
                        {
                            Source = new Uri($"{DictPathPrefix}{langCode}{DictPathSuffix}", UriKind.Relative)
                        };
                        foreach (var key in d.Keys) mergedDict[key] = d[key];
                    }
                    catch { /* 某一层读不到就跳过，不影响其它层 */ }
                }

                // 回退链自下而上：英文打底 → （仅中文变体额外铺简体中文）→ 目标语言覆盖。
                //
                // 为什么用英文而不是中文打底：一个德国/日本/瑞典用户碰到还没翻译的条目，
                // 看到英文至少能猜出这个按钮干什么；看到中文完全无从下手。
                // 繁体/粤语是例外——它们跟简体的重合度远高于英文，所以在英文之上
                // 再铺一层简体，未翻条目显示简体中文比显示英文更贴近这批用户的习惯。
                Overlay(FallbackLanguageCode);                                  // 1. 英文底
                if (code is "zh-Hant" or "yue-Hant") Overlay(DefaultLanguageCode); // 2. 中文变体额外铺简体
                foreach (var key in newDict.Keys) mergedDict[key] = newDict[key];  // 3. 目标语言

                newDict = mergedDict;
            }
            catch
            {
                // 整个回退链构建失败就用原样的目标语言字典，不影响主流程。
            }
        }

        var merged = Application.Current.Resources.MergedDictionaries;

        // 找到当前已经挂载的语言字典（通过给它一个约定的 x:Key 标记来识别，见下方
        // "__XCL2_LangDictMarker__" 用法）并整份替换掉；第一次调用时(启动时)集合里
        // 还没有语言字典，直接添加。
        var oldIndex = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Contains(LangDictMarkerKey))
            {
                oldIndex = i;
                break;
            }
        }

        newDict[LangDictMarkerKey] = code; // 标记 + 顺便记录当前是哪个语言，供上面下次查找

        if (oldIndex >= 0) merged[oldIndex] = newDict;
        else merged.Add(newDict);

        CurrentLanguageCode = code;

        // 跟 ThemeService.RefreshOpenWindows 是完全同一份逻辑：DynamicResource 引用字符串
        // 的控件在样式 Seal 后同样需要"摘掉 Style 再装回去"才会重新查找资源，直接复用
        // ThemeService 已经写好、已经在生产环境验证过的这套刷新方法，不重复造轮子。
        ThemeService.RefreshOpenWindowsPublic();

        LanguageChanged?.Invoke();
    }

    private const string LangDictMarkerKey = "__XCL2_LangDictMarker__";

    /// <summary>
    /// 运行时按 key 查询当前语言的字符串，给 code-behind 里需要拼接文案（不是单纯 XAML
    /// 静态绑定）的场景用。找不到 key 时返回 key 本身（方便一眼看出漏翻译，而不是显示
    /// 空字符串让人以为文案本来就是空的）。
    /// </summary>
    public static string GetString(string key)
    {
        if (Application.Current?.Resources[key] is string s) return s;
        return key;
    }

    /// <summary>
    /// Debug 模式下的开发期校验：拿新加载的语言字典跟简体中文（基准/权威 key 集合）比对，
    /// 缺失的 key 打印到输出窗口，帮助在开发阶段就发现漏翻译，而不是等用户切换语言后
    /// 才发现某处显示成英文 fallback 或者资源键名本身。Release 构建不做这个校验（避免
    /// 正式发布版本承担额外的启动开销），所以生产环境里静默 fallback 到 zh-Hans 兜底值，
    /// 不会抛异常影响用户使用。
    /// </summary>
    [Conditional("DEBUG")]
    private static void DebugValidateKeys(ResourceDictionary dict, string code)
    {
        if (code == DefaultLanguageCode) return; // 基准语言自己不用跟自己比

        ResourceDictionary baseline;
        try
        {
            baseline = new ResourceDictionary
            {
                Source = new Uri($"{DictPathPrefix}{DefaultLanguageCode}{DictPathSuffix}", UriKind.Relative)
            };
        }
        catch
        {
            return; // 基准语言都加载不出来，跳过这次校验（Apply 里对基准语言加载失败已经会直接抛出）
        }

        // 现在只汇总一个数字，不再逐条 Debug.WriteLine。
        // 原因：翻译是分批推进的，目前 8 种语言各只覆盖了 86/297 个 key，
        // 逐条打印会在输出窗口刷出上千行，把真正有用的调试信息淹掉。
        // 缺 key 本身不是 bug——Apply() 里的回退链会用英文补上（见那边的注释）。
        var missing = 0;
        foreach (var key in baseline.Keys)
        {
            if (key is string k && k != LangDictMarkerKey && !dict.Contains(k)) missing++;
        }

        if (missing > 0)
        {
            var total = baseline.Count - 1; // 减掉 LangDictMarkerKey
            Debug.WriteLine(
                $"[LocalizationService] 语言 '{code}' 覆盖 {total - missing}/{total} 条，" +
                $"缺 {missing} 条（运行时会自动回退到 {FallbackLanguageCode}）。" +
                "补翻译流程见 Tools/extract-hardcoded-strings.py。");
        }
    }
}
