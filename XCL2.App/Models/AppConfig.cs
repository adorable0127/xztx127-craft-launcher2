namespace XCL2.App.Models;

public enum DownloadSource
{
    Official,   // Mojang 官方源
    BMCLAPI     // BMCLAPI 镜像源（国内加速）
}

/// <summary>
/// xcl2/config.json 的内容：全局配置。
/// </summary>
public class AppConfig
{
    public List<GameFolder> Folders { get; set; } = new();
    public string? SelectedFolderPath { get; set; }
    public string? SelectedVersionId { get; set; }

    /// <summary>
    /// 默认使用官方源 (Mojang)：数据权威、不依赖第三方镜像的可用性。
    /// BMCLAPI 镜像源仍保留，用户可在下载中心的来源下拉框里手动切换（国内网络访问官方源较慢时更适用）。
    /// </summary>
    public DownloadSource Source { get; set; } = DownloadSource.Official;

    public string? JavaPath { get; set; }
    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    public bool FullScreen { get; set; } = false;

    /// <summary>
    /// 游戏内语言（Minecraft 的 options.txt lang 字段格式，如 "zh_cn"、"en_us"）。
    /// 注意：这是"游戏内显示语言"，跟启动器界面本身用什么语言是两回事——
    /// 启动器 UI 目前固定中文，这里只影响 Minecraft 客户端本体的语言。
    /// 默认简体中文，符合大多数用户预期。
    /// </summary>
    public string GameLanguage { get; set; } = "zh_cn";

    /// <summary>是否已经完成过首次启动向导（新手引导）。默认 false；向导跑完/用户主动跳过后设为 true，
    /// 之后启动器不会再自动弹出，但用户仍可在设置页手动"重新打开新手引导"。</summary>
    public bool FirstRunWizardCompleted { get; set; } = false;

    public string LastSelectedAccountId { get; set; } = "";

    /// <summary>false=傻瓜模式（默认，隐藏高级选项，一键完成）；true=高级模式（可自定义 Java 版本/架构/安装方式等）</summary>
    public bool AdvancedMode { get; set; } = false;

    /// <summary>上次选择的 Java 主版本号（8~25），仅高级模式下由用户修改，傻瓜模式固定使用推荐版本</summary>
    public int PreferredJavaMajorVersion { get; set; } = 21;

    /// <summary>Java 架构：x64 或 x86</summary>
    public string PreferredJavaArch { get; set; } = "x64";

    /// <summary>Java 安装方式：Portable(zip 便携版，安装到 xcl2/runtime) 或 System(安装到系统 Program Files 目录)</summary>
    public string PreferredJavaInstallMode { get; set; } = "Portable";

    /// <summary>
    /// 是否强制使用与当前版本匹配的 Java。默认 false（只建议，不强制）：启动前检测到用户
    /// 指定的 Java 跟版本实际需要的主版本号不一致时，弹窗提示，用户仍可以选择"仍然使用"
    /// 当前这个不匹配的 Java 继续启动。
    /// 打开后（true）：同样的场景下不再提供"仍然使用"的选项，弹窗只用来告知会自动切换到
    /// 匹配的 Java（列表里已登记的匹配项，或者没有则自动下载一个），点确定后直接切换，
    /// 不给继续用错误版本启动的机会。
    /// 见 MainWindow.xaml.cs 的 LaunchInternalAsync，Java 版本匹配检查那一段。
    /// </summary>
    public bool EnforceJavaVersionMatch { get; set; } = false;

    /// <summary>
    /// 用户自定义的 JVM 启动参数（仅高手模式下显示/生效），例如 "-XX:+UseG1GC -Dsomething=xxx"。
    /// 全局一份，不分版本；拼接顺序在 <see cref="LauncherService"/> 里位于官方 arguments.jvm
    /// 解析出的参数之后、"-cp" 之前，遵循"后面覆盖前面"的 JVM 惯例，让用户自定义参数优先生效。
    /// </summary>
    public string? CustomJvmArgs { get; set; }

