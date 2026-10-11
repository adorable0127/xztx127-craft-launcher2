using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

internal sealed record JavaDownloadSource(string DownloadUrl, string FileName, string? Sha256 = null, long? Size = null)
{
    /// <summary>ZIP 与 MSI 各自使用官方清单的链接和校验值，不能靠替换文件扩展名猜安装包。</summary>
    internal static JavaDownloadSource FromAdoptiumBinary(JsonElement binary, bool systemInstall)
    {
        var kind = systemInstall ? "installer" : "package";
        if (!binary.TryGetProperty(kind, out var asset) || asset.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(systemInstall
                ? "Adoptium 没有提供此版本的 MSI 安装程序，请改用便携版安装。"
                : "Adoptium 响应中缺少 Java 便携包。");
        var link = asset.TryGetProperty("link", out var linkValue) ? linkValue.GetString() : null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Adoptium 下载链接无效。");
        var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        name = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? uri.LocalPath : name);
        if (!name.EndsWith(systemInstall ? ".msi" : ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Adoptium 返回的文件格式与安装方式不匹配。");
        var checksum = asset.TryGetProperty("checksum", out var hashValue) ? hashValue.GetString() : null;
        if (checksum == null || checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Adoptium 清单缺少有效的 SHA-256 校验值，无法验证 Java 下载。");
        long? size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) && bytes > 0
            ? bytes : null;
        return new JavaDownloadSource(link!, name, checksum, size);
    }
}
