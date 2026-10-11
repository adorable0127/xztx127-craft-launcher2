using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace XCL2.App.Services;

/// <summary>Fluent visual resources for existing, fully interactive WPF pages.</summary>
internal static class PreviewFluentTheme
{
    private sealed record SavedResource(bool Existed, object? Value);
    private static readonly Dictionary<object, SavedResource> Original = new();
    private static readonly Dictionary<Style, Style> EnabledStyles = new();
    private static readonly Dictionary<Style, Style> DerivedStyles = new();
    private static readonly Dictionary<Style, Style> Replacements = new();
    private static readonly Dictionary<Style, Style> StyleCache = new();
    // Detached cached pages (including the ordinary AI page) can return after
    // several toggles. Keep their original style without retaining dead pages.
    private static readonly ConditionalWeakTable<Style, Style> StyleOrigins = new();
    private static bool _enabled;
    internal static bool IsEnabled => _enabled;

    internal static void SetEnabled(bool enabled, FrameworkElement root)
    {
        root.Dispatcher.VerifyAccess();
        if (_enabled == enabled) return;
        var application = Application.Current ?? throw new InvalidOperationException("Application is not initialized.");
        var resources = application.Resources;
        Replacements.Clear(); StyleCache.Clear();
        if (enabled)
        {
            var dictionary = new ResourceDictionary { Source = new Uri("/XCL2;component/Resources/Themes/PreviewFluent.xaml", UriKind.Relative) };
            // ThemeService normally adds this rule once. A preview replaces the
            // implicit styles, so retain the same dynamic font rule on each one.
            foreach (var key in dictionary.Keys.Cast<object>().ToArray())
            {
                if (key is Type type && dictionary[key] is Style visualStyle)
                {
                    var fontStyle = new Style(type, visualStyle);
                    fontStyle.Setters.Add(new Setter(System.Windows.Controls.Control.FontFamilyProperty,
                        new DynamicResourceExtension("AppFontFamily")));
                    dictionary[key] = fontStyle;
                }
            }
            Original.Clear(); EnabledStyles.Clear(); DerivedStyles.Clear();
            foreach (DictionaryEntry entry in dictionary)
            {
                bool existed = resources.Contains(entry.Key);
                var previous = existed ? resources[entry.Key] : null;
                Original[entry.Key] = new SavedResource(existed, previous);
                if (previous is Style before && entry.Value is Style after)
                {
                    EnabledStyles[before] = after;
                    if (!StyleOrigins.TryGetValue(after, out _)) StyleOrigins.Add(after, before);
                }
            }
            // App.xaml owns direct entries; merged dictionaries cannot override them.
            foreach (DictionaryEntry entry in dictionary) resources[entry.Key] = entry.Value;
            foreach (var pair in EnabledStyles) Replacements[pair.Key] = pair.Value;
        }
        else
        {
            foreach (var pair in Original)
            {
                if (pair.Value.Existed) resources[pair.Key] = pair.Value.Value;
                else resources.Remove(pair.Key);
            }
            foreach (var pair in EnabledStyles) Replacements[pair.Value] = pair.Key;
            foreach (var pair in DerivedStyles) Replacements[pair.Key] = pair.Value;
        }
        _enabled = enabled;
        ApplyTo(root);
    }

    internal static void ApplyTo(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            // Keep dynamic resource references and style bindings intact.
            if (current is FrameworkElement element && element.ReadLocalValue(FrameworkElement.StyleProperty) is Style style)
            {
                var replacement = Resolve(style);
                if (!ReferenceEquals(style, replacement)) element.Style = replacement;
            }
            if (current is Popup { Child: { } child }) pending.Push(child);
            if (current is Visual or Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) pending.Push(VisualTreeHelper.GetChild(current, i));
            if (current is FrameworkElement or FrameworkContentElement)
                foreach (var logical in LogicalTreeHelper.GetChildren(current))
                    if (logical is DependencyObject dependency) pending.Push(dependency);
        }
    }

    private static Style Resolve(Style style)
    {
        if (Replacements.TryGetValue(style, out var replacement)) return replacement;
        if (StyleCache.TryGetValue(style, out var cached)) return cached;
        if (StyleOrigins.TryGetValue(style, out var original))
        {
            var current = _enabled ? Resolve(original) : original;
            StyleCache[style] = current;
            return current;
        }
        if (style.BasedOn is not { } previousBase) return style;
        var nextBase = Resolve(previousBase);
        if (ReferenceEquals(previousBase, nextBase)) return style;
        var derived = new Style(style.TargetType, nextBase);
        foreach (var setter in style.Setters) derived.Setters.Add(setter);
        foreach (var trigger in style.Triggers) derived.Triggers.Add(trigger);
        foreach (DictionaryEntry resource in style.Resources) derived.Resources[resource.Key] = resource.Value;
        foreach (var dictionary in style.Resources.MergedDictionaries) derived.Resources.MergedDictionaries.Add(dictionary);
        StyleCache[style] = derived;
        if (_enabled)
        {
            DerivedStyles[derived] = style;
            StyleOrigins.Add(derived, style);
        }
        return derived;
    }
}
