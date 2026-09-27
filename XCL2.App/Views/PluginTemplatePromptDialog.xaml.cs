using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>"生成插件项目模板"命令的参数收集弹窗：插件 Id、显示名称、落盘目录，
/// 校验通过后直接调用 PluginTemplateGenerator.Generate 并把生成结果目录带回给调用方。</summary>
public partial class PluginTemplatePromptDialog : OverlayDialogControl
{
    /// <summary>生成成功后的目标目录；仅在 ShowDialog() 返回 true 时有意义。</summary>
    public string? GeneratedDirectory { get; private set; }

    public PluginTemplatePromptDialog()
    {
        InitializeComponent();
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择生成插件模板的目录" };
        if (dialog.ShowDialog() == true)
        {
            TargetDirBox.Text = dialog.FolderName;
            ErrorText.Text = "";
        }
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var pluginId = PluginIdBox.Text.Trim();
        var displayName = DisplayNameBox.Text.Trim();
        var targetDir = TargetDirBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(displayName))
        {
            ErrorText.Text = "插件 Id 和显示名称都不能为空。";
            return;
        }
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            ErrorText.Text = "请先选择一个目标目录。";
            return;
        }
        try
        {
            if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            {
                ErrorText.Text = "目标目录不是空的，换一个空目录（避免覆盖已有文件）。";
                return;
            }

            PluginTemplateGenerator.Generate(targetDir, pluginId, displayName);
            GeneratedDirectory = targetDir;
            CloseWith(true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"生成失败：{ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);
}
