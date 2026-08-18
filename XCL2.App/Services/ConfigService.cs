using System.IO;
using System.Linq;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 负责 xcl2/config.json（全局配置）与 xcl2/accounts.json（账户缓存）的读写。
/// 账户缓存实现"无需重复输入账户密码"：离线账户直接记住用户名+UUID，
/// 微软账户记住 refresh token，下次启动可静默刷新 access token。
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ConfigPath { get; }
    public string AccountsPath { get; }

    public AppConfig Config { get; private set; } = new();
    public List<Account> Accounts { get; private set; } = new();

    /// <summary>
    /// 访客模式下本次会话的临时账户，只存在于内存中，不属于 <see cref="Accounts"/> 列表，
    /// 也从不写入 accounts.json。由 MainWindow 在检测到 cfg.GuestModeEnabled 时创建并赋值。
    /// GetSelectedAccount 会优先返回这个账户（访客模式开启时），实现"访客模式下启动游戏
    /// 永远用这个临时账户，不会用到真实保存的账户"。
    /// </summary>
    public Account? GuestAccount { get; set; }

    public ConfigService()
    {
        ConfigPath = Path.Combine(App.DataDir, "config.json");
        AccountsPath = Path.Combine(App.DataDir, "accounts.json");
    }

    /// <summary>
    /// 「注册表功能」总开关：关闭后，Load/Save 完全跳过注册表读写，只用 config.json，
    /// 行为等同于这个功能上线前的老版本。默认开启。见设置页"关闭注册表功能"。
    /// 这个开关本身也存在 config.json 里（不放注册表——关掉注册表功能这件事本身
    /// 显然不能又依赖注册表才能记住）。
    /// </summary>
    public bool RegistryFeatureEnabled { get; private set; } = true;

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"读取配置文件失败，已使用默认配置：{ConfigPath}", ex);
            Config = new AppConfig();
        }

        RegistryFeatureEnabled = Config.RegistryFeatureEnabled;

        // 注册表为主存储、config.json 为镜像的字段：这里从注册表读回最新值覆盖 Config 里
        // 对应字段（如果注册表两支都没有，保留 config.json 里已有的值不动——即便注册表功能
        // 是这次新开的，也不会用注册表的"没有"把 config.json 里用户原有的设置冲掉）。
        // 见 RegistrySyncedFields.LoadFromRegistry 的详细字段清单与取舍说明。
        if (RegistryFeatureEnabled)
            RegistrySyncedFields.LoadFromRegistry(Config);

        // 反序列化后，若 JSON 中某属性显式写了 null（旧版本/手动编辑损坏的配置文件），
        // 该属性会被覆盖为 null 而不会走字段初始值，这里做兜底修复，避免后续 NullReferenceException。
        Config.Folders ??= new List<GameFolder>();
        Config.LastSelectedAccountId ??= "";
        Config.FavoriteVersionIds ??= new List<string>();
        Config.FavoriteItems ??= new List<FavoriteItem>();
        Config.FavoriteItems.RemoveAll(f => f == null);
        Config.VersionIsolationOverrides ??= new Dictionary<string, bool>();
        Config.VersionJavaOverrides ??= new Dictionary<string, int>();
        Config.InstalledJavas ??= new List<InstalledJava>();
        Config.InstalledJavas.RemoveAll(j => j == null); // 清理数组中可能存在的 null 元素

        // 老配置文件迁移：Priority 是新加的字段，旧版本写的 config.json 里没有这个属性，
        // 反序列化后全部落到默认值 0，如果不处理，"Java 列表"里所有条目会一起并列最高优先级，
        // 排序在 UI 上/FindJava 匹配时都会变得不确定（Distinct/OrderBy 对相同 Priority 的元素
        // 相对顺序没有强保证）。这里检测到"存在 2 条以上、Priority 都是 0"这种典型的老配置
        // 特征时，按它们在数组里的原始顺序(等于当初注册/添加的先后顺序)重新编号，
        // 保持"老配置升级后，自动匹配顺序跟以前感觉一样(先添加的先用)"，不会因为这个新字段
        // 突然打乱用户已经在用的 Java 优先级。
        if (Config.InstalledJavas.Count > 1 && Config.InstalledJavas.All(j => j.Priority == 0))
        {
            for (var i = 0; i < Config.InstalledJavas.Count; i++)
                Config.InstalledJavas[i].Priority = i;
        }

        Config.VersionJavaIdOverrides ??= new Dictionary<string, string>();

        // 老配置文件迁移：只要有 FavoriteVersionIds 里的旧版本收藏、并且还没搬进
        // FavoriteItems（避免每次启动重复搬运出现重复项），就补一条 Type=Version 的记录。
        // 迁移后不清空 FavoriteVersionIds（万一用户还开着旧版本启动器共用一份配置文件，
        // 保留旧字段能让旧版本继续正常读到收藏；新版本此后只读写 FavoriteItems）。
        foreach (var versionId in Config.FavoriteVersionIds)
        {
            if (!Config.FavoriteItems.Any(f => f.MatchesKey(FavoriteItemType.Version, versionId, ModSource.Combined)))
            {
                Config.FavoriteItems.Add(new FavoriteItem
                {
                    Type = FavoriteItemType.Version,
                    Source = ModSource.Combined,
                    SourceId = versionId
                });
            }
        }

        try
        {
            if (File.Exists(AccountsPath))
            {
                var json = File.ReadAllText(AccountsPath);
                Accounts = JsonSerializer.Deserialize<List<Account>>(json) ?? new List<Account>();
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"读取账户缓存失败，已重置为空账户列表：{AccountsPath}", ex);
            Accounts = new List<Account>();
        }

        Accounts ??= new List<Account>();
        Accounts.RemoveAll(a => a == null); // 清理数组中可能存在的 null 元素

        EnsureDefaultFolder();
    }

    /// <summary>
    /// 确保至少存在一个默认 .minecraft 目录：启动器运行目录根下的 .minecraft 文件夹
    /// （即"官启文件夹以及运行 exe 的文件根目录下"的要求）。
    /// </summary>
    private void EnsureDefaultFolder()
    {
        if (Config.Folders.Count == 0)
        {
            var defaultPath = Path.Combine(AppContext.BaseDirectory, ".minecraft");
            Directory.CreateDirectory(defaultPath);
            var folder = new GameFolder { Name = "当前文件夹", Path = defaultPath, IsDefault = true };
            Config.Folders.Add(folder);
            Config.SelectedFolderPath = defaultPath;
            Save();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(App.DataDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOpts));

        RegistryFeatureEnabled = Config.RegistryFeatureEnabled;
        if (RegistryFeatureEnabled)
        {
            // 注册表为主存储：每次 Save() 顺手把镜像字段同步写回注册表。
            // 写入分支（HKLM 全设备 / HKCU 当前用户）由 Config.UseMachineWideRegistry +
            // 当前进程是否管理员共同决定，具体规则见 RegistryConfigService 类头注释与
            // RegistrySyncedFields.SaveToRegistry。
            RegistrySyncedFields.SaveToRegistry(Config);
        }
    }

    /// <summary>
    /// 把 snapshot 里每一个可读写属性的值逐个复制到当前 Config 实例上（跟 PatchDefaults
    /// 用的是同一套反射遍历套路），而不是直接把 Config 属性整个替换成 snapshot 引用——
    /// Config 是 { get; private set; }，且项目里不少地方持有 Config 的引用长期使用，
    /// 直接换引用会导致那些地方还拿着旧对象。用于设置页"自动保存后点击回退"场景，
    /// 把配置整体还原到自动保存前的那一份 JSON 快照，见 SettingsPage.RollbackAutoSave。
    /// </summary>
    public void ReplaceConfigFieldsFrom(AppConfig snapshot)
    {
        foreach (var prop in typeof(AppConfig).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            prop.SetValue(Config, prop.GetValue(snapshot));
        }
    }

    /// <summary>
    /// 设置页"更新配置文件"按钮：把 <see cref="AppConfig"/> 里字段的默认值补丁进当前已加载的
    /// 配置，但**只补两类安全情况**，不会覆盖用户已经改过的任何设置：
    ///   1. 字符串/集合/字典字段当前是 null（老版本 config.json 没有这个字段，反序列化后
    ///      就是 null，Load() 里针对已知字段做过兜底，但这里作为通用兜底再扫一遍，
    ///      覆盖任何未来新增、Load() 里可能漏加兜底的字段）。
    ///   2. 数值/布尔字段当前正好等于 .NET 的"类型默认值"（0 / false）**且**这一次的字段级
    ///      默认值 Attribute 明确标注为"允许补丁"——由于反射拿不到 C# 属性初始化表达式本身，
    ///      这里改用显式白名单 <see cref="PatchableScalarDefaults"/> 记录"字段名 → 期望默认值"，
    ///      逐条核对，而不是对所有标量字段做危险的全量猜测（例如用户手动把
    ///      DownloadSpeedLimitKBps 设成 0 表示"不限速"，如果不加白名单，反射会把它误判成
    ///      "没改过"进而被新默认值覆盖）。
    /// 新增需要补丁的标量字段时，在 <see cref="PatchableScalarDefaults"/> 里补一行即可。
    /// </summary>
    public int PatchDefaults()
    {
        var fresh = new AppConfig();
        var patchedCount = 0;

        foreach (var prop in typeof(AppConfig).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var currentValue = prop.GetValue(Config);

            if (currentValue is null)
            {
                var freshValue = prop.GetValue(fresh);
                if (freshValue is null) continue;
                prop.SetValue(Config, freshValue);
                patchedCount++;
                continue;
            }

            if (PatchableScalarDefaults.TryGetValue(prop.Name, out var typeDefault) &&
                Equals(currentValue, typeDefault))
            {
                var freshValue = prop.GetValue(fresh);
                if (!Equals(freshValue, typeDefault))
                {
                    prop.SetValue(Config, freshValue);
                    patchedCount++;
                }
            }
        }

        if (patchedCount > 0) Save();
        return patchedCount;
    }

    /// <summary>
    /// <see cref="PatchDefaults"/> 使用的标量字段白名单：只有列在这里的字段，当它当前值
    /// 等于对应的"未设置占位值"时，才会被新默认值覆盖。不在这个字典里的标量字段（尤其是
    /// 那些 0/false 本身就是用户可能主动选择的合法值的字段）一律跳过，绝不触碰。
    /// </summary>
    private static readonly Dictionary<string, object> PatchableScalarDefaults = new()
    {
        [nameof(AppConfig.PreferredJavaMajorVersion)] = 0,
        [nameof(AppConfig.MinMemoryMb)] = 0,
        [nameof(AppConfig.MaxMemoryMb)] = 0,
        [nameof(AppConfig.WindowWidth)] = 0,
        [nameof(AppConfig.WindowHeight)] = 0,
        [nameof(AppConfig.MaxDownloadThreads)] = 0,
        [nameof(AppConfig.MemoryOptimizationReserveMb)] = 0,
        [nameof(AppConfig.AutoThemeLightStartHour)] = 0,
        [nameof(AppConfig.AutoThemeDarkStartHour)] = 0,
    };

    public void SaveAccounts()
    {
        Directory.CreateDirectory(App.DataDir);
        File.WriteAllText(AccountsPath, JsonSerializer.Serialize(Accounts, JsonOpts));
    }

    /// <summary>
    /// 把一个 javaw.exe 路径登记进"Java 列表"(<see cref="AppConfig.InstalledJavas"/>)。
    /// 路径已存在(不区分大小写)则直接返回原有记录，不产生重复项；否则新建一条并自动生成一个
    /// 默认名字(如 "Java 21")，需要外部调用方自己 Save()。不在这里探测版本号——调用方通常
    /// 已经拿到了版本号(下载/扫描时都会附带)，避免重复调用一次外部进程。
    /// </summary>
    public InstalledJava RegisterJava(string javawPath, int? majorVersion, string source)
    {
        var existing = Config.InstalledJavas.FirstOrDefault(
            j => string.Equals(j.JavawPath, javawPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // 已存在：如果这次拿到了版本号而之前没有，顺便补全一下，不新建重复项。
            if (majorVersion is > 0 && existing.MajorVersion is null or 0)
                existing.MajorVersion = majorVersion;
            return existing;
        }

        var name = majorVersion is > 0 ? $"Java {majorVersion}" : "Java (版本未知)";
        // 避免同名：同名已存在时加个序号后缀，方便用户在下拉框里区分。
        if (Config.InstalledJavas.Any(j => j.Name == name))
        {
            var i = 2;
            while (Config.InstalledJavas.Any(j => j.Name == $"{name} ({i})")) i++;
            name = $"{name} ({i})";
        }

        var entry = new InstalledJava
        {
            Name = name,
            JavawPath = javawPath,
            MajorVersion = majorVersion,
            Source = source,
            // 新记录追加到优先级列表末尾(取当前最大 Priority + 1)，不打乱已有条目的顺序；
            // 列表为空时从 0 开始。
            Priority = Config.InstalledJavas.Count == 0 ? 0 : Config.InstalledJavas.Max(j => j.Priority) + 1
        };
        Config.InstalledJavas.Add(entry);
        return entry;
    }

    /// <summary>把 Java 列表按 Priority 升序排列（数值越小越靠前=优先级越高），
    /// UI 展示和 FindJava 自动匹配都用这个顺序，保证"列表看到的顺序"就是"实际匹配顺序"。</summary>
    public List<InstalledJava> GetJavaListInPriorityOrder() =>
        Config.InstalledJavas.OrderBy(j => j.Priority).ToList();

    /// <summary>
    /// 上移/下移一条 Java 记录：跟相邻的那一条交换 Priority 值，而不是重新给整个列表编号——
    /// 这样每次移动只影响两条记录，逻辑简单且不会因为中途有记录被删除导致编号出现空洞后
    /// 排序错乱。moveUp=true 表示往列表前面移(优先级提高)，false 表示往后移(优先级降低)。
    /// 已经在最顶/最底时移动无效果(找不到可交换的相邻项)，调用方不需要额外判断边界。
    /// </summary>
    public void MoveJavaPriority(string javaId, bool moveUp)
    {
        var ordered = GetJavaListInPriorityOrder();
        var index = ordered.FindIndex(j => j.Id == javaId);
        if (index < 0) return;

        var swapIndex = moveUp ? index - 1 : index + 1;
        if (swapIndex < 0 || swapIndex >= ordered.Count) return;

        (ordered[index].Priority, ordered[swapIndex].Priority) = (ordered[swapIndex].Priority, ordered[index].Priority);
    }

    /// <summary>按 Id 在 Java 列表里查找一条记录，找不到(比如用户后来手动删除了这条记录)返回 null。</summary>
    public InstalledJava? FindJavaById(string? javaId) =>
        string.IsNullOrEmpty(javaId) ? null : Config.InstalledJavas.FirstOrDefault(j => j.Id == javaId);

    /// <summary>
    /// 解析一个 Java 列表条目实际能不能用：路径文件还存在就返回它的 javaw.exe 路径，
    /// 文件已经被移动/删除则返回 null，调用方应该回退到旧的自动探测逻辑，而不是直接崩溃。
    /// </summary>
    public string? ResolveJavaPath(string? javaId)
    {
        var entry = FindJavaById(javaId);
        if (entry == null || string.IsNullOrEmpty(entry.JavawPath) || !File.Exists(entry.JavawPath))
            return null;
        return entry.JavawPath;
    }

    /// <summary>
    /// 访客模式开启时(cfg.GuestModeEnabled)，永远优先返回 GuestAccount（本次会话的临时账户），
    /// 完全跳过真实保存的账户列表——这是访客模式"不使用/不暴露已保存账户"这一诉求的核心实现点。
    /// 访客模式关闭时行为不变：优先用上次选中记录的账户，其次第一个标记为选中的，最后兜底第一个。
    /// </summary>
    public Account? GetSelectedAccount()
    {
        if (Config.GuestModeEnabled && GuestAccount != null) return GuestAccount;

        return Accounts.FirstOrDefault(a => a.Id == Config.LastSelectedAccountId) ??
               Accounts.FirstOrDefault(a => a.IsSelected) ??
               Accounts.FirstOrDefault();
    }

    public void SelectAccount(string accountId)
    {
        foreach (var a in Accounts) a.IsSelected = a.Id == accountId;
        Config.LastSelectedAccountId = accountId;
        SaveAccounts();
        Save();
    }

    public void AddOrUpdateAccount(Account account)
    {
        // 每次账户被"新建/登录成功/静默刷新成功"地写入这里，都视为一次成功的在线校验，
        // 顺手盖个时间戳——这是"令牌保留时效"降级判断的依据（见 AppConfig.
        // AccountTokenGracePeriodDays 与 MainWindow 里 Java 版账户刷新失败后的降级分支）。
        // 只对微软账户有意义，但其它类型账户存这个字段也无害，不特殊区分。
        account.LastVerifiedAtUtc = DateTime.UtcNow;

        var existing = Accounts.FirstOrDefault(a => a.Id == account.Id);
        if (existing != null)
        {
            // 更新已存在的账户（比如微软账户静默刷新 token 后整个替换成新对象）：
            // 保留原来的 CreatedAtUtc，不能让"刷新 token"这种跟用户无感的后台动作
            // 把这个账户的创建时间冲成"现在"，否则"默认高亮最近创建的账户"这个需求
            // 会被每次自动刷新悄悄破坏——账户列表里最早创建的那个，只因为最近登录时
            // 触发了一次静默刷新，就会被误判成"最新创建"。
            account.CreatedAtUtc = existing.CreatedAtUtc;
            Accounts.Remove(existing);
        }
        Accounts.Add(account);
        SaveAccounts();
    }

    /// <summary>
    /// 取"最近创建"的账户（按 CreatedAtUtc 排序，不受列表存储顺序/刷新更新影响）。
    /// 供账户选择弹窗默认高亮使用——没有账户时返回 null。
    /// </summary>
    public Account? GetMostRecentlyCreatedAccount() =>
        Accounts.OrderByDescending(a => a.CreatedAtUtc).FirstOrDefault();

    public void RemoveAccount(string accountId)
    {
        Accounts.RemoveAll(a => a.Id == accountId);
        SaveAccounts();
        // 顺手清理这个账户可能保存过的自定义皮肤文件，避免残留孤儿文件。
        new SkinService().RemoveCustomSkin(accountId);
    }

    // ===================== 危险操作：需要业务层先做 "xztx127" 二次确认 =====================
    // 三个方法本身不再要求调用方传入确认码——确认码校验是 UI 层的责任（弹窗输入框比对），
    // 这里只提供"确认通过之后，具体做什么"的执行逻辑，保持职责分离：这层不关心确认码
    // 长什么样、从哪个控件读出来，只保证"一旦被调用，就正确地只做该做的那一件事，
    // 绝不波及范围之外的任何东西"。

    /// <summary>关闭注册表功能：只是把开关关掉、保存 config.json，不删除已写入的注册表内容
    /// （已有的注册表项原样留着，万一用户以后又重新打开这个开关，还能读回来）。</summary>
    public void DisableRegistryFeature()
    {
        Config.RegistryFeatureEnabled = false;
        RegistryFeatureEnabled = false;
        Save(); // Save() 内部会因为 RegistryFeatureEnabled=false 而跳过写注册表，只落盘 config.json
    }

    /// <summary>删除所有新增的启动器注册表项（HKLM 和 HKCU 下的 SOFTWARE\XCL2 键本身，
    /// 不触碰这个键以外的任何注册表内容）。不自动关闭注册表功能开关——删除后如果用户继续
    /// 使用，下次 Save() 只要 RegistryFeatureEnabled 还是 true，就会重新按当前 config.json
    /// 的值把这些键写回去；如果用户是想"彻底不再使用"，应该在删除前先调用
    /// <see cref="DisableRegistryFeature"/>。</summary>
    public (bool HklmDeleted, bool HkcuDeleted) DeleteAllRegistryEntries() =>
        RegistryConfigService.DeleteXcl2Key();

    /// <summary>
    /// 清除 XCL2 在本机存在的痕迹：删除注册表项 + 删除 xcl2 数据目录（config.json、
    /// accounts.json、日志、下载缓存的 Java 运行时、导出的启动脚本等全部内容）。
    ///
    /// **范围硬编码锁死**：只删除 <see cref="App.DataDir"/>（即程序运行目录下的 "xcl2" 文件夹）
    /// 和注册表里的 SOFTWARE\XCL2 键，这两个路径都是编译期常量，不接受任何外部传参覆盖——
    /// 这是"注意界限，不要把此电脑删了"这条要求的直接落实：以前的离谱 bug 就是删除范围
    /// 被做成了可配置/可传参，这里刻意反过来，把范围焊死在代码里，杜绝任何"传错参数就删掉
    /// 不该删的东西"的可能性。**绝不删除 .minecraft 游戏目录、不删除用户任何其它文件**。
    ///
    /// 调用这个方法后，进程本身应该立刻退出（config.json 已经被删了，继续跑下去的任何
    /// Save() 调用都会把目录重新建出来，等于清理了个寂寞）——退出逻辑由 UI 层负责。
    /// </summary>
    public void ClearAllTraces()
    {
        RegistryConfigService.DeleteXcl2Key();

        try
        {
            if (Directory.Exists(App.DataDir))
                Directory.Delete(App.DataDir, recursive: true);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"清除本机痕迹时删除数据目录失败：{App.DataDir}", ex);
            throw; // 向上抛给 UI 层：这一步失败了必须让用户知道，不能假装"清除成功"
        }
    }

    // ===================== 导出 / 导入 =====================

    /// <summary>"导出注册表 (.reg)"：见 <see cref="RegistryConfigService.ExportToRegFileContent"/>。
    /// 两支都没有 XCL2 键时返回 null。</summary>
    public string? ExportRegistryFile() => RegistryConfigService.ExportToRegFileContent();

    /// <summary>
    /// "导出所有配置"：把 xcl2/config.json + 注册表镜像字段的当前值 + 各实例的
    /// versions/&lt;id&gt;/xcl/settings.json（若存在）打包成一份 JSON 归档，
    /// 返回归档文件的完整文本内容，调用方（UI 层）负责写文件/弹保存对话框。
    /// accounts.json 里的 refresh token 属于敏感凭据，默认不打包进去（只导出账户的
    /// 用户名/UUID/账户类型这些非敏感展示信息），避免导出文件被别人拿到后能冒用登录状态。
    /// </summary>
    public string ExportAllConfig()
    {
        var archive = new ConfigArchive
        {
            ExportedAtUtc = DateTime.UtcNow,
            Config = Config,
            RegistrySnapshot = new Dictionary<string, string?>
            {
                ["AgreementsAccepted"] = Config.AgreementsAccepted.ToString(),
                ["AcceptedAgreementVersion"] = Config.AcceptedAgreementVersion.ToString(),
                ["RestrictedMode"] = Config.RestrictedMode.ToString(),
                ["UiSkin"] = Config.UiSkin,
                ["IsDarkMode"] = Config.IsDarkMode.ToString(),
                ["LauncherLanguage"] = Config.LauncherLanguage,
            },
            AccountSummaries = Accounts.Select(a => new AccountSummary
            {
                Id = a.Id,
                Type = a.Type.ToString(),
                Username = a.Username,
                Uuid = a.Uuid
            }).ToList(),
            InstanceSettings = CollectInstanceSettings()
        };
        return JsonSerializer.Serialize(archive, JsonOpts);
    }

    /// <summary>扫描所有已注册文件夹下 versions/*/xcl/settings.json，收集进导出归档。
    /// 找不到/损坏的实例设置文件直接跳过，不影响其它实例正常导出。</summary>
    private Dictionary<string, string> CollectInstanceSettings()
    {
        var result = new Dictionary<string, string>();
        foreach (var folder in Config.Folders)
        {
            var versionsDir = Path.Combine(folder.Path, "versions");
            if (!Directory.Exists(versionsDir)) continue;

            foreach (var versionDir in Directory.GetDirectories(versionsDir))
            {
                var settingsPath = InstanceConfigService.GetSettingsPath(versionDir);
                if (!File.Exists(settingsPath)) continue;
                try
                {
                    var key = $"{folder.Path}|{Path.GetFileName(versionDir)}";
                    result[key] = File.ReadAllText(settingsPath);
                }
                catch { /* 单个实例设置读取失败不影响整体导出 */ }
            }
        }
        return result;
    }

    /// <summary>
    /// "导入配置"：解析一份由 <see cref="ExportAllConfig"/> 产出的归档 JSON，写回 config.json、
    /// 注册表镜像字段、以及各实例的 xcl/settings.json。导入的 <see cref="AppConfig"/> 会整体
    /// 替换当前内存中的 Config（导入配置本来的诉求就是"恢复/迁移一份完整设置"，不是逐字段
    /// 合并），随后立即 Save() 落盘（含注册表镜像同步）。accounts.json 不受导入影响——
    /// 归档里只有非敏感的账户展示信息，不含可用于登录的凭据，不会、也不能拿它覆盖真实账户列表。
    /// 返回成功导入的实例设置数量，供 UI 层提示。
    /// </summary>
    public int ImportAllConfig(string archiveJson)
    {
        var archive = JsonSerializer.Deserialize<ConfigArchive>(archiveJson)
                      ?? throw new InvalidOperationException("配置归档文件格式无法识别。");

        if (archive.Config != null)
        {
            Config = archive.Config;
            Config.Folders ??= new List<GameFolder>();
            Config.InstalledJavas ??= new List<InstalledJava>();
            Config.FavoriteItems ??= new List<FavoriteItem>();
        }

        var restoredInstances = 0;
        if (archive.InstanceSettings != null)
        {
            foreach (var (key, json) in archive.InstanceSettings)
            {
                var parts = key.Split('|', 2);
                if (parts.Length != 2) continue;
                var versionDir = Path.Combine(parts[0], "versions", parts[1]);
                if (!Directory.Exists(versionDir)) continue; // 目标实例本机不存在，跳过不硬造目录
                try
                {
                    var settingsPath = InstanceConfigService.GetSettingsPath(versionDir);
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                    File.WriteAllText(settingsPath, json);
                    restoredInstances++;
                }
                catch { /* 单个实例写入失败不影响其它实例继续导入 */ }
            }
        }

        Save();
        EnsureDefaultFolder();
        return restoredInstances;
    }
}

/// <summary>"导出所有配置"归档的顶层结构，见 ConfigService.ExportAllConfig。</summary>
public class ConfigArchive
{
    public DateTime ExportedAtUtc { get; set; }
    public Models.AppConfig? Config { get; set; }
    public Dictionary<string, string?> RegistrySnapshot { get; set; } = new();
    public List<AccountSummary> AccountSummaries { get; set; } = new();
    /// <summary>key 格式："&lt;文件夹路径&gt;|&lt;版本ID&gt;"，value 是该实例 xcl/settings.json 原始内容。</summary>
    public Dictionary<string, string> InstanceSettings { get; set; } = new();
}

/// <summary>导出归档里的账户信息——只含非敏感展示字段，不含任何可用于登录的凭据。</summary>
public class AccountSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
}
