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

public sealed class TouchControlsSettingsWindow : Window, IGlobalWindowTreatmentExempt
{
    private readonly ObservableCollection<TouchControlDefinition> _rows = new(TouchControlSettingsService.Load());
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false,
        CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, MinRowHeight = 38,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private TouchControlDefinition? _recordingRow;
    private CheckBox _touchpadCheck = null!;
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
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/XCL2;component/Views/TouchControlsGridStyles.xaml", UriKind.Relative)
        });
        Style = new Style(typeof(Window));
        Resources[typeof(DataGrid)] = new Style(typeof(DataGrid));
        UpdateEditorPalette();
        Loaded += (_, _) => UpdateEditorPalette();
        SetResourceReference(BackgroundProperty, "TouchEditorSurfaceBrush");
        SetResourceReference(ForegroundProperty, "TouchEditorTextBrush");
        Opacity = 1;
        ThemeService.AppearanceChanged += UpdateEditorPalette;
        Closed += (_, _) => ThemeService.AppearanceChanged -= UpdateEditorPalette;
        _grid.Resources = Resources;
        ApplyGridTheme();
        var root = new DockPanel { Margin = new Thickness(16) };
        root.SetResourceReference(Panel.BackgroundProperty, "TouchEditorSurfaceBrush");
        Content = root;
        var hint = new TextBlock { Text = "双击单元格编辑。坐标/宽高使用 DIP；右/下锚点从右/下边缘计量，底部居中 X 为中心偏移。\n默认 E 键仅在游戏中显示；背包可用 ESC 关闭。绑定与游戏设置保持一致，保存后立即生效。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TouchEditorSecondaryTextBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TouchEditorSecondaryTextBrush");
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        _touchpadCheck = new CheckBox
        {
            Content = "显示触摸板（默认隐藏；开启后右侧会出现一块相对位移区，滑动=移动指针/转视角，轻点=左键）",
            IsChecked = TouchControlSettingsService.TouchpadEnabled, Margin = new Thickness(0, 0, 0, 10)
        };
        _touchpadCheck.SetResourceReference(Control.ForegroundProperty, "TouchEditorTextBrush");
        DockPanel.SetDock(_touchpadCheck, Dock.Top); root.Children.Add(_touchpadCheck);
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        void Button(string label, Action action, bool primary = false)
        {
            var button = new Button { Content = label, Style = (Style)FindResource(primary ? "TouchEditorPrimaryButton" : "TouchEditorButton"),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 6) };
            button.Click += (_, _) =>
            {
                _recordingRow = null;
                _status.SetResourceReference(TextBlock.ForegroundProperty, "TouchEditorSecondaryTextBrush");
                action();
            };
            actions.Children.Add(button);
        }
        Button("添加组件", () => { if (!Commit()) return; var row = new TouchControlDefinition(); _rows.Add(row); _grid.SelectedItem = row; _grid.ScrollIntoView(row); });
        Button("删除选中", () => { if (Commit() && _grid.SelectedItem is TouchControlDefinition row) _rows.Remove(row); });
        Button("录入选中行按键", () =>
        {
            if (!Commit() || _grid.SelectedItem is not TouchControlDefinition) { _status.Text = "请先选择一行。"; return; }
            _recordingRow = (TouchControlDefinition)_grid.SelectedItem; _status.Text = "请按键（支持 Ctrl/Shift/Alt 组合；ESC 也可录入）。";
        });
        Button("恢复默认", () => { _grid.CancelEdit(); _grid.CancelEdit(DataGridEditingUnit.Row); _rows.Clear();
            foreach (var row in TouchControlSettingsService.CreateDefaults()) _rows.Add(row); _touchpadCheck.IsChecked = false; _status.Text = "已载入默认按键（触摸板默认隐藏），保存后生效。"; });
        Button("保存", Save, primary: true);
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

    private void UpdateEditorPalette()
    {
        // 整个编辑器（含按钮及模板内的文字）使用独立不透明配色。
        // 浅色使用灰蓝底和深灰字，不使用全局的透明文字、玻璃背景或皮肤强调色。
        var dark = ThemeService.CurrentIsDarkMode;
        void SetBrush(string key, string lightColor, string darkColor)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor)!);
            brush.Freeze();
            Resources[key] = brush;
        }
        SetBrush("TouchEditorSurfaceBrush", "#E8EDF3", "#0F172A");
        SetBrush("TouchEditorBackgroundBrush", "#F5F7FA", "#1E293B");
        SetBrush("TouchEditorAlternateRowBrush", "#EAF0F6", "#253348");
        SetBrush("TouchEditorHeaderBrush", "#D5DFEA", "#162238");
        SetBrush("TouchEditorTextBrush", "#17212F", "#F1F5F9");
        SetBrush("TouchEditorSecondaryTextBrush", "#334155", "#CBD5E1");
        SetBrush("TouchEditorBorderBrush", "#94A3B8", "#64748B");
        SetBrush("TouchEditorButtonBrush", "#DAE3ED", "#334155");
        SetBrush("TouchEditorButtonHoverBrush", "#C5D3E3", "#475569");
        SetBrush("TouchEditorButtonPressedBrush", "#B4C5DA", "#52657F");
        SetBrush("TouchEditorFocusBrush", "#1D4ED8", "#93C5FD");
        SetBrush("TouchEditorErrorBrush", "#B91C1C", "#FCA5A5");

        // 系统模板及滚动条仍会引用这些公共键；只在本窗口中提供不透明的替代值。
        Resources["TextPrimaryBrush"] = Resources["TouchEditorTextBrush"];
        Resources["TextSecondaryBrush"] = Resources["TouchEditorSecondaryTextBrush"];
        Resources["PanelBrush"] = Resources["TouchEditorBackgroundBrush"];
        Resources["SideBrush"] = Resources["TouchEditorSurfaceBrush"];
        Resources["BorderBrush2"] = Resources["TouchEditorBorderBrush"];
        Resources["AccentBrush"] = Resources["TouchEditorFocusBrush"];
    }

    private void ApplyGridTheme()
    {
        // 表格的系统默认白底不会随应用主题变化，所有表面及文字统一使用动态资源。
        _grid.SetResourceReference(Control.BackgroundProperty, "TouchEditorBackgroundBrush");
        _grid.SetResourceReference(Control.ForegroundProperty, "TouchEditorTextBrush");
        _grid.SetResourceReference(Control.BorderBrushProperty, "TouchEditorBorderBrush");
        _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "TouchEditorBorderBrush");
        _grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "TouchEditorBorderBrush");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TouchEditorBackgroundBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TouchEditorAlternateRowBrush");
        _grid.AlternationCount = 2;
        _grid.BorderThickness = new Thickness(1);
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;

        static Setter Resource(DependencyProperty property, string key)
            => new(property, new DynamicResourceExtension(key));
        _grid.ColumnHeaderStyle = (Style)FindResource("TouchEditorColumnHeader");

        // 使用独立的不透明配色；皮肤的强调色和文字透明度不能降低选中态对比度。
        var selectionBackground = (Brush)_grid.FindResource("TouchGridSelectionBrush");
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(Resource(Control.ForegroundProperty, "TouchEditorTextBrush"));
        var rowSelected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowSelected.Setters.Add(new Setter(Control.BackgroundProperty, selectionBackground));
        rowSelected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        rowStyle.Triggers.Add(rowSelected);
        _grid.RowStyle = rowStyle;

        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(Resource(Control.ForegroundProperty, "TouchEditorTextBrush"));
        cell.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        cell.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, selectionBackground));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        cell.Triggers.Add(selected);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(Resource(Control.BorderBrushProperty, "TouchEditorFocusBrush"));
        cell.Triggers.Add(focused);
        _grid.CellStyle = cell;

        // 显式关联单元格前景色，避免隐式 TextBlock 样式覆盖选中行的文字颜色。
        _textStyle = new Style(typeof(TextBlock));
        _textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty,
            new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) }));
        _textStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        _textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4)));
        _editorStyle = (Style)FindResource("TouchGridTextEditor");
        _comboStyle = (Style)_grid.FindResource("TouchGridComboEditor");
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
            TouchControlSettingsService.SaveTouchpadEnabled(_touchpadCheck.IsChecked == true, notify: false);
            TouchControlSettingsService.Save(_rows);
            DialogResult = true;
        }
        catch (Exception ex) { _status.SetResourceReference(TextBlock.ForegroundProperty, "TouchEditorErrorBrush"); _status.Text = ex.Message; }
    }
}
