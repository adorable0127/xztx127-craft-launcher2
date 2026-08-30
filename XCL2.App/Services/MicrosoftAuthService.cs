using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>登录链路某一步失败时抛出，携带具体阶段名和原始响应，方便定位问题。</summary>
public class AuthStepException : Exception
{
    public string Step { get; }
    public AuthStepException(string step, string message) : base(message) => Step = step;
}

/// <summary>见 MicrosoftAuthService.LastRefreshFailureReason。</summary>
public enum RefreshFailureReason
{
    /// <summary>没有失败（或还没调用过）。</summary>
    None,
    /// <summary>连不上微软登录服务/XSTS/Minecraft 服务本身，或对方 5xx——服务不可用，
    /// 不代表这个账户的授权已失效，允许调用方走令牌保留时效降级直接离线启动。</summary>
    ServiceUnavailable,
    /// <summary>微软明确应答拒绝了这次刷新（4xx，典型是 refresh token 已过期/被吊销），
    /// 是真正的"需要重新登录"，不应该走令牌保留时效降级。</summary>
    TokenInvalid
}

/// <summary>
/// 微软账户登录：使用 OAuth2 Device Code Flow。
/// 通过系统默认浏览器打开微软登录页(https://microsoft.com/link)，用户在浏览器完成登录，
/// 本程序在后台轮询获取 token —— 完全不需要内嵌 WebView。
/// 流程：MSA device code -> MSA token -> Xbox Live -> XSTS -> Minecraft services -> profile
/// </summary>
public class MicrosoftAuthService
{
    private readonly string _clientId;
    private const string Scope = "XboxLive.signin offline_access";

    /// <summary>
    /// 微软登录用的 Azure 应用 Client ID：直接写死编译进程序，不再暴露成设置页里可编辑/可见的
    /// 输入框。之前把这个值明文展示在设置页 TextBox 里，任何拿到软件的人都能直接复制走，
    /// 冒用这个 Client ID 去发起自己的登录请求——Client ID 本身虽然不是密钥，泄露出去也不会让
    /// 别人拿到你的账户，但让别人的流量算在你的 Azure 应用配额/审计记录里终究不是好事，
    /// 而且没有必要为了一个"填一次就不用再改"的值常驻在界面上增加攻击面。
    /// 如果之后需要换成另一个 Azure 应用，直接改这里的常量重新编译即可。
    /// </summary>
    public const string DefaultClientId = "b41a829c-4710-4954-a6de-814923dca264";

    // 微软官方为"原生/公共客户端"应用预留的特殊重定向 URI：不需要真的有服务器监听它，
    // 内嵌登录窗口只要监测到跳转到这个地址、把 URL 上的 ?code= 取出来即可。
    // 必须同时在 Azure 门户「Authentication」里为该应用添加这个重定向 URI
    // （平台类型选"移动和桌面应用程序"），否则微软会在显示登录页之前就直接拒绝请求。
    public const string NativeClientRedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";

    private readonly HttpClient _http = new();

    /// <param name="clientId">留空则使用编译进程序的默认 Client ID (<see cref="DefaultClientId"/>)。</param>
    public MicrosoftAuthService(string? clientId = null)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId;

