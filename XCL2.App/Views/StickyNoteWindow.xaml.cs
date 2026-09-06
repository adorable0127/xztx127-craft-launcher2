using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Views;

/// <summary>
/// 桌面便签窗口（百宝箱「桌面便签」工具的置顶弹出窗口）。
/// 内容与样式都纯本地保存；正文仍是原来的 .txt，样式另存为同名 .style 文件，
/// 不破坏用户已有便签文本格式。
/// </summary>
public partial class StickyNoteWindow : Window
{
    public static readonly List<StickyNoteWindow> OpenWindows = new();

    private sealed record NotePalette(
        string Key, string Card, string TitleBar, string Editor, string Text,
        string TitleText, string Selection, string Grip);

    private static readonly NotePalette[] Palettes =
    {
        new("Yellow",   "#FFF9C4", "#F7E28A", "#FFFDF0", "#3D3D3D", "#6B5B1E", "#F0DE84", "#B8A45A"),
        new("Blue",     "#DCEEFF", "#BBDDFC", "#EFF8FF", "#17324D", "#24547B", "#A8D4FA", "#5A93BE"),
        new("Mint",     "#DDF7E8", "#BDEBCF", "#F0FCF5", "#173C2B", "#286548", "#A9E2C0", "#65A982"),
        new("Pink",     "#FFE2EC", "#F7C4D7", "#FFF3F7", "#4D2635", "#7D3E57", "#F2B6CE", "#BA718F"),
        new("Purple",   "#EDE3FF", "#D5C0F6", "#F8F4FF", "#35284C", "#604B82", "#CDB7EE", "#8B70B2"),
        new("Paper",    "#F7F7F4", "#E4E5E1", "#FFFFFF", "#262A2F", "#505963", "#D9E0E6", "#88929D"),
        new("Midnight", "#232936", "#30394B", "#1C222D", "#EEF3FA", "#D9E6F5", "#4B5F7A", "#8AA1BF")
    };

    private readonly string _filePath;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressSave;
    private string _styleKey = "Yellow";

    public string FilePath => _filePath;

    public StickyNoteWindow(string filePath, string? styleKey = null)
    {
        _filePath = filePath;
        InitializeComponent();

        // 修复"内容全部显示成一片惨白、文字/图标都是极浅的灰白色几乎看不清"：
        // Windows 11 会给"没有系统标题栏"（WindowStyle=None）的顶层窗口自动套上一层
        // 云母/亚克力（Mica/Acrylic）背景材质，这层材质会盖在我们自己画的内容上面，
        // 把所有颜色都往白色方向"冲淡"——症状正是"该有的底色/字色全在，但看起来像
        // 蒙了一层白纱"，跟上一步修的"分层窗口合成失败=纯灰方块"是两个完全不同的问题。
        // 用 DwmSetWindowAttribute 显式把这个窗口的 SystemBackdropType 设成
        // DWMSBT_NONE（=1），关掉这层自动材质，让 NoteCard/TitleBar/ContentBox 自己
        // 画的颜色不再被蒙白。SourceInitialized 时机足够早——这时候 HWND 已经创建好，
        // 但窗口通常还没真正显示到屏幕上。
        SourceInitialized += (_, _) => TryDisableSystemBackdrop();

        TitleText.Text = Path.GetFileName(filePath);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => Save();

        _suppressSave = true;
        try { ContentBox.Text = File.Exists(filePath) ? File.ReadAllText(filePath) : ""; }
        catch { ContentBox.Text = ""; }
        _suppressSave = false;

        ApplyStyle(string.IsNullOrWhiteSpace(styleKey) ? ReadStyleKey(filePath) : styleKey!, persist: false);

        OpenWindows.Add(this);
        Closed += (_, _) => OpenWindows.Remove(this);
    }

    private static string StylePath(string notePath) => notePath + ".style";

