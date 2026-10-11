using System.Text.RegularExpressions;
using XCL2.Preview;

namespace XCL2.App.Services.Plugins;

/// <summary>API 2.0 native WinUI preview contributions. No WPF controls or arbitrary XAML cross processes.</summary>
public sealed class PluginPreviewService
{
    private readonly PluginContext _context;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (PluginContext Context, PreviewCard Card)> Cards = new();
    private static readonly Dictionary<string, (PluginContext Context, PreviewAppearance Appearance)> Appearances = new();
    internal PluginPreviewService(PluginContext context) { _context = context; }
    public IDisposable RegisterCard(string id, string title, string description, string destination = "worlds", string accent = "#1BD96A")
    {
        Demand(); Validate(id, title, description, accent);
        if (destination is not ("home" or "instances" or "worlds" or "settings" or "ai" or "downloads" or "plugins")) throw new ArgumentException("不支持的预览导航目标。");
        var key = _context.PluginId + ":" + id;
        var card = new PreviewCard(key, _context.Metadata.Name, title, description, destination, accent);
        lock (Gate)
        {
            if (Cards.Count >= 64 && !Cards.ContainsKey(key)) throw new InvalidOperationException("预览卡片数量达到上限。");
            if (Cards.ContainsKey(key)) throw new InvalidOperationException("卡片 ID 已注册；请用 UpdateCard 更新。");
            Cards.Add(key, (_context, card));
        }
        return _context.Track(new PluginRegistration(() => { lock (Gate) Cards.Remove(key); }));
    }
    public void UpdateCard(string id, string title, string description, string accent = "#1BD96A")
    {
        Demand(); Validate(id, title, description, accent); var key = _context.PluginId + ":" + id;
        lock (Gate)
        { if (!Cards.TryGetValue(key, out var value)) throw new KeyNotFoundException(id); Cards[key] = (_context, value.Card with { Title = title, Description = description, Accent = accent }); }
    }
    public IDisposable SetAppearance(string title, string accent = "#1BD96A", bool compact = false)
    {
        Demand(); Validate("appearance", title, "", accent); var key = _context.PluginId;
        var appearance = new PreviewAppearance(title, accent, compact);
        lock (Gate) Appearances[key] = (_context, appearance);
        return _context.Track(new PluginRegistration(() => { lock (Gate) if (Appearances.TryGetValue(key, out var current) && ReferenceEquals(current.Appearance, appearance)) Appearances.Remove(key); }));
    }
    private void Demand()
    { _context.Permissions.Demand(PluginPermissionNames.UiPreview); if (ConfigService.Active?.Config.EnablePreviewPluginApi != true) throw new InvalidOperationException("请先由用户在 API 设置中开启 WinUI 3 预览扩展。"); }
    private static void Validate(string id, string title, string description, string accent)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || string.IsNullOrWhiteSpace(title) || title.Length > 80 || description.Length > 1000 || !Regex.IsMatch(accent, "\\A#[0-9A-Fa-f]{6}\\z")) throw new ArgumentException("预览参数无效。");
    }
    internal static (PreviewCard[] Cards, PreviewAppearance Appearance) Snapshot()
    {
        if (ConfigService.Active?.Config.EnablePreviewPluginApi != true) return (Array.Empty<PreviewCard>(), new());
        lock (Gate)
        {
            bool Allowed(PluginContext context) => context.Permissions.Has(PluginPermissionNames.UiPreview);
            var appearance = Appearances.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => Allowed(p.Value.Context)).Select(p => p.Value.Appearance).LastOrDefault() ?? new();
            return (Cards.Values.Where(c => Allowed(c.Context)).Select(c => c.Card).ToArray(), appearance);
        }
    }
}
