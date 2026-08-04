using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// "实验性功能"面板：换肤（白/蓝/黄/紫/粉，各自浅色版/深色版）已经全部做完，
/// 搬到了正式的「设置」页配色皮肤下拉框 + 首页「模式设置」/「自动循环」按钮，
/// 不再放在这个实验性面板里（见 ExperimentalFeaturesWindow.xaml 顶部注释）。
/// 现在这里只剩"初步测试版功能"这一层，比正式功能更不成熟，需要单独的强烈风险
/// 提示 + token 校验才能进入。
///
/// 只有先通过 ExperimentalGateWindow 那次强制 10 秒等待确认之后，调用方（SettingsPage）
/// 才会打开这个窗口——这里不重复校验 AppConfig.ExperimentalFeaturesUnlocked，信任调用方
/// 已经做过这个检查（同一个模式在这个项目里很常见，比如各个 XxxWindow 都不重复校验
/// 调用者是否已经登录/已经选好版本，这类前置条件由外层页面负责）。
/// </summary>
public partial class ExperimentalFeaturesWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;

    public ExperimentalFeaturesWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(null);
    }

    /// <summary>
    /// "启动基岩版"：检测系统是否已安装 Minecraft for Windows，已安装就直接唤起；
    /// 没检测到就提示用户去应用商店安装，不在这里帮用户跳转商店页面——商店页面链接/商品 ID
    /// 这类信息比包名本身更容易过时（商店链接、商品上下架都由 Microsoft/Mojang 单方面控制），
    /// 写死一个链接反而可能几个月后失效，不如让用户自己在开始菜单/商店里搜"Minecraft"。
    /// </summary>
    private async void LaunchBedrockFeature_Click(object sender, RoutedEventArgs e)
    {
        var installed = await BedrockLaunchService.IsInstalledAsync();
        if (!installed)
        {
            MessageBoxDialog.ShowInfo(
                "没有检测到已安装的「Minecraft for Windows」（基岩版）。\n\n" +
                "这是完全独立于 Java 版的另一个游戏（不同引擎、不同 Mod 生态），需要先在 Microsoft Store 里搜索" +
                "「Minecraft」单独安装，本启动器不提供下载。", Loc.T("Str_Cs_Bedrock_Edition_Not_Detected", "未检测到基岩版"));
            return;
        }

        BedrockLaunchService.Launch();
    }

    /// <summary>
    /// "初步测试版功能"下唯一的子功能入口：多加载器合装。打开专门的
    /// MultiLoaderInstallWindow，那边有自己独立的风险提示 + token 校验，这里不重复处理。
    /// </summary>
    private void MultiLoaderFeature_Click(object sender, RoutedEventArgs e)
    {
        var window = new MultiLoaderInstallWindow(_owner) ;
        var result = window.ShowDialog();

        // 修复："一锅乱炖"装完之后启动器找不到刚装好的版本，根本原因是这里以前从不
        // 处理 ShowDialog() 的返回值——版本文件确实已经装进 versions/ 目录了，
        // 只是没人告诉版本选择页"该重新扫一遍了"。现在只要 MultiLoaderInstallWindow
        // 报告"至少装成功了一个"，就顺手刷新一下版本页（如果它正开着的话）。
        if (result == true && window.InstalledVersionIds.Count > 0)
        {
            _owner.RefreshVersionsPageIfActive();
        }
    }
}
