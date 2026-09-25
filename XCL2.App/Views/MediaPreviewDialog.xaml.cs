using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 背景候选池"预览图片"的动图/视频专用预览：以前这两种类型点"预览图片"只会弹一个提示，
/// 说"已经能在主窗口底层看效果了"，现在改成真的开一个独立的轻量预览，不用去改当前正在用
/// 的背景（跟 ImagePreviewDialog 一样，纯粹看一眼，不影响 MainWindow.SetCustomBackgroundImage
/// 实际应用的那一份）。
///
/// 两条播放路径都是 WPF/系统自带的最轻实现，没有引入任何第三方播放器：
///   GIF：GifBitmapDecoder 解帧 + DispatcherTimer，逻辑跟主窗口背景层的 GIF 动图一模一样，
///        直接照搬过来，不是另外发明一套。
///   视频：MediaElement，底层是系统 Windows Media Foundation 硬件解码，WPF 里最轻的播放方式，
///        没有比这更省资源的选项了（比如再套一层 WebView2 播放 &lt;video&gt; 标签只会更重）。
/// </summary>
public partial class MediaPreviewDialog : OverlayDialogControl
{
    private DispatcherTimer? _gifTimer;
    private BitmapFrame[]? _gifFrames;
    private int _gifFrameIndex;
    private bool _isVideo;

    public MediaPreviewDialog(string path)
    {
        InitializeComponent();
        FileNameText.Text = Path.GetFileName(path);
        RequestClose += (_, _) => StopPlayback();

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".gif") LoadGif(path);
            else LoadVideo(path);
        }
        catch (Exception ex)
        {
            FileNameText.Text += "（预览加载失败）";
            HintText.Text = "文件可能已损坏或者格式不受支持。";
            ErrorPresenter.LogTechnicalDetail($"[背景动图/视频预览加载失败] {path}\n{ex}");
        }
    }

    private void LoadGif(string path)
    {
        // 限制跟主窗口背景层的 GIF 加载完全一致（30MB / 200 帧），这里只是预览、
        // 不需要比正式使用更宽松的上限，两处保持同一套安全边界更容易维护。
        if (new FileInfo(path).Length > 30 * 1024 * 1024)
            throw new InvalidOperationException("GIF 动图超过 30 MB。");
        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0 || decoder.Frames.Count > 200)
            throw new InvalidOperationException("GIF 动图帧数无效或超过 200 帧。");

        _gifFrames = decoder.Frames.ToArray();
        _gifFrameIndex = 0;
        PreviewImage.Source = _gifFrames[0];
        PreviewImage.Visibility = Visibility.Visible;
        PreviewVideo.Visibility = Visibility.Collapsed;
        HintText.Text = $"GIF 动图 · 共 {_gifFrames.Length} 帧，自动循环播放。";

        _gifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _gifTimer.Tick += (_, _) =>
        {
            if (_gifFrames == null || _gifFrames.Length == 0) return;
            _gifFrameIndex = (_gifFrameIndex + 1) % _gifFrames.Length;
            PreviewImage.Source = _gifFrames[_gifFrameIndex];
        };
        _gifTimer.Start();
    }

    private void LoadVideo(string path)
    {
        _isVideo = true;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewVideo.Visibility = Visibility.Visible;
        PreviewVideo.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        // 预览默认带声音（跟正式壁纸场景不一样，这里是用户主动点开看效果，
        // 听得到声音才知道这段视频到底带不带声轨），给一个静音按钮方便随时关掉。
        PreviewVideo.Volume = 1.0;
        PreviewVideo.IsMuted = false;
        MuteToggleButton.Visibility = Visibility.Visible;
        MuteToggleButton.Content = "静音";
        HintText.Text = "视频 · 自动循环播放。";
        PreviewVideo.Play();
    }

    private void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        PreviewVideo.IsMuted = !PreviewVideo.IsMuted;
        MuteToggleButton.Content = PreviewVideo.IsMuted ? "取消静音" : "静音";
    }

    private void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        PreviewVideo.Position = TimeSpan.Zero;
        PreviewVideo.Play();
    }

    private void PreviewVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        HintText.Text = "视频播放失败：" + e.ErrorException?.Message;
        ErrorPresenter.LogTechnicalDetail($"[背景视频预览播放失败] {e.ErrorException}");
    }

    /// <summary>关闭预览时必须停掉计时器/视频播放并释放文件引用，否则用户紧接着在候选池
    /// 列表里删除/重命名同一个文件，会复现"文件被占用"的老问题（见导入背景图片处的注释）。</summary>
    private void StopPlayback()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
        _gifFrames = null;
        if (_isVideo)
        {
            PreviewVideo.Stop();
            PreviewVideo.Source = null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(null);
}
