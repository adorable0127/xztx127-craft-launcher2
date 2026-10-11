#!/usr/bin/env python3
"""Check generated catalog routes, controls, resources and toolbox tab indices without Windows."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET

from generate_catalog import APP, PAGES, X, build

path = APP / "Resources/Data/FeatureSearchCatalog.json"
entries = json.loads(path.read_text(encoding="utf-8"))
assert entries == build(), "generated search catalog is stale"
assert len(entries) > 400
assert len({entry["Id"] for entry in entries}) == len(entries)
routes = {page[0]: file for file, page in PAGES.items()}
routes.update(worlds="WorldArchivePage", api="PluginApiDocumentationPage")
assert all(entry["PageKey"] in routes for entry in entries)
trees = {page[0]: ET.parse(APP / "Views" / (file + ".xaml")).getroot() for file, page in PAGES.items()}
for entry in entries:
    assert entry["Title"] and not entry["Title"].startswith("{"), entry["Id"]
    if entry.get("TargetName"):
        names = {element.get(X + "Name") for element in trees[entry["PageKey"]].iter()}
        assert entry["TargetName"] in names, entry["Id"]
    if entry.get("SectionKey"):
        assert any(element.get(X + "Name") == entry["SectionKey"] for element in trees[entry["PageKey"]].iter()), entry["Id"]
tabs = [entry for entry in entries if entry["Id"].startswith("toolbox.tab.")]
assert [entry["TabIndex"] for entry in tabs] == list(range(36))
assert any(entry.get("TargetName") == "LaunchGameBtn" and entry["PageKey"] == "main" for entry in entries)
assert any(entry.get("TargetName") == "CatVersion" and "下载游戏" in entry["Keywords"] for entry in entries)
assert sum(entry["PageKey"] == "settings" for entry in entries) >= 100
assert any(entry.get("TargetName") == "GlobalWindowOpacitySlider" for entry in entries)
assert any(entry.get("TargetName") == "ShowFeatureSearchCheck" and entry["PageKey"] == "settings" for entry in entries)
print(f"PASS: {len(entries)} catalog destinations; exact named controls, routes, category panels and 36 tab indices verified.")
