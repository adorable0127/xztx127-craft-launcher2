using System.Windows;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private void ShowFeatureSearchCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
        // Preview immediately; the normal save/discard flow owns persistence.
        _owner.ApplyFeatureSearchVisibility(ShowFeatureSearchCheck.IsChecked == true);
    }
}
