using System.Windows;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel
{
    private bool _confirmingWebSearch;

    private void EnableWebSearchCheck_Click(object sender, RoutedEventArgs e)
    {
        // 载入、导入或恢复配置仍走 LoadConfig；程序赋值不会触发 Click。
        if (_loading || EnableWebSearchCheck.IsChecked != true) return;
        if (_confirmingWebSearch)
        {
            EnableWebSearchCheck.IsChecked = false;
            return;
        }

        _confirmingWebSearch = true;
        try
        {
            EnableWebSearchCheck.IsChecked = AiWebSearchConfirmation.ConfirmEnable();
        }
        finally { _confirmingWebSearch = false; }
    }
}
