using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// Offline metadata generated from built-in page XAML. Loading/searching this catalog never
/// creates pages, starts downloads, opens dialogs, or invokes control handlers.
/// </summary>
public static class FeatureSearchCatalog
{
    private static readonly Lazy<IReadOnlyList<FeatureSearchEntry>> RawEntries = new(ReadEmbedded);

    /// <summary>Resolve resources on every open so language changes do not leave stale result titles.</summary>
    public static IReadOnlyList<FeatureSearchEntry> Load() => Localize(RawEntries.Value, Loc.T);

    public static IReadOnlyList<FeatureSearchEntry> Localize(
        IEnumerable<FeatureSearchEntry> entries, Func<string, string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(translate);
        return entries.Select(entry =>
        {
            var page = PageLabel(entry.PageKey);
            var pageTitle = page.Key.Length == 0 ? page.Title : translate(page.Key, page.Title);
            var title = entry.ResourceKey is { Length: > 0 } key ? translate(key, entry.Title)
                : entry.Id == entry.PageKey + ".page" ? pageTitle : entry.Title;
            var section = entry.SectionResourceKey is { Length: > 0 } sectionKey
                ? translate(sectionKey, entry.SectionTitle) : entry.SectionTitle;
            return entry with
            {
                Title = title,
                // A label resolved from resources must also be the visible target text.
                TargetText = entry.TargetText == entry.Title ? title : entry.TargetText,
                PageTitle = pageTitle,
                SectionTitle = section,
                // Keep original-language labels as aliases when users switch launcher language.
                Keywords = $"{entry.Keywords} {entry.Title} {entry.PageTitle} {entry.SectionTitle}"
            };
        }).ToArray();
    }

    private static IReadOnlyList<FeatureSearchEntry> ReadEmbedded()
    {
        const string resource = "XCL2.App.Resources.Data.FeatureSearchCatalog.json";
        using var stream = typeof(FeatureSearchCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded feature search catalog: {resource}");
        var entries = JsonSerializer.Deserialize<FeatureSearchEntry[]>(stream)
            ?? throw new InvalidOperationException("The feature search catalog is empty.");
        return Array.AsReadOnly(entries);
    }

    private static (string Key, string Title) PageLabel(string pageKey) => pageKey switch
    {
        "home" => ("Str_Nav_Home", "主页"),
        "versions" => ("Str_Nav_Versions", "版本选择"),
        "downloads" => ("Str_Nav_Download", "下载"),
        "multiplayer" => ("Str_Nav_Multiplayer", "联机"),
        "mods" => ("Str_Nav_ModManager", "Mod 管理"),
        "worlds" => ("", "存档"),
        "servers" => ("Str_Nav_ServerManager", "服务端管理"),
        "accounts" => ("Str_Nav_Accounts", "账户管理"),
        "settings" => ("Str_Nav_Settings", "设置"),
        "logs" => ("Str_Nav_Logs", "日志"),
        "ai" => ("", "AI 助手"),
        "toolbox" => ("Str_Nav_Toolbox", "百宝箱"),
        "bedrock" => ("Str_Nav_Bedrock", "基岩版"),
        "about" => ("Str_Nav_AboutHelp", "鸣谢与帮助"),
        "api" => ("", "API 开发中心"),
        "more" => ("", "更多"),
        "main" => ("", "主窗口"),
        _ => ("", pageKey)
    };
}
