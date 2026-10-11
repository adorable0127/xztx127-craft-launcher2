using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private FeatureSearchHighlight? _featureSearchHighlight;

    /// <summary>只识别已经挂载的页面；建立搜索目录不会实例化页面或启动页面网络请求。</summary>
    public string CurrentSearchPageKey => MainContent?.Content switch
    {
        HomePage => "home", VersionSelectPage => "versions", DownloadCenterPage => "downloads",
        MultiplayerPage => "multiplayer", ModManagerPage => "mods", WorldArchivePage => "worlds",
        ServerManagerPage => "servers", LoginPage => "accounts", SettingsPage => "settings",
        LogsPage => "logs", AiAssistantPanel => "ai", ToolboxPage => "toolbox", BedrockPage => "bedrock",
        AboutHelpPage => "about", PluginApiDocumentationPage => "api", MorePage => "more", _ => "main"
    };

    /// <summary>
    /// 定位功能入口，永远不调用入口按钮的 Click/Command。页面导航仍走既有保存确认、
    /// 受限模式与协议检查；每次 await 后核对页面代次，避免快速上下跳转定位到旧页面。
    /// </summary>
    public async Task<string> NavigateToSearchResultAsync(FeatureSearchEntry entry, CancellationToken ct)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => NavigateToSearchResultAsync(entry, ct)).Task.Unwrap();

        ct.ThrowIfCancellationRequested();
        _featureSearchHighlight?.Dispose();
        _featureSearchHighlight = null;

        if (entry.PageKey != "main" && CurrentSearchPageKey != entry.PageKey)
        {
            var previousGeneration = _pageNavigationGeneration;
            if (!RequestFeatureSearchPage(entry.PageKey)) return "该功能所在页面暂不可用";
            ct.ThrowIfCancellationRequested();
            if (_pageNavigationGeneration == previousGeneration && CurrentSearchPageKey != entry.PageKey)
                return "已取消跳转，当前页面保持不变";
        }

        var expectedGeneration = _pageNavigationGeneration;
        var wait = Stopwatch.StartNew();
        while (_pageNavigationCancellation != null)
        {
            ct.ThrowIfCancellationRequested();
            if (expectedGeneration != _pageNavigationGeneration || _isWindowClosed) return "页面已切换，已取消本次定位";
            if (wait.Elapsed > TimeSpan.FromSeconds(20)) return "页面仍在加载，请稍后再次定位";
            await Task.Delay(25, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (_isWindowClosed || expectedGeneration != _pageNavigationGeneration) return "页面已切换，已取消本次定位";
        if (entry.PageKey != "main" && CurrentSearchPageKey != entry.PageKey) return "未能打开目标页面，请重试";

        var page = entry.PageKey == "main" ? this : MainContent.Content as FrameworkElement;
        if (page == null) return "该页面尚未准备好";
        bool IsCurrent() => !_isWindowClosed && expectedGeneration == _pageNavigationGeneration
            && (entry.PageKey == "main" || ReferenceEquals(MainContent.Content, page));

        if (entry.TabIndex is int tabIndex)
        {
            var selected = page is ToolboxPage toolbox
                ? toolbox.SelectToolboxFeature(tabIndex)
                : SelectFeatureSearchTab(EnumerateFeatureSearchTree(page).OfType<TabControl>().FirstOrDefault(), tabIndex);
            if (!selected) return "该分类被当前设置隐藏或已取消选择";
        }
        if (!string.IsNullOrWhiteSpace(entry.SectionKey) && page is DownloadCenterPage downloads
            && !downloads.SelectFeatureSearchSection(entry.SectionKey))
            return "该分类当前不可用";

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, ct);
        ct.ThrowIfCancellationRequested();
        if (!IsCurrent()) return "页面已切换，已取消本次定位";

        var searchRoot = !string.IsNullOrWhiteSpace(entry.SectionKey)
            ? FindFeatureSearchNamedElement(page, entry.SectionKey) ?? page : page;
        if (entry.TabIndex is int && string.IsNullOrWhiteSpace(entry.SectionKey))
        {
            // 同一页不同 Tab 常有同名“刷新/复制”按钮，文本回退必须限于已选择的分类。
            var tabs = EnumerateFeatureSearchTree(page).OfType<TabControl>().FirstOrDefault();
            searchRoot = tabs?.SelectedItem as FrameworkElement ?? searchRoot;
        }
        var target = FindFeatureSearchTarget(searchRoot, entry);
        // 没有具体控件的目录项就是页面/分类入口。
        if (target == null && string.IsNullOrWhiteSpace(entry.TargetName) && string.IsNullOrWhiteSpace(entry.TargetText))
        {
            if (entry.TabIndex is int selectedIndex)
            {
                var tabs = EnumerateFeatureSearchTree(page).OfType<TabControl>().FirstOrDefault();
                target = tabs?.ItemContainerGenerator.ContainerFromIndex(selectedIndex) as FrameworkElement
                    ?? tabs?.SelectedItem as FrameworkElement;
                if (target is TabItem { Content: FrameworkElement content }) target = content;
            }
            target ??= searchRoot;
        }
        if (target == null) return $"已打开页面；「{entry.Title}」当前尚未显示";
        // 百宝箱模板不绘制 Tab 标题；定位其实际内容，也适用于普通页的分类目录项。
        if (target is TabItem { Content: FrameworkElement tabContent }) target = tabContent;

        // 只展开正常可展开容器，不把用户隐藏/模式限制/缺少前置条件的控件强制设为 Visible。
        var ancestors = FeatureSearchAncestors(target).Reverse().ToArray();
        if (ancestors.OfType<FrameworkElement>().Any(element => element.Visibility != Visibility.Visible))
            return $"「{entry.Title}」被当前设置隐藏或尚不可用";
        foreach (var ancestor in ancestors)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent()) return "页面已切换，已取消本次定位";
            if (ancestor is TabItem tab && !tab.IsSelected)
            {
                var tabControl = FeatureSearchAncestors(tab).OfType<TabControl>().FirstOrDefault();
                if (tabControl == null) return "该功能分类暂不可用";
                var index = tabControl.Items.IndexOf(tab);
                var selected = page is ToolboxPage toolbox && ReferenceEquals(tabControl, page.FindName("ToolboxTabControl"))
                    ? toolbox.SelectToolboxFeature(index) : SelectFeatureSearchTab(tabControl, index);
                if (!selected) return "已取消分类切换";
            }
            if (ancestor is Expander expander && !expander.IsExpanded)
            {
                if (!expander.IsEnabled) return "该功能所在分组当前不可展开";
                expander.SetCurrentValue(Expander.IsExpandedProperty, true);
            }
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, ct);
        ct.ThrowIfCancellationRequested();
        if (!IsCurrent()) return "页面已切换，已取消本次定位";
        if (!target.IsVisible) return $"「{entry.Title}」被当前设置隐藏或尚不可用";
        target.BringIntoView();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, ct);
        ct.ThrowIfCancellationRequested();
        if (!IsCurrent()) return "页面已切换，已取消本次定位";
        if (!target.IsVisible) return $"「{entry.Title}」当前尚不可用";
        _featureSearchHighlight = new FeatureSearchHighlight(target);
        return target.IsEnabled ? $"已定位：{entry.Title}" : $"已定位：{entry.Title}（当前不可操作）";
    }

    private bool RequestFeatureSearchPage(string key)
    {
        switch (key)
        {
            case "home": NavigateToHome(); break;
            case "versions": NavigateToVersions(); break;
            case "downloads": NavigateToDownloadCenter(); break;
            case "multiplayer": NavigateToMultiplayer(); break;
            case "mods": NavigateToModManager(); break;
            case "worlds": NavigateToWorlds(); break;
            case "servers": NavigateToServerManager(); break;
            case "accounts": NavigateToAccounts(); break;
            case "settings": NavigateToSettings(); break;
            case "logs": NavigateToLogs(); break;
            case "ai": NavigateToAiAssistant(); break;
            case "toolbox": NavigateToToolbox(); break;
            case "bedrock": NavigateToBedrock(); break;
            case "about": NavigateToAboutHelp(); break;
            case "api": NavigateToApiDocumentation(); break;
            case "more": NavigateToMore(); break;
            default: return false;
        }
        return true;
    }

    private static bool SelectFeatureSearchTab(TabControl? tabs, int index)
    {
        if (tabs == null || tabs.Visibility != Visibility.Visible || !tabs.IsEnabled || index < 0 || index >= tabs.Items.Count) return false;
        var tab = tabs.Items[index] as TabItem ?? tabs.ItemContainerGenerator.ContainerFromIndex(index) as TabItem;
        if (tab == null || tab.Visibility != Visibility.Visible || !tab.IsEnabled) return false;
        tabs.SetCurrentValue(Selector.SelectedIndexProperty, index);
        return tabs.SelectedIndex == index;
    }

    private static FrameworkElement? FindFeatureSearchNamedElement(FrameworkElement root, string name) =>
        root.FindName(name) as FrameworkElement
        ?? EnumerateFeatureSearchTree(root).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name);

    private static FrameworkElement? FindFeatureSearchTarget(FrameworkElement root, FeatureSearchEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.TargetName))
        {
            var named = FindFeatureSearchNamedElement(root, entry.TargetName);
            // 侧边栏折叠时，同一个启动入口改由图标按钮呈现；不改变侧边栏设置。
            if (root is MainWindow && entry.TargetName == "LaunchGameBtn" && named?.Visibility != Visibility.Visible)
                named = FindFeatureSearchNamedElement(root, "LaunchGameBtnCollapsed");
            if (named != null) return named;
        }
        var text = !string.IsNullOrWhiteSpace(entry.ResourceKey)
            ? root.TryFindResource(entry.ResourceKey) as string ?? entry.TargetText : entry.TargetText;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var candidates = EnumerateFeatureSearchTree(root).OfType<FrameworkElement>()
            .Select(element => (Element: element, Text: GetFeatureSearchText(element)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .ToArray();
        var exact = candidates.FirstOrDefault(candidate => string.Equals(candidate.Text!.Trim(), text.Trim(), StringComparison.CurrentCultureIgnoreCase)).Element;
        var match = exact ?? candidates.FirstOrDefault(candidate => candidate.Text!.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase)).Element;
        if (match is TextBlock)
        {
            var button = FeatureSearchAncestors(match).Skip(1).Take(4).OfType<ButtonBase>().FirstOrDefault();
            if (button != null) return button;
        }
        return match;
    }

    private static string? GetFeatureSearchText(FrameworkElement element) => element switch
    {
        TextBlock text => text.Text,
        HeaderedContentControl header when header.Header is string text => text,
        ContentControl content when content.Content is string text => text,
        _ => null
    };

    private static IEnumerable<DependencyObject> EnumerateFeatureSearchTree(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            yield return current;
            // Logical children include unselected tabs; visual children include generated templates.
            var children = LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>().ToList();
            if (current is Visual or Visual3D)
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                    children.Add(VisualTreeHelper.GetChild(current, index));
            for (var index = children.Count - 1; index >= 0; index--) pending.Push(children[index]);
        }
    }

    private static IEnumerable<DependencyObject> FeatureSearchAncestors(DependencyObject start)
    {
        var visited = new HashSet<DependencyObject>();
        for (DependencyObject? current = start; current != null && visited.Add(current);)
        {
            yield return current;
            current = LogicalTreeHelper.GetParent(current)
                ?? (current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : null);
        }
    }
}
