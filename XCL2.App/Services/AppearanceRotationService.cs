using System.Collections.Generic;
using System.IO;
using System.Linq;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 「背景图片 / 配色色系」的候选池与每日轮换。
///
/// 解决的需求：
///   1. 可以导入多张图片作为背景候选项；
///   2. 只有一个背景时默认就用它，这时候打开"轮换"也不应该有任何作用；
///   3. 可以固定用某一张，也可以每天轮换；
///   4. 配色/主题也套同一套"固定 or 每天轮换"的规则。
///
/// 设计要点（为什么是现在这个形状）：
/// - 候选池只负责"可以从哪些里面选"，真正生效的那一个仍然写回 AppConfig.CustomBackgroundImagePath
///   / AppConfig.UiSkin。这两个字段在项目里已经有一堆读取点（MainWindow 启动套背景、
///   ThemeService.ApplyForCurrentState、注册表同步……），保持它们"当前值"的语义不变，
///   就不需要去动那些调用方，也不会出现"有的地方读候选池、有的地方读旧字段"的双份真相。
/// - 所有 Resolve* 方法都是"纯计算 + 就地修正 cfg"，返回值表示"cfg 是否被改动过、调用方
///   需不需要 Save()"。这样调用方（MainWindow 启动时、每秒定时器、设置页保存后）可以统一
///   处理，不用各自判断什么时候该落盘。
/// - 轮换按"本地自然日"推进，且靠 *LastDate 去重：同一天内无论启动多少次启动器、定时器
///   Tick 多少次，都只会换一次。这是"每天轮换"字面意义上该有的行为——如果每次启动都往后
///   挪一张，用户一天开三次启动器就看到三张不同的背景，跟设置项描述对不上。
/// - 轮换是按候选池顺序循环，不是随机。随机会出现"连着两天抽到同一张"的情况，用户会以为
///   功能坏了；顺序循环则一眼可预期。
/// </summary>
public static class AppearanceRotationService
{
    /// <summary>今天的日期标识（本地时间）。轮换以用户本地的自然日为准，不用 UTC——
    /// 用户感知的"今天"就是自己时区的今天，UTC 会让东八区用户在早上 8 点前后莫名其妙换一次。</summary>
    private static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd");

    // ======================= 背景图片 =======================

    /// <summary>
    /// 规整背景候选池：去掉空串/重复项/文件已经不存在的路径，并把旧配置里单独存着的
    /// <see cref="AppConfig.CustomBackgroundImagePath"/> 合并进来（老用户升级上来时，
    /// 他原来那张背景必须自动成为候选池的第一项，否则会表现成"更新之后背景没了"）。
    /// 返回 true 表示 cfg 确实被改过，调用方应该 Save()。
    /// </summary>
    public static bool NormalizeBackgroundCandidates(AppConfig cfg)
    {
        var original = cfg.CustomBackgroundImageCandidates ?? new List<string>();
        var cleaned = new List<string>();

        // 旧配置迁移：当前这一张永远排在最前面，保持老用户打开新版本时"背景没变"。
        if (!string.IsNullOrWhiteSpace(cfg.CustomBackgroundImagePath))
            cleaned.Add(cfg.CustomBackgroundImagePath!);

        foreach (var path in original)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (cleaned.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) continue;
            cleaned.Add(path);
        }

        // 文件可能被用户手动删掉/清理软件清掉了。留着不存在的路径会让轮换轮到它的那天
        // 直接没有背景，用户完全看不懂为什么，所以在这里就剔除。
        cleaned = cleaned.Where(File.Exists).ToList();

        var changed = !SameSequence(original, cleaned);
        cfg.CustomBackgroundImageCandidates = cleaned;

