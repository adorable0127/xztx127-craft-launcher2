using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

// 已从独立 Window 迁移为进程内 Overlay 弹窗（不再弹出新的系统窗口，而是盖在启动器
// 主窗口上面）。原来"DialogResult = true/false; Close();"两行统一改成
// "CloseWith(true/false);"一行，语义完全对应，其余业务逻辑（校验、赋值输出属性等）
// 不用动——详见 IOverlayDialog.cs 顶部注释。

/// <summary>
/// 「隐藏项目」独立窗口：把原来平铺在设置页上的「功能隐藏」和「隐藏设置项」两大片勾选框
/// 搬到这里，设置页上只留两个入口按钮。理由见 xaml 头部注释。
///
/// 这次重写顺手修掉了原实现的三个毛病，根因都是同一个——旧代码靠
/// <c>FindVisualChildren&lt;CheckBox&gt;(列表控件)</c> 去遍历可视化树读写勾选状态：
///
///  1. **点大项时小项不跟着选中 / 取消大项小项也不跟着取消**：旧的大项勾选框只是一个
///     携带大类 key 的普通 CheckBox，代码里压根没有"联动子项"这回事，两级各管各的。
///     现在改成 VM 里的 <see cref="HideGroupViewModel.GroupChecked"/> 一改就写穿所有子项。
///  2. **全部小项都勾上时大项不自动变成勾选**：同理，反向联动也没有实现。
///     现在每个子项变化后都会回头重算一次大项状态（全选=勾上，部分=中间态，全不选=不勾）。
///  3. **子项较多的大项点了没反应**：可视化树遍历只能拿到"已经生成出来的"容器，
///     而这些勾选框挂在嵌套 ItemsControl + ScrollViewer 里，没滚动到的部分拿不到；
///     于是点击后有一部分状态读写不到，表现就是"点了像没反应"。改成数据绑定之后，
///     状态存在 VM 上，跟界面有没有把那一行画出来完全无关。
///
/// 勾选只改窗口内的 VM；点"确定"才把结果回传给设置页，设置页再按它自己的
/// "保存才生效"策略写进配置。取消/直接关窗口则什么都不变。
/// </summary>
public partial class HiddenItemsWindow : OverlayDialogControl
{
    private readonly List<HideGroupViewModel> _featureGroups;
    private readonly List<HideGroupViewModel> _settingGroups;

    /// <summary>点"确定"之后，用户最终勾选的「功能隐藏」key 集合。</summary>
    public HashSet<string> ResultFeatureKeys { get; private set; } = new();

    /// <summary>点"确定"之后，用户最终勾选的「隐藏设置项」key 集合（含大类 key 和单项 key）。</summary>
    public HashSet<string> ResultSettingKeys { get; private set; } = new();

    public HiddenItemsWindow(IEnumerable<string> checkedFeatureKeys, IEnumerable<string> checkedSettingKeys)
    {
        InitializeComponent();

        var featureSet = new HashSet<string>(checkedFeatureKeys);
        var settingSet = new HashSet<string>(checkedSettingKeys);

        // 「功能隐藏」的大类在 FeatureVisibilityService 里没有自己的 key（它只是个分组标题），
        // 所以这里的大项勾选框是纯粹的"全选/全不选"开关，GroupKey 传 null，不会被写进结果集合。
        _featureGroups = FeatureVisibilityService.Groups
            .Select(g => new HideGroupViewModel(
                g.GroupLabel,
                groupKey: null,
                g.Items.Select(i => new HideItemViewModel(i.Label, i.Key, featureSet.Contains(i.Key))),
                OnAnyChanged))
            .ToList();

        // 「隐藏设置项」的大类有自己的 key，勾上表示"整类隐藏"，它本身也要进结果集合。
        _settingGroups = SettingsVisibilityService.Groups
            .Select(g => new HideGroupViewModel(
                g.GroupLabel,
                groupKey: g.Key,
                g.Items.Select(i => new HideItemViewModel(i.Label, i.Key, settingSet.Contains(i.Key))),
                OnAnyChanged,
                groupSelfChecked: settingSet.Contains(g.Key)))
            .ToList();

        FeatureGroupList.ItemsSource = _featureGroups;
        SettingGroupList.ItemsSource = _settingGroups;
        RefreshSummary();
    }

    private void OnAnyChanged() => RefreshSummary();

    private void RefreshSummary()
    {
        // SummaryText 在 InitializeComponent 之后才存在；构造期间的第一次回调可能早于它，
        // 所以这里容错一下，不然构造大量 VM 时会在第一个子项回调上直接 NRE。
        if (SummaryText == null) return;
        var features = _featureGroups.Sum(g => g.Items.Count(i => i.IsChecked));
        var settings = _settingGroups.Sum(g => g.Items.Count(i => i.IsChecked) + (g.GroupSelfChecked ? 1 : 0));
        SummaryText.Text = features == 0 && settings == 0
            ? "当前没有隐藏任何东西。"
            : $"已勾选：功能 {features} 项、设置 {settings} 项。点“确定”回到设置页，再点“保存设置”才会真正生效。";
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        // 只清当前这个标签页，不是两页一起清——用户在"隐藏设置项"页点"全部取消勾选"，
        // 十有八九没打算把另一页的功能隐藏也一并清掉，那属于破坏性的意外。
        var target = Tabs.SelectedIndex == 0 ? _featureGroups : _settingGroups;
        foreach (var group in target) group.GroupChecked = false;
        RefreshSummary();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultFeatureKeys = new HashSet<string>(
            _featureGroups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Key));

