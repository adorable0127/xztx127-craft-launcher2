namespace XCL2.App.Models;

public enum DownloadSource
{
    Official,   // Mojang 官方源
    BMCLAPI     // BMCLAPI 镜像源（国内加速）
}

/// <summary>
/// 点击标题栏右上角关闭按钮（叉号）时的默认行为。
/// DirectClose：跟原来行为一致，直接关闭启动器进程。
/// MinimizeToTray：不真正退出，隐藏主窗口、缩到系统托盘，进程继续在后台运行
/// （下载/挂机中的服务器控制台等后台任务不会被打断），需要真正退出时从托盘右键菜单选「退出」。
/// Minimize：新增。只是普通的最小化到任务栏（跟点标题栏「最小化」按钮效果一样），
/// 不显示托盘图标——介于「直接关闭」和「最小化到托盘」之间的一个更轻量选项，适合
/// 不需要托盘图标、只是想暂时把窗口收起来的用户。
/// AskEachTime：新增。不预设固定行为，每次点叉号都弹出「关闭/最小化/返回任务栏托盘/取消」
/// 四选一弹窗，跟"下载/启动进行中"时强制弹出的那个提示复用同一个弹窗（见
/// MainWindow.CloseButton_Click），由用户自己当场选，不由这里的默认值决定。
/// </summary>
public enum CloseButtonAction
{
    DirectClose,
    MinimizeToTray,
    Minimize,
    AskEachTime
}

/// <summary>
/// 游戏启动后启动器主窗口应该做什么。默认 KeepAsIs（保持不变）——不是所有用户都希望
/// 启动器在游戏起来之后自动消失或缩小，尤其是还想留着看控制台日志/内存占用的用户，
/// 所以刻意选一个"什么都不做"当默认值，跟 <see cref="CloseButtonAction"/> 默认直接关闭
/// 的取舍逻辑不同（那个是兼容旧行为，这个是新功能，新功能默认不应该改变用户没预期到的行为）。
/// </summary>
public enum AutoStartLaunchBehavior
{
    /// <summary>开机自启动后正常显示主界面。</summary>
    ShowWindow,
    /// <summary>开机自启动后保留任务栏按钮，但窗口最小化。</summary>
    Minimize,
    /// <summary>开机自启动后隐藏主窗口，仅保留系统托盘入口。</summary>
    MinimizeToTray
}

public enum PostGameLaunchAction
{
    /// <summary>保持不变，默认值——启动器窗口状态不受游戏启动这件事影响。</summary>
    KeepAsIs,
    /// <summary>普通最小化到任务栏，不显示托盘图标。</summary>
    Minimize,
    /// <summary>隐藏主窗口、缩到系统托盘，跟 CloseButtonAction.MinimizeToTray 是同一套实现。</summary>
    MinimizeToTray,
    /// <summary>直接关闭启动器窗口/进程。游戏本身是独立进程，不会跟着一起被关掉。</summary>
    Close
}

/// <summary>启动/关闭自动实例备份的目标选择方式。</summary>
public enum LifecycleBackupTargetMode
{
    /// <summary>只备份当前选中的一个实例。</summary>
    Single,
    /// <summary>备份设置页里手动填写的多个实例 ID；不存在的条目跳过。</summary>
    Multiple,
    /// <summary>候选实例里只要存在任意一个，就备份所有实际存在的候选实例。</summary>
    Any,
    /// <summary>只有候选实例全部存在时才执行备份；缺任意一个则整次跳过。</summary>
    All
}

/// <summary>
/// 启动前完整性检查的严格程度。见 InstanceIntegrityService 上的完整说明。
/// </summary>
public enum IntegrityCheckMode
{
    /// <summary>只查关键文件（version json、client jar、加载器 jar/依赖库）+ assets 按"数量"抽查，不逐个核对哈希，速度快。</summary>
    Simple,
    /// <summary>在 Simple 的基础上，assets 里每一个 object 文件也逐个核对是否存在、大小是否匹配，更准但启动前会慢一些。</summary>
    Strict
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
    /// 启动前完整性检查的严格程度。null 表示用户还没选过——首次触发检查时会弹一个
    /// 10 秒倒计时的选择框，选完（或超时后自动按 Simple）就把结果写回这里，之后不再重复问，
    /// 用户也可以随时在设置页里改。见 IntegrityCheckModeChoiceDialog。
    /// </summary>
    public IntegrityCheckMode? IntegrityCheckMode { get; set; }

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

    /// <summary>是否已经逐页阅读并同意过首次启动展示的三份协议（用户协议 / 隐私协议 / 开源协议）。
    /// 默认 false：首次启动必须先看完三页协议并点「同意并继续」才能进入新手引导与主界面——
    /// 前两页（用户协议、隐私协议）按钮强制阅读 5 秒后才可以点击，第三页（开源协议）
    /// 不强制阅读、进入即可继续。按 Esc / 页面上没有提供其它关闭路径，任何"未走到第三页
    /// 就关闭"的情况一律视为未同意，不会写入 true，下次启动会重新展示——法律性文本
    /// 不允许"跳过即视为同意"。见 Views/AgreementsWindow 与 MainWindow 构造函数里的
    /// 首次启动流程。
    /// 新逻辑（协议版本号上线后）：这个布尔字段保留用于兼容旧配置与旧代码的读取；
    /// 真正的"是否需要在本次启动重新弹协议"判断改为比较 <see cref="AcceptedAgreementVersion"/>
    /// 与 <see cref="Views.AgreementsText.AgreementsVersion"/>。同意完整协议时两个字段
    /// 都会写入（AcceptedAgreementVersion=当前版本号、AgreementsAccepted=true）。</summary>
    public bool AgreementsAccepted { get; set; } = false;