    /// <summary>关闭 Windows 11 给无标题栏顶层窗口自动加的云母/亚克力背景材质。
    /// 失败（比如系统版本更老、dwmapi.dll 里没有这个属性）就直接忽略——
    /// 老系统本来就不会有这个自动加材质的行为，不需要这个调用也不受影响。</summary>
    private void TryDisableSystemBackdrop()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int backdropNone = DwmsbtNone;
            NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaSystembackdropType, ref backdropNone, sizeof(int));
        }
        catch { /* 老系统/驱动不支持，忽略即可 */ }
    }

    private const int DwmwaSystembackdropType = 38; // DWMWA_SYSTEMBACKDROP_TYPE（Windows 11 22H2+）
    private const int DwmsbtNone = 1;                // DWMSBT_NONE：不要任何自动背景材质

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }

    public static string ReadStyleKey(string notePath)
    {
        try
        {
            var value = File.Exists(StylePath(notePath)) ? File.ReadAllText(StylePath(notePath)).Trim() : "Yellow";
            return Palettes.Any(p => string.Equals(p.Key, value, StringComparison.OrdinalIgnoreCase)) ? value : "Yellow";
        }
        catch { return "Yellow"; }
    }

    public static void WriteStyleKey(string notePath, string styleKey)
    {
        var normalized = Palettes.FirstOrDefault(p => string.Equals(p.Key, styleKey, StringComparison.OrdinalIgnoreCase))?.Key ?? "Yellow";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
            File.WriteAllText(StylePath(notePath), normalized);
        }
        catch { }
    }

    public void ApplyStyle(string styleKey, bool persist = true)
    {
        var palette = Palettes.FirstOrDefault(p => string.Equals(p.Key, styleKey, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];
        _styleKey = palette.Key;

        Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        // 窗口本身现在是不透明的（见 xaml 头部关于去掉 AllowsTransparency 的说明），
        // Window.Background 也要跟着换色，不然切样式时只有 NoteCard 变了色，
        // 四个直角窗口边缘会露出上一个样式的颜色，跟 NoteCard 的圆角对不上。
        Background = B(palette.Card);
        NoteCard.Background = B(palette.Card);
        TitleBar.Background = B(palette.TitleBar);
        ContentBox.Background = B(palette.Editor);
        ContentBox.Foreground = B(palette.Text);
        ContentBox.CaretBrush = B(palette.Text);
        ContentBox.SelectionBrush = B(palette.Selection);
        TitleText.Foreground = B(palette.TitleText);
        NoteResizeGrip.Foreground = B(palette.Grip);

        if (persist) WriteStyleKey(_filePath, _styleKey);
        StyleBtn.ToolTip = $"切换便签样式（当前：{GetStyleDisplayName(_styleKey)}）";
    }

    private static string GetStyleDisplayName(string key) => key switch
    {
        "Blue" => "天空蓝",
        "Mint" => "薄荷绿",
        "Pink" => "樱花粉",
        "Purple" => "紫晶",
        "Paper" => "纸白",
        "Midnight" => "深夜",
        _ => "经典黄纸"
    };

    private void StyleBtn_Click(object sender, RoutedEventArgs e)
    {
        var current = Array.FindIndex(Palettes, p => string.Equals(p.Key, _styleKey, StringComparison.OrdinalIgnoreCase));
        var next = Palettes[(current + 1 + Palettes.Length) % Palettes.Length];
        ApplyStyle(next.Key);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinBtn.Content = Topmost ? "📌" : "📍";
        PinBtn.ToolTip = Topmost ? "点击取消置顶" : "点击置顶";
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Save();
        Close();
    }

    private void ContentBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressSave) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ContentBox_LostFocus(object sender, RoutedEventArgs e) => Save();
    private void StickyNoteWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) => Save();

    private void Save()
    {
        _saveTimer.Stop();
        if (_suppressSave) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, ContentBox.Text);
        }
        catch { }
    }
}
