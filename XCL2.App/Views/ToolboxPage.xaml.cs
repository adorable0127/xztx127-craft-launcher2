using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 「百宝箱」页：正式功能范围内的一批小工具集合（成就图片生成、皮肤头像生成、
/// 自定义文件下载、正版皮肤下载、加载器 Jar 单独下载、清理游戏垃圾、创建快捷方式、
/// 查看启动计数、内存优化），不属于「实验性功能」，参见 ExperimentalGateWindow.xaml
/// 里"不属于实验性功能"那段说明。
///
/// XAML 界面（5 个 Tab）早就搭好了，这个文件是把每个按钮点击事件接上对应的、
/// 已经写好并有完整实现的 Service：AchievementImageService / SkinAvatarRenderService /
/// OfficialSkinFetchService / JunkCleanupService / ShortcutService /
/// ClientLoaderInstallService / MemoryOptimizerService，这里只是"胶水代码"，
/// 不重新实现任何一个功能的核心逻辑。
/// </summary>
public partial class ToolboxPage : UserControl
{
    private readonly MainWindow _owner;

    // ===== Tab 1：成就图片 =====
    private byte[]? _achPreviewBytes;

    // ===== Tab 2：皮肤头像 =====
    private string? _avatarSkinPath;
    private byte[]? _avatarPreviewBytes;

    // ===== Tab 3：文件下载 / 正版皮肤 =====
    private readonly HttpClient _dlHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly OfficialSkinFetchService _officialSkinService = new();
    private OfficialSkinFetchService.OfficialSkinInfo? _lookedUpSkinInfo;
    private bool _dlInProgress;

    // ===== Tab 4：加载器下载 =====
    private ClientLoaderInstallService? _loaderService;
    private string? _selectedLoaderTag;

    public ToolboxPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        _dlHttp.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher-Toolbox/1.0");

        RefreshAchBedrockDirText();
        MemOptCheck.IsChecked = _owner.ConfigService.Config.EnableMemoryOptimization;
        BuildColorCodeSwatches();
        ColorCodeInputBox_TextChanged(this, null!);

        // ===== Tab 12：系统内存监视 =====
        // 打开百宝箱页时立即刷新一次，之后每 2 秒自动刷新（PageUnloaded 时停掉，
        // 避免用户切到别的页面后这个定时器还在后台白跑）。
        MemMonRefresh_Click(this, null!);
        _memMonTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _memMonTimer.Tick += (_, _) => RefreshMemoryMonitor();
        _memMonTimer.Start();
        Unloaded += (_, _) => _memMonTimer.Stop();

        // ===== Tab 13：离线 UUID 生成器 =====
        OfflineUuidNameBox_TextChanged(this, null!);

        // ===== Tab 14：JVM 参数生成器 =====
        RefreshJvmArgs();

        // ===== Tab 16：快捷键速查表 =====
        BuildKeyRefList();

        // ===== Tab 18：经验等级计算器 =====
        XpLevelBox_TextChanged(this, null!);

        // ===== Tab 19：坐标距离计算器 =====
        RefreshDistanceResult();

        // ===== Tab 20：常用指令生成器 =====
        RefreshCmdGenResult();

        // ===== Tab 21：Base64 编码/解码 =====
        B64PlainBox_TextChanged(this, null!);

        // ===== Tab 22：游戏内时间换算器 =====
        GtTickBox_TextChanged(this, null!);

        // ===== Tab 23：物品堆叠 / 箱子格子计算器 =====
        RefreshStackCalc();

        // ===== Tab 24：RGB 转 MC 文本颜色代码 =====
        RgbConvert_TextChanged(this, null!);

        // ===== Tab 25：红石中继器延时计算器 =====
        RefreshRedstoneCalc();

        // ===== Tab 27：服务器 MOTD 长度检查器 =====
        MotdInputBox_TextChanged(this, null!);

        // ===== Tab 28：时间戳转换器 =====
        TsNow_Click(this, null!);

        // ===== Tab 29：服务器地址解析器 =====
        AddrParseInputBox_TextChanged(this, null!);

        // ===== Tab 30：药水持续时间换算 =====
        PotionSecBox_TextChanged(this, null!);

        // ===== Tab 31：颜色代码速查表 =====
        BuildColorRefList();

