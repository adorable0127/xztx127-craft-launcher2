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
    /// 算出"现在应该显示哪一张背景（图片或视频）"，并把结果写回
    /// <see cref="AppConfig.CustomBackgroundImagePath"/>。返回 true 表示 cfg 被改动过（需要
    /// Save），<paramref name="path"/> 是最终生效的路径（没有任何候选时为 null，调用方按
    /// "清除背景"处理）。图片和视频共用这一个候选池、这一套轮换规则——轮换只关心"候选池里
    /// 的下一项是谁"，至于那一项到底是图片还是视频，由 MainWindow 按扩展名分派渲染，
    /// 跟这里的调度逻辑无关。
    ///
    /// 规则：
    ///   候选池为空        → null；
    ///   候选池只有一张    → 永远是它，任何轮换模式都不做任何轮换（需求点 2）；
    ///   Fixed             → 当前路径仍在候选池里就继续用它，否则回退到候选池第一张；
    ///   Daily             → 跨过一个自然日才前进一格，同一天内保持不变；
    ///   FixedTime         → 每天到达用户指定的 HH:mm 那一刻才前进一格，一天只换一次；
    ///   Minutely/Hourly/
    ///   CustomInterval    → 距离上一次真正换过去的时间达到对应间隔（1 分钟 / 1 小时 /
    ///                       自定义分钟数）才前进一格，用挂钟时间差判断，不受启动器
    ///                       启动/退出影响——挂着不关和频繁重启换到的张数应该是一样的。
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
            // 需求点 2：只有一张时直接默认选它，任何轮换模式开着也没有任何作用——
            // 这里连 LastDate/LastTimestamp 都不去动，避免用户之后又导入第二张时，因为
            // "这一轮已经换过了"而白白多等一个周期才看到轮换真正开始工作。
            index = 0;
        }
        else if (cfg.BackgroundRotationMode == AppearanceRotationMode.Daily)
        {
            index = AdvanceByCalendarDay(cfg, candidates.Count, ref changed);
        }
        else if (cfg.BackgroundRotationMode == AppearanceRotationMode.FixedTime)
        {
            index = AdvanceByFixedTimeOfDay(cfg, candidates.Count, ref changed);
        }
        else if (cfg.BackgroundRotationMode is AppearanceRotationMode.Minutely
                 or AppearanceRotationMode.Hourly
                 or AppearanceRotationMode.CustomInterval)
        {
            var intervalMinutes = cfg.BackgroundRotationMode switch
            {
                AppearanceRotationMode.Minutely => 1,
                AppearanceRotationMode.Hourly => 60,
                // 用户可能手填 0 或负数；下限钉在 1 分钟，否则每秒 Tick 都会命中，
                // 相当于变成"每次重绘都换一张"，跟设置项的字面意思不符。
                _ => Math.Max(1, cfg.BackgroundRotationIntervalMinutes)
            };
            index = AdvanceByElapsedInterval(cfg, candidates.Count, intervalMinutes, ref changed);
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

    /// <summary>Daily 模式：跨过一个自然日才 +1，同一天内多次调用保持不变。</summary>
    private static int AdvanceByCalendarDay(AppConfig cfg, int candidateCount, ref bool changed)
    {
        var today = TodayKey;
        var index = cfg.BackgroundRotationIndex;
        if (string.IsNullOrEmpty(cfg.BackgroundRotationLastDate))
        {
            // 刚打开轮换：只记下今天，今天仍然显示用户现在看到的这一张，从明天开始才换。
            // 一勾选就立刻换图会让用户以为自己误触了别的设置。
            var currentIdx = FindCurrentIndex(cfg, candidateCount);
            index = currentIdx;
            cfg.BackgroundRotationIndex = index;
            cfg.BackgroundRotationLastDate = today;
            changed = true;
        }
        else if (!string.Equals(cfg.BackgroundRotationLastDate, today, StringComparison.Ordinal))
        {
            index = (index + 1) % candidateCount;
            cfg.BackgroundRotationIndex = index;
            cfg.BackgroundRotationLastDate = today;
            changed = true;
        }

        return Clamp(index, candidateCount);
    }

    /// <summary>FixedTime 模式：每天到达用户指定的 HH:mm 那一刻（且当天还没换过）才 +1。
    /// 用日期字符串去重，跟 Daily 一样保证一天最多换一次；不同的是"该不该换"还要额外看
    /// 当前时间是否已经过了那个钟点——没到点之前，即使跨了天也先不换，等真正到点那一秒才换，
    /// 这样"固定时间轮换"才名副其实。</summary>
    private static int AdvanceByFixedTimeOfDay(AppConfig cfg, int candidateCount, ref bool changed)
    {
        var today = TodayKey;
        var index = cfg.BackgroundRotationIndex;
        var alreadyRotatedToday = string.Equals(cfg.BackgroundRotationLastDate, today, StringComparison.Ordinal);

        if (string.IsNullOrEmpty(cfg.BackgroundRotationLastDate))
        {
            // 刚打开轮换：先认领"今天"，但不代表"今天已经换过"——如果打开的时候已经过了
            // 设定的钟点，仍然应该在下面的判断里正常触发一次，而不是白白等到明天。
            // 这里不预先写 LastDate，让下面统一走"是否到点"的判断。
            index = FindCurrentIndex(cfg, candidateCount);
            cfg.BackgroundRotationIndex = index;
            changed = true;
        }

        if (!alreadyRotatedToday && HasPassedTimeOfDay(cfg.BackgroundRotationFixedTime))
        {
            // 避免"刚打开轮换、当天已经过点"这一帧就立刻往后跳一张——首次记录的这一刻只
            // 认领日期，真正的换图从下一次真正跨过这个点开始。跟 Daily/Minutely 等其它
            // 模式"打开开关不立刻变"的体感保持一致。
            if (string.IsNullOrEmpty(cfg.BackgroundRotationLastDate))
            {
                cfg.BackgroundRotationLastDate = today;
                changed = true;
            }
            else
            {
                index = (index + 1) % candidateCount;
                cfg.BackgroundRotationIndex = index;
                cfg.BackgroundRotationLastDate = today;
                changed = true;
            }
        }

        return Clamp(index, candidateCount);
    }

    /// <summary>Minutely / Hourly / CustomInterval 共用：距上一次真正换过去的挂钟时间达到
    /// <paramref name="intervalMinutes"/> 才 +1。用绝对时间戳而不是"计数器"，是因为启动器可能
    /// 被最小化到托盘挂一整晚——用户预期的是"真的过了这么久就该换"，不是"我今天开了几次
    /// 启动器就换几次"。</summary>
    private static int AdvanceByElapsedInterval(AppConfig cfg, int candidateCount, int intervalMinutes, ref bool changed)
    {
        var now = DateTime.Now;
        var index = cfg.BackgroundRotationIndex;

        if (string.IsNullOrEmpty(cfg.BackgroundRotationLastTimestamp) ||
            !DateTime.TryParse(cfg.BackgroundRotationLastTimestamp, out var lastTime))
        {
            // 刚打开轮换（或时间戳格式被手改坏了）：只记下"现在"作为计时起点，本次不换，
            // 从下一个完整周期开始才真正轮换——跟其它几档"打开开关不立刻变"的体感一致。
            index = FindCurrentIndex(cfg, candidateCount);
            cfg.BackgroundRotationIndex = index;
            cfg.BackgroundRotationLastTimestamp = now.ToString("yyyy-MM-dd HH:mm:ss");
            changed = true;
            return Clamp(index, candidateCount);
        }

        var elapsedMinutes = (now - lastTime).TotalMinutes;
        if (elapsedMinutes >= intervalMinutes)
        {
            // 如果启动器休眠/被挂起了很久（比如挂了一晚上，隔了 8 小时），只当作换了一张，
            // 不会一次性把积压的周期数全部补上——不然用户开机唤醒的瞬间背景会疯狂闪一圈，
            // 而且候选池就那么几张，补多少圈最后落点都一样，没有意义。
            index = (index + 1) % candidateCount;
            cfg.BackgroundRotationIndex = index;
            cfg.BackgroundRotationLastTimestamp = now.ToString("yyyy-MM-dd HH:mm:ss");
            changed = true;
        }

        return Clamp(index, candidateCount);
    }

    /// <summary>把 "HH:mm" 解析成今天的具体时刻，判断现在的挂钟时间是否已经过了它。
    /// 解析失败（用户手改配置文件填了非法字符串）时按"00:00"处理，即等同于 Daily。</summary>
    private static bool HasPassedTimeOfDay(string? hhmm)
    {
        var now = DateTime.Now;
        if (!TimeSpan.TryParse(hhmm, out var timeOfDay))
            timeOfDay = TimeSpan.Zero;
        return now.TimeOfDay >= timeOfDay;
    }

    /// <summary>找到候选池里当前正在显示的那一张的下标；找不到（比如刚导入、还没选过任何一张）
    /// 就退回第一张。三种"打开轮换开关的那一瞬间"共用这段逻辑。</summary>
    private static int FindCurrentIndex(AppConfig cfg, int candidateCount)
    {
        var candidates = cfg.CustomBackgroundImageCandidates;
        var idx = candidates.FindIndex(p => string.Equals(p, cfg.CustomBackgroundImagePath, StringComparison.OrdinalIgnoreCase));
        return Clamp(idx >= 0 ? idx : 0, candidateCount);
    }

    private static int Clamp(int index, int candidateCount)
        => index < 0 || index >= candidateCount ? 0 : index;

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