    /// <summary>
    /// 启动前执行的命令行（高手模式/一键启动向导「高级选项」里可选填），例如启动前先跑一个
    /// 脚本同步配置/备份存档。全局一份，不分版本；执行失败不会阻止游戏启动，
    /// 具体执行逻辑见 <see cref="LauncherService.Launch"/>。
    /// </summary>
    public string? PreLaunchCommand { get; set; }

    /// <summary>是否显示日志面板（游戏控制台输出 / 启动器日志）。默认关闭，小白用户看不到也不受打扰。</summary>
    public bool ShowLogPanel { get; set; } = false;

    /// <summary>是否启用注入检测（游戏进程模块扫描 + 已知外挂特征码匹配）。默认开启，属于安全保护功能。</summary>
    public bool EnableInjectionScan { get; set; } = true;

    /// <summary>是否在启动游戏时额外弹出一个独立的 CMD 窗口，实时显示游戏控制台输出。
    /// 默认关闭；开启后高手可以直接在命令行里看日志，不需要打开日志面板。</summary>
    public bool EnableGameConsoleWindow { get; set; } = false;

    /// <summary>是否在下载中心的 Mod 搜索结果列表里显示模组图标（从 Modrinth/CurseForge 抓取的
    /// icon_url/logo）。默认开启；网络较差、或者不喜欢列表里混着图片的用户可以在设置页关闭，
    /// 关闭后只是不再请求/渲染这些图标图片，不影响搜索和下载功能本身。</summary>
    public bool ShowModIcons { get; set; } = true;

    /// <summary>是否在服务器启动成功后自动弹出"如何开放外网访问"教程窗口。默认开启，
    /// 帮助不熟悉内网穿透/端口映射的用户第一次开服后就知道下一步该做什么；用户在教程窗口里
    /// 勾选"不再提示"后关闭。</summary>
    public bool ShowServerNetworkGuideOnStart { get; set; } = true;

    /// <summary>
    /// 全局默认的"版本隔离"开关：官方启动器/HMCL/PCL 等主流第三方启动器默认都是版本隔离的——
    /// 每个版本的 mods、resourcepacks、saves、config、shaderpacks 等都各自独立存放在
    /// .minecraft/versions/&lt;版本号&gt;/ 目录下，而不是全部版本共用根目录下同一份 mods 文件夹。
    /// 这里作为全局默认值，默认开启(true)，符合大多数用户的预期；单个版本可以在「版本选择」
    /// 页里单独覆盖这个全局默认设置（见 GameVersion.IsolatedOverride）。
    /// </summary>
    public bool IsolateVersionsByDefault { get; set; } = true;

    /// <summary>
    /// 单个版本对"版本隔离"全局默认设置的覆盖，key 是版本 ID (对应 versions/&lt;id&gt; 文件夹名)，
    /// value 是这个版本是否启用隔离。字典里没有这个版本的 key，就跟随
    /// <see cref="IsolateVersionsByDefault"/> 这个全局默认值。
    /// </summary>
    public Dictionary<string, bool> VersionIsolationOverrides { get; set; } = new();

    /// <summary>
    /// 全局默认的"资源包/材质包/光影包下载作用域"：true 表示下载的资源包只装进当前选中版本的
    /// resourcepacks/shaderpacks 目录（跟随版本隔离的目录布局）；false 表示装进
    /// .minecraft 根目录下的 resourcepacks/shaderpacks，所有版本共用同一份——很多材质包/光影包
    /// 是跨版本通用的（尤其材质包，只要资源命名空间没变就能用），每个版本各下一份纯属浪费空间
    /// 和下载流量，用户可以按需选择"每个版本独立"还是"全局共用一份"。
    /// 默认 true（版本隔离）：不同版本各自单独下载一份材质包/数据包/光影包，互不共用，
    /// 避免跨版本共用同一份资源目录导致的兼容性问题（材质包命名空间在版本间也可能变化）。
    /// 用户仍可以在「设置」页把这个默认值改回 false（全局共用），或者对单个版本单独覆盖。
    /// </summary>
    public bool IsolateResourcePacksByDefault { get; set; } = true;

