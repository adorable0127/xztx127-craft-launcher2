using System.Security.Cryptography;
using System.Text;

namespace XCL2.App.Services;

/// <summary>
/// "初步测试版功能"（目前唯一的子功能：多加载器合装）的准入校验。
///
/// 需求原话："这个功能需要一个 user token ID 才能正常使用...如果不输入就无法继续启动...
/// 把这个ID以哈希形式存储在代码里"——这里不存明文 token，只存它的 SHA-256 哈希值，
/// 校验时把用户输入同样跑一遍哈希再逐字节比较，跟存明文比较相比，即使有人反编译/看到源码，
/// 也不能从哈希值直接反推出原始 token（除非能碰撞出一个哈希相同的字符串，SHA-256 目前
/// 没有已知的实际可行碰撞攻击）。
///
/// 需求原话："ID (不区分大小写)"——校验前先把用户输入统一转成小写再取哈希，跟下面
/// 存的哈希值（对应 token 全小写形式算出的哈希）比较，这样无论用户输入时字母是大写、
/// 小写还是大小写混合，只要字符本身对得上，都能通过校验。
/// </summary>
public static class TokenGateService
{
    /// <summary>
    /// "实验性内容1：安装多个加载器"功能对应的 user token ID 的 SHA-256 哈希值
    /// （对 token 转小写后的 UTF-8 字节做哈希）。
    /// 原始 token 本身不出现在代码/注释里的任何位置；开发者本人留档的原文备份
    /// 见项目根目录 tokenid.md（该文件不应发给他人或提交到公开仓库）。
    /// </summary>
    private const string MultiLoaderTokenSha256 =
        "8e2778fe54ab35994176c122ccdfdc08eccde0caa3262042719be4c564bafd52";

    /// <summary>
    /// 校验用户输入的 token 是否匹配"安装多个加载器"功能要求的 token。
    /// 空输入/空白输入直接判定不通过，不会去跟哈希空字符串比较——避免有人误以为"什么都不填"
    /// 也能蒙混过关。
    /// </summary>
    public static bool ValidateMultiLoaderToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;

        var normalized = input.Trim().ToLowerInvariant();
        var actualHash = ComputeSha256Hex(normalized);

        // 用固定时间比较而不是 ==，避免理论上的计时攻击（虽然对一个本地单机启动器来说
        // 实际风险几乎为零，但这个模式本来就是校验密钥类输入时的标准写法，顺手用上）。
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actualHash),
            Convert.FromHexString(MultiLoaderTokenSha256));
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
