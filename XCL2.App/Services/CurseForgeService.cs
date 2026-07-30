using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 接入 CurseForge API (api.curseforge.com)，目前只做"地图/存档"这一类下载——
/// Modrinth 没有对应的 project_type，这里补上。
///
/// 跟 ModrinthService 不同，CurseForge 强制要求带 API Key（见 CurseForgeKeyService），
/// 没有 key 打不了任何请求。所有对外方法在发请求前先检查 key，没配置就抛
/// CurseForgeKeyMissingException，上层 UI 捕获后引导用户去设置页粘贴 key，
/// 而不是让用户看到一个语焉不详的网络错误。
/// </summary>
public class CurseForgeService
{
    private const string BaseUrl = "https://api.curseforge.com/v1";
    private readonly HttpClient _http;
    private readonly CurseForgeKeyService _keyService;

    public CurseForgeService(CurseForgeKeyService keyService)
    {
        _keyService = keyService;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>每次请求前都重新读一次 key（用户可能刚在设置页改过，不需要重启程序才生效）。</summary>
    private string RequireKey()
    {
        var key = _keyService.TryGetKey();
        if (string.IsNullOrEmpty(key))
            throw new CurseForgeKeyMissingException();
        return key;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("x-api-key", RequireKey());
        return req;
    }

    /// <summary>搜索地图/存档。query 可为空（浏览热门）；gameVersion 可为空（不限定版本）。</summary>
    public Task<CurseForgeSearchResult> SearchMapsAsync(string query, string? gameVersion,
        int index = 0, int pageSize = 20, CancellationToken ct = default)
        => SearchByClassAsync(CurseForgeConstants.WorldsClassId, query, gameVersion, null, index, pageSize, ct);

    /// <summary>搜索 Mod。query 可为空（浏览热门）；gameVersion/modLoader 可为空（不限定）。</summary>
    public Task<CurseForgeSearchResult> SearchModsAsync(string query, string? gameVersion,
        string? modLoader = null, int index = 0, int pageSize = 20, CancellationToken ct = default)
        => SearchByClassAsync(CurseForgeConstants.ModsClassId, query, gameVersion, modLoader, index, pageSize, ct);

    /// <summary>
    /// 搜索材质包/光影包/数据包。之前"下载中心"这三个分类只接了 Modrinth 一条来源，
    /// CurseForgeService 里完全没有对应方法——这里补上，跟 SearchMapsAsync/SearchModsAsync
    /// 走同一套底层 SearchByClassAsync，只是按 CurseForgeResourceKind 换一个 classId。
    /// </summary>
    public Task<CurseForgeSearchResult> SearchResourcesAsync(CurseForgeResourceKind kind, string query,
        string? gameVersion, int index = 0, int pageSize = 20, CancellationToken ct = default)
        => SearchByClassAsync(ResolveClassId(kind), query, gameVersion, null, index, pageSize, ct);

    private static int ResolveClassId(CurseForgeResourceKind kind) => kind switch
    {
        CurseForgeResourceKind.ResourcePack => CurseForgeConstants.ResourcePacksClassId,
        CurseForgeResourceKind.Shader => CurseForgeConstants.ShaderPacksClassId,
        CurseForgeResourceKind.DataPack => CurseForgeConstants.DataPacksClassId,
        CurseForgeResourceKind.Plugin => CurseForgeConstants.BukkitPluginsClassId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// 底层通用搜索，按 classId 区分地图(17)/Mod(6)等类别。
    /// modLoaderType 只在搜 Mod 时有意义：CurseForge modLoaderType 编号 1=Forge 2=Cauldron 3=LiteLoader
    /// 4=Fabric 5=Quilt（官方 v1 文档定义），不认识的字符串就不传，交给用户自己在结果里筛选版本。
    /// </summary>
    private async Task<CurseForgeSearchResult> SearchByClassAsync(int classId, string query, string? gameVersion,
        string? modLoader, int index, int pageSize, CancellationToken ct)
    {
        var url = $"{BaseUrl}/mods/search?gameId={CurseForgeConstants.MinecraftGameId}" +
                   $"&classId={classId}" +
                   $"&index={index}&pageSize={pageSize}&sortField=2&sortOrder=desc";

        if (!string.IsNullOrWhiteSpace(query)) url += $"&searchFilter={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

        if (classId == CurseForgeConstants.ModsClassId && !string.IsNullOrWhiteSpace(modLoader))
        {
            var loaderType = modLoader.Trim().ToLowerInvariant() switch
            {
                "forge" => 1,
                "fabric" => 4,
                "quilt" => 5,
                _ => (int?)null
            };
            if (loaderType != null) url += $"&modLoaderType={loaderType}";
        }

        using var req = BuildRequest(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CurseForgeSearchResult>(json) ?? new CurseForgeSearchResult();
    }

    /// <summary>获取一个地图/存档下所有可下载的文件版本，按发布时间倒序（CurseForge 默认顺序）。</summary>
    public async Task<List<CurseForgeFile>> GetFilesAsync(int modId, string? gameVersion, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/mods/{modId}/files";
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += $"?gameVersion={Uri.EscapeDataString(gameVersion)}";

        using var req = BuildRequest(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<CurseForgeFileListResult>(json) ?? new CurseForgeFileListResult();
        return result.Data;
    }

    /// <summary>
    /// 下载一个地图/存档文件并解压到 &lt;minecraftDir&gt;/saves/ 下。
    /// 地图压缩包通常是"压缩包内一个文件夹=一个存档"的结构，直接解压到 saves/ 根目录即可，
    /// 不需要像数据包那样指定目标存档（地图本身就是新建一个独立存档）。
    /// </summary>
    public async Task<string> DownloadMapAsync(string minecraftDir, CurseForgeFile file,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(file.DownloadUrl))
            throw new InvalidOperationException("这个文件没有可用的下载链接（CurseForge 有部分作者禁止了第三方工具下载，只能去官网手动下载）。");

        var savesDir = Path.Combine(minecraftDir, "saves");
        Directory.CreateDirectory(savesDir);

        var tmpZip = Path.Combine(Path.GetTempPath(), $"xcl2_map_{Guid.NewGuid():N}.zip");
        try
        {
            progress?.Report($"下载 {file.FileName} ...");
            using (var resp = await _http.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmpZip);
                await resp.Content.CopyToAsync(fs, ct);
            }

            progress?.Report("解压存档 ...");
            System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, savesDir, overwriteFiles: true);
            progress?.Report("安装完成");
            return savesDir;
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* 忽略清理失败 */ }
        }
    }