    /// <summary>
    /// 用户已同意到的协议版本号（对应 <see cref="Views.AgreementsText.AgreementsVersion"/>）。
    /// 小于当前版本（旧配置没有这个字段时反序列化得到默认值 0，同样小于当前版本）就会在
    /// 下次启动时重新弹出协议同意页——这就是"每次协议更新都会重新弹出同意协议菜单"的
    /// 实现基础：协议文本有实质修改时，只需要把 AgreementsText.AgreementsVersion 往上加一，
    /// 老用户下次启动就会被要求重新逐页确认一遍，不需要任何额外的云端下发或远程标记
    /// （本项目没有服务器，这种"版本号比对"的纯本地机制正是唯一正确的实现方式）。
    /// 仅当用户完整走完三页协议并点「同意并继续」后，才会写入当前版本号。
    /// </summary>
    public int AcceptedAgreementVersion { get; set; } = 0;

    /// <summary>
    /// 是否处于"基本模式"（受限模式）。true 表示用户没有接受完整协议（点「不同意」后选择了
    /// 「使用基本模式」），或从设置页主动「注销应用（暂时不同意协议）」。基本模式下，
    /// 启动器只允许"启动游戏"和"选择/切换游戏文件夹"两项功能，其余功能入口保持可见但
    /// 不可用（置灰，不是隐藏——用户随时能看见还有哪些功能存在）；主界面右上角会出现
    /// 只有基本模式才有的「重新阅读协议并同意」按钮，点击后重新走完整协议流程，
    /// 同意即退出基本模式、恢复全部功能。见 <see cref="BasicAgreementAccepted"/>。
    /// </summary>
    public bool RestrictedMode { get; set; } = false;

    /// <summary>
    /// 是否同意过《基本模式协议》（约 1000 字，见 <see cref="Views.AgreementsText.BasicModeAgreementText"/>）。
    /// 仅在 <see cref="RestrictedMode"/> 为 true 时有意义：未同意（false）时，每次启动都会
    /// 先弹出基本模式协议；点「同意」写入 true、本次会话不再重复弹出；点「不同意」则保持
    /// false、继续以"只可启动游戏和选择文件夹"的受限状态进入主界面，不阻塞使用。
    /// 一旦用户通过「重新阅读协议并同意」接受了完整协议并退出基本模式，RestrictedMode
    /// 会变回 false，本字段一并复位为 true（完整协议覆盖基本协议，无需再单独确认）。
    /// </summary>
    public bool BasicAgreementAccepted { get; set; } = false;

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

    // ===================== 拖拽安装（Drag & Drop）默认行为 =====================

    /// <summary>
    /// 拖入 .zip 且无法从内容百分百确定类型时的默认处理方式。
    /// zip 是材质包/光影包/数据包/存档/整合包的共同容器，光看扩展名分不出来；
    /// DragDropInstallService 会先开包看结构，只有**看不出来**时才用这个默认值。
    /// "Ask" = 弹一个内嵌选择框问用户（默认，也最不容易装错地方）。
    /// </summary>
    public DropZipDefault ZipDropDefault { get; set; } = DropZipDefault.Ask;

    /// <summary>
    /// 在「服务端管理」页拖入 .jar 时的默认去向。
    /// 服务端页面拖 jar 绝大多数情况是想装服务端核心/服务端 mod，
    /// 而不是装到客户端实例的 mods 里，所以这里默认 Server。
    /// </summary>
    public DropJarTarget ServerPageJarDropTarget { get; set; } = DropJarTarget.Server;

    /// <summary>
    /// 在主页 / 版本选择 / 其它非服务端页面拖入 .jar 时的默认去向。
    /// 默认装进当前选中实例的 mods。
    /// </summary>
    public DropJarTarget DefaultJarDropTarget { get; set; } = DropJarTarget.CurrentInstanceMods;

    /// <summary>
    /// 拖入整合包时，是否默认"从零下载一个全新实例"（下载对应的原版 + 加载器再装整合包内容），
    /// 而不是把内容覆盖进某个已有实例。默认 true——整合包本来就是"一整套独立环境"，
    /// 覆盖装进已有实例几乎必然跟已装的 mod 打架。
    /// </summary>
    public bool ModpackDropCreatesNewInstance { get; set; } = true;

    /// <summary>
    /// 社区资源是否显示预览版（beta / alpha）。
    /// 对应「下载中心 - 社区资源」筛选栏里的"显示预览版资源"勾选框，
    /// 以及资源详情页版本列表的默认展开状态（详情页仍可临时切换，不写回这里）。
    /// 默认 false：新手装到 beta 版 mod 导致游戏崩溃是很常见的坑，
    /// 让用户主动打开比默认打开安全。
    /// </summary>
    public bool ShowPreviewVersions { get; set; } = false;

