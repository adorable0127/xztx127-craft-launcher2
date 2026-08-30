using System.Windows;

namespace XCL2.App.Views;

/// <summary>见 HiddenInstancesDialog.xaml 顶部注释。</summary>
public partial class HiddenInstancesDialog : OverlayDialogControl
{
    /// <summary>勾选框绑定项：只是 (Id, 是否勾选) 的简单包装，不需要完整的 INotifyPropertyChanged，
    /// CheckBox 双向绑定靠 ItemsControl 生成的实例本身持有状态即可，弹窗关闭时统一读一遍。</summary>
    public class HiddenItem
    {
        public string Id { get; set; } = "";
        public bool IsChecked { get; set; }
    }

    private readonly List<HiddenItem> _items;

    /// <summary>点"恢复选中项"关闭后，调用方从这里读取用户勾选的 id 列表。</summary>
    public List<string> UnhiddenIds { get; private set; } = new();

    public HiddenInstancesDialog(IEnumerable<string> hiddenIds)
    {
        InitializeComponent();
        _items = hiddenIds.Select(id => new HiddenItem { Id = id }).ToList();
        HiddenListControl.ItemsSource = _items;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        UnhiddenIds = _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
