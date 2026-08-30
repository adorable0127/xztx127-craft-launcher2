using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 见 SingleInstanceService 类头注释——这个窗口只负责"问用户四选一"，具体怎么处理
/// （给旧实例发管道指令/直接退出）都交给调用方（App.xaml.cs RunStartupSequence）根据
/// <see cref="Result"/> 来做，本类不直接触碰管道逻辑。
/// </summary>
public partial class InstanceConflictDialog : Window
{
    /// <summary>
    /// 默认值是"关闭此实例"：如果用户没有点任何按钮就把窗口关掉了（比如 Alt+F4），
    /// 选一个最保守、最不会有副作用的默认行为——只退出这个新打开的实例，不去动原实例。
    /// </summary>
    public SingleInstanceService.ConflictChoice Result { get; private set; } =
        SingleInstanceService.ConflictChoice.CloseNewInstance;

    public InstanceConflictDialog()
    {
        InitializeComponent();
    }

    private void KeepBothRunning_Click(object sender, RoutedEventArgs e)
    {
        Result = SingleInstanceService.ConflictChoice.KeepBothRunning;
        Close();
    }

    private void CloseNewInstance_Click(object sender, RoutedEventArgs e)
    {
        Result = SingleInstanceService.ConflictChoice.CloseNewInstance;
        Close();
    }

    private void CloseNewAndActivateOld_Click(object sender, RoutedEventArgs e)
    {
        Result = SingleInstanceService.ConflictChoice.CloseNewAndActivateOld;
        Close();
    }

    private void CloseOldKeepNewInstance_Click(object sender, RoutedEventArgs e)
    {
        Result = SingleInstanceService.ConflictChoice.CloseOldKeepNewInstance;
        Close();
    }
}
