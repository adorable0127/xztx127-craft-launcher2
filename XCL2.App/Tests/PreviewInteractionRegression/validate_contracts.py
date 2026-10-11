#!/usr/bin/env python3
"""Repeatable source contracts for the complete Fluent preview; no Windows UI execution."""
from __future__ import annotations

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

from tree_sitter import Language, Parser
import tree_sitter_c_sharp

APP = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
P = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
PARSER = Parser(Language(tree_sitter_c_sharp.language()))
checks: list[dict] = []


def check(name: str, passed: bool, detail=None):
    item = {"name": name, "passed": bool(passed)}
    if detail is not None:
        item["detail"] = detail
    checks.append(item)


def walk(node):
    yield node
    for child in node.children:
        yield from walk(child)


def read(relative: str) -> str:
    return (APP / relative).read_text(encoding="utf-8-sig")


def methods(paths: list[Path]) -> dict[str, list[dict]]:
    result: dict[str, list[dict]] = {}
    for path in paths:
        data = path.read_bytes()
        tree = PARSER.parse(data)
        check("C# syntax: " + str(path.relative_to(APP)), not tree.root_node.has_error)
        for node in walk(tree.root_node):
            if node.type != "method_declaration":
                continue
            name = node.child_by_field_name("name")
            if name is None:
                continue
            body = node.child_by_field_name("body")
            calls = set()
            constructed_types = set()
            if body is not None:
                for expression in walk(body):
                    if expression.type == "invocation_expression":
                        function = expression.child_by_field_name("function")
                        if function is not None:
                            value = data[function.start_byte:function.end_byte].decode()
                            if re.fullmatch(r"(?:this\.)?\w+", value):
                                calls.add(value.removeprefix("this."))
                    elif expression.type == "object_creation_expression":
                        type_node = expression.child_by_field_name("type")
                        if type_node is not None:
                            constructed_types.add(data[type_node.start_byte:type_node.end_byte].decode())
            result.setdefault(data[name.start_byte:name.end_byte].decode(), []).append({
                "file": str(path.relative_to(APP)),
                "body": data[body.start_byte:body.end_byte].decode() if body else "",
                "source": data[node.start_byte:node.end_byte].decode(),
                "calls": calls,
                "constructed_types": constructed_types,
            })
    return result


def combined_body(collection: dict, name: str) -> str:
    return "\n".join(item["body"] for item in collection.get(name, []))


def style_key(element) -> str | None:
    if element.get(X + "Key"):
        return element.get(X + "Key")
    if element.tag == P + "Style" and element.get("TargetType"):
        target = element.get("TargetType")
        match = re.fullmatch(r"\{x:Type\s+([^}]+)\}", target)
        return "type:" + (match.group(1) if match else target).split(":")[-1]
    return None


def resource_keys(root) -> set[str]:
    # Include keys declared in templates/local ResourceDictionary scopes.
    return {key for element in root.iter() if (key := style_key(element))}


def referenced_static_resources(root) -> set[str]:
    references: set[str] = set()
    for element in root.iter():
        if element.tag == P + "StaticResource" and element.get("ResourceKey"):
            references.add(element.get("ResourceKey"))
        for value in element.attrib.values():
            for match in re.finditer(r"\{StaticResource\s+((?:\{x:Type\s+[^}]+\})|[^{}]+)\}", value):
                key = match.group(1).strip().removeprefix("ResourceKey=").strip("'\"")
                type_match = re.fullmatch(r"\{x:Type\s+([^}]+)\}", key)
                references.add("type:" + type_match.group(1).split(":")[-1] if type_match else key)
    return references


main_paths = sorted((APP / "Views").glob("MainWindow*.cs"))
home_paths = sorted((APP / "Views").glob("HomePage*.cs"))
main_methods = methods(main_paths)
home_methods = methods(home_paths)
theme_path = APP / "Services/PreviewFluentTheme.cs"
theme_methods = methods([theme_path])
main_source = "\n".join(path.read_text(encoding="utf-8-sig") for path in main_paths)
home_source = "\n".join(path.read_text(encoding="utf-8-sig") for path in home_paths)
preview_source = read("Views/MainWindow.WinUiPreview.cs")
theme_source = theme_path.read_text(encoding="utf-8-sig")

xml_paths = ("App.xaml", "Views/MainWindow.xaml", "Views/HomePage.xaml", "Resources/Themes/PreviewFluent.xaml")
trees = {}
for path in xml_paths:
    trees[path] = ET.parse(APP / path).getroot()
    check("XML parses: " + path, True)
