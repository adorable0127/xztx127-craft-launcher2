using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 启动器启动时自动扫描本机可能存在的 .minecraft 文件夹（不需要用户手动一个个"添加文件夹"）。
/// 扫描范围两块：
///  1. %AppData%（也就是官方启动器默认装的地方，Roaming\.minecraft）——绝大多数玩家不管用
///     什么启动器，第一次装的时候大概率落在这里，命中率最高，优先扫。
///  2. 每个逻辑磁盘分区的 1/2/3 级目录——覆盖"装在 D 盘游戏目录""E:\Games\Minecraft\.minecraft"
///     这类自定义路径，但只扫到第三级，不递归到底，避免在数据盘上跑一次全盘扫描（可能几十万
///     个文件夹，耗时不可接受，还容易扫到 .minecraft 命名的无关内容比如某些 mod 开发工程）。
///
/// 找到即算数：只要一个目录名字面上叫 ".minecraft"，就当作候选，不强制要求里面已经有
/// versions/saves 等子目录——刚创建还没下载任何版本的空 .minecraft 文件夹也是合法的
/// Minecraft 目录，不应该被排除在外。
///
/// 去重：跟 config.Folders 里已经存在的路径（大小写不敏感，路径分隔符统一）比较，已经加过的
/// 不重复添加，避免每次启动都往列表里塞重复项。
///
/// 排除标记(.NOXCL)：如果某个目录下（不管是不是 .minecraft 目录本身，还是往下探的中间目录）
/// 存在一个叫 ".NOXCL" 的文件/文件夹（内容不重要，只看存不存在），本次自动扫描就跳过它：
///   - 如果标记放在 .minecraft 目录里面：这个 .minecraft 不会被自动加入列表；
///   - 如果标记放在中间目录（比如 D:\SomeModProject）：直接不再往它内部递归，避免在被明确
///     标记为"不是游戏目录"的地方（例如某些 mod 开发工程根目录也可能叫 .minecraft 相关名字）
///     浪费扫描时间，也防止误报。
/// 注意：这个排除只影响"自动扫描"这一步。用户如果通过"添加已有文件夹"手动把这个目录加进来，
/// 依然可以正常启动、管理——.NOXCL 不会拦用户的手动操作，只是让自动探测绕开它。
/// </summary>
public static class MinecraftFolderScanService
{
    private const string FolderName = ".minecraft";

    /// <summary>排除标记：目录下存在这个名字的文件/文件夹，扫描就跳过该目录（不阻止手动添加）。</summary>
    public const string ExcludeMarkerName = ".NOXCL";

