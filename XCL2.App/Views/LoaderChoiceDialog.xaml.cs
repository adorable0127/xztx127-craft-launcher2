using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// "点击某个版本的下载安装按钮"之后弹出的选择窗：原版 / Fabric / Forge / NeoForge / Quilt
/// 五选一，另外还有 OptiFine / LiteLoader / Cleanroom / LabyMod 四个"非主流"选项。
///
/// 背景：之前"下载中心"要求用户先在顶部切换一整排"加载器筛选"单选按钮，再回到列表里点某一行的
/// "下载安装"（此时按钮文案会变成"安装 xxx"），两步分离、容易搞混，而且一次只能锁定一种加载器——
/// 如果用户想给两个不同版本分别装 Fabric 和 Forge，需要来回切换顶部筛选。
///
/// 现在改成"点哪个版本，就在这个版本上问一次装什么"，跟 PCL/HMCL 等主流启动器的下载体验一致，
/// 不需要预先做任何全局筛选，每次点击都是一次独立、完整的选择。
///
/// 迁移记录：原来是独立 Window（LoaderChoiceWindow），现在改成挂在 MainWindow
/// Overlay 层里的 UserControl（继承 OverlayDialogControl，见 IOverlayDialog.cs）。
/// 原来"DialogResult = true; Close();"两行，现在统一改成调用基类的
/// CloseWith(true/false)。
///
/// ===== 不支持的加载器置灰 =====
/// 老版本 MC 没有 Fabric/NeoForge/Quilt 中的大部分选项，新版本又早已没有 LiteLoader/Cleanroom
/// 这类老加载器的构建。四个"非主流"选项（OptiFine/LiteLoader/Cleanroom/LabyMod）打开对话框时
/// 先统一置灰不可点，构造函数里异步调用 LoaderAvailabilityService 探测一次，探测到真的支持
/// 当前这个 MC 版本的才会被打开成正常黑字可点；探测不到、探测失败、或者官方本来就没有这个版本
/// 的构建，都保持灰色——避免用户选中一个实际装不了的选项，白填一遍表单才在后面的安装步骤收到报错。
/// 五个主流选项（原版/Fabric/Forge/NeoForge/Quilt）本来就有各自完善的"选了不支持的组合"处理
/// （见 ClientLoaderInstallService 里 ResolveLoaderVersionOrAutoMatchAsync 等注释），这里不改动
/// 它们的既有交互，只对四个新加的"非主流"选项做置灰。
/// </summary>
public partial class LoaderChoiceDialog : OverlayDialogControl
{
    /// <summary>用户确认选择的加载器类型；取消返回 null。</summary>
    public ServerCoreType SelectedLoader { get; private set; } = ServerCoreType.Vanilla;

    private readonly string _mcVersionId;
    private readonly ClientLoaderInstallService _probeService;

    public LoaderChoiceDialog(string mcVersionId)
    {
        InitializeComponent();
        _mcVersionId = mcVersionId;
        TitleText.Text = $"安装 {mcVersionId}";

        // 只是用来探测"支持不支持"，不需要跟着用户的下载源/多线程设置走，用最简单的默认构造即可。
        _probeService = new ClientLoaderInstallService(DownloadSource.Official);
        RequestClose += (_, _) => _probeService.Dispose();

        _ = LoadAvailabilityAsync();
    }

    private async Task LoadAvailabilityAsync()
    {
        Dictionary<ServerCoreType, bool> availability;
        try
        {
            availability = await LoaderAvailabilityService.GetAvailabilityAsync(_probeService, _mcVersionId);
        }
        catch
        {
            // 探测本身整体失败（理论上不会，GetAvailabilityAsync 内部每一项都已经吞过异常了，
            // 这里只是双重保险）：保持四个非主流选项原样置灰，不影响主流五项的正常使用。
            return;
        }

        ApplyAvailability(OptOptiFine, ServerCoreType.OptiFine, availability,
            "该版本没有查到 OptiFine 构建（可能是官方没有发布，或暂时无法访问数据源）。");
        ApplyAvailability(OptLiteLoader, ServerCoreType.LiteLoader, availability,
            "该版本没有 LiteLoader 构建（LiteLoader 早已停止更新，只覆盖较老的版本）。");
        ApplyAvailability(OptCleanroom, ServerCoreType.Cleanroom, availability,
            "Cleanroom 目前只支持 Minecraft 1.12.2。");
        ApplyAvailability(OptLabyMod, ServerCoreType.LabyMod, availability,
            "LabyMod 暂不支持在这里自动安装，请前往 LabyMod 官网手动下载。");
    }

    private void ApplyAvailability(RadioButton button, ServerCoreType type,
        Dictionary<ServerCoreType, bool> availability, string unsupportedHint)
    {
        var supported = availability.TryGetValue(type, out var ok) && ok;
        button.IsEnabled = supported;
        button.ToolTip = supported ? null : unsupportedHint;
        // 万一这个选项正好是之前某次探测完打开、用户选中了，但这次重新探测发现不再支持
        // （理论上不会发生，因为每次都是新开对话框），保险起见置灰的同时也取消选中。
        if (!supported && button.IsChecked == true)
        {
            button.IsChecked = false;
            OptVanilla.IsChecked = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedLoader = OptVanilla.IsChecked == true ? ServerCoreType.Vanilla
            : OptFabric.IsChecked == true ? ServerCoreType.Fabric
            : OptForge.IsChecked == true ? ServerCoreType.Forge
            : OptNeoForge.IsChecked == true ? ServerCoreType.NeoForge
            : OptQuilt.IsChecked == true ? ServerCoreType.Quilt
            : OptOptiFine.IsChecked == true ? ServerCoreType.OptiFine
            : OptLiteLoader.IsChecked == true ? ServerCoreType.LiteLoader
            : OptCleanroom.IsChecked == true ? ServerCoreType.Cleanroom
            : OptLabyMod.IsChecked == true ? ServerCoreType.LabyMod
            : ServerCoreType.Vanilla;

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
