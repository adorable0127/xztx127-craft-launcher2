using System;
using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views;

public partial class AiFloatingButton : UserControl
{
    public event EventHandler? Clicked;

    public AiFloatingButton()
    {
        InitializeComponent();
    }

    private void MainButton_Click(object sender, RoutedEventArgs e) => Clicked?.Invoke(this, EventArgs.Empty);

    public void ShowBadge(bool show) => Badge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
}
