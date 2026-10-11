using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class DriverDownloadLinks : UserControl
{
    public DriverDownloadLinks() => InitializeComponent();
    private void OpenLink_Click(object sender, RoutedEventArgs e)
        => ExternalLinkService.Open((sender as Button)?.Tag as string);
    private void OpenCustom_Click(object sender, RoutedEventArgs e)
        => ExternalLinkService.Open(CustomUrl.Text.Trim());
}
