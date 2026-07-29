namespace XCL2.App.Models;

/// <summary>
/// 一个已创建的服务器实例的持久化配置。对应清单里"一键开服"的各项可选项：
/// 安装位置(Directory) / 加载器(CoreType) / Java 版本(JavaMajorVersion) /
/// 内存上限(MaxMemoryMb) / CPU 上限(CpuLimitPercent) / 磁盘上限(DiskLimitMb)。
///
/// 存放位置：xcl2/servers.json，是一个 ServerInstance 列表，风格上和 AppConfig.Folders
/// 保持一致(那是"客户端多.minecraft目录"列表，这个是"多服务器实例"列表)。
/// </summary>
public class ServerInstance
{
    /// <summary>唯一 ID，用于内部引用（进程管理表的 key、备份/日志文件命名等），与 DisplayName 分开，
    /// 避免用户改名导致历史日志/备份文件对不上。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "";

    /// <summary>服务端安装目录，里面直接是 server.jar / run.bat / world/ / plugins/ 等内容。</summary>
    public string Directory { get; set; } = "";

    public ServerCoreType CoreType { get; set; }
    public string McVersion { get; set; } = "";

    /// <summary>启动时实际执行的 jar 文件名（相对 Directory），或者 Forge/NeoForge 场景下的启动脚本名。
    /// 由创建向导在下载/安装完成后写入，避免每次启动都要重新猜测文件名。</summary>
    public string LaunchTarget { get; set; } = "server.jar";

    /// <summary>true = LaunchTarget 是启动脚本(run.bat/run.sh)，直接执行脚本本身；
    /// false = LaunchTarget 是 jar 文件，需要拼 "java -jar" 命令行。</summary>
    public bool LaunchTargetIsScript { get; set; }

    public string? JavaPath { get; set; }

    /// <summary>
    /// 从全局"Java 列表"(AppConfig.InstalledJavas)里明确选中的这个服务器要用的 Java，
    /// 存的是 InstalledJava.Id。这是"多 Java 共存"功能里服务器一侧的选择项：不同服务器
    /// 实例可以各自选择列表里不同的一条 Java，互不影响；留空(null)则回退到旧逻辑——
    /// 直接使用上面的 JavaPath 字段(创建/安装时探测/下载好写入的路径)。
    /// </summary>
    public string? JavaId { get; set; }

    /// <summary>
    /// 这个服务器核心运行所需的 Java 主版本号，创建/覆盖安装时由 ServerCoreDownloadResult
    /// .RequiredJavaMajorVersion 写入。用于启动前校验当前 JavaPath 是否仍然匹配核心要求，
    /// 不匹配时提示用户重新下载/选择正确版本的 Java，而不是直接尝试启动然后崩溃出
    /// UnsupportedClassVersionError。
    /// </summary>
    public int RequiredJavaMajorVersion { get; set; } = 21;

    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>
    /// CPU 使用率上限（0-100，null=不限制）。通过 Windows Job Object 的
    /// JOBOBJECT_CPU_RATE_CONTROL_INFORMATION 强制限制，是操作系统层面的硬限制，
    /// 不是"尽量不超过"的软限制。
    /// </summary>
    public int? CpuLimitPercent { get; set; }

    /// <summary>
    /// 磁盘占用上限（MB，null=不限制）。
    /// 重要说明：Windows 没有原生 API 能对"任意一个文件夹"做硬性磁盘配额限制
    /// （NTFS 磁盘配额是按用户账户、按整个卷计算的，不是按文件夹）。这个值目前只用于
    /// ServerProcessManager 启动前/运行中的监控预警(超过阈值在控制台提示/日志记录)，
    /// 不是真正意义上"写满即拒绝写入"的硬限制，避免用户误以为这是强制生效的。
    /// </summary>
    public int? DiskLimitMb { get; set; }

    /// <summary>额外的 JVM 参数（比如 Aikar's flags 之类的 GC 调优参数），高级用户可自定义。</summary>
    public string ExtraJvmArgs { get; set; } = "";

    /// <summary>
    /// 自定义图标的本地文件路径（复制进 xcl2/server-icons/ 目录后保存的绝对路径）。
    /// null/空 = 使用列表卡片的默认占位图标。
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// 是否为"默认服务器"。目前只做单选标记（同一时间最多一个实例为 true，由
    /// ServerInstanceService.SetDefault 维护互斥），用于后续"启动器启动时自动打开/高亮"
    /// 之类的场景；这里先只提供标记与展示，不接入自动启动等行为。
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// 局域网/公网连接端口，读自服务端目录下 server.properties 的 server-port 字段（默认 25565）。
    /// 每次刷新服务器列表/启动完成后由 ServerConnectionInfo.Resolve 重新解析并回填，
    /// 不由用户手动填写——server-port 才是权威来源，手填容易和 server.properties 实际值不一致。
    /// </summary>
    public int? ServerPort { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
