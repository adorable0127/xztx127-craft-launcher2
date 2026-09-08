using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 根据用户在设置里选的 <see cref="ModFileNamingStyle"/>，把"中文名"和"原始文件名"拼成
/// 下载完落地时实际使用的文件名。只负责拼字符串，不碰磁盘——调用方负责实际改名/写文件。
/// </summary>
public static class ModFileNamingHelper
{
    /// <summary>
    /// 拼出最终文件名（含扩展名）。
    /// chineseName 为空（没查到中文名，或用户选了 Original 样式）时，原样返回 originalFileName，
    /// 不留下"【】"或多余的"-"这类空壳前后缀。
    /// </summary>
    public static string BuildFileName(string originalFileName, string? chineseName, ModFileNamingStyle style)
    {
        if (string.IsNullOrWhiteSpace(chineseName) || style == ModFileNamingStyle.Original)
            return originalFileName;

        var ext = Path.GetExtension(originalFileName);
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);

        var combined = style switch
        {
            ModFileNamingStyle.FullBracket => $"【{chineseName}】{baseName}",
            ModFileNamingStyle.SquareBracket => $"[{chineseName}] {baseName}",
            ModFileNamingStyle.DashPrefix => $"{chineseName}-{baseName}",
            ModFileNamingStyle.DashSuffix => $"{baseName}-{chineseName}",
            _ => baseName,
        };

        return combined + ext;
    }
}
