using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>一个可更新的候选文件（模组或资源包，判定逻辑完全一样，只是扫描的文件夹和后缀不同）。</summary>
public class UpdateCandidate
{
    public string CurrentFilePath { get; set; } = "";
    public string CurrentFileName => Path.GetFileName(CurrentFilePath);
    public bool IsCurrentlyDisabled { get; set; }
    public string DisplayName { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string CurrentVersionName { get; set; } = "";
    public string NewVersionName { get; set; } = "";
    public string NewFileName { get; set; } = "";
    public string NewDownloadUrl { get; set; } = "";
    public string NewSha1 { get; set; } = "";
    public long NewFileSize { get; set; }

    /// <summary>用户在确认对话框里是否勾选了这一项参与本次批量升级。</summary>
    public bool Selected { get; set; } = true;
}

/// <summary>
/// "一键批量升级模组/资源包"：
///
/// 核心思路——不依赖启动器自己记录"这个文件是从哪装的"（本地历史上从来没有这层记录，
/// 用户还可能手动拖进来过 jar），而是用文件内容的 SHA1 哈希去 Modrinth 反查："这个哈希
/// 对应的是哪个项目(mod/资源包)的哪个版本"，查到项目后再问一遍"这个项目在当前游戏版本+
/// 加载器下最新的版本是什么"，两次查询的版本号/文件哈希不一致就认定"有更新"。
/// 这个反查接口（version_file/{hash}）是 Modrinth 官方为"启动器更新检测"这个场景专门
/// 设计的，不需要用户/mod 作者做任何额外配合。
///
/// 只支持 Modrinth：CurseForge 的等价"按哈希反查"接口(fingerprint)需要用 murmur2 且
/// 必须带官方发的 API Key，跟现有 CurseForgeKeyService 的接入方式还要再对一轮，
/// 先把最常用、免key、免额外依赖的 Modrinth 路径先做完整、跑通，CurseForge 支持
/// 作为后续扩展点（见下面 CheckAsync 的注释）。查不到 Modrinth 记录的文件（比如
/// 纯 CurseForge 独占的 mod）会被跳过，不会误报"有更新"或"无法识别"这种噪音提示。
/// </summary>
public class BatchUpdateService : IDisposable
{
    private const string ModrinthBaseUrl = "https://api.modrinth.com/v2";
    private readonly HttpClient _http;
    private readonly GenericFileDownloadService _downloader = new();