        // ===== Tab 32：附魔台书架数量对照 =====
        BookshelfCountBox_TextChanged(this, null!);
    }

    // ============================================================
    // Tab 28：Unix 时间戳 / 日期时间互转
    // ============================================================

    private bool _tsUpdating;

    private void TsNow_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        TsInputBox.Text = now.ToUnixTimeSeconds().ToString();
    }

    private void TsInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_tsUpdating || TsResultText == null) return;
        var text = (TsInputBox.Text ?? "").Trim();
        if (!long.TryParse(text, out var value))
        {
            TsResultText.Text = "请输入一个整数时间戳（自动识别秒级或毫秒级）。";
            return;
        }

        // 13 位左右当作毫秒级，10 位左右当作秒级，粗略按数量级判断，够覆盖常见范围。
        DateTimeOffset dto;
        try
        {
            dto = Math.Abs(value) >= 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch
        {
            TsResultText.Text = "这个数值超出了可换算的时间范围，请检查输入。";
            return;
        }

        _tsUpdating = true;
        try
        {
            var local = dto.ToLocalTime();
            TsDateBox.Text = local.ToString("yyyy-MM-dd HH:mm:ss");
            TsResultText.Text = $"本地时间：{local:yyyy-MM-dd HH:mm:ss}（{local:zzz}）；UTC：{dto.UtcDateTime:yyyy-MM-dd HH:mm:ss}。";
        }
        finally { _tsUpdating = false; }
    }

    private void TsDateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_tsUpdating || TsResultText == null) return;
        var text = (TsDateBox.Text ?? "").Trim();
        if (!DateTime.TryParse(text, out var dt))
        {
            TsResultText.Text = "日期时间格式无法识别，建议使用 yyyy-MM-dd HH:mm:ss。";
            return;
        }

        _tsUpdating = true;
        try
        {
            var dto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
            TsInputBox.Text = dto.ToUnixTimeSeconds().ToString();
            TsResultText.Text = $"对应 Unix 秒级时间戳：{dto.ToUnixTimeSeconds()}；毫秒级：{dto.ToUnixTimeMilliseconds()}。";
        }
        finally { _tsUpdating = false; }
    }

    // ============================================================
    // Tab 29：服务器地址解析 / 格式校验
    // ============================================================

    private void AddrParseInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AddrParseHostText == null) return;

        var input = (AddrParseInputBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(input))
        {
            AddrParseHostText.Text = "--";
            AddrParsePortText.Text = "--";
            AddrParseStatusText.Text = "";
            return;
        }

        string host;
        string portText;
        var lastColon = input.LastIndexOf(':');
        if (lastColon > 0 && lastColon < input.Length - 1)
        {
            host = input[..lastColon];
            portText = input[(lastColon + 1)..];
        }
        else
        {
            host = input;
            portText = "25565"; // Java 版默认端口
        }

        AddrParseHostText.Text = host;

        var portOk = int.TryParse(portText, out var port) && port is > 0 and <= 65535;
        AddrParsePortText.Text = portOk ? port.ToString() : $"{portText}（无效）";

        var hostOk = System.Text.RegularExpressions.Regex.IsMatch(host,
            @"^(([A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*|(\d{1,3}\.){3}\d{1,3})$");

        if (hostOk && portOk)
        {
            AddrParseStatusText.Text = "✔ 格式看起来正常，可以直接使用。";
            AddrParseStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessTextBrush");
        }
        else
        {
            var reasons = new List<string>();
            if (!hostOk) reasons.Add("主机名/IP 格式不太对");
            if (!portOk) reasons.Add("端口需要是 1~65535 之间的数字");
            AddrParseStatusText.Text = "✘ " + string.Join("；", reasons) + "。";
            AddrParseStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    // ============================================================
    // Tab 30：药水效果持续时间换算
    // ============================================================

    private bool _potionUpdating;

    private void PotionSecBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_potionUpdating || PotionResultText == null) return;
        if (!double.TryParse(PotionSecBox.Text.Trim(), out var seconds) || seconds < 0)
        {
            PotionResultText.Text = "请输入一个非负的秒数。";
            return;
        }
        _potionUpdating = true;
        try
        {
            var ticks = seconds * 20;
            PotionTickBox.Text = ticks.ToString("0");
            PotionResultText.Text = $"{seconds:0.##} 秒 = {ticks:0} 游戏 tick（/effect give 指令里直接填秒数即可，不需要自己转 tick）。";
        }
        finally { _potionUpdating = false; }
    }

    private void PotionTickBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_potionUpdating || PotionResultText == null) return;
        if (!double.TryParse(PotionTickBox.Text.Trim(), out var ticks) || ticks < 0)
        {
            PotionResultText.Text = "请输入一个非负的 tick 数。";
            return;
        }
        _potionUpdating = true;
        try
        {
            var seconds = ticks / 20.0;
            PotionSecBox.Text = seconds.ToString("0.##");
            PotionResultText.Text = $"{ticks:0} 游戏 tick = {seconds:0.##} 秒。";
        }
        finally { _potionUpdating = false; }
    }

    // ============================================================
    // Tab 31：颜色代码速查表
    // ============================================================

    private static readonly (string Code, string Name, string Hex)[] ColorRefData =
    {
        ("§0", "黑色 Black", "#000000"),
        ("§1", "深蓝 Dark Blue", "#0000AA"),
        ("§2", "深绿 Dark Green", "#00AA00"),
        ("§3", "湖蓝 Dark Aqua", "#00AAAA"),
        ("§4", "深红 Dark Red", "#AA0000"),
        ("§5", "紫色 Dark Purple", "#AA00AA"),
        ("§6", "金色 Gold", "#FFAA00"),
        ("§7", "灰色 Gray", "#AAAAAA"),
        ("§8", "深灰 Dark Gray", "#555555"),
        ("§9", "蓝色 Blue", "#5555FF"),
        ("§a", "绿色 Green", "#55FF55"),
        ("§b", "天蓝 Aqua", "#55FFFF"),
        ("§c", "红色 Red", "#FF5555"),
        ("§d", "粉色 Light Purple", "#FF55FF"),
        ("§e", "黄色 Yellow", "#FFFF55"),
        ("§f", "白色 White", "#FFFFFF"),
        ("§k", "随机乱码 Obfuscated", "--"),
        ("§l", "粗体 Bold", "--"),
        ("§m", "删除线 Strikethrough", "--"),
        ("§n", "下划线 Underline", "--"),
        ("§o", "斜体 Italic", "--"),
        ("§r", "重置 Reset", "--"),
    };

    private void BuildColorRefList()
    {
        ColorRefList.Items.Clear();
        foreach (var (code, name, hex) in ColorRefData)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var codeBadge = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("SideBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            codeBadge.Child = new TextBlock { Text = code, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
            Grid.SetColumn(codeBadge, 0);
            row.Children.Add(codeBadge);

            if (hex != "--")
            {
                var swatch = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(3),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("DividerBrush"),
                    BorderThickness = new Thickness(1),
                    Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex))
                };
                Grid.SetColumn(swatch, 1);
                row.Children.Add(swatch);
            }

            var nameText = new TextBlock
            {
                Text = name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(nameText, 2);
            row.Children.Add(nameText);

            var hexText = new TextBlock
            {
                Text = hex,
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hexText, 3);
            row.Children.Add(hexText);

            ColorRefList.Items.Add(row);
        }
    }

    // ============================================================
    // Tab 32：附魔台书架数量对照
    // ============================================================

    private void BookshelfCountBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BookshelfResultText == null) return;

        if (!int.TryParse(BookshelfCountBox.Text.Trim(), out var count))
        {
            BookshelfResultText.Text = "请输入一个整数。";
            return;
        }

        if (count < 0) count = 0;
        if (count > 15) count = 15;

        // 附魔台顶层等级 = min(书架数, 15)，最高档等级列表大致按官方数据表呈线性 + 微调，
        // 这里给出的是三个候选项（1~30 级）里能出现的最高等级参考值，足够日常摆放参考使用。
        var maxLevel = count switch
        {
            >= 15 => 30,
            >= 12 => 27,
            >= 9 => 22,
            >= 6 => 17,
            >= 3 => 12,
            >= 1 => 5,
            _ => 1
        };

        BookshelfResultText.Text = $"{count} 个书架：三个候选附魔中最高档大约能开到 {maxLevel} 级左右" +
                                    (count < 15 ? $"（满级 30 需要摆够 15 个书架，还差 {15 - count} 个）。" : "（已达到满级书架数）。");
    }

    // ============================================================
    // Tab 23：物品堆叠 / 箱子格子计算器
    // ============================================================

    private void StackCalc_TextChanged(object sender, TextChangedEventArgs e) => RefreshStackCalc();
    private void StackCalc_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshStackCalc();

    private void RefreshStackCalc()
    {
        if (StackResultText == null) return;

        if (!long.TryParse(StackCountBox.Text.Trim(), out var count) || count < 0)
        {
            StackResultText.Text = "请输入一个非负整数数量。";
            return;
        }

        var stackSize = StackSizeCombo.SelectedIndex switch
        {
            1 => 16,
            2 => 1,
            _ => 64
        };

        var stacks = (long)Math.Ceiling(count / (double)stackSize);
        var singleChests = (long)Math.Ceiling(stacks / 27.0);
        var doubleChests = (long)Math.Ceiling(stacks / 54.0);

        StackResultText.Text = $"共需要 {stacks} 组（每组 {stackSize} 个）；" +
                                $"需要 {singleChests} 个单箱（27 格）或 {doubleChests} 个双箱（54 格）才能一次性装完。";
    }

    // ============================================================
    // Tab 24：RGB / 十六进制 转 Minecraft 文本颜色代码
    // ============================================================

    private void RgbConvert_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RgbShortText == null) return;

        var hex = (RgbHexBox.Text ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^[0-9A-Fa-f]{6}$"))
        {
            RgbShortText.Text = "--";
            RgbLongText.Text = "--";
            RgbSwatch.Background = System.Windows.Media.Brushes.Transparent;
            return;
        }

        hex = hex.ToUpperInvariant();

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#" + hex);
            RgbSwatch.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        catch { RgbSwatch.Background = System.Windows.Media.Brushes.Transparent; }

        RgbShortText.Text = $"&#{hex}";

        var longSb = new StringBuilder("&x");
        foreach (var ch in hex)
            longSb.Append('&').Append(ch);
        RgbLongText.Text = longSb.ToString();
    }

    // ============================================================
    // Tab 25：红石中继器延时计算器
    // ============================================================

    private void RedstoneCalc_TextChanged(object sender, TextChangedEventArgs e) => RefreshRedstoneCalc();
    private void RedstoneCalc_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRedstoneCalc();

    private void RefreshRedstoneCalc()
    {
        if (RedstoneResultText == null) return;

        if (!int.TryParse(RedstoneCountBox.Text.Trim(), out var count) || count < 0)
        {
            RedstoneResultText.Text = "请输入一个非负整数数量。";
            return;
        }

        var tier = RedstoneTickCombo.SelectedIndex + 1; // 1~4
        var redstoneTicksEach = tier; // 每档 1 红石 tick
        var totalRedstoneTicks = (long)count * redstoneTicksEach;
        var totalGameTicks = totalRedstoneTicks * 2; // 1 红石 tick = 2 游戏 tick
        var totalSeconds = totalRedstoneTicks * 0.1;

        RedstoneResultText.Text = $"{count} 个中继器，每个 {tier} 档（{redstoneTicksEach} 红石 tick）：" +
                                   $"总延时 {totalRedstoneTicks} 红石 tick = {totalGameTicks} 游戏 tick ≈ {totalSeconds:0.#} 秒。";
    }

    // ============================================================
    // Tab 26：随机玩家名生成器
    // ============================================================

    private static readonly string[] NameGenPrefixes =
    {
        "Shadow", "Cyber", "Silent", "Frost", "Blaze", "Iron", "Neon", "Ghost",
        "Storm", "Crimson", "Lunar", "Solar", "Dark", "Swift", "Golden", "Void"
    };

    private static readonly string[] NameGenSuffixes =
    {
        "Wolf", "Hunter", "Miner", "Knight", "Fox", "Dragon", "Pixel", "Ranger",
        "Phantom", "Wizard", "Rider", "Falcon", "Reaper", "Nova", "Scout", "Blade"
    };

    private static readonly Random _nameGenRandom = new();

    private void NameGen_Click(object sender, RoutedEventArgs e)
    {
        NameGenList.Items.Clear();
        for (var i = 0; i < 8; i++)
        {
            var prefix = NameGenPrefixes[_nameGenRandom.Next(NameGenPrefixes.Length)];
            var suffix = NameGenSuffixes[_nameGenRandom.Next(NameGenSuffixes.Length)];
            var name = prefix + suffix;

            // 有一定概率加个 2~4 位数字后缀，模拟真实注册时"名字被占用了加数字"的常见样式，
            // 同时顺便控制一下长度，避免固定拼接总是刚好差一点点碰不到 16 位上限。
            if (_nameGenRandom.Next(2) == 0)
                name += _nameGenRandom.Next(10, 9999).ToString();

            if (name.Length > 16) name = name[..16];

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = name,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 0);
            row.Children.Add(nameText);

            var copyBtn = new Button { Content = "复制", Padding = new Thickness(10, 3, 10, 3) };
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(name); }
                catch { /* 剪贴板偶尔被占用，忽略即可 */ }
            };
            Grid.SetColumn(copyBtn, 1);
            row.Children.Add(copyBtn);

            NameGenList.Items.Add(row);
        }
    }

    // ============================================================
    // Tab 27：服务器 MOTD 长度 / 换行检查器
    // ============================================================

    // MOTD 每行显示宽度的粗略估算：西文字符按 1 计，中日韩等全角字符按 2 计，
    // 跟客户端多人游戏列表里实际的等宽字体渲染宽度大致对应（不追求逐像素精确，够用做提前预警）。
    private const int MotdMaxWidthPerLine = 45;

    private void MotdInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MotdLineList == null) return;

        MotdLineList.Items.Clear();
        var raw = MotdInputBox.Text ?? "";
        var lines = raw.Replace("\\n", "\n").Split('\n');

        if (lines.Length > 2)
        {
            MotdLineList.Items.Add(new TextBlock
            {
                Text = $"⚠ 一共 {lines.Length} 行，但多人游戏列表最多只显示两行，第 3 行及以后不会显示。",
                Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        var lineIndex = 0;
        foreach (var line in lines)
        {
            lineIndex++;
            if (lineIndex > 2) break;

            // 去掉 § 颜色/格式代码本身不占显示宽度，只按去码后的可见文本估算宽度。
            var visible = System.Text.RegularExpressions.Regex.Replace(line, "§.", "");
            var width = 0;
            foreach (var ch in visible)
                width += ch > 0x2E80 ? 2 : 1; // 粗略：CJK 及更高码位按全角算

            var over = width > MotdMaxWidthPerLine;

            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = $"第 {lineIndex} 行：{line}",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
            row.Children.Add(new TextBlock
            {
                Text = over
                    ? $"估算宽度 {width}（超过约 {MotdMaxWidthPerLine} 的建议上限，显示时可能被截断或挤压）"
                    : $"估算宽度 {width}（未超限）",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource(over ? "DangerBrush" : "TextSecondaryBrush")
            });

            MotdLineList.Items.Add(row);
        }
    }

    // ============================================================
    // Tab 18：经验等级计算器
    // ============================================================

    // 官方经验等级 <-> 累计经验值换算公式，分三段：0-15 / 16-30 / 31+。
    private static long XpLevelToTotal(long level)
    {
        if (level < 0) level = 0;
        if (level <= 16) return level * level + 6 * level;
        if (level <= 31) return (long)(2.5 * level * level - 40.5 * level + 360);
        return (long)(4.5 * level * level - 162.5 * level + 2220);
    }

    private static long XpLevelToNextLevelCost(long level)
    {
        if (level < 0) level = 0;
        if (level < 16) return 2 * level + 7;
        if (level < 31) return 5 * level - 38;
        return 9 * level - 158;
    }

    // 给定累计经验值，反推等级（简单线性扫描；等级上限实际场景不会很大，够用且不容易出错）。
    private static long XpTotalToLevel(long total, out long remainderIntoLevel)
    {
        if (total <= 0) { remainderIntoLevel = 0; return 0; }
        long level = 0;
        while (XpLevelToTotal(level + 1) <= total && level < 5000)
            level++;
        remainderIntoLevel = total - XpLevelToTotal(level);
        return level;
    }

    private bool _xpUpdating;

    private void XpLevelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_xpUpdating || XpTotalBox == null || XpResultText == null) return; // InitializeComponent 期间
            // TextBox 的 Text="30" 默认值会提前触发一次 TextChanged，此时同一 Tab 里排在后面的
            // 控件还没解析完（字段仍是 null），跳过，等构造函数里显式调用的那一次再处理。
        if (!long.TryParse(XpLevelBox.Text.Trim(), out var level) || level < 0)
        {
            XpResultText.Text = "请输入一个非负整数等级。";
            return;
        }
        _xpUpdating = true;
        try
        {
            var total = XpLevelToTotal(level);
            XpTotalBox.Text = total.ToString();
            var nextCost = XpLevelToNextLevelCost(level);
            XpResultText.Text = $"等级 {level} 对应累计经验值 {total}；升到等级 {level + 1} 还需要 {nextCost} 点经验。";
        }
        finally { _xpUpdating = false; }
    }

    private void XpTotalBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_xpUpdating || XpLevelBox == null || XpResultText == null) return;
        if (!long.TryParse(XpTotalBox.Text.Trim(), out var total) || total < 0)
        {
            XpResultText.Text = "请输入一个非负整数经验值。";
            return;
        }
        _xpUpdating = true;
        try
        {
            var level = XpTotalToLevel(total, out var remainder);
            XpLevelBox.Text = level.ToString();
            var nextCost = XpLevelToNextLevelCost(level);
            XpResultText.Text = $"累计经验值 {total} 对应等级 {level}（当前等级内已有 {remainder} 点）；升到等级 {level + 1} 还需要 {nextCost - remainder} 点经验。";
        }
        finally { _xpUpdating = false; }
    }

    // ============================================================
    // Tab 19：三维坐标距离计算器
    // ============================================================

    private void DistBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDistanceResult();

    private void RefreshDistanceResult()
    {
        if (Dist3DText == null) return;

        static bool TryGet(TextBox box, out double value) => double.TryParse(box.Text.Trim(), out value);

        if (!TryGet(DistAxBox, out var ax) || !TryGet(DistAyBox, out var ay) || !TryGet(DistAzBox, out var az) ||
            !TryGet(DistBxBox, out var bx) || !TryGet(DistByBox, out var by) || !TryGet(DistBzBox, out var bz))
        {
            Dist3DText.Text = "--";
            DistXZText.Text = "--";
            return;
        }

        var dx = bx - ax;
        var dy = by - ay;
        var dz = bz - az;

        var dist3D = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var distXZ = Math.Sqrt(dx * dx + dz * dz);

        Dist3DText.Text = $"{dist3D:0.##} 格";
        DistXZText.Text = $"{distXZ:0.##} 格";
    }

    // ============================================================
    // Tab 20：常用指令生成器
    // ============================================================

    private void CmdGenOption_Changed(object sender, SelectionChangedEventArgs e) => RefreshCmdGenResult();
    private void CmdGenOption_TextChanged(object sender, TextChangedEventArgs e) => RefreshCmdGenResult();

    private void RefreshCmdGenResult()
    {
        if (CmdResultText == null) return;

        var panels = new[] { CmdGivePanel, CmdTpPanel, CmdGamemodePanel, CmdTimePanel, CmdWeatherPanel };
        var selectedIndex = CmdTypeCombo.SelectedIndex;
        for (var i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == selectedIndex ? Visibility.Visible : Visibility.Collapsed;

        string result;
        switch (selectedIndex)
        {
            case 0: // /give
                {
                    var player = string.IsNullOrWhiteSpace(CmdGivePlayerBox.Text) ? "@p" : CmdGivePlayerBox.Text.Trim();
                    var item = string.IsNullOrWhiteSpace(CmdGiveItemBox.Text) ? "minecraft:diamond" : CmdGiveItemBox.Text.Trim();
                    var count = int.TryParse(CmdGiveCountBox.Text.Trim(), out var c) && c > 0 ? c : 1;
                    result = $"/give {player} {item} {count}";
                    break;
                }
            case 1: // /tp
                {
                    var player = string.IsNullOrWhiteSpace(CmdTpPlayerBox.Text) ? "@p" : CmdTpPlayerBox.Text.Trim();
                    var x = string.IsNullOrWhiteSpace(CmdTpXBox.Text) ? "0" : CmdTpXBox.Text.Trim();
                    var y = string.IsNullOrWhiteSpace(CmdTpYBox.Text) ? "64" : CmdTpYBox.Text.Trim();
                    var z = string.IsNullOrWhiteSpace(CmdTpZBox.Text) ? "0" : CmdTpZBox.Text.Trim();
                    result = $"/tp {player} {x} {y} {z}";
                    break;
                }
            case 2: // /gamemode
                {
                    var mode = CmdGamemodeCombo.SelectedIndex switch
                    {
                        1 => "creative",
                        2 => "adventure",
                        3 => "spectator",
                        _ => "survival"
                    };
                    var player = CmdGamemodePlayerBox.Text.Trim();
                    result = string.IsNullOrWhiteSpace(player) ? $"/gamemode {mode}" : $"/gamemode {mode} {player}";
                    break;
                }
            case 3: // /time set
                {
                    var time = CmdTimeCombo.SelectedIndex switch
                    {
                        1 => "noon",
                        2 => "night",
                        3 => "midnight",
                        _ => "day"
                    };
                    result = $"/time set {time}";
                    break;
                }
            case 4: // /weather
                {
                    var weather = CmdWeatherCombo.SelectedIndex switch
                    {
                        1 => "rain",
                        2 => "thunder",
                        _ => "clear"
                    };
                    result = $"/weather {weather}";
                    break;
                }
            default:
                result = "";
                break;
        }

        CmdResultText.Text = result;
    }

    private void CmdGenCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CmdResultText.Text)) return;
        try { Clipboard.SetText(CmdResultText.Text); }
        catch { /* 剪贴板偶尔被占用，忽略即可 */ }
    }

    // ============================================================
    // Tab 21：Base64 编码 / 解码
    // ============================================================

    private bool _b64Updating;

    private void B64PlainBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_b64Updating || B64EncodedBox == null) return;
        _b64Updating = true;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(B64PlainBox.Text ?? "");
            B64EncodedBox.Text = Convert.ToBase64String(bytes);
            B64StatusText.Text = "";
        }
        finally { _b64Updating = false; }
    }

    private void B64EncodedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_b64Updating || B64PlainBox == null) return;
        _b64Updating = true;
        try
        {
            var text = B64EncodedBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                B64PlainBox.Text = "";
                B64StatusText.Text = "";
                return;
            }

            var bytes = Convert.FromBase64String(text);
            B64PlainBox.Text = Encoding.UTF8.GetString(bytes);
            B64StatusText.Text = "";
        }
        catch
        {
            // 输入还没打完整的时候（比如漏了 padding）会解不出来，这里只提示不报错，
            // 等用户继续输入或换成合法内容自然就恢复了。
            B64StatusText.Text = "当前 Base64 内容不完整或不是合法的 Base64，请检查后再试。";
        }
        finally { _b64Updating = false; }
    }

    private void B64CopyEncoded_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(B64EncodedBox.Text ?? ""); }
        catch { /* 忽略 */ }
    }

    private void B64CopyPlain_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(B64PlainBox.Text ?? ""); }
        catch { /* 忽略 */ }
    }

    private void B64Clear_Click(object sender, RoutedEventArgs e)
    {
        B64PlainBox.Text = "";
        B64EncodedBox.Text = "";
        B64StatusText.Text = "";
    }

    // ============================================================
    // Tab 22：游戏内时间 / 现实时间换算器
    // ============================================================

    private bool _gtUpdating;

    private static string DescribeTick(long tick)
    {
        // 0 tick = 日出（早上6点），游戏内一整天 24000 tick 对应现实的 24 小时表盘。
        var hour = ((tick / 1000.0) + 6.0) % 24.0;
        var h = (int)hour;
        var m = (int)((hour - h) * 60);
        string phase = tick switch
        {
            < 12000 => "白天",
            < 13000 => "黄昏",
            < 23000 => "夜晚",
            _ => "黎明"
        };
        return $"约 {h:00}:{m:00}（{phase}）";
    }

    private void GtTickBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_gtUpdating || GtResultText == null) return;
        if (!double.TryParse(GtTickBox.Text.Trim(), out var tick) || tick < 0)
        {
            GtResultText.Text = "请输入一个非负的 tick 数值。";
            return;
        }
        _gtUpdating = true;
        try
        {
            var normalizedTick = tick % 24000;
            var minutes = tick / 20.0 / 60.0;
            GtMinuteBox.Text = minutes.ToString("0.##");
            GtResultText.Text = $"Tick {tick}（一天内为第 {normalizedTick:0} tick）对应现实 {minutes:0.##} 分钟，游戏内时刻{DescribeTick((long)normalizedTick)}。";
        }
        finally { _gtUpdating = false; }
    }

    private void GtMinuteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_gtUpdating || GtResultText == null) return;
        if (!double.TryParse(GtMinuteBox.Text.Trim(), out var minutes) || minutes < 0)
        {
            GtResultText.Text = "请输入一个非负的分钟数。";
            return;
        }
        _gtUpdating = true;
        try
        {
            var tick = minutes * 60.0 * 20.0;
            var normalizedTick = tick % 24000;
            GtTickBox.Text = tick.ToString("0");
            GtResultText.Text = $"现实 {minutes:0.##} 分钟对应 Tick {tick:0}（一天内为第 {normalizedTick:0} tick），游戏内时刻{DescribeTick((long)normalizedTick)}。";
        }
        finally { _gtUpdating = false; }
    }

    // ============================================================
    // Tab 1：自定义成就图片生成器
    // ============================================================

    // 生成过程要联网找真实贴图（见 AchievementImageService.DownloadItemTextureFromWeb），
    // 之前是直接在按钮点击事件里同步调用，等于在 UI 线程上跑网络请求——网稍微慢一点，
    // 整个窗口就跟着卡死好几秒，这正是"自定义图像生成器很卡"的根因。
    // 现在整个生成过程搬到后台线程（Task.Run），UI 线程只负责在生成前后切换按钮/提示的
    // 显示状态，界面全程可以正常拖动、点其它 Tab，不会再被网络请求拖住。
    private bool _achGenerating;

    private async void AchPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_achGenerating) return;
        await RunAchGenerationAsync();
    }

    private async Task RunAchGenerationAsync()
    {
        _achGenerating = true;
        AchPreviewButton.IsEnabled = false;
        AchSaveButton.IsEnabled = false;
        AchGeneratingText.Visibility = Visibility.Visible;
        AchStatusText.Text = "";
        AchSuggestPopup.IsOpen = false;

        try
        {
            var rawItemId = string.IsNullOrWhiteSpace(AchItemIdBox.Text) ? "minecraft:diamond" : AchItemIdBox.Text.Trim();

            // 物品 ID 归一化：把 "Diamond Sword" 这种写法规整成合法的 minecraft:diamond_sword，
            // 中文名（比如"钻石剑"）也在这一步查词典转换，并把结果回填到输入框，
            // 让用户看到实际用的是什么 ID。
            var normalized = AchievementImageService.NormalizeItemId(rawItemId);
            var itemId = normalized.FullId;
            var achName = string.IsNullOrWhiteSpace(AchNameBox.Text) ? "Achievement Get!" : AchNameBox.Text.Trim();
            var line1 = AchLine1Box.Text?.Trim() ?? "";
            var line2 = string.IsNullOrWhiteSpace(AchLine2Box.Text) ? null : AchLine2Box.Text.Trim();
            var mcDir = GetCurrentMinecraftDir();
            var versionId = _owner.ConfigService.Config.SelectedVersionId;
            var bedrockDir = _owner.ConfigService.Config.BedrockManualInstallDir;

            var (bytes, usedRealTexture) = await Task.Run(() =>
            {
                var png = AchievementImageService.Generate(itemId, achName, line1, line2, mcDir, versionId, bedrockDir);
                return (png, AchievementImageService.LastGenerateUsedRealTexture);
            });

            if (normalized.WasChanged) AchItemIdBox.Text = normalized.FullId;
            _achPreviewBytes = bytes;
            AchPreviewImage.Source = BytesToBitmapImage(_achPreviewBytes);

            // 告诉用户这次画的到底是不是真实贴图——"填了个不存在的方块，结果只画出兜底
            // 图标"跟"没生效、还是预设"在观感上很像，加一句明确的状态提示能把两者区分开。
            AchStatusText.Text = usedRealTexture
                ? $"已使用「{itemId}」的真实贴图。"
                : $"未找到「{itemId}」对应的真实贴图，已用示意图标代替——可以点输入框看下拉建议，" +
                  "或检查一下方块/物品名有没有写对（支持直接输入中文名，比如「钻石」「红石块」「橡木原木」）。";
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"生成成就预览图失败：{ex.Message}");
        }
        finally
        {
            _achGenerating = false;
            AchPreviewButton.IsEnabled = true;
            AchSaveButton.IsEnabled = true;
            AchGeneratingText.Visibility = Visibility.Collapsed;
        }
    }

    // ===== Tab 1：中/英文搜索建议下拉 =====

    private void AchItemIdBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAchSuggestions();

    private void AchItemIdBox_GotFocus(object sender, RoutedEventArgs e) => RefreshAchSuggestions();

    private void RefreshAchSuggestions()
    {
        // 输入框在 XAML 里写了初始文本 Text="minecraft:diamond"，控件树按顺序解析时，
        // TextBox 赋值触发的 TextChanged 会在后面的 Popup/ListBox 还没构造出来之前就先跑一次
        // （AchSuggestPopup/AchSuggestList 此时还是 null），不判空直接用会在 InitializeComponent
        // 阶段就抛 NullReferenceException，导致整个 Toolbox 页打不开。
        if (AchSuggestPopup == null || AchSuggestList == null) return;

        var text = AchItemIdBox.Text;
        var suggestions = AchievementImageService.SearchSuggestions(text);
        if (suggestions.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            AchSuggestPopup.IsOpen = false;
            return;
        }

        AchSuggestList.ItemsSource = suggestions
            .Select(s => $"{s.Chinese} → {s.Id}")
            .ToList();
        AchSuggestPopup.IsOpen = true;
    }

    private void AchItemIdBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 点下拉列表本身也会先触发 LostFocus，用一点延迟关闭，避免点建议项时列表先一步收起、
        // 点击事件落空的问题。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!AchSuggestList.IsMouseOver) AchSuggestPopup.IsOpen = false;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AchSuggestList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (AchSuggestList.SelectedItem is not string picked) return;
        var arrow = picked.IndexOf(" → ", StringComparison.Ordinal);
        AchItemIdBox.Text = arrow >= 0 ? picked[(arrow + 3)..] : picked;
        AchItemIdBox.CaretIndex = AchItemIdBox.Text.Length;
        AchSuggestPopup.IsOpen = false;
    }

    // ===== Tab 1：已安装基岩版目录（成就图片用真实贴图）=====

    private void RefreshAchBedrockDirText()
    {
        var dir = _owner.ConfigService.Config.BedrockManualInstallDir;
        AchBedrockDirText.Text = string.IsNullOrWhiteSpace(dir)
            ? "尚未设置。设置后成就图片会直接从你安装的 MC 基岩版游戏包里读取真实物品贴图（不再是模拟图形）。"
            : $"已设置：{dir}";
    }

    private void AchSetBedrockDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择已安装的 MC 基岩版所在文件夹（例如 …\\Minecraft for Windows\\Content）" };
        if (dialog.ShowDialog() != true) return;

        _owner.ConfigService.Config.BedrockManualInstallDir = dialog.FolderName;
        _owner.ConfigService.Save();
        RefreshAchBedrockDirText();
        MessageBoxDialog.ShowInfo("已记录基岩版游戏目录。生成成就图片时会直接从该游戏包里读取真实物品贴图。", "已设置");
    }

    private void AchClearBedrockDir_Click(object sender, RoutedEventArgs e)
    {
        _owner.ConfigService.Config.BedrockManualInstallDir = null;
        _owner.ConfigService.Save();
        RefreshAchBedrockDirText();
    }

    private async void AchSave_Click(object sender, RoutedEventArgs e)
    {
        // 保存前先跑一遍生成逻辑（现在是后台线程 + await，不会卡 UI），
        // 避免用户改完文字忘记点"预览"就直接点"保存图片"，保存的还是上一次的旧内容。
        if (!_achGenerating) await RunAchGenerationAsync();
        if (_achPreviewBytes == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存成就图片",
            Filter = "PNG 图片|*.png",
            FileName = $"achievement_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _achPreviewBytes);
            MessageBoxDialog.ShowSuccess($"已保存到：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"保存失败：{ex.Message}");
        }
    }

    // ============================================================
    // Tab 2：皮肤头像生成器
    // ============================================================

    private void AvatarSelectSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择皮肤文件", Filter = "PNG 图片|*.png" };
        if (dialog.ShowDialog() != true) return;

        _avatarSkinPath = dialog.FileName;
        AvatarSourceText.Text = _avatarSkinPath;
        RenderAvatarPreview();
    }

    private void AvatarSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderAvatarPreview();

    private void RenderAvatarPreview()
    {
        if (string.IsNullOrEmpty(_avatarSkinPath) || !File.Exists(_avatarSkinPath)) return;

        try
        {
            var size = GetSelectedAvatarSize();
            var skinBytes = File.ReadAllBytes(_avatarSkinPath);
            _avatarPreviewBytes = SkinAvatarRenderService.RenderFaceAvatar(skinBytes, size);
            AvatarPreviewImage.Source = BytesToBitmapImage(_avatarPreviewBytes);
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"生成头像预览失败：{ex.Message}");
        }
    }

    private int GetSelectedAvatarSize()
    {
        var content = (AvatarSizeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "64x64";
        var sizeText = content.Split('x')[0];
        return int.TryParse(sizeText, out var size) ? size : 64;
    }

    private void AvatarSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_avatarSkinPath))
        {
            MessageBoxDialog.ShowWarning("请先选择一个皮肤文件。");
            return;
        }

        RenderAvatarPreview();
        if (_avatarPreviewBytes == null) return;

        var size = GetSelectedAvatarSize();
        var dialog = new SaveFileDialog
        {
            Title = "保存皮肤头像",
            Filter = "PNG 图片|*.png",
            FileName = $"avatar_{size}x{size}.png"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _avatarPreviewBytes);
            MessageBoxDialog.ShowSuccess($"已保存到：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"保存失败：{ex.Message}");
        }
    }

    // ============================================================
    // Tab 3：下载自定义文件 / 下载正版玩家的皮肤
    // ============================================================

    private void DlBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择保存目录" };
        if (!string.IsNullOrWhiteSpace(DlSaveDirBox.Text) && Directory.Exists(DlSaveDirBox.Text))
            dialog.InitialDirectory = DlSaveDirBox.Text;

        if (dialog.ShowDialog() == true)
            DlSaveDirBox.Text = dialog.FolderName;
    }

    private void DlOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetDlSaveDirOrDefault();
        Directory.CreateDirectory(dir);
        FolderOpenHelper.Open(dir);
    }

    private string GetDlSaveDirOrDefault()
    {
        if (!string.IsNullOrWhiteSpace(DlSaveDirBox.Text)) return DlSaveDirBox.Text;
        var dir = Path.Combine(AppContext.BaseDirectory, "Downloads");
        DlSaveDirBox.Text = dir;
        return dir;
    }

    private async void DlStart_Click(object sender, RoutedEventArgs e)
    {
        if (_dlInProgress) return;

        var url = DlUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBoxDialog.ShowWarning("请填写下载地址。");
            return;
        }

        var saveDir = GetDlSaveDirOrDefault();

        _dlInProgress = true;
        DlStartBtn.IsEnabled = false;
        DlStatusText.Text = "正在下载...";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(DlUserAgentBox.Text))
            {
                request.Headers.Remove("User-Agent");
                request.Headers.TryAddWithoutValidation("User-Agent", DlUserAgentBox.Text.Trim());
            }

            using var response = await _dlHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var fileName = string.IsNullOrWhiteSpace(DlFileNameBox.Text)
                ? ResolveDownloadFileName(url, response)
                : DlFileNameBox.Text.Trim();

            Directory.CreateDirectory(saveDir);
            var destPath = Path.Combine(saveDir, fileName);

            await using (var fs = File.Create(destPath))
            await using (var stream = await response.Content.ReadAsStreamAsync())
            {
                await stream.CopyToAsync(fs);
            }

            DlStatusText.Text = $"下载完成：{destPath}";
        }
        catch (Exception ex)
        {
            // 403 等特定情形按需求文案单独提示一句，更贴近截图里"部分网站可能会报错 (403) 已禁止"的说明。
            DlStatusText.Text = ex is HttpRequestException httpEx && httpEx.Message.Contains("403")
                ? "下载失败：目标网站返回 403（已禁止），该站点可能不允许程序直接下载，请尝试用浏览器手动下载。"
                : $"下载失败：{ex.Message}";
        }
        finally
        {
            _dlInProgress = false;
            DlStartBtn.IsEnabled = true;
        }
    }

    private static string ResolveDownloadFileName(string url, HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        if (!string.IsNullOrWhiteSpace(contentDisposition?.FileNameStar))
            return contentDisposition.FileNameStar!.Trim('"');
        if (!string.IsNullOrWhiteSpace(contentDisposition?.FileName))
            return contentDisposition.FileName!.Trim('"');

        try
        {
            var uri = new Uri(url);
            var name = Path.GetFileName(uri.LocalPath);
            return string.IsNullOrWhiteSpace(name) ? $"download_{DateTime.Now:yyyyMMdd_HHmmss}" : name;
        }
        catch
        {
            return $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
    }

    private async void OfficialSkinSave_Click(object sender, RoutedEventArgs e)
    {
        var playerName = OfficialPlayerNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            MessageBoxDialog.ShowWarning("请输入正版玩家名。");
            return;
        }

        OfficialSkinStatusText.Text = "正在查询玩家信息...";
        try
        {
            _lookedUpSkinInfo = await _officialSkinService.LookupAsync(playerName);
            OfficialSkinStatusText.Text = $"已找到玩家 {_lookedUpSkinInfo.PlayerName}（UUID: {_lookedUpSkinInfo.Uuid}），正在下载皮肤...";

            var saveDir = Path.Combine(AppContext.BaseDirectory, "Skins");
            var savedPath = await _officialSkinService.DownloadSkinAsync(_lookedUpSkinInfo, saveDir);
            OfficialSkinStatusText.Text = $"已保存到：{savedPath}";
        }
        catch (Exception ex)
        {
            OfficialSkinStatusText.Text = $"获取失败：{ex.Message}";
        }
    }

    // ============================================================
    // Tab 4：加载器 Jar 单独下载
    // ============================================================

    private ClientLoaderInstallService LoaderService =>
        _loaderService ??= new ClientLoaderInstallService(_owner.ConfigService.Config);

    private async void LoaderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = LoaderListBox.SelectedItem as ListBoxItem;
        var tag = item?.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;

        _selectedLoaderTag = tag;
        LoaderTitleText.Text = $"{tag} — 选择版本后下载 Jar";
        LoaderDownloadBtn.IsEnabled = false;
        LoaderMcVersionCombo.ItemsSource = null;
        LoaderVersionCombo.ItemsSource = null;
        LoaderStatusText.Text = Loc.T("Str_Ui_Fetching_Versions", "正在获取版本列表...");

        if (tag == "LabyMod")
        {
            // LabyMod 4 不是"某个 MC 版本对应一个 loader jar"这种传统加载器形态，而是一个
            // 独立的启动器/客户端外壳（自带模组管理、装完之后自己联网拉取对应 MC 版本的组件），
            // 官方直接给的是这个启动器本身的安装包，不需要也没有"选 MC 版本 + 选构建版本"这一步，
            // 这里跳过两级下拉框，直接让「下载」按钮可点。
            LoaderTitleText.Text = "LabyMod — 下载官方启动器安装包";
            LoaderMcVersionCombo.IsEnabled = false;
            LoaderVersionCombo.IsEnabled = false;
            LoaderDownloadBtn.IsEnabled = true;
            LoaderStatusText.Text = "点击下载即可获取 LabyMod 官方启动器安装包（LabyModLauncherSetup.exe），" +
                                     "运行它会自己联网安装/更新 LabyMod 本体，不需要在这里选 MC 版本。";
            return;
        }
        LoaderMcVersionCombo.IsEnabled = true;
        LoaderVersionCombo.IsEnabled = true;

        try
        {
            switch (tag)
            {
                case "Forge":
                    var forgeMcVersions = await LoaderService.GetForgeVersionsAsync();
                    LoaderMcVersionCombo.ItemsSource = forgeMcVersions;
                    break;
                case "NeoForge":
                    // NeoForge 只有 1.20.1 之后的版本，接口直接给的是完整版本号(如 21.1.100)，
                    // 不是"先选 MC 版本、再选 loader 版本"这种两级结构，这里把 MC 版本下拉框
                    // 隐去(不赋值 ItemsSource，UI 保留但留空)，加载器版本下拉框直接放完整列表。
                    var neoVersions = await LoaderService.GetNeoForgeVersionsAsync();
                    LoaderVersionCombo.ItemsSource = neoVersions;
                    LoaderMcVersionCombo.IsEnabled = false;
                    break;
                case "Fabric":
                    LoaderMcVersionCombo.IsEnabled = true;
                    var fabricMcVersions = await LoaderService.GetFabricMcVersionsAsync();
                    LoaderMcVersionCombo.ItemsSource = fabricMcVersions;
                    break;
                case "Quilt":
                    LoaderMcVersionCombo.IsEnabled = true;
                    var quiltMcVersions = await LoaderService.GetQuiltMcVersionsAsync();
                    LoaderMcVersionCombo.ItemsSource = quiltMcVersions;
                    break;
                case "LiteLoader":
                    // LiteLoader 早已停止更新，官方 versions.json 里"versions"对象的 key
                    // 就是它支持的全部 MC 版本（覆盖到 1.12.2 左右），没有额外的"加载器版本"
                    // 概念——一个 MC 版本对应一个构建，所以这里直接把 MC 版本列表喂给
                    // LoaderMcVersionCombo，选中后由 McVersionCombo_SelectionChanged 里
                    // 的 LiteLoader 分支去查该版本下具体的 artifact 版本号。
                    LoaderMcVersionCombo.IsEnabled = true;
                    var liteMcVersions = await GetLiteLoaderMcVersionsAsync();
                    LoaderMcVersionCombo.ItemsSource = liteMcVersions;
                    break;
                case "Cleanroom":
                    // Cleanroom 只发布给 1.12.2（TRIP 后端目前只兼容这一个 MC 版本线，
                    // 跟 LoaderAvailabilityService 里的探测口径保持一致），MC 版本下拉框
                    // 直接固定成这一个选项即可，不需要请求任何接口。
                    LoaderMcVersionCombo.IsEnabled = true;
                    LoaderMcVersionCombo.ItemsSource = new List<string> { "1.12.2" };
                    break;
                case "OptiFine":
                    // OptiFine 本身没有公开列出"支持哪些 MC 版本"的接口，这里退而求其次：
                    // 拿完整的 MC 正式版列表给用户选（跟"下载中心-游戏版本"面板同一份数据源，
                    // 走用户在设置里选的官方源/BMCLAPI），选中某个版本后再到
                    // McVersionCombo_SelectionChanged 里查 BMCLAPI 该版本下是否真的有构建——
                    // 没有构建的版本会在那一步得到空列表，UI 上会提示，而不是筛不掉的假选项。
                    LoaderMcVersionCombo.IsEnabled = true;
                    LoaderMcVersionCombo.ItemsSource = await GetVanillaReleaseVersionsAsync();
                    break;
                case "LegacyFabric":
                    // Legacy Fabric 是 Fabric 生态里专门覆盖 1.13 之前老版本的分支项目，
                    // Meta API 跟官方 Fabric 是同一套接口形状（meta.legacyfabric.net 对
                    // meta.fabricmc.net），这里直接查它自己的 /versions/game。
                    LoaderMcVersionCombo.IsEnabled = true;
                    LoaderMcVersionCombo.ItemsSource = await GetLegacyFabricMcVersionsAsync();
                    break;
            }

            LoaderStatusText.Text = tag == "NeoForge" ? "请选择加载器版本。" : Loc.T("Str_Cs_Please_Choose_A_Minecraft_Version", "请选择 Minecraft 版本。");
        }
        catch (Exception ex)
        {
            LoaderStatusText.Text = $"获取版本列表失败：{ex.Message}";
        }
    }

    private async void LoaderMcVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LoaderMcVersionCombo.SelectedItem is not string mcVersion || string.IsNullOrEmpty(_selectedLoaderTag)) return;

        LoaderVersionCombo.ItemsSource = null;
        LoaderDownloadBtn.IsEnabled = false;
        LoaderStatusText.Text = "正在获取加载器版本列表...";

        try
        {
            switch (_selectedLoaderTag)
            {
                case "Forge":
                    var forgeBuilds = await LoaderService.GetForgeInstallerVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = forgeBuilds;
                    LoaderVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
                    break;
                case "Fabric":
                    var fabricLoaders = await LoaderService.GetFabricLoaderVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = fabricLoaders;
                    LoaderVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
                    break;
                case "Quilt":
                    var quiltLoaders = await LoaderService.GetQuiltLoaderVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = quiltLoaders;
                    LoaderVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
                    break;
                case "LiteLoader":
                    var liteVersions = await GetLiteLoaderArtifactVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = liteVersions;
                    break;
                case "Cleanroom":
                    var cleanroomTags = await GetCleanroomReleaseTagsAsync();
                    LoaderVersionCombo.ItemsSource = cleanroomTags;
                    break;
                case "OptiFine":
                    var optiFineBuilds = await LoaderService.GetOptiFineVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = optiFineBuilds;
                    LoaderVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);
                    break;
                case "LegacyFabric":
                    var legacyFabricLoaders = await GetLegacyFabricLoaderVersionsAsync(mcVersion);
                    LoaderVersionCombo.ItemsSource = legacyFabricLoaders;
                    break;
            }

            var itemCount = LoaderVersionCombo.ItemsSource is System.Collections.ICollection col ? col.Count : 0;
            if (itemCount > 0)
            {
                LoaderStatusText.Text = "请选择加载器 / 安装器版本。";
            }
            else if (_selectedLoaderTag == "OptiFine")
            {
                LoaderStatusText.Text = "该 MC 版本没有找到 OptiFine 构建（BMCLAPI 上无此版本记录）。";
            }
            LoaderDownloadBtn.IsEnabled = itemCount > 0;
        }
        catch (Exception ex)
        {
            LoaderStatusText.Text = $"获取加载器版本列表失败：{ex.Message}";
        }
    }

    /// <summary>
    /// NeoForge 分支下拉框直接是版本号本身，选中即可下载，不需要再等 LoaderVersionCombo 的
    /// SelectionChanged（因为 NeoForge 场景下 LoaderVersionCombo 本身就承担了"唯一一级选择"
    /// 的角色，没有 McVersionCombo 的 SelectionChanged 顺带把下载按钮点亮），这里额外接一个
    /// 处理器保证按钮状态跟着联动。
    /// </summary>
    private void LoaderVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoaderDownloadBtn.IsEnabled = LoaderVersionCombo.SelectedItem != null;
    }

    private async void LoaderDownload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedLoaderTag))
        {
            MessageBoxDialog.ShowWarning("请先选择一个加载器。");
            return;
        }

        LoaderDownloadBtn.IsEnabled = false;
        LoaderStatusText.Text = "正在下载 Jar...";

        try
        {
            var saveDir = GetLoaderDownloadDir();
            Directory.CreateDirectory(saveDir);

            string url;
            string fileName;

            switch (_selectedLoaderTag)
            {
                case "Forge":
                {
                    var mcVersion = LoaderMcVersionCombo.SelectedItem as string
                                     ?? throw new InvalidOperationException("请先选择 Minecraft 版本。");
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择安装器版本。");
                    var forgeFullVersion = $"{mcVersion}-{build.DisplayVersion}";
                    url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{forgeFullVersion}/forge-{forgeFullVersion}-installer.jar";
                    fileName = $"forge-{forgeFullVersion}-installer.jar";
                    break;
                }
                case "NeoForge":
                {
                    var build = LoaderVersionCombo.SelectedItem as string
                                ?? throw new InvalidOperationException("请先选择加载器版本。");
                    url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{build}/neoforge-{build}-installer.jar";
                    fileName = $"neoforge-{build}-installer.jar";
                    break;
                }
                case "Fabric":
                {
                    var mcVersion = LoaderMcVersionCombo.SelectedItem as string
                                     ?? throw new InvalidOperationException("请先选择 Minecraft 版本。");
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择加载器版本。");
                    // Fabric 的"客户端 profile json"本身不是单独一个 jar 文件（是若干 library
                    // 引用+启动参数拼成的 json，LauncherService 靠这份 json 走 inheritsFrom
                    // 完整安装），"下载 Jar"这里改成下载 Fabric Loader 本体的 jar
                    // （Maven 坐标：net/fabricmc/fabric-loader/{loaderVersion}/），这跟
                    // 截图里"单独下载一个 jar 文件"的诉求对得上，而不是尝试下载一个根本不存在
                    // 的单文件"Fabric 客户端 jar"。
                    url = $"https://maven.fabricmc.net/net/fabricmc/fabric-loader/{build.DisplayVersion}/fabric-loader-{build.DisplayVersion}.jar";
                    fileName = $"fabric-loader-{build.DisplayVersion}.jar";
                    break;
                }
                case "Quilt":
                {
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择加载器版本。");
                    url = $"https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-loader/{build.DisplayVersion}/quilt-loader-{build.DisplayVersion}.jar";
                    fileName = $"quilt-loader-{build.DisplayVersion}.jar";
                    break;
                }
                case "LiteLoader":
                {
                    var mcVersion = LoaderMcVersionCombo.SelectedItem as string
                                     ?? throw new InvalidOperationException("请先选择 Minecraft 版本。");
                    var liteVersion = LoaderVersionCombo.SelectedItem as string
                                       ?? throw new InvalidOperationException("请先选择加载器版本。");
                    // Maven 坐标形如 com/mumfrey/liteloader/{mcVersion}/liteloader-{liteVersion}.jar，
                    // 跟 dl.liteloader.com 上实际的仓库目录结构一致。
                    url = $"https://dl.liteloader.com/versions/com/mumfrey/liteloader/{mcVersion}/liteloader-{liteVersion}.jar";
                    fileName = $"liteloader-{liteVersion}.jar";
                    break;
                }
                case "Cleanroom":
                {
                    var tag = LoaderVersionCombo.SelectedItem as string
                              ?? throw new InvalidOperationException("请先选择加载器版本。");
                    // Cleanroom 每个 release 下的资产文件名不完全固定，这里现查一次该 release
                    // 的 assets 列表，取名字里带 "installer" 的 jar；没有的话退而求其次拿
                    // 第一个 .jar 资产，避免因为命名规则变化而硬编码 URL 猜错。
                    (url, fileName) = await GetCleanroomAssetAsync(tag);
                    break;
                }
                case "OptiFine":
                {
                    var mcVersion = LoaderMcVersionCombo.SelectedItem as string
                                     ?? throw new InvalidOperationException("请先选择 Minecraft 版本。");
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择构建版本。");
                    // RawIdentifier 是 GetOptiFineVersionsAsync 里存的 "type|patch"；BMCLAPI 的
                    // /optifine/{mcVersion}/{type}/{patch} 端点直接返回安装器 jar 本体（不是
                    // 先给下载地址再跳转的那种），跟 GetOptiFineVersionsAsync 顶部注释里说的
                    // "BMCLAPI 是事实上唯一可用数据源"一致，这里复用同一个后端。
                    var parts = (build.RawIdentifier ?? "").Split('|', 2);
                    var type = parts.Length > 0 ? parts[0] : "";
                    var patch = parts.Length > 1 ? parts[1] : "";
                    url = $"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}";
                    fileName = $"OptiFine_{mcVersion}_{build.DisplayVersion}.jar";
                    break;
                }
                case "LegacyFabric":
                {
                    var build = LoaderVersionCombo.SelectedItem as ServerCoreBuild
                                ?? throw new InvalidOperationException("请先选择加载器版本。");
                    // Legacy Fabric 复用了 Fabric 同一套 net.fabricmc:fabric-loader Maven 坐标，
                    // 只是发布到自己独立的仓库（maven.legacyfabric.net），跟上面 Fabric 分支
                    // 拿 loader 本体 jar 的口径一致。
                    url = $"https://maven.legacyfabric.net/net/fabricmc/fabric-loader/{build.DisplayVersion}/fabric-loader-{build.DisplayVersion}.jar";
                    fileName = $"legacyfabric-loader-{build.DisplayVersion}.jar";
                    break;
                }
                case "LabyMod":
                {
                    // 官方启动器安装包直链（win32/x64），不区分 MC 版本，装完之后启动器自己
                    // 联网拉取具体版本组件。
                    url = "https://releases.r2.labymod.net/launcher/win32/x64/LabyModLauncherSetup-latest.exe";
                    fileName = "LabyModLauncherSetup.exe";
                    break;
                }
                default:
                    throw new InvalidOperationException("暂不支持该加载器的单独下载。");
            }

            var destPath = Path.Combine(saveDir, fileName);
            var bytes = await _dlHttp.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(destPath, bytes);

            LoaderStatusText.Text = $"下载完成：{destPath}";
        }
        catch (Exception ex)
        {
            LoaderStatusText.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            LoaderDownloadBtn.IsEnabled = true;
        }
    }

    private void LoaderOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetLoaderDownloadDir();
        Directory.CreateDirectory(dir);
        FolderOpenHelper.Open(dir);
    }

    private static string GetLoaderDownloadDir() => Path.Combine(AppContext.BaseDirectory, "LoaderJars");

    /// <summary>返回 LiteLoader 官方 versions.json 里"versions"对象下的全部 MC 版本 key，
    /// 跟 LoaderAvailabilityService.IsLiteLoaderSupportedAsync 用的是同一份数据源，
    /// 口径保持一致（能探测到"支持"的版本，这里也一定能查到）。</summary>
    private async Task<List<string>> GetLiteLoaderMcVersionsAsync()
    {
        var json = await _dlHttp.GetStringAsync("https://dl.liteloader.com/versions/versions.json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<string>();
        if (doc.RootElement.TryGetProperty("versions", out var versions))
        {
            foreach (var v in versions.EnumerateObject())
                result.Add(v.Name);
        }
        return result;
    }

    /// <summary>某个 MC 版本下 LiteLoader 实际发布过的 artifact 版本号列表（通常只有一个，
    /// 但保留多版本结构以防个别 MC 版本下有多条历史构建）。</summary>
    private async Task<List<string>> GetLiteLoaderArtifactVersionsAsync(string mcVersion)
    {
        var json = await _dlHttp.GetStringAsync("https://dl.liteloader.com/versions/versions.json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<string>();
        if (doc.RootElement.TryGetProperty("versions", out var versions)
            && versions.TryGetProperty(mcVersion, out var verEntry))
        {
            // 官方 JSON 里这个字段用的是英式拼写 "artefacts"；保留一个 "artifacts" 兜底，
            // 以防将来数据源改了拼写。
            var hasArtefacts = verEntry.TryGetProperty("artefacts", out var artefacts)
                                || verEntry.TryGetProperty("artifacts", out artefacts);
            if (hasArtefacts && artefacts.TryGetProperty("com.mumfrey:liteloader", out var liteLoaderArtifact))
            {
                foreach (var build in liteLoaderArtifact.EnumerateObject())
                    result.Add(build.Name);
            }
        }
        // 没查到任何具体构建号时，退而求其次直接用 MC 版本号本身当作 artifact 版本号
        // （LiteLoader 绝大多数版本的构建号跟 MC 版本号是一致的）。
        if (result.Count == 0) result.Add(mcVersion);
        return result;
    }

    /// <summary>Cleanroom 在 GitHub 上已发布的 release tag 列表。</summary>
    private async Task<List<string>> GetCleanroomReleaseTagsAsync()
    {
        var json = await _dlHttp.GetStringAsync("https://api.github.com/repos/CleanroomMC/Cleanroom/releases");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("tag_name", out var tag) && tag.GetString() is { } t)
                result.Add(t);
        }
        return result;
    }

    /// <summary>取某个 Cleanroom release 下可下载的 jar 资产：优先选名字里带"installer"的，
    /// 否则退而求其次选第一个 .jar 资产。</summary>
    private async Task<(string Url, string FileName)> GetCleanroomAssetAsync(string tag)
    {
        var json = await _dlHttp.GetStringAsync(
            $"https://api.github.com/repos/CleanroomMC/Cleanroom/releases/tags/{Uri.EscapeDataString(tag)}");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0)
            throw new InvalidOperationException($"该 Cleanroom release（{tag}）没有可下载的资产。");

        System.Text.Json.JsonElement? chosen = null;
        System.Text.Json.JsonElement? firstJar = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            firstJar ??= asset;
            if (name.Contains("installer", StringComparison.OrdinalIgnoreCase)) { chosen = asset; break; }
        }
        var picked = chosen ?? firstJar
            ?? throw new InvalidOperationException($"该 Cleanroom release（{tag}）没有 .jar 资产。");

        var fileName = picked.GetProperty("name").GetString()!;
        var url = picked.GetProperty("browser_download_url").GetString()!;
        return (url, fileName);
    }

    /// <summary>MC 正式版列表，供 OptiFine 分支的 MC 版本下拉框使用（OptiFine 官方没有可查询
    /// "支持哪些版本"的接口，只能把完整正式版列表摆出来让用户选，具体某个版本有没有构建交给
    /// BMCLAPI 的 /optifine/{mcVersion} 查询结果决定）。走用户在设置里选好的下载源
    /// （官方 Mojang / BMCLAPI），跟"下载中心"页面选版本时的数据源、排序规则保持一致。</summary>
    private async Task<List<string>> GetVanillaReleaseVersionsAsync()
    {
        using var svc = DownloadService.CreateFromConfig(_owner.ConfigService.Config);
        var manifest = await svc.GetVersionManifestAsync();
        return manifest.Versions
            .Where(v => v.GetCategory() == VersionCategory.Release)
            .SortNewestFirst()
            .Select(v => v.Id)
            .ToList();
    }

    private const string LegacyFabricMetaBase = "https://meta.legacyfabric.net/v2";

    /// <summary>Legacy Fabric 自己维护的 Meta API，接口形状跟官方 Fabric 完全一致
    /// （只是覆盖 1.13 之前的老版本），直接查 /versions/game。</summary>
    private async Task<List<string>> GetLegacyFabricMcVersionsAsync()
    {
        var json = await _dlHttp.GetStringAsync($"{LegacyFabricMetaBase}/versions/game");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("version", out var ver) && ver.GetString() is { } s)
                result.Add(s);
        }
        return result;
    }

    /// <summary>某个 MC 版本下 Legacy Fabric loader 的可用版本列表，跟官方 Fabric 的
    /// /versions/loader/{mcVersion} 返回结构一致（每项是 {"loader":{"version":...},...}）。</summary>
    private async Task<List<ServerCoreBuild>> GetLegacyFabricLoaderVersionsAsync(string mcVersion)
    {
        var json = await _dlHttp.GetStringAsync(
            $"{LegacyFabricMetaBase}/versions/loader/{Uri.EscapeDataString(mcVersion)}");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var version = e.GetProperty("loader").GetProperty("version").GetString();
            if (string.IsNullOrEmpty(version)) continue;
            result.Add(new ServerCoreBuild { DisplayVersion = version, IsRecommended = false, RawIdentifier = version });
        }
        if (result.Count > 0) result[0].IsRecommended = true;
        return result;
    }

    // ============================================================
    // Tab 5：清理游戏垃圾 / 创建快捷方式 / 启动计数 / 内存优化
    // ============================================================

    private JunkCleanupService.JunkScanResult? _junkScanResult;

    private string GetCurrentMinecraftDir()
    {
        var cfg = _owner.ConfigService.Config;
        var folder = cfg.Folders?.FirstOrDefault(f => f.Path == cfg.SelectedFolderPath);
        return folder?.Path ?? cfg.Folders?.FirstOrDefault()?.Path ?? "";
    }

    private async void JunkScan_Click(object sender, RoutedEventArgs e)
    {
        var dir = GetCurrentMinecraftDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            JunkStatusText.Text = "还没有选择 .minecraft 文件夹，请先去「版本选择」页添加一个。";
            JunkCleanBtn.IsEnabled = false;
            return;
        }

        // 扫描/删除本身没有可预知的总量，这里用不确定进度条（转圈样式）只是为了
        // 给用户"正在处理，没有卡死"的即时反馈，而不是假装能算出百分比。
        // 真正的耗时工作放到 Task.Run 里跑，不阻塞 UI 线程——修复"点了之后界面
        // 未响应一段时间"的问题。
        var progress = new ProgressDialog("正在扫描游戏垃圾文件…", indeterminate: true);
        progress.Show();
        try
        {
            _junkScanResult = await Task.Run(() => JunkCleanupService.Scan(dir));
            JunkStatusText.Text = _junkScanResult.Items.Count == 0
                ? "扫描完成：没有发现可清理的垃圾文件，很干净！"
                : $"扫描完成：发现 {_junkScanResult.Items.Count} 个可清理文件，共 {JunkCleanupService.FormatBytes(_junkScanResult.TotalBytes)}。";
            JunkCleanBtn.IsEnabled = _junkScanResult.Items.Count > 0;
        }
        catch (Exception ex)
        {
            JunkStatusText.Text = $"扫描失败：{ex.Message}";
            JunkCleanBtn.IsEnabled = false;
        }
        finally
        {
            progress.Close();
        }
    }

    private async void JunkClean_Click(object sender, RoutedEventArgs e)
    {
        if (_junkScanResult == null || _junkScanResult.Items.Count == 0) return;

        if (!MessageBoxDialog.ShowConfirm(
                $"即将删除 {_junkScanResult.Items.Count} 个文件，共释放约 {JunkCleanupService.FormatBytes(_junkScanResult.TotalBytes)}。\n" +
                "不会影响存档/Mod/资源包/设置，确定继续吗？"))
        {
            return;
        }

        var items = _junkScanResult.Items;
        var progress = new ProgressDialog("正在清理…", indeterminate: true);
        progress.Show();
        try
        {
            var (deletedCount, freedBytes) = await Task.Run(() => JunkCleanupService.Delete(items));
            JunkStatusText.Text = $"清理完成：删除了 {deletedCount} 个文件，释放 {JunkCleanupService.FormatBytes(freedBytes)}。";
            _junkScanResult = null;
            JunkCleanBtn.IsEnabled = false;
        }
        finally
        {
            progress.Close();
        }
    }

    // ============================================================
    // Tab 8：电脑清理（跟上面 Tab 5 的"清理游戏垃圾"是两个不同范围的清理——
    // 那个只清 .minecraft 目录里的东西，这个是清理整台电脑的系统临时文件/浏览器缓存，
    // 详细的安全边界说明见 SystemJunkCleanupService 类注释）
    // ============================================================

    private SystemJunkCleanupService.JunkScanResult? _sysJunkScanResult;

    private async void SysJunkScan_Click(object sender, RoutedEventArgs e)
    {
        SysJunkStatusText.Text = "正在扫描，文件数量较多时可能需要几秒钟…";
        SysJunkCleanBtn.IsEnabled = false;

        var progress = new ProgressDialog("正在扫描系统垃圾文件…", indeterminate: true);
        progress.Show();
        try
        {
            _sysJunkScanResult = await Task.Run(() => SystemJunkCleanupService.Scan());
            SysJunkStatusText.Text = _sysJunkScanResult.Items.Count == 0
                ? "扫描完成：没有发现可清理的垃圾文件，很干净！"
                : $"扫描完成：发现 {_sysJunkScanResult.Items.Count} 项可清理内容，共 {SystemJunkCleanupService.FormatBytes(_sysJunkScanResult.TotalBytes)}。";
            SysJunkCleanBtn.IsEnabled = _sysJunkScanResult.Items.Count > 0;
        }
        catch (Exception ex)
        {
            SysJunkStatusText.Text = $"扫描失败：{ex.Message}";
            SysJunkCleanBtn.IsEnabled = false;
        }
        finally
        {
            progress.Close();
        }
    }

    private async void SysJunkClean_Click(object sender, RoutedEventArgs e)
    {
        if (_sysJunkScanResult == null || _sysJunkScanResult.Items.Count == 0) return;

        if (!MessageBoxDialog.ShowConfirm(
                $"即将清理 {_sysJunkScanResult.Items.Count} 项内容，共释放约 {SystemJunkCleanupService.FormatBytes(_sysJunkScanResult.TotalBytes)}。\n" +
                "只会清理临时/缓存文件，不会影响你的文档、下载、桌面等个人数据，确定继续吗？"))
        {
            return;
        }

        var items = _sysJunkScanResult.Items;
        var progress = new ProgressDialog("正在清理…", indeterminate: true);
        progress.Show();
        try
        {
            var (deletedCount, freedBytes) = await Task.Run(() => SystemJunkCleanupService.Delete(items));
            SysJunkStatusText.Text = $"清理完成：删除了 {deletedCount} 项，释放 {SystemJunkCleanupService.FormatBytes(freedBytes)}。";
            _sysJunkScanResult = null;
            SysJunkCleanBtn.IsEnabled = false;
        }
        finally
        {
            progress.Close();
        }
    }

    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ShortcutService.CreateDesktopShortcut();
            MessageBoxDialog.ShowSuccess($"已在桌面创建快捷方式：{path}");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"创建快捷方式失败：{ex.Message}");
        }
    }

    private void ShowLaunchCount_Click(object sender, RoutedEventArgs e)
    {
        // 复用 AppConfig.GameLaunchSuccessCount：MainWindow.LaunchInternalAsync 里游戏
        // 每次成功启动都会 ++ 这个字段并持久化保存，这里只是读出来展示，不需要另建一套
        // 独立的"启动计数"存储/统计逻辑。
        var count = _owner.ConfigService.Config.GameLaunchSuccessCount;
        MessageBoxDialog.ShowInfo($"累计成功启动游戏 {count} 次。", "启动计数");
    }

    private void MemOptCheck_Changed(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;
        cfg.EnableMemoryOptimization = MemOptCheck.IsChecked == true;
        _owner.ConfigService.Save();

        MemOptStatusText.Text = cfg.EnableMemoryOptimization
            ? "已开启：下次启动游戏前会自动按当前可用内存重新计算 -Xms/-Xmx。"
            : "已关闭：启动游戏将使用「设置」页手动填写的固定内存数值。";
    }

    private void MemOptPreview_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _owner.ConfigService.Config;
        var recommendation = MemoryOptimizerService.Calculate(cfg.MemoryOptimizationReserveMb);

        MemOptStatusText.Text = recommendation == null
            ? "无法获取系统内存信息（可能不是 Windows 系统），此功能暂不可用。"
            : recommendation.Explanation;
    }

    // ============================================================
    // 公共工具方法
    // ============================================================

    private static BitmapImage BytesToBitmapImage(byte[] bytes)
    {
        var image = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ============================================================
    // Tab 9：服务器测速（Minecraft Server List Ping 协议实测延迟/MOTD/人数）
    // ============================================================

    private async void PingGo_Click(object sender, RoutedEventArgs e)
    {
        var host = PingHostBox.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            PingResultText.Text = "请先填写服务器地址。";
            return;
        }
        if (!int.TryParse(PingPortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            PingResultText.Text = "端口号不对，应该是 1-65535 之间的数字（默认 25565）。";
            return;
        }

        PingGoBtn.IsEnabled = false;
        PingResultText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        PingResultText.Text = "正在连接测速…";
        try
        {
            var result = await ServerPingService.PingAsync(host, port);
            if (!result.Success)
            {
                PingResultText.Foreground = System.Windows.Media.Brushes.IndianRed;
                PingResultText.Text = result.ErrorMessage;
            }
            else
            {
                var motd = string.IsNullOrWhiteSpace(result.MotdPlain) ? "(空)" : result.MotdPlain;
                PingResultText.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                PingResultText.Text =
                    $"延迟：{result.LatencyMs} ms\n" +
                    $"版本：{result.VersionName ?? "未知"}\n" +
                    $"在线人数：{result.OnlinePlayers?.ToString() ?? "?"} / {result.MaxPlayers?.ToString() ?? "?"}\n" +
                    $"MOTD：{motd}";
            }
        }
        finally
        {
            PingGoBtn.IsEnabled = true;
        }
    }

    // ============================================================
    // Tab 10：主世界 / 下界坐标换算（1:8 比例，Y 不参与换算）
    // ============================================================

    private bool _coordSyncing;

    private void OverworldCoord_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_coordSyncing) return;
        // XAML 加载阶段：给 OverworldXBox 设置初始 Text="0" 会立即触发本事件，
        // 但此时同一分组里排在它后面的 NetherXBox/NetherZBox/CoordStatusText
        // 还没有被 InitializeComponent 连接完毕（字段仍是 null），
        // 这里直接跳过，等真正的 Loaded 之后用户输入触发的事件再处理。
        if (OverworldXBox == null || OverworldZBox == null || NetherXBox == null ||
            NetherZBox == null || CoordStatusText == null) return;
        if (!double.TryParse(OverworldXBox.Text, out var x) || !double.TryParse(OverworldZBox.Text, out var z))
        {
            CoordStatusText.Text = "请输入数字。";
            return;
        }
        _coordSyncing = true;
        NetherXBox.Text = Math.Round(x / 8.0, 2).ToString();
        NetherZBox.Text = Math.Round(z / 8.0, 2).ToString();
        _coordSyncing = false;
        CoordStatusText.Text = "";
    }

    private void NetherCoord_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_coordSyncing) return;
        // 同上：避免 XAML 加载阶段控件尚未全部连接时触发导致空引用。
        if (OverworldXBox == null || OverworldZBox == null || NetherXBox == null ||
            NetherZBox == null || CoordStatusText == null) return;
        if (!double.TryParse(NetherXBox.Text, out var x) || !double.TryParse(NetherZBox.Text, out var z))
        {
            CoordStatusText.Text = "请输入数字。";
            return;
        }
        _coordSyncing = true;
        OverworldXBox.Text = Math.Round(x * 8.0, 2).ToString();
        OverworldZBox.Text = Math.Round(z * 8.0, 2).ToString();
        _coordSyncing = false;
        CoordStatusText.Text = "";
    }

    // ============================================================
    // Tab 11：聊天 / MOTD 颜色代码编辑器（& 转 § 格式代码，带色块插入 + 实时预览）
    // ============================================================

    // Minecraft 官方定义的 16 种聊天颜色，代码 0-9/a-f 对应的十六进制色值（Java 版标准配色表）。
    private static readonly (char Code, string Name, string Hex)[] ColorCodes =
    {
        ('0', "黑色", "#000000"), ('1', "深蓝", "#0000AA"), ('2', "深绿", "#00AA00"), ('3', "深青", "#00AAAA"),
        ('4', "深红", "#AA0000"), ('5', "紫色", "#AA00AA"), ('6', "金色", "#FFAA00"), ('7', "浅灰", "#AAAAAA"),
        ('8', "深灰", "#555555"), ('9', "蓝色", "#5555FF"), ('a', "绿色", "#55FF55"), ('b', "青色", "#55FFFF"),
        ('c', "红色", "#FF5555"), ('d', "粉色", "#FF55FF"), ('e', "黄色", "#FFFF55"), ('f', "白色", "#FFFFFF"),
    };

    private static readonly (char Code, string Label)[] FormatCodes =
    {
        ('l', "粗体"), ('o', "斜体"), ('n', "下划线"), ('m', "删除线"), ('r', "重置"),
    };

    private void BuildColorCodeSwatches()
    {
        foreach (var (code, name, hex) in ColorCodes)
        {
            var swatch = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 4, 4),
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!,
                ToolTip = $"§{code} {name}",
                Tag = $"&{code}"
            };
            swatch.Click += ColorSwatch_Click;
            ColorSwatchPanel.Children.Add(swatch);
        }
        foreach (var (code, label) in FormatCodes)
        {
            var btn = new Button
            {
                Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12, Tag = $"&{code}"
            };
            btn.Click += ColorSwatch_Click;
            FormatSwatchPanel.Children.Add(btn);
        }
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        var code = (string)((Button)sender).Tag;
        var caret = ColorCodeInputBox.CaretIndex;
        ColorCodeInputBox.Text = ColorCodeInputBox.Text.Insert(caret, code);
        ColorCodeInputBox.CaretIndex = caret + code.Length;
        ColorCodeInputBox.Focus();
    }

    private void ColorCodeInputBox_TextChanged(object sender, TextChangedEventArgs? e)
    {
        if (ColorCodePreviewText == null) return; // InitializeComponent 期间可能提前触发一次
        ColorCodePreviewText.Inlines.Clear();

        var text = ColorCodeInputBox.Text;
        var currentColor = "#FFFFFF";
        bool bold = false, italic = false, underline = false, strike = false;
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            var run = new System.Windows.Documents.Run(buffer.ToString())
            {
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(currentColor)!,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            };
            System.Windows.Documents.Inline inline = run;
            if (underline || strike)
            {
                var decorations = new System.Windows.TextDecorationCollection();
                if (underline) decorations.Add(System.Windows.TextDecorations.Underline[0]);
                if (strike) decorations.Add(System.Windows.TextDecorations.Strikethrough[0]);
                run.TextDecorations = decorations;
            }
            ColorCodePreviewText.Inlines.Add(inline);
            buffer.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            // 玩家常用 & 代替真正的 § 符号书写(键盘打不出 §)，两种都认，跟游戏内插件
            // 常见的"用 & 写、显示时自动转成 §"的习惯一致。
            if ((text[i] == '&' || text[i] == '\u00A7') && i + 1 < text.Length)
            {
                var code = char.ToLowerInvariant(text[i + 1]);
                var colorMatch = ColorCodes.FirstOrDefault(c => c.Code == code);
                if (colorMatch != default)
                {
                    Flush();
                    currentColor = colorMatch.Hex;
                    bold = italic = underline = strike = false;
                    i++;
                    continue;
                }
                switch (code)
                {
                    case 'l': Flush(); bold = true; i++; continue;
                    case 'o': Flush(); italic = true; i++; continue;
                    case 'n': Flush(); underline = true; i++; continue;
                    case 'm': Flush(); strike = true; i++; continue;
                    case 'r': Flush(); currentColor = "#FFFFFF"; bold = italic = underline = strike = false; i++; continue;
                }
            }
            buffer.Append(text[i]);
        }
        Flush();
    }

    private void ColorCodeCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ColorCodeInputBox.Text); }
        catch { /* 剪贴板偶尔被其它程序占用导致写入失败，忽略即可，不影响编辑器本身使用 */ }
    }

    private void ColorCodeClear_Click(object sender, RoutedEventArgs e)
    {
        ColorCodeInputBox.Text = "";
    }

    // ============================================================
    // Tab 12：系统内存监视 / 一键释放
    // ============================================================

    private readonly DispatcherTimer _memMonTimer;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private void RefreshMemoryMonitor()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            MemMonStatusText.Text = "获取系统内存信息失败。";
            return;
        }

        var totalGb = status.ullTotalPhys / 1024.0 / 1024 / 1024;
        var availGb = status.ullAvailPhys / 1024.0 / 1024 / 1024;
        var usedGb = totalGb - availGb;

        MemMonPercentText.Text = $"{status.dwMemoryLoad}%";
        MemMonBar.Value = status.dwMemoryLoad;
        MemMonDetailText.Text = $"已用 {usedGb:F1} GB / 总共 {totalGb:F1} GB，可用 {availGb:F1} GB。";
    }

    private void MemMonRefresh_Click(object sender, RoutedEventArgs e) => RefreshMemoryMonitor();

    /// <summary>「一键释放内存」：对当前用户能访问的其它进程调用 EmptyWorkingSet，
    /// 让它们把已经分配但暂时没在用的内存工作集交还给系统的可用内存池——这不是杀进程、
    /// 也不是清空进程数据（那些数据还在，只是被换出到页面文件，下次那个进程真的要用到
    /// 时系统会自动换回来，用户感知不到），跟任务管理器里某些"内存整理"工具是同一原理。
    /// 没有权限访问的系统进程会直接失败，这里逐个 try/catch 跳过，不影响其它进程。</summary>
    private void MemMonTrim_Click(object sender, RoutedEventArgs e)
    {
        MemMonTrimBtn.IsEnabled = false;
        MemMonStatusText.Text = "正在释放…";

        var before = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref before);

        int trimmed = 0, total = 0;
        var currentPid = Process.GetCurrentProcess().Id;
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                if (proc.Id == currentPid) continue; // 自己就不用整理了，正在用
                total++;
                try
                {
                    if (EmptyWorkingSet(proc.Handle)) trimmed++;
                }
                catch { /* 大概率是权限不足(系统进程)或进程已退出，跳过即可 */ }
            }
        }

        RefreshMemoryMonitor();
        var after = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref after);
        var freedMb = ((long)after.ullAvailPhys - (long)before.ullAvailPhys) / 1024.0 / 1024;

        MemMonStatusText.Text = freedMb > 0
            ? $"完成：成功整理了 {trimmed}/{total} 个进程，可用内存增加约 {freedMb:F0} MB。"
            : $"完成：成功整理了 {trimmed}/{total} 个进程（这次系统当前可用内存没有明显增加，属于正常情况——不是所有进程都有可释放的闲置内存）。";
        MemMonTrimBtn.IsEnabled = true;
    }

    // ============================================================
    // Tab 13：离线（正版无关）UUID 生成器
    // ============================================================

    /// <summary>按 Minecraft 离线模式的算法算出用户名对应的离线 UUID：
    /// MD5("OfflinePlayer:" + 用户名)，再按 UUID v3（基于名字的 UUID）规则改写
    /// 第 7 个字节的高 4 位为 0x3（版本号）、第 9 个字节的高 2 位为 0x8（变体标识）——
    /// 这两步跟官方离线模式服务端实际使用的算法完全一致，同一个用户名任何时候、
    /// 任何机器上算出来的结果都相同，纯本地计算不需要联网。</summary>
    private static Guid ComputeOfflineUuid(string username)
    {
        var bytes = Encoding.UTF8.GetBytes("OfflinePlayer:" + username);
        var hash = MD5.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        // .NET 的 Guid(byte[]) 构造函数对前 3 段是小端序，跟 Java UUID 的大端书写顺序不一致，
        // 这里手动按大端顺序拼十六进制字符串再解析，保证跟游戏内/Java 版算出来的结果一字不差。
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return Guid.Parse(hex);
    }

    private void OfflineUuidNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OfflineUuidDashedText == null || OfflineUuidPlainText == null) return; // InitializeComponent 期间
            // TextBox 的 Text="Steve" 默认值会提前触发一次 TextChanged，此时同一 Tab 里排在后面的
            // 结果文本控件还没解析完（字段仍是 null），直接跳过，等构造函数里显式调用的那一次再处理。

        var name = OfflineUuidNameBox.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            OfflineUuidDashedText.Text = "";
            OfflineUuidPlainText.Text = "";
            return;
        }

        var uuid = ComputeOfflineUuid(name);
        OfflineUuidDashedText.Text = uuid.ToString("D");
        OfflineUuidPlainText.Text = uuid.ToString("N");
    }

    private void OfflineUuidCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OfflineUuidDashedText.Text)) return;
        try { Clipboard.SetText(OfflineUuidDashedText.Text); }
        catch { /* 剪贴板偶尔被其它程序占用导致写入失败，忽略即可 */ }
    }

    // ============================================================
    // Tab 14：JVM 参数生成器
    // ============================================================

    /// <summary>按内存/核数/版本三个维度拼一套 G1GC 调优参数（Aikar's Flags 的简化版，
    /// 这套参数是社区多年验证过的通用最佳实践，不是本项目自己发明的数值）。
    /// 内存低于 4G 时 G1GC 的分代/停顿调优反而不划算（新生代空间太小，参数越多开销越大），
    /// 所以 &lt;4G 只给最基础的 -Xms/-Xmx + 默认回收器，不堆砌一堆对小内存没意义的参数。</summary>
    private void JvmGenOption_Changed(object sender, SelectionChangedEventArgs e) => RefreshJvmArgs();

    private void RefreshJvmArgs()
    {
        if (JvmResultText == null) return; // InitializeComponent 期间 ComboBox 的 IsSelected="True" 会提前触发一次
                                            // SelectionChanged，此时后面的控件可能还没解析完，直接跳过等构造函数
                                            // 里显式调用的那一次。
        if (JvmMemCombo?.SelectedItem is not ComboBoxItem memItem) return;
        if (JvmCoreCombo?.SelectedItem is not ComboBoxItem coreItem) return;
        if (JvmVersionCombo?.SelectedItem is not ComboBoxItem verItem) return;

        var mem = int.Parse((string)memItem.Tag);
        var cores = int.Parse((string)coreItem.Tag);
        var modern = (string)verItem.Tag == "modern";

        var xms = Math.Max(1, mem / 2);
        var sb = new StringBuilder();
        sb.Append($"-Xms{xms}G -Xmx{mem}G ");

        if (mem < 4)
        {
            // 内存太小，G1GC 一堆分代参数反而添乱，只给最基本的堆大小设置。
            sb.Append("-XX:+UseSerialGC");
        }
        else
        {
            sb.Append("-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 ");
            sb.Append("-XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch ");
            sb.Append("-XX:G1NewSizePercent=30 -XX:G1MaxNewSizePercent=40 -XX:G1HeapRegionSize=8M ");
            sb.Append("-XX:G1ReservePercent=20 -XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4 ");
            sb.Append("-XX:InitiatingHeapOccupancyPercent=15 -XX:G1MixedGCLiveThresholdPercent=90 ");
            sb.Append("-XX:G1RSetUpdatingPauseTimePercent=5 -XX:SurvivorRatio=32 ");
            sb.Append("-XX:+PerfDisableSharedMem -XX:MaxTenuringThreshold=1 ");
            sb.Append($"-XX:ParallelGCThreads={Math.Max(2, cores)} ");
            if (modern) sb.Append("-XX:+UseStringDeduplication");
        }

        JvmResultText.Text = sb.ToString().Trim();
    }

    private void JvmGenCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(JvmResultText.Text)) return;
        try { Clipboard.SetText(JvmResultText.Text); }
        catch { /* 剪贴板偶尔被其它程序占用导致写入失败，忽略即可 */ }
    }

    // ============================================================
    // Tab 15：资源包完整性校验
    // ============================================================

    private void RpVerifyBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "资源包 (*.zip)|*.zip", Title = "选择资源包文件" };
        if (dlg.ShowDialog() != true) return;

        RpVerifyFileText.Text = Path.GetFileName(dlg.FileName);
        RpVerifyFormatText.Text = "读取中…";
        RpVerifySha1Text.Text = "计算中…";
        RpVerifyResultText.Text = "";

        try
        {
            using var fs = File.OpenRead(dlg.FileName);
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(fs);
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            RpVerifySha1Text.Text = hash;

            RpVerifyFormatText.Text = ReadPackFormat(dlg.FileName) ?? "未在包内找到 pack.mcmeta，或 pack_format 字段缺失";
        }
        catch (Exception ex)
        {
            RpVerifySha1Text.Text = "计算失败";
            RpVerifyResultText.Text = $"读取文件失败：{ex.Message}";
            return;
        }

        RpVerifyExpectedBox_TextChanged(this, null!);
    }

    /// <summary>从资源包 zip 里读出 pack.mcmeta 的 pack_format 字段，只做最基础的字符串定位，
    /// 不引入完整 JSON 解析库——pack.mcmeta 结构很简单，够用了，也避免这个纯本地小工具
    /// 依赖额外的包。</summary>
    private static string? ReadPackFormat(string zipPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("pack.mcmeta");
        if (entry == null) return null;

        using var reader = new StreamReader(entry.Open());
        var json = reader.ReadToEnd();
        var idx = json.IndexOf("\"pack_format\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0) return null;

        var numStart = colonIdx + 1;
        while (numStart < json.Length && !char.IsDigit(json[numStart])) numStart++;
        var numEnd = numStart;
        while (numEnd < json.Length && char.IsDigit(json[numEnd])) numEnd++;

        return numEnd > numStart ? json.Substring(numStart, numEnd - numStart) : null;
    }

    private void RpVerifyExpectedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var expected = RpVerifyExpectedBox.Text.Trim().ToLowerInvariant();
        var actual = RpVerifySha1Text.Text;

        if (string.IsNullOrEmpty(expected) || actual == "--" || actual == "计算中…" || actual == "计算失败")
        {
            RpVerifyResultText.Text = "";
            RpVerifyResultText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            return;
        }

        if (expected == actual)
        {
            RpVerifyResultText.Text = "✔ 校验通过：文件内容跟期望的 SHA1 一致，没有损坏或被改动。";
            RpVerifyResultText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessTextBrush");
        }
        else
        {
            RpVerifyResultText.Text = "✘ 校验不一致：文件内容跟期望的 SHA1 对不上，可能下载不完整或被改动过，建议重新下载。";
            RpVerifyResultText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    // ============================================================
    // Tab 16：快捷键速查表
    // ============================================================

    private static readonly (string Category, string Key, string Desc)[] KeyRefData =
    {
        ("移动", "W / A / S / D", "前 / 左 / 后 / 右移动"),
        ("移动", "空格", "跳跃 / 飞行上升（创造/旁观模式）"),
        ("移动", "Shift（按住）", "潜行 / 飞行下降（创造/旁观模式）"),
        ("移动", "Ctrl（按住）+ W", "疾跑"),
        ("移动", "双击空格", "切换飞行状态（创造/旁观模式）"),
        ("交互", "鼠标左键", "攻击 / 挖掘"),
        ("交互", "鼠标右键", "使用物品 / 放置方块 / 交互"),
        ("交互", "鼠标中键", "选取方块（创造模式）"),
        ("交互", "Q", "丢弃手中物品（Ctrl+Q 丢弃整组）"),
        ("界面", "E", "打开/关闭物品栏"),
        ("界面", "T", "打开聊天栏"),
        ("界面", "/", "打开聊天栏并自动填入 \"/\"（输入指令）"),
        ("界面", "Tab", "玩家列表（多人游戏）"),
        ("界面", "F1", "隐藏/显示界面（截图常用）"),
        ("调试", "F3", "调试信息面板（坐标/朝向/FPS等）"),
        ("调试", "F3 + C", "复制当前坐标对应的 /tp 指令"),
        ("调试", "F3 + A", "重新加载所有区块"),
        ("调试", "F3 + T", "重新加载材质包"),
        ("调试", "F3 + G", "显示/隐藏区块边界线"),
        ("截图/录制", "F2", "截图（保存到 screenshots 文件夹）"),
        ("截图/录制", "F5", "切换视角（第一/第三人称）"),
        ("创造模式", "双击 E 后选中物品栏空位", "创造模式物品栏直接搜索/拖拽取物品"),
        ("创造模式", "T 后输入指令", "常用：/gamemode、/tp、/give、/time set"),
    };

    private void BuildKeyRefList()
    {
        KeyRefList.Items.Clear();
        string? lastCategory = null;
        foreach (var (category, key, desc) in KeyRefData)
        {
            if (category != lastCategory)
            {
                KeyRefList.Items.Add(new TextBlock
                {
                    Text = category,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    Margin = new Thickness(0, lastCategory == null ? 0 : 14, 0, 4)
                });
                lastCategory = category;
            }

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var keyBadge = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("SideBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            keyBadge.Child = new TextBlock { Text = key, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
            Grid.SetColumn(keyBadge, 0);
            row.Children.Add(keyBadge);

            var descText = new TextBlock
            {
                Text = desc,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(descText, 1);
            row.Children.Add(descText);

            KeyRefList.Items.Add(row);
        }
    }

    // ============================================================
    // Tab 17：随机种子生成器
    // ============================================================

    private static readonly Random _seedRandom = new();
    private const string SeedStringChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private void SeedTypeRadio_Changed(object sender, RoutedEventArgs e) { /* 只影响下一次点「生成种子」的结果，这里不用做任何事 */ }

    private void SeedGen_Click(object sender, RoutedEventArgs e)
    {
        if (SeedNumericRadio.IsChecked == true)
        {
            // Minecraft 的世界种子内部是 64 位长整型，这里在 long 的完整范围内取随机值，
            // 跟游戏内"留空随机生成"时实际用到的种子范围一致。
            var buffer = new byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
            var seed = BitConverter.ToInt64(buffer, 0);
            SeedResultText.Text = seed.ToString();
        }
        else
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 12; i++)
                sb.Append(SeedStringChars[_seedRandom.Next(SeedStringChars.Length)]);
            SeedResultText.Text = sb.ToString();
        }
    }

    private void SeedCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SeedResultText.Text) || SeedResultText.Text.StartsWith("点下面")) return;
        try { Clipboard.SetText(SeedResultText.Text); }
        catch { /* 剪贴板偶尔被其它程序占用导致写入失败，忽略即可 */ }
    }

}

/// <summary>用 Windows 资源管理器打开一个文件夹，多处 Tab（文件下载/加载器下载）
/// 的"打开文件夹"按钮共用，避免每个按钮各自写一遍 Process.Start。</summary>
internal static class FolderOpenHelper
{
    public static void Open(string dir)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch
        {
            // 极端情况下打不开资源管理器（比如目录被删了）不应该抛异常打断用户操作，
            // 静默失败即可——用户能直接看到状态文字里已经显示的完整路径，自己手动导航过去。
        }
    }

}
