using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>
/// 4 月 1 日彩蛋（愚人节整蛊）总控服务。
///
/// ===== 触发规则 =====
/// - 只在系统日期是 4 月 1 日这一天生效；当天"第一次启动启动器"时，从 8 种手段里随机抽取
///   一个组合（可能只抽到 1 种，也可能抽到好几种叠加），写入 <see cref="StateFilePath"/> 存起来。
///   同一天之内再次启动，直接读回上次抽到的组合，不重新抽（保证"今天出现的花样"在一天内稳定，
///   不会每次开关启动器都变一次，用户感受上更像是"今天运气不好"而不是"每次都随机故障"）。
/// - 右下角新出现一面小白旗，鼠标悬浮提示"点击小白旗就不会受到折磨了"。点一下：
///     1) 当天立即恢复正常（所有已生效的整蛊效果停止/复原）；
///     2) 弹出这次抽到的整蛊手段的说明文字；
///     3) 把说明文字写进 xcl2/cdjs/{n}.txt（n 为 1-8 中被抽中的那个编号），
///        但只有"这个编号今天真的被抽中触发过"才会写这一份，且只在文件不存在时写一次
///        （不会每年 4 月 1 日反复覆盖同一份文件）。
/// - 注册表 <c>SOFTWARE\XCL2</c> 下的 <c>noyrj</c>（DWORD）如果是 1，整个功能直接不触发，
///   且不在任何界面上提示这个开关的存在——这是刻意留的"内部/客服用"关闭方式，不走
///   RegistrySyncedFields 那一套（那一套是"配置项，双向读写、config.json 兜底镜像"，
///   noyrj 只读不写、且不进 AppConfig，两者性质不同，不应该混在一起）。
/// </summary>
public static class AprilFoolsService
{
    private const string DisableRegistryValueName = "noyrj";

    private static string StateDir => Path.Combine(App.DataDir, "cdjs");
    private static string StateFilePath => Path.Combine(StateDir, "state.json");

    private static AprilFoolsState? _state;

    // “千万别点”第三个确定可在任意日期临时触发一次愚人节效果。
    // 这两个字段只存在于当前进程内，绝不写 state.json，因此重启启动器后必定消失。
    private static Effect _sessionEffects = Effect.None;
    private static bool _sessionEffectsDismissed;

    /// <summary>某个整蛊效果被启用/恢复时触发，UI 层（MainWindow/HomePage）订阅它来
    /// 显示/隐藏小白旗、开始/停止各自的效果。</summary>
    public static event Action? StateChanged;

    [Flags]
    public enum Effect
    {
        None = 0,
        /// <summary>1：点击"启动游戏"/"下载"跳转到 Never Gonna Give You Up。</summary>
        RickRoll1 = 1 << 0,
        /// <summary>2：点击"启动游戏"/"下载"弹出假的 Win32 报错窗口。</summary>
        FakeWin32Error = 1 << 1,
        /// <summary>3：鼠标靠近"启动游戏"按钮时按钮随机乱窜，点不到。</summary>
        DodgeLaunchButton = 1 << 2,
        /// <summary>4：点击"启动游戏"/"下载"跳转到另一个恶搞视频。</summary>
        RickRoll2 = 1 << 3,
        /// <summary>5：窗口在桌面上乱窜、无法全屏，强制窗口模式（按 F1 可直接恢复）。</summary>
        WindowChaos = 1 << 4,
        /// <summary>7：启动游戏时提示"游戏今天不想启动"之类的抽象拒绝文案。</summary>
        LauncherRefuses = 1 << 5,
        /// <summary>8：首页磁贴乱跳，点"启动游戏"磁贴提示"磁贴今天不开心"。</summary>
        TileChaos = 1 << 6,
    }

