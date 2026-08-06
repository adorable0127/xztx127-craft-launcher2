using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 离线登录：无需网络、无需账号密码，直接用用户名生成一个稳定的离线 UUID
/// （与官方离线模式规则一致：UUID = MD5("OfflinePlayer:" + name)，并设置版本位为 3）。
/// </summary>
public static class OfflineAuthService
{
    public static Account CreateOfflineAccount(string username)
    {
        var uuid = GenerateOfflineUuid(username);
        return new Account
        {
            Type = AccountType.Offline,
            Username = username,
            Uuid = uuid,
            IsSelected = true
        };
    }

    public static string GenerateOfflineUuid(string username)
    {
        using var md5 = MD5.Create();
        var input = Encoding.UTF8.GetBytes("OfflinePlayer:" + username);
        var hash = md5.ComputeHash(input);

        // 设置版本(3)与变体位，符合 UUID v3 规范（离线模式约定）
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
