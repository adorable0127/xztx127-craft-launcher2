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

        var sessionJson = await _http.GetStringAsync(
            $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}", ct);
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

        return new OfficialSkinInfo(playerName.Trim(), uuid, skinUrl, isSlim);
    }

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
