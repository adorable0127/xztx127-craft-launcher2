using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 客户端"游戏加载器"(Fabric/Forge/NeoForge) 安装。
///
/// 修复"无法下载游戏加载器"：之前"下载中心"的「游戏版本」面板只对接了 Mojang 原版 version_manifest，
/// InstallVersion_Click -> DownloadService.InstallVersionAsync 从头到尾都只处理原版安装，
/// 完全没有任何入口能把 Fabric/Forge/NeoForge 装进 .minecraft/versions/ 下——用户在"版本选择"页
/// 只能看到原版版本，选不到、也没地方下载任何加载器版本，这是这个问题的根因。
/// 服务端那边的 ServerCoreDownloadService 只解决"服务端核心"下载，跟客户端加载器完全是两回事，
/// 不能直接复用（服务端核心不需要生成客户端能启动的 version json）。
///
/// 各加载器的实现方式：
/// - Fabric: meta.fabricmc.net/v2 直接提供"客户端可用的完整 version json"
///   （GET /v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json），不需要本地跑安装器，
///   json 里已经包含 inheritsFrom 指向原版、mainClass、libraries 等全部信息，
///   LauncherService 已经原生支持 inheritsFrom 继承链（见该类关于 inheritsFrom 的注释），拿到就能直接用。
///   这是三种加载器里最简单可靠的一种，优先完整实现。
/// - Forge/NeoForge: 官方只发布"安装器 jar"，没有直接可下载的客户端 version json，必须本地用 Java
///   跑一次 `java -jar xxx-installer.jar --installClient <.minecraft目录>`，安装器会自己下载所需库文件
///   并在 versions/ 下写入对应的 version json + libraries。这里的实现直接复用
///   ServerCoreDownloadService.RunForgeInstallerAsync 已经验证过的"起进程 + 读输出 + 判断退出码"模式，
///   只是把参数从 --installServer 换成 --installClient。
/// </summary>
public class ClientLoaderInstallService : IDisposable
{
    /// <summary>
    /// 跟 LauncherService 里同名方法逻辑一致（该文件改名容错查找）：优先按"文件夹名/版本 id"找精确
    /// 文件名，找不到就退化为"文件夹里唯一一个该后缀的文件"。做成独立实例时需要把原版 client.jar
    /// 拷贝进加载器自己的文件夹，这里要用同样的容错方式去定位原版 jar，避免用户手动改过原版
    /// 版本文件夹名字时找不到文件。
    /// </summary>
    private static string? ResolveVersionFile(string dir, string preferredBaseName, string extension)
    {
        var exact = Path.Combine(dir, $"{preferredBaseName}.{extension}");
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        var matches = Directory.GetFiles(dir, $"*.{extension}");
        return matches.Length == 1 ? matches[0] : null;
    }

    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    /// <summary>Quilt 官方 Meta API，接口形状(端点路径/返回字段)跟 Fabric Meta 几乎一一对应——
    /// Quilt 本来就是从 Fabric Loader fork 出来的，两边团队一直保持 Meta API 兼容，
    /// 这也是下面 Quilt 相关方法能直接照抄 Fabric 那几个方法、只换 base url 和产物文件名前缀的原因。</summary>
    private const string QuiltMetaBase = "https://meta.quiltmc.org/v3";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly DownloadService _vanillaDownloader;

    /// <summary>用户在设置里选的下载源，供 DownloadFileNoHashCheckAsync/post-install 库补全复用
    /// DownloadEndpoints 的镜像回退逻辑（构造函数里保存一份，避免每处都要多传一个参数）。</summary>
    private readonly bool _preferMirror;

    /// <summary>Fabric API 走 Modrinth 下载（Fabric API 本身就是发布在 Modrinth 上的普通 mod，
    /// 复用现成的 ModrinthService 搜索+下载逻辑，不用重新实现一遍 Modrinth API 调用。</summary>
    private readonly ModrinthService _modrinth = new();

    /// <summary>按完整 AppConfig 构造：加载器安装同样需要先装一个原版底座（父版本），
    /// 走的正是内部这份 _vanillaDownloader，理应享受跟"下载中心-游戏版本"面板一样的
    /// 多线程下载/限速配置，而不是永远单线程——否则用户开了多线程下载，唯独装 Fabric/Forge 时
    /// 感觉不到任何加速，体验不一致。</summary>
    public ClientLoaderInstallService(AppConfig cfg)
        : this(cfg.Source)
    {
        _vanillaDownloader.Dispose();
        _vanillaDownloader = DownloadService.CreateFromConfig(cfg);
    }

    /// <summary>沿用旧签名：只传 DownloadSource，内部退化为单线程不限速的 DownloadService
    /// （等价于以前的行为）。保留这个构造是为了不强迫所有调用方立刻改成传完整 AppConfig。</summary>
    public ClientLoaderInstallService(DownloadSource source)
    {
        _vanillaDownloader = new DownloadService(source);
        _preferMirror = source != DownloadSource.Official;
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2Launcher", "1.0"));
    }

