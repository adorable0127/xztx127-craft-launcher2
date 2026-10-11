namespace XCL2.App.Models;

/// <summary>A searchable destination. Selecting an entry navigates to a control; it never executes it.</summary>
public sealed record FeatureSearchEntry(
    string Id,
    string PageKey,
    string Title,
    string Keywords = "",
    string? TargetName = null,
    string? TargetText = null,
    int? TabIndex = null,
    string? ResourceKey = null)
{
    public string PageTitle { get; init; } = "";
    public string SectionTitle { get; init; } = "";
    public string? SectionResourceKey { get; init; }
    public string? SectionKey { get; init; }
    public string DisplayPath => string.IsNullOrWhiteSpace(SectionTitle) || SectionTitle == PageTitle
        ? PageTitle : $"{PageTitle} › {SectionTitle}";
}
