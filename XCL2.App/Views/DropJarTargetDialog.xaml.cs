using System.IO;
using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>
/// 拖入 .jar 时问"装给客户端还是服务端"。
///
/// 只有当设置里对应的默认值是 <see cref="DropJarTarget.Ask"/> 时才会弹出来。
/// 默认情况下不会弹：在「服务端管理」页拖 jar 直接装给服务器，在其它页面直接装进
/// 当前选中的客户端实例（这两个默认值分别是 AppConfig.ServerPageJarDropTarget 和
/// AppConfig.DefaultJarDropTarget，都能在「设置 - 拖拽安装」里改）。
/// </summary>
public partial class DropJarTargetDialog : OverlayDialogControl
{
    public DropJarTarget SelectedTarget { get; private set; } = DropJarTarget.CurrentInstanceMods;

    public bool Remember => RememberCheck.IsChecked == true;

    /// <param name="onServerPage">当前是不是在「服务端管理」页。只影响预选项和提示文案，
    /// 让弹窗跟用户所处的上下文一致，不用每次都从头读一遍两个选项。</param>
    public DropJarTargetDialog(string filePath, bool onServerPage)
    {
        InitializeComponent();

        FileNameText.Text = Path.GetFileName(filePath);
        ClientHint.Text = "放进当前选中实例的 mods 文件夹，下次启动游戏时生效";
        ServerHint.Text = "放进服务器实例的 mods 文件夹（服务端 Mod）";

        if (onServerPage) ChoiceServer.IsChecked = true;
        else ChoiceClient.IsChecked = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedTarget = ChoiceServer.IsChecked == true
            ? DropJarTarget.Server
            : DropJarTarget.CurrentInstanceMods;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);
}
