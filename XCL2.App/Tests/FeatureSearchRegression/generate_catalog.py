#!/usr/bin/env python3
"""Regenerate the offline feature index from real XAML without constructing WPF pages.

Run from any directory: python Tests/FeatureSearchRegression/generate_catalog.py
Use --check in static validation. Templates, popup menus and runtime item rows are
deliberately excluded: those are not unconditional launcher destinations.
"""
import argparse
import collections
import json
import re
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
PAGES = {
    "HomePage": ("home", "主页", "首页 磁贴 主页面 home"),
    "VersionSelectPage": ("versions", "版本选择", "版本管理 加载器 选择游戏 version"),
    "DownloadCenterPage": ("downloads", "下载", "下载中心 download install"),
    "MultiplayerPage": ("multiplayer", "联机", "多人 局域网 陶瓦 联机大厅 multiplayer"),
    "ModManagerPage": ("mods", "Mod 管理", "本地模组 插件 mods"),
    "ServerManagerPage": ("servers", "服务端管理", "开服 服务器 server"),
    "LoginPage": ("accounts", "账户管理", "帐号 登录 正版 离线 account login"),
    "SettingsPage": ("settings", "设置", "选项 配置 settings preferences"),
    "LogsPage": ("logs", "日志", "崩溃 错误 报错 log crash"),
    "AiAssistantPanel": ("ai", "AI 助手", "人工智能 聊天 对话 assistant"),
    "ToolboxPage": ("toolbox", "百宝箱", "工具箱 小工具 toolbox tools"),
    "BedrockPage": ("bedrock", "基岩版", "基岩版启动 Bedrock"),
    "AboutHelpPage": ("about", "鸣谢与帮助", "关于 帮助 鸣谢 about help"),
    "MorePage": ("more", "更多", "简洁模式 更多功能 more"),
    "MainWindow": ("main", "主窗口", "主界面 侧边栏 main"),
}
LANG = {
    element.attrib[X + "Key"]: "".join(element.itertext()).strip()
    for element in ET.parse(APP / "Resources/Lang/Lang.zh-Hans.xaml").getroot()
    if X + "Key" in element.attrib
}
CONTROLS = {"Button", "ToggleButton", "RadioButton", "CheckBox", "ComboBox", "TextBox", "Slider", "Expander"}
SKIP_PARENTS = {"Style", "ControlTemplate", "DataTemplate", "ItemsPanelTemplate", "Popup", "ContextMenu"}
DOWNLOAD_PANELS = {"VersionPanel", "ModPanel", "ResourcePanel", "MapPanel", "FavoritesPanel"}
TOOL_ALIASES = {
    0: "成就 图片 achievement advancement 图片生成", 1: "头像 皮肤 avatar skin",
    2: "文件 下载 链接 url", 3: "加载器 Forge Fabric NeoForge Quilt jar",
    4: "系统 工具 内存优化 垃圾 清理 快捷方式", 5: "四则 运算 数学 calculator",
    6: "便签 记事 笔记 备忘 note", 7: "电脑 清理 垃圾 磁盘 cleanup",
    8: "服务器 测速 ping 延迟", 9: "下界 地狱 主世界 坐标 换算",
    10: "颜色 文字 格式 代码 color", 11: "系统 内存 监视 ram memory",
    12: "离线 UUID 玩家 id", 13: "Java JVM 参数 垃圾回收 内存",
    14: "资源包 完整性 校验 hash", 15: "快捷键 按键 键盘 shortcut",
    16: "随机 种子 seed", 17: "种子 结构 查询 村庄 要塞 seed structure",
    18: "经验 等级 xp level", 19: "坐标 距离 distance",
    20: "指令 命令 command give summon", 21: "base64 编码 解码 encode decode",
    22: "游戏 时间 tick 换算", 23: "堆叠 格子 背包 数量 stack",
    24: "RGB HEX 颜色 转换", 25: "红石 延时 repeat tick",
    26: "随机 玩家 名字 昵称 username", 27: "MOTD 服务器 描述 长度",
    28: "时间戳 日期 unix timestamp", 29: "服务器 地址 IP 域名 端口",
    30: "药水 时长 效果 potion", 31: "颜色 代码 格式 速查",
    32: "附魔 书架 等级 enchantment", 33: "数据包 互转 zip datapack",
    34: "网页 浏览器 web browser", 35: "正版 账户 皮肤 下载 skin",
}
LABEL_OVERRIDES = {
    "settings.WidthBox": "游戏窗口宽度", "settings.HeightBox": "游戏窗口高度",
    "settings.JavaVersionCombo": "Java 版本（高级）", "settings.JavaArchCombo": "Java 架构（高级）",
    "settings.JavaInstallModeCombo": "Java 安装方式（高级）",
    "settings.BackgroundRotationIntervalBox": "背景图片轮换间隔",
    "settings.BackgroundRotationFixedTimeBox": "背景图片每日轮换时间",
    "settings.AccentRSlider": "主题颜色 RGB 红色分量", "settings.AccentGSlider": "主题颜色 RGB 绿色分量",
    "settings.AccentBSlider": "主题颜色 RGB 蓝色分量",
    "settings.DownloadNotifyModeCombo": "下载完成通知方式",
    "settings.AccountTokenGraceDaysBox": "微软账户令牌离线宽限天数",
    "downloads.ResourceGameVersionBox": "游戏版本",
}


