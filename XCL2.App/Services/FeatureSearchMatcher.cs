using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Pure, deterministic matching for both launcher and toolbox search.</summary>
public static class FeatureSearchMatcher
{
    /// <summary>
    /// All whitespace-separated words must match. A null pageKey searches all pages;
    /// a concrete pageKey also includes the always-present main window controls.
    /// Empty queries return no results. Exact titles precede prefixes and keyword-only matches.
    /// Equal-ranked entries retain catalog order, so previous/next navigation is stable.
    /// </summary>
    public static IReadOnlyList<FeatureSearchEntry> Match(
        IEnumerable<FeatureSearchEntry> entries, string? query, string? pageKey = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0) return Array.Empty<FeatureSearchEntry>();
        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        var scope = Normalize(pageKey);
        return entries
            .Where(entry => scope.Length == 0 || Normalize(entry.PageKey) == scope || entry.PageKey == "main")
            .Select((entry, index) => new
            {
                Entry = entry,
                Index = index,
                Title = Normalize(entry.Title),
                Text = Normalize($"{entry.Title} {entry.Keywords} {entry.PageTitle} {entry.SectionTitle}")
            })
            .Where(item => words.All(word => item.Text.Contains(word, StringComparison.Ordinal)))
            .OrderBy(item => item.Title == normalizedQuery ? 0
                : item.Title.StartsWith(normalizedQuery, StringComparison.Ordinal) ? 1
                : words.All(word => item.Title.Contains(word, StringComparison.Ordinal)) ? 2 : 3)
            .ThenBy(item => item.Index)
            .Select(item => item.Entry).ToArray();
    }

    /// <summary>Width, case, and Unicode whitespace normalization without culture-dependent matching.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new StringBuilder(value.Length);
        var spacePending = false;
        foreach (var c in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(c)) { spacePending = builder.Length > 0; continue; }
            if (spacePending) builder.Append(' ');
            builder.Append(char.ToUpperInvariant(c));
            spacePending = false;
        }
        return builder.ToString();
    }
}
