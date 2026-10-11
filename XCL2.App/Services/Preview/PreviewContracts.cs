namespace XCL2.Preview;

public sealed record PreviewInstance(string Name, string Root, string Version, string Loader, bool Installed, bool Selected);
public sealed record PreviewCard(string Id, string Plugin, string Title, string Description, string Destination, string Accent);
public sealed record PreviewAppearance(string Title = "XCL2", string Accent = "#1BD96A", bool Compact = false);
public sealed record PreviewSnapshot(IReadOnlyList<PreviewInstance> Instances, IReadOnlyList<PreviewCard> Cards,
    PreviewAppearance Appearance, bool PreviewApiEnabled, bool WorldsApiEnabled, IReadOnlyList<string> GameRoots);
public sealed record PreviewRequest(string Action, string? Page = null, string? Root = null, string? Version = null,
    bool? PreviewApiEnabled = null, bool? WorldsApiEnabled = null);
