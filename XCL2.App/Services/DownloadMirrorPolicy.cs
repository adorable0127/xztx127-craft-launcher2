using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>纯 URL 规则。精确匹配主机和路径边界，不把令牌、签名查询或用户信息交给镜像。</summary>
public static class DownloadMirrorPolicy
{
    private static readonly (string Source, string Mirror)[] BmclRules =
    {
        ("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets"),
        ("https://maven.fabricmc.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://meta.fabricmc.net", "https://bmclapi2.bangbang93.com/fabric-meta"),
        // BMCLAPI 文档注明 Quilt Meta 暂不可用；只启用明确支持的 Maven 路径。
        ("https://maven.quiltmc.org/repository/release", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.neoforged.net/releases", "https://bmclapi2.bangbang93.com/maven"),
        ("https://files.minecraftforge.net/maven", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.minecraftforge.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://authlib-injector.yushi.moe", "https://bmclapi2.bangbang93.com/mirrors/authlib-injector")
    };

    // 来自镜像运营方接入指南，详见 DOWNLOAD_MIRRORS.md。
    private static readonly (string Source, string Mirror)[] CommunityRules =
    {
        ("https://cdn.modrinth.com", "https://mod.mcimirror.top"),
        ("https://edge.forgecdn.net", "https://mod.mcimirror.top"),
        ("https://mediafilez.forgecdn.net", "https://mod.mcimirror.top"),
        ("https://api.modrinth.com/v2", "https://mod.mcimirror.top/modrinth/v2")
    };

    private static readonly HashSet<string> ModrinthPublicQueryNames = new(StringComparer.Ordinal)
    { "query", "facets", "offset", "limit", "index", "game_versions", "loaders", "featured", "include_changelog", "ids" };

    public static bool MayMirror(string url)
    {
        if (!TryPublicHttps(url, out var uri)) return false;
        if (uri.Query.Length == 0) return true;
        // 搜索、版本筛选是公开参数；除此之外，任何查询字符串均不跨域转发。
        if (!uri.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
            !HasPathPrefix(uri.AbsolutePath, "/v2")) return false;
        return uri.Query[1..].Split('&').All(part =>
            ModrinthPublicQueryNames.Contains(Uri.UnescapeDataString(part.Split('=', 2)[0])));
    }

    public static IReadOnlyList<string> Mirrors(string original, DownloadMirrorConfig? config = null)
    {
        if (!MayMirror(original)) return Array.Empty<string>();
        config ??= new DownloadMirrorConfig();
        var result = new List<string>();
        foreach (var (source, mirror) in BmclRules) AddMapped(result, original, source, mirror);
        if (config.EnableCommunityMirrors)
            foreach (var (source, mirror) in CommunityRules) AddMapped(result, original, source, mirror);
        if (config.EnableGithubMirrors && IsPublicGithubFile(original))
        {
            result.Add("https://gh-proxy.org/" + original);
            result.Add("https://gh-proxy.com/" + original);
        }
        foreach (var rule in (config.CustomRules ?? new()).Take(32))
            if (rule is { Enabled: true } && ValidateRule(rule) == null)
                AddMapped(result, original, rule.SourcePrefix, rule.MirrorPrefix);
        return result.Where(url => !url.Equals(original, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string? ValidateRule(DownloadMirrorRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return "请填写镜像名称。";
        if (!TryPublicHttps(rule.SourcePrefix, out var source) || source.Query.Length != 0)
            return "原始前缀必须是 HTTPS 地址，不能包含账号、查询参数或片段。";
        if (!TryPublicHttps(rule.MirrorPrefix, out var mirror) || mirror.Query.Length != 0)
            return "镜像前缀必须是 HTTPS 地址，不能包含账号、查询参数或片段。";
        if (rule.SourcePrefix.Equals(rule.MirrorPrefix, StringComparison.Ordinal)) return "原始前缀与镜像前缀不能相同。";
        return null;
    }

    private static bool IsPublicGithubFile(string url)
    {
        var uri = new Uri(url);
        if (uri.Query.Length != 0) return false;
        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4 &&
            (segments[2] == "archive" || segments[2] == "raw" ||
             segments.Length >= 6 && segments[2] == "releases" && segments[3] == "download");
    }

    private static void AddMapped(List<string> result, string original, string sourcePrefix, string mirrorPrefix)
    {
        if (!Uri.TryCreate(sourcePrefix, UriKind.Absolute, out var source) ||
            !Uri.TryCreate(original, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals(source.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme != source.Scheme || uri.Port != source.Port) return;
        var prefixPath = source.AbsolutePath.TrimEnd('/');
        if (!HasPathPrefix(uri.AbsolutePath, prefixPath)) return;
        result.Add(mirrorPrefix.TrimEnd('/') + uri.AbsolutePath[prefixPath.Length..] + uri.Query);
    }

    private static bool HasPathPrefix(string path, string prefix) =>
        prefix.Length == 0 || path.Equals(prefix, StringComparison.Ordinal) || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static bool TryPublicHttps(string url, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    }
}