    /// <summary>
    /// 单个版本对"资源包下载作用域"全局默认设置的覆盖，key 是版本 ID，value 是这个版本是否
    /// 把资源包单独存放在自己的版本目录下。字典里没有这个版本的 key，就跟随
    /// <see cref="IsolateResourcePacksByDefault"/> 这个全局默认值。
    /// </summary>
    public Dictionary<string, bool> VersionResourcePackIsolationOverrides { get; set; } = new();

    /// <summary>
    /// 单个版本对"应该使用的 Java 主版本号"的覆盖，key 是版本 ID (对应 versions/&lt;id&gt; 文件夹名)，
    /// value 是这个版本要使用的 Java 主版本号。
    /// 优先级最高：即使自动探测(version json + mods 里的 fabric.mod.json "java" 依赖声明)算出
    /// 了另一个版本号，只要这里给某个版本单独指定了，就以这里为准——这是为了应对"自动探测覆盖不到
    /// 的极端情况"(比如某个 mod 没有按标准字段声明 Java 要求，导致自动探测漏判)，用户可以针对
    /// 单个版本手动兜底指定，而不需要牵动全局的 PreferredJavaMajorVersion。
    /// 字典里没有这个版本的 key，就走自动探测(见 LauncherService.GetRequiredJavaMajorVersion)。
    /// </summary>
    public Dictionary<string, int> VersionJavaOverrides { get; set; } = new();

    /// <summary>
    /// "Java 列表"：用户登记(手动浏览 / 下载安装 / 全盘扫描添加)的所有 Java 运行时集合，
    /// 每条记录见 <see cref="InstalledJava"/>。这是"多 Java 共存"功能的核心数据——
    /// 客户端的每个版本(<see cref="VersionJavaIdOverrides"/>)和每个服务器实例
    /// (ServerInstance.JavaId)都可以从这个列表里单独选择要用哪一个 Java，
    /// 多个 Java 版本可以同时登记在案、按需切换，互不影响。
    /// </summary>
    public List<InstalledJava> InstalledJavas { get; set; } = new();

    /// <summary>
    /// 「设置」页里选的全局默认 Java（从 <see cref="InstalledJavas"/> 里选一条，存它的 Id）。
    /// 没有单独为某个版本/服务器指定 Java 时，最终会回退到这里指定的这一条；
    /// 如果这里也没选(null)，则继续走原来的自动探测/JavaPath/PreferredJavaMajorVersion 逻辑，
    /// 保证老配置文件（还没有 Java 列表数据时）能无缝兼容，不会因为升级启动器而无法启动游戏。
    /// </summary>
    public string? SelectedJavaId { get; set; }

    /// <summary>
    /// 单个客户端版本对"要使用哪一个已登记 Java"的选择，key 是版本 ID，value 是
    /// <see cref="InstalledJava.Id"/>。优先级高于 <see cref="VersionJavaOverrides"/>（只指定
    /// 主版本号、仍需自动搜索）——这里是明确选中 Java 列表里的哪一条，直接拿到路径，不需要
    /// 再去搜索/匹配。字典里没有这个版本的 key，则回退到 VersionJavaOverrides / 自动探测 /
    /// 全局 SelectedJavaId 的旧逻辑链路。
    /// </summary>
    public Dictionary<string, string> VersionJavaIdOverrides { get; set; } = new();

    /// <summary>
    /// 收藏的游戏版本 ID 列表（来自下载中心的"☆ 收藏"按钮）。
    /// 这里先只做版本收藏；Mod/资源包/光影/地图等社区资源的收藏，等接入 Modrinth/CurseForge
    /// 之后再扩展成统一的收藏项结构（当前任务范围只做游戏版本下载）。
    /// </summary>
    public List<string> FavoriteVersionIds { get; set; } = new();

