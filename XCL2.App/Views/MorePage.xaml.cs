using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

/// <summary>「更多」磁贴页：简洁模式开启后，从侧边栏收起的那九个导航按钮（联机大厅、
/// Mod 管理、服务器管理、百宝箱、基岩版启动、鸣谢与帮助、日志、AI 助手、实验性功能）
/// 在这里重新以磁贴形式摆出来，避免用户找不到入口。每个磁贴点击直接转发到 MainWindow
/// 已有的 NavigateToXxx() / OpenExperimentalFeatures()，不重复实现导航逻辑，
/// 参考 HomePage.xaml.cs 里 TileLaunch_Click 等现有写法。</summary>
public partial class MorePage : UserControl
{
    private readonly MainWindow _owner;

    public MorePage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
    }

    private void MultiplayerTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToMultiplayer();

    private void ModManagerTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToModManager();

    private void ServerManagerTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToServerManager();

    private void ToolboxTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToToolbox();

    private void BedrockTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToBedrock();

    private void AboutHelpTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToAboutHelp();

    private void LogsTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToLogs();

    private void AiAssistantTile_Click(object sender, RoutedEventArgs e) => _owner.NavigateToAiAssistant();

    // 「实验性功能」是独立弹窗（OpenExperimentalFeatures），不是 NavigateToXxx 页面跳转——
    // 该方法本来就是 public（见 MainWindow.xaml.cs OpenExperimentalFeatures 类头注释），
    // 不需要额外包装。
    private void ExperimentalTile_Click(object sender, RoutedEventArgs e) => _owner.OpenExperimentalFeatures();
}
