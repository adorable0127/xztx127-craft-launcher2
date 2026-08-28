using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>
/// 百宝箱「基岩版 ↔ Java 版数据包互转」工具。UI 只负责收集参数、跑
/// <see cref="DatapackConverterService"/> 并把结果展示出来，具体转换规则都在
/// Service 里，方便以后单独扩展/测试。
/// </summary>
public partial class DatapackConverterTool : UserControl
{
    public DatapackConverterTool()
    {
        InitializeComponent();
    }

    private void DpcBrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择源目录（数据包 / 行为包根目录）" };
        if (!string.IsNullOrWhiteSpace(DpcSourceDirBox.Text) && Directory.Exists(DpcSourceDirBox.Text))
            dialog.InitialDirectory = DpcSourceDirBox.Text;

        if (dialog.ShowDialog() == true)
            DpcSourceDirBox.Text = dialog.FolderName;
    }

    private void DpcBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择转换结果输出目录" };
        if (!string.IsNullOrWhiteSpace(DpcOutputDirBox.Text) && Directory.Exists(DpcOutputDirBox.Text))
            dialog.InitialDirectory = DpcOutputDirBox.Text;

        if (dialog.ShowDialog() == true)
            DpcOutputDirBox.Text = dialog.FolderName;
    }

    private void DpcSourceDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var dir = DpcSourceDirBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            DpcDetectedText.Text = "尚未选择源目录";
            return;
        }

        var type = DatapackConverterService.DetectPackType(dir);
        switch (type)
        {
            case DatapackConverterService.PackType.JavaDatapack:
                DpcDetectedText.Text = "检测到：Java 版数据包（找到 pack.mcmeta / data 目录）";
                DirJavaToBedrock.IsChecked = true;
                break;
            case DatapackConverterService.PackType.BedrockBehaviorPack:
                DpcDetectedText.Text = "检测到：基岩版行为包（找到 manifest.json）";
                DirBedrockToJava.IsChecked = true;
                break;
            default:
                DpcDetectedText.Text = "没能自动识别出包类型，请确认这是数据包或行为包的根目录，并手动选择正确的转换方向。";
                break;
        }

        if (string.IsNullOrWhiteSpace(DpcOutputDirBox.Text))
        {
            var suggestSuffix = DirJavaToBedrock.IsChecked == true ? "_bedrock转换结果" : "_java转换结果";
            DpcOutputDirBox.Text = dir.TrimEnd('\\', '/') + suggestSuffix;
        }
    }

    private async void DpcConvert_Click(object sender, RoutedEventArgs e)
    {
        var source = DpcSourceDirBox.Text?.Trim();
        var output = DpcOutputDirBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            MessageBoxDialog.ShowWarning("请先选择一个存在的源目录。", "数据包互转");
            return;
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            MessageBoxDialog.ShowWarning("请先选择输出目录。", "数据包互转");
            return;
        }
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            MessageBoxDialog.ShowWarning("输出目录不能和源目录相同，请换一个目录，避免覆盖原始文件。", "数据包互转");
            return;
        }
        if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length > 0)
        {
            if (!MessageBoxDialog.ShowConfirm(
                    "输出目录已经存在文件，转换过程中同名文件会被覆盖，其余文件不受影响。是否继续？",
                    "数据包互转"))
                return;
        }

        var toBedrock = DirJavaToBedrock.IsChecked == true;
        var ns = DpcNamespaceBox.Text;

        DpcConvertButton.IsEnabled = false;
        DpcOpenOutputButton.IsEnabled = false;
        DpcSummaryText.Visibility = Visibility.Collapsed;
        DpcLogBox.Text = "正在转换……";

        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => toBedrock
                ? DatapackConverterService.ConvertJavaToBedrock(source, output)
                : DatapackConverterService.ConvertBedrockToJava(source, output, ns));

            RenderResult(result, toBedrock);
        }
        catch (Exception ex)
        {
            DpcLogBox.Text = $"转换过程中发生未预期的错误：\n{ex}";
        }
        finally
        {
            DpcConvertButton.IsEnabled = true;
        }
    }

    private void RenderResult(DatapackConverterService.ConvertResult result, bool toBedrock)
    {
        if (!result.Success)
        {
            DpcSummaryText.Text = "转换失败";
            DpcSummaryText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            DpcSummaryText.Visibility = Visibility.Visible;
            DpcLogBox.Text = $"转换失败：{result.FatalError}";
            return;
        }

        DpcOpenOutputButton.IsEnabled = true;

        var sb = new StringBuilder();
        sb.AppendLine($"转换方向：{(toBedrock ? "Java 数据包 → 基岩版行为包" : "基岩版行为包 → Java 数据包")}");
        sb.AppendLine($"输出目录：{result.OutputDir}");
        sb.AppendLine($"转换语法/生成清单文件：{result.FilesConverted} 个");
        sb.AppendLine($"原样复制（需要人工核对）：{result.FilesCopiedAsIs} 个");
        sb.AppendLine($"未转换/跳过：{result.FilesSkipped} 个");
        sb.AppendLine();

        if (result.InfoLog.Count > 0)
        {
            sb.AppendLine("【处理记录】");
            foreach (var line in result.InfoLog) sb.AppendLine("· " + line);
            sb.AppendLine();
        }

        if (result.WarnLog.Count > 0)
        {
            sb.AppendLine("【需要你手动核对的地方】");
            foreach (var line in result.WarnLog) sb.AppendLine("⚠ " + line);
        }
        else
        {
            sb.AppendLine("没有需要特别注意的提示，不过仍然建议转换完成后自己开一下游戏测试。");
        }

        DpcLogBox.Text = sb.ToString();

        DpcSummaryText.Text = result.FilesSkipped > 0
            ? $"转换完成，但有 {result.FilesSkipped} 个文件因为两边没有对应功能被跳过，请看下方报告。"
            : "转换完成。";
        DpcSummaryText.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        DpcSummaryText.Visibility = Visibility.Visible;
    }

    private void DpcOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var output = DpcOutputDirBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(output) || !Directory.Exists(output))
        {
            MessageBoxDialog.ShowWarning("输出目录不存在，可能还没有执行过转换。", "数据包互转");
            return;
        }
        FolderOpenHelper.Open(output);
    }
}
