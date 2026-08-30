using System.IO;

namespace XCL2.App.Services;

/// <summary>
/// 负责读取本地保存的 CurseForge API Key。
///
/// 故意不放进 config.json：config.json 有时会被用户手动打开编辑、复制粘贴发给别人求助排查问题，
/// 一旦混在里面很容易被截图/贴聊天时意外泄露（就像这次一样）。单独用一个纯文本文件存放，
/// 文件名和用途一目了然，用户自己管理起来也更清楚"这个文件不能给别人看"。
///
/// Key 文件路径：xcl2/curseforge_key.txt，只有一行，内容就是 key 本身（前后空白会被去除）。
/// 文件不存在或读取失败时，CurseForge 相关功能（目前主要是"地图"分类）会被上层调用方
/// 自动禁用/隐藏，而不是抛异常导致整个下载中心打不开——参照之前 FavoriteVersionIds 反序列化
/// 空引用崩溃的教训，任何"用户本地环境可能缺失的可选数据"都不该用会崩溃的方式处理。
/// </summary>
public class CurseForgeKeyService
{
    public string KeyFilePath { get; }

    /// <summary>
    /// 内置 key（测试阶段临时写死，正式发布前必须替换为从加密仓库/构建流程注入，
    /// 不要把这个值带入面向公众发布的版本）。
    /// </summary>
    private const string BuiltInKey = "$2a$10$AELa495vU.ZJ6rstJOuSb.gMHCd0R/USkAkXgdfT.AbuA45Od63xm";

    public CurseForgeKeyService()
    {
        KeyFilePath = Path.Combine(App.DataDir, "curseforge_key.txt");
    }

    /// <summary>内置 key 的读取入口，按需替换实现（解密/环境变量等）。</summary>
    private static string? TryGetBuiltInKey()
    {
        return string.IsNullOrWhiteSpace(BuiltInKey) ? null : BuiltInKey.Trim();
    }

    /// <summary>
    /// 读取生效的 key：优先使用用户本地保存的 key（用户自己配置过，视为更明确的意图），
    /// 否则回退到内置 key；都没有则返回 null（不抛异常）。
    /// </summary>
    public string? TryGetKey()
    {
        try
        {
            if (File.Exists(KeyFilePath))
            {
                var content = File.ReadAllText(KeyFilePath).Trim();
                if (!string.IsNullOrEmpty(content)) return content;
            }
        }
        catch
        {
            // 忽略本地文件读取失败，继续尝试内置 key
        }

        return TryGetBuiltInKey();
    }

    /// <summary>是否已配置 key（用于 UI 判断要不要显示"未配置"提示）。</summary>
    public bool HasKey() => TryGetKey() != null;

    /// <summary>把 key 写入本地文件（用户在设置页粘贴 key 后调用）。</summary>
    public void SaveKey(string key)
    {
        Directory.CreateDirectory(App.DataDir);
        File.WriteAllText(KeyFilePath, key.Trim());
    }

    /// <summary>清空已保存的 key（用户想撤销时）。</summary>
    public void ClearKey()
    {
        try
        {
            if (File.Exists(KeyFilePath)) File.Delete(KeyFilePath);
        }
        catch { /* 忽略删除失败 */ }
    }
}
