using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 全盘扫描 Java 完成后的简单选择窗口：列出所有候选(路径+尽力探测到的版本号)，
/// 用户单击选中一项再点"使用这个"确认，避免用容易输错的"输入序号"交互。
/// 纯代码构建 UI（这个窗口足够简单，不需要单独的 .xaml 文件）。
/// </summary>
/// <remarks>
/// 已从独立 Window 迁移为进程内 Overlay 弹窗（继承 OverlayDialogControl）。
/// 尺寸/居中/圆角卡片背景都由 MainWindow 的 OverlayCard 统一提供，
/// 所以原来的 Title/Width/Height/WindowStartupLocation 全部去掉，
/// 只保留一个 MaxHeight 防止候选项特别多时把弹窗顶到屏幕外。
/// </remarks>
public class JavaCandidatePickerWindow : OverlayDialogControl
{
    private readonly ListBox _list = new() { Margin = new Thickness(12) };

    public string? SelectedPath { get; private set; }

    public JavaCandidatePickerWindow(List<JavaCandidate> candidates)
    {
        MinWidth = 560;
        MaxWidth = 640;
        MaxHeight = 420;

        foreach (var c in candidates)
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = (c.Version != null ? $"[Java {c.Version}]  " : "[版本未知]  ") + c.JavawPath,
                Tag = c.JavawPath
            });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        var okButton = new Button { Content = "使用这个", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        okButton.Click += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem item && item.Tag is string path)
            {
                SelectedPath = path;
                CloseWith(true);
            }
        };

        var cancelButton = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        cancelButton.Click += (_, _) => CloseWith(false);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(okButton);

        // Overlay 没有标题栏，原来写在 Window.Title 里的文字改成内容顶部的一行标题。
        var title = new TextBlock
        {
            Text = "选择要使用的 Java",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(12, 12, 12, 0),
        };

        var root = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        root.Children.Add(title);
        root.Children.Add(buttonRow);
        root.Children.Add(_list);

        Content = root;
    }
}
