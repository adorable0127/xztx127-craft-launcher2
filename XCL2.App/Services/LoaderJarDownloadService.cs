using System.IO;
using System.Net.Http;

namespace XCL2.App.Services;

/// <summary>加载器类型：跟 ServerCoreType 概念上重叠，但这里独立定义一个更小的枚举，
/// 只覆盖「百宝箱」-「加载器 Jar 单独下载」这个轻量场景（不需要 ServerCoreType 里
/// 服务端相关的额外成员），避免这个小工具功能反过来依赖一个语义更重的服务端枚举。</summary>
public enum StandaloneLoaderType
{
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

/// <summary>
/// 「百宝箱」-「加载器 Jar 单独下载」：对应截图里加载器列表(Minecraft/OptiFine/Forge/
/// NeoForge/Cleanroom/Fabric/Legacy Fabric/Quilt/LabyMod/LiteLoader)"选中某个加载器后
/// 单独下它的安装器/loader jar，不走完整的一键安装流程"这个需求——用户只是想要那个 jar
/// 文件本身（比如自己手动装到别的启动器里，或者单纯想留一份离线安装包），不需要这里
/// 帮忙把它装进任何 .minecraft 版本目录。
///
/// 复用 ClientLoaderInstallService/ForgeVersionQueryService 里已经踩过坑、验证过的
/// 各家 Meta API 地址(见那两个类的注释)，只是这里查完版本号列表后不走后续"下载 profile
/// json + 补 libraries + 写版本文件夹"那一整套安装步骤，而是直接把最终的安装器/loader jar
/// 下载到用户指定的目录，作为一个独立文件保留。
/// </summary>
public class LoaderJarDownloadService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    private const string QuiltMetaBase = "https://meta.quiltmc.org/v3";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    public void Dispose() => _http.Dispose();

    /// <summary>查询某个加载器类型支持的 MC 版本列表，供 UI 下拉框选择。
    /// Forge/NeoForge 复用 ForgeVersionQueryService（已经处理过官方 404/重定向 的坑）；
    /// Fabric/Quilt 直接查各自 Meta API 的 /versions/game。</summary>
    public async Task<List<string>> GetSupportedMcVersionsAsync(StandaloneLoaderType type, CancellationToken ct = default)
    {
        switch (type)
        {
            case StandaloneLoaderType.Fabric:
                return await GetStableGameVersionsAsync($"{FabricMetaBase}/versions/game", ct);
            case StandaloneLoaderType.Quilt:
                return await GetStableGameVersionsAsync($"{QuiltMetaBase}/versions/game", ct);
            case StandaloneLoaderType.Forge:
                return await ForgeVersionQueryService.GetForgeVersionsAsync(_http, ct);
            case StandaloneLoaderType.NeoForge:
                return await ForgeVersionQueryService.GetNeoForgeVersionsAsync(_http, ct);
            default:
                return new List<string>();
        }
    }

    private async Task<List<string>> GetStableGameVersionsAsync(string url, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(url, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && !stable.GetBoolean()) continue;
            result.Add(v.GetProperty("version").GetString()!);
        }
        return result;
    }

    /// <summary>查询某个加载器在指定 MC 版本下的可用 loader/安装器版本号列表。</summary>
    public async Task<List<string>> GetLoaderVersionsAsync(StandaloneLoaderType type, string mcVersion, CancellationToken ct = default)
    {
        switch (type)
        {
            case StandaloneLoaderType.Fabric:
            {
                var json = await _http.GetStringAsync($"{FabricMetaBase}/versions/loader/{mcVersion}", ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.GetProperty("loader").GetProperty("version").GetString()!)
                    .ToList();
            }
            case StandaloneLoaderType.Quilt:
            {
                var json = await _http.GetStringAsync($"{QuiltMetaBase}/versions/loader/{mcVersion}", ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.GetProperty("loader").GetProperty("version").GetString()!)
                    .ToList();
            }
            case StandaloneLoaderType.Forge:
            {
                var builds = await ForgeVersionQueryService.GetForgeInstallerVersionsAsync(_http, mcVersion, ct);
                return builds.Select(b => b.DisplayVersion).ToList();
            }
            case StandaloneLoaderType.NeoForge:
            {
                // NeoForge 的版本号本身就同时编码了 MC 版本 + loader 版本（如 21.1.100 对应 1.21.1），
                // 复用完整版本号列表，筛选出属于当前 mcVersion 的那些，跟 ClientLoaderInstallService
                // 安装 NeoForge 时使用的匹配口径保持一致（同一个 mcVersion 段前缀）。
                var all = await ForgeVersionQueryService.GetNeoForgeVersionsAsync(_http, ct);
                var shortVer = mcVersion.StartsWith("1.") ? mcVersion[2..] : mcVersion;
                return all.Where(v => v.StartsWith(shortVer + ".", StringComparison.OrdinalIgnoreCase)
                                       || v.StartsWith(shortVer, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            default:
                return new List<string>();
        }
    }

    /// <summary>
    /// 下载指定加载器/版本的安装器或 loader jar 到用户指定目录，返回最终保存的文件路径。
    /// 各家格式差异：
    /// - Fabric/Quilt：直接是"loader jar"本体（不是安装器），从 Meta API 的 loader 端点拼 URL；
    /// - Forge：下载官方 installer jar（用户需要自己运行这个 jar 来安装/生成客户端或服务端）；
    /// - NeoForge：同 Forge，也是 installer jar。
    /// </summary>
    public async Task<string> DownloadAsync(StandaloneLoaderType type, string mcVersion, string loaderVersion,
        string saveDir, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(saveDir);

        var (url, fileName) = type switch
        {
            StandaloneLoaderType.Fabric =>
                ($"{FabricMetaBase}/versions/loader/{mcVersion}/{loaderVersion}/profile/json",
                 $"fabric-loader-{loaderVersion}-{mcVersion}.json"),
            StandaloneLoaderType.Quilt =>
                ($"{QuiltMetaBase}/versions/loader/{mcVersion}/{loaderVersion}/profile/json",
                 $"quilt-loader-{loaderVersion}-{mcVersion}.json"),
            StandaloneLoaderType.Forge =>
                ($"{ForgeMavenBase}/{mcVersion}-{loaderVersion}/forge-{mcVersion}-{loaderVersion}-installer.jar",
                 $"forge-{mcVersion}-{loaderVersion}-installer.jar"),
            StandaloneLoaderType.NeoForge =>
                ($"{NeoForgeMavenBase}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar",
                 $"neoforge-{loaderVersion}-installer.jar"),
            _ => throw new NotSupportedException($"不支持的加载器类型：{type}")
        };

        // Fabric/Quilt 的"loader jar"实际上是一份 JSON profile（真正的可执行 jar 由客户端启动时
        // 按 profile 里 libraries 列表从 Maven 逐个拉取，没有一个单独打包好的"loader.jar"文件）。
        // 这里如实按各家生态的真实产物类型下载并保存，不假装 Fabric/Quilt 也有一个单体 jar。
        progress?.Report(new ProgressInfo("下载加载器文件", 0, 1, fileName));

        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n地址：{url}");

        var destPath = Path.Combine(saveDir, fileName);
        var tempPath = destPath + ".tmp";
        await using (var fs = File.Create(tempPath))
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        {
            await stream.CopyToAsync(fs, ct);
        }
        File.Move(tempPath, destPath, overwrite: true);

        progress?.Report(new ProgressInfo("下载加载器文件", 1, 1, fileName));
        return destPath;
    }
}
