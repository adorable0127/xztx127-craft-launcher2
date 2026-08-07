using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 接入 Modrinth API（api.modrinth.com/v2），负责社区资源（材质包/数据包/光影包）的搜索与下载安装。
/// Modrinth 免费、无需申请 API Key，但官方要求所有请求带一个能唯一标识调用方的 User-Agent。
/// </summary>
public class ModrinthService
{
    private const string BaseUrl = "https://api.modrinth.com/v2";
    private readonly HttpClient _http;

    public ModrinthService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // Modrinth 官方要求：必须带一个能唯一标识“谁在调用”的 User-Agent，
        // 格式建议 "作者/项目名 (联系方式)"，不能用默认的 .NET HttpClient UA（会被限流/拒绝）。
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2-Launcher", "1.0"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/xztx127-craft/xcl2)"));
    }

    /// <summary>
    /// 搜索指定分类的资源。
    /// gameVersion 可为空（不限定游戏版本）；query 可为空（浏览热门/最新，不搜关键词）。
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(ModrinthResourceType type, string query,
        string? gameVersion, int offset = 0, int limit = 20, CancellationToken ct = default, string? modLoader = null)
    {
        var projectType = (type == ModrinthResourceType.DataPack || type == ModrinthResourceType.Mod)
            ? "mod" : ToProjectTypeString(type);
        // Plugin 在 Modrinth 是独立的 project_type("plugin")，跟 Mod 的 "mod" 不是一回事——
        // 服务端插件(Bukkit/Spigot/Paper 生态)跟客户端 Mod(Fabric/Forge 生态)完全不通用，
        // 不能像 DataPack 那样借用 "mod" 这个 project_type。

        var facetGroups = new List<string> { $"[\"project_type:{projectType}\"]" };
        if (type == ModrinthResourceType.DataPack)
            facetGroups.Add("[\"categories:datapack\"]");
        // Mod 是加载器相关的（Fabric/Forge/NeoForge/Quilt 各自的 jar 互不通用），
        // 传了加载器就按 categories facet 过滤，避免用户装错加载器版本的 mod 导致游戏打不开。
        if (type == ModrinthResourceType.Mod && !string.IsNullOrWhiteSpace(modLoader))
            facetGroups.Add($"[\"categories:{modLoader.ToLowerInvariant()}\"]");
        // 插件同理可按服务端核心类型(paper/spigot/purpur/bukkit/folia)过滤。
        if (type == ModrinthResourceType.Plugin && !string.IsNullOrWhiteSpace(modLoader))
            facetGroups.Add($"[\"categories:{modLoader.ToLowerInvariant()}\"]");
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facetGroups.Add($"[\"versions:{gameVersion}\"]");

        var facets = Uri.EscapeDataString("[" + string.Join(",", facetGroups) + "]");
        var q = Uri.EscapeDataString(query ?? "");
        var url = $"{BaseUrl}/search?query={q}&facets={facets}&offset={offset}&limit={limit}&index=relevance";

        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ModrinthSearchResult>(json) ?? new ModrinthSearchResult();
    }

    /// <summary>
    /// 获取一个项目下所有可下载版本（按游戏版本可选过滤），Modrinth 默认按发布时间倒序返回。
    ///
    /// 之前这里拼 URL 时只对 gameVersion 内容本身做了 Uri.EscapeDataString，
    /// 但用来包裹它的方括号 [ ] 和双引号 " 完全没有转义就直接拼进了查询字符串——
    /// 这几个字符在 URL 里属于需要转义的保留/非法字符，不同网络环境(尤其是经过某些
    /// 代理/网关转发时)可能因此拒绝请求或返回 404，表现为"获取版本信息失败"。
    /// 现在改为把整个 game_versions 参数值(含方括号和引号)作为一个整体转义。
    /// </summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, string? gameVersion,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/project/{projectId}/version";
        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += $"?game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}";

        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ModrinthVersion>>(json) ?? new List<ModrinthVersion>();
    }

    /// <summary>
    /// 下载一个版本的主文件（primary=true 的那个；没有标记 primary 的就取第一个）。
    ///
    /// 材质包 -> &lt;minecraftDir&gt;/resourcepacks/，光影包 -> &lt;minecraftDir&gt;/shaderpacks/，
    /// 这两类是全局生效的，路径固定。
    ///
    /// 数据包比较特殊：原版 Minecraft 只认"某个存档目录下的 datapacks 文件夹"
    /// (&lt;minecraftDir&gt;/saves/&lt;存档名&gt;/datapacks/)，不存在全局生效的数据包目录——
    /// 放进 .minecraft/datapacks/ 这种路径游戏根本不会加载，等于下载了但完全没用。
    /// 所以数据包必须传入 saveName（具体存档名）才能下载；不传就直接报错，好过悄悄放到一个不生效的位置
    /// 让用户以为装上了。
    ///
    /// 下载完成后校验 SHA1（若 Modrinth 返回了 hash），避免和游戏本体下载一样出现"看似下载成功实际是坏文件"的问题。
    /// </summary>
    public async Task<string> DownloadResourceAsync(string minecraftDir, ModrinthResourceType type,
        ModrinthVersion version, IProgress<string>? progress, string? saveName = null, CancellationToken ct = default,
        bool appendCategorySubdir = true)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        if (file == null) throw new InvalidOperationException("这个版本没有可下载的文件。");

        // 整合包不走这条"下载单文件丢进固定目录"的通用路径：.mrpack 本身只是一份清单
        // （modrinth.index.json + overrides/），不是能直接使用的单个文件，必须解析清单、
        // 逐个下载里面列出的 mod、再展开 overrides——这条逻辑已经在 ModpackService.ImportMrpackAsync
        // 里实现，调用方（DownloadCenterPage 整合包下载按钮）应该直接调那个方法，不应该走到这里。
        if (type == ModrinthResourceType.Modpack)
            throw new InvalidOperationException("整合包不支持这种下载方式，请使用整合包专用的安装流程。");

        string destDir;
        if (!appendCategorySubdir)
        {
            // 调用方已经给了最终落点（比如用户在资源管理器里亲手选的目录）：文件直接存进
            // 这个目录，不再拼接 mods/saves/resourcepacks 等分类子目录——选哪里存哪里，所见即所得。
            destDir = minecraftDir;
        }
        else if (type == ModrinthResourceType.DataPack)
        {
            if (string.IsNullOrWhiteSpace(saveName))
                throw new InvalidOperationException("数据包必须安装到具体的存档里，请先选择一个存档。");
            destDir = Path.Combine(minecraftDir, "saves", saveName, "datapacks");
        }
        else
        {
            var subDir = type switch
            {
                ModrinthResourceType.ResourcePack => "resourcepacks",
                ModrinthResourceType.Shader => "shaderpacks",
                ModrinthResourceType.Mod => "mods",
                // minecraftDir 参数在插件场景下调用方传入的其实是"服务器实例目录"。
                ModrinthResourceType.Plugin => "plugins",
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
            destDir = Path.Combine(minecraftDir, subDir);
        }

        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, file.Filename);

        progress?.Report($"下载 {file.Filename} ...");

        const int maxAttempts = 3;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var tmp = destPath + $".tmp{attempt}";
            try
            {
                using (var resp = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs, ct);
                }

                if (!string.IsNullOrEmpty(file.Hashes?.Sha1) && !VerifySha1(tmp, file.Hashes.Sha1))
                {
                    lastError = new IOException("文件校验失败(SHA1 不匹配)，可能下载不完整。");
                    TryDelete(tmp);
                    continue;
                }

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                progress?.Report("下载完成");
                return destPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                TryDelete(tmp);
            }
        }

        throw new IOException($"下载失败，已重试 {maxAttempts} 次: {file.Filename}", lastError);
    }

    private static string ToProjectTypeString(ModrinthResourceType type) => type switch
    {
        ModrinthResourceType.ResourcePack => "resourcepack",
        ModrinthResourceType.Shader => "shader",
        ModrinthResourceType.DataPack => "mod",
        ModrinthResourceType.Mod => "mod",
        ModrinthResourceType.Plugin => "plugin",
        ModrinthResourceType.Modpack => "modpack",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 忽略清理失败 */ }
    }

    private static bool VerifySha1(string path, string expectedSha1)
    {
        try
        {
            using var sha1 = SHA1.Create();
            using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(sha1.ComputeHash(fs)).ToLowerInvariant();
            return hash == expectedSha1.ToLowerInvariant();
        }
        catch { return false; }
    }
}
