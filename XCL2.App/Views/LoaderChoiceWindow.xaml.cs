using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>
/// "点击某个版本的下载安装按钮"之后弹出的选择窗：原版 / Fabric / Forge / NeoForge 四选一。
///
/// 背景：之前"下载中心"要求用户先在顶部切换一整排"加载器筛选"单选按钮，再回到列表里点某一行的
/// "下载安装"（此时按钮文案会变成"安装 xxx"），两步分离、容易搞混，而且一次只能锁定一种加载器——
/// 如果用户想给两个不同版本分别装 Fabric 和 Forge，需要来回切换顶部筛选。
///
/// 现在改成"点哪个版本，就在这个版本上问一次装什么"，跟 PCL/HMCL 等主流启动器的下载体验一致，
/// 不需要预先做任何全局筛选，每次点击都是一次独立、完整的选择。
/// </summary>
public partial class LoaderChoiceWindow : Window
{
    /// <summary>用户确认选择的加载器类型；取消返回 null。</summary>
    public ServerCoreType SelectedLoader { get; private set; } = ServerCoreType.Vanilla;

    public LoaderChoiceWindow(string mcVersionId)
    {
        InitializeComponent();
        TitleText.Text = $"安装 {mcVersionId}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedLoader = OptVanilla.IsChecked == true ? ServerCoreType.Vanilla
            : OptFabric.IsChecked == true ? ServerCoreType.Fabric
            : OptForge.IsChecked == true ? ServerCoreType.Forge
            : ServerCoreType.NeoForge;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
