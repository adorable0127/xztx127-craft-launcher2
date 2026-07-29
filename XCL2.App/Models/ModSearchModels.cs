namespace XCL2.App.Models;

/// <summary>下载中心「Mod」分类的数据源选择。综合 = 同时查 Modrinth + CurseForge（MC百科结果单独展示，
/// 因为它没有直接下载能力，混进统一下载列表会误导用户点了却装不上）。</summary>
public enum ModSource
{
    Combined,
    Modrinth,
    CurseForge
}

/// <summary>统一的 Mod 搜索结果条目，屏蔽 Modrinth/CurseForge 数据结构差异，供 UI 统一绑定展示。</summary>
public class UnifiedModItem
{
    public ModSource Source { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }

    /// <summary>是否显示图标，由页面根据 AppConfig.ShowModIcons 在结果加载完成/设置变更时统一赋值
    /// （不用 XAML 转换器绑定 Config 是因为项目里目前没有 IValueConverter 基础设施，这样更省事，
    /// 也不用给每个 DataTemplate 单独接一份 Converter 资源）。默认 true，跟 ShowModIcons 默认值一致。</summary>
    public bool ShowIcon { get; set; } = true;

    /// <summary>实际绑定给 Image.Source 用的值：关闭图标显示、或者没有图标 URL 时给 null，
    /// WPF 的 Image 控件在 Source 为 null 时不会渲染任何内容（不会报错/不会显示裂图占位）。</summary>
    public string? DisplayIconUrl => ShowIcon ? IconUrl : null;

    /// <summary>Modrinth 用 project_id (字符串)，CurseForge 用 modId (数字)，两种来源用同一个字段存字符串形式，
    /// 具体下载时靠 Source 判断走哪条链路重新按 id 查询。</summary>
    public string SourceId { get; set; } = "";

    public string SourceLabel => Source switch
    {
        ModSource.Modrinth => "Modrinth",
        ModSource.CurseForge => "CurseForge",
        _ => ""
    };

    /// <summary>原始对象引用，方便下载时不用重新反查（Modrinth: ModrinthSearchHit；CurseForge: CurseForgeMod）。</summary>
    public object? RawItem { get; set; }
}

/// <summary>
/// 统一的材质包/光影包/数据包搜索结果条目，跟 UnifiedModItem 是同一个思路：屏蔽 Modrinth/CurseForge
/// 数据结构差异，供「下载中心」资源面板统一绑定展示。之前这个面板只查 Modrinth，没有这个类型，
/// 现在补上 CurseForge 结果后需要一个统一的形状。
/// </summary>
public class UnifiedResourceItem
{
    public ModSource Source { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? IconUrl { get; set; }
    public long Downloads { get; set; }

    /// <summary>是否显示图标，规则同 UnifiedModItem.ShowIcon。</summary>
    public bool ShowIcon { get; set; } = true;
    public string? DisplayIconUrl => ShowIcon ? IconUrl : null;

    /// <summary>Modrinth 用 project_id，CurseForge 用 modId 的字符串形式。</summary>
    public string SourceId { get; set; } = "";

    public string SourceLabel => Source switch
    {
        ModSource.Modrinth => "Modrinth",
        ModSource.CurseForge => "CurseForge",
        _ => ""
    };

    /// <summary>原始对象引用：Modrinth: ModrinthSearchHit；CurseForge: CurseForgeMod。</summary>
    public object? RawItem { get; set; }
}
