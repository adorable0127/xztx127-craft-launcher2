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
    }

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
}