main_tree = trees["Views/MainWindow.xaml"]
home_tree = trees["Views/HomePage.xaml"]
theme_tree = trees["Resources/Themes/PreviewFluent.xaml"]
main_names = {element.get(X + "Name") for element in main_tree.iter() if element.get(X + "Name")}
home_names = {element.get(X + "Name") for element in home_tree.iter() if element.get(X + "Name")}

# Validate event names across all existing partials, not merely the new partial.
event_attributes = {"Click", "Checked", "Unchecked", "Loaded", "Unloaded", "MouseLeftButtonDown", "MouseRightButtonDown", "KeyDown", "PreviewKeyDown", "TextChanged", "SelectionChanged", "SizeChanged", "Closed", "Closing", "StateChanged", "MouseMove", "MouseEnter", "MouseLeave", "ValueChanged", "ScrollChanged", "ContextMenuOpening", "LostFocus", "GotFocus", "IsVisibleChanged", "PreviewMouseWheel", "PreviewMouseLeftButtonDown", "MouseDoubleClick", "Opened", "DragOver", "Drop", "PreviewDragOver", "PreviewDrop"}
for filename, tree, collection in (("Views/MainWindow.xaml", main_tree, main_methods), ("Views/HomePage.xaml", home_tree, home_methods)):
    handlers = {value for element in tree.iter() for attr, value in element.attrib.items() if attr.split("}")[-1] in event_attributes and re.fullmatch(r"\w+", value)}
    missing = sorted(handlers - collection.keys())
    check("XAML routed-event handlers resolve: " + filename, not missing, missing or {"handler_count": len(handlers)})
for label, tree in (("MainWindow", main_tree), ("HomePage", home_tree)):
    all_names = [element.get(X + "Name") for element in tree.iter() if element.get(X + "Name")]
    # Template names have independent namescopes; only top-level page controls are required below.
    check(label + " contains required preview controls", (set(("MainContent", "TitleBarRow", "CustomTitleBar", "MaximizeRestoreButton", "NavPluginsButton", "NavPluginsIcon", "NavPluginsLabel")) if label == "MainWindow" else set(("PreviewToggle", "PreviewPluginSection", "PreviewPluginHeading", "PreviewPluginCards", "LaunchTile", "LanguageEntryButton"))) <= set(all_names))
for label, source, names, pattern in (
    ("MainWindow", preview_source, main_names, r"\bNav[A-Z]\w*(?:Button|Icon|Label)\b"),
    ("HomePage", read("Views/HomePage.Preview.cs"), home_names, r"\b(?:PreviewPlugin(?:Section|Heading|Cards)|\w+Tile|LanguageEntryButton)\b"),
):
    missing = sorted(set(re.findall(pattern, source)) - names)
    check("Preview partial's named controls exist in XAML: " + label, not missing, missing)

# Explicit application resources take precedence over merged dictionaries.
app_dict = trees["App.xaml"].find(P + "Application.Resources/" + P + "ResourceDictionary")
app_styles = {style_key(element) for element in app_dict if element.tag == P + "Style"}
theme_styles = {style_key(element) for element in theme_tree if element.tag == P + "Style"}
check("All 47 application style keys have Fluent replacements", len(app_styles) == 47 and app_styles <= theme_styles, {"application_style_count": len(app_styles), "preview_style_count": len(theme_styles), "missing": sorted(app_styles - theme_styles)})
check("Fluent theme replaces direct application entries", "resources[entry.Key] = entry.Value" in theme_source)
check("Disabling preview restores original application resources", "pair.Value.Existed" in theme_source and "resources.Remove(pair.Key)" in theme_source and "Replacements[pair.Value] = pair.Key" in theme_source)
check("Existing local StaticResource style objects are restyled", "ReadLocalValue(FrameworkElement.StyleProperty) is Style style" in theme_source and "element.Style = replacement" in theme_source)
check("Newly navigated pages receive Fluent resources", "PreviewFluentTheme.ApplyTo(page)" in combined_body(main_methods, "UpdatePreviewNavigationSelection"))
all_global_keys = resource_keys(trees["App.xaml"])
# Collect only declared merged dictionaries; unrelated page keys do not hide typos.
for dictionary in trees["App.xaml"].iter(P + "ResourceDictionary"):
    if dictionary.get("Source"):
        source_path = APP / dictionary.get("Source").replace("\\", "/")
        if source_path.exists():
            all_global_keys.update(resource_keys(ET.parse(source_path).getroot()))
