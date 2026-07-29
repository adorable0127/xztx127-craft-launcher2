using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ProgressWindow : Window
{
    public IProgress<ProgressInfo> Progress { get; }

    public ProgressWindow(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
        Progress = new System.Progress<ProgressInfo>(info =>
        {
            Dispatcher.Invoke(() =>
            {
                var pct = info.Total > 0 ? (double)info.Done / info.Total * 100 : 0;
                Bar.Value = Math.Min(100, pct);
                DetailText.Text = $"{info.Stage}: {info.Done}/{info.Total}  {info.CurrentFile}";
            });
        });
    }
}