        // 下标可能因为删除而越界。
        if (cleaned.Count == 0)
        {
            if (cfg.BackgroundRotationIndex != 0) { cfg.BackgroundRotationIndex = 0; changed = true; }
        }
        else if (cfg.BackgroundRotationIndex < 0 || cfg.BackgroundRotationIndex >= cleaned.Count)
        {
            cfg.BackgroundRotationIndex = 0;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 算出"现在应该显示哪一张背景"，并把结果写回 <see cref="AppConfig.CustomBackgroundImagePath"/>。
    /// 返回 true 表示 cfg 被改动过（需要 Save），<paramref name="path"/> 是最终生效的路径
    /// （没有任何候选时为 null，调用方按"清除背景"处理）。
    ///
    /// 规则：
    ///   候选池为空        → null；
    ///   候选池只有一张    → 永远是它，此时 Daily 模式不做任何轮换（需求点 2）；
    ///   Fixed             → 当前路径仍在候选池里就继续用它，否则回退到候选池第一张；
    ///   Daily             → 跨过一个自然日才前进一格，同一天内保持不变。
    /// </summary>
    public static bool ResolveBackground(AppConfig cfg, out string? path)
    {
        var changed = NormalizeBackgroundCandidates(cfg);
        var candidates = cfg.CustomBackgroundImageCandidates;

        if (candidates.Count == 0)
        {
            path = null;
            if (cfg.CustomBackgroundImagePath != null) { cfg.CustomBackgroundImagePath = null; changed = true; }
            return changed;
        }

        int index;
        if (candidates.Count == 1)
        {
            // 需求点 2：只有一张时直接默认选它，"每天轮换"开着也没有任何作用——
            // 这里连 LastDate 都不去动，避免用户之后又导入第二张时，因为"今天已经换过了"
            // 而白白多等一天才看到轮换真正开始工作。
            index = 0;
        }
        else if (cfg.BackgroundRotationMode == AppearanceRotationMode.Daily)
        {
            var today = TodayKey;
            index = cfg.BackgroundRotationIndex;
            if (string.IsNullOrEmpty(cfg.BackgroundRotationLastDate))
            {
                // 刚打开轮换：只记下今天，今天仍然显示用户现在看到的这一张，从明天开始才换。
                // 一勾选就立刻换图会让用户以为自己误触了别的设置。
                var currentIdx = candidates.FindIndex(p =>
                    string.Equals(p, cfg.CustomBackgroundImagePath, StringComparison.OrdinalIgnoreCase));
                index = currentIdx >= 0 ? currentIdx : 0;
                cfg.BackgroundRotationIndex = index;
                cfg.BackgroundRotationLastDate = today;
                changed = true;
            }
            else if (!string.Equals(cfg.BackgroundRotationLastDate, today, StringComparison.Ordinal))
            {
                index = (index + 1) % candidates.Count;
                cfg.BackgroundRotationIndex = index;
                cfg.BackgroundRotationLastDate = today;
                changed = true;
            }

            if (index < 0 || index >= candidates.Count) index = 0;
        }
        else
        {
            // 固定模式：以用户当前钉住的那一张为准；它如果被移出候选池了就退回第一张。
            var current = cfg.CustomBackgroundImagePath;
            var found = candidates.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
            index = found >= 0 ? found : 0;
        }

        path = candidates[index];
        if (!string.Equals(cfg.CustomBackgroundImagePath, path, StringComparison.OrdinalIgnoreCase))
        {
            cfg.CustomBackgroundImagePath = path;
            changed = true;
        }
        return changed;
    }

    // ======================= 配色色系 =======================

    /// <summary>
    /// 规整配色候选池：剔除空串/重复/不认识的色系 Tag（ThemeService.AllSkins 之外的值，
    /// 比如旧配置里的历史遗留常量或者手改坏的 config.json）。
    ///
    /// 刻意**不**强行把当前 UiSkin 塞进候选池：用户完全可以"现在用 A，但只让 B/C/D 参与
    /// 每日轮换"，替他补一个他没勾的候选是越权。候选池不足两个时轮换本来就不会生效，
    /// 当前色系会原样保留，不存在"配置被清空导致配色乱跳"的风险。
    /// </summary>
    public static bool NormalizeSkinCandidates(AppConfig cfg)
    {
        var original = cfg.UiSkinCandidates ?? new List<string>();
        var cleaned = new List<string>();

        foreach (var skin in original)
        {
            if (string.IsNullOrWhiteSpace(skin)) continue;
            if (!ThemeService.AllSkins.Contains(skin, StringComparer.Ordinal)) continue;
            if (cleaned.Contains(skin, StringComparer.Ordinal)) continue;
            cleaned.Add(skin);
        }

        var changed = !SameSequence(original, cleaned);
        cfg.UiSkinCandidates = cleaned;

        if (cleaned.Count == 0)
        {
            if (cfg.UiSkinRotationIndex != 0) { cfg.UiSkinRotationIndex = 0; changed = true; }
        }
        else if (cfg.UiSkinRotationIndex < 0 || cfg.UiSkinRotationIndex >= cleaned.Count)
        {
            cfg.UiSkinRotationIndex = 0;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 算出"现在应该用哪个色系"，写回 <see cref="AppConfig.UiSkin"/>。规则跟背景完全一致。
    /// 注意只轮换色相，不碰 IsDarkMode——明暗有自己的自动循环/跟随系统两套机制，
    /// 掺进来会互相覆盖。
    /// </summary>
    public static bool ResolveSkin(AppConfig cfg, out string skin)
    {
        var changed = NormalizeSkinCandidates(cfg);
        var candidates = cfg.UiSkinCandidates;

        // 候选池少于两个：没有可轮换的对象，保持用户当前选中的色系不动。
        if (candidates.Count < 2 || cfg.UiSkinRotationMode != AppearanceRotationMode.Daily)
        {
            skin = cfg.UiSkin;
            return changed;
        }

        var today = TodayKey;
        var index = cfg.UiSkinRotationIndex;
        if (string.IsNullOrEmpty(cfg.UiSkinRotationLastDate))
        {
            // 刚打开轮换：只记下今天，不立刻换——否则用户一勾选，界面配色马上跳变，
            // 会以为自己点错了什么。从明天开始才真正开始每天换。
            cfg.UiSkinRotationLastDate = today;
            var currentIdx = candidates.FindIndex(s => string.Equals(s, cfg.UiSkin, StringComparison.Ordinal));
            index = currentIdx >= 0 ? currentIdx : 0;
            cfg.UiSkinRotationIndex = index;
            changed = true;
        }
        else if (!string.Equals(cfg.UiSkinRotationLastDate, today, StringComparison.Ordinal))
        {
            index = (index + 1) % candidates.Count;
            cfg.UiSkinRotationIndex = index;
            cfg.UiSkinRotationLastDate = today;
            changed = true;
        }

        if (index < 0 || index >= candidates.Count) index = 0;
        skin = candidates[index];
        if (!string.Equals(cfg.UiSkin, skin, StringComparison.Ordinal))
        {
            cfg.UiSkin = skin;
            changed = true;
        }
        return changed;
    }

    // ======================= 公共小工具 =======================

    /// <summary>两个字符串序列内容是否完全一致（顺序敏感）。只用来判断"要不要 Save"，
    /// 不需要考虑大小写归一之外的复杂比较。</summary>
    private static bool SameSequence(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
