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