    /// <summary>每个效果编号（对应需求文档里的 1/2/3/4/5/7/8）→ 展示给用户的说明文字，
    /// 同时也是写入 xcl2/cdjs/{n}.txt 的内容。</summary>
    private static readonly Dictionary<int, (Effect Effect, string Description)> Catalog = new()
    {
        [1] = (Effect.RickRoll1, "愚人节彩蛋 1：点击「开始游戏」和「下载」按钮会跳转到 Never Gonna Give You Up 的 B 站视频。点击小白旗即可恢复。"),
        [2] = (Effect.FakeWin32Error, "愚人节彩蛋 2：每次点击「启动游戏」和「下载游戏」都会弹出一个 Win32 报错窗口，提示某个文件的某行某列出现错误。点击小白旗即可恢复。"),
        [3] = (Effect.DodgeLaunchButton, "愚人节彩蛋 3：鼠标靠近「启动游戏」按钮时，按钮会随机乱窜，永远点不到。点击小白旗即可恢复。"),
        [4] = (Effect.RickRoll2, "愚人节彩蛋 4：点击「开始游戏」和「下载」按钮会跳转到另一个恶搞视频。点击小白旗即可恢复。"),
        [5] = (Effect.WindowChaos, "愚人节彩蛋 5：界面会在桌面上乱窜，且无法全屏，强制窗口模式。按 F1 或点击小白旗即可恢复。"),
        [7] = (Effect.LauncherRefuses, "愚人节彩蛋 7：启动游戏时会提示「游戏今天不想启动」「启动器今天不行为你启动」等抽象文案。点击小白旗即可恢复。"),
        [8] = (Effect.TileChaos, "愚人节彩蛋 8：主页上的磁贴会乱跳，点击「启动游戏」磁贴会提示「磁贴今天不开心，很不高兴为你启动游戏」。点击小白旗即可恢复。"),
    };

    private static readonly Random Rng = new();

    private class AprilFoolsState
    {
        public DateTime SelectedDate { get; set; }
        public Effect Effects { get; set; }
        public bool FlagClicked { get; set; }
    }

    /// <summary>当前是否应该表现出任何整蛊效果。
    /// 正常愚人节状态仍要求 4 月 1 日；“千万别点”第三按钮触发的会话彩蛋不检查日期，
    /// 但只活到当前进程结束，不写配置、不写 state.json。</summary>
    public static bool IsActive => DateEffectsActive || SessionEffectsActive;

    private static bool DateEffectsActive =>
        IsAprilFoolsDate() && !IsDisabledByRegistry() && _state is { FlagClicked: false } && _state.Effects != Effect.None;

    private static bool SessionEffectsActive => !_sessionEffectsDismissed && _sessionEffects != Effect.None;

    public static Effect ActiveEffects =>
        (DateEffectsActive ? _state!.Effects : Effect.None) |
        (SessionEffectsActive ? _sessionEffects : Effect.None);

    public static bool Has(Effect effect) => (ActiveEffects & effect) == effect && effect != Effect.None;

    private static bool IsAprilFoolsDate() => DateTime.Now is { Month: 4, Day: 1 };

    /// <summary>noyrj 注册表值检查。只读，永远不由本服务写入——用户需要自己手动在注册表里
    /// SOFTWARE\XCL2 下新建一个名为 noyrj 的 DWORD (32 位) 值并设为 1，任何界面都不会提示这一点。</summary>
    private static bool IsDisabledByRegistry()
    {
        try
        {
            return RegistryConfigService.GetInt(DisableRegistryValueName, 0) == 1;
        }
        catch
        {
            // 读取异常一律当作"没关闭"处理，不让这里的问题影响其余启动流程。
            return false;
        }
    }

    /// <summary>启动器启动流程里调用一次（越早越好，MainWindow 构造函数/Loaded 均可）。
    /// 非 4 月 1 日或者被 noyrj 关闭时直接清空状态、什么都不做；4 月 1 日且今天还没抽过，
    /// 就随机抽一组效果并落盘；已经抽过就读回旧状态（可能是"已抽中"也可能是"今天已点过白旗"）。</summary>
    public static void EnsureTodaysStateLoaded()
    {
        if (!IsAprilFoolsDate() || IsDisabledByRegistry())
        {
            _state = null;
            StateChanged?.Invoke();
            return;
        }

        LoadState();

        var today = DateTime.Now.Date;
        if (_state == null || _state.SelectedDate != today)
        {
            _state = new AprilFoolsState
            {
                SelectedDate = today,
                Effects = RollRandomEffects(),
                FlagClicked = false,
            };
            SaveState();
            PersistTriggeredDescriptions(_state.Effects);
        }

        StateChanged?.Invoke();
    }