    /// <summary>
    /// 社区资源下载到本地后，文件名里中文名和原始文件名的组合样式（见 <see cref="ModFileNamingStyle"/>）。
    /// 默认 SquareBracket（"[中文名] 原始文件名"），跟旧版无中文名前缀的文件名相比多了一层可读性，
    /// 同时用方括号而不是中文全角括号，避免个别老旧压缩软件/命令行工具对全角字符处理不一致的问题。
    /// </summary>
    public ModFileNamingStyle ModFileNamingStyle { get; set; } = ModFileNamingStyle.SquareBracket;

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
    /// "此实例默认使用所选 Java，不再提示切换"：按版本 ID 记录的开关集合，出现在这个集合里的
    /// 版本，启动时即使检测到 <see cref="VersionJavaIdOverrides"/> 指定的 Java 跟自动匹配的
    /// 主版本号不一致，也不再弹「Java 版本可能不匹配」的确认/强制切换弹窗——直接照用户为这个
    /// 实例选定的 Java 启动。用来满足"这个实例我很清楚就是要用这个 Java（比如就是要跑某个只支持
    /// 老版本 Java 的老 mod/整合包），每次启动都被多问一遍很烦"的场景。
    /// 只影响"版本不匹配"这一类提示；完全找不到可用 Java 时仍然会正常提示下载，不受这个开关影响。
    /// </summary>
    public HashSet<string> VersionSkipJavaMismatchPrompt { get; set; } = new();

    /// <summary>点击标题栏关闭按钮（叉号）时的默认行为，见 CloseButtonAction 枚举注释。
    /// 默认 DirectClose，保持老用户升级后行为不变（不会突然"点叉号却关不掉"）。</summary>
    public CloseButtonAction DefaultCloseAction { get; set; } = CloseButtonAction.DirectClose;

    /// <summary>游戏窗口成功出现（判定为"启动成功"的那一刻）之后启动器主窗口应该做什么，
    /// 见 PostGameLaunchAction 枚举注释。默认 KeepAsIs，不改变任何用户没预期到的行为。</summary>
    public PostGameLaunchAction PostGameLaunchAction { get; set; } = PostGameLaunchAction.KeepAsIs;

    /// <summary>没有明显关闭按钮的内嵌弹窗（Overlay），停留 10 秒用户还没有任何操作时，
    /// 是否弹一条"Esc 可以关闭"的小提示 Toast。默认开启；用户点了 Toast 上的
    /// "以后不再提示"或者在设置页关掉这个开关后变成 false，见 OverlayDialogService.Push。</summary>
    public bool ShowEscCloseHint { get; set; } = true;

    /// <summary>是否开机自启动（写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，
    /// 不需要管理员权限，只影响当前登录用户）。默认关闭。</summary>
    public bool AutoStartOnBoot { get; set; } = false;

    /// <summary>仅在“开机自启动”这条启动路径中生效；用户平时双击启动器不受影响。</summary>
    public AutoStartLaunchBehavior AutoStartBehavior { get; set; } = AutoStartLaunchBehavior.ShowWindow;

    /// <summary>
    /// 收藏的游戏版本 ID 列表（来自下载中心的"☆ 收藏"按钮）。
    /// 保留这个字段只是为了兼容老版本配置文件（升级前已经收藏过版本的用户，配置文件里
    /// 只有这个字段，没有下面的 FavoriteItems）——ConfigService 加载时会把这里的内容
    /// 一次性搬进 FavoriteItems（Type=Version），此后新增/取消收藏统一走 FavoriteItems，
    /// 这个列表不再被写入，只在加载老配置时读一次。新代码不要再往这里加东西。
    /// </summary>
    public List<string> FavoriteVersionIds { get; set; } = new();

    /// <summary>
    /// "从列表中删除"的实例（不删文件）。已安装版本列表是每次直接扫描 versions/ 目录得到的
    /// （见 FolderService.ScanVersions），本身没有持久化列表可"移除"，所以这里单独维护一份
    /// "用户主动隐藏、暂时不想在列表里看到"的黑名单，扫描结果里命中的直接过滤掉，不影响磁盘
    /// 上的文件——跟 .NOXCL（整个目录级别、面向"不想让 xcl 探测"）不是一回事：这里是单个
    /// 实例级别、面向"看得到目录但暂时不想在列表里显示"，且没有对应的"新建标记文件"这种
    /// 用户可自行操作的入口，只能通过启动器界面添加/移除。
    ///
    /// key 格式："{文件夹路径}|{版本Id}"（两者都不区分大小写，比较前统一转小写），用文件夹路径
    /// 加以限定是因为同一个版本 Id（比如 "1.20.1"）完全可能同时出现在不同的 .minecraft 目录下，
    /// 不加文件夹前缀会导致"在 A 文件夹隐藏了 1.20.1，结果 B 文件夹的 1.20.1 也跟着消失"这种
    /// 跨文件夹误伤，虽然 VersionJavaIdOverrides 等老字段是这么做的（只用 versionId 做 key），
    /// 但这里新加字段没有历史包袱，直接用更严谨的复合 key，不重复那个已知不够精确的写法。
    /// </summary>
    public List<string> HiddenInstanceKeys { get; set; } = new();

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
    /// 「访客模式」：严格属于当前进程会话。开启后主页账户是只存在于本次运行的临时离线账户
    /// （不写入 accounts.json、不进入持久账户列表），关闭时清理本次会话新产生的日志/临时下载；
    /// GuestModeEnabled 本身也不会持久化为 true，启动器重启后始终自动回到普通模式。
    /// 默认关闭。见 <see cref="Services.GuestModeService"/> 与 ConfigService.Save()。
    /// </summary>
    public bool GuestModeEnabled { get; set; } = false;

