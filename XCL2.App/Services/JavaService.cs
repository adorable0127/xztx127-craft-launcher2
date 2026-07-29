using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace XCL2.App.Services;

/// <summary>Java 下载安装方式：Portable=解压到 xcl2/runtime 便携使用；System=安装到系统 Program Files 目录（需管理员权限运行安装程序）。</summary>
public enum JavaInstallMode
{
    Portable,
    System
}

/// <summary>下载 Java 前的用户选择：主版本号(8~26)、架构(x64/x86)、安装方式。</summary>
public record JavaDownloadRequest(int MajorVersion, string Arch, JavaInstallMode InstallMode);

/// <summary>全盘扫描找到的一个 Java 候选：javaw.exe 路径 + (尽力探测到的)版本号字符串，探测失败时 Version 为 null。</summary>
public record JavaCandidate(string JavawPath, string? Version);

/// <summary>
/// Java 运行时管理：
/// - 探测系统已安装 / 已下载便携版 Java。
/// - 支持按用户指定的版本(8~26)、架构(x64/x86)、安装方式(便携zip/系统安装)下载。
/// 下载优先走 Adoptium(Eclipse Temurin) 官方 API（有完整版本查询能力），
/// 失败时回退 BMCLAPI 镜像（仅覆盖常见 LTS 版本 8/11/17/21）。
/// </summary>
public class JavaService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public string RuntimeDir { get; } = Path.Combine(App.DataDir, "runtime");

    /// <summary>Adoptium 目前发布 GA 正式版的主版本号（LTS + 最新特性版）。其余版本号大多只有 EA/已过期，选择时给出提示。</summary>
    public static readonly int[] KnownGoodMajorVersions = { 8, 11, 17, 21, 25, 26 };

    /// <summary>
    /// 尝试寻找可用的 java.exe：先看配置里手动指定的路径，再看已下载的便携版，最后看
    /// JAVA_HOME/注册表/PATH。
    ///
    /// 重要修复：之前这里只要 configuredPath 存在就直接返回，完全不管 preferMajorVersion，
    /// 也不检查这个 Java 到底是不是对应版本——一旦用户曾经配置/下载过一个 Java（哪怠版本不对），
    /// 之后所有版本都会硬用这一个，造成 "class file version 69.0...only recognizes up to 65.0"
    /// 这种"版本要求的 Java 和实际启动用的 Java 对不上"的崩溃，且自动匹配形同虚设。
    /// 现在改为：当调用方明确要求了某个主版本号时，会先用 java.exe -version 实际探测每个候选
    /// (而不是猜文件夹名字)，只有版本真正吻合才采用；不吻合就继续往下找/最终返回 null
    /// 交给上层去下载正确版本。没有指定 preferMajorVersion 时行为不变(不做版本校验，能用就行)。
    /// </summary>
    public string? FindJava(string? configuredPath, int? preferMajorVersion = null)
    {
        // 收集所有"候选"，而不是找到第一个就直接返回——这样在有版本要求时可以继续往下找
        // 真正匹配的那一个，而不是被一个凑巧存在但版本不对的路径卡住。
        var orderedCandidates = new List<string>();

        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
            orderedCandidates.Add(configuredPath);

        if (Directory.Exists(RuntimeDir))
        {
            orderedCandidates.AddRange(
                Directory.GetDirectories(RuntimeDir)
                    .Select(dir => Path.Combine(dir, "bin", "javaw.exe"))
                    .Where(File.Exists));
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var p = Path.Combine(javaHome, "bin", "javaw.exe");
            if (File.Exists(p)) orderedCandidates.Add(p);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\Java Runtime Environment")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\JDK");
            var version = key?.GetValue("CurrentVersion") as string;
            if (version != null)
            {
                using var verKey = key!.OpenSubKey(version);
                var home = verKey?.GetValue("JavaHome") as string;
                if (home != null)
                {
                    var p = Path.Combine(home, "bin", "javaw.exe");
                    if (File.Exists(p)) orderedCandidates.Add(p);
                }
            }
        }
        catch { /* 忽略注册表访问异常 */ }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var p = Path.Combine(dir.Trim(), "javaw.exe");
            if (File.Exists(p)) orderedCandidates.Add(p);
        }

        orderedCandidates = orderedCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (orderedCandidates.Count == 0) return null;

        if (preferMajorVersion is not > 0)
            return orderedCandidates[0]; // 没有版本要求：保持旧行为，第一个能用的就用

        // 有版本要求：实际探测每个候选的版本号（而不是猜文件夹名），找到第一个真正匹配的。
        foreach (var candidate in orderedCandidates)
        {
            var detected = TryGetJavaMajorVersionSync(candidate);
            if (detected == preferMajorVersion.Value) return candidate;
        }

        // 没有任何候选匹配要求的版本：返回 null，让上层去下载正确版本，
        // 而不是硬塞一个版本不对的 Java 导致 UnsupportedClassVersionError。
        return null;
    }

    /// <summary>同步版本探测（FindJava 是同步方法，不方便改造成 async）：调用 "java -version"，从形如
    /// java version "21.0.5" / openjdk version "1.8.0_412" 的输出里解析出主版本号。
    /// 公开出来是因为启动前"检查用户手动指定的 Java 跟这个版本要求是否匹配"(MainWindow.LaunchInternalAsync)
    /// 需要复用同一份探测逻辑，不想再实现一份重复的调进程代码。</summary>
    public static int? TryGetJavaMajorVersionSync(string javawPath)
    {
        try
        {
            var javaExe = Path.Combine(Path.GetDirectoryName(javawPath) ?? "", "java.exe");
            if (!File.Exists(javaExe)) return null;

            var psi = new System.Diagnostics.ProcessStartInfo(javaExe, "-version")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            return ParseJavaMajorVersion(output);
        }
        catch { return null; }
    }

    /// <summary>
    /// 解析 "java -version" 输出里的主版本号。兼容两种历史格式：
    /// - 旧式(Java 8 及以前): "1.8.0_412" -> 主版本号是第二段 "8"
    /// - 新式(Java 9+，JEP 223 起): "21.0.5" / "17" -> 主版本号就是第一段
    /// </summary>
    internal static int? ParseJavaMajorVersion(string versionOutput)
    {
        var idx = versionOutput.IndexOf('"');
        var lastIdx = versionOutput.LastIndexOf('"');
        if (idx < 0 || lastIdx <= idx) return null;
        var versionString = versionOutput.Substring(idx + 1, lastIdx - idx - 1);

        var parts = versionString.Split('.', '_', '-');
        if (parts.Length == 0) return null;
        if (!int.TryParse(parts[0], out var first)) return null;

        // "1.8.0_412" 这种旧格式第一段永远是 1，真正版本号在第二段。
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second))
            return second;

        return first;
    }

    /// <summary>
    /// 快速探测本机 Java（轻量版，秒回）：只查常见位置——便携版目录(RuntimeDir)、JAVA_HOME、
    /// 注册表、PATH 环境变量，不遍历整个磁盘，供设置页 Java 列表的"刷新"按钮使用。
    /// 和 FindJava() 走的是同一批候选来源，区别是这里返回全部候选(带实测版本号)供用户在列表里
    /// 挑选/一键批量登记，而不是只取第一个能用的。跟 ScanWholeDiskForJavaAsync（全盘扫描，
    /// 需要用户二次确认、耗时可能几分钟）是两个不同粒度的功能，不要混用。
    /// </summary>
    public async Task<List<JavaCandidate>> QuickDetectJavaAsync(CancellationToken ct = default)
    {
        var orderedCandidates = new List<string>();

        if (Directory.Exists(RuntimeDir))
        {
            orderedCandidates.AddRange(
                Directory.GetDirectories(RuntimeDir)
                    .Select(dir => Path.Combine(dir, "bin", "javaw.exe"))
                    .Where(File.Exists));
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var p = Path.Combine(javaHome, "bin", "javaw.exe");
            if (File.Exists(p)) orderedCandidates.Add(p);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\Java Runtime Environment")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\JDK");
            var version = key?.GetValue("CurrentVersion") as string;
            if (version != null)
            {
                using var verKey = key!.OpenSubKey(version);
                var home = verKey?.GetValue("JavaHome") as string;
                if (home != null)
                {
                    var p = Path.Combine(home, "bin", "javaw.exe");
                    if (File.Exists(p)) orderedCandidates.Add(p);
                }
            }

            // 常见安装场景里注册表下会有多个版本子键(不止 CurrentVersion 指向的那个)，
            // 例如同时装了 Java 8 和 Java 21——把每个子键都探测一遍，而不是只看 CurrentVersion。
            using var jreRoot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\Java Runtime Environment");
            using var jdkRoot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft\JDK");
            foreach (var root in new[] { jreRoot, jdkRoot })
            {
                if (root == null) continue;
                foreach (var subName in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(subName);
                    var home = sub?.GetValue("JavaHome") as string;
                    if (home == null) continue;
                    var p = Path.Combine(home, "bin", "javaw.exe");
                    if (File.Exists(p)) orderedCandidates.Add(p);
                }
            }
        }
        catch { /* 忽略注册表访问异常 */ }

        // Eclipse Adoptium 系统安装模式常见的固定路径，注册表键名跟版本走(不一定叫上面那两个)，
        // 直接兜底扫一下这个目录本身，避免用户用"系统安装"方式装的 Java 探测不到。
        try
        {
            const string adoptiumRoot = @"C:\Program Files\Eclipse Adoptium";
            if (Directory.Exists(adoptiumRoot))
            {
                orderedCandidates.AddRange(
                    Directory.GetDirectories(adoptiumRoot)
                        .Select(dir => Path.Combine(dir, "bin", "javaw.exe"))
                        .Where(File.Exists));
            }
        }
        catch { /* 忽略访问异常 */ }

        // 官方启动器 / HMCL / PCL 等主流第三方启动器下载的便携版 Java 大多放在用户 AppData 目录下
        // （例如官方启动器的 %AppData%\.minecraft\runtime\，HMCL 的 %AppData%\.hmcl\java\ 等），
        // 之前完全没有扫描过这些位置，导致"明明用其他启动器下载过 Java，这边却探测不到"。
        // 这里补上对几个常见启动器 AppData 目录的扫描，只扫固定的几层子目录，不做递归全盘扫描
        // （那是 ScanWholeDiskForJavaAsync 的职责），保持这个方法本身"几秒内完成"的定位。
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                var appDataJavaRoots = new[]
                {
                    Path.Combine(appData, ".minecraft", "runtime"),   // 官方启动器
                    Path.Combine(appData, ".hmcl", "java"),           // HMCL
                    Path.Combine(appData, "PCL", "Java"),             // PCL / PCL2
                    Path.Combine(appData, "PCL2", "Java"),
                };
                foreach (var root in appDataJavaRoots)
                {
                    if (!Directory.Exists(root)) continue;
                    // 这些目录下 Java 安装通常再嵌套 1~2 层(如 runtime/java-runtime-gamma/windows-x64/xxx/bin)，
                    // 用 AllDirectories 递归查找 javaw.exe，但只在这个已知的固定根目录下递归，
                    // 范围很小，不会像全盘扫描那样耗时。
                    try
                    {
                        orderedCandidates.AddRange(
                            Directory.GetFiles(root, "javaw.exe", SearchOption.AllDirectories));
                    }
                    catch { /* 单个根目录访问失败不影响其余根目录 */ }
                }
            }
        }
        catch { /* 忽略 AppData 路径解析异常 */ }

        // 其他常见第三方 JDK 发行版：Azul Zulu、Amazon Corretto、BellSoft Liberica、
        // Microsoft Build of OpenJDK、Oracle JDK。这些发行版大多不走上面
        // SOFTWARE\JavaSoft\... 这条标准注册表路径，而是各自用自己的键名/固定安装目录，
        // 之前只探测标准路径时会完全漏掉这些——如果用户反馈"明明装了 Java 但探测不到"，
        // 大概率就是装的这几种发行版之一。这里同时兜底"固定安装目录扫一遍" +
        // "各自的注册表键（如果有）"两种方式，尽量覆盖默认安装选项。
        var thirdPartyInstallRoots = new[]
        {
            @"C:\Program Files\Zulu",                 // Azul Zulu 默认安装目录
            @"C:\Program Files\Amazon Corretto",       // Amazon Corretto
            @"C:\Program Files\BellSoft\LibericaJDK",  // BellSoft Liberica
            @"C:\Program Files\Microsoft\jdk",         // Microsoft Build of OpenJDK (msi 默认)
            @"C:\Program Files\Java",                  // Oracle JDK 传统默认目录
        };
        foreach (var root in thirdPartyInstallRoots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                orderedCandidates.AddRange(
                    Directory.GetDirectories(root)
                        .Select(dir => Path.Combine(dir, "bin", "javaw.exe"))
                        .Where(File.Exists));
            }
            catch { /* 忽略单个目录的访问异常，不影响其余候选的探测 */ }
        }

        // 部分发行版自己的注册表根键（不是每个都有，逐个 try 互不影响）：
        // - Zulu 通常写在 SOFTWARE\Azul Systems\Zulu 下，子键名形如 "zulu-21"
        // - Corretto 有的版本会额外写一份 SOFTWARE\Amazon Corretto
        // - Oracle 新版 JDK 装到 SOFTWARE\JavaSoft\JDK 已经在上面覆盖了，这里再补一个
        //   老版本可能用到的 SOFTWARE\JavaSoft\Java Development Kit
        var extraRegistryRoots = new[]
        {
            @"SOFTWARE\Azul Systems\Zulu",
            @"SOFTWARE\Amazon Corretto",
            @"SOFTWARE\JavaSoft\Java Development Kit",
        };
        foreach (var regRoot in extraRegistryRoots)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(regRoot);
                if (root == null) continue;
                foreach (var subName in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(subName);
                    // 不同发行版这个字段名不完全一致，两个都试一下，取到第一个非空的。
                    var home = sub?.GetValue("JavaHome") as string ?? sub?.GetValue("InstallDir") as string;
                    if (home == null) continue;
                    var p = Path.Combine(home, "bin", "javaw.exe");
                    if (File.Exists(p)) orderedCandidates.Add(p);
                }
            }
            catch { /* 忽略访问异常，某个发行版没装/没有这个注册表键是正常情况 */ }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var p = Path.Combine(dir.Trim(), "javaw.exe");
            if (File.Exists(p)) orderedCandidates.Add(p);
        }

        orderedCandidates = orderedCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var results = new List<JavaCandidate>();
        foreach (var path in orderedCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var version = await TryGetJavaVersionAsync(path, ct);
            results.Add(new JavaCandidate(path, version));
        }
        return results;
    }

    /// <summary>
    /// 全盘扫描找 Java（全新的可选功能）：
    /// - 默认不会调用这个方法。只有用户在设置里手动打开"全盘扫描"开关、并在弹窗里明确点了"同意"，
    ///   UI 层才会调这个方法；FindJava() 本身完全不受影响，永远只看默认路径
    ///   (配置里手动指定的路径 / JAVA_HOME / 注册表 / PATH / 便携版目录)。
    /// - 扫描范围：本机所有"固定盘"(排除可移动盘/光驱/网络盘，避免翻一整晚)的常见安装位置和整个盘根，
    ///   查找 javaw.exe，并尝试用 "javaw -version" 反查每个候选的版本号，方便用户直接选择匹配版本。
    /// - 这是一个耗时操作(可能几分钟)，调用方应该在后台线程跑，并展示进度/可取消。
    /// </summary>
    public async Task<List<JavaCandidate>> ScanWholeDiskForJavaAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var results = new List<JavaCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();

        foreach (var root in drives)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在扫描 {root} ...");

            IEnumerable<string> found;
            try
            {
                // EnumerateFiles 用 AllDirectories 会遍历整个盘符；用 try/catch 逐个跳过没权限访问的目录
                // (系统保护目录、其他用户的 AppData 等)，避免因为一个目录报错就中断整个扫描。
                found = SafeEnumerateFiles(root, "javaw.exe", ct);
            }
            catch
            {
                continue;
            }

            foreach (var path in found)
            {
                ct.ThrowIfCancellationRequested();
                if (!seenPaths.Add(path)) continue;

                var version = await TryGetJavaVersionAsync(path, ct);
                results.Add(new JavaCandidate(path, version));
                progress?.Report($"找到: {path}" + (version != null ? $" (Java {version})" : ""));
            }
        }

        return results;
    }

    /// <summary>安全地递归枚举文件：逐目录下钻，遇到无权限/被占用等异常的子目录直接跳过而不是整体失败。</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir, pattern); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subDirs)
            {
                // 跳过明显不会有 Java 的系统噪音目录，减少扫描时间。
                var name = Path.GetFileName(sub);
                if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;
                pending.Push(sub);
            }
        }
    }

    /// <summary>调用 "javaw -version" 反查候选路径的 Java 版本号，用于展示给用户参考，失败时返回 null（不影响它仍被列为候选）。</summary>
    private static async Task<string?> TryGetJavaVersionAsync(string javawPath, CancellationToken ct)
    {
        try
        {
            var javaExe = Path.Combine(Path.GetDirectoryName(javawPath) ?? "", "java.exe");
            if (!File.Exists(javaExe)) return null;

            var psi = new System.Diagnostics.ProcessStartInfo(javaExe, "-version")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var idx = output.IndexOf('"');
            var lastIdx = output.LastIndexOf('"');
            return idx >= 0 && lastIdx > idx ? output.Substring(idx + 1, lastIdx - idx - 1) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 傻瓜模式：不需要用户做任何选择，自动选一个当前系统架构下的推荐 LTS 版本（21），便携安装。
    /// </summary>
    public Task<string> DownloadRecommendedJavaAsync(IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        return DownloadJavaAsync(new JavaDownloadRequest(21, arch, JavaInstallMode.Portable), progress, ct);
    }

    /// <summary>
    /// 高级模式：按用户指定的版本号、架构、安装方式下载安装 Java，返回可执行的 javaw.exe 路径。
    /// </summary>
    public async Task<string> DownloadJavaAsync(JavaDownloadRequest request, IProgress<ProgressInfo>? progress,
        CancellationToken ct = default)
    {
        if (request.MajorVersion is < 8 or > 26)
            throw new ArgumentOutOfRangeException(nameof(request), "Java 版本号需在 8~26 之间。");

        var arch = request.Arch == "x86" ? "x86" : "x64";
        Directory.CreateDirectory(RuntimeDir);
        progress?.Report(new ProgressInfo("获取 Java 列表", 0, 1, $"查询 Java {request.MajorVersion} ({arch})"));

        (string downloadUrl, string fileName) source;
        try
        {
            source = await ResolveAdoptiumDownloadAsync(request.MajorVersion, arch, ct);
        }
        catch (Exception primaryEx)
        {
            try
            {
                source = await ResolveBmclapiDownloadAsync(request.MajorVersion, arch, ct);
            }
            catch (Exception fallbackEx)
            {
                var hint = KnownGoodMajorVersions.Contains(request.MajorVersion)
                    ? ""
                    : $"\n提示：Java {request.MajorVersion} 可能没有正式发布的稳定构建，建议改选 {string.Join("/", KnownGoodMajorVersions)} 中的版本。";
                throw new InvalidOperationException(
                    $"未能获取到 Java {request.MajorVersion} ({arch}) 的下载地址。\n" +
                    $"官方源(Adoptium)错误: {primaryEx.Message}\nBMCLAPI 镜像错误: {fallbackEx.Message}{hint}\n" +
                    "你也可以手动下载安装 Java 后，在设置里指定 javaw.exe 路径。");
            }
        }

        if (request.InstallMode == JavaInstallMode.System)
            return await DownloadAndRunSystemInstallerAsync(source, progress, ct);

        return await DownloadAndExtractPortableAsync(source, progress, ct);
    }

    private async Task<string> DownloadAndExtractPortableAsync((string downloadUrl, string fileName) source,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var zipPath = Path.Combine(RuntimeDir, source.fileName);
        await DownloadToFileAsync(source.downloadUrl, zipPath, source.fileName, progress, ct);

        var extractDir = Path.Combine(RuntimeDir, Path.GetFileNameWithoutExtension(source.fileName));
        progress?.Report(new ProgressInfo("解压 Java 运行时", 0, 1, "解压中..."));
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        File.Delete(zipPath);

        // 有些压缩包解压后会多一层目录，向下查找 javaw.exe
        var found = Directory.GetFiles(extractDir, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (found == null)
            throw new InvalidOperationException("Java 解压后未找到 javaw.exe，请手动配置 Java 路径。");

        progress?.Report(new ProgressInfo("Java 安装完成", 1, 1, found));
        return found;
    }

    /// <summary>
    /// 系统安装模式：下载 Adoptium 提供的 .msi 安装包并静默安装到系统目录（Program Files）。
    /// 注意：需要用户在弹出的 UAC 提示中确认管理员权限；MSI 安装完成后从注册表探测安装路径。
    /// </summary>
    private async Task<string> DownloadAndRunSystemInstallerAsync((string downloadUrl, string fileName) source,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // 系统安装模式需要 .msi 包，如果解析到的是 zip（便携包），改为向 Adoptium 请求 msi 格式
        var msiUrl = source.downloadUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
            ? source.downloadUrl
            : source.downloadUrl.Replace(".zip", ".msi");

        var msiPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(msiUrl));
        await DownloadToFileAsync(msiUrl, msiPath, Path.GetFileName(msiUrl), progress, ct);

        progress?.Report(new ProgressInfo("安装 Java 到系统目录", 0, 1, "正在运行安装程序（可能需要管理员确认）..."));

        var psi = new System.Diagnostics.ProcessStartInfo("msiexec.exe")
        {
            Arguments = $"/i \"{msiPath}\" /qb ADDLOCAL=FeatureMain,FeatureEnvironment,FeatureJarFileRunWith,FeatureJavaHome",
            UseShellExecute = true,
            Verb = "runas" // 触发 UAC 提权
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("安装已取消（未授予管理员权限），可改用便携版(zip)安装方式，无需管理员权限。");
        }
        finally
        {
            try { File.Delete(msiPath); } catch { /* 忽略清理失败 */ }
        }

        // 安装完成后尝试从注册表探测新安装的 Java
        var found = FindJava(null);
        if (found == null)
            throw new InvalidOperationException("系统安装似乎已完成，但未能自动探测到 javaw.exe，请在设置中手动指定路径（通常在 C:\\Program Files\\Eclipse Adoptium\\ 下）。");

        progress?.Report(new ProgressInfo("Java 安装完成", 1, 1, found));
        return found;
    }

    private async Task DownloadToFileAsync(string url, string destPath, string displayName,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        progress?.Report(new ProgressInfo("下载 Java 运行时", 0, 1, displayName));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载 Java 失败: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} ({url})");

        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var fs = File.Create(destPath);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
                progress?.Report(new ProgressInfo("下载 Java 运行时", (int)(read * 100 / total), 100, displayName));
        }
    }

    /// <summary>
    /// Adoptium(Eclipse Temurin) 官方 API：
    /// GET https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot?os=windows&image_type=jre&architecture={x64|x86}&vendor=eclipse
    /// 返回数组，取第一个的 binary.package.link 作为下载地址（zip 格式）。
    /// </summary>
    private async Task<(string downloadUrl, string fileName)> ResolveAdoptiumDownloadAsync(int majorVersion, string arch, CancellationToken ct)
    {
        var url = $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot" +
                   $"?os=windows&image_type=jre&architecture={arch}&vendor=eclipse";
        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Adoptium 未返回可用的 JDK {majorVersion} ({arch}) 版本信息");

        var package = first.GetProperty("binary").GetProperty("package");
        var link = package.GetProperty("link").GetString();
        var name = package.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(link))
            throw new InvalidOperationException("Adoptium 响应中缺少下载链接");

        return (link, name ?? $"jdk-{majorVersion}-{arch}-adoptium.zip");
    }

    /// <summary>BMCLAPI 镜像回退方案（仅覆盖常见 LTS 版本，且仅支持 x64）。</summary>
    private async Task<(string downloadUrl, string fileName)> ResolveBmclapiDownloadAsync(int majorVersion, string arch, CancellationToken ct)
    {
        if (arch != "x64")
            throw new InvalidOperationException("BMCLAPI 镜像仅支持 x64 架构");

        var listUrl = "https://bmclapi2.bangbang93.com/java/list?os=windows&arch=x64";
        var listJson = await _http.GetStringAsync(listUrl, ct);
        using var doc = JsonDocument.Parse(listJson);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var v = item.TryGetProperty("version", out var vv) ? vv.GetString() ?? "" : "";
            if (v.StartsWith(majorVersion.ToString()))
            {
                var component = item.TryGetProperty("component", out var c) ? c.GetString() : "jre";
                var downloadUrl = $"https://bmclapi2.bangbang93.com/java/download/windows/x64/{component}/{v}";
                return (downloadUrl, $"jdk-{v}-bmclapi.zip");
            }
        }
        throw new InvalidOperationException($"BMCLAPI 未返回 Java {majorVersion} 的可用版本");
    }
}
