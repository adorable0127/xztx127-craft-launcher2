using System.Linq;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 极简的"从 Java 列表选一个"弹窗，服务器管理页的「选择 Java...」菜单项用它，
/// 复用 JavaListItem(定义在 SettingsPage.xaml.cs 里，同命名空间下直接可用) 做展示。
/// </summary>
public partial class SelectJavaWindow : Window
{
    public string? SelectedJavaId { get; private set; }
    public string? SelectedJavaPath { get; private set; }

    /// <param name="configService">用来读取全局 Java 列表。</param>
    /// <param name="currentJavaId">当前已经选中的 Java 列表条目 Id(没有则为 null)，用于预选中。</param>
    /// <param name="currentJavaPath">没有 currentJavaId 时的兜底展示，用于在列表里按路径匹配预选。</param>
    public SelectJavaWindow(ConfigService configService, string? currentJavaId, string? currentJavaPath)
    {
        InitializeComponent();

        JavaListBox.Items.Add(new JavaListItem { Entry = null }); // "（不指定，沿用原有路径）"
        foreach (var j in configService.Config.InstalledJavas) JavaListBox.Items.Add(new JavaListItem { Entry = j });

        var items = JavaListBox.Items.Cast<JavaListItem>();
        var preSelect = !string.IsNullOrEmpty(currentJavaId)
            ? items.FirstOrDefault(i => i.Entry?.Id == currentJavaId)
            : (!string.IsNullOrEmpty(currentJavaPath)
                ? items.FirstOrDefault(i => string.Equals(i.Entry?.JavawPath, currentJavaPath, System.StringComparison.OrdinalIgnoreCase))
                : null);
        JavaListBox.SelectedItem = preSelect ?? JavaListBox.Items[0];
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var picked = (JavaListBox.SelectedItem as JavaListItem)?.Entry;
        SelectedJavaId = picked?.Id;
        SelectedJavaPath = picked?.JavawPath;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
