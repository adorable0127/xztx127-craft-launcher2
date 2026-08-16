using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 「鸣谢与帮助」页：左侧导航栏新增的一个页面，内部分两个子板块——
/// 「帮助」（默认停留：登录微软账户 / 购买 Minecraft / 开局第一天 / 报错排查）
/// 和「鸣谢」（作者信息、赞助入口、开源仓库、感谢名单、赞助榜、哔哩哔哩粉丝墙）。
///
/// 两个板块实际上是同一个 ScrollViewer 里上下排列的两大块内容，不是真正意义上的
/// "切页"：顶部的「帮助/鸣谢」两个 Tab 按钮点击时滚动定位到对应板块；
/// 反过来往下滑动滚动条越过分割线时，也会把 Tab 的选中态同步过去——两种触发方式
/// 互相呼应，符合"默认停在帮助，往下滑就能到鸣谢"的需求描述，不需要真的用
/// TabControl/多个 UserControl 来回替换。
/// </summary>
public partial class AboutHelpPage : UserControl
{
    // 官网跳转地址：整理成常量，方便以后统一维护/替换镜像地址。
    private const string MicrosoftLoginUrl = "https://login.live.com/";
    private const string MinecraftBuyUrl = "https://www.minecraft.net/zh-hans/store/minecraft-java-bedrock-edition-pc";
    private const string GitHubRepoUrl = "https://github.com/adorable0127/xztx127-craft-launcher2";
    private const string DonateUrl = "https://ifdian.net/a/xztx127";

    // 滚动同步 Tab 选中态时，用这个标记避免"代码触发的 RadioButton.Checked"
    // 反过来又调用一次滚动定位，造成两边来回打架、抖动。
    private bool _suppressScrollSync;

    private readonly MainWindow _owner;

    public AboutHelpPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        BiliFansList.ItemsSource = BiliFanNames;
    }

    /// <summary>
    /// 哔哩哔哩粉丝墙用户名列表——来自截图整理，按截图里出现的顺序排列，
    /// 排名不分先后，纯展示、不带任何跳转（用户名本身不一定对应可点击的稳定链接）。
    /// </summary>
    private static readonly string[] BiliFanNames =
    {
        "MEiucV", "0090867755", "SONG2013",
        "盖比特斯", "蜂条", "杨玺么么么",
        "Grosgrain_mx", "Bellachenfang", "bili_31970594677",
        "陈玩乐高", "喵喵碧姬公主", "bili_3706994055711652",
        "bili_97571116538", "bili_32985147256", "动情交欢",
        "bili_35853316190", "星玄已出院", "bili_23723056057",
        "节奏盒子Dave-改名成功", "bili_21598593536",
    };

    // ===================== 顶部 Tab ⇄ 滚动位置 双向联动 =====================

    private void SubNavHelp_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 里 SubNavHelp 的 IsChecked="True" 是默认值，InitializeComponent()
        // 解析到这一行时就会立刻触发本 Checked 事件——但此时 MainScroll 这个
        // 具名字段还没被赋值（它在 XAML 里排在后面），直接访问会 NullReferenceException。
        // 所以这里要判空，等真正加载完成后再响应。
        if (_suppressScrollSync || MainScroll is null) return;
        MainScroll.ScrollToTop();
    }

    private void SubNavThanks_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressScrollSync) return;
        // 同样：InitializeComponent 阶段 MainScroll / SectionDivider 可能还没就绪。
        if (MainScroll?.Content is not UIElement content || SectionDivider is null) return;
        // 用分割线相对于滚动内容顶端的位置定位，而不是简单 ScrollToBottom——
        // 这样"鸣谢"板块本身如果比一屏还长，点进来第一眼看到的是鸣谢板块的开头
        // （作者信息卡片），不会因为内容变长而一下子跳到最底部。
        var offset = SectionDivider.TranslatePoint(new Point(0, 0), content).Y;
        MainScroll.ScrollToVerticalOffset(offset);
    }

    /// <summary>
    /// 往下滑动越过分割线时，自动把顶部 Tab 的选中态从「帮助」切到「鸣谢」，反之亦然。
    /// 需求原话："默认停留在帮助页面，往下滑才可以到鸣谢页面"——用同一个 ScrollViewer
    /// 承载两块内容正好天然满足"往下滑"这个交互，这里只是让顶部 Tab 的视觉状态跟手。
    /// </summary>
    private void MainScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (SectionDivider is null || MainScroll.Content is not UIElement content) return;

        var dividerOffset = SectionDivider.TranslatePoint(new Point(0, 0), content).Y;

        // 阈值：滚动位置越过"分割线上边缘再往上一点点"就判定为进入鸣谢板块，
        // 避免恰好停在分割线正中间时来回反复横跳。
        var isInThanks = MainScroll.VerticalOffset >= dividerOffset - 40;

        _suppressScrollSync = true;
        try
        {
            if (isInThanks && SubNavThanks.IsChecked != true) SubNavThanks.IsChecked = true;
            else if (!isInThanks && SubNavHelp.IsChecked != true) SubNavHelp.IsChecked = true;
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    // ===================== 帮助板块：跳转按钮 =====================

    private void OpenMicrosoftLogin_Click(object sender, RoutedEventArgs e) => OpenUrl(MicrosoftLoginUrl);

    private void OpenMinecraftBuy_Click(object sender, RoutedEventArgs e) => OpenUrl(MinecraftBuyUrl);

    private void OpenLogsPage_Click(object sender, RoutedEventArgs e)
    {
        _owner.NavigateToLogs();
    }

    private void OpenIssue_Click(object sender, RoutedEventArgs e) => OpenUrl($"{GitHubRepoUrl}/issues/new");

    // ===================== 鸣谢板块：作者 / 赞助 / 仓库 =====================

    private void OpenDonate_Click(object sender, RoutedEventArgs e) => OpenUrl(DonateUrl);

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubRepoUrl);

    private void OpenRelease_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubRepoUrl);

    /// <summary>
    /// 统一的"用系统默认浏览器打开链接"入口，跟项目里其它地方（VersionSelectPage 等）
    /// 的 Process.Start(UseShellExecute=true) 写法保持一致，失败时弹出提示而不是静默吞掉。
    /// </summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowWarning("打开浏览器失败：\n" + ex.Message, "错误");
        }
    }
}