    /// <summary>
    /// 界面配色"色系"：内置色系或 Custom 自定义主题（Dark 作为独立色系保留兼容旧配置，
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

    /// <summary>
    /// 是否开启"跟随系统深浅色"：开启后 <see cref="IsDarkMode"/> 由 Windows 系统当前的
    /// 应用深浅色主题设置（"设置-个性化-颜色-选择您的模式"里的"应用模式"）决定，系统主题
    /// 一变化就立即跟着切换，不需要用户手动点「模式设置」按钮，也不需要像
    /// <see cref="AutoThemeCycleEnabled"/> 那样自己配置切换时间点。
    /// 默认关闭：不影响老用户已经习惯的手动/按时间自动模式。
    ///
    /// 跟 <see cref="AutoThemeCycleEnabled"/>（按固定时间点自动循环）是两种互斥的"自动"来源——
    /// 两个不会同时生效，设置页/首页会保证同一时刻只有一个处于开启状态，开一个会自动关掉
    /// 另一个。跟用户手动点「模式设置」按钮的关系也是"手动优先"：开着跟随系统时，手动点一下
    /// 按钮可以临时覆盖，但下一次系统主题变化事件触发时还是会被重新接管。
    /// </summary>
    public bool FollowSystemTheme { get; set; } = false;

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

    /// <summary>低性能模式：关闭页面动画、窗口特效和额外阴影。</summary>
    public bool LowPerformanceMode { get; set; } = false;

    /// <summary>启动器是否默认置于其它窗口之上；F3 可在当前会话临时切换。</summary>
    public bool AlwaysOnTop { get; set; } = false;

    public bool ScheduledInstanceBackupEnabled { get; set; } = false;
    public int ScheduledInstanceBackupIntervalHours { get; set; } = 24;
    public int ScheduledInstanceBackupRetentionCount { get; set; } = 5;
    public string? ScheduledInstanceBackupVersionId { get; set; }

    /// <summary>每次启动器启动、主界面首帧显示后自动备份实例。后台执行，不阻塞首帧。</summary>
    public bool BackupInstanceOnStartup { get; set; } = false;

    /// <summary>每次真正关闭主窗口前自动备份实例；备份完成后才继续退出。</summary>
    public bool BackupInstanceOnClose { get; set; } = false;

    /// <summary>启动/关闭自动备份时的实例选择方式。</summary>
    public LifecycleBackupTargetMode LifecycleBackupTargetMode { get; set; } = LifecycleBackupTargetMode.Single;

    /// <summary>Multiple / Any / All 模式下的候选实例 ID，限定在当前选择的游戏文件夹中。</summary>
    public List<string> LifecycleBackupVersionIds { get; set; } = new();
    public string? CustomBackgroundImagePath { get; set; }

    /// <summary>
    /// 自定义毛玻璃背景图片的磨砂强度。取值范围 25~100：25 接近透明/清晰，
    /// 100 为最强磨砂。该值只控制用户导入的背景图片层，不与“面板透明度”绑定，
    /// 因此切换面板透明度或 Win11 Mica/Acrylic 材质时不会把用户选好的磨砂度覆盖掉。
    /// </summary>
    public int CustomBackgroundFrostPercent { get; set; } = 65;

    /// <summary>实际使用离线账户启动的累计次数。</summary>
    public int OfflineLaunchCount { get; set; } = 0;
    /// <summary>用户已完成一次捐助后不再提示。</summary>
    public bool DonationAcknowledged { get; set; } = false;

    /// <summary>Custom 自定义主题使用的主色。只有 UiSkin=Custom 时作为主题强调色生效；
    /// 选择其它预设色系时会保留该值但不覆盖预设主题，方便之后切回自定义继续使用。</summary>
    public string? CustomAccentColor { get; set; }

    /// <summary>是否启用 WinUI 3 风格外观。仅保存设置，修改后提示重启启动器。</summary>
    public bool EnableWinUi3Design { get; set; } = false;

    /// <summary>界面字体，取值对应 ThemeService.FontFamilyOptions 里的 Tag，空字符串="跟随系统"。
    /// 跟 EnableWinUi3Design 是两个独立维度，见 ThemeService.ApplyFontFamily 注释：WinUi3 只在
    /// 候选链最前面加西文可变字重字体，这里选的字体（含中文兜底）负责实际显示的中文字形，
    /// 修复"部分字显示不全"——原来候选链末尾没有显式中文字体兜底，个别 Windows 语言/字体
    /// 安装环境下系统隐式回退选中的字体行高跟界面预留空间对不上，中文字符底部被裁掉一点。</summary>
    public string AppFontFamily { get; set; } = "";

