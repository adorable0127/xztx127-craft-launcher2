namespace XCL2.App.Models;

public enum AccountType
{
    Offline,
    Microsoft,

    /// <summary>
    /// 通过第三方认证服务器（Yggdrasil 协议兼容的"皮肤站"，例如基于 authlib-injector 的
    /// 统一通行证/blessing-skin 类服务）登录的账户。跟 Offline/Microsoft 是完全独立的第三种
    /// 类型，不复用/不影响原有两种账户类型的任何处理逻辑——LauncherService、SkinService、
    /// LoginPage 里所有原来 "account.Type == AccountType.Offline" / "== AccountType.Microsoft"
    /// 的判断分支保持原样不动，这个新类型只在各自新增的 else/switch 分支里被处理。
    /// </summary>
    AuthServer
}

/// <summary>
/// 单个账户记录。离线账户与微软账户共存于同一列表中，
/// 通过 Type 区分。微软账户的 RefreshToken 用于免登录刷新。
/// </summary>
public class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountType Type { get; set; } = AccountType.Offline;

    /// <summary>游戏内显示的用户名</summary>
    public string Username { get; set; } = "Player";

    /// <summary>
    /// 玩家 UUID（离线账户由用户名哈希生成，微软账户由 Xbox/Minecraft 服务返回，
    /// 例如用户提供的 b41a829c-4710-4954-a6de-814923dca264）
    /// </summary>
    public string Uuid { get; set; } = "";

    /// <summary>微软账户：用于免重复登录刷新会话的 refresh token（本地加密存储）</summary>
    public string? MsRefreshToken { get; set; }

    /// <summary>微软账户：最近一次获取的 Minecraft access token（有效期内可直接复用）</summary>
    public string? MinecraftAccessToken { get; set; }

    /// <summary>access token 过期时间（UTC），到期前会自动用 refresh token 静默刷新</summary>
    public DateTime? AccessTokenExpiresAtUtc { get; set; }

    /// <summary>
    /// 认证服务器（皮肤站）账户专用：登录时使用的 Yggdrasil API 根地址（例如
    /// "https://example.com/api/yggdrasil"），游戏启动时会用这个地址给
    /// authlib-injector 挂 -javaagent，让客户端向这个地址而不是 Mojang 官方服务器
    /// 请求会话校验/皮肤，这样才能真正以这个账户的身份、皮肤进入游戏。
    /// 只对 Type=AuthServer 有意义。
    /// </summary>
    public string? AuthServerApiRoot { get; set; }

    /// <summary>
    /// 认证服务器（皮肤站）账户专用：登录时服务端生成/返回的 clientToken，
    /// 用于以后调用 /refresh 静默刷新会话而不需要用户重新输入密码。
    /// 只对 Type=AuthServer 有意义。
    /// </summary>
    public string? AuthServerClientToken { get; set; }

    /// <summary>是否为当前选中使用的账户</summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// 是否为"访客模式"临时账户：这类账户只存在于内存中，不会被
    /// ConfigService.AddOrUpdateAccount/SaveAccounts 写入 accounts.json，
    /// 启动器关闭后自动消失，不留下任何记录。见 GuestModeService。
    /// </summary>
    public bool IsGuest { get; set; }

    /// <summary>
    /// 离线皮肤类型：None(不设置/使用游戏默认)、Steve、Alex、Custom(自定义上传的皮肤文件)。
    /// 只对离线账户有意义——微软账户的皮肤由 Mojang 服务器托管，启动器不需要也不应该干预。
    /// </summary>
    public OfflineSkinType SkinType { get; set; } = OfflineSkinType.None;

    /// <summary>
    /// 自定义皮肤文件在本地的存放路径（SkinType=Custom 时有效），指向
    /// xcl2/skins/&lt;accountId&gt;.png。史蒂夫/艾利克斯是内置资源，不需要这个字段。
    /// </summary>
    public string? CustomSkinPath { get; set; }

    /// <summary>
    /// 自定义皮肤是否为"纤细手臂"模型（Alex 骨架，对应 authlib-injector 的 slim 变体）。
    /// 只在 SkinType=Custom 时有意义；上传时由用户选择。
    /// </summary>
    public bool CustomSkinSlim { get; set; }

    public string DisplayLabel => Type switch
    {
        AccountType.Microsoft => $"{Username} (微软账户)",
        AccountType.AuthServer => $"{Username} (认证服务器账户)",
        _ => IsGuest ? $"{Username} (访客)" : $"{Username} (离线账户)"
    };
}

/// <summary>离线账户的皮肤来源：不设置、内置史蒂夫/艾利克斯、或用户自定义上传。</summary>
public enum OfflineSkinType
{
    None,
    Steve,
    Alex,
    Custom
}
