using System.IO;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「电脑清理」：跟 JunkCleanupService（只清 .minecraft 目录里的东西）不同，
/// 这个是清理整台电脑级别的临时垃圾——用户临时目录(%TEMP%)、系统 Windows\Temp、
/// 浏览器/系统更新留下的常见缓存目录。
///
/// 安全边界（宁可少清，不能误删）：
/// - 只删"临时/缓存"性质、删除后系统/程序会自动重新生成的目录，不碰用户文档、
///   下载目录、桌面等任何"用户主动创建的数据"。
/// - 只清超过 <see cref="OldFileThresholdHours"/> 小时没修改过的文件——刚生成的临时文件
///   很可能是某个正在运行的程序这一刻还在用，过早删除容易导致该程序出错甚至崩溃。
/// - 单个文件/目录删除失败（正被占用、权限不足）直接跳过，不中断整体流程、不弹错误打断用户，
///   这类"删不掉"本来就是临时文件被清理工具处理时的常态。
/// - 不清空回收站、不碰注册表、不做"系统深度清理"这类有风险的操作——这些不是这个工具
///   要覆盖的范围，真有需要用户可以自己去用 Windows 自带的"磁盘清理"。
/// </summary>
public static class SystemJunkCleanupService
{
    public record JunkScanResult(List<JunkItem> Items, long TotalBytes);
    public record JunkItem(string Path, long SizeBytes, string Category, bool IsDirectory);

    /// <summary>文件超过这个小时数没被修改过，才视为"大概率不会再被正在运行的程序用到"。
    /// 临时文件夹里经常混着"进行中任务"的中间产物，阈值定得比游戏垃圾清理(7天)短很多，
    /// 因为这里清理的对象生命周期通常以"小时"为单位，而不是"天"。</summary>
    private const int OldFileThresholdHours = 6;

    public static JunkScanResult Scan()
    {
        var items = new List<JunkItem>();
        var cutoff = DateTime.Now.AddHours(-OldFileThresholdHours);

        void ScanDir(string? dir, string category)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            // 顶层文件
            foreach (var file in SafeEnumerateFiles(dir))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime < cutoff)
                        items.Add(new JunkItem(file, info.Length, category, false));
                }
                catch { /* 单个文件读取失败(可能正被占用)，跳过继续扫描其它文件 */ }
            }

            // 子目录：作为一个整体加入(删除时整目录删除)，用目录总大小 + 最后写入时间判断，
            // 避免逐个子文件枚举导致临时文件数量很大时(有些浏览器缓存能到几万个小文件)
            // 扫描本身耗时过久、卡住界面。
            foreach (var subDir in SafeEnumerateDirectories(dir))
            {
                try
                {
                    var dirInfo = new DirectoryInfo(subDir);
                    if (dirInfo.LastWriteTime >= cutoff) continue;
                    var size = SafeDirectorySize(subDir);
                    items.Add(new JunkItem(subDir, size, category, true));
                }
                catch { /* 单个子目录读取失败，跳过 */ }
            }
        }

        // 当前用户的临时目录：绝大多数安装程序/浏览器/office/mc 启动器自己都会往这里扔
        // 临时文件，是最大头也最安全的一块。
        ScanDir(Path.GetTempPath(), "用户临时文件(%TEMP%)");

        // 系统级临时目录：需要管理员权限才能真正删除里面部分文件，删不掉的会在 Delete 阶段
        // 被跳过，不影响其它部分照常清理。
        var windowsTemp = Environment.GetEnvironmentVariable("SystemRoot") is { } sysRoot
            ? Path.Combine(sysRoot, "Temp")
            : null;
        ScanDir(windowsTemp, "系统临时文件(Windows\\Temp)");

        // 常见浏览器的磁盘缓存目录(纯缓存，删除只是让下次打开网页时重新缓存，不会丢登录状态
        // /书签/历史记录这些实际数据——那些存在 Cookies/Login Data 等文件里，这里不会碰)。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            ScanDir(Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"), "Chrome 网页缓存");
            ScanDir(Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"), "Edge 网页缓存");
            ScanDir(Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles"), "Firefox 相关缓存");
        }

        return new JunkScanResult(items, items.Sum(i => i.SizeBytes));
    }

    /// <summary>实际删除扫描出的垃圾文件/目录，返回成功删除的数量和释放的字节数。
    /// 单个失败（占用中/权限不足）不中断整体流程，跳过继续处理下一个——这在系统临时目录
    /// 场景下尤其常见，是预期之内的正常情况，不是错误。</summary>
    public static (int deletedCount, long freedBytes) Delete(List<JunkItem> items)
    {
        var deletedCount = 0;
        long freedBytes = 0;
        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.Path)) continue;
                    Directory.Delete(item.Path, recursive: true);
                }
                else
                {
                    if (!File.Exists(item.Path)) continue;
                    File.Delete(item.Path);
                }
                deletedCount++;
                freedBytes += item.SizeBytes;
            }
            catch { /* 单个文件/目录删除失败(常见于系统临时目录里正被占用的文件)，跳过 */ }
        }
        return (deletedCount, freedBytes);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static long SafeDirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* 单个文件读取失败，跳过 */ }
            }
        }
        catch { /* 目录本身遍历失败(权限等)，返回目前累计到的大小 */ }
        return total;
    }
}
