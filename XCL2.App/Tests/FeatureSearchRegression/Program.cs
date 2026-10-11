using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}

var entries = FeatureSearchCatalog.Load();
Check(entries.Count > 400, "catalog includes concrete launcher features, not just page links");
Check(entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == entries.Count,
    "destination ids are unique");
Check(entries.All(entry => entry.Title.Length > 0 && !entry.Title.StartsWith("{")),
    "resource expressions never appear as titles");
Check(FeatureSearchMatcher.Match(entries, null).Count == 0
    && FeatureSearchMatcher.Match(entries, " \t\n\u3000").Count == 0, "empty search has no arbitrary result");

var launch = FeatureSearchMatcher.Match(entries, "启动游戏");
Check(launch[0].TargetName == "LaunchGameBtn", "exact launch button precedes descriptive settings matches");
Check(FeatureSearchMatcher.Match(entries, " launch\tGAME ").Any(entry => entry.TargetName == "LaunchGameBtn"),
    "case-insensitive multiword English alias");
Check(FeatureSearchMatcher.Match(entries, "下载游戏").Any(entry => entry.TargetName == "CatVersion"),
    "download-game synonym targets the real game download category");
Check(FeatureSearchMatcher.Match(entries, "设置 下载 ＪＡＶＡ").Any(entry => entry.TargetName == "DownloadJavaBtn"),
    "multiple words may match page name and localized control, with full-width normalization");
Check(FeatureSearchMatcher.Match(entries, "设置 下载 ＪＡＶＡ impossible-word").Count == 0,
    "all words are required, rather than unrelated partial matches");
Check(FeatureSearchMatcher.Match(entries, "透明", "settings").Any(entry => entry.TargetName == "GlobalWindowOpacitySlider"),
    "named controls use real nearby labels");

var local = FeatureSearchMatcher.Match(entries, "工具", "toolbox");
Check(local.Count > 0 && local.All(entry => entry.PageKey is "toolbox" or "main"), "local scope excludes other pages");
Check(FeatureSearchMatcher.Match(entries, "启动游戏", "toolbox").Any(entry => entry.PageKey == "main"),
    "main window controls remain reachable from current-page scope");
Check(FeatureSearchMatcher.Match(entries, "显卡", "home").Count == 0,
    "home scope does not silently search the settings page");
Check(FeatureSearchMatcher.Match(entries, "显卡").Any(entry => entry.PageKey == "settings"),
    "all scope crosses page boundaries");

var toolboxTabs = entries.Where(entry => entry.Id.StartsWith("toolbox.tab.", StringComparison.Ordinal)).ToArray();
Check(toolboxTabs.Length == 36 && toolboxTabs.Select(entry => entry.TabIndex).SequenceEqual(Enumerable.Range(0, 36).Select(index => (int?)index)),
    "every toolbox tab has the exact zero-based navigation index");
Check(FeatureSearchMatcher.Match(toolboxTabs, "四则")[0].TabIndex == 5, "calculator synonym points to the calculator tab");
Check(FeatureSearchMatcher.Match(toolboxTabs, "ping")[0].TabIndex == 8, "ping synonym points to the server latency tab");
Check(entries.Single(entry => entry.TargetName == "ModSearchBox").SectionKey == "ModPanel",
    "custom download category metadata retains the correct hidden panel");

var sample = new[]
{
    new FeatureSearchEntry("keyword", "settings", "Other", "memory") { PageTitle = "Settings" },
    new FeatureSearchEntry("prefix", "settings", "Memory usage"),
    new FeatureSearchEntry("exact", "settings", "Memory"),
    new FeatureSearchEntry("stable", "settings", "Other", "memory"),
    new FeatureSearchEntry("excluded", "ai", "Memory")
};
Check(FeatureSearchMatcher.Match(sample, "memory", "settings").Select(entry => entry.Id)
    .SequenceEqual(new[] { "exact", "prefix", "keyword", "stable" }),
    "exact/prefix priority and stable equal-ranked ordering keep next/previous predictable");
Check(FeatureSearchMatcher.Match(sample, "memory MEMORY", " SETTINGS ").Count == 4,
    "repeated words and scope whitespace do not duplicate or lose results");

var raw = new[]
{
    new FeatureSearchEntry("localized", "settings", "下载 Java", "java runtime", "DownloadJavaBtn", "下载 Java", ResourceKey: "download")
    { PageTitle = "设置", SectionTitle = "Java 列表", SectionResourceKey = "section" }
};
var localized = FeatureSearchCatalog.Localize(raw, (key, fallback) => key switch
{
    "download" => "Download Java", "section" => "Java runtimes", "Str_Nav_Settings" => "Settings", _ => fallback
});
Check(localized[0].Title == "Download Java" && localized[0].TargetText == "Download Java"
    && localized[0].DisplayPath == "Settings › Java runtimes", "localized titles, target text and path stay aligned");
Check(FeatureSearchMatcher.Match(localized, "下载 Java").Count == 1,
    "original Chinese aliases still work after changing the launcher language");
Check(raw[0].Title == "下载 Java", "localization does not mutate cached source entries");
Check(FeatureSearchMatcher.Normalize("  ＪＡＶＡ\t架构\n") == "JAVA 架构", "Unicode width and whitespace normalize deterministically");

Console.WriteLine($"PASS: {checks} feature search regression checks; {entries.Count} indexed destinations.");
