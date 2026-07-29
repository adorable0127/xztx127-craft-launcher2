namespace XCL2.App.Models;

public enum AccountType
{
    Offline,
    Microsoft
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

    public string DisplayLabel => Type == AccountType.Microsoft
        ? $"{Username} (微软账户)"
        : IsGuest ? $"{Username} (访客)" : $"{Username} (离线账户)";
}

/// <summary>离线账户的皮肤来源：不设置、内置史蒂夫/艾利克斯、或用户自定义上传。</summary>
public enum OfflineSkinType
{
    None,
    Steve,
    Alex,
    Custom
}