def tag(element):
    return element.tag.rsplit("}", 1)[-1]


def text(value):
    if not value:
        return "", None
    match = re.fullmatch(r"\{(?:Dynamic|Static)Resource\s+([^}]+)\}", value)
    if match:
        key = match[1]
        return LANG.get(key, ""), key
    if value.startswith("{"):
        return "", None
    return re.sub(r"\s+", " ", value).strip(), None


def readable(value):
    return bool(value and any(c.isalnum() for c in value))


def own_label(element):
    for attr in ("Content", "Header", "AutomationProperties.Name"):
        label, key = text(element.get(attr))
        if readable(label):
            return label, key
    descendants = [e for e in element.iter() if tag(e) == "TextBlock"]
    preferred = [e for e in descendants if "TileTitle" in e.get("Style", "")]
    for child in preferred + descendants:
        label, key = text(child.get("Text"))
        if readable(label) and len(label) < 150:
            return label, key
    return "", None


def nearby_label(element, parents):
    """Find the real label beside/above an input; never index example input values."""
    current = element
    for _ in range(4):
        parent = parents.get(current)
        if parent is None:
            break
        preceding = list(parent)[:list(parent).index(current)]
        for sibling in reversed(preceding):
            if tag(sibling) == "TextBlock":
                label, key = text(sibling.get("Text"))
                if readable(label) and len(label) <= 100:
                    return label, key
        current = parent
    return "", None


def ancestors(element, parents):
    current = parents.get(element)
    while current is not None:
        yield current
        current = parents.get(current)


def make_entry(identifier, page, title, keywords="", name=None, target=None, tab=None, resource=None,
               section="", section_resource=None, section_key=None):
    result = {"Id": identifier, "PageKey": page[0], "Title": title,
              "Keywords": " ".join(dict.fromkeys((keywords + " " + page[2]).split())),
              "PageTitle": page[1]}
    for key, value in (("TargetName", name), ("TargetText", target), ("TabIndex", tab),
                       ("ResourceKey", resource), ("SectionTitle", section),
                       ("SectionResourceKey", section_resource), ("SectionKey", section_key)):
        if value is not None and value != "":
            result[key] = value
    return result


