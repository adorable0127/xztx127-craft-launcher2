using System.Windows;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class HelpDocumentDialog : OverlayDialogControl
{
    private readonly string _fileName;

    public HelpDocumentDialog(string fileName)
    {
        InitializeComponent();
        _fileName = fileName;
        TitleText.Text = EmbeddedDocumentationService.GetTitle(fileName);
        DocumentViewer.Markdown = EmbeddedDocumentationService.Read(fileName);
    }

    public static void ExportMarkdown(string fileName)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出" + EmbeddedDocumentationService.GetTitle(fileName),
                FileName = fileName,
                DefaultExt = ".md",
                Filter = "Markdown 文档 (*.md)|*.md",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true) return;
            EmbeddedDocumentationService.Export(fileName, dialog.FileName);
            MessageBoxDialog.ShowInfo("文档已导出到：\n" + dialog.FileName, "帮助文档");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning("导出文档失败：\n" + ex.Message, "帮助文档");
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e) => ExportMarkdown(_fileName);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(null);
}
