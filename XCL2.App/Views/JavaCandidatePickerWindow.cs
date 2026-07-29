using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 全盘扫描 Java 完成后的简单选择窗口：列出所有候选(路径+尽力探测到的版本号)，
/// 用户单击选中一项再点"使用这个"确认，避免用容易输错的"输入序号"交互。
/// 纯代码构建 UI（这个窗口足够简单，不需要单独的 .xaml 文件）。
/// </summary>
public class JavaCandidatePickerWindow : Window
{
    private readonly ListBox _list = new() { Margin = new Thickness(12) };

    public string? SelectedPath { get; private set; }

    public JavaCandidatePickerWindow(List<JavaCandidate> candidates)
    {
        Title = "选择要使用的 Java";
        Width = 640;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

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
                DialogResult = true;
            }
        };

        var cancelButton = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        cancelButton.Click += (_, _) => { DialogResult = false; };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(okButton);

        var root = new DockPanel();
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        root.Children.Add(buttonRow);
        root.Children.Add(_list);

        Content = root;
    }
}
