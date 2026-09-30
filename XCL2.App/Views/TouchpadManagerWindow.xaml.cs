using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class TouchpadProcessRow : INotifyPropertyChanged
{
    public GameProcessInfo Info { get; }
    public string TitleLine => Info.VersionId;
    public string DetailLine => $"账户：{Info.AccountLabel}  |  PID {Info.Pid}  |  启动于 {Info.StartedAt:HH:mm:ss}";
    public TouchOverlayState State { get; private set; }
    public string? ErrorText { get; private set; }
    public Visibility ErrorVisibility => string.IsNullOrEmpty(ErrorText) ? Visibility.Collapsed : Visibility.Visible;
    public string StatusText => State switch
    {
        TouchOverlayState.Starting => "正在开启…",
        TouchOverlayState.Enabled => "已开启",
        TouchOverlayState.Failed => "开启失败",
        _ => "未开启"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public TouchpadProcessRow(GameProcessInfo info)
    {
        Info = info;
        Refresh();
    }

    public void Refresh()
    {
        var status = TouchOverlayService.GetStatus(Info);
        if (State == status.State && ErrorText == status.Error) return;
        State = status.State;
        ErrorText = status.Error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorVisibility)));
    }
}

public partial class TouchpadManagerWindow : OverlayDialogControl
{
    private readonly GameProcessManager _manager;
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => new TouchControlsSettingsWindow().ShowDialog();

    private readonly AppConfig _config;
    private readonly ObservableCollection<TouchpadProcessRow> _rows = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _subscribed;

    public TouchpadManagerWindow(GameProcessManager manager, AppConfig config)
    {
        _manager = manager;
        _config = config;
        InitializeComponent();
        InstanceListBox.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _refreshTimer.Tick += (_, _) => RefreshInstances();
        RefreshInstances();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _manager.Changed += RefreshInstances;
            TouchOverlayService.Changed += RefreshInstances;
            _subscribed = true;
        }
        RefreshInstances();
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        if (!_subscribed) return;
        _manager.Changed -= RefreshInstances;
        TouchOverlayService.Changed -= RefreshInstances;
        _subscribed = false;
    }

    private void RefreshInstances()
    {
        var running = _manager.Running;
        var wasEmpty = _rows.Count == 0;
        foreach (var row in _rows.Where(row => !running.Contains(row.Info)).ToList())
            _rows.Remove(row);
        foreach (var info in running)
        {
            var row = _rows.FirstOrDefault(item => ReferenceEquals(item.Info, info));
            if (row == null) _rows.Add(new TouchpadProcessRow(info));
            else row.Refresh();
        }

        // 保留现有行对象与选中项，定时刷新不会打断选择或重置滚动位置。
        if (wasEmpty && _rows.Count == 1) InstanceListBox.SelectedIndex = 0;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var enabled = _rows.Count(row => row.State == TouchOverlayState.Enabled);
        var starting = _rows.Count(row => row.State == TouchOverlayState.Starting);
        SummaryText.Text = $"运行中：{_rows.Count} 个实例  ·  触摸板已开启：{enabled} 个" +
                           (starting > 0 ? $"  ·  正在开启：{starting} 个" : "");
        RefreshActions();
    }

    private void RefreshActions()
    {
        var row = InstanceListBox.SelectedItem as TouchpadProcessRow;
        var running = row != null && !row.Info.HasExited;
        EnableButton.IsEnabled = running &&
            (row!.State is TouchOverlayState.Disabled or TouchOverlayState.Failed);
        DisableButton.IsEnabled = running &&
            (row!.State is TouchOverlayState.Starting or TouchOverlayState.Enabled);
        DisableButton.Content = row?.State == TouchOverlayState.Starting ? "取消开启" : "关闭触摸板";
    }

    private void InstanceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshActions();

    private void EnableButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceListBox.SelectedItem is TouchpadProcessRow row && !row.Info.HasExited)
            TouchOverlayService.Attach(row.Info, _config);
        RefreshInstances();
    }

    private void DisableButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceListBox.SelectedItem is TouchpadProcessRow row)
            TouchOverlayService.Detach(row.Info);
        RefreshInstances();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => CloseWith(null);
}