    /// <summary>
    /// 下载一个 Mod 文件到 &lt;minecraftDir&gt;/mods/。跟地图不同，Mod 文件直接就是 jar，
    /// 不需要解压，原样放进 mods 目录即可。
    /// </summary>
    public async Task<string> DownloadModAsync(string minecraftDir, CurseForgeFile file,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(file.DownloadUrl))
            throw new InvalidOperationException("这个文件没有可用的下载链接（CurseForge 有部分作者禁止了第三方工具下载，只能去官网手动下载）。");

        var modsDir = Path.Combine(minecraftDir, "mods");
        Directory.CreateDirectory(modsDir);
        var destPath = Path.Combine(modsDir, file.FileName);
        var tmp = destPath + ".tmp";

        try
        {
            progress?.Report($"下载 {file.FileName} ...");
            using (var resp = await _http.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            progress?.Report("下载完成");
            return destPath;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失败 */ }
            throw;
        }
    }

    /// <summary>
    /// 下载一个材质包/光影包/数据包文件到对应目录，跟 ModrinthService.DownloadResourceAsync
    /// 落地目录规则完全一致：材质包 -&gt; resourcepacks/，光影包 -&gt; shaderpacks/，
    /// 数据包 -&gt; saves/&lt;存档名&gt;/datapacks/（必须指定 saveName，理由同 ModrinthService 类注释）。
    /// 文件本身直接落地，不解压。
    /// </summary>
    public async Task<string> DownloadResourceAsync(string minecraftDir, CurseForgeResourceKind kind,
        CurseForgeFile file, IProgress<string>? progress, string? saveName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(file.DownloadUrl))
            throw new InvalidOperationException("这个文件没有可用的下载链接（CurseForge 有部分作者禁止了第三方工具下载，只能去官网手动下载）。");

        string destDir;
        if (kind == CurseForgeResourceKind.DataPack)
        {
            if (string.IsNullOrWhiteSpace(saveName))
                throw new InvalidOperationException("数据包必须安装到具体的存档里，请先选择一个存档。");
            destDir = Path.Combine(minecraftDir, "saves", saveName, "datapacks");
        }
        else
        {
            // minecraftDir 参数对插件场景传入的其实是"服务器实例目录"（跟 Modrinth 那边一致，
            // 调用方负责传对目录，这里只管按 kind 拼子目录名），插件必须落在 plugins/ 下
            // 服务端才认得到，跟 resourcepacks/shaderpacks 是完全独立的目录含义。
            var subDir = kind switch
            {
                CurseForgeResourceKind.ResourcePack => "resourcepacks",
                CurseForgeResourceKind.Plugin => "plugins",
                _ => "shaderpacks"
            };
            destDir = Path.Combine(minecraftDir, subDir);
        }

        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, file.FileName);
        var tmp = destPath + ".tmp";

        try
        {
            progress?.Report($"下载 {file.FileName} ...");
            using (var resp = await _http.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            progress?.Report("下载完成");
            return destPath;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失败 */ }
            throw;
        }
    }
}

/// <summary>未配置 CurseForge API Key 时抛出，携带一条面向用户的中文说明，方便 UI 直接展示。</summary>
public class CurseForgeKeyMissingException : Exception
{
    public CurseForgeKeyMissingException()
        : base("还没有配置 CurseForge API Key，请先去「设置」页粘贴你的 key。")
    {
    }
}