        // Xbox Live / XSTS 接口对请求头很敏感：官方文档明确指出，如果不设置
        // Accept: application/json（PostAsJsonAsync 只会自动设置 Content-Type，不会设置 Accept），
        // 服务器会直接返回 HTTP 400。这里统一在 HttpClient 层面加上默认 Accept 头。
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // 部分请求在缺少 User-Agent 时会被网关判定为异常请求而拒绝（参考同类开源启动器实现），
        // 这里补上一个明确的 User-Agent，降低被无理由拒绝的概率。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2Launcher/2.0 (+https://github.com/)");
    }

    public event Action<string, string>? UserCodeReady; // (verificationUri, userCode)
    public event Action<string>? StatusChanged; // 每一步的进度提示，用于弹窗实时显示

    public async Task<Account?> LoginInteractiveAsync(CancellationToken ct = default)
    {
        StatusChanged?.Invoke("正在向微软请求登录代码...");
        var device = await RequestDeviceCodeAsync(ct);

        UserCodeReady?.Invoke(device.VerificationUri, device.UserCode);
        try
        {
            Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
        }
        catch { /* 用户可手动打开浏览器输入代码 */ }

        StatusChanged?.Invoke("等待你在浏览器中完成登录...");
        var msaToken = await PollForTokenAsync(device, ct);

        StatusChanged?.Invoke("登录成功，正在换取 Xbox / Minecraft 令牌...");
        return await CompleteLoginChainAsync(msaToken.AccessToken, msaToken.RefreshToken, ct);
    }

    /// <summary>
    /// 构造"内嵌浏览器直接登录"（免复制验证码）所需的授权 URL，使用 Authorization Code + PKCE。
    /// 调用方（一个托管 WebView2 的窗口）负责打开这个 URL、监测跳转到 <see cref="NativeClientRedirectUri"/>
    /// 时 URL 上的 code 参数，然后把 code 和这里返回的 verifier 一起传给 <see cref="ExchangeAuthCodeAsync"/>。
    /// </summary>
    public (string Url, string Verifier) BuildInteractiveAuthorizeUrl()
    {
        var verifier = GeneratePkceVerifier();
        var challenge = ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = string.Join("&", new[]
        {
            $"client_id={Uri.EscapeDataString(_clientId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(NativeClientRedirectUri)}",
            "response_mode=query",
            $"scope={Uri.EscapeDataString(Scope)}",
            $"code_challenge={challenge}",
            "code_challenge_method=S256",
            "prompt=select_account"
        });
        return ($"https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?{query}", verifier);
    }

    /// <summary>用内嵌登录窗口截获的授权码换取 Microsoft token，再走完整条 Xbox/Minecraft 登录链路。</summary>
    public async Task<Account?> LoginWithAuthorizationCodeAsync(string code, string verifier, CancellationToken ct = default)
    {
        StatusChanged?.Invoke("登录成功，正在换取 Xbox / Minecraft 令牌...");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = NativeClientRedirectUri,
            ["code_verifier"] = verifier,
            ["scope"] = Scope
        };
        var resp = await _http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new AuthStepException("获取登录令牌", $"HTTP {(int)resp.StatusCode}: {body}");
        var token = JsonSerializer.Deserialize<MsaTokenResponse>(body)
            ?? throw new AuthStepException("获取登录令牌", "响应解析失败: " + body);

        return await CompleteLoginChainAsync(token.AccessToken, token.RefreshToken, ct);
    }

    private static string GeneratePkceVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return ToBase64Url(bytes);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>使用已缓存的 refresh token 静默刷新，免去重新登录浏览器的步骤。</summary>
    /// <summary>
    /// 上一次 <see cref="RefreshAsync"/> 失败的原因分类，供调用方决定要不要走"令牌保留时效"
    /// 降级（见 AppConfig.AccountTokenGracePeriodDays）。每次调用 RefreshAsync 开始时重置为
    /// None，调用方应在 await 返回后立即读取，不要跨多次调用复用同一个实例读旧值。
    /// </summary>
    public RefreshFailureReason LastRefreshFailureReason { get; private set; } = RefreshFailureReason.None;

    public async Task<Account?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        LastRefreshFailureReason = RefreshFailureReason.None;
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        };

        System.Net.Http.HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(form), ct);
        }
        catch (Exception) when (ct.IsCancellationRequested == false)
        {
            // 连不上微软登录服务器本身（断网/DNS/超时等）：这不是"这个 refresh token 已失效"，
            // 是"这次根本没能问到答案"，标记为服务不可用，供调用方走令牌保留时效降级。
            LastRefreshFailureReason = RefreshFailureReason.ServiceUnavailable;
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            // 微软明确应答了（不是网络层失败），但拒绝了这次刷新：绝大多数情况下是 refresh
            // token 本身已经过期/被吊销/用户在别处撤销了授权，这是明确的"需要重新登录"，
            // 不应该走令牌保留时效降级（继续离线启动没有意义——正版身份确实已经失效）。
            // 5xx 状态码例外：那是微软服务端自己出了问题，同样按"服务不可用"处理。
            LastRefreshFailureReason = (int)resp.StatusCode >= 500
                ? RefreshFailureReason.ServiceUnavailable
                : RefreshFailureReason.TokenInvalid;
            return null;
        }
        var token = await resp.Content.ReadFromJsonAsync<MsaTokenResponse>(cancellationToken: ct);
        if (token == null) return null;

        // 根因修复（"明明是微软账户，进游戏却变成 Demo 试玩"）：微软的 refresh_token 授权端点
        // 并不保证每次都返回新的 refresh_token —— 很多时候它会省略这个字段，让客户端继续复用
        // 旧的那个。之前这里直接把 token.RefreshToken（可能是 null）传给 CompleteLoginChainAsync
        // 再原样写回 Account.MsRefreshToken，一旦某次刷新恰好没带新 refresh_token，就会用 null
        // 覆盖掉账户里本来还有效的旧 refresh_token 并持久化保存。下一次 access token 过期后，
        // MainWindow 启动前的检查（"!string.IsNullOrEmpty(account.MsRefreshToken)"）会因为它已经
        // 变成 null 而直接跳过刷新，转而拿着一个已过期的 access token 去启动游戏——Minecraft
        // 收到无效凭证时不会报错，而是静默降级成离线试玩(Demo)模式，现象上就是"账户管理里明明
        // 显示已登录微软账户，一进游戏却是 Demo"。
        // 修复：微软没给新的就沿用调用方传进来的旧 refreshToken，永远不用 null 覆盖已存在的值。
        var effectiveRefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? refreshToken : token.RefreshToken;
        return await CompleteLoginChainAsync(token.AccessToken, effectiveRefreshToken, ct);
    }

    /// <summary>
    /// 手动构造 JSON POST 请求，Content-Type 精确写成 "application/json"（不带 charset 后缀），
    /// 和已知能跑通的 JS 版本实现保持字节级一致，排除".NET 默认多加了 charset 参数导致服务器
    /// 行为不一致"这类可能性。返回状态码、完整响应头（拼成字符串，方便诊断时直接和另一个正常
    /// 工作的实现的抓包结果对比）、以及响应正文。
    /// </summary>
    private async Task<(System.Net.HttpStatusCode Status, string Body, string ResponseHeadersDump)> PostJsonRawAsync(
        string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); // 不带 charset，对齐 JS 实现
        using var resp = await _http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var headerLines = resp.Headers.Concat(resp.Content.Headers)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");
        var headersDump = string.Join(" | ", headerLines);
        return (resp.StatusCode, body, headersDump);
    }

    private async Task<Account> CompleteLoginChainAsync(string msaAccessToken, string? newRefreshToken, CancellationToken ct)
    {
        // 1) Xbox Live 认证
        var xblReq = new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"d={msaAccessToken}" },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        var (xblStatus, xblBody, xblHeaders) = await PostJsonRawAsync("https://user.auth.xboxlive.com/user/authenticate", xblReq, ct);
        if (xblStatus != System.Net.HttpStatusCode.OK)
        {
            // Xbox Live 接口即使报错，正常也一定会带 JSON 说明（例如 XErr 错误码）。
            // 如果 body 是空的，大概率不是 Xbox 服务器本身返回的错误，而是网络链路中间的
            // 设备（防火墙/运营商/校园网代理等）拦截或篡改了这次请求，伪造了一个空 400。
            var hint = string.IsNullOrWhiteSpace(xblBody)
                ? "（响应正文为空，这不是 Xbox Live 正常的报错格式。响应头：" + xblHeaders + "）"
                : "";
            throw new AuthStepException("Xbox Live 认证",
                $"HTTP {(int)xblStatus}: {xblBody} {hint}\n[诊断] RpsTicket 前缀长度={msaAccessToken.Length}, " +
                $"访问令牌前 12 位={msaAccessToken[..Math.Min(12, msaAccessToken.Length)]}...");
        }
        var xbl = JsonSerializer.Deserialize<XblResponse>(xblBody)
            ?? throw new AuthStepException("Xbox Live 认证", "响应解析失败: " + xblBody);

        // 2) XSTS 认证
        var xstsReq = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xbl.Token } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };
        var (xstsStatus, xstsBody, xstsHeaders) = await PostJsonRawAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsReq, ct);
        if (xstsStatus != System.Net.HttpStatusCode.OK)
        {
            // XSTS 常见错误码：2148916233(无 Xbox 账户) / 2148916235(所在地区不支持) / 2148916238(儿童账户需家长同意)
            var hint = xstsBody.Contains("2148916233") ? "（此微软账户没有关联的 Xbox 账户，请先到 https://www.xbox.com 创建一个）"
                : xstsBody.Contains("2148916235") ? "（你所在的地区/国家不支持 Xbox Live）"
                : xstsBody.Contains("2148916238") ? "（此账户是未成年账户，需要监护人在家庭组中添加同意）"
                : string.IsNullOrWhiteSpace(xstsBody) ? "（响应正文为空。响应头：" + xstsHeaders + "）"
                : "";
            throw new AuthStepException("XSTS 认证", $"HTTP {(int)xstsStatus}: {xstsBody} {hint}");
        }
        var xsts = JsonSerializer.Deserialize<XblResponse>(xstsBody)
            ?? throw new AuthStepException("XSTS 认证", "响应解析失败: " + xstsBody);
        var userHash = xsts.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs
            ?? throw new AuthStepException("XSTS 认证", "响应中缺少 uhs 字段: " + xstsBody);

        // 3) 用 Xbox 令牌换取 Minecraft access token
        var mcReq = new { identityToken = $"XBL3.0 x={userHash};{xsts.Token}" };
        var mcResp = await _http.PostAsJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcReq, ct);
        var mcBody = await mcResp.Content.ReadAsStringAsync(ct);
        if (!mcResp.IsSuccessStatusCode)
            throw new AuthStepException("Minecraft 服务登录", $"HTTP {(int)mcResp.StatusCode}: {mcBody}");
        var mcToken = JsonSerializer.Deserialize<MinecraftLoginResponse>(mcBody)
            ?? throw new AuthStepException("Minecraft 服务登录", "响应解析失败: " + mcBody);

        // 4) 获取玩家档案(用户名 + UUID)
        var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken.AccessToken);
        var profileResp = await _http.SendAsync(profileReq, ct);
        var profileBody = await profileResp.Content.ReadAsStringAsync(ct);
        if (!profileResp.IsSuccessStatusCode)
        {
            var hint = profileResp.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "（此微软账户名下没有正版 Minecraft，请确认购买/关联游戏的账户是否就是刚才登录的这个）"
                : "";
            throw new AuthStepException("获取游戏档案", $"HTTP {(int)profileResp.StatusCode}: {profileBody} {hint}");
        }
        var profile = JsonSerializer.Deserialize<MinecraftProfile>(profileBody)
            ?? throw new AuthStepException("获取游戏档案", "响应解析失败: " + profileBody);

        return new Account
        {
            Type = AccountType.Microsoft,
            Username = profile.Name,
            Uuid = FormatUuid(profile.Id),
            MsRefreshToken = newRefreshToken,
            MinecraftAccessToken = mcToken.AccessToken,
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(mcToken.ExpiresIn),
            IsSelected = true
        };
    }

    private static string FormatUuid(string raw)
    {
        if (raw.Contains('-')) return raw;
        return $"{raw[..8]}-{raw[8..12]}-{raw[12..16]}-{raw[16..20]}-{raw[20..32]}";
    }

    private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["client_id"] = _clientId, ["scope"] = Scope };
        var resp = await _http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var hint = body.Contains("unauthorized_client") || body.Contains("invalid_client")
                ? "（请检查 Azure 应用是否已开启 “允许公共客户端流” = 是，并且账户类型支持个人微软账户）"
                : "";
            throw new AuthStepException("请求登录代码", $"HTTP {(int)resp.StatusCode}: {body} {hint}");
        }
        return JsonSerializer.Deserialize<DeviceCodeResponse>(body)
            ?? throw new AuthStepException("请求登录代码", "响应解析失败: " + body);
    }

    private async Task<MsaTokenResponse> PollForTokenAsync(DeviceCodeResponse device, CancellationToken ct)
    {
        var interval = Math.Max(device.Interval, 5);
        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = device.DeviceCode
            };
            var resp = await _http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(form), ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
                return JsonSerializer.Deserialize<MsaTokenResponse>(json)
                    ?? throw new AuthStepException("获取登录令牌", "响应解析失败: " + json);

            using var doc = JsonDocument.Parse(json);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (error is "authorization_pending" or "slow_down") continue;

            var hint = error == "expired_token" ? "（登录代码已过期，请重新点击“添加微软账户”）"
                : error == "authorization_declined" ? "（登录已被拒绝/取消）"
                : "";
            throw new AuthStepException("获取登录令牌", $"error={error} {hint} 原始响应: {json}");
        }
        throw new AuthStepException("获取登录令牌", "登录代码已过期，请重新发起登录。");
    }

    // --- DTOs ---
    private class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "https://microsoft.com/link";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } = 900;
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
    }

    private class MsaTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }

    private class XblResponse
    {
        [JsonPropertyName("Token")] public string Token { get; set; } = "";
        [JsonPropertyName("DisplayClaims")] public DisplayClaims? DisplayClaims { get; set; }
    }
    private class DisplayClaims { [JsonPropertyName("xui")] public List<XuiEntry>? Xui { get; set; } }
    private class XuiEntry { [JsonPropertyName("uhs")] public string Uhs { get; set; } = ""; }

    private class MinecraftLoginResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } = 86400;
    }

    private class MinecraftProfile
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }
}
