using System.Collections.ObjectModel;

namespace XCL2.App.Services;

/// <summary>
/// 通用分页帮助类：把一份完整结果集（搜索返回的全部条目）按固定页大小切片，
/// 只把"当前页"的子集放进绑定给 ListBox 的 ObservableCollection 里，
/// 配合 DownloadCenterPage/QuickStartWizardWindow 里的分页条（上一页/页码/下一页）使用。
///
/// 之前的问题：Mod/资源/地图搜索结果一次性全部塞进 ListBox（Modrinth/CurseForge 一次搜索
/// 可能返回几十上百条），高度撑不满、末尾内容被裁掉看不见，用户以为"列表显示不全"。
/// PCL 的做法是分页：每页固定数量，底部有页码条，翻页只是切换"当前显示哪一页"，
/// 不需要重新发网络请求（网络请求本身可以一次性拉一大批，也可以配合 offset 分批拉，
/// 这里先做"客户端分页"：先把这一批结果全部拉回来，再本地切片分页展示，逻辑简单可靠，
/// 不需要每类资源各自实现服务端翻页参数）。
/// </summary>
public class PagedListHelper<T>
{
    private readonly List<T> _allItems = new();
    public ObservableCollection<T> CurrentPageItems { get; } = new();

    /// <summary>每页展示的条目数，参考 PCL 列表密度，20 条能在常见窗口高度下完整显示不裁切。</summary>
    public int PageSize { get; set; } = 20;

    public int CurrentPage { get; private set; } = 1;

    public int TotalCount => _allItems.Count;

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;

    /// <summary>页码文案，例如"第 1 / 5 页 · 共 96 条"，直接绑给分页条中间的 TextBlock。</summary>
    public string PageSummaryText => TotalCount == 0
        ? "没有结果"
        : $"第 {CurrentPage} / {TotalPages} 页 · 共 {TotalCount} 条";

    /// <summary>用一批新结果重置分页状态，回到第 1 页。搜索/刷新/切换筛选条件时调用。</summary>
    public void Reset(IEnumerable<T> items)
    {
        _allItems.Clear();
        _allItems.AddRange(items);
        CurrentPage = 1;
        ApplyCurrentPage();
    }

    public void GoToPage(int page)
    {
        if (page < 1) page = 1;
        if (page > TotalPages) page = TotalPages;
        CurrentPage = page;
        ApplyCurrentPage();
    }

    public void PreviousPage()
    {
        if (HasPreviousPage) GoToPage(CurrentPage - 1);
    }

    public void NextPage()
    {
        if (HasNextPage) GoToPage(CurrentPage + 1);
    }

    private void ApplyCurrentPage()
    {
        CurrentPageItems.Clear();
        foreach (var item in _allItems.Skip((CurrentPage - 1) * PageSize).Take(PageSize))
            CurrentPageItems.Add(item);
    }
}
