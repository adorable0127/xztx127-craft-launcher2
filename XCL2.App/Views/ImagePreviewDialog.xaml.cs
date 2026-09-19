using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 背景候选池"预览图片"的极简放大预览弹窗：给背景候选列表看图用，不做任何编辑，
/// 也不影响当前正在应用的背景——跟"点列表条目 = 设为当前背景"是两件完全独立的事，
/// 用户可能只是想看清楚某一张长什么样，并不一定想切换过去。
/// </summary>
public partial class ImagePreviewDialog : OverlayDialogControl
{
    public ImagePreviewDialog(string path)
    {
        InitializeComponent();
        FileNameText.Text = Path.GetFileName(path);

        try
        {
            // CacheOption=OnLoad 立刻把像素读进内存并释放文件句柄：预览窗口开着的时候
            // 用户仍然可能在背景候选列表里做删除/重命名，不能让这里的预览占着文件不放，
            // 否则会复现"覆盖/删除同名文件时 IOException"那个老毛病（见导入背景图片的注释）。
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage.Source = bmp;
        }
        catch (Exception ex)
        {
            FileNameText.Text += "（图片加载失败）";
            ErrorPresenter.LogTechnicalDetail($"[背景图片预览加载失败] {path}\n{ex}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(null);
}
