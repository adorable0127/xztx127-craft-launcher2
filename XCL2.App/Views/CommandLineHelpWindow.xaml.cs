using System.Windows;

namespace XCL2.App.Views;

/// <summary>
/// -help / --help / /? 命中时展示的命令行帮助窗口。取代原来的
/// <c>MessageBox.Show(CommandLineService.HelpText, ...)</c>——原因见 xaml 头部注释。
/// 只在 App.xaml.cs 的 OnStartup 里、还没创建 MainWindow 之前使用一次，不依赖
/// OverlayDialogService/MainWindow 的任何状态，可以完全独立弹出、独立关闭。
/// </summary>
public partial class CommandLineHelpWindow : Window
{
    public CommandLineHelpWindow(string helpText)
    {
        InitializeComponent();
        HelpTextBox.Text = helpText;
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(HelpTextBox.Text);
            CopyAllBtn.Content = "已复制 ✓";
        }
        catch
        {
            // 极少数情况下剪贴板被其它进程占用会抛异常，这里不是关键功能，静默忽略即可，
            // 用户仍然可以自己在 TextBox 里手动选中复制。
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
