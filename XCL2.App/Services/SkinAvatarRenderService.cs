using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「皮肤头像生成器」：从一张标准 Minecraft 皮肤贴图(64x64，8x8 每格)里裁剪出
/// "脸部"图层(基础层 8,8 起 8x8 像素)和"帽子"图层(叠加层 40,8 起 8x8 像素，也就是
/// 玩家戴的"第二层"头饰，比如帽子/头发装饰)，按最近邻(NearestNeighbor)放大合成成一张
/// 方形头像 PNG——这是 Minecraft 皮肤玩家头像渲染最常见的做法（Crafatar/NameMC 等
/// 第三方头像服务都是这个思路），用最近邻缩放而不是双线性，是为了保留像素风格的锐利边缘，
/// 避免放大后头像糊成一团、丢失"像素画"的观感。
///
/// 不依赖任何第三方图像库(System.Drawing/ImageSharp 等)，纯用 WPF 自带的
/// System.Windows.Media.Imaging 完成裁剪/合成/编码，跟项目现有的"没有引入额外图像处理
/// NuGet 包"这个既有选择保持一致。
/// </summary>
public static class SkinAvatarRenderService
{
    /// <summary>
    /// 从皮肤 PNG 字节生成头像 PNG 字节。
    /// </summary>
    /// <param name="skinPngBytes">原始皮肤贴图的 PNG 字节。</param>
    /// <param name="outputSize">目标头像边长(像素)，如 64/128/256。</param>
    /// <param name="includeHatLayer">是否叠加"帽子"图层(第二层头饰)。截图里的头像生成器
    /// 没有单独暴露这个开关，这里默认 true——大多数玩家的第二层头饰是空白透明的，
    /// 叠加上去不会有任何视觉差异；有头饰的玩家叠加上去才是"完整"的头像。</param>
    public static byte[] RenderFaceAvatar(byte[] skinPngBytes, int outputSize, bool includeHatLayer = true)
    {
        using var ms = new MemoryStream(skinPngBytes);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];

        // 皮肤贴图有 64x64（新格式，含完整第二层）和 64x32（旧格式，无第二层）两种规格，
        // 旧格式没有"帽子"图层坐标，这里按贴图实际高度判断是否要跳过第二层合成。
        var hasSecondLayer = source.PixelHeight >= 64;

        var baseFace = CropRegion(source, 8, 8, 8, 8);
        var scaledBase = ScaleNearestNeighbor(baseFace, outputSize, outputSize);

        if (!includeHatLayer || !hasSecondLayer)
        {
            return EncodePng(scaledBase);
        }

        var hatLayer = CropRegion(source, 40, 8, 8, 8);
        var scaledHat = ScaleNearestNeighbor(hatLayer, outputSize, outputSize);

        var composed = ComposeOver(scaledBase, scaledHat, outputSize, outputSize);
        return EncodePng(composed);
    }

    private static CroppedBitmap CropRegion(BitmapSource source, int x, int y, int width, int height)
        => new(source, new System.Windows.Int32Rect(x, y, width, height));

    /// <summary>最近邻放大：逐目标像素反查源像素，保持像素风格的硬边缘，不做任何插值模糊。</summary>
    private static WriteableBitmap ScaleNearestNeighbor(BitmapSource source, int targetWidth, int targetHeight)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var srcW = converted.PixelWidth;
        var srcH = converted.PixelHeight;
        var srcStride = srcW * 4;
        var srcPixels = new byte[srcStride * srcH];
        converted.CopyPixels(srcPixels, srcStride, 0);

        var dst = new WriteableBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Bgra32, null);
        var dstStride = targetWidth * 4;
        var dstPixels = new byte[dstStride * targetHeight];

        for (var dy = 0; dy < targetHeight; dy++)
        {
            var sy = Math.Min(srcH - 1, dy * srcH / targetHeight);
            for (var dx = 0; dx < targetWidth; dx++)
            {
                var sx = Math.Min(srcW - 1, dx * srcW / targetWidth);
                var srcIdx = sy * srcStride + sx * 4;
                var dstIdx = dy * dstStride + dx * 4;
                Array.Copy(srcPixels, srcIdx, dstPixels, dstIdx, 4);
            }
        }

        dst.WritePixels(new System.Windows.Int32Rect(0, 0, targetWidth, targetHeight), dstPixels, dstStride, 0);
        return dst;
    }

    /// <summary>逐像素 alpha 混合叠加：把 overlay(第二层头饰) 盖在 baseLayer(脸部基础层) 上面。
    /// 手写 alpha-over 而不是用 DrawingVisual + DrawImage，是因为 overlay 里大量像素是完全
    /// 透明的(没有头饰的玩家)，逐像素混合能正确处理透明度叠加，避免"看起来盖住了脸"的问题。</summary>
    private static WriteableBitmap ComposeOver(WriteableBitmap baseLayer, WriteableBitmap overlay, int width, int height)
    {
        var stride = width * 4;
        var basePixels = new byte[stride * height];
        var overlayPixels = new byte[stride * height];
        baseLayer.CopyPixels(basePixels, stride, 0);
        overlay.CopyPixels(overlayPixels, stride, 0);

        var result = new byte[stride * height];
        for (var i = 0; i < result.Length; i += 4)
        {
            // Bgra32：字节顺序 B,G,R,A
            var overlayAlpha = overlayPixels[i + 3] / 255.0;
            if (overlayAlpha <= 0.0)
            {
                Array.Copy(basePixels, i, result, i, 4);
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                result[i + c] = (byte)(overlayPixels[i + c] * overlayAlpha + basePixels[i + c] * (1 - overlayAlpha));
            }
            result[i + 3] = (byte)Math.Min(255, overlayPixels[i + 3] + basePixels[i + 3] * (1 - overlayAlpha));
        }

        var dst = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        dst.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), result, stride, 0);
        return dst;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
