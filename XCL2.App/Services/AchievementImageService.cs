using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「自定义成就图片生成器」：仿 Minecraft 游戏内"获得成就"弹出提示的样式，
/// 生成一张静态 PNG 图片，供玩家截图分享/做视频素材用（截图注明"仅支持英文"——原版
/// 成就提示用的字体本身对中文支持有限，这里的文字排版/字号也是照原版英文提示的比例
/// 设计的，跟着这个约束走，不额外做中文换行/字号自适应）。
///
/// 纯用 WPF DrawingVisual + RenderTargetBitmap 手绘，不依赖任何原版游戏资源文件
/// （不从游戏 jar 里提取真实的成就框图片素材，避免涉及任何原版资源的再分发问题），
/// 用纯色矩形 + 描边模拟原版的"石头灰底+金色文字+进度条式左侧色块"观感，形似而非
/// 逐像素复刻。
/// </summary>
public static class AchievementImageService
{
    /// <summary>
    /// 生成成就提示图片。
    /// </summary>
    /// <param name="itemId">物品 ID（仅用于展示，比如 "minecraft:diamond"，不真的去解析物品图标，
    /// 因为不引入游戏资源文件；这里用 itemId 的首字母生成一个简单的色块图标占位）。</param>
    /// <param name="achievementName">成就名（顶部小字，如 "Achievement Get!"）。</param>
    /// <param name="firstLine">主标题行（大字）。</param>
    /// <param name="secondLine">可选的副标题行（小字，留空则不显示）。</param>
    public static byte[] Generate(string itemId, string achievementName, string firstLine, string? secondLine)
    {
        const int width = 520;
        const int height = 80;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 背景：深灰石头色半透明底，圆角矩形描边，模拟原版成就 Toast 的底板。
            var bgBrush = new SolidColorBrush(Color.FromArgb(230, 40, 40, 40));
            var borderBrush = new SolidColorBrush(Color.FromArgb(255, 20, 20, 20));
            dc.DrawRoundedRectangle(bgBrush, new Pen(borderBrush, 2), new Rect(1, 1, width - 2, height - 2), 4, 4);

            // 左侧图标占位：用物品 ID 的首字母(大写)画在一个金色边框方块里，代表"物品图标"。
            var iconRect = new Rect(10, 10, 60, 60);
            var iconBg = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70));
            var iconBorder = new SolidColorBrush(Color.FromArgb(255, 255, 215, 0));
            dc.DrawRectangle(iconBg, new Pen(iconBorder, 2), iconRect);

            var iconLetter = string.IsNullOrWhiteSpace(itemId)
                ? "?"
                : itemId.Split(':').Last().TrimStart().ToUpperInvariant().FirstOrDefault().ToString();
            var iconText = new FormattedText(
                iconLetter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Black"), 28, Brushes.White, 1.25);
            dc.DrawText(iconText, new Point(
                iconRect.X + (iconRect.Width - iconText.Width) / 2,
                iconRect.Y + (iconRect.Height - iconText.Height) / 2));

            // 右侧文字区：顶部小字"achievementName"(金色)，主标题(白色大字)，可选副标题(浅灰小字)。
            var textX = 84;

            var achievementText = new FormattedText(
                string.IsNullOrWhiteSpace(achievementName) ? "Achievement Get!" : achievementName,
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, new SolidColorBrush(Color.FromRgb(255, 215, 0)), 1.25);
            dc.DrawText(achievementText, new Point(textX, 12));

            var firstLineText = new FormattedText(
                firstLine ?? "",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 20, Brushes.White, 1.25);
            dc.DrawText(firstLineText, new Point(textX, 30));

            if (!string.IsNullOrWhiteSpace(secondLine))
            {
                var secondLineText = new FormattedText(
                    secondLine,
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 13, new SolidColorBrush(Color.FromRgb(200, 200, 200)), 1.25);
                dc.DrawText(secondLineText, new Point(textX, 54));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static string SaveToFile(byte[] pngBytes, string saveDir, string fileName)
    {
        Directory.CreateDirectory(saveDir);
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";
        var path = Path.Combine(saveDir, fileName);
        File.WriteAllBytes(path, pngBytes);
        return path;
    }
}
