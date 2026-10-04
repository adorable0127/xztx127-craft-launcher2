using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// AI 提问卡片。AiAssistantService 收到模型的 XCL2_ASK 请求后，经由 AiAssistantPanel 调用
/// <see cref="AskAsync"/>：卡片显示在输入框同一位置（覆盖它），用户选择/输入后点“提交”，
/// 返回 <see cref="AiQuestionAnswer"/>；点“忽略”/按 Esc 返回 null；ct 取消（用户点了停止）则抛 OperationCanceledException。
///
/// 键盘：Tab / 上下键切换行，回车或空格选中选项；在“输入你的回答…”里回车 = 确认；
/// Ctrl+Enter 随时提交；Esc 忽略。
/// </summary>
public partial class AiQuestionCard : UserControl
{
    private sealed class QState
    {
        public QState(AiQuestion q) { Question = q; }
        public AiQuestion Question { get; }
        public HashSet<int> Selected { get; } = new();
        public string Custom { get; set; } = "";
    }

    private sealed class RowInfo
    {
        public int Index { get; init; }
        public TextBlock Check { get; init; } = null!;
    }

    private List<QState> _states = new();
    private int _index;
    private TaskCompletionSource<AiQuestionAnswer?>? _tcs;
    private readonly List<Border> _optionRows = new();
    private Border? _customRow;
    private TextBox? _customBox;

    public AiQuestionCard()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>当前是否正在等用户作答。</summary>
    public bool IsAsking => _tcs != null;