    /// <summary>从 8 种（实际编号 1/2/3/4/5/7/8，共 7 个候选）里随机抽一组：先抽 1 个，
    /// 然后有一定概率再叠加 0~2 个额外效果，模拟需求里"也有可能是几种结合"。</summary>
    private static Effect RollRandomEffects()
    {
        var pool = Catalog.Values.Select(v => v.Effect).ToList();
        var first = pool[Rng.Next(pool.Count)];
        var result = first;

        // 30% 概率再叠加一个，叠加后再有 15% 概率继续叠加第三个——多数情况下只出现 1 种，
        // 少数情况下 2~3 种组合出现，符合"随机出现...也有可能是几种结合"的描述。
        if (Rng.NextDouble() < 0.30)
        {
            var second = pool[Rng.Next(pool.Count)];
            result |= second;
            if (Rng.NextDouble() < 0.15)
            {
                var third = pool[Rng.Next(pool.Count)];
                result |= third;
            }
        }

        return result;
    }

    /// <summary>
    /// 从“4 月 1 日可能出现”的 Catalog 中随机抽一个效果，在任意日期强制启用。
    /// 专供百宝箱“千万别点”第三个红色确定按钮使用。该状态只放内存：
    /// - 有同一面小白旗；
    /// - F1 同样可关闭；
    /// - 10 秒/60 秒恢复提示由 AprilFoolsUi 在 StateChanged 后重新计时；
    /// - 重启启动器后不会再出现。
    /// </summary>
    public static Effect ActivateRandomSessionEffect()
    {
        var pool = Catalog.Values.Select(v => v.Effect).ToArray();
        _sessionEffects = pool[Rng.Next(pool.Length)];
        _sessionEffectsDismissed = false;
        StateChanged?.Invoke();
        return _sessionEffects;
    }

