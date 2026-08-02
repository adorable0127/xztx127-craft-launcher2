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
    /// 启动器 UI 语言见下面的 LauncherLanguage，这里只影响 Minecraft 客户端本体的语言。
    /// 默认简体中文，符合大多数用户预期。
    /// </summary>
    public string GameLanguage { get; set; } = "zh_cn";

    /// <summary>
    /// 启动器界面（不是游戏内）使用的语言，取值是 LocalizationService.SupportedLanguages
    /// 里的 Code（如 "zh-Hans"、"en-US"），跟上面 GameLanguage 的格式（options.txt 风格）
    /// 完全不同、互不影响，不要混用。默认简体中文，即当前启动器最初唯一支持的语言，
    /// 保证升级到这个版本的老用户不会因为新增多语言功能而"莫名其妙变成别的语言"。
    /// 见 Resources/Lang/README.md 了解整套多语言资源的组织方式。
    /// </summary>
    public string LauncherLanguage { get; set; } = "zh-Hans";

    /// <summary>
    /// 游戏内左下角"版本类型"水印文字（对应启动参数 --versionType，Minecraft 客户端本身
    /// 就会把这个值渲染在主菜单/游戏内左下角，例如原版官方启动器传的是 "release"，
    /// PCL2/HMCL 等第三方启动器习惯借用这个位置显示启动器品牌）。
    /// 默认 "XCL2"，用户可以在设置页改成别的文字，或清空后退回官方原始的 "release"
    /// （LauncherService.BuildArguments 里 string.IsNullOrWhiteSpace 时会这样兜底）。
    /// 这是"游戏内"的品牌展示，跟上面 LauncherLanguage（启动器界面语言）是两个完全独立的
    /// 概念，不要混淆——一个决定游戏窗口里显示什么，一个决定启动器窗口本身显示什么语言。
    /// </summary>
    public string GameVersionTypeLabel { get; set; } = "XCL2";

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
    /// "开启后进入某某某服务器"：按游戏实例(版本 id)记录的自动进服务器地址。
    /// key 是 GameVersion.Id，value 是服务器地址（形如 "play.example.com" 或 "1.2.3.4:25565"）。
    /// 某个版本不在字典里，或者值为空/空白，都表示"这个实例不自动进服务器"，游戏正常进主菜单，
    /// 跟这个功能上线前的行为完全一致——这是一个纯增量的可选开关，不影响任何已有实例的启动行为。
    /// 见 LauncherService.LaunchOptions.AutoJoinServerAddress 的具体拼参数逻辑。
    /// </summary>
    public Dictionary<string, string> VersionAutoJoinServer { get; set; } = new();

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
    /// 保留这个字段只是为了兼容老版本配置文件（升级前已经收藏过版本的用户，配置文件里
    /// 只有这个字段，没有下面的 FavoriteItems）——ConfigService 加载时会把这里的内容
    /// 一次性搬进 FavoriteItems（Type=Version），此后新增/取消收藏统一走 FavoriteItems，
    /// 这个列表不再被写入，只在加载老配置时读一次。新代码不要再往这里加东西。
    /// </summary>
    public List<string> FavoriteVersionIds { get; set; } = new();

    /// <summary>
    /// 统一的"收藏夹"内容：游戏版本 + Modrinth/CurseForge 的 Mod/材质包/数据包/光影包/地图，
    /// 现在收藏夹不再局限于"游戏版本"这一种类型，下载中心每个分类的卡片上都能收藏，
    /// 全部汇总展示在"我的收藏"里，按类型分组。
    /// 去重规则：同一个 (Type, SourceId, Source) 三元组只保留一条，重复收藏视为取消收藏
    /// （具体判重逻辑见 FavoriteItem.MatchesKey，跟 DownloadCenterPage 里各个 XxxFavorite_Click
    /// 处理函数配套使用）。
    /// </summary>
    public List<FavoriteItem> FavoriteItems { get; set; } = new();

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
    /// 界面配色"色系"：White/Blue/Yellow/Purple/Pink（Dark 作为独立色系保留兼容旧配置，
    /// 见下面 IsDarkMode 的注释）。用户在设置里手动选择，独立于访客模式；访客模式开启期间
    /// 会临时覆盖显示为纯黑深色，关闭访客模式后恢复回这里保存的值。见 <see cref="Services.ThemeService"/>。
    ///
    /// 从只有"白/蓝/黄/黑"四个互斥选项，改成"色系 + 明暗"两个独立维度的原因：用户要的是
    /// "蓝色系也能有深色版"，而不是把黑色单独当成第五个跟颜色无关的选项。现在色系只决定
    /// 色相（蓝/黄/紫/粉这几个色相），具体显示成浅色版还是深色版由 <see cref="IsDarkMode"/>
    /// 独立控制，两者组合、不互相覆盖。
    /// </summary>
    public string UiSkin { get; set; } = "White";

    /// <summary>
    /// 界面明暗模式：false=浅色（默认），true=深色。跟 <see cref="UiSkin"/> 选的色系是完全独立的
    /// 两个维度——比如 UiSkin=Blue 时，IsDarkMode=false 显示"蓝色系-浅"，true 则显示
    /// "蓝色系-深"，色相不变，只是背景/文字对比度切换成夜间友好的深色版本。
    /// 由首页/主界面的"模式设置"按钮直接控制，也会被 <see cref="AutoThemeCycleEnabled"/>
    /// 自动循环按计划覆盖。见 <see cref="Services.ThemeService"/>。
    /// </summary>
    public bool IsDarkMode { get; set; } = false;

    /// <summary>
    /// 是否开启"自动循环"：开启后由系统当前时间自动决定 <see cref="IsDarkMode"/>，
    /// 不需要用户手动点"模式设置"按钮。具体的切换时间点见
    /// <see cref="AutoThemeLightStartHour"/>/<see cref="AutoThemeDarkStartHour"/>。
    /// 默认关闭：不影响老用户已经习惯的手动模式，只有主动开启才会接管明暗切换。
    ///
    /// 与用户手动点击"模式设置"按钮的关系：手动优先——用户随时可以点按钮临时切换/覆盖当前
    /// 显示的明暗，但到下一个自动切换时间点，还是会被自动循环按计划重新覆盖回去（除非用户
    /// 关闭这个开关）。也就是说自动循环不会"锁死"按钮不让点，只是会在下一次时间点到达时
    /// 重新接管一次。见 MainWindow 里的每分钟定时检查逻辑。
    /// </summary>
    public bool AutoThemeCycleEnabled { get; set; } = false;

    /// <summary>自动循环下，浅色模式的开始时间（小时，0~23）。默认 8，即早上 8:00 开始浅色模式。
    /// 用户可在设置页自行调整。</summary>
    public int AutoThemeLightStartHour { get; set; } = 8;

    /// <summary>自动循环下，深色模式的开始时间（小时，0~23）。默认 19，即下午 19:00 开始深色模式，
    /// 直到次日 <see cref="AutoThemeLightStartHour"/> 之前都保持深色。用户可在设置页自行调整。</summary>
    public int AutoThemeDarkStartHour { get; set; } = 19;

    /// <summary>
    /// 记录自动循环上一次自动写入 IsDarkMode 的"目标时间段"（用浅/深色区间的起始小时当唯一标识，
    /// 比如浅色区间的标识就是 AutoThemeLightStartHour 本身），用来判断"现在是不是需要重新自动
    /// 切换一次"，避免用户手动覆盖后，同一个时间段内每次定时检查都被自动循环立即纠正回去
    /// （那样手动覆盖就完全没意义了——见 IsDarkMode 注释里"手动优先"的约定：只有真正跨入
    /// 下一个新的时间段时，自动循环才重新接管一次）。null 表示还没有任何一次自动切换记录过
    /// （刚开启自动循环、或者旧配置文件升级上来），此时会立即按当前时间校正一次。
    /// </summary>
    public int? AutoThemeLastAppliedSlotStartHour { get; set; }

    /// <summary>
    /// "实验性功能"总开关：用户是否已经完整走过一次强制等待（10 秒倒计时不可跳过）的
    /// 确认流程。这个流程本身是一次性的"仪式"——第一次点开实验性功能入口时强制等待，
    /// 让用户有机会读完警告文案、真正意识到"这里的东西不稳定"，而不是手滑点进去。
    /// 一旦确认过一次，后续再打开实验性功能面板不需要重复等待 10 秒（不然用户每次只是
    /// 想改个换肤设置都要罚站 10 秒，体验会变得很烦人，也偏离了"警示新用户"这个本意）。
    /// 默认 false：全新安装/全新配置文件的用户第一次进入实验性功能都要走一遍强制等待。
    /// </summary>
    public bool ExperimentalFeaturesUnlocked { get; set; } = false;

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

    /// <summary>
    /// 用户手动指定的陶瓦联机(Terracotta)可执行文件路径。陶瓦联机本体是 burningtnt/Terracotta
    /// 发布的独立可执行程序(基于 EasyTier 的 P2P 联机工具)，不是本启动器能重新实现的协议——
    /// 真正的建房/加入房间/房间码交互，全部在陶瓦联机自己的界面里完成，见 TerracottaService 类注释。
    ///
    /// 启动器已经内置了一份陶瓦联机可执行文件(EmbeddedResource，见 TerracottaService.EnsureExtracted)，
    /// 默认情况下用户完全不需要碰这一项——首次点"启动陶瓦联机"会自动把内置版本释放到本地并直接运行。
    /// 这一项只作为"高级覆盖"保留：如果用户想手动换成自己下载的其他版本(比如以后陶瓦联机出了
    /// 新版本、内置版本还没来得及更新)，可以在联机页手动选择一个 exe 路径覆盖内置版本；
    /// 留空(默认)则始终使用内置版本。
    /// </summary>
    public string? TerracottaExecutablePath { get; set; }

    // ===== 百宝箱（工具箱）相关配置 =====

    /// <summary>累计"启动游戏"成功的次数（不含启动失败/提前退出的情况），用于「百宝箱」
    /// 的「查看启动计数」功能。每次 MainWindow 里真正弹出"启动成功"提示时自增 1，
    /// 是一个只增不减的历史累计值，不随删除版本/切换文件夹而重置。</summary>
    public long GameLaunchSuccessCount { get; set; } = 0;

    /// <summary>
    /// 内存优化功能总开关：开启后，启动游戏前会按 <see cref="Services.MemoryOptimizerService"/>
    /// 的推荐算法，结合当前系统可用内存 + 已选版本的加载器类型，自动把 MinMemoryMb/MaxMemoryMb
    /// 校正到一个更合理的区间（避免用户手动设置的 -Xmx 远超过系统实际可用内存，导致
    /// 启动巨卡/系统濒临爆内存）。默认关闭：尊重用户在设置页手动填写的内存数值，
    /// 只有主动打开这个开关才会介入自动调整。
    /// </summary>
    public bool EnableMemoryOptimization { get; set; } = false;

    /// <summary>内存优化时，给系统自身/其它程序预留的内存(MB)，不会被分配给 Java 堆。
    /// 默认 1536MB，兼顾"尽量把内存让给游戏"和"不能让系统本身卡死"两个目标。</summary>
    public int MemoryOptimizationReserveMb { get; set; } = 1536;

    // ===== 功能隐藏 =====

    /// <summary>
    /// 被隐藏的功能项集合，存的是 <see cref="Services.FeatureVisibilityService"/> 里定义的
    /// 固定 key（如 "Nav.Download"、"Settings.Java"、"Tool.Toolbox" 等），不是显示文案——
    /// 文案会跟着界面语言切换，key 不会，这样切语言不会导致隐藏设置全部失效。
    /// 命中这个集合的功能项，正常情况下不在界面上出现；按 F12 可以临时（不改这个配置，
    /// 只影响当前这一次显示）把它们都显示出来，方便用户自己手滑隐藏后还能找回来改设置。
    /// </summary>
    public List<string> HiddenFeatureKeys { get; set; } = new();
}