    public Task<AiQuestionAnswer?> AskAsync(AiQuestionSet set, CancellationToken ct)
    {
        // 正常不会同时有两个提问；万一有，把旧的当“忽略”收掉，避免它永远悬着。
        _tcs?.TrySetResult(null);

        var tcs = new TaskCompletionSource<AiQuestionAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tcs = tcs;
        _states = set.Questions.Select(q => new QState(q)).ToList();
        _index = 0;

        var reg = ct.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_tcs, tcs)) return;
            Close();
            tcs.TrySetCanceled(ct);
        })));
        _ = tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);

        Visibility = Visibility.Visible;
        ShowQuestion(0);
        return tcs.Task;
    }

    // ------------------------------------------------------------------
    // 状态/收尾
    // ------------------------------------------------------------------

    private void Close()
    {
        _tcs = null;
        _states = new List<QState>();
        OptionsPanel.Children.Clear();
        _optionRows.Clear();
        _customRow = null;
        _customBox = null;
        Visibility = Visibility.Collapsed;
    }

    private void Finish(AiQuestionAnswer? answer)
    {
        var tcs = _tcs;
        Close();
        tcs?.TrySetResult(answer);
    }

    private void Submit()
    {
        if (_tcs == null || _states.Count == 0) return;
        CaptureCustom();

        // 多个问题时，前面几页的“提交”其实是“下一题”；最后一页才真正提交。
        if (_index < _states.Count - 1)
        {
            ShowQuestion(_index + 1);
            return;
        }

        var answer = new AiQuestionAnswer();
        foreach (var st in _states)
        {
            answer.Items.Add(new AiQuestionAnswerItem
            {
                Selected = st.Selected.OrderBy(i => i).Select(i => st.Question.Options[i]).ToList(),
                CustomText = st.Custom.Trim()
            });
        }
        Finish(answer);
    }

    private void CaptureCustom()
    {
        if (_customBox != null && _index >= 0 && _index < _states.Count)
            _states[_index].Custom = _customBox.Text;
    }

    // ------------------------------------------------------------------
    // 渲染
    // ------------------------------------------------------------------

    private void ShowQuestion(int index)
    {
        CaptureCustom();
        _index = Math.Clamp(index, 0, _states.Count - 1);
        var st = _states[_index];
        var q = st.Question;

        HeaderChip.Visibility = q.Header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = q.Header;
        QuestionText.Text = q.Text;

        PagerPanel.Visibility = _states.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PagerText.Text = $"{_index + 1} / {_states.Count}";
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _states.Count - 1;
        SubmitButton.Content = _index < _states.Count - 1 ? "下一题" : "提交";

        BuildRows(st);
        _ = Dispatcher.BeginInvoke(new Action(FocusFirstRow), DispatcherPriority.Input);
    }

    private void BuildRows(QState st)
    {
        OptionsPanel.Children.Clear();
        _optionRows.Clear();
        _customRow = null;
        _customBox = null;

        var q = st.Question;
        for (var i = 0; i < q.Options.Count; i++)
        {
            var row = MakeOptionRow(i, q.Options[i]);
            _optionRows.Add(row);
            OptionsPanel.Children.Add(row);
        }

        if (q.AllowCustom)
        {
            _customRow = MakeCustomRow(q.Options.Count + 1, st);
            OptionsPanel.Children.Add(_customRow);
        }

        RefreshSelection();
    }

    private static TextBlock MakeText(string text, bool secondary, double size = 13)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty,
            secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
        return tb;
    }

    private Border NewRowShell()
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Focusable = true,
            FocusVisualStyle = null,
            Cursor = Cursors.Hand
        };
        row.IsKeyboardFocusWithinChanged += (_, _) => UpdateRowVisual(row);
        row.MouseEnter += (_, _) => UpdateRowVisual(row);
        row.MouseLeave += (_, _) => UpdateRowVisual(row);
        return row;
    }

    private Border MakeOptionRow(int i, AiQuestionOption opt)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idx = MakeText($"{i + 1}.", secondary: true);
        Grid.SetColumn(idx, 0);

        var label = MakeText(opt.Label, secondary: false);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, opt.Description.Length > 0 ? 10 : 0, 0);
        Grid.SetColumn(label, 1);

        var desc = MakeText(opt.Description, secondary: true);
        Grid.SetColumn(desc, 2);

        var check = MakeText("✓", secondary: false);
        check.Margin = new Thickness(8, 0, 0, 0);
        check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        check.Visibility = Visibility.Collapsed;
        Grid.SetColumn(check, 3);

        grid.Children.Add(idx);
        grid.Children.Add(label);
        grid.Children.Add(desc);
        grid.Children.Add(check);

        var row = NewRowShell();
        row.Child = grid;
        row.Tag = new RowInfo { Index = i, Check = check };
        row.MouseLeftButtonDown += (_, e) =>
        {
            Keyboard.Focus(row);
            SelectOption(i);
            e.Handled = true;
        };
        return row;
    }

    private Border MakeCustomRow(int number, QState st)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var idx = MakeText($"{number}.", secondary: true);
        Grid.SetColumn(idx, 0);

        var box = new TextBox
        {
            Style = null!,   // 不吃全局 TextBox 样式：这里要的是无边框、透明底的内联输入
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 1, 0, 1),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 2000,
            Text = st.Custom
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimaryBrush");

        var placeholder = MakeText("输入你的回答…", secondary: true);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(2, 0, 0, 0);
        placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 先赋 Text 再挂事件：初始化时不应该触发“清空选项”之类的联动。
        box.TextChanged += (_, _) =>
        {
            st.Custom = box.Text;
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // 单选：自己填了内容就等于放弃上面选的选项。
            if (!st.Question.Multiple && box.Text.Length > 0 && st.Selected.Count > 0)
                st.Selected.Clear();
            RefreshSelection();
        };

        var inner = new Grid();
        inner.Children.Add(box);
        inner.Children.Add(placeholder);
        Grid.SetColumn(inner, 1);

        grid.Children.Add(idx);
        grid.Children.Add(inner);

        var row = NewRowShell();
        row.Cursor = Cursors.IBeam;
        row.Child = grid;
        row.MouseLeftButtonDown += (_, e) =>
        {
            Keyboard.Focus(box);
            e.Handled = true;
        };
        _customBox = box;
        return row;
    }

    private void UpdateRowVisual(Border row)
    {
        if (_states.Count == 0) return;
        var st = _states[_index];

        bool selected;
        if (row.Tag is RowInfo info)
        {
            selected = st.Selected.Contains(info.Index);
            info.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            selected = !string.IsNullOrWhiteSpace(st.Custom);
        }

        if (selected || row.IsKeyboardFocusWithin || row.IsMouseOver)
            row.SetResourceReference(Border.BackgroundProperty, "SideBrush");
        else
            row.Background = Brushes.Transparent;
    }

    private void RefreshSelection()
    {
        foreach (var row in _optionRows) UpdateRowVisual(row);
        if (_customRow != null) UpdateRowVisual(_customRow);
    }

    private void SelectOption(int i)
    {
        if (_tcs == null || _index >= _states.Count) return;
        var st = _states[_index];
        if (i < 0 || i >= st.Question.Options.Count) return;

        if (st.Question.Multiple)
        {
            if (!st.Selected.Add(i)) st.Selected.Remove(i);
        }
        else if (st.Selected.Contains(i))
        {
            st.Selected.Clear();
        }
        else
        {
            st.Selected.Clear();
            st.Selected.Add(i);
            // 单选：选了选项就清掉自己填的内容（TextChanged 里文本为空不会再反向清选项）。
            if (_customBox != null && _customBox.Text.Length > 0)
            {
                _customBox.Text = "";
                st.Custom = "";
            }
        }
        RefreshSelection();
    }

    private void FocusFirstRow()
    {
        if (_tcs == null) return;
        if (_optionRows.Count > 0) Keyboard.Focus(_optionRows[0]);
        else if (_customBox != null) Keyboard.Focus(_customBox);
    }

    // ------------------------------------------------------------------
    // 键盘
    // ------------------------------------------------------------------

    private List<UIElement> FocusTargets()
    {
        var list = new List<UIElement>(_optionRows);
        if (_customBox != null) list.Add(_customBox);
        return list;
    }

    private void MoveFocus(int delta)
    {
        var targets = FocusTargets();
        if (targets.Count == 0) return;
        var cur = targets.FindIndex(t => t.IsKeyboardFocusWithin);
        int next;
        if (cur < 0) next = delta > 0 ? 0 : targets.Count - 1;     // 焦点在按钮上：从头/尾进入列表
        else next = Math.Clamp(cur + delta, 0, targets.Count - 1);
        Keyboard.Focus(targets[next]);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tcs == null) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var focused = Keyboard.FocusedElement;
        var inText = _customBox != null && ReferenceEquals(focused, _customBox);

        switch (e.Key)
        {
            case Key.Escape:
                Finish(null);
                e.Handled = true;
                return;

            case Key.Enter when ctrl:
                Submit();
                e.Handled = true;
                return;

            case Key.Up:
                MoveFocus(-1);
                e.Handled = true;
                return;

            case Key.Down:
                MoveFocus(+1);
                e.Handled = true;
                return;

            case Key.Enter or Key.Space when focused is Border { Tag: RowInfo info }:
                SelectOption(info.Index);
                e.Handled = true;
                return;

            case Key.Enter when inText:
                // 输入框里回车：有内容（或这一页已经选了东西）就确认；什么都没有则忽略这次回车，避免误触。
                if (_customBox!.Text.Trim().Length > 0 || (_index < _states.Count && _states[_index].Selected.Count > 0))
                    Submit();
                e.Handled = true;
                return;
        }
    }

    // ------------------------------------------------------------------
    // 按钮
    // ------------------------------------------------------------------

    private void PrevButton_Click(object sender, RoutedEventArgs e) => ShowQuestion(_index - 1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => ShowQuestion(_index + 1);
    private void SubmitButton_Click(object sender, RoutedEventArgs e) => Submit();
    private void IgnoreButton_Click(object sender, RoutedEventArgs e) => Finish(null);
}
