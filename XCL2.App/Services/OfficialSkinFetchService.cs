using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「下载正版玩家的皮肤」：按正版玩家名查询 Mojang 公开 API，取出该玩家当前
/// 皮肤纹理的 PNG 直链并下载到本地，供用户查看/复用（比如给自己的离线账户配一个
/// 同款皮肤）。全程只读 Mojang 官方公开接口，不涉及任何账户凭据/登录态。
///
/// 查询链路：
/// 1) GET https://api.mojang.com/users/profiles/minecraft/{playerName} 拿玩家名对应的 UUID；
/// 2) GET https://sessionserver.mojang.com/session/minecraft/profile/{uuid} 拿到 properties
///    里 base64 编码的 textures 字段；
/// 3) base64 解码后是一段 JSON，里面 textures.SKIN.url 就是皮肤 PNG 的直链地址。
/// 这是 Mojang 官方文档记录的标准查询方式，主流第三方启动器(PCL2/HMCL)查正版皮肤都是
/// 走这同一套接口。
/// </summary>
public class OfficialSkinFetchService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => _http.Dispose();

    public record OfficialSkinInfo(string PlayerName, string Uuid, string SkinUrl, bool IsSlimModel);

    /// <summary>按正版玩家名查询皮肤信息（不下载文件，只拿到直链+元信息，供 UI 先预览）。</summary>
    public async Task<OfficialSkinInfo> LookupAsync(string playerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            throw new ArgumentException("请输入正版玩家名。", nameof(playerName));

        var profileJson = await _http.GetStringAsync(
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(playerName.Trim())}", ct);
        using var profileDoc = JsonDocument.Parse(profileJson);
        if (!profileDoc.RootElement.TryGetProperty("id", out var idProp))
            throw new InvalidOperationException($"找不到名为「{playerName}」的正版玩家（可能是离线/自定义 ID 账户，或者玩家名拼写有误）。");

        var uuid = idProp.GetString()!;

        return await LookupByUuidAsync(uuid, playerName.Trim(), ct);
    }

    /// <summary>直接按 UUID 读取该正版档案当前公开皮肤。3D 纸娃娃优先走这个接口，
    /// 这样即使玩家刚改过名字，也不会因为旧用户名查不到而退回默认模型。</summary>
    public async Task<OfficialSkinInfo> LookupByUuidAsync(string uuid, string? playerName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            throw new ArgumentException("UUID 不能为空。", nameof(uuid));

        var compactUuid = uuid.Replace("-", "", StringComparison.Ordinal).Trim();
        var sessionJson = await _http.GetStringAsync(
            $"https://sessionserver.mojang.com/session/minecraft/profile/{Uri.EscapeDataString(compactUuid)}", ct);
        using var sessionDoc = JsonDocument.Parse(sessionJson);

        var texturesB64 = sessionDoc.RootElement.GetProperty("properties")
            .EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "textures")
            .GetProperty("value").GetString()!;

        var texturesJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(texturesB64));
        using var texturesDoc = JsonDocument.Parse(texturesJson);
        var skinElement = texturesDoc.RootElement.GetProperty("textures").GetProperty("SKIN");
        var skinUrl = skinElement.GetProperty("url").GetString()!;

        var isSlim = skinElement.TryGetProperty("metadata", out var metadata)
                     && metadata.TryGetProperty("model", out var model)
                     && model.GetString() == "slim";

        var resolvedName = string.IsNullOrWhiteSpace(playerName)
            ? (sessionDoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? compactUuid : compactUuid)
            : playerName.Trim();
        return new OfficialSkinInfo(resolvedName, compactUuid, skinUrl, isSlim);
    }

    /// <summary>跟 <see cref="LookupByUuidAsync(string,string?,CancellationToken)"/> 是同一套
    /// Yggdrasil 标准查询逻辑，唯一区别是 sessionserver 的 Host 换成调用方传入的认证服务器
    /// （皮肤站）apiRoot，而不是写死的 Mojang 官方地址。authlib-injector 生态的皮肤站在
    /// {apiRoot}/sessionserver/session/minecraft/profile/{uuid} 上实现了跟 Mojang 完全相同的
    /// 响应格式（这是 Yggdrasil 协议本身的约定，不是皮肤站自己发明的），所以直接复用同一套
    /// base64 解码 + textures.SKIN.url 解析代码，不需要另外写一套。
    /// 用于「3D 皮肤纸娃娃」在皮肤站(AuthServer)账户下也能拉取到该账户在皮肤站上当前生效的
    /// 皮肤，而不是永远回退到本地占位模型。</summary>
    public async Task<OfficialSkinInfo> LookupByUuidFromYggdrasilAsync(string apiRoot, string uuid, string? playerName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiRoot))
            throw new ArgumentException("皮肤站 API Root 不能为空。", nameof(apiRoot));
        if (string.IsNullOrWhiteSpace(uuid))
            throw new ArgumentException("UUID 不能为空。", nameof(uuid));

        var compactUuid = uuid.Replace("-", "", StringComparison.Ordinal).Trim();
        var root = apiRoot.TrimEnd('/');
        var sessionJson = await _http.GetStringAsync(
            $"{root}/sessionserver/session/minecraft/profile/{Uri.EscapeDataString(compactUuid)}", ct);
        using var sessionDoc = JsonDocument.Parse(sessionJson);

        var texturesB64 = sessionDoc.RootElement.GetProperty("properties")
            .EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "textures")
            .GetProperty("value").GetString()!;

        var texturesJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(texturesB64));
        using var texturesDoc = JsonDocument.Parse(texturesJson);
        var skinElement = texturesDoc.RootElement.GetProperty("textures").GetProperty("SKIN");
        var skinUrl = skinElement.GetProperty("url").GetString()!;

        var isSlim = skinElement.TryGetProperty("metadata", out var metadata)
                     && metadata.TryGetProperty("model", out var model)
                     && model.GetString() == "slim";

        var resolvedName = string.IsNullOrWhiteSpace(playerName)
            ? (sessionDoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? compactUuid : compactUuid)
            : playerName.Trim();
        return new OfficialSkinInfo(resolvedName, compactUuid, skinUrl, isSlim);
    }

    /// <summary>只下载皮肤 PNG 字节，供内存中的 2D/3D 预览直接使用，不强制落盘。</summary>
    public Task<byte[]> DownloadSkinBytesAsync(OfficialSkinInfo info, CancellationToken ct = default)
        => _http.GetByteArrayAsync(info.SkinUrl, ct);

    /// <summary>下载皮肤 PNG 到指定目录，文件名为 "{玩家名}.png"，返回保存路径。</summary>
    public async Task<string> DownloadSkinAsync(OfficialSkinInfo info, string saveDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(saveDir);
        var bytes = await _http.GetByteArrayAsync(info.SkinUrl, ct);
        var destPath = Path.Combine(saveDir, $"{info.PlayerName}.png");
        await File.WriteAllBytesAsync(destPath, bytes, ct);
        return destPath;
    }
}