        ResultSettingKeys = new HashSet<string>(
            _settingGroups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Key));
        foreach (var group in _settingGroups.Where(g => g.GroupSelfChecked && g.GroupKey != null))
            ResultSettingKeys.Add(group.GroupKey!);

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);
}

/// <summary>「隐藏项目」窗口里的一个大项。承载两级勾选框之间的全部联动逻辑。</summary>
public sealed class HideGroupViewModel : INotifyPropertyChanged
{
    private readonly Action _onChanged;

    /// <summary>联动期间置位，避免"父改子 → 子回调改父 → 父再改子"这种来回打架的递归。
    /// 没有这个标志，勾一次大项就会在两级之间反复弹跳，子项多的时候能明显卡住。</summary>
    private bool _syncing;

    public string GroupLabel { get; }

    /// <summary>大类自己的 key；为 null 表示这个大项只是"全选开关"，不代表一条可保存的隐藏项
    /// （「功能隐藏」那一侧就是这种情况）。</summary>
    public string? GroupKey { get; }

    public List<HideItemViewModel> Items { get; }

    /// <summary>大类自己是否被勾上（只有 GroupKey 非 null 时才有意义 = 整类隐藏）。</summary>
    public bool GroupSelfChecked { get; private set; }

    public HideGroupViewModel(string groupLabel, string? groupKey,
        IEnumerable<HideItemViewModel> items, Action onChanged, bool groupSelfChecked = false)
    {
        GroupLabel = groupLabel;
        GroupKey = groupKey;
        _onChanged = onChanged;
        GroupSelfChecked = groupSelfChecked;
        Items = items.ToList();

        foreach (var item in Items) item.Owner = this;
        RecomputeGroupState(notify: false);
    }

    private bool? _groupChecked;

    /// <summary>
    /// 三态：true = 这一类全部勾上，false = 全部没勾，null = 只勾了一部分。
    ///
    /// 界面上用户只能在 true/false 之间点（IsThreeState 让中间态可以*显示*出来，
    /// 但 WPF 的点击循环是 false → true → null → false；这里在 setter 里把用户点出来的
    /// null 直接当成 false 处理，因为"部分选中"是子项状态的结果，不该是用户能主动选的一档，
    /// 让它出现在点击循环里只会让人点三下才回到原点）。
    /// </summary>
    public bool? GroupChecked
    {
        get => _groupChecked;
        set
        {
            var target = value == true; // null（用户点出来的中间态）按"取消全选"处理
            if (_syncing)
            {
                // 来自 RecomputeGroupState 的内部更新：直接落值，不要反过来再写子项。
                _groupChecked = value;
                OnPropertyChanged();
                return;
            }

            _syncing = true;
            try
            {
                _groupChecked = target;
                GroupSelfChecked = GroupKey != null && target;
                foreach (var item in Items) item.SetCheckedSilently(target);
            }
            finally { _syncing = false; }

            OnPropertyChanged();
            _onChanged();
        }
    }

    /// <summary>某个子项被用户改动之后，回头重算大项应该显示成哪一态。
    /// 这就是"把所有小项都勾上，大项自动变成勾选"这条需求的实现。</summary>
    internal void OnItemChanged()
    {
        if (_syncing) return;
        RecomputeGroupState(notify: true);
        _onChanged();
    }

    private void RecomputeGroupState(bool notify)
    {
        var checkedCount = Items.Count(i => i.IsChecked);
        bool? state = Items.Count == 0 ? GroupSelfChecked
            : checkedCount == Items.Count ? true
            : checkedCount == 0 ? false
            : null;

        // 全选时大类自己也算被隐藏（GroupKey 存在的那一侧），跟用户"我把这一整类关掉了"的
        // 心智一致；只勾了一部分时不动大类 key，否则会把没勾的那几条也一起藏掉。
        if (GroupKey != null) GroupSelfChecked = state == true;

        _syncing = true;
        try { GroupChecked = state; }
        finally { _syncing = false; }

        if (notify) OnPropertyChanged(nameof(GroupChecked));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>「隐藏项目」窗口里的一个小项。</summary>
public sealed class HideItemViewModel : INotifyPropertyChanged
{
    private bool _isChecked;

    public string Label { get; }
    public string Key { get; }
    internal HideGroupViewModel? Owner { get; set; }

    public HideItemViewModel(string label, string key, bool isChecked)
    {
        Label = label;
        Key = key;
        _isChecked = isChecked;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            Owner?.OnItemChanged();
        }
    }

    /// <summary>父项联动时用：改值并通知界面，但**不**回调父项重算——那一轮重算由父项自己做，
    /// 让每个子项都触发一次会变成 O(n²)，正是子项多的大类点起来发卡的原因。</summary>
    internal void SetCheckedSilently(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
