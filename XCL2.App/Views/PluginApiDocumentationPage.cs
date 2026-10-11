using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>内置文档页：不同 API 按专题浏览，所有内容随程序打包，可离线查看和导出。</summary>
internal sealed class PluginApiDocumentationPage : UserControl
{
    internal PluginApiDocumentationPage()
    {
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "API 开发中心 · " + PluginApi.Version.ToString(2), FontSize = 25, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock { Text = "选择专题查看完整接口与可复制样例。文档已内置，离线也能阅读。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        var toolbar = new WrapPanel();
        var apiSettings = new Button { Content = "API 开关与等待时间", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        apiSettings.Click += (_, _) => OverlayDialogService.ShowModal(new PluginApiSettingsDialog());
        toolbar.Children.Add(apiSettings);
        var export = new Button { Content = "导出当前专题 MD…", Padding = new Thickness(12, 6, 12, 6) };
        toolbar.Children.Add(export); header.Children.Add(toolbar);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var files = new[] { EmbeddedDocumentationService.ApiFileName, EmbeddedDocumentationService.LauncherControlFileName,
            EmbeddedDocumentationService.CompositionApiFileName,
            EmbeddedDocumentationService.WorldsApiFileName, EmbeddedDocumentationService.ApiExamplesFileName,
            EmbeddedDocumentationService.PluginExeProtocolFileName, EmbeddedDocumentationService.PluginGuideFileName };
        var topics = new ListBox { Margin = new Thickness(0, 0, 16, 0) };
        foreach (var file in files) topics.Items.Add(new ListBoxItem
        { Content = new TextBlock { Text = EmbeddedDocumentationService.GetTitle(file), TextWrapping = TextWrapping.Wrap }, Tag = file, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 4) });
        var viewer = new MarkdownViewer();
        var scroll = new ScrollViewer { Content = viewer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); grid.Children.Add(topics); grid.Children.Add(scroll); root.Children.Add(grid);
        topics.SelectionChanged += (_, _) =>
        {
            if ((topics.SelectedItem as ListBoxItem)?.Tag is not string file) return;
            try { viewer.Markdown = EmbeddedDocumentationService.Read(file); scroll.ScrollToTop(); }
            catch (Exception ex) { viewer.Markdown = "无法读取文档：" + ex.Message; }
        };
        export.Click += (_, _) => { if ((topics.SelectedItem as ListBoxItem)?.Tag is string file) HelpDocumentDialog.ExportMarkdown(file); };
        topics.SelectedIndex = 0; Content = root;
    }
}
