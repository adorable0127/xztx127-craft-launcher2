using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class TouchControlsSettingsWindow : Window
{
    private readonly ObservableCollection<TouchControlDefinition> _rows = new(TouchControlSettingsService.Load());
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false,
        CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, MinRowHeight = 38,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private TouchControlDefinition? _recordingRow;
    private Style _textStyle = null!;
    private Style _editorStyle = null!;
    private Style _comboStyle = null!;
    private Style _comboDisplayStyle = null!;
    private sealed record Choice<T>(T Value, string Label);

    public TouchControlsSettingsWindow()
    {
        Title = "触屏按键与布局设置";
        Width = Math.Min(1220, SystemParameters.WorkArea.Width);
        Height = Math.Min(720, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "SideBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        ApplyGridTheme();
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var hint = new TextBlock { Text = "双击单元格编辑。坐标/宽高使用 DIP；右/下锚点从右/下边缘计量，底部居中 X 为中心偏移。\n菜单动作：背包、ESC 设为“切换”，聊天设为“打开”。绑定与游戏设置保持一致，保存后立即生效。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 6) };
            button.Click += (_, _) => { _recordingRow = null; action(); }; actions.Children.Add(button);
        }
        Button("添加组件", () => { if (!Commit()) return; var row = new TouchControlDefinition(); _rows.Add(row); _grid.SelectedItem = row; _grid.ScrollIntoView(row); });
        Button("删除选中", () => { if (Commit() && _grid.SelectedItem is TouchControlDefinition row) _rows.Remove(row); });
        Button("录入选中行按键", () =>
        {
            if (!Commit() || _grid.SelectedItem is not TouchControlDefinition) { _status.Text = "请先选择一行。"; return; }
            _recordingRow = (TouchControlDefinition)_grid.SelectedItem; _status.Text = "请按键（支持 Ctrl/Shift/Alt 组合；ESC 也可录入）。";
        });
        Button("恢复默认", () => { _grid.CancelEdit(); _grid.CancelEdit(DataGridEditingUnit.Row); _rows.Clear();
            foreach (var row in TouchControlSettingsService.CreateDefaults()) _rows.Add(row); _status.Text = "已载入默认按键，保存后生效。"; });
        Button("保存", Save);
        Button("取消", () => Close());
        _grid.ItemsSource = _rows;
        root.Children.Add(_grid);
        var checkStyle = (Style)_grid.FindResource("TouchGridCheckBox");
        var checkDisplayStyle = new Style(typeof(CheckBox), checkStyle);
        checkDisplayStyle.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
        checkDisplayStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "启用", Width = 64,
            ElementStyle = checkDisplayStyle, EditingElementStyle = checkStyle,
            Binding = new Binding(nameof(TouchControlDefinition.Enabled)) { Mode = BindingMode.TwoWay }
        });
        TextColumn("名称", nameof(TouchControlDefinition.Label), 110);
        TextColumn("绑定", nameof(TouchControlDefinition.Binding), 120);
        EnumColumn("模式", nameof(TouchControlDefinition.Mode), new[] { "点按", "按住", "切换保持", "功能命令" }, Enum.GetValues<TouchControlMode>());
        EnumColumn("菜单动作", nameof(TouchControlDefinition.MenuAction), new[] { "无", "切换", "打开", "关闭" }, Enum.GetValues<TouchMenuAction>());
        EnumColumn("显示范围", nameof(TouchControlDefinition.Visibility), new[] { "始终", "游戏中", "菜单中" }, Enum.GetValues<TouchControlVisibility>());
        EnumColumn("锚点", nameof(TouchControlDefinition.Anchor), new[] { "左上", "右上", "左下", "右下", "底部居中" }, Enum.GetValues<TouchControlAnchor>());
        TextColumn("X", nameof(TouchControlDefinition.X), 70); TextColumn("Y", nameof(TouchControlDefinition.Y), 70);
        TextColumn("宽", nameof(TouchControlDefinition.Width), 65); TextColumn("高", nameof(TouchControlDefinition.Height), 65);
        TextColumn("层级", nameof(TouchControlDefinition.ZIndex), 65);
        _status.Text = "鼠标：MOUSE_L / MOUSE_R / MOUSE_M；滚轮：WHEEL_UP / WHEEL_DOWN。键盘按钮可一键打开系统屏幕键盘。";
        PreviewKeyDown += RecordKey;
    }

    private void ApplyGridTheme()
    {
        _grid.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/XCL2;component/Views/TouchControlsGridStyles.xaml", UriKind.Relative)
        });
        // 表格的系统默认白底不会随应用主题变化，所有表面及文字统一使用动态资源。
        _grid.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        _grid.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _grid.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
        _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "DividerBrush");
        _grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "DividerBrush");
        _grid.RowBackground = Brushes.Transparent;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;

        static Setter Resource(DependencyProperty property, string key)
            => new(property, new DynamicResourceExtension(key));
        var header = new Style(typeof(DataGridColumnHeader));
        header.Setters.Add(Resource(Control.BackgroundProperty, "SideBrush"));
        header.Setters.Add(Resource(Control.ForegroundProperty, "TextPrimaryBrush"));
        header.Setters.Add(Resource(Control.BorderBrushProperty, "DividerBrush"));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 10, 8, 10)));
        header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        _grid.ColumnHeaderStyle = header;

        // 使用独立的不透明配色；皮肤的强调色和文字透明度不能降低选中态对比度。
        var selectionBackground = (Brush)_grid.FindResource("TouchGridSelectionBrush");
        var rowStyle = new Style(typeof(DataGridRow));
        var rowSelected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowSelected.Setters.Add(new Setter(Control.BackgroundProperty, selectionBackground));
        rowSelected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        rowStyle.Triggers.Add(rowSelected);
        _grid.RowStyle = rowStyle;

        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(Resource(Control.ForegroundProperty, "TextPrimaryBrush"));
        cell.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        cell.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, selectionBackground));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        cell.Triggers.Add(selected);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(147, 197, 253))));
        cell.Triggers.Add(focused);
        _grid.CellStyle = cell;

        // 显式关联单元格前景色，避免隐式 TextBlock 样式覆盖选中行的文字颜色。
        _textStyle = new Style(typeof(TextBlock));
        _textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty,
            new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) }));
        _textStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        _textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4)));
        _editorStyle = new Style(typeof(TextBox), (Style)FindResource(typeof(TextBox)));
        _editorStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        _editorStyle.Setters.Add(new Setter(Control.BackgroundProperty, selectionBackground));
        _editorStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        _editorStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.White));
        _editorStyle.Setters.Add(new Setter(TextBox.SelectionBrushProperty, Brushes.CornflowerBlue));
        _comboStyle = new Style(typeof(ComboBox), (Style)FindResource(typeof(ComboBox)));
        _comboDisplayStyle = (Style)_grid.FindResource("TouchGridComboDisplay");
    }

    private void TextColumn(string title, string path, double width) => _grid.Columns.Add(new DataGridTextColumn
    { Header = title, Width = width, ElementStyle = _textStyle, EditingElementStyle = _editorStyle, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true } });
    private void EnumColumn<T>(string title, string path, string[] labels, T[] values) => _grid.Columns.Add(new DataGridComboBoxColumn
    {
        Header = title, Width = 100, ItemsSource = values.Select((v, i) => new Choice<T>(v, labels[i])).ToArray(),
        ElementStyle = _comboDisplayStyle, EditingElementStyle = _comboStyle,
        DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValueBinding = new Binding(path)
    });
    private bool Commit()
    {
        if (_grid.CommitEdit(DataGridEditingUnit.Cell, true) && _grid.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        _status.Text = "请先修正当前单元格的数值。"; return false;
    }
    private void RecordKey(object sender, KeyEventArgs e)
    {
        if (_recordingRow is not TouchControlDefinition row) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return;
        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("CTRL");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("SHIFT");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("ALT");
        parts.Add(TouchInputInjector.GetKeyName((ushort)vk));
        // 录入期间仍可能重新进入表格编辑，先提交，避免覆盖尚未提交的值。
        if (!Commit()) return;
        row.Binding = string.Join("+", parts);
        if (row.Mode == TouchControlMode.Command) row.Mode = TouchControlMode.Tap;
        _recordingRow = null;
        _status.Text = $"已录入：{row.Binding}";
    }
    private void Save()
    {
        if (!Commit()) return;
        try
        {
            TouchControlSettingsService.Save(_rows);
            DialogResult = true;
        }
        catch (Exception ex) { _status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); _status.Text = ex.Message; }
    }
}