def build():
    result = []
    for file, page in PAGES.items():
        tree = ET.parse(APP / "Views" / (file + ".xaml")).getroot()
        parents = {child: parent for parent in tree.iter() for child in parent}
        all_tabs = [e for e in tree.iter() if tag(e) == "TabItem" and not any(tag(p) in SKIP_PARENTS for p in ancestors(e, parents))]
        if page[0] != "main":
            result.append(make_entry(page[0] + ".page", page, page[1], page[2]))
        for index, item in enumerate(all_tabs):
            label, key = own_label(item)
            words = " ".join(text(e.get("Text"))[0] for e in item.iter() if tag(e) == "TextBlock")
            result.append(make_entry(page[0] + ".tab." + str(index), page, label,
                TOOL_ALIASES.get(index, "") + " " + words if page[0] == "toolbox" else words,
                item.get(X + "Name"), label, index, key))
        unnamed = collections.Counter()
        suffixes = collections.Counter()
        heading, heading_resource = "", None
        for element in tree.iter():
            kind = tag(element)
            name = element.get(X + "Name")
            chain = list(ancestors(element, parents))
            if kind == "TextBlock" and element.get("FontWeight") in {"Bold", "SemiBold"} and not any(tag(p) in SKIP_PARENTS for p in chain):
                candidate, candidate_key = text(element.get("Text"))
                if readable(candidate) and len(candidate) < 70:
                    heading, heading_resource = candidate, candidate_key
            if kind not in CONTROLS or any(tag(p) in SKIP_PARENTS or tag(p).endswith(".Resources") for p in chain):
                continue
            # Search UI is not itself a useful search destination.
            if name and name != "ShowFeatureSearchCheck" and any(word in name for word in ("FeatureSearch", "ToolboxSearch", "HomeSearch", "SettingsSearch")):
                continue
            if page[0] == "main" and name != "LaunchGameBtn":
                continue  # page navigation is already indexed; ignore window chrome and overlays.
            if kind in {"TextBox", "ComboBox", "Slider"} and not name:
                continue
            label, key = own_label(element) if kind not in {"TextBox", "ComboBox", "Slider"} else ("", None)
            if not label:
                label, key = nearby_label(element, parents)
            if page[0] + "." + str(name) in LABEL_OVERRIDES:
                label, key = LABEL_OVERRIDES[page[0] + "." + str(name)], None
            if not label or len(label) > 150:
                continue
            tab = next((all_tabs.index(p) for p in chain if p in all_tabs), None)
            section, section_resource, section_key = heading, heading_resource, None
            if tab is not None:
                section, section_resource = own_label(all_tabs[tab])
            if page[0] == "downloads":
                section_key = next((p.get(X + "Name") for p in chain if p.get(X + "Name") in DOWNLOAD_PANELS), None)
                section = {"VersionPanel": "游戏版本", "ModPanel": "Mod", "ResourcePanel": "资源包",
                           "MapPanel": "地图", "FavoritesPanel": "收藏"}.get(section_key, "")
            tooltip, _ = text(element.get("ToolTip"))
            words = " ".join((tooltip, re.sub(r"([a-z])([A-Z])", r"\1 \2", name or ""), section or ""))
            # Add common user phrases to actual controls, not a fictional action.
            if name == "LaunchGameBtn":
                words += " 启动游戏 开始游戏 运行游戏 launch game play minecraft"
            if name == "CatVersion":
                words += " 下载游戏 下载 Minecraft 安装游戏"
            if not name:
                identity = (label, tab, section_key)
                unnamed[identity] += 1
                if unnamed[identity] > 1:
                    continue  # no stable way to distinguish repeated unnamed template-free buttons
            suffix = name or element.get("Click") or element.get("Checked") or "control" + str(len(result))
            suffixes[suffix] += 1
            if suffixes[suffix] > 1:
                suffix += "." + str(suffixes[suffix])
            result.append(make_entry(page[0] + "." + suffix, page, label, words,
                name, label, tab, key, section, section_resource, section_key))

    # Code-built pages have no XAML; keep an explicit, auditable list of real destinations.
    world = ("worlds", "存档", "世界 编辑 存档 NBT 投影 结构 world save")
    result.append(make_entry("worlds.page", world, "存档"))
    for i, title in enumerate(("打开存档目录", "查看投影 / 结构文件", "允许作弊", "极限模式", "保存设置", "修改并生成副本")):
        result.append(make_entry("worlds.action." + str(i), world, title, target=title, tab=0 if i >= 2 else None))
    for i, title in enumerate(("世界设置", "投影 / 材料", "NBT 结构")):
        result.append(make_entry("worlds.tab." + str(i), world, title, target=title, tab=i))
    world_code = (APP / "Services/Worlds/WorldArchiveService.cs").read_text(encoding="utf-8-sig")
    for key, title in re.findall(r'new\("([\w.]+)", "([^"]+)", "(?:string|int|long|float|double|bool)"', world_code):
        if title in {"允许作弊", "极限模式"}:
            continue
        result.append(make_entry("worlds.field." + key, world, title, keywords=key, target=title, tab=0))
    api = ("api", "API 开发中心", "API 接口 样例 文档 插件 启动器 控制 HTTP")
    result.append(make_entry("api.page", api, "API 开发中心"))
    for i, title in enumerate(("API 开关与等待时间", "导出当前专题 MD…")):
        result.append(make_entry("api.action." + str(i), api, title, target=title))
    ids = [entry["Id"] for entry in result]
    if len(ids) != len(set(ids)):
        raise ValueError("Duplicate destination ids: " + str([key for key, count in collections.Counter(ids).items() if count > 1]))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    entries = build()
    destination = APP / "Resources/Data/FeatureSearchCatalog.json"
    output = json.dumps(entries, ensure_ascii=False, indent=2) + "\n"
    if args.check:
        if not destination.exists() or destination.read_text(encoding="utf-8") != output:
            raise SystemExit("Feature search catalog is stale. Run generate_catalog.py.")
    else:
        destination.write_text(output, encoding="utf-8")
    print(f"Feature search catalog: {len(entries)} real page/control destinations; "
          + ", ".join(f"{page}={count}" for page, count in collections.Counter(e["PageKey"] for e in entries).items()))


if __name__ == "__main__":
    main()
