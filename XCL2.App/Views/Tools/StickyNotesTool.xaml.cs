using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>
/// 百宝箱「桌面便签」工具：纯本地文本便签，不联网。
/// - 便签以 .txt 文件形式存放在数据目录 StickyNotes/ 下，随时可以手动打开/备份/迁移；
/// - 左侧列表 + 右侧编辑区，编辑内容自动保存（防抖 800ms，切换/关闭/删除前强制落盘）；
/// - 「📌 作为桌面便签打开」把当前便签弹出为一个置顶小窗口（StickyNoteWindow），
///   可以随手记在桌面上，窗口里的改动同样自动写回同一个文件。
/// </summary>
public partial class StickyNotesTool : UserControl
{
    private static string NotesDir => Path.Combine(App.DataDir, "StickyNotes");

    private string? _currentFile;          // 当前正在编辑的便签文件路径
    private bool _suppressSave;            // 程序自己改文本框（加载内容）时抑制保存
    private readonly DispatcherTimer _saveTimer;

    private sealed record NoteItem(string Path, string Title, string TimeText);

    public StickyNotesTool()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => SaveCurrent();
        RefreshNoteList();
        NoteStatusText.Text = "便签保存在：" + NotesDir;
    }

    // ==================== 列表 ====================

    private void RefreshNoteList()
    {
        try { Directory.CreateDirectory(NotesDir); } catch { /* 建不了会在读写时自然报错 */ }

        var items = new List<NoteItem>();
        try
        {
            items = new DirectoryInfo(NotesDir)
                .GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new NoteItem(f.FullName, GetNoteTitle(f.FullName), f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")))
                .ToList();
        }
        catch { /* 读取失败列表为空 */ }

        var prev = _currentFile;
        NoteList.ItemsSource = items;

        // 重新绑定后按路径还原选中项（新建/删除后列表刷新，选中态不应丢失）
        if (prev != null)
        {
            foreach (var it in items)
            {
                if (string.Equals(it.Path, prev, StringComparison.OrdinalIgnoreCase))
                {
                    NoteList.SelectedItem = it;
                    break;
                }
            }
        }
    }

    private static string GetNoteTitle(string path)
    {
        try
        {
            var firstLine = File.ReadLines(path).FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(firstLine)) return firstLine.Length > 20 ? firstLine[..20] + "…" : firstLine;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveCurrent(); // 切走之前把上一个便签落盘

        _currentFile = (NoteList.SelectedItem as NoteItem)?.Path;
        if (_currentFile == null)
        {
            NoteEditor.Clear();
            NoteEditor.IsEnabled = false;
            NotePinBtn.IsEnabled = false;
            return;
        }

        _suppressSave = true;
        try
        {
            NoteEditor.Text = File.Exists(_currentFile)
                ? File.ReadAllText(_currentFile)
                : "";
        }
        catch (Exception ex)
        {
            NoteStatusText.Text = "读取便签失败：" + ex.Message;
        }
        finally
        {
            _suppressSave = false;
        }
        NoteEditor.IsEnabled = true;
        NotePinBtn.IsEnabled = true;
    }

    // ==================== 编辑保存 ====================

    private void NoteEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSave || _currentFile == null) return;
        _saveTimer.Stop();
        _saveTimer.Start(); // 防抖：停下来不敲了 800ms 后再写盘
    }

    private void NoteEditor_LostFocus(object sender, RoutedEventArgs e) => SaveCurrent();

    private void StickyNotesTool_Unloaded(object sender, RoutedEventArgs e) => SaveCurrent();

    private void SaveCurrent()
    {
        _saveTimer.Stop();
        if (_currentFile == null || _suppressSave) return;
        try
        {
            Directory.CreateDirectory(NotesDir);
            File.WriteAllText(_currentFile, NoteEditor.Text);
            NoteStatusText.Text = "已自动保存 " + Path.GetFileName(_currentFile);
        }
        catch (Exception ex)
        {
            NoteStatusText.Text = "保存便签失败：" + ex.Message;
        }
    }

    // ==================== 按钮 ====================

    private void NoteNew_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(NotesDir, $"便签-{DateTime.Now:yyyyMMddHHmmss}.txt");
        try
        {
            Directory.CreateDirectory(NotesDir);
            File.WriteAllText(path, "新便签\n");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"创建便签失败：{ex.Message}");
            return;
        }
        RefreshNoteList();
        foreach (var it in NoteList.ItemsSource as IEnumerable<NoteItem> ?? Enumerable.Empty<NoteItem>())
        {
            if (string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                NoteList.SelectedItem = it;
                NoteList.ScrollIntoView(it);
                break;
            }
        }
        NoteEditor.Focus();
    }

    private void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null) return;
        if (!MessageBoxDialog.ShowConfirm(
                $"确定删除便签「{Path.GetFileName(_currentFile)}」吗？删除后无法恢复。",
                "删除便签")) return;

        SaveCurrent();
        try { File.Delete(_currentFile); } catch (Exception ex) { MessageBoxDialog.ShowError($"删除失败：{ex.Message}"); return; }
        _currentFile = null;
        RefreshNoteList();
        NoteStatusText.Text = "已删除便签";
    }

    private void NoteOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(NotesDir); } catch { }
        FolderOpenHelper.Open(NotesDir);
    }

    private void NotePin_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null) return;
        SaveCurrent();
        var win = new StickyNoteWindow(_currentFile) { Topmost = true };
        win.Show();
        NoteStatusText.Text = $"已弹出桌面便签：{Path.GetFileName(_currentFile)}（窗口置顶，可拖动）";
    }
}
