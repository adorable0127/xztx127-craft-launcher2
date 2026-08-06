using System.IO;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 拖入一个内容特征不明确的 .zip 时，问用户"这是整合包还是资源包"。
///
/// 为什么需要这个：材质包 / 光影包 / 数据包 / 存档 / 整合包**全都是 .zip**，
/// 扩展名完全一样。DragDropInstallService.ClassifyZip 会先开包看结构
/// （找 pack.mcmeta / level.dat / shaders/ / modrinth.index.json 这些强制文件），
/// 绝大多数包都能自动认出来；只有那些结构不标准、认不出的，才会走到这个弹窗。
///
/// 用户可以勾"记住这个选择"，写进 AppConfig.ZipDropDefault，以后同类情况直接按这个走，
/// 也可以随时在「设置 - 拖拽安装」里改回"每次询问"。
/// </summary>
public partial class DropTypeChoiceDialog : OverlayDialogControl
{
    /// <summary>用户选择的类型。仅在对话框返回 true 时有意义。</summary>
    public DragDropInstallService.DropKind SelectedKind { get; private set; } =
        DragDropInstallService.DropKind.Modpack;

    /// <summary>用户是否勾了"记住这个选择"。调用方据此决定要不要写回配置。</summary>
    public bool Remember => RememberCheck.IsChecked == true;

    public DropTypeChoiceDialog(string filePath, DragDropInstallService.DropKind preselect)
    {
        InitializeComponent();

        FileNameText.Text = Path.GetFileName(filePath);

        // 预选：如果配置里已经有默认值（不是 Ask），就把那一项先选上，
        // 用户直接回车即可，不用每次都重新点一遍。
        switch (preselect)
        {
            case DragDropInstallService.DropKind.ResourcePack:
                ChoiceResourcePack.IsChecked = true;
                break;
            case DragDropInstallService.DropKind.ShaderPack:
                ChoiceShaderPack.IsChecked = true;
                break;
            case DragDropInstallService.DropKind.World:
                ChoiceWorld.IsChecked = true;
                break;
            default:
                ChoiceModpack.IsChecked = true;
                break;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedKind =
            ChoiceResourcePack.IsChecked == true ? DragDropInstallService.DropKind.ResourcePack :
            ChoiceShaderPack.IsChecked == true ? DragDropInstallService.DropKind.ShaderPack :
            ChoiceWorld.IsChecked == true ? DragDropInstallService.DropKind.World :
            DragDropInstallService.DropKind.Modpack;

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);
}
