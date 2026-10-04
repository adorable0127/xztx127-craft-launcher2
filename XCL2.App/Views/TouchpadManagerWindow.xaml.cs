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
    public string DetailLine => $"账户：{Info.AccountLabel}  |  PID {Info.Pid}  |  启动于 {Info.StartedAt:HH:mm:ss}  |  {Info.TouchInputRoute}";
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
        State = status.State;
        ErrorText = status.Error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLine)));
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
    {
        var previousEffect = Effect;
        try
        {
            // 主界面的磨砂保持；打开按键设置时，后面的触摸板管理面板也磨砂。
            SetCurrentValue(EffectProperty, new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 12,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            });
            var settings = new TouchControlsSettingsWindow();
            var owner = Window.GetWindow(this);
            if (owner != null)
            {
                settings.Owner = owner;
                settings.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            settings.ShowDialog();
        }
        finally { SetCurrentValue(EffectProperty, previousEffect); }
    }

    private readonly AppConfig _config;
    private readonly ObservableCollection<TouchpadProcessRow> _rows = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _subscribed;
    private bool _discovering;

    private async void DiscoverExternalGames()
    {
        if (_discovering || !_subscribed) return;
        _discovering = true;
        try
        {
            await _manager.RefreshAsync();
            if (_subscribed) RefreshInstances();
        }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail($"[发现外部游戏窗口] {ex}"); }
        finally { _discovering = false; }
    }

    public TouchpadManagerWindow(GameProcessManager manager, AppConfig config)
    {
        _manager = manager;
        _config = config;
        InitializeComponent();
        InstanceListBox.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _refreshTimer.Tick += (_, _) => { RefreshInstances(); DiscoverExternalGames(); };
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
        DiscoverExternalGames();
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
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshInstances));
            return;
        }
        // 与关闭游戏和内存预警使用同一份进程列表；发现实例不会启用触屏。
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
        DisableButton.IsEnabled = running && row!.Info.TouchModeEnabled;
        DisableButton.Content = row?.State == TouchOverlayState.Starting ? "取消开启" : "关闭触摸板";
    }

    private void InstanceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshActions();

    private void EnableButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceListBox.SelectedItem is TouchpadProcessRow row && !row.Info.HasExited)
            TouchOverlayService.Enable(row.Info, _config);
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