    public BatchUpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2-Launcher", "1.0"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/xztx127-craft/xcl2)"));
    }

    private static string ComputeSha1(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 扫描 folderPath 下所有匹配后缀的文件（mods 传 "*.jar"，resourcepacks 传 "*.zip"），
    /// 逐个查 Modrinth 是否有更新。已禁用的文件（.disabled 后缀）也会一并检查——用户很可能
    /// 就是先禁用了旧版本等着换新版本，不应该因为禁用状态被跳过。
    /// 单个文件查询失败（网络问题/这个文件根本不是 Modrinth 上的资源）不会中断整体扫描，
    /// 直接跳过继续查下一个。
    /// </summary>
    public async Task<List<UpdateCandidate>> CheckAsync(string folderPath, string filePattern,
        string mcVersion, string? loader, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var result = new List<UpdateCandidate>();
        if (!Directory.Exists(folderPath)) return result;

        var files = Directory.GetFiles(folderPath, filePattern)
            .Concat(Directory.GetFiles(folderPath, filePattern + ".disabled"))
            .ToList();

        var checkedCount = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            checkedCount++;
            progress?.Report(new ProgressInfo("正在检查更新", checkedCount, files.Count, Path.GetFileName(file)));

            try
            {
                var candidate = await CheckOneAsync(file, mcVersion, loader, ct);
                if (candidate != null) result.Add(candidate);
            }
            catch
            {
                // 单个文件识别失败（网络抖动/不是 Modrinth 资源/哈希查无结果）静默跳过，
                // 不能因为一个文件查询异常就让整批检查失败。
            }
        }

        return result;
    }

    private async Task<UpdateCandidate?> CheckOneAsync(string filePath, string mcVersion, string? loader, CancellationToken ct)
    {
        var isDisabled = filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var sha1 = ComputeSha1(filePath);

        // 第一步：哈希反查"这是 Modrinth 上哪个项目的哪个版本"。查不到说明这个文件不是
        // （或不确定是）Modrinth 上的资源，直接放弃，不当作错误处理。
        using var lookupResp = await _http.GetAsync($"{ModrinthBaseUrl}/version_file/{sha1}?algorithm=sha1", ct);
        if (!lookupResp.IsSuccessStatusCode) return null;
        var lookupJson = await lookupResp.Content.ReadAsStringAsync(ct);
        using var lookupDoc = JsonDocument.Parse(lookupJson);
        var root = lookupDoc.RootElement;

        var projectId = root.GetProperty("project_id").GetString() ?? "";
        var currentVersionNumber = root.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(projectId)) return null;

        // 第二步：查这个项目在目标游戏版本 + 加载器下，最新的一个版本是什么。
        var loaderFilter = string.IsNullOrWhiteSpace(loader) ? "" : $"&loaders=[\"{Uri.EscapeDataString(loader.ToLowerInvariant())}\"]";
        var versionsUrl = $"{ModrinthBaseUrl}/project/{projectId}/version?game_versions=[\"{Uri.EscapeDataString(mcVersion)}\"]{loaderFilter}";
        using var versionsResp = await _http.GetAsync(versionsUrl, ct);
        if (!versionsResp.IsSuccessStatusCode) return null;
        var versionsJson = await versionsResp.Content.ReadAsStringAsync(ct);
        using var versionsDoc = JsonDocument.Parse(versionsJson);
        var versionsArr = versionsDoc.RootElement;
        if (versionsArr.GetArrayLength() == 0) return null;

        // Modrinth 按发布时间倒序返回，第一条就是当前筛选条件下最新的版本。
        var latest = versionsArr[0];
        var latestVersionId = latest.GetProperty("id").GetString() ?? "";
        var latestVersionNumber = latest.TryGetProperty("version_number", out var lvn) ? lvn.GetString() ?? "" : "";
        var currentVersionId = root.TryGetProperty("id", out var vid) ? vid.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(latestVersionId) || latestVersionId == currentVersionId) return null; // 已经是最新，没有更新

        var files = latest.GetProperty("files");
        JsonElement primaryFile = files[0];
        foreach (var f in files.EnumerateArray())
        {
            if (f.TryGetProperty("primary", out var isPrimary) && isPrimary.GetBoolean()) { primaryFile = f; break; }
        }

        var projectResp = await _http.GetAsync($"{ModrinthBaseUrl}/project/{projectId}", ct);
        string displayName = projectId;
        if (projectResp.IsSuccessStatusCode)
        {
            using var projDoc = JsonDocument.Parse(await projectResp.Content.ReadAsStringAsync(ct));
            if (projDoc.RootElement.TryGetProperty("title", out var titleEl))
                displayName = titleEl.GetString() ?? projectId;
        }

        return new UpdateCandidate
        {
            CurrentFilePath = filePath,
            IsCurrentlyDisabled = isDisabled,
            DisplayName = displayName,
            ProjectId = projectId,
            CurrentVersionName = currentVersionNumber,
            NewVersionName = latestVersionNumber,
            NewFileName = primaryFile.GetProperty("filename").GetString() ?? $"{projectId}.jar",
            NewDownloadUrl = primaryFile.GetProperty("url").GetString() ?? "",
            NewSha1 = primaryFile.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha1", out var h1) ? h1.GetString() ?? "" : "",
            NewFileSize = primaryFile.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0,
        };
    }

    /// <summary>
    /// 对用户勾选确认过的候选逐个执行升级：下载新文件、删除旧文件、保持原有的启用/禁用状态
    /// （旧文件是禁用状态，新文件下载后也加上 .disabled 后缀，不会因为升级把用户特意禁用的
    /// mod 意外唤醒）。单个下载失败不影响其它文件继续升级，失败的会在返回结果里标出来，
    /// 由调用方决定怎么提示用户（成功了几个/失败了哪几个）。
    /// </summary>
    public async Task<(List<string> succeeded, List<(string name, string error)> failed)> ApplyAsync(
        string folderPath, List<UpdateCandidate> candidates,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var succeeded = new List<string>();
        var failed = new List<(string, string)>();

        var selected = candidates.Where(c => c.Selected).ToList();
        var done = 0;
        foreach (var c in selected)
        {
            done++;
            progress?.Report(new ProgressInfo("正在升级", done, selected.Count, c.DisplayName));
            try
            {
                var tempName = c.NewFileName + ".xcl2tmp";
                await _downloader.DownloadAsync(c.NewDownloadUrl, folderPath, tempName, userAgent: null, progress: null, ct);

                var finalName = c.IsCurrentlyDisabled ? c.NewFileName + ".disabled" : c.NewFileName;
                var finalPath = Path.Combine(folderPath, finalName);
                var tempPath = Path.Combine(folderPath, tempName);

                // 新旧文件名可能不同（mod 作者改过命名规则），新文件落地成功后才删旧文件，
                // 避免"新文件没下完/校验失败"却已经把旧文件删了、两头落空的中间状态。
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tempPath, finalPath);
                if (File.Exists(c.CurrentFilePath) && !string.Equals(c.CurrentFilePath, finalPath, StringComparison.OrdinalIgnoreCase))
                    File.Delete(c.CurrentFilePath);

                succeeded.Add(c.DisplayName);
            }
            catch (Exception ex)
            {
                failed.Add((c.DisplayName, ex.Message));
            }
        }

        return (succeeded, failed);
    }

    public void Dispose()
    {
        _http.Dispose();
        _downloader.Dispose();
    }
}