    /// <summary>
    /// 字体"分层/分块"设置：在全局界面字体（<see cref="AppFontFamily"/>）之上，允许对
    /// 标题栏 / 侧边导航栏 / 右侧内容区 三个视觉分区分别指定一款系统已安装字体覆盖，
    /// 不影响其它分区（仍然用全局字体）。留空字符串或 null 表示该分区"跟随全局"，
    /// 不做任何覆盖。取值是 System.Windows.Media.Fonts.SystemFontFamilies 里的字体
    /// 家族名字符串（如 "Cascadia Code"、"华文楷体"），由 SettingsPage 的三个下拉框
    /// 直接列出本机已安装字体供选择，而不是像 <see cref="AppFontFamily"/> 那样固定几个
    /// 预置候选。见 Services/FontService.cs 的应用逻辑与 MainWindow.xaml 里三个分区
    /// 容器（CustomTitleBar / SidebarAreaBorder / ContentAreaBorder）。
    /// </summary>
    public string? AppFontFamily_TitleBar { get; set; }
    public string? AppFontFamily_Sidebar { get; set; }
    public string? AppFontFamily_Content { get; set; }

    /// <summary>界面整体亮度，0~200，100 为不调整（原样显示）。低于 100 整体调暗，
    /// 高于 100 整体调亮，见 ThemeService.ApplyBrightness 注释——WPF 没有系统级"亮度"概念，
    /// 这里用一层盖在最上面的全局遮罩（黑色/白色，随数值调整不透明度）模拟效果，
    /// 不是真的调整了显示器/系统亮度，只影响启动器窗口内部的观感。</summary>
    public int BrightnessPercent { get; set; } = 100;

    /// <summary>
    /// 窗口透明度开关。默认 false（关闭），需要用户在设置页「外观与视觉效果」里主动开启——
    /// 这是一个纯装饰性的视觉功能，不影响任何现有窗口行为，所以刻意默认关闭，避免老用户
    /// 升级后界面观感突然发生变化。开启后按 <see cref="WindowOpacityPercent"/> 让主窗口/
    /// 各弹窗的侧栏、卡片等面板呈现半透明的\"玻璃质感\"，见 Services/Win11EffectsService.cs。
    /// 出于兼容性考虑（项目里所有窗口都是标准系统边框窗口，没有用 AllowsTransparency + 
    /// WindowStyle=None 的自绘窗口，见 WindowChromeService 类注释里的取舍说明），这里做的是
    /// \"内容面板半透明\"而不是真正的整窗操作系统级像素穿透——这样不需要重写窗口拖动/
    /// 缩放/贴边等系统级行为，风险和改动范围都小得多，视觉效果上依然能看到底层桌面/
    /// 其它窗口透出来的朦胧效果。
    /// </summary>
    public bool EnableWindowTransparency { get; set; } = false;

    /// <summary>
    /// 窗口透明度百分比，仅在 <see cref="EnableWindowTransparency"/> 开启时生效。
    /// 取值范围 20~100（100 等同于完全不透明，20 是允许的最透明程度）。默认 88，是一个观感上\"能看出透明质感、
    /// 又不影响阅读\"的折中值。
    /// </summary>
    public int WindowOpacityPercent { get; set; } = 88;

    /// <summary>
    /// Windows 11 新光效/新图形设计支持开关。默认 false（关闭，需要去设置里手动开启）——
    /// 这是纯视觉增强，且依赖 Windows 11 才有的 DWM 特性（Mica 云母材质背景、窗口圆角、
    /// 更强的悬停/点击光晕过渡），在 Windows 10 或更早系统上会被静默忽略、不影响任何功能，
    /// 但既然是"新"设计就不该在老用户毫无预期的情况下自动打开，所以跟窗口透明度一样默认关闭。
    /// 开启后见 Services/Win11EffectsService.cs：给窗口套用 DWM 云母背景材质 + 圆角，
    /// 同时给 ThemeService 应用的强调色/悬停色加一层\"沉浸光感\"渐变光晕效果。
    /// </summary>
    public bool EnableWin11VisualEffects { get; set; } = false;

    /// <summary>Win11 新光效开启时，主窗口用哪种背景材质：Mica（默认，贴合壁纸的柔和渐变）/
    /// MicaAlt（云母加深版，层次更明显）/ Acrylic（真正的毛玻璃磨砂模糊，透感更强）。
    /// 存字符串而不是数字，方便直接对应 Win11EffectsService.BackdropMaterial 的枚举名，
    /// 配置文件里可读性也更好。旧配置文件没有这一项时按 Mica 处理，跟以前的固定行为一致。</summary>
    public string Win11BackdropMaterial { get; set; } = "Mica";

    /// <summary>
    /// 全局窗口透明（整窗级别的像素穿透，而不是 <see cref="WindowOpacityPercent"/> 那种只让
    /// 侧栏/卡片等面板半透明的"内容面板半透明"）。默认关闭。开启后通过 Window.Opacity 直接
    /// 让整个窗口（含标题栏、边框、所有内容）按 <see cref="GlobalWindowOpacityPercent"/> 呈现
    /// 半透明效果，能看到窗口后方桌面/其它窗口透出来。跟 EnableWindowTransparency 是两套独立
    /// 效果，可以同时开：面板透明负责"玻璃质感"的层次感，这个负责"整窗都能透"的通透感。
    /// </summary>
    public bool EnableGlobalWindowTransparency { get; set; } = false;

    /// <summary>
    /// 全局窗口透明度百分比，仅在 <see cref="EnableGlobalWindowTransparency"/> 开启时生效。
    /// 取值范围 50~100（50 是允许的最透明程度——整窗透明比面板透明更容易影响可读性，
    /// 所以下限比 <see cref="WindowOpacityPercent"/> 更保守）。默认 80。
    /// </summary>
    public int GlobalWindowOpacityPercent { get; set; } = 80;

