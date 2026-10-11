using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Services.Workbench;

namespace XCL2.App.Views;

internal static class AiWorkbenchDialogs
{
    private static Window Dialog(string title, Window? owner) => new()
    { Title = title, Width = 880, Height = 670, MinWidth = 580, MinHeight = 400, Owner = owner,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = (Brush?)Application.Current.TryFindResource("PanelBrush") ?? Brushes.White,
        Foreground = (Brush?)Application.Current.TryFindResource("TextPrimaryBrush") ?? Brushes.Black };
    public static bool Review(Window? owner, FileChange change, AiFileDiff diff)
    {
        var window = Dialog("修改前预览", owner);
        var layout = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = change.Path + "\n原文件尚未修改 · 应用后可在变更历史中回滚", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var apply = new Button { Content = "应用修改", MinWidth = 100, Padding = new Thickness(12, 8, 12, 8) };
        var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        apply.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(apply); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new AiFileDiffView { DataContext = new { FileDiff = diff, FileDiffHeader = $"新增 {diff.AddedLines} · 删除 {diff.DeletedLines} · 修改 {diff.ModifiedLines}" } } });
        window.Content = layout; return window.ShowDialog() == true;
    }
    public static void History(Window? owner, IEnumerable<string> ids)
    {
        var window = Dialog("变更历史 · 一键回滚", owner);
        var rows = new StackPanel { Margin = new Thickness(20) };
        rows.Children.Add(new TextBlock { Text = "回滚将检查文件当前内容；存在后续修改时会停止。Plan 模式请先切换 Build。", TextWrapping = TextWrapping.Wrap });
        foreach (var id in ids.Reverse().Distinct())
        {
            FileChange change;
            try { change = AiFileAccessService.Changes.Get(id); } catch { continue; }
            var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            var label = new TextBlock { Text = $"{change.CreatedUtc.ToLocalTime():g} · {change.State}\n{change.Path}", TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(label);
            var rollback = new Button { Content = "一键回滚", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), IsEnabled = change.State is "Applied" or "Applying" or "RollingBack" };
            rollback.Click += (_, _) =>
            {
                try { var result = AiFileAccessService.RollbackChange(id); rollback.IsEnabled = false; label.Text = "已回滚 · " + result.Path; }
                catch (Exception ex) { MessageBoxDialog.ShowWarning(ex.Message, "回滚未完成"); }
            };
            panel.Children.Add(rollback); rows.Children.Add(panel);
        }
        window.Content = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; window.ShowDialog();
    }
}
