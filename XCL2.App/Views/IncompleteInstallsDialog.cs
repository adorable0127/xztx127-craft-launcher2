using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// "未完成的安装"列表：每个需要修复的实例后面跟一个"安装"按钮，点击只补全缺失文件，不启动游戏。
/// 点击后只是把补全任务加入统一下载队列（右上角下载抽屉），进度、暂停/取消、失败重试都在那里处理。
/// </summary>
public class IncompleteInstallsDialog : OverlayDialogControl
{
    private sealed class Row
    {
        public required string Id { get; init; }
        public required Button InstallBtn { get; init; }
        public required TextBlock Status { get; init; }
    }

    private readonly Action<string> _enqueueRepair;
    private readonly List<Row> _rows = new();
    private readonly Button _closeBtn;

    /// <summary>是否至少加入过一个补全任务（任务完成后由调用方的回调刷新列表）。</summary>
    public bool AnyInstalled { get; private set; }

    public IncompleteInstallsDialog(IReadOnlyList<GameVersion> versions, Action<string> enqueueRepair)
    {
        _enqueueRepair = enqueueRepair;
        Width = 520;

        var root = new StackPanel { Margin = new Thickness(20) };

        var title = new TextBlock
        {
            Text = $"未完成的安装 ({versions.Count})",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "这些实例的版本信息、游戏本体或依赖文件不完整。点击「安装」只会补全缺失的文件，不会启动游戏。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        root.Children.Add(hint);

        var listPanel = new StackPanel();
        foreach (var v in versions) listPanel.Children.Add(BuildRow(v.Id));
        root.Children.Add(new ScrollViewer
        {
            Content = listPanel, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 14)
        });

        _closeBtn = new Button { Content = "关闭", Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
        _closeBtn.Click += (_, _) => CloseWith(AnyInstalled);
        root.Children.Add(_closeBtn);

        Content = root;
    }

    private UIElement BuildRow(string id)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = id, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = id });
        var status = new TextBlock
        {
            Text = "实例文件不完整，需要修复", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        text.Children.Add(status);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var btn = new Button { Content = "安装", MinWidth = 72, Padding = new Thickness(14, 6, 14, 6) };
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);

        var row = new Row { Id = id, InstallBtn = btn, Status = status };
        _rows.Add(row);
        btn.Click += (_, _) => Enqueue(row);
        return grid;
    }

    private void Enqueue(Row row)
    {
        _enqueueRepair(row.Id);
        AnyInstalled = true;
        row.Status.Text = "已加入下载列表，可在右上角下载抽屉查看进度";
        row.InstallBtn.Content = "已加入";
        row.InstallBtn.IsEnabled = false;
    }

}
