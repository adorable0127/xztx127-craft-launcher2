using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XCL2.App.Services;

/// <summary>为文本控件显式安装编辑菜单，避免 WPF 内置菜单绕过应用样式。</summary>
public static class TextEditContextMenuService
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(TextBoxBase), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnEditorLoaded), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(PasswordBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnEditorLoaded), handledEventsToo: true);
    }

    private static void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control editor) return;
        // 保留页面自定义菜单，以及 ContextMenu="{x:Null}" 的显式禁用设置。
        var source = DependencyPropertyHelper.GetValueSource(editor, ContextMenuService.ContextMenuProperty);
        if (source.BaseValueSource != BaseValueSource.Default || editor.ContextMenu != null) return;

        var menu = new ContextMenu { PlacementTarget = editor };
        menu.SetResourceReference(FrameworkElement.StyleProperty, "TextEditContextMenuStyle");
        Populate(menu, editor);
        editor.ContextMenuOpening += (_, _) =>
        {
            // 只读状态可能在加载后改变；目标始终是原编辑器，菜单获取焦点不会丢失选区。
            Populate(menu, editor);
            CommandManager.InvalidateRequerySuggested();
        };
        editor.ContextMenu = menu;
    }

    private static void Populate(ContextMenu menu, Control editor)
    {
        menu.Items.Clear();
        bool password = editor is PasswordBox;
        bool readOnly = editor is TextBoxBase text && text.IsReadOnly;
        if (!password && !readOnly)
            Add(menu, editor, ApplicationCommands.Cut, "剪切", "Ctrl+X",
                "M5,3 A2,2 0 1 1 1,3 A2,2 0 1 1 5,3 M5,13 A2,2 0 1 1 1,13 A2,2 0 1 1 5,13 M4,4 L14,14 M4,12 L14,2");
        if (!password)
            Add(menu, editor, ApplicationCommands.Copy, "复制", "Ctrl+C",
                "M5,5 H14 V15 H5 Z M11,5 V1 H1 V11 H5");
        if (!readOnly)
            Add(menu, editor, ApplicationCommands.Paste, "粘贴", "Ctrl+V",
                "M5,3 H2 V15 H14 V3 H11 M5,1 H11 V5 H5 Z M5,8 H11 M5,11 H10");

        menu.Items.Add(new Separator());
        Add(menu, editor, ApplicationCommands.SelectAll, "全选", "Ctrl+A",
            "M5,1 H1 V5 M11,1 H15 V5 M15,11 V15 H11 M5,15 H1 V11 M5,5 H11 V11 H5 Z");
    }

    private static void Add(ContextMenu menu, Control editor, RoutedUICommand command,
        string label, string shortcut, string geometry)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry), Width = 16, Height = 16,
            Stretch = Stretch.Uniform, StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false
        };
        icon.SetBinding(Shape.StrokeProperty, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1)
        });
        var item = new MenuItem
        {
            Header = label, Icon = icon, Command = command, CommandTarget = editor,
            InputGestureText = shortcut
        };
        item.SetResourceReference(FrameworkElement.StyleProperty, "TextEditMenuItemStyle");
        menu.Items.Add(item);
    }
}