    /// <summary>把今天真正抽中触发过的每个编号的说明文字各写一次 xcl2/cdjs/{n}.txt，
    /// 只有文件还不存在时才写（即"只有触发过这个彩蛋，它才会保存一次这个文件"，且只保存一次，
    /// 不会因为之后年份又抽中同一个编号就反复覆盖）。</summary>
    private static void PersistTriggeredDescriptions(Effect triggered)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            foreach (var (number, (effect, description)) in Catalog)
            {
                if ((triggered & effect) != effect) continue;
                var path = Path.Combine(StateDir, $"{number}.txt");
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, description);
                }
            }
        }
        catch
        {
            // 落盘失败（权限/磁盘问题）不影响整蛊效果本身的展示，静默忽略。
        }
    }

    /// <summary>小白旗被点击：今天彻底恢复正常，并返回本次触发过的所有说明文字
    /// （用于弹窗展示给用户，告诉 ta 刚才到底经历了什么）。</summary>
    public static List<string> ClickWhiteFlag()
    {
        var descriptions = new List<string>();
        var effectsToDescribe = ActiveEffects;
        foreach (var (_, (effect, description)) in Catalog)
        {
            if ((effectsToDescribe & effect) == effect) descriptions.Add(description);
        }
        DismissForToday();
        return descriptions;
    }

    /// <summary>无需说明弹窗地关闭当天彩蛋。F1 恢复快捷键使用这个入口：
    /// 跟点击小白旗一样会把 FlagClicked 落盘，因此当天后续重启也保持正常模式。
    /// </summary>
    public static void DismissForToday()
    {
        // 真实 4 月 1 日状态照旧落盘，保证当天重启后不再出现。
        if (_state != null && IsAprilFoolsDate())
        {
            _state.FlagClicked = true;
            SaveState();
        }

        // “千万别点”触发的状态只改内存，不做任何持久化。
        _sessionEffectsDismissed = true;
        _sessionEffects = Effect.None;
        StateChanged?.Invoke();
    }

    private static void LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) { _state = null; return; }
            var json = File.ReadAllText(StateFilePath);
            _state = JsonSerializer.Deserialize<AprilFoolsState>(json);
        }
        catch
        {
            // 状态文件损坏：当成"今天还没抽过"处理，走正常的重新随机流程。
            _state = null;
        }
    }

    private static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFilePath, json);
        }
        catch
        {
            // 落盘失败不影响当次会话里的效果表现，只是下次重启可能重新抽一次，可接受。
        }
    }

    /// <summary>「启动游戏」「下载」类按钮统一调用的钩子：只要今天 <see cref="IsActive"/>
    /// （4 月 1 日、没被 noyrj 关掉、今天还没点白旗），这次点击就必须被"劫持"掉
    /// （调用方应在返回 true 时直接 return，不再执行原本的启动/下载逻辑）。
    ///
    /// ===== 修复：之前这里只覆盖 RickRoll1/RickRoll2/FakeWin32Error/LauncherRefuses
    /// 四种"点击类"劫持，DodgeLaunchButton（3 号，鼠标躲避）和 TileChaos（8 号，磁贴乱跳）
    /// 只在"启动游戏"按钮/磁贴自己的 MouseEnter/PreviewMouseLeftButtonDown 里处理，
    /// 完全没有接到这个统一钩子上。这就导致只要绕开鼠标交互——比如托盘菜单里的
    /// "启动游戏"（直接调用 Launch_Click，不经过任何按钮的鼠标事件）——3/8 号生效时
    /// 照样能正常启动游戏，跟"愚人节当天用户以各种形式都无法启动游戏"的需求不符。
    /// 现在把 DodgeLaunchButton/TileChaos/WindowChaos 也纳入候选池：只要 IsActive 为真
    /// （意味着今天确实抽中了某种效果），这个方法永远返回 true，保证托盘图标、首页磁贴、
    /// 主界面按钮等任何入口都逃不掉——具体表现成哪一种由 AprilFoolsUi 那边的 switch 决定，
    /// 如果抽中的效果里没有任何"自带专属文案"的类型，兜底用 LauncherRefuses 的随机拒绝
    /// 文案顶上，确保调用方一定能拿到一个可展示的效果。</summary>
    public static bool TryGetLaunchOrDownloadInterception(out Effect which)
    {
        which = Effect.None;
        if (!IsActive) return false;

        // 同一次点击如果好几种效果同时生效，随机选一种表现出来，避免弹出一堆东西。
        var candidates = new List<Effect>();
        if (Has(Effect.RickRoll1)) candidates.Add(Effect.RickRoll1);
        if (Has(Effect.RickRoll2)) candidates.Add(Effect.RickRoll2);
        if (Has(Effect.FakeWin32Error)) candidates.Add(Effect.FakeWin32Error);
        if (Has(Effect.LauncherRefuses)) candidates.Add(Effect.LauncherRefuses);
        if (Has(Effect.DodgeLaunchButton)) candidates.Add(Effect.DodgeLaunchButton);
        if (Has(Effect.TileChaos)) candidates.Add(Effect.TileChaos);
        if (Has(Effect.WindowChaos)) candidates.Add(Effect.WindowChaos);

        // 兜底：IsActive 已经保证 _state.Effects != None，理论上 candidates 不会为空
        // （上面 7 个分支覆盖了 Catalog 里全部枚举值），但即使将来加了新效果类型没有
        // 及时补充到这个列表，也不能因此放行一次真正的启动——用 LauncherRefuses 的
        // 拒绝文案兜底，保证"今天有效果生效 = 这次点击一定被拦下"这条底线不被打破。
        which = candidates.Count > 0 ? candidates[Rng.Next(candidates.Count)] : Effect.LauncherRefuses;
        return true;
    }

    /// <summary>"游戏今天不想启动"一类的抽象拒绝文案池，LauncherRefuses 效果用。</summary>
    public static string RandomRefusalText()
    {
        string[] texts =
        [
            "游戏今天不想启动。",
            "启动器今天不行为你启动。",
            "启动器今天心情不好，改天再来吧。",
            "游戏说它还没睡醒。",
            "启动器罢工了，理由是今天不想上班。",
        ];
        return texts[Rng.Next(texts.Length)];
    }
}