    /// <summary>Fabric：客户端支持的 MC 版本列表（与服务端是同一份数据源）。</summary>
    public async Task<List<string>> GetFabricMcVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{FabricMetaBase}/versions/game", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && !stable.GetBoolean()) continue;
            result.Add(v.GetProperty("version").GetString()!);
        }
        return result;
    }

    /// <summary>
    /// Fabric：可用 loader 版本列表。
    ///
    /// ===== 修复安装 Fabric 报 404 (Not Found) =====
    /// 旧实现请求的是 /v2/versions/loader —— 这是**全量** loader 列表，跟具体哪个 MC 版本
    /// 无关，从 0.x 最早的构建一路列到最新。拿这个列表里的任意一项去拼
    ///     /v2/versions/loader/{mc}/{loader}/{installer}/profile/json
    /// 时，只要这对 (mcVersion, loaderVersion) 在 Fabric 那边**没有交集条目**，服务端就直接
    /// 返回 404，于是 GetStringAsync 抛 HttpRequestException("...404 (Not Found)")，
    /// 整个安装流程报"安装失败"——这正是日志里那条异常的来源。
    ///
    /// 典型触发场景有两种，用户都很容易碰到：
    /// - 选了一个较新的 MC 版本（例如截图里的 26.x），但被默认选中的 loader 是列表里
    ///   第一个 stable 项，未必声明支持这个 MC 版本；
    /// - 选了很老的 MC 版本，而新 loader 早已不再为它发布交集条目。
    ///
    /// 正确做法是用 Fabric Meta 官方提供的**按游戏版本查交集**端点：
    ///     GET /v2/versions/loader/{gameVersion}
    /// 它只返回"确实能用在这个 MC 版本上"的 loader，逐条都保证 profile/json 能取到。
    /// gameVersion 做一次 URL 编码，避免版本号里出现空格/特殊字符时拼出非法 URL。
    /// </summary>
    /// <param name="mcVersion">必填。为空时退回全量列表（仅用于"还没选 MC 版本"的过渡态，
    /// 真正安装前调用方必须已经选定 MC 版本）。</param>
    public async Task<List<ServerCoreBuild>> GetFabricLoaderVersionsAsync(string? mcVersion = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(mcVersion)
            ? $"{FabricMetaBase}/versions/loader"
            : $"{FabricMetaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}";

        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 这个 MC 版本在 Fabric 那边压根没有任何 loader 交集——直接给一句人话，
            // 而不是把 "404 (Not Found)" 原样甩给用户。
            throw new InvalidOperationException(
                $"Fabric 目前还没有为 Minecraft {mcVersion} 发布 Loader。\n" +
                "通常是因为这个版本太新（Fabric 还没跟进）或太老（已停止支持）。" +
                "可以换一个 MC 版本，或者过几天等 Fabric 更新后再试。", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            // 按游戏版本查交集时，返回的每一项形如 { "loader": {...}, "intermediary": {...} }，
            // 真正的 loader 信息在 "loader" 子对象里；全量列表则是平铺的。两种形状都兼容。
            var node = v.TryGetProperty("loader", out var loaderNode) ? loaderNode : v;
            if (!node.TryGetProperty("version", out var verEl)) continue;

            result.Add(new ServerCoreBuild
            {
                DisplayVersion = verEl.GetString()!,
                IsRecommended = node.TryGetProperty("stable", out var stable) && stable.GetBoolean()
            });
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"Fabric 没有返回任何适用于 Minecraft {mcVersion} 的 Loader 版本，无法继续安装。");

        return result;
    }

    /// <summary>Quilt：客户端支持的 MC 版本列表。跟 GetFabricMcVersionsAsync 是同一套过滤逻辑
    /// (只保留 stable=true 的正式版)，Quilt Meta 的 /versions/game 返回结构跟 Fabric Meta 一致。</summary>
    public async Task<List<string>> GetQuiltMcVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{QuiltMetaBase}/versions/game", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && !stable.GetBoolean()) continue;
            result.Add(v.GetProperty("version").GetString()!);
        }
        return result;
    }

    /// <summary>Quilt：可用 loader 版本列表。跟 GetFabricLoaderVersionsAsync 同样的 404 修复
    /// （见那边的详细注释）——Quilt Meta 的端点形状跟 Fabric Meta 一一对应，
    /// /v3/versions/loader/{gameVersion} 同样返回"该 MC 版本可用的 loader 交集"。</summary>
    public async Task<List<ServerCoreBuild>> GetQuiltLoaderVersionsAsync(string? mcVersion = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(mcVersion)
            ? $"{QuiltMetaBase}/versions/loader"
            : $"{QuiltMetaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}";

        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Quilt 目前还没有为 Minecraft {mcVersion} 发布 Loader。\n" +
                "可以换一个 MC 版本，或者过几天等 Quilt 更新后再试。", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            // 同 Fabric：按游戏版本查交集时条目是 { "loader": {...}, "intermediary": {...} }，
            // 全量列表时是平铺的。两种形状都要认，否则按版本查回来的数据会全部取不到 "version"
            // 而抛 KeyNotFoundException（表现同样是"安装失败"，只是异常类型不同）。
            var node = v.TryGetProperty("loader", out var loaderNode) ? loaderNode : v;
            if (!node.TryGetProperty("version", out var verEl)) continue;
            var ver = verEl.GetString()!;

            result.Add(new ServerCoreBuild
            {
                DisplayVersion = ver,
                // Quilt Meta 的 loader 列表条目本身不像 Fabric 那样带 "stable" 字段，
                // 官方约定"不含 -beta/-rc 等预发布后缀的版本号"即视为稳定版，
                // 用字符串是否包含连字符来判断，跟 Quilt 官方文档/其它启动器(PCL2/HMCL)
                // 采用的判断口径一致。
                IsRecommended = !ver.Contains('-')
            });
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"Quilt 没有返回任何适用于 Minecraft {mcVersion} 的 Loader 版本，无法继续安装。");

        return result;
    }

    /// <summary>Forge：有安装器构建的 MC 版本列表（客户端和服务端安装器是同一个 jar，只是参数不同）。
    /// 实际逻辑已抽到 ForgeVersionQueryService（见该类注释：跟 ServerCoreDownloadService 消除重复代码）。</summary>
    public Task<List<string>> GetForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeVersionsAsync(_http, ct);

    public Task<List<ServerCoreBuild>> GetForgeInstallerVersionsAsync(string mcVersion, CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeInstallerVersionsAsync(_http, mcVersion, ct);

    /// <summary>
    /// NeoForge：可用完整版本号列表。逻辑已抽到 ForgeVersionQueryService（见该类注释里
    /// 关于 404 bug 根因的说明），这里只是保留原有的方法签名，方便调用方不用改。
    /// </summary>
    public Task<List<string>> GetNeoForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetNeoForgeVersionsAsync(_http, ct);

    /// <summary>Fabric API 在 Modrinth 上的项目 slug，固定值（官方项目，不会变）。</summary>
    private const string FabricApiModrinthSlug = "fabric-api";

    /// <summary>
    /// GetStringAsync 的包装：把 HTTP 404 翻译成一句用户能看懂的话。
    ///
    /// 之前所有 Meta 请求都是裸的 _http.GetStringAsync，一旦 404 就直接把
    /// "Response status code does not indicate success: 404 (Not Found)." 连同整个
    /// 调用栈抛到界面上——用户完全无法从这句话判断"是我选错了版本组合"还是"网炸了"。
    /// 其它状态码（超时/5xx/DNS 失败）保持原样抛出，交给上层的
    /// ErrorPresenter.ShowFriendlyError 统一按"网络问题"处理，这里只专门处理 404，
    /// 因为只有 404 才明确对应"这个组合不存在"这一种业务含义。
    /// </summary>
    private async Task<string> GetStringWithFriendly404Async(string url, string friendlyMessage, CancellationToken ct)
    {
        try
        {
            return await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(friendlyMessage, ex);
        }
    }

    /// <summary>
    /// 修复"安装 Fabric/Quilt 报 404"的第二处根因（第一处是 GetFabricLoaderVersionsAsync 已经
    /// 修过的"全量列表 vs 按版本查交集"）：即使下拉框当时给的是正确的交集列表，UI 层仍然可能把
    /// 一个跟当前 mcVersion 不匹配的 loaderVersion 传进安装方法——常见诱因包括用户切换 MC 版本后
    /// 下拉框没来得及刷新就点了安装、缓存的旧选择、或者手动传参调用。旧实现对这种情况完全不做
    /// 校验，直接拿 (mcVersion, loaderVersion) 去拼 profile/json，遇到不存在的交集就是日志里那条
    /// "Fabric 没有这个组合的安装信息" 404。
    ///
    /// 这里加一层自动匹配兜底：先按 mcVersion 查一次真正的 loader 交集列表，如果调用方传入的版本
    /// 不在里面，不再直接报错，而是自动换成交集列表里最新的稳定版（找不到稳定版就退到列表第一项，
    /// 即 Meta 按新到旧排序的最新构建）重试，并通过 progress 告知用户发生了自动切换——用户仍然
    /// 能看到实际装的是哪个版本，而不是静默换掉又不说一声。
    /// </summary>
    /// <param name="metaBase">Fabric 用 FabricMetaBase，Quilt 用 QuiltMetaBase。</param>
    /// <param name="loaderLabel">仅用于进度提示文案，"Fabric" 或 "Quilt"。</param>
    private async Task<string> ResolveLoaderVersionOrAutoMatchAsync(string metaBase, string loaderLabel,
        string mcVersion, string requestedLoaderVersion, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var url = $"{metaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}";
        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 这个 MC 版本在该加载器那边压根没有任何交集条目，自动匹配也无从谈起，
            // 直接给出跟 GetFabricLoaderVersionsAsync 一致的友好提示。
            throw new InvalidOperationException(
                $"{loaderLabel} 目前还没有为 Minecraft {mcVersion} 发布 Loader，无法安装。\n" +
                "可以换一个 MC 版本，或者过几天等官方更新后再试。", ex);
        }

        using var doc = JsonDocument.Parse(json);
        // (版本号, 是否稳定版) 的有序列表——Meta 接口本身就按新到旧排序，这里保持原始顺序，
        // "取第一个稳定版" 天然就是"支持这个 MC 版本的最高稳定版"，不需要额外做版本号比较。
        var candidates = new List<(string Version, bool Stable)>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var node = v.TryGetProperty("loader", out var loaderNode) ? loaderNode : v;
            if (!node.TryGetProperty("version", out var verEl)) continue;
            var ver = verEl.GetString();
            if (string.IsNullOrEmpty(ver)) continue;
            var stable = node.TryGetProperty("stable", out var stableEl) && stableEl.GetBoolean();
            candidates.Add((ver, stable));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"{loaderLabel} 没有返回任何适用于 Minecraft {mcVersion} 的 Loader 版本，无法继续安装。");

        if (candidates.Any(c => c.Version == requestedLoaderVersion))
            return requestedLoaderVersion;

        // 请求的版本不在当前 MC 版本的交集里——自动换成最高的稳定版（拿不到稳定版就退到列表
        // 第一项）。不直接抛异常，这正是"自动匹配功能：自动安装适合版本的最高版本"这个需求要的行为。
        var fallback = candidates.FirstOrDefault(c => c.Stable).Version ?? candidates[0].Version;
        progress?.Report(new ProgressInfo(
            $"{loaderLabel} Loader {requestedLoaderVersion} 不支持 Minecraft {mcVersion}，" +
            $"已自动改用兼容的最高版本 {fallback}",
            0, 1, fallback));
        return fallback;
    }

    /// <summary>
    /// 安装 Fabric 客户端到 .minecraft/versions/{versionId}/。
    /// 步骤：1) 先确保原版父版本已安装（Fabric json 靠 inheritsFrom 引用它，父版本缺失会导致启动失败）；
    /// 2) 直接下载 Fabric Meta 提供的现成客户端 profile json；3) 按 json 里的 libraries 列表补下依赖库
    /// （复用 DownloadService 里已经验证过的"支持 Fabric/Quilt 风格 name+url 库条目"的下载逻辑，
    /// 不重新实现一遍，避免产生和已修复过的语言文件/native 库下载 bug 相同的问题）；
    /// 4) 可选：装好 Fabric Loader 之后再从 Modrinth 拉一份 Fabric API 放进 mods/（很多 Fabric 模组
    /// 都依赖它，是 Fabric 生态里事实上的"标准库"，新手不知道要单独装这个是很常见的安装失败原因）。
    /// </summary>
    /// <param name="installFabricApi">true 时额外从 Modrinth 下载安装 Fabric API（可选步骤，
    /// 失败不影响 Fabric Loader 本身的安装结果——见方法内部注释）。</param>
    /// <param name="customInstanceName">用户自定义的实例名（对应 versions/ 下的文件夹名）。
    /// 传 null 或空字符串时退回默认格式（Fabric Meta 返回的 "fabric-loader-{loader}-{mc}" 风格 id），
    /// 跟这个方法改造前的行为完全一致，不影响没有用到这个新参数的旧调用方。传了值时会经过
    /// SanitizeInstanceName/MakeUniqueInstanceName 规整+去重（同「导入整合包」流程复用同一套
    /// 命名规则，保证两处生成的实例文件夹命名风格一致）。</param>
    public async Task<string> InstallFabricClientAsync(string minecraftDir, string mcVersion, string loaderVersion,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, bool installFabricApi = false,
        string? customInstanceName = null)
    {
        // 1. 确保原版父版本已装好（Fabric 客户端启动需要原版 client.jar + assets）
        var parentVersionDir = Path.Combine(minecraftDir, "versions", mcVersion);
        if (!File.Exists(Path.Combine(parentVersionDir, $"{mcVersion}.jar")))
        {
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installing_The_Base_Vanilla_Version", "安装原版父版本"), 0, 1, mcVersion));
            var manifest = await _vanillaDownloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                ?? throw new InvalidOperationException($"在版本清单中找不到 MC 版本 {mcVersion}，无法安装 Fabric 所需的原版父版本。");
            await _vanillaDownloader.InstallVersionAsync(minecraftDir, entry, progress, ct);
        }

        // 1.5 校验 loaderVersion 是否真的支持这个 mcVersion，不支持就自动换成兼容的最高版本
        //     （见 ResolveLoaderVersionOrAutoMatchAsync 注释：修复"选了不兼容的 Loader 版本导致
        //     profile/json 404"）。这一步必须在下面拼 profile url 之前做，否则还是会 404。
        loaderVersion = await ResolveLoaderVersionOrAutoMatchAsync(
            FabricMetaBase, "Fabric", mcVersion, loaderVersion, progress, ct);

        // 2. 拉取 installer 版本（取最新稳定版，用户不需要关心这个号）
        progress?.Report(new ProgressInfo("查询 Fabric installer 版本", 0, 1, loaderVersion));
        var installerJson = await GetStringWithFriendly404Async(
            $"{FabricMetaBase}/versions/installer",
            "无法获取 Fabric 安装器版本列表，请检查网络或稍后重试。", ct);
        using var installerDoc = JsonDocument.Parse(installerJson);
        string? installerVersion = null;
        foreach (var v in installerDoc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && stable.GetBoolean())
            {
                installerVersion = v.GetProperty("version").GetString();
                break;
            }
        }
        installerVersion ??= installerDoc.RootElement[0].GetProperty("version").GetString();

        // 3. 下载现成的客户端 profile json（含 inheritsFrom + libraries + mainClass，官方生成好的，不用自己拼）
        //    版本号统一做 URL 编码：MC 版本号里可能出现空格（用户手改过的目录名）、
        //    loader 版本号里可能出现 "+build.xxx" 这类字符，不编码就会拼出非法 URL。
        var profileUrl = $"{FabricMetaBase}/versions/loader/" +
                         $"{Uri.EscapeDataString(mcVersion)}/" +
                         $"{Uri.EscapeDataString(loaderVersion)}/" +
                         $"{Uri.EscapeDataString(installerVersion!)}/profile/json";
        progress?.Report(new ProgressInfo("下载 Fabric 版本信息", 0, 1, "profile/json"));
        var profileJson = await GetStringWithFriendly404Async(profileUrl,
            $"Fabric 没有 \"Minecraft {mcVersion} + Loader {loaderVersion}\" 这个组合的安装信息。\n" +
            "多半是这个 Loader 版本并不支持所选的 MC 版本。请在「Loader 版本」下拉框里换一个" +
            "（列表现在只会列出确实支持当前 MC 版本的 Loader），或者换一个 MC 版本再试。", ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(profileJson)
            ?? throw new InvalidOperationException("Fabric 返回的版本信息解析失败。");

        // Fabric Meta 返回的 json 里 id 字段形如 "fabric-loader-0.15.11-1.20.1"，直接采用官方给的命名，
        // 与其它主流启动器保持一致，方便用户在多个启动器之间识别是同一个版本。
        // 用户自定义了实例名时优先用用户给的名字（同「导入整合包」一样做合法化 + 去重），
        // 不传就还是原来的默认格式，行为不变。
        var defaultVersionId = string.IsNullOrEmpty(detail.Id) ? $"fabric-loader-{loaderVersion}-{mcVersion}" : detail.Id;
        var versionId = string.IsNullOrWhiteSpace(customInstanceName)
            ? defaultVersionId
            : ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
        var versionDir = Path.Combine(minecraftDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        // 4. 把加载器实例做成"独立实例"：不再靠 inheritsFrom 指向单独共用的原版文件夹，而是把原版
        // client.jar 直接拷贝一份进这个加载器自己的版本文件夹，并去掉 profile json 里的 inheritsFrom
        // 字段。这样每个 Fabric 实例（哪怕对应同一个 MC 版本、不同的 mod 列表）都是完全独立的文件夹，
        // 跟纯净原版、以及其它加载器实例互不影响——删除/改名/单独导出一个 Fabric 实例，都不会波及
        // 原版文件夹或其它加载器实例，符合 PCL2/HMCL 里"每个版本都是独立实例"的直觉。
        // 之前 versionId 文件夹只落一份 json，靠 LauncherService 的 inheritsFrom 继承链去父版本
        // 文件夹里找 jar；现在把 jar 也落一份在本地文件夹，profile json 也顺手去掉 inheritsFrom，
        // 保证即使原版父版本文件夹以后被删掉，这个 Fabric 实例依然能独立启动。
        var parentJarPath = ResolveVersionFile(parentVersionDir, mcVersion, "jar");
        if (parentJarPath != null)
        {
            File.Copy(parentJarPath, Path.Combine(versionDir, $"{versionId}.jar"), overwrite: true);
        }

        // Fabric profile json 本身不带 assetIndex/assets/downloads 字段(它靠 inheritsFrom 指向原版
        // 去继承这些信息)。去掉 inheritsFrom 之后如果不补上，LauncherService 会因为找不到 assetsId
        // 而回退成 "legacy"，导致资源文件目录用错(新版本会找不到材质音效)。这里从原版自己的 json 里
        // 读一份出来，把这三个字段原样搬进 Fabric 自己的 json，保证独立后信息完整、不依赖父版本文件夹。
        var parentJsonPath = ResolveVersionFile(parentVersionDir, mcVersion, "json");
        if (parentJsonPath != null)
        {
            var parentDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
            if (parentDetail != null)
            {
                detail.AssetIndex ??= parentDetail.AssetIndex;
                detail.Assets ??= parentDetail.Assets;
                detail.Downloads ??= parentDetail.Downloads;
                detail.JavaVersion ??= parentDetail.JavaVersion;
            }
        }
        detail.InheritsFrom = null;
        detail.Id = versionId;
        var finalProfileJson = JsonSerializer.Serialize(detail,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
        await File.WriteAllTextAsync(versionJsonPath, finalProfileJson, ct);

        // 5. 补下 libraries：Fabric profile json 里的库全部是 "name+url" 风格（无 downloads 对象），
        // 复用 DownloadService 已经支持这种风格的下载逻辑，不重复实现一遍 Maven 坐标换算。
        progress?.Report(new ProgressInfo("下载 Fabric 加载器库文件", 0, Math.Max(detail.Libraries.Count, 1), versionId));
        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, detail, progress, ct);

        // 5. 可选：Fabric API（很多 Fabric mod 的硬依赖，新手常常不知道要单独装）。
        // 这一步失败不应该让整个 Fabric Loader 安装被判定为失败——loader 本身已经装好、
        // 可以正常启动游戏，Fabric API 只是"锦上添花"的常见依赖，装不上顶多是后续装某些 mod
        // 时提示缺依赖，用户还能再手动装一次，不应该因为这一步网络抖动就让用户以为 Fabric 都没装上。
        if (installFabricApi)
        {
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Fabric_Api", "下载 Fabric API"), 0, 1, FabricApiModrinthSlug));
            try
            {
                var versions = await _modrinth.GetVersionsAsync(FabricApiModrinthSlug, mcVersion, ct);
                // Fabric API 的 Modrinth 版本列表里同时有给 Fabric 用的和给 Quilt 用的构建，
                // 用 loaders 字段过滤，避免装到 Quilt 专用构建（能下载但 Fabric Loader 用不了）。
                var apiVersion = versions.FirstOrDefault(v =>
                    v.Loaders != null && v.Loaders.Any(l => l.Equals("fabric", StringComparison.OrdinalIgnoreCase)))
                    ?? versions.FirstOrDefault();

                if (apiVersion == null)
                {
                    progress?.Report(new ProgressInfo(
                        $"Fabric API 没有找到适配 MC {mcVersion} 的版本，已跳过（不影响 Fabric Loader 本身的安装）",
                        1, 1, FabricApiModrinthSlug));
                }
                else
                {
                    var apiProgress = new Progress<string>(msg =>
                        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Fabric_Api", "下载 Fabric API"), 0, 1, msg)));
                    await _modrinth.DownloadResourceAsync(minecraftDir, ModrinthResourceType.Mod, apiVersion,
                        apiProgress, saveName: null, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 同上：Fabric API 下载失败不应该让整个安装抛异常，只在进度里提示一下，
                // 让用户知道"loader 装好了，但 Fabric API 这一步没成功"，而不是笼统地报"安装失败"。
                progress?.Report(new ProgressInfo(
                    $"Fabric API 下载失败（{ex.Message}），已跳过，不影响 Fabric Loader 本身的安装",
                    1, 1, FabricApiModrinthSlug));
            }
        }

        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 1, 1, versionId));
        return versionId;
    }

    /// <summary>QSL(Quilt Standard Libraries) 在 Modrinth 上的项目 slug，固定值（官方项目，不会变）。</summary>
    private const string QslModrinthSlug = "qsl";

    /// <summary>
    /// 安装 Quilt 客户端到 .minecraft/versions/{versionId}/。跟 InstallFabricClientAsync 的整体
    /// 步骤(装原版父版本 -> 下载官方现成 profile json -> 补库文件)基本一致，只有一处跟 Fabric 不同：
    /// Quilt Meta 的 profile json 端点是 "/versions/loader/{mc}/{loader}/profile/json"，
    /// 路径里不含"installer 版本"这一段——Quilt 的服务端把 installer 版本收敛成官方固定值，
    /// 不需要像 Fabric 那样先单独查一次 installer 列表再拼进 url。
    ///
    /// 可选步骤：QSL(Quilt Standard Libraries) 对应 Fabric API 在 Quilt 生态里的角色，同样发布在
    /// Modrinth 上(slug "qsl")。跟 Fabric API 一样做成可选自动安装项，交给用户在安装界面勾选，
    /// 不强制默认装——QSL 不是所有 Quilt mod 的通用硬依赖(存在纯用 Quilt 特性、完全不依赖 QSL 的
    /// mod)，但对绝大多数 Quilt 生态的模组来说都是常见依赖，跟 Fabric API 的定位一致，所以采用
    /// 同样"可选、默认不勾、失败不影响 Loader 本身安装结果"的处理方式。
    /// </summary>
    /// <param name="installQsl">true 时额外从 Modrinth 下载安装 QSL（可选步骤，失败不影响
    /// Quilt Loader 本身的安装结果——见方法内部注释）。</param>
    /// <param name="customInstanceName">用户自定义的实例名，语义跟 InstallFabricClientAsync 的
    /// 同名参数完全一致（见其上方注释），传空/null 退回默认命名。</param>
    public async Task<string> InstallQuiltClientAsync(string minecraftDir, string mcVersion, string loaderVersion,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, bool installQsl = false,
        string? customInstanceName = null)
    {
        // 1. 确保原版父版本已装好（Quilt 客户端启动同样需要原版 client.jar + assets）
        var parentVersionDir = Path.Combine(minecraftDir, "versions", mcVersion);
        if (!File.Exists(Path.Combine(parentVersionDir, $"{mcVersion}.jar")))
        {
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installing_The_Base_Vanilla_Version", "安装原版父版本"), 0, 1, mcVersion));
            var manifest = await _vanillaDownloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                ?? throw new InvalidOperationException($"在版本清单中找不到 MC 版本 {mcVersion}，无法安装 Quilt 所需的原版父版本。");
            await _vanillaDownloader.InstallVersionAsync(minecraftDir, entry, progress, ct);
        }

        // 1.5 同 InstallFabricClientAsync：校验 loaderVersion 是否真的支持这个 mcVersion，
        //     不支持就自动换成兼容的最高版本，避免下面拼 profile url 时 404。
        loaderVersion = await ResolveLoaderVersionOrAutoMatchAsync(
            QuiltMetaBase, "Quilt", mcVersion, loaderVersion, progress, ct);

        // 2. 下载现成的客户端 profile json（Quilt Meta 直接给完整 json，跟 Fabric 一样不需要
        // 本地跑安装器；路径里没有 installer 版本这一段，见方法上方注释）。
        var profileUrl = $"{QuiltMetaBase}/versions/loader/" +
                         $"{Uri.EscapeDataString(mcVersion)}/" +
                         $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
        progress?.Report(new ProgressInfo("下载 Quilt 版本信息", 0, 1, "profile/json"));
        var profileJson = await GetStringWithFriendly404Async(profileUrl,
            $"Quilt 没有 \"Minecraft {mcVersion} + Loader {loaderVersion}\" 这个组合的安装信息。\n" +
            "请在「Loader 版本」下拉框里换一个，或者换一个 MC 版本再试。", ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(profileJson)
            ?? throw new InvalidOperationException("Quilt 返回的版本信息解析失败。");

        // Quilt Meta 返回的 json 里 id 字段形如 "quilt-loader-0.24.0-1.20.1"，直接采用官方给的命名。
        // 命名优先级同 InstallFabricClientAsync：用户自定义优先，不传退回默认格式。
        var defaultVersionId = string.IsNullOrEmpty(detail.Id) ? $"quilt-loader-{loaderVersion}-{mcVersion}" : detail.Id;
        var versionId = string.IsNullOrWhiteSpace(customInstanceName)
            ? defaultVersionId
            : ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
        var versionDir = Path.Combine(minecraftDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        // 跟 Fabric 一样做成"独立实例"：把原版 client.jar 拷贝进 Quilt 自己的版本文件夹，去掉
        // inheritsFrom，并从原版 json 补齐 assetIndex/assets/downloads/javaVersion 字段——
        // 理由和具体做法见 InstallFabricClientAsync 里的详细注释，这里两边保持一致。
        var parentJarPath = ResolveVersionFile(parentVersionDir, mcVersion, "jar");
        if (parentJarPath != null)
        {
            File.Copy(parentJarPath, Path.Combine(versionDir, $"{versionId}.jar"), overwrite: true);
        }
        var parentJsonPath = ResolveVersionFile(parentVersionDir, mcVersion, "json");
        if (parentJsonPath != null)
        {
            var parentDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(parentJsonPath));
            if (parentDetail != null)
            {
                detail.AssetIndex ??= parentDetail.AssetIndex;
                detail.Assets ??= parentDetail.Assets;
                detail.Downloads ??= parentDetail.Downloads;
                detail.JavaVersion ??= parentDetail.JavaVersion;
            }
        }
        detail.InheritsFrom = null;
        detail.Id = versionId;
        var finalProfileJson = JsonSerializer.Serialize(detail,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
        await File.WriteAllTextAsync(versionJsonPath, finalProfileJson, ct);

        // 3. 补下 libraries：Quilt profile json 里的库同样是"name+url"风格(无 downloads 对象)，
        // 复用跟 Fabric 共用的同一套下载逻辑。
        progress?.Report(new ProgressInfo("下载 Quilt 加载器库文件", 0, Math.Max(detail.Libraries.Count, 1), versionId));
        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, detail, progress, ct);

        // 4. 可选：QSL（Quilt 生态里事实上的"标准库"，很多 Quilt 模组依赖它，跟 Fabric API 一样
        // 常有新手不知道要单独装）。这一步失败不应该让整个 Quilt Loader 安装被判定为失败——
        // loader 本身已经装好、可以正常启动游戏，QSL 只是常见依赖，装不上顶多是后续装某些 mod
        // 时提示缺依赖，用户还能再手动装一次，不应该因为网络抖动就让用户以为 Quilt 都没装上。
        if (installQsl)
        {
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Qsl", "下载 QSL"), 0, 1, QslModrinthSlug));
            try
            {
                var versions = await _modrinth.GetVersionsAsync(QslModrinthSlug, mcVersion, ct);
                // QSL 的 Modrinth 版本列表理论上只发布 Quilt 构建，但保险起见仍按 loaders 字段过滤，
                // 跟 Fabric API 那边的处理方式保持一致，避免装到不兼容的构建。
                var qslVersion = versions.FirstOrDefault(v =>
                    v.Loaders != null && v.Loaders.Any(l => l.Equals("quilt", StringComparison.OrdinalIgnoreCase)))
                    ?? versions.FirstOrDefault();

                if (qslVersion == null)
                {
                    progress?.Report(new ProgressInfo(
                        $"QSL 没有找到适配 MC {mcVersion} 的版本，已跳过（不影响 Quilt Loader 本身的安装）",
                        1, 1, QslModrinthSlug));
                }
                else
                {
                    var qslProgress = new Progress<string>(msg =>
                        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Qsl", "下载 QSL"), 0, 1, msg)));
                    await _modrinth.DownloadResourceAsync(minecraftDir, ModrinthResourceType.Mod, qslVersion,
                        qslProgress, saveName: null, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 同上：QSL 下载失败不应该让整个安装抛异常，只在进度里提示一下，
                // 让用户知道"loader 装好了，但 QSL 这一步没成功"，而不是笼统地报"安装失败"。
                progress?.Report(new ProgressInfo(
                    $"QSL 下载失败（{ex.Message}），已跳过，不影响 Quilt Loader 本身的安装",
                    1, 1, QslModrinthSlug));
            }
        }

        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 1, 1, versionId));
        return versionId;
    }

    /// <summary>
    /// 安装 Forge/NeoForge 客户端：下载官方安装器 jar，本地用指定 Java 跑一次 --installClient。
    /// 与服务端的 RunForgeInstallerAsync 是同一套"起进程等退出码"模式，只是参数不同，
    /// 这里独立实现（而不是直接调用服务端那个方法）是因为服务端版本的参数/异常信息文案是按
    /// "服务端安装"场景写的，混用会让客户端场景下的报错提示文不对题。
    /// </summary>
    /// <param name="customInstanceName">用户自定义的实例名，语义同 InstallFabricClientAsync 的
    /// 同名参数（见其上方注释）。跟 Fabric/Quilt 不同的是：Forge/NeoForge 的目标文件夹名是官方
    /// 安装器自己决定、写死在它内部逻辑里的，我们没法提前告诉安装器"用这个名字"，只能等安装器
    /// 跑完之后，把它生成的文件夹重命名成用户想要的名字（同时把 json/jar 文件名、json 内部 id
    /// 字段都同步改掉，否则 LauncherService 会因为"文件夹名跟 json id 对不上"而找不到主 jar）。</param>
    public async Task<string> InstallForgeOrNeoForgeClientAsync(string minecraftDir, ServerCoreType coreType,
        string fullVersion, string javaExePath, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        string? customInstanceName = null)
    {
        if (coreType is not (ServerCoreType.Forge or ServerCoreType.NeoForge))
            throw new ArgumentException("只支持 Forge/NeoForge。", nameof(coreType));
        if (!File.Exists(javaExePath))
            throw new FileNotFoundException("找不到可用的 Java，无法运行加载器安装器。", javaExePath);

        var mavenBase = coreType == ServerCoreType.Forge ? ForgeMavenBase : NeoForgeMavenBase;
        var prefix = coreType == ServerCoreType.Forge ? "forge" : "neoforge";
        var fileName = $"{prefix}-{fullVersion}-installer.jar";
        var url = $"{mavenBase}/{fullVersion}/{fileName}";

        // Forge/NeoForge 官方安装器是照搬 Mojang 官方启动器的行为写的，它在 --installClient 时会去读
        // <.minecraft>/launcher_profiles.json，如果这个文件不存在就直接报错退出（"There is no Minecraft
        // launcher profile ... you need to run the launcher first!"，退出码 1），即使 .minecraft 目录本身
        // 已经存在、版本也已经装好也不例外——它只认这一个文件存在与否，不检查目录里是否已经有版本。
        // 因为 XCL2 是独立启动器，从来不会自己生成这个 Mojang 专用的档案文件，所以只要是全新的
        // .minecraft 目录（或者是没被官方启动器打开过的目录），装 Forge/NeoForge 必现这个报错。
        // 这里在跑安装器之前主动写一份能满足安装器"文件存在且是合法 JSON"要求的最小占位文件，
        // 不需要跟真实的 Mojang 官方启动器格式完全一致（安装器只检查文件存在性和基本 JSON 结构，
        // 不会真的把这个当启动器状态使用），已存在的文件不覆盖，避免破坏用户可能真正用官方启动器
        // 生成过的档案数据。
        EnsureLauncherProfilesJson(minecraftDir);

        var tempDir = Path.Combine(Path.GetTempPath(), "xcl2-loader-installer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, fileName);

        progress?.Report(new ProgressInfo($"下载 {coreType} 安装器", 0, 2, fileName));
        await DownloadFileNoHashCheckAsync(url, installerPath, ct);

        progress?.Report(new ProgressInfo("正在运行安装器（首次运行可能需要下载额外库文件）", 1, 2, fileName));
        var psi = new ProcessStartInfo
        {
            FileName = javaExePath,
            ArgumentList = { "-jar", installerPath, "--installClient", minecraftDir },
            WorkingDirectory = tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 显式指定 UTF-8：不指定时用的是 Console.OutputEncoding（中文 Windows 下通常是 GBK/936），
            // 而 Forge/NeoForge 安装器（以及它内部调起的 Java 子进程）打印的诊断信息很多是 UTF-8，
            // 编码不一致会导致失败时捕获到的"最后输出"全是乱码，报错信息形同虚设，用户完全看不懂
            // 到底哪里失败了——这是"Forge 安装报错但看不出原因"的一个常见诱因，跟安装器本身是否
            // 真的失败无关，是纯粹的输出编码问题。
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var outputLines = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputLines.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) outputLines.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 安装器进程超时保护：Forge/NeoForge 安装器本身会再联网下载一批 library（不是本类
        // 之前已经下载好的 installer jar 本身，是安装器运行时自己另外拉取的依赖），网络卡住时
        // 之前的代码会无限等待 WaitForExitAsync，UI 侧表现为进度条停在"正在运行安装器"不再变化，
        // 用户既不知道是卡住了还是真的在跑、也没有任何机会中止，只能强杀整个启动器进程。
        // 这里给一个宽松但有限的超时（10 分钟，安装器本身网络下载可能比较慢，尤其国内直连
        // Forge/NeoForge 官方源较慢是已知情况，不能设得太短），超时后主动杀掉子进程并给出
        // 明确提示，而不是让调用方永远等不到结果。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        bool timedOut;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            timedOut = false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
        }

        if (timedOut)
        {
            throw new InvalidOperationException(
                $"{coreType} 客户端安装器运行超过 10 分钟仍未完成，已中止。这通常是因为安装器自己\n" +
                "另外联网下载依赖库时网络太慢或被墙——可以尝试更换网络环境（如使用代理）后重试，\n" +
                "或者检查本地防火墙/杀毒软件是否拦截了 Java 的联网请求。");
        }

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', outputLines.TakeLast(20));
            var fullOutput = string.Join('\n', outputLines);
            var hint = DiagnoseForgeInstallerFailure(fullOutput);
            throw new InvalidOperationException(
                $"{coreType} 客户端安装器执行失败（退出码 {process.ExitCode}）。{hint}最后输出：\n{tail}");
        }

        try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响安装已经完成这个事实 */ }

        // 修复"Forge 装完之后启动报 Module ... log4j not found"：安装器 --installClient 这一步
        // 自己内部还会再联网下载一批库文件（见上面第 638 行注释），这部分下载完全在安装器自己的
        // 进程里发生，走的是它内置的下载逻辑，既不经过我们的 DownloadService，也就享受不到
        // DownloadEndpoints 的镜像回退——如果这批库里恰好有一两个文件（哪怕只是 log4j-core 这种
        // 基础库）因为网络问题下载失败/下到损坏文件，安装器有时仍然会以退出码 0 结束（比如日志里
        // 只是打印了一条 WARN 而不是真的 FAIL），我们只看退出码的话完全感知不到这个"看起来装完了，
        // 其实缺库"的情况，直到用户真正启动游戏、JPMS 在解析模块时才报错暴露出来。
        //
        // 这里在安装器退出码为 0、成功定位到生成的版本目录之后，主动读一遍它生成的 version json，
        // 用我们自己的 DownloadLibrariesOnlyAsync 把 json 里列出的所有 libraries 重新过一遍——
        // 该方法内部对每个文件都会先做 sha1 校验，已经存在且校验通过的文件直接跳过、不会重新下载，
        // 只有真正缺失/损坏的文件才会补下，且现在已经具备镜像↔官方自动回退能力（见 DownloadService
        // 里的改动），相当于给安装器自己下载的这批库文件做一次"体检+补漏"，从根源上避免缺库启动失败。
        //
        // 安装器会自己在 versions/ 下生成形如 "{mcVersion}-{prefix}-{loaderVersion}" 的版本目录，
        // 这里在 versions/ 下找一个最近创建、名字包含加载器前缀的目录作为安装结果返回给调用方，
        // 不同 Forge/NeoForge 版本生成的确切目录命名格式有细微差异，用"最近修改时间"比精确拼字符串更稳妥。
        var versionsDir = Path.Combine(minecraftDir, "versions");
        var candidate = Directory.Exists(versionsDir)
            ? Directory.GetDirectories(versionsDir)
                .Where(d => Path.GetFileName(d).Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (candidate != null)
        {
            try
            {
                var repairVersionId = Path.GetFileName(candidate);
                var repairJsonPath = ResolveVersionFile(candidate, repairVersionId, "json");
                if (repairJsonPath != null)
                {
                    var repairDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(repairJsonPath));
                    if (repairDetail != null)
                    {
                        repairDetail.Id = repairVersionId;
                        progress?.Report(new ProgressInfo("校验并补全依赖库", 1, 2, fileName));
                        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, repairDetail, progress, ct);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 补库本身失败（比如两个源都连不上）不应该让整个 Forge/NeoForge 安装被判定为失败——
                // 安装器至少已经跑完了，能不能启动最终由用户实际点"启动游戏"来验证，这里只是
                // "尽力而为"的一次额外体检，不是安装成功与否的判定依据。
            }
        }

        // Forge/NeoForge 官方安装器生成的 version json 默认也是靠 inheritsFrom 指向原版文件夹
        // （跟 Fabric/Quilt 改造前的行为一样），同一份原版 jar 被多个加载器实例共用。这里同样把它
        // 改造成"独立实例"：把原版 client.jar 拷贝进安装器生成的这个文件夹，去掉 json 里的
        // inheritsFrom，并从原版 json 补齐 assetIndex/assets/downloads/javaVersion 字段——
        // 具体理由跟 InstallFabricClientAsync 里的注释一致，这里三种加载器统一处理方式。
        if (candidate != null)
        {
            try
            {
                var loaderVersionId = Path.GetFileName(candidate);
                var loaderJsonPath = ResolveVersionFile(candidate, loaderVersionId, "json");
                if (loaderJsonPath != null)
                {
                    var loaderDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(loaderJsonPath));
                    if (loaderDetail != null && !string.IsNullOrEmpty(loaderDetail.InheritsFrom))
                    {
                        var vanillaId = loaderDetail.InheritsFrom;
                        var vanillaDir = Path.Combine(minecraftDir, "versions", vanillaId);
                        var vanillaJarPath = ResolveVersionFile(vanillaDir, vanillaId, "jar");
                        if (vanillaJarPath != null)
                        {
                            File.Copy(vanillaJarPath, Path.Combine(candidate, $"{loaderVersionId}.jar"), overwrite: true);
                        }
                        var vanillaJsonPath = ResolveVersionFile(vanillaDir, vanillaId, "json");
                        if (vanillaJsonPath != null)
                        {
                            var vanillaDetail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(vanillaJsonPath));
                            if (vanillaDetail != null)
                            {
                                loaderDetail.AssetIndex ??= vanillaDetail.AssetIndex;
                                loaderDetail.Assets ??= vanillaDetail.Assets;
                                loaderDetail.Downloads ??= vanillaDetail.Downloads;
                                loaderDetail.JavaVersion ??= vanillaDetail.JavaVersion;
                            }
                        }
                        loaderDetail.InheritsFrom = null;
                        loaderDetail.Id = loaderVersionId;
                        var finalJson = JsonSerializer.Serialize(loaderDetail,
                            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                        await File.WriteAllTextAsync(loaderJsonPath, finalJson, ct);
                    }
                }
            }
            catch
            {
                // 独立实例化失败(比如原版 jar 意外找不到)不应该让整个 Forge/NeoForge 安装被判定为失败——
                // 安装器本身已经成功产出了一个能用 inheritsFrom 正常启动的版本，独立化只是锦上添花，
                // 失败了大不了退回旧的"共用原版文件夹"行为，游戏依然能正常启动。
            }
        }

        // 用户自定义了实例名：把安装器自己生成的文件夹重命名成用户想要的名字。放在"独立实例化"
        // （拷贝原版 jar、去掉 inheritsFrom）之后做，理由是重命名只是"换个文件夹名+同步 json 里的
        // id/文件名"，不影响上面已经完成的独立化处理，顺序对调也没问题，但放在后面逻辑更直观
        // （先把内容做对，再决定叫什么名字）。
        if (candidate != null && !string.IsNullOrWhiteSpace(customInstanceName))
        {
            var renamed = TryRenameInstalledInstance(minecraftDir, candidate, customInstanceName!);
            if (renamed != null) return renamed;
            // 重命名失败（比如目标名冲突、文件被占用）不应该让整个安装被判定为失败——
            // 加载器本身已经装好、能用默认生成的名字正常启动，重命名只是锦上添花，
            // 大不了退回默认名字，游戏依然能用。
        }

        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 2, 2, fileName));
        return candidate != null ? Path.GetFileName(candidate) : fullVersion;
    }

    /// <summary>
    /// 把一个已安装版本的文件夹重命名成用户自定义的实例名（供 Forge/NeoForge 客户端安装、以及
    /// 原版直装两处复用——原版没有独立的"加载器安装"步骤能提前指定目标目录名，只能装完之后
    /// 原地改名，跟 Forge/NeoForge 官方安装器自己决定文件夹名、装完再改名是同一种局面）：
    /// 1) 用 MakeUniqueInstanceName 规整 + 去重得到目标文件夹名；
    /// 2) 物理重命名整个版本目录；
    /// 3) 目录内跟旧文件夹名同名的 .json/.jar（LauncherService 靠"文件名跟版本 id 一致"
    ///    去定位主 jar，见 ResolveVersionFile 的改名容错查找逻辑）同步改名成新文件夹名；
    /// 4) json 内部的 "id" 字段同步改成新名字，保持文件名和 json 内容一致。
    /// 任何一步失败都直接返回 null，交给调用方回退到默认名字，不让重命名失败拖累整个安装结果。
    /// </summary>
    public static string? TryRenameInstalledInstance(string minecraftDir, string oldDir, string customInstanceName)
    {
        try
        {
            var oldName = Path.GetFileName(oldDir);
            var versionsDir = Path.Combine(minecraftDir, "versions");
            var newName = ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
            if (string.Equals(newName, oldName, StringComparison.Ordinal)) return oldName; // 没有实际变化

            var newDir = Path.Combine(versionsDir, newName);
            Directory.Move(oldDir, newDir);

            var oldJsonPath = Path.Combine(newDir, $"{oldName}.json");
            var oldJarPath = Path.Combine(newDir, $"{oldName}.jar");
            var newJsonPath = Path.Combine(newDir, $"{newName}.json");
            var newJarPath = Path.Combine(newDir, $"{newName}.jar");

            if (File.Exists(oldJsonPath))
            {
                var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(oldJsonPath));
                if (detail != null)
                {
                    detail.Id = newName;
                    var updatedJson = JsonSerializer.Serialize(detail,
                        new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                    File.WriteAllText(oldJsonPath, updatedJson);
                }
                File.Move(oldJsonPath, newJsonPath, overwrite: true);
            }
            if (File.Exists(oldJarPath))
                File.Move(oldJarPath, newJarPath, overwrite: true);

            return newName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 根据安装器完整输出里的关键词，识别几种社区里最常见、有明确解决办法的 Forge/NeoForge
    /// 安装失败原因，返回一句可直接指导用户下一步该做什么的中文提示（识别不到就返回空字符串，
    /// 调用方仍然会展示原始输出尾巴，不会因为诊断失败而丢失信息）。
    ///
    /// 这几类是实际使用中反复出现、且原始安装器报错信息对普通用户很不友好的典型情况：
    /// - Java 版本不匹配（安装器要求特定 Java 版本运行，用户本地默认 Java 版本不满足）；
    /// - 网络下载失败/校验和不匹配（安装器自己联网下载 library 时失败，常见于国内直连不稳定）；
    /// - 磁盘空间不足；
    /// - 目标目录没有写入权限（常见于装在 Program Files 下、没有管理员权限运行的情况）。
    /// </summary>
    private static string DiagnoseForgeInstallerFailure(string output)
    {
        if (string.IsNullOrEmpty(output)) return "";

        // UnsupportedClassVersionError / "class file version" 是"用错 Java 主版本号跑安装器"的
        // 典型异常类型，比如用 Java 8 跑一个要求 Java 17+ 才能运行的新版安装器。
        if (output.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("class file version", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：这通常是本地选用的 Java 版本太旧，安装器本身需要更高版本的 Java 才能运行。" +
                "请在「安装新版本」弹窗里点「自动检测」重新匹配，或去「设置」页下载一个更新的 Java 版本后重试。\n";
        }

        if (output.Contains("NoSuchAlgorithmException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("SSLHandshakeException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ConnectException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("UnknownHostException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Downloading library", StringComparison.OrdinalIgnoreCase) && output.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：安装器在自己联网下载所需的库文件时失败了，通常是网络问题（国内直连 Forge/NeoForge\n" +
                "官方源经常不稳定）。可以尝试更换网络环境（如使用代理）后重试，多试几次也可能成功。\n";
        }

        if (output.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：磁盘空间不足，请清理出至少 1GB 可用空间后重试。\n";
        }

        if (output.Contains("AccessDeniedException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：没有写入权限，通常是因为 .minecraft 文件夹装在 C:\\Program Files 等需要管理员\n" +
                "权限才能写入的目录下。建议把游戏文件夹换到不需要额外权限的位置（如 D:\\Games\\.minecraft），\n" +
                "或者以管理员身份运行本启动器后重试。\n";
        }

        return "";
    }

    /// <summary>
    /// 确保 minecraftDir 下存在一份 launcher_profiles.json，满足 Forge/NeoForge 官方安装器
    /// --installClient 的前置检查。已存在时不覆盖（不破坏可能存在的真实数据）。
    /// 格式对齐官方启动器实际写出的最小结构（profiles/settings/version 三个顶层字段），
    /// 安装器只做"文件存在 + JSON 可解析"检查，不校验具体字段内容，这里给的是能通过校验的最小合法值。
    /// </summary>
    private static void EnsureLauncherProfilesJson(string minecraftDir)
    {
        Directory.CreateDirectory(minecraftDir);
        var path = Path.Combine(minecraftDir, "launcher_profiles.json");
        if (File.Exists(path)) return;

        const string minimalProfiles = """
        {
          "profiles": {},
          "settings": {
            "crashAssistance": true,
            "enableAdvanced": false,
            "enableAnalytics": true,
            "enableHistorical": false,
            "enableReleases": true,
            "enableSnapshots": false,
            "keepLauncherOpen": false,
            "profileSorting": "ByLastPlayed",
            "showGameLog": false,
            "showMenu": false,
            "soundOn": false
          },
          "version": 3
        }
        """;

        File.WriteAllText(path, minimalProfiles);
    }

    /// <summary>单次下载尝试超时，理由同 DownloadService.SingleAttemptTimeout：避免假死连接
    /// 拖到 _http 整体的 15 分钟超时才失败——同类"下载卡住"问题，这里跟 DownloadService 一起修。</summary>
    private static readonly TimeSpan SingleAttemptTimeout = TimeSpan.FromSeconds(45);

    private async Task DownloadFileNoHashCheckAsync(string url, string destPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        const int maxAttempts = 3;
        Exception? lastError = null;

        // 修复：Forge/NeoForge 安装器 jar 本身之前永远只请求 Maven 官方地址（国内直连经常很慢/超时），
        // 完全没有走 DownloadEndpoints 的镜像候选池——即使用户在设置里选择了"使用镜像源"，这里也
        // 感知不到、依旧死磕官方地址。现在改成跟 DownloadService.DownloadFileAsync 一致的策略：
        // 按用户偏好把候选 URL 排好序，每个候选源都给足 maxAttempts 次机会，前一个源多次失败再换下一个。
        var candidates = DownloadEndpoints.Candidates(url, _preferMirror);

        foreach (var candidateUrl in candidates)
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var tmp = destPath + $".tmp{attempt}";
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(SingleAttemptTimeout);
                try
                {
                    using (var resp = await _http.GetAsync(candidateUrl, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var fs = File.Create(tmp);
                        await resp.Content.CopyToAsync(fs, attemptCts.Token);
                    }
                    if (new FileInfo(tmp).Length == 0)
                    {
                        lastError = new IOException($"下载得到空文件: {candidateUrl}");
                        TryDelete(tmp);
                        continue;
                    }
                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(tmp, destPath);
                    DownloadEndpoints.ReportSuccess(candidateUrl);
                    return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = new TimeoutException(
                        $"下载单次尝试超时（{SingleAttemptTimeout.TotalSeconds:0}秒内无响应）: {candidateUrl}");
                    TryDelete(tmp);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    TryDelete(tmp);
                }
            }
            DownloadEndpoints.ReportFailure(candidateUrl);
        }
        // 友好化报错：如果最后一次失败是 404（安装器文件在 Maven 上找不到，常见于这个具体版本号
        // 已经被下架/移除），额外提示"换一个版本试试"，而不是只甩一个裸的 URL 让用户自己猜原因。
        var is404 = lastError is HttpRequestException hre &&
            (hre.StatusCode == System.Net.HttpStatusCode.NotFound || hre.Message.Contains("404"));
        var hint = is404
            ? "\n这通常是因为该版本的安装器已从官方仓库下架，建议在版本列表里换一个相近的版本重试。"
            : "";
        throw new IOException($"下载失败（已尝试所有可用源）: {url}{hint}", lastError);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略清理失败 */ }
    }

    // ========================================================================
    // 原地增删/升降级/重装加载器
    //
    // 需求：对一个已经存在、里面已经有 mods/saves/resourcepacks/config 等用户数据的实例，
    // 更换加载器类型、升级/降级加载器版本、或者重装同一个加载器版本，都不应该破坏这些用户
    // 数据文件夹——用户要保留的是"这个实例"，只是想换/修/重装它的加载器部分。
    //
    // 实现思路：复用上面已经验证过的 Install*ClientAsync（它们各自都有一套完整的、已经趟过坑的
    // 下载/安装逻辑），但不直接装进目标实例文件夹，而是先装进一个全新的临时版本文件夹，
    // 装完之后只把"加载器定义相关的文件"（顶层 version json + 客户端 jar，以及 libraries 等
    // 非用户数据子目录）合并覆盖进目标实例文件夹，mods/saves/resourcepacks/config/screenshots/
    // crash-reports/logs 这些认定为"用户数据"的子目录完全跳过、绝不触碰。
    // 操作开始前先用 InstanceBackupService 把目标实例整个打包备份一份，即使合并逻辑本身有意外，
    // 用户也能一键找回操作前的完整状态。
    // ========================================================================

    private static readonly HashSet<string> UserDataDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "resourcepacks", "shaderpacks", "saves", "config",
        "screenshots", "schematics", "crash-reports", "logs", "kubejs", "defaultconfigs"
    };

    /// <summary>
    /// 把 sourceDir（临时新装好的加载器文件夹）合并进 targetDir（用户的目标实例文件夹）：
    /// - 顶层的 *.json / *.jar（版本定义文件、客户端 jar）先清空旧的再拷贝新的，避免目录里
    ///   同时残留新旧两份 json 导致 FolderService"唯一 json 兜底识别"失效。
    /// - mods/saves/resourcepacks 等用户数据子目录整个跳过，不管新装的临时文件夹里有没有同名目录，
    ///   一律不覆盖、不合并、不删除目标目录里已有的内容。
    /// - 其余子目录（如 libraries/、assets 相关文件）按文件递归覆盖合并，只增不减
    ///   （新装文件夹里没有的文件不会被删除，避免误删目标实例里其它无关文件）。
    /// </summary>
    private static void MergeLoaderFilesPreservingUserData(string sourceDir, string targetDir, bool isTopLevel = true)
    {
        Directory.CreateDirectory(targetDir);

        if (isTopLevel)
        {
            foreach (var old in Directory.GetFiles(targetDir, "*.json").Concat(Directory.GetFiles(targetDir, "*.jar")))
            {
                try { File.Delete(old); } catch { /* 尽力而为，单个文件删不掉不阻断整个流程 */ }
            }
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            if (isTopLevel && UserDataDirNames.Contains(name)) continue;
            MergeLoaderFilesPreservingUserData(dir, Path.Combine(targetDir, name), isTopLevel: false);
        }
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>
    /// 原地增删/升降级/重装加载器的统一入口。
    /// installNewLoaderAsync 负责"装出一个全新版本文件夹"（直接传现成的 InstallFabricClientAsync
    /// 等方法即可，不需要调用方自己关心命名），本方法负责：备份 → 调用它 → 合并进目标实例 →
    /// 清理临时文件夹这一整套流程，并保证任何一步失败都不会残留半成品覆盖掉原实例
    /// （合并动作是最后一步，且备份已经在最前面完成）。
    /// </summary>
    public async Task<string> ChangeLoaderInPlaceAsync(
        string minecraftDir, string existingVersionId,
        Func<CancellationToken, Task<string>> installNewLoaderAsync,
        string backupReason,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var existingDir = Path.Combine(minecraftDir, "versions", existingVersionId);
        string? backupZipPath = null;

        if (Directory.Exists(existingDir))
        {
            progress?.Report(new ProgressInfo(
                Loc.T("Str_Cs_Backing_Up_Instance", "正在备份当前实例（操作前自动备份）"), 0, 1, existingVersionId));
            backupZipPath = await InstanceBackupService.CreateBackupAsync(minecraftDir, existingVersionId, backupReason, ct: ct);
        }

        var newVersionId = await installNewLoaderAsync(ct);
        var newDir = Path.Combine(minecraftDir, "versions", newVersionId);

        try
        {
            progress?.Report(new ProgressInfo(
                Loc.T("Str_Cs_Merging_Loader_Files", "正在把新加载器合并进当前实例（不影响存档/模组/资源包）"),
                0, 1, existingVersionId));
            MergeLoaderFilesPreservingUserData(newDir, existingDir);
        }
        finally
        {
            // 临时文件夹已经合并完成，不再需要；就算合并中途抛异常，也尝试清理掉，
            // 避免版本列表里多出一个不明所以、内容残缺的临时文件夹。
            try { if (Directory.Exists(newDir)) Directory.Delete(newDir, recursive: true); } catch { /* 忽略清理失败 */ }
        }

        // 走到这里说明合并没有抛异常，视为操作成功：弹出左下角"是否删除本次备份"通知，
        // 明确建议先启动验证再删——见 InstanceBackupService.NotifyBackupCreated 的注释。
        if (backupZipPath != null)
            InstanceBackupService.NotifyBackupCreated(backupZipPath, DescribeBackupReason(backupReason));

        return existingVersionId;
    }

    private static string DescribeBackupReason(string backupReason) => backupReason switch
    {
        "fabric_change" => "Fabric 加载器版本转换",
        "quilt_change" => "Quilt 加载器版本转换",
        "forge_change" => "Forge 加载器版本转换",
        "neoforge_change" => "NeoForge 加载器版本转换",
        _ => "加载器操作",
    };

    /// <summary>原地更换/升级/降级/重装 Fabric（同一个 loaderVersion 传当前版本号即为"重装"）。</summary>
    public Task<string> ChangeFabricLoaderInPlaceAsync(string minecraftDir, string existingVersionId,
        string mcVersion, string loaderVersion, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        bool installFabricApi = false)
        => ChangeLoaderInPlaceAsync(minecraftDir, existingVersionId,
            innerCt => InstallFabricClientAsync(minecraftDir, mcVersion, loaderVersion, progress, innerCt, installFabricApi),
            "fabric_change", progress, ct);

    /// <summary>原地更换/升级/降级/重装 Quilt。</summary>
    public Task<string> ChangeQuiltLoaderInPlaceAsync(string minecraftDir, string existingVersionId,
        string mcVersion, string loaderVersion, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        bool installQsl = false)
        => ChangeLoaderInPlaceAsync(minecraftDir, existingVersionId,
            innerCt => InstallQuiltClientAsync(minecraftDir, mcVersion, loaderVersion, progress, innerCt, installQsl),
            "quilt_change", progress, ct);

    /// <summary>原地更换/升级/降级/重装 Forge 或 NeoForge。</summary>
    public Task<string> ChangeForgeOrNeoForgeLoaderInPlaceAsync(string minecraftDir, string existingVersionId,
        ServerCoreType coreType, string fullVersion, string javaExePath,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        => ChangeLoaderInPlaceAsync(minecraftDir, existingVersionId,
            innerCt => InstallForgeOrNeoForgeClientAsync(minecraftDir, coreType, fullVersion, javaExePath, progress, innerCt),
            coreType == ServerCoreType.Forge ? "forge_change" : "neoforge_change", progress, ct);

    /// <summary>释放内部 _vanillaDownloader（进而释放它可能持有的智能限速后台采样任务）。</summary>
    public void Dispose() => _vanillaDownloader.Dispose();
}