    /// <summary>
    /// 判断某个目录是否带有 .NOXCL 排除标记。只看是否存在同名条目（文件或文件夹都算），
    /// 不关心内容。IO 异常一律当作"没有标记"处理，不能因为探测标记本身出错而挂掉扫描。
    /// </summary>
    public static bool HasExcludeMarker(string dir)
    {
        try
        {
            var marker = Path.Combine(dir, ExcludeMarkerName);
            return File.Exists(marker) || Directory.Exists(marker);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>逐级往下探的最大深度：磁盘根目录本身算第 0 级，1/2/3 级分别往下探三层。
    /// 深度越大耗时越久，3 层是"能覆盖大多数自定义安装路径"和"扫描耗时可接受"之间的折衷，
    /// 需求原文也是明确写"1、2、3 级目录"。</summary>
    private const int MaxDepth = 3;

    /// <summary>
    /// 执行一次扫描并把新发现的 .minecraft 目录以 GameFolder 形式加入 config.Folders
    /// （复用 FolderService.AddFolder，保持"添加文件夹"这个动作只有一处实现）。
    /// 返回本次新增的文件夹列表（可能为空），调用方可以用这个列表决定要不要弹提示告知用户。
    ///
    /// 全程包在 try/catch 里、每个候选目录单独判断异常：扫描不到、没权限访问某个分区
    /// 都不应该让启动器直接崩掉或者卡住——枚举目录失败（权限不足、盘符没插盘等）的分区
    /// 直接跳过，不影响其余分区正常扫描。
    /// </summary>
    public static List<GameFolder> ScanAndRegister(AppConfig config)
    {
        var folderService = new FolderService();
        var found = new List<string>();

        // 1. AppData\Roaming\.minecraft（官方启动器默认位置，命中率最高，优先扫这里）
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                var candidate = Path.Combine(appData, FolderName);
                if (Directory.Exists(candidate) && !HasExcludeMarker(candidate)) found.Add(candidate);
            }
        }
        catch { /* 拿不到 AppData 路径就跳过，不影响后续磁盘扫描 */ }

        // 2. 每个已就绪的逻辑磁盘分区，往下探 1~3 级目录
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!SafeIsReady(drive)) continue;
                ScanDriveForMinecraftFolders(drive.RootDirectory.FullName, found);
            }
        }
        catch { /* 极端情况下 DriveInfo.GetDrives() 本身失败也不应该拖垮启动流程 */ }

        // 去重 + 跟已有配置比对，只留下真正"新发现"的
        var existingNormalized = config.Folders
            .Select(f => NormalizePath(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newlyAdded = new List<GameFolder>();
        foreach (var path in found.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = NormalizePath(path);
            if (existingNormalized.Contains(normalized)) continue;

            var folder = folderService.AddFolder(config, path, GuessDisplayName(path));
            newlyAdded.Add(folder);
            existingNormalized.Add(normalized); // 防止同一次扫描内的重复路径被加两次
        }

        return newlyAdded;
    }

    /// <summary>逐级递归查找名字是 .minecraft 的子目录，最多下探 MaxDepth 层。
    /// 找到一个 .minecraft 目录就不再往它内部继续递归——.minecraft 目录内部本身可能有
    /// 几百个 mod/资源文件，没必要也不应该在里面继续找"更深一层的 .minecraft"。</summary>
    private static void ScanDriveForMinecraftFolders(string dir, List<string> found, int depth = 0)
    {
        if (depth > MaxDepth) return;

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return; // 没权限访问 / 目录已被移除等，跳过这一层，不影响兄弟目录
        }

        foreach (var sub in subDirs)
        {
            string name;
            try { name = Path.GetFileName(sub); }
            catch { continue; }

            if (string.IsNullOrEmpty(name)) continue;

            // 跳过系统/回收站等明显不会是游戏目录、且体积或权限问题容易拖慢扫描的特殊目录
            if (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
                continue;

            // .NOXCL 排除标记：不管这个目录是不是 .minecraft，只要带了标记就整个跳过——
            // 既不把它收进自动发现列表（哪怕它就叫 .minecraft），也不再往它内部递归查找。
            // 只影响自动扫描；用户手动"添加已有文件夹"不受此限制。
            if (HasExcludeMarker(sub)) continue;

            if (string.Equals(name, FolderName, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(sub);
                continue; // 命中了就不再往这个目录内部继续扫
            }

            ScanDriveForMinecraftFolders(sub, found, depth + 1);
        }
    }

    /// <summary>DriveInfo.IsReady 在某些异常盘符（没插盘的读卡器、断开的网络映射盘）上
    /// 本身可能抛异常而不是老老实实返回 false，这里包一层保护。</summary>
    private static bool SafeIsReady(DriveInfo drive)
    {
        try { return drive.IsReady; }
        catch { return false; }
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')).ToLowerInvariant();

    /// <summary>给自动发现的文件夹起个比"当前文件夹"更有辨识度的显示名，带上上一级目录名，
    /// 方便用户在有多个 .minecraft 时分清楚哪个是哪个，比如 "Games\.minecraft" 而不是
    /// 一堆都叫 ".minecraft" 分不清谁是谁。</summary>
    private static string GuessDisplayName(string path)
    {
        var parent = Directory.GetParent(path)?.Name;
        return string.IsNullOrEmpty(parent) ? FolderName : $"{parent}\\{FolderName}";
    }
}