    /// <summary>
    /// 是否启用多线程下载（同时并发下载多个库文件/资源文件，而不是逐个串行下载）。
    /// 默认开启：官方/BMCLAPI 源在下载 libraries、assets 这类"成百上千个小文件"的场景下，
    /// 串行下载每个文件都要单独走一次 TCP 握手+请求延迟，并发下载能显著缩短总耗时。
    /// 关闭后完全退回逐个文件顺序下载（等价于 <see cref="MaxDownloadThreads"/>=1），
    /// 给网络环境较差、或者不希望下载占用过多并发连接的用户一个退路。
    /// </summary>
    public bool EnableMultiThreadDownload { get; set; } = true;

    /// <summary>
    /// 多线程下载时的最大并发数（同时进行的文件下载数）。默认 8，参考主流启动器
    /// (HMCL/PCL) 的默认并发档位，在下载速度和"占满对方 CDN/本地网卡"之间取一个折中值。
    /// 高手模式下用户可以在设置页调整（建议范围 1~32）；<see cref="EnableMultiThreadDownload"/>
    /// 为 false 时这个值不生效（视为 1）。
    /// </summary>
    public int MaxDownloadThreads { get; set; } = 8;

    /// <summary>
    /// 全局下载速度上限，单位 KB/s。0 表示不限速（默认）。
    /// 限速对"多线程下载的所有并发连接加总"生效，而不是"每个连接单独限速到这个值"——
    /// 否则用户设置的上限会被并发数放大好几倍，跟界面上写的数字对不上。
    /// 实现见 <see cref="Services.DownloadRateLimiter"/>（令牌桶）。
    /// </summary>
    public int DownloadSpeedLimitKBps { get; set; } = 0;

    /// <summary>
    /// 智能限速：不设固定速度上限，而是持续采样系统当前的网络占用情况，当检测到"除本程序外
    /// 的其他网络活动明显增多"（比如用户正在看视频/开着语音/其他下载工具在跑）时，自动调低
    /// 本程序的下载速度，避免抢占其他程序的带宽；其他网络活动变少时再自动恢复全速。
    /// 默认关闭（多数用户下载游戏文件时并不会同时有其他大流量活动，固定不限速更简单直接）；
    /// 与 <see cref="DownloadSpeedLimitKBps"/> 手动限速可以同时开启——两者是"下限"和"动态调节"
    /// 的关系，智能限速计算出的目标速度不会超过手动设置的固定上限（0 表示手动上限不生效）。
    /// 实现见 <see cref="Services.SmartBandwidthMonitor"/>。
    /// </summary>
    public bool SmartBandwidthThrottle { get; set; } = false;

    /// <summary>
    /// 「访客模式」：开启后，主页的账户始终是一个只存在于本次运行的临时离线账户（不写入
    /// accounts.json，不出现在账户管理页的持久列表里），且关闭启动器时会清理本次会话新产生的
    /// 游戏日志/临时下载文件。适合在别人电脑上临时借用启动器、不想留下任何个人痕迹的场景。
    /// 默认关闭。见 <see cref="Services.GuestModeService"/>。
    /// </summary>
    public bool GuestModeEnabled { get; set; } = false;

    /// <summary>
    /// 万能皮肤补丁(authlib-injector) 使用的皮肤服务 API Root。默认使用内置的公共服务
    /// (<see cref="Services.SkinService.DefaultSkinApiRoot"/>)；有自己皮肤站的用户可以在设置里替换。
    /// </summary>
    public string SkinApiRoot { get; set; } = Services.SkinService.DefaultSkinApiRoot;

    /// <summary>
    /// 是否启用界面切换动画（左侧导航栏切页时右侧内容区的淡入过渡）。默认开启，
    /// 让页面切换不那么生硬。介意动画影响响应速度、或者觉得动画多余的用户可以在
    /// 设置页关闭，关闭后页面切换恢复成瞬间直接替换，没有任何过渡效果。
    /// 见 MainWindow.SetMainContent。
    /// </summary>
    public bool EnablePageAnimations { get; set; } = true;
}