for filename in ("Views/MainWindow.xaml", "Views/HomePage.xaml", "Resources/Themes/PreviewFluent.xaml"):
    available = all_global_keys | resource_keys(trees[filename]) | resource_keys(theme_tree)
    missing = sorted(referenced_static_resources(trees[filename]) - available)
    check("StaticResource keys resolve in local/application/theme declarations: " + filename, not missing, missing)

required_parts = {
    "PreviewTextBoxBase": {"PART_ContentHost"},
    "PasswordBox": {"PART_ContentHost"},
    "PreviewComboBoxBase": {"PART_EditableTextBox", "PART_Popup"},
    "ScrollBar": {"PART_Track", "PART_Thumb"},
    "ProgressBar": {"PART_Track", "PART_Indicator"},
    "TabControl": {"PART_SelectedContentHost"},
    "Slider": {"PART_Track"},
    "MenuItem": {"PART_Popup"},
}
for key, parts in required_parts.items():
    style = next((element for element in theme_tree if style_key(element) == (key if not key in {"PasswordBox", "ScrollBar", "ProgressBar", "TabControl", "Slider", "MenuItem"} else "type:" + key)), None)
    declared = {element.get(X + "Name") for element in style.iter()} if style is not None else set()
    check("Interactive control template retains parts: " + key, parts <= declared, {"required": sorted(parts), "missing": sorted(parts - declared)})
for template in theme_tree.iter(P + "ControlTemplate"):
    declared = {element.get(X + "Name") for element in template.iter() if element.get(X + "Name")}
    missing = sorted({element.get("TargetName") for element in template.iter() if element.get("TargetName")} - declared)
    check("Template trigger targets resolve: " + str(template.get("TargetType")), not missing, missing)

switch = combined_body(main_methods, "SetPreviewVisualMode")
hide_index = switch.find("if (wasVisible) Hide();")
theme_index = switch.find("PreviewFluentTheme.SetEnabled(enabled, this)")
show_index = switch.find("Show();", theme_index)
check("Old presentation hides before changing resources and showing preview", 0 <= hide_index < theme_index < show_index)
check("Preview switch does not restart process, close launcher or replace pages", not re.search(r"\b(?:Process\.Start|Shutdown|Close|MainContent\.Content\s*=|NavigateLazy)\s*\(?", switch))
check("Preview active flag is actual visual mode", "IsNativePreviewOpen => _previewVisualMode" in preview_source and "_previewVisualMode = enabled" in switch)
check("Config preserves enabled mode across launcher sessions", "ConfigService.Config.PreviewModeEnabled = enabled" in switch and "ConfigService.Config.PreviewModeEnabled || App.StartupArgs.EnterPreviewMode" in preview_source)
check("Switch failure restores previous theme and mode", "_previewVisualMode = previous" in switch and "PreviewFluentTheme.SetEnabled(previous, this)" in switch)
sync = combined_body(home_methods, "SyncNativePreview")
check("Preview toggle follows actual owner state and remains interactive", "PreviewToggle.IsChecked = _previewWindow != null || _owner.IsNativePreviewOpen" in sync and "PreviewToggle.IsEnabled = true" in sync)
check("Home subscribes to actual mode changes", "_owner.NativePreviewStateChanged += SyncNativePreview" in home_source and "_owner.NativePreviewStateChanged -= SyncNativePreview" in home_source)
check("Preview toggle handlers switch complete launcher mode", "_owner.OpenWinUiPreview()" in combined_body(home_methods, "OpenExperimentalPreview_Checked") and "_owner.CloseNativePreview()" in combined_body(home_methods, "OpenExperimentalPreview_Unchecked"))
toggle_style = next(element for element in theme_tree if style_key(element) == "PreviewToggleBase")
checked_triggers = [element for element in toggle_style.iter(P + "Trigger") if element.get("Property") == "IsChecked" and element.get("Value") == "True"]
check("Checked preview toggle has a visible accent background", any("AccentBrush" in (setter.get("Value") or "") and setter.get("Property") == "Background" for trigger in checked_triggers for setter in trigger.iter(P + "Setter")))
check("Window uses custom caption with native chrome behavior", main_tree.get("WindowStyle") == "None" and main_tree.find(".//{clr-namespace:System.Windows.Shell;assembly=PresentationFramework}WindowChrome") is not None)
check("Preview layout keeps custom title bar and window controls", "CustomTitleBar.Visibility" in combined_body(main_methods, "ApplyPreviewLayout") and "MaximizeRestoreButton.Visibility = Visibility.Visible" in combined_body(main_methods, "ApplyPreviewLayout") and "ResizeMode = ResizeMode.CanResize" in combined_body(main_methods, "ApplyPreviewLayout"))
check("F11 uses preview caption height and full-screen hides caption", "Key.F11" in main_source and "double captionHeight = isFullScreen ? 0 : IsNativePreviewOpen ? 40 : 32" in main_source and "WindowChromeService.IsFullScreen ? Visibility.Collapsed : Visibility.Visible" in combined_body(main_methods, "ApplyPreviewLayout"))

# All navigation remains wired to the original concrete business page/dialog entries.
navigation = {
    "NavHomeButton": ("NavHome_Click", "HomePage"),
    "NavVersionsButton": ("NavVersions_Click", "VersionSelectPage"),
    "NavDownloadButton": ("NavDownload_Click", "DownloadCenterPage"),
    "NavMultiplayerButton": ("NavMultiplayer_Click", "MultiplayerPage"),
    "NavWorldsButton": ("NavWorlds_Click", "WorldArchivePage"),
    "NavModManagerButton": ("NavModManager_Click", "ModManagerPage"),
    "NavServerManagerButton": ("NavServerManager_Click", "ServerManagerPage"),
    "NavToolboxButton": ("NavToolbox_Click", "ToolboxPage"),
    "NavBedrockButton": ("NavBedrock_Click", "BedrockPage"),
    "NavAboutHelpButton": ("NavAboutHelp_Click", "AboutHelpPage"),
    "NavAccountsButton": ("NavAccounts_Click", "LoginPage"),
    "NavSettingsButton": ("NavSettings_Click", "SettingsPage"),
    "NavMoreButton": ("NavMore_Click", "MorePage"),
    "NavLogsButton": ("NavLogs_Click", "LogsPage"),
    "NavAiAssistantButton": ("NavAiAssistant_Click", "AiAssistantPanel"),
    "NavExperimentalButton": ("NavExperimental_Click", "ExperimentalFeaturesWindow"),
    "NavPluginsButton": ("NavPlugins_Click", "PluginManagerDialog"),
}


def reaches_type(start: str, target: str) -> bool:
    pending = [start]
    visited = set()
    while pending:
        method = pending.pop()
        if method in visited:
            continue
        visited.add(method)
        for declaration in main_methods.get(method, []):
            if target in declaration["constructed_types"]:
                return True
            pending.extend(name for name in declaration["calls"] if name in main_methods and name not in visited)
    return False


navigation_report = []
for control, (handler, target) in navigation.items():
    element = next((element for element in main_tree.iter() if element.get(X + "Name") == control), None)
    wired = element is not None and element.get("Click") == handler and reaches_type(handler, target)
    check("Real navigation implementation reachable: " + control, wired, {"handler": handler, "target": target})
    navigation_report.append({"button": control, "handler": handler, "business_page_or_dialog": target, "reachable_from_handler": wired})
collapse = combined_body(main_methods, "ApplySidebarCollapsedState")
for suffix in ("ApiDocumentation", "Plugins"):
    expected = f"(Nav{suffix}Button, Nav{suffix}Icon, Nav{suffix}Label)"
    check("New sidebar entry supports icon-only collapse: " + suffix, expected in collapse)
for destination in ("home", "instances", "worlds", "settings", "ai", "downloads", "plugins"):
    check("Plugin home card routes to live functionality: " + destination, 'case "' + destination + '"' in combined_body(home_methods, "NavigatePreviewCard"))
check("Plugin home cards are actual clickable buttons", "button.Click += (_, _) => NavigatePreviewCard(card.Destination)" in home_source)

failed = [item for item in checks if not item["passed"]]
report = {
    "kind": "static_source_contract_validation",
    "checks": checks,
    "passed": len(checks) - len(failed),
    "failed": len(failed),
    "navigation": navigation_report,
    "limitations": [
        "Static contracts and C# grammar/XML parsing only; they do not prove C# semantic compilation or WPF XAML compilation.",
        "Windows build, visual rendering, mouse/keyboard interactions and end-to-end account/download/game/AI actions were not run in this Linux environment.",
        "The preview is Fluent/WinUI 3 visual styling of the complete existing WPF launcher, not replacement Microsoft.UI.Xaml controls.",
        "Control-template part names and navigation source reachability are checked; runtime behavior still requires Windows validation.",
    ],
    "windows_build": "NOT_RUN: .NET CoreCLR initialization fails (0x8007000E) and Windows UI is unavailable.",
}
(HERE / "validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"static_checks_passed": report["passed"], "failed": failed, "windows_runtime": "NOT_RUN"}, ensure_ascii=False))
sys.exit(1 if failed else 0)