    /// <summary>界面主要文字的不透明度百分比。默认 100，范围 50~100。它只调整文字画刷，
    /// 不会改变背景/卡片透明度；用于背景特别通透时单独把文字拉回清晰。</summary>
    public int TextOpacityPercent { get; set; } = 100;

    /// <summary>
    /// 设置页"是否可以直接保存，无需点击保存设置"。默认关闭（false）——维持原有的
    /// "改完必须手动点保存设置按钮"流程，避免老用户误触发不想要的自动保存。
    /// 开启后，设置页里任何控件的改动都会在短暂防抖（约 0.4 秒无新改动）后自动保存，
    /// 并在左下角保留一张“设置已自动保存 / 回退”操作卡片；它不会计时自动消失，
    /// 用户可随时点击“回退”撤销最近一次自动保存（见 SettingsPage.OnSettingsEdited）。
    /// 关闭时改为左下角保留“设置已修改，是否保存”的操作卡片（撤销/保存两个按钮），
    /// 以及切换到其它页面时的三选一确认弹窗。
    /// </summary>
    public bool SettingsAutoSaveWithoutConfirm { get; set; } = false;

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

    /// <summary>"今日第几次启动启动器"计数所属的日期（yyyy-MM-dd）。跟 <see cref="TodayLauncherStartCount"/>
    /// 配套使用：LauncherLogService 在每次启动器启动时检查这个日期是否等于今天，不等于就把计数清零重新开始，
    /// 用来给 xcl2/logs/ 下每次会话的日志文件命名（见 LauncherLogService 类注释）。</summary>
    public string? LastLaunchCountDate { get; set; }

    /// <summary>当天（LastLaunchCountDate 那一天）启动器已经被打开的次数，跨天自动从 0 重新计数。</summary>
    public int TodayLauncherStartCount { get; set; } = 0;

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

    // ===== 基岩版客户端 =====

    /// <summary>
    /// 是否已同意过《基岩版分发协议》（微软对 Minecraft for Windows 客户端及基岩版
    /// 专用服务端的分发法律协议）。默认 false：进入「基岩版启动」页面时必须先同意这份
    /// 协议，不同意就停留在当前页面不进入（可再次点进入重新同意）；同意后写入 true
    /// 持久化，本次及后续进入不再重复弹协议。
    /// 见 MainWindow.OpenBedrockWithAgreementGate。
    /// </summary>
    public bool BedrockAgreementAccepted { get; set; } = false;

    /// <summary>
    /// 基岩版客户端（Bedrock Edition Windows Client）下载时的默认安装文件夹。
    /// null/空 = 每次下载都弹文件夹选择框。
    /// </summary>
    public string? BedrockClientDefaultDownloadDir { get; set; }

    /// <summary>
    /// 用户手动选择的、已安装的 Microsoft Store 版基岩版所在文件夹（比如
    /// "...\XboxGames\Minecraft for Windows\Content"）。用来在自动检测（PowerShell
    /// 查询 Appx 包）失败或不可用时提供一个手动兜底：只要这个目录里能找到
    /// Minecraft.Windows.exe，就认为"已安装"，「启动基岩版」直接运行这个 exe。
    /// null/空 = 没有手动指定，完全依赖自动检测。
    /// </summary>
    public string? BedrockManualInstallDir { get; set; }


    /// <summary>
    /// 已下载的基岩版客户端实例列表（每个实例对应一个独立目录）。
    /// </summary>
    public List<BedrockClientRecord> BedrockClients { get; set; } = new();

    // ===== 基岩版专用服务端（BDS） =====

    /// <summary>
    /// 基岩版服务端（Bedrock Dedicated Server）下载时的默认安装文件夹。
    /// null/空 = 每次下载都弹文件夹选择框（旧行为）；设置了这个值之后，下载按钮默认直接用
    /// 这个文件夹（用户仍可以点"选择其他文件夹"临时改一次，不影响这里保存的默认值）。
    /// 跟 GameFolder（Java 版 .minecraft 多目录）是完全独立的两个概念，不要混用。
    /// </summary>
    public string? BedrockServerDefaultDownloadDir { get; set; }

/// <summary>
    /// 基岩版专用服务端（BDS）实例列表（每个实例对应一个独立目录，互不覆盖），
    /// 用于"下载完之后原地启动"、以及下次回到这个页面时能看到之前装过哪些版本。
    /// 存放位置：跟随 xcl2/config.json 一起持久化。
    /// </summary>
    public List<BedrockServerRecord> BedrockServers { get; set; } = new();

    // ===== AI 助手 =====

    /// <summary>AI 助手完整配置（API、模型表、普通/专家路由、上下文等）。</summary>
    public AiAssistantConfig AiAssistant { get; set; } = new();

    /// <summary>旧版本兼容字段。新版本保存时与 AiAssistant.ShowFloatingButton 同步。</summary>
    public bool AiAssistantFloatingButton { get; set; } = false;

    // ===== 下载通知设置 =====

    /// <summary>下载完成通知方式：0=弹窗(默认)，1=右下角角标，2=Windows自带通知，3=不提示</summary>
    public int DownloadNotifyMode { get; set; } = 0;

