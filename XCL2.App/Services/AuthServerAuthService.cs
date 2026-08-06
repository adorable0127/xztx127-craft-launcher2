using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 第三方认证服务器（俗称"皮肤站"）登录：实现标准 Yggdrasil 协议的
/// POST {apiRoot}/authserver/authenticate 接口——这是 authlib-injector 生态里
/// 事实标准的登录方式，市面上主流的统一通行证/blessing-skin 等面板都实现了这个接口，
/// 跟 Mojang 官方废弃已久的旧版登录协议是同一套格式，只是 Host 换成了用户自己的皮肤站。
///
/// 跟 MicrosoftAuthService 是完全独立的两条登录路径，互不调用、互不影响：
/// 微软账户登录/刷新逻辑一个字节都没有改动。
///
/// 登录成功后返回的 Account.Type = AuthServer，游戏启动时由 LauncherService 结合
/// SkinService.BuildSkinJvmArgs 的思路（见 MainWindow 里给 AuthServer 账户单独加的分支）
/// 挂 authlib-injector，让客户端把这个 apiRoot 当成认证/皮肤服务器使用。
/// </summary>
public class AuthServerAuthService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public AuthServerAuthService()
    {
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2Launcher", "1.0"));
    }

    /// <summary>
    /// 用邮箱/用户名 + 密码登录到指定认证服务器，返回一个可以直接保存/启动游戏用的 Account。
    /// apiRoot 可以是完整 API Root（如 "https://example.com/api/yggdrasil"），
    /// 也可以是常见皮肤站主页地址（如 "https://littleskin.cn"）；后者会自动探测常见 API 路径。
    /// </summary>
    public async Task<Account> LoginAsync(string apiRoot, string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiRoot))
            throw new AuthStepException("认证服务器地址", "请填写皮肤站地址。常见皮肤站会自动匹配 API 地址；未检测到的皮肤站才需要填写完整 API Root。");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new AuthStepException("填写账号密码", "用户名/邮箱和密码都不能为空。");

        var normalizedRoot = await ResolveApiRootAsync(apiRoot, ct);
        var clientToken = Guid.NewGuid().ToString("N");

        var requestBody = JsonSerializer.Serialize(new
        {
            username,
            password,
            clientToken,
            requestUser = true,
            agent = new { name = "Minecraft", version = 1 }
        });

        HttpResponseMessage resp;
        try
        {
            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            resp = await _http.PostAsync($"{normalizedRoot}/authserver/authenticate", content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AuthStepException("连接认证服务器",
                $"无法连接到认证服务器，请确认地址填写正确、且服务器可以正常访问。原始错误：{ex.Message}");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Yggdrasil 标准错误响应形如 {"error":"ForbiddenOperationException","errorMessage":"..."}，
            // 优先把服务端给的 errorMessage（通常已经是人话，比如"密码错误"）原样展示给用户，
            // 解析失败（服务端返回格式不标准）则退化成展示原始状态码。
            string friendly;
            try
            {
                using var errDoc = JsonDocument.Parse(body);
                friendly = errDoc.RootElement.TryGetProperty("errorMessage", out var msg)
                    ? msg.GetString() ?? "登录失败。"
                    : $"登录失败（HTTP {(int)resp.StatusCode}）。";
            }
            catch
            {
                friendly = $"登录失败（HTTP {(int)resp.StatusCode}），且服务器返回的内容不是标准的 Yggdrasil 错误格式，可能这个地址不是一个有效的认证服务器 API 根地址。";
            }
            throw new AuthStepException("认证服务器登录", friendly);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("accessToken", out var accessTokenEl))
            throw new AuthStepException("认证服务器登录", "登录响应里没有 accessToken 字段，这个服务器可能不是标准的 Yggdrasil 认证服务器。");
        var accessToken = accessTokenEl.GetString() ?? "";

        // selectedProfile 在只绑定了一个游戏角色时会直接给出；如果账号下有多个角色但没有
        // selectedProfile，则从 availableProfiles 里取第一个——这是大多数皮肤站单角色场景下
        // 最常见的情况，多角色选择器留到以后有真实需求时再做，不在这里强行猜测用户想要哪个。
        JsonElement? profile = null;
        if (root.TryGetProperty("selectedProfile", out var sel) && sel.ValueKind == JsonValueKind.Object)
            profile = sel;
        else if (root.TryGetProperty("availableProfiles", out var avail) && avail.ValueKind == JsonValueKind.Array
            && avail.GetArrayLength() > 0)
            profile = avail[0];

        if (profile is null)
            throw new AuthStepException("获取游戏角色", "这个账号下没有可用的游戏角色，请先在认证服务器的网页后台创建一个角色后再登录。");

        var profileName = profile.Value.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? username : username;
        var profileUuid = profile.Value.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

        return new Account
        {
            Type = AccountType.AuthServer,
            Username = profileName,
            Uuid = InsertUuidDashes(profileUuid),
            MinecraftAccessToken = accessToken,
            AuthServerApiRoot = normalizedRoot,
            AuthServerClientToken = clientToken,
            // 皮肤站账户的会话有效期由服务器自行控制（多数实现里跟官方一样是几十小时到几天不等），
            // 这里不去猜测具体时长——启动时如果 accessToken 已经失效，游戏本体登录会话校验失败，
            // 用户重新登录一次即可，比较简单可靠，不引入额外的静默刷新时序复杂度。
            AccessTokenExpiresAtUtc = null
        };
    }

    private async Task<string> ResolveApiRootAsync(string input, CancellationToken ct)
    {
        var candidates = BuildApiRootCandidates(input).ToList();
        if (candidates.Count == 0)
            throw new AuthStepException("识别皮肤站地址", "无法识别这个皮肤站地址，请填写完整的认证服务器 API Root。");

        foreach (var candidate in candidates)
        {
            if (await LooksLikeYggdrasilApiRootAsync(candidate, ct))
                return candidate;
        }

        throw new AuthStepException("识别皮肤站地址",
            "未能自动检测到这个皮肤站的 Yggdrasil API 地址。请在皮肤站后台或帮助页面查找并填写完整 API Root，例如：https://example.com/api/yggdrasil");
    }

    private static IEnumerable<string> BuildApiRootCandidates(string input)
    {
        var raw = input.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        if (!raw.Contains("://")) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) yield break;

        var noAuthserver = raw.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase)
            ? raw[..^"/authserver".Length]
            : raw;

        if (noAuthserver.Contains("/api/yggdrasil", StringComparison.OrdinalIgnoreCase) ||
            noAuthserver.Contains("/authlib-injector/api", StringComparison.OrdinalIgnoreCase))
        {
            yield return noAuthserver;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var path = uri.AbsolutePath.Trim('/');
        if (!string.IsNullOrWhiteSpace(path))
            yield return $"{origin}/{path}/api/yggdrasil";

        yield return $"{origin}/api/yggdrasil";
        yield return $"{origin}/authlib-injector/api";
    }

    private async Task<bool> LooksLikeYggdrasilApiRootAsync(string apiRoot, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiRoot);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // authlib-injector 兼容 API Root 通常会在根路径返回 meta/skinDomains/signaturePublickey。
            return root.TryGetProperty("meta", out _) ||
                   root.TryGetProperty("skinDomains", out _) ||
                   root.TryGetProperty("signaturePublickey", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Yggdrasil 返回的 UUID 通常是不带短横线的 32 位十六进制字符串，
    /// 这里补回标准的 8-4-4-4-12 格式，跟项目里其它地方存储 UUID 的格式保持一致。</summary>
    private static string InsertUuidDashes(string raw)
    {
        var hex = raw.Replace("-", "");
        if (hex.Length != 32) return raw; // 格式不对就原样返回，不强行拼接出错误数据
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
