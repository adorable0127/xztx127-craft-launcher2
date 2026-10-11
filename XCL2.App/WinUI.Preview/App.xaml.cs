using Microsoft.UI.Xaml;
namespace XCL2.WinUI.Preview;
public partial class App : Application
{
    private MainWindow? _window;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(commandLine, "--pipe");
        _window = new MainWindow(index >= 0 && index + 1 < commandLine.Length ? commandLine[index + 1] : null);
        _window.Activate();
    }
}