    /// <summary>游戏版本下载完成是否不再弹窗（改用角标/系统通知）</summary>
    public bool GameVersionNoPopup { get; set; } = false;

    /// <summary>社区资源下载完成是否不再弹窗</summary>
    public bool CommunityResourceNoPopup { get; set; } = false;

    /// <summary>整合包下载完成是否不再弹窗</summary>
    public bool ModpackNoPopup { get; set; } = false;

    /// <summary>标题栏下载气泡列表是否展示详情行（剩余时间/当前速度/大小），
    /// 开启后气泡整体高度放大 60% 以容纳这一行，见 MainWindow.xaml
    /// DownloadQueuePopup 与 DownloadQueueService.DownloadQueueItem 的 DetailText。</summary>
    public bool DownloadPopupShowDetailStats { get; set; } = false;

    /// <summary>下载详情行里"大小"这一项展示方式：0=剩余大小(默认)，1=已下载大小，2=两者都显示</summary>
    public int DownloadPopupSizeDisplayMode { get; set; } = 0;

    /// <summary>高性能模式：跟 LowPerformanceMode 相反，开启更强的切页/交互动效（缩放+位移+
    /// 回弹缓动）。默认开启：新配置、缺少该字段的旧配置以及“恢复默认设置”都会使用高性能模式。
    /// 用户仍可在设置页手动关闭；不做任何自动降级，仅由 FrameRateMonitorService 在持续低帧率时提示。</summary>
    public bool EnableHighPerformanceMode { get; set; } = true;

    // ===== 注册表功能（HKLM/HKCU 双路径存储） =====

    /// <summary>
    /// 「注册表功能」总开关。默认开启：一部分启动早期就要用到的设置（是否阅读/同意用户协议、
    /// 基本模式状态、界面配色等，具体字段清单见 <see cref="Services.RegistrySyncedFields"/>）
    /// 会以注册表 <c>HKEY_LOCAL_MACHINE\SOFTWARE\XCL2</c>（或 HKCU，取决于是否提权/是否开启
    /// <see cref="UseMachineWideRegistry"/>）为主存储，config.json 里的同名字段只作为镜像备份。
    /// 关闭后完全退回只用 config.json，不再读写注册表——这是"设置里支持关闭注册表功能"的实现点。
    /// 关闭这个开关本身不会自动删除已经写入的注册表项，删除需要用户在"危险操作"里单独确认执行。
    /// </summary>
    public bool RegistryFeatureEnabled { get; set; } = true;

    /// <summary>
    /// 是否使用"全设备"范围的注册表（HKLM）而不是"当前用户"范围（HKCU）。
    /// 默认 false：绝大多数用户以普通权限运行，写 HKCU 即可正常工作，不需要每次都弹 UAC。
    /// 打开后，只有当前进程**确实**以管理员身份运行时才会真正写入 HKLM；用户开着这个开关
    /// 但这次以普通权限运行时，会静默退化为写 HKCU（不报错、不阻塞保存），
    /// 保证"这次没有提权"也不会导致设置保存失败或者把已在 HKLM 里的旧设置弄丢
    /// （HKLM 里的内容只有真正用管理员权限运行时才会被这个开关驱动的写入触碰到）。
    /// 见 <see cref="Services.RegistryConfigService"/> 类头注释里的完整读写规则。
    /// </summary>
    public bool UseMachineWideRegistry { get; set; } = false;

    // ===== 正版账户令牌保留时效 =====

    /// <summary>
    /// 微软正版账户的"令牌保留时效"（天数）。当 access token 已过期、且尝试用 refresh token
    /// 静默刷新时因为网络问题/Mojang 认证服务本身故障（不是"refresh token 已失效"这种
    /// 明确的认证错误）而失败时，只要账户最近一次成功验证的时间距今不超过这个天数，
    /// 就允许直接使用本地缓存的正版 UUID + 用户名离线启动（跳过在线校验），
    /// 而不是直接报错拒绝启动——见 Account.LastVerifiedAtUtc、
    /// MicrosoftAuthService.RefreshAsync 调用处的降级处理。
    /// 默认 7 天：在"长时间断网/服务中断也能继续玩"和"长期不联网校验，令牌形同虚设"
    /// 之间取一个折中值，用户可在设置页调整（0 表示关闭这个降级，服务不可用时直接报错，
    /// 恢复到功能上线前的行为）。
    /// </summary>
    public int AccountTokenGracePeriodDays { get; set; } = 7;

    // ===== 弹窗（OverlayCard）/ 抽屉（AiAssistantPanel）独立外观 =====
    // 默认全部关闭（PopupUseCustomAppearance/DrawerUseCustomAppearance = false）：
    // 关闭时弹窗/抽屉的透明度、磨砂度、文字透明度都直接跟随主界面（WindowOpacityPercent
    // 等已有设置），跟旧版本行为完全一致，只有用户主动打开"独立设置"开关才会应用下面这几个
    // 单独的值。见 Views/AiAssistantSettingsDialog 附近的"外观"设置区块和
    // Services/ThemeService.ApplyPopupAppearance / ApplyDrawerAppearance。

