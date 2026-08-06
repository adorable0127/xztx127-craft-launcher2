using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「自定义成就图片生成器」：仿 Minecraft 游戏内"达成进度"提示条的样式，
/// 生成一张静态 PNG，供玩家做截图分享/视频素材。
///
/// ===== 这一版修了什么（"那个物品没有正常的格式"）=====
///
/// 旧实现有三个问题，合起来就是"物品那一块看着不对劲"：
///
/// 1) **物品图标只画了一个字母。**
///    旧代码取 itemId 冒号后那段的首字母画进方块里，所以填 "minecraft:diamond"
///    出来是一个大写的 "D"，完全看不出是钻石——跟原版提示里左边是**物品贴图**的
///    观感差得很远，这是最直观的"格式不正常"。
///
/// 2) **空段会画出 NUL 字符。**
///    旧代码是 `...Last().TrimStart().ToUpperInvariant().FirstOrDefault().ToString()`。
///    FirstOrDefault() 作用在字符串上返回 char，空字符串时返回 '\0'，.ToString() 得到 "\0"。
///    用户只要填成 "minecraft:"（或末尾多打一个冒号），画出来就是个渲染不出的豆腐块，
///    而不是回退成 "?"。
///
/// 3) **物品 ID 完全不做校验/归一化。**
///    Minecraft 资源 ID 有明确规则：namespace:path，命名空间只允许 [a-z0-9_.-]，
///    路径只允许 [a-z0-9_./-]，不写命名空间时默认 minecraft。旧实现原样接收任何输入，
///    用户填 "Diamond Sword" 或"钻石"都照单全收。
///
/// 现在的做法：
/// - NormalizeItemId 把输入规整成合法的 namespace:path（转小写、空格转下划线、
///   补默认命名空间、剔除非法字符），并告诉调用方是否改过，界面可以提示"已自动更正为 xxx"；
/// - 图标改成**按物品类别画的矢量图形**（剑/镐/斧/锹/锭/宝石/苹果/药水/书/方块），
///   颜色从名字里的材质关键词推断（diamond→青、gold→金、netherite→深褐…），
///   认不出类别时退回等距像素方块而不是字母。
///
/// 所有图形都是自己用几何图元画的，**不使用任何原版游戏资源文件**，
/// 不涉及原版素材再分发的问题（跟旧版的设计约束保持一致）。
/// </summary>
public static class AchievementImageService
{
    private static readonly Regex NamespaceInvalid = new("[^a-z0-9_.-]", RegexOptions.Compiled);
    private static readonly Regex PathInvalid = new("[^a-z0-9_./-]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>归一化结果。WasChanged 供界面提示"已自动更正为 minecraft:diamond_sword"。</summary>
    public sealed record NormalizedItemId(string FullId, string Namespace, string Path, bool WasChanged);

    /// <summary>
    /// 把用户随手填的东西规整成合法的 Minecraft 物品 ID。
    /// 例："Diamond Sword" → "minecraft:diamond_sword"；"diamond" → "minecraft:diamond"；
    ///     "minecraft:" → "minecraft:air"（路径空了退回 air，不留空段——这正是旧版画出 NUL 的根因）。
    /// </summary>
    public static NormalizedItemId NormalizeItemId(string? raw)
    {
        var original = (raw ?? "").Trim();
        var s = Whitespace.Replace(original.ToLowerInvariant(), "_");

        string ns, path;
        var colon = s.IndexOf(':');
        if (colon < 0)
        {
            ns = "minecraft";
            path = s;
        }
        else
        {
            ns = s[..colon];
            path = s[(colon + 1)..].Replace(":", ""); // 多余的冒号是非法的，直接去掉
        }

        ns = NamespaceInvalid.Replace(ns, "");
        path = PathInvalid.Replace(path, "");

        if (string.IsNullOrEmpty(ns)) ns = "minecraft";
        if (string.IsNullOrEmpty(path)) path = "air";

        var full = ns + ":" + path;
        return new NormalizedItemId(full, ns, path, !string.Equals(full, original, StringComparison.Ordinal));
    }

    /// <summary>生成成就提示图片。itemId 内部会自动归一化。</summary>
    public static byte[] Generate(string itemId, string achievementName, string firstLine, string? secondLine)
    {
        var id = NormalizeItemId(itemId);

        const int width = 520;
        const int height = 80;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bgBrush = new SolidColorBrush(Color.FromArgb(235, 28, 28, 30));
            var borderBrush = new SolidColorBrush(Color.FromArgb(255, 12, 12, 14));
            dc.DrawRoundedRectangle(bgBrush, new Pen(borderBrush, 2), new Rect(1, 1, width - 2, height - 2), 3, 3);

            DrawItemIcon(dc, new Rect(12, 12, 56, 56), id.Path);

            const double textX = 84.0;

            var achievementText = new FormattedText(
                string.IsNullOrWhiteSpace(achievementName) ? "Advancement made!" : achievementName,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, new SolidColorBrush(Color.FromRgb(255, 215, 0)), 1.25);
            dc.DrawText(achievementText, new Point(textX, 11));

            var firstLineText = new FormattedText(
                firstLine ?? "",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 20, Brushes.White, 1.25);
            firstLineText.MaxTextWidth = width - textX - 14;
            firstLineText.MaxLineCount = 1;
            firstLineText.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(firstLineText, new Point(textX, 29));

            if (!string.IsNullOrWhiteSpace(secondLine))
            {
                var secondLineText = new FormattedText(
                    secondLine,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 13, new SolidColorBrush(Color.FromRgb(196, 196, 200)), 1.25);
                secondLineText.MaxTextWidth = width - textX - 14;
                secondLineText.MaxLineCount = 1;
                secondLineText.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(secondLineText, new Point(textX, 55));
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

    // ==================== 物品图标绘制 ====================

    /// <summary>
    /// 按物品 ID 推断材质颜色。规则来自物品命名本身（diamond_sword / golden_apple 这种
    /// 前缀就是材质）；认不出来时按 ID 稳定哈希取色相——同一个 ID 每次生成颜色一致。
    /// </summary>
    private static (Color Main, Color Dark) GuessMaterialColor(string path)
    {
        static (Color, Color) Pair(byte r, byte g, byte b) =>
            (Color.FromRgb(r, g, b), Color.FromRgb((byte)(r * 0.62), (byte)(g * 0.62), (byte)(b * 0.62)));

        if (path.Contains("netherite")) return Pair(76, 66, 68);
        if (path.Contains("diamond")) return Pair(92, 219, 213);
        if (path.Contains("gold")) return Pair(249, 200, 72);
        if (path.Contains("iron")) return Pair(216, 216, 216);
        if (path.Contains("emerald")) return Pair(63, 191, 118);
        if (path.Contains("lapis")) return Pair(48, 88, 178);
        if (path.Contains("redstone")) return Pair(203, 42, 32);
        if (path.Contains("copper")) return Pair(199, 118, 78);
        if (path.Contains("amethyst")) return Pair(154, 108, 214);
        if (path.Contains("cobble") || path.Contains("stone")) return Pair(130, 130, 130);
        if (path.Contains("plank") || path.Contains("wood") || path.Contains("oak")) return Pair(162, 130, 78);
        if (path.Contains("leather")) return Pair(160, 101, 64);
        if (path.Contains("apple")) return Pair(216, 54, 44);
        if (path.Contains("grass") || path.Contains("leaves")) return Pair(94, 168, 72);
        if (path.Contains("water")) return Pair(60, 110, 220);
        if (path.Contains("lava") || path.Contains("fire") || path.Contains("blaze")) return Pair(232, 116, 32);
        if (path.Contains("ender") || path.Contains("chorus")) return Pair(38, 132, 122);
        if (path.Contains("coal")) return Pair(48, 48, 48);
        if (path.Contains("quartz") || path.Contains("bone")) return Pair(232, 228, 214);

        var h = 0;
        foreach (var c in path) h = unchecked(h * 31 + c);
        var hue = Math.Abs(h) % 360;
        var col = HsvToRgb(hue, 0.55, 0.82);
        return (col, Color.FromRgb((byte)(col.R * 0.62), (byte)(col.G * 0.62), (byte)(col.B * 0.62)));
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>按 ID 里的类别关键词选形状绘制。全部是自绘几何图形，不引用原版贴图。</summary>
    private static void DrawItemIcon(DrawingContext dc, Rect box, string path)
    {
        var (main, dark) = GuessMaterialColor(path);
        Brush mainBrush = new SolidColorBrush(main);
        Brush darkBrush = new SolidColorBrush(dark);
        Brush handleBrush = new SolidColorBrush(Color.FromRgb(140, 106, 62));

        // 物品栏格子观感：深色内凹方块
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(255, 58, 58, 62)),
            new Pen(new SolidColorBrush(Color.FromArgb(255, 22, 22, 24)), 2), box);

        var r = new Rect(box.X + 8, box.Y + 8, box.Width - 16, box.Height - 16);

        double X(double t) => r.X + r.Width * t;
        double Y(double t) => r.Y + r.Height * t;

        void Poly(Brush brush, params double[] xy)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(xy[0]), Y(xy[1])), true, true);
                for (var i = 2; i < xy.Length; i += 2)
                    ctx.LineTo(new Point(X(xy[i]), Y(xy[i + 1])), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(brush, null, geo);
        }

        void Rect01(Brush brush, double x0, double y0, double x1, double y1) =>
            dc.DrawRectangle(brush, null, new Rect(X(x0), Y(y0), X(x1) - X(x0), Y(y1) - Y(y0)));

        if (path.Contains("sword"))
        {
            Rect01(handleBrush, 0.42, 0.70, 0.58, 0.98);
            Rect01(darkBrush, 0.26, 0.62, 0.74, 0.72);
            Poly(mainBrush, 0.5, 0.02, 0.66, 0.20, 0.62, 0.64, 0.38, 0.64, 0.34, 0.20);
        }
        else if (path.Contains("pickaxe"))
        {
            Rect01(handleBrush, 0.44, 0.30, 0.56, 0.98);
            Poly(mainBrush, 0.06, 0.28, 0.30, 0.08, 0.70, 0.08, 0.94, 0.28,
                            0.80, 0.30, 0.62, 0.20, 0.38, 0.20, 0.20, 0.30);
        }
        else if (path.Contains("axe"))
        {
            Rect01(handleBrush, 0.46, 0.16, 0.58, 0.98);
            Poly(mainBrush, 0.46, 0.10, 0.86, 0.16, 0.88, 0.48, 0.46, 0.54);
        }
        else if (path.Contains("shovel") || path.Contains("spade"))
        {
            Rect01(handleBrush, 0.44, 0.30, 0.56, 0.98);
            Poly(mainBrush, 0.30, 0.06, 0.70, 0.06, 0.66, 0.40, 0.34, 0.40);
        }
        else if (path.Contains("ingot"))
        {
            Poly(mainBrush, 0.16, 0.34, 0.84, 0.34, 0.96, 0.70, 0.04, 0.70);
            Poly(darkBrush, 0.16, 0.34, 0.84, 0.34, 0.78, 0.44, 0.22, 0.44);
        }
        else if (path.Contains("apple") || path.Contains("berry") || path.Contains("melon"))
        {
            dc.DrawEllipse(mainBrush, null, new Point(X(0.5), Y(0.58)), r.Width * 0.36, r.Height * 0.34);
            Rect01(new SolidColorBrush(Color.FromRgb(110, 78, 44)), 0.47, 0.12, 0.53, 0.28);
            Poly(new SolidColorBrush(Color.FromRgb(94, 168, 72)), 0.53, 0.18, 0.80, 0.10, 0.66, 0.28);
        }
        else if (path.Contains("potion") || path.Contains("bottle"))
        {
            Rect01(new SolidColorBrush(Color.FromRgb(190, 190, 195)), 0.42, 0.06, 0.58, 0.24);
            dc.DrawEllipse(mainBrush, new Pen(darkBrush, 1.5),
                new Point(X(0.5), Y(0.66)), r.Width * 0.34, r.Height * 0.30);
        }
        else if (path.Contains("book") || path.Contains("enchant"))
        {
            Rect01(new SolidColorBrush(Color.FromRgb(150, 52, 44)), 0.14, 0.14, 0.86, 0.86);
            Rect01(new SolidColorBrush(Color.FromRgb(238, 232, 214)), 0.24, 0.20, 0.86, 0.80);
            Rect01(new SolidColorBrush(Color.FromRgb(120, 40, 34)), 0.14, 0.14, 0.24, 0.86);
        }
        else if (path.Contains("diamond") || path.Contains("emerald") || path.Contains("amethyst")
                 || path.Contains("gem") || path.Contains("shard"))
        {
            Poly(mainBrush, 0.5, 0.06, 0.92, 0.40, 0.5, 0.94, 0.08, 0.40);
            Poly(darkBrush, 0.5, 0.06, 0.92, 0.40, 0.5, 0.48, 0.08, 0.40);
        }
        else
        {
            // 兜底：等距像素方块，比一个字母像"游戏里的物品"得多
            Poly(mainBrush, 0.5, 0.06, 0.94, 0.30, 0.5, 0.54, 0.06, 0.30);
            Poly(darkBrush, 0.06, 0.30, 0.5, 0.54, 0.5, 0.96, 0.06, 0.72);
            Poly(new SolidColorBrush(Color.FromRgb(
                    (byte)(main.R * 0.80), (byte)(main.G * 0.80), (byte)(main.B * 0.80))),
                0.94, 0.30, 0.94, 0.72, 0.5, 0.96, 0.5, 0.54);
        }
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
