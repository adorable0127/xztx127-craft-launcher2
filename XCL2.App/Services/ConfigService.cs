using System.IO;
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
        catch { Config = new AppConfig(); }

        // 反序列化后，若 JSON 中某属性显式写了 null（旧版本/手动编辑损坏的配置文件），
        // 该属性会被覆盖为 null 而不会走字段初始值，这里做兜底修复，避免后续 NullReferenceException。
        Config.Folders ??= new List<GameFolder>();
        Config.LastSelectedAccountId ??= "";
        Config.FavoriteVersionIds ??= new List<string>();
        Config.VersionIsolationOverrides ??= new Dictionary<string, bool>();
        Config.VersionJavaOverrides ??= new Dictionary<string, int>();
        Config.InstalledJavas ??= new List<InstalledJava>();
        Config.InstalledJavas.RemoveAll(j => j == null); // 清理数组中可能存在的 null 元素
        Config.VersionJavaIdOverrides ??= new Dictionary<string, string>();

        try
        {
            if (File.Exists(AccountsPath))
            {
                var json = File.ReadAllText(AccountsPath);
                Accounts = JsonSerializer.Deserialize<List<Account>>(json) ?? new List<Account>();
            }
        }
        catch { Accounts = new List<Account>(); }

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
            Source = source
        };
        Config.InstalledJavas.Add(entry);
        return entry;
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
        if (existing != null) Accounts.Remove(existing);
        Accounts.Add(account);
        SaveAccounts();
    }

    public void RemoveAccount(string accountId)
    {
        Accounts.RemoveAll(a => a.Id == accountId);
        SaveAccounts();
        // 顺手清理这个账户可能保存过的自定义皮肤文件，避免残留孤儿文件。
        new SkinService().RemoveCustomSkin(accountId);
    }
}