    /// <summary>弹窗是否使用独立于主界面的透明度/磨砂度/文字透明度。默认 false（跟随主界面）。</summary>
    public bool PopupUseCustomAppearance { get; set; } = false;
    /// <summary>弹窗背景透明度百分比（20~100，100=完全不透明）。仅在 <see cref="PopupUseCustomAppearance"/> 开启时生效。</summary>
    public int PopupOpacityPercent { get; set; } = 92;
    /// <summary>弹窗磨砂强度（0~100，0=不磨砂/清晰，100=最强磨砂）。仅在 <see cref="PopupUseCustomAppearance"/> 开启时生效。</summary>
    public int PopupFrostPercent { get; set; } = 40;
    /// <summary>弹窗内文字透明度百分比（40~100）。仅在 <see cref="PopupUseCustomAppearance"/> 开启时生效。</summary>
    public int PopupTextOpacityPercent { get; set; } = 100;

    /// <summary>抽屉（AI 助手面板）是否使用独立于主界面的透明度/磨砂度/文字透明度。默认 false（跟随主界面）。</summary>
    public bool DrawerUseCustomAppearance { get; set; } = false;
    /// <summary>抽屉背景透明度百分比（20~100）。仅在 <see cref="DrawerUseCustomAppearance"/> 开启时生效。</summary>
    public int DrawerOpacityPercent { get; set; } = 92;
    /// <summary>抽屉磨砂强度（0~100）。仅在 <see cref="DrawerUseCustomAppearance"/> 开启时生效。</summary>
    public int DrawerFrostPercent { get; set; } = 40;
    /// <summary>抽屉内文字透明度百分比（40~100）。仅在 <see cref="DrawerUseCustomAppearance"/> 开启时生效。</summary>
    public int DrawerTextOpacityPercent { get; set; } = 100;

    /// <summary>鼠标滚轮灵敏度百分比。100 表示 ScrollWheelBehavior 的基准步长，
    /// 默认 90，较旧版本的固定高速滚动略微降低一点，减少长设置页一格滚得过头的感觉。
    /// 用户可在设置页调整，范围由 ScrollWheelBehavior.ClampSensitivityPercent 统一限制。</summary>
    public int MouseWheelSensitivityPercent { get; set; } = 90;

    /// <summary>是否启用"按住 Ctrl + 滚轮/方向键缩放整窗界面"功能。默认开启——跟浏览器
    /// Ctrl+滚轮缩放页面是同一套用户习惯，开启后不会影响任何现有操作（只有同时按住 Ctrl
    /// 才触发，单独滚轮/单独方向键完全不受影响）。</summary>
    public bool EnableUiZoomShortcut { get; set; } = true;
    /// <summary>缩放快捷键的具体绑定方式，见 <see cref="UiZoomShortcutMode"/>。可以同时勾选
    /// 多种（滚轮 + 方向键），不是互斥单选。</summary>
    public List<string> UiZoomShortcutBindings { get; set; } = new() { "CtrlWheel", "CtrlArrow" };
    /// <summary>当前界面缩放比例，100 = 原始大小，范围 40~300。跟随应用启动时的最后一次
    /// 缩放结果保存，下次打开保持用户上次调整的大小，不用每次重新缩放。</summary>
    public int UiZoomPercent { get; set; } = 100;
}

/// <summary>"Ctrl + 缩放"支持的两种触发方式，存成字符串 List 而不是单个枚举/bool，
/// 是因为需求是"可以绑定多个"——用户可以同时开滚轮和方向键，也可以只开其中一种。</summary>
public static class UiZoomShortcutMode
{
    /// <summary>Ctrl + 鼠标滚轮上下滚动。</summary>
    public const string CtrlWheel = "CtrlWheel";
    /// <summary>Ctrl + 键盘上下方向键。</summary>
    public const string CtrlArrow = "CtrlArrow";
    public static readonly string[] All = { CtrlWheel, CtrlArrow };
}

/// <summary>拖入 .zip 且内容特征不明确时的默认处理方式。</summary>
public enum DropZipDefault
{
    /// <summary>弹内嵌选择框问用户（默认）。</summary>
    Ask,
    /// <summary>一律当整合包处理。</summary>
    Modpack,
    /// <summary>一律当资源包（材质包）处理。</summary>
    ResourcePack,
}

/// <summary>拖入 .jar 时的默认安装去向。</summary>
public enum DropJarTarget
{
    /// <summary>装进当前选中客户端实例的 mods/。</summary>
    CurrentInstanceMods,
    /// <summary>装进当前选中服务器实例的 mods/（服务端 mod）。</summary>
    Server,
    /// <summary>每次都问。</summary>
    Ask,
}

/// <summary>
/// 社区资源（Mod 等）下载到本地后，文件名里中文名和原始文件名的组合方式。
/// 只有在能查到中文名（见 ModDisplayNameResolver）时才会生效——查不到中文名，
/// 不管选哪种样式，都只使用原始文件名，不留下"【】"或"-"这类空壳前后缀。
/// </summary>
public enum ModFileNamingStyle
{
    /// <summary>【中文名】原始文件名（默认）。</summary>
    FullBracket,
    /// <summary>[中文名] 原始文件名。</summary>
    SquareBracket,
    /// <summary>中文名-原始文件名。</summary>
    DashPrefix,
    /// <summary>原始文件名-中文名。</summary>
    DashSuffix,
    /// <summary>保持原始文件名，不加中文名。</summary>
    Original,
}
