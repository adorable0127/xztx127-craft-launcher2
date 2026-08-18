using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace XCL2.App.Views;

/// <summary>
/// 桌面便签窗口（百宝箱「桌面便签」工具的置顶弹出窗口）：
/// - 无边框、可拖动、默认置顶，仿真实便利贴的黄底样式（不随启动器深浅色主题变，
///   它是"贴在桌面上"的东西，跟桌面环境走，保持醒目的固定配色）；
/// - 内容编辑后自动写回同一个便签文件（防抖 800ms），关闭时强制保存；
/// - 「📌」按钮切换是否置顶，图标随状态变化。
/// </summary>
public partial class StickyNoteWindow : Window
{
    /// <summary>当前所有已置顶到桌面、仍处于打开状态的便签窗口——供 MainWindow 关闭时
    /// 检测"是否还有便签钉在桌面上"，从而决定要不要弹出"是否连同便签一起关闭"的提示。
    /// 构造时加入、Closed 时移除，不需要调用方手动维护。</summary>
    public static readonly List<StickyNoteWindow> OpenWindows = new();

    private readonly string _filePath;
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressSave;

    public StickyNoteWindow(string filePath)
    {
        _filePath = filePath;
        InitializeComponent();

        TitleText.Text = Path.GetFileName(filePath);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => Save();

        _suppressSave = true;
        ContentBox.Text = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
        _suppressSave = false;

        OpenWindows.Add(this);
        Closed += (_, _) => OpenWindows.Remove(this);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* 窗口正在被系统操作时拖不动，忽略 */ }
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
        catch { /* 保存失败不影响窗口使用，下次输入还会再触发保存 */ }
    }
}
