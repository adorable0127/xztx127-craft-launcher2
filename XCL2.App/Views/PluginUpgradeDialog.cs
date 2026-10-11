using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

internal sealed class PluginUpgradeDialog : OverlayDialogControl
{
    private readonly PluginManager _manager;
    private readonly LoadedPluginInfo _plugin;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _report = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 190 };
    private readonly CheckBox _consent = new() { Content = "我已知悉升级风险、备份范围和本地编译行为", Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _prepare = new() { Content = "备份并生成升级包", Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _install = new() { Content = "确认安装此升级包", IsEnabled = false, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _sourceButton = new() { Content = "导入源码（.csproj / .zip，可选）", Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _sourceLabel = new() { Text = "使用已安装插件二进制", TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _stop = new();
    private PluginUpgradeResult? _result;
    private string? _source;
    private bool _busy;
    internal PluginUpgradeDialog(PluginManager manager, LoadedPluginInfo plugin)
    {
        _manager = manager; _plugin = plugin; Width = 680; MaxHeight = 720;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "🪖 安全哨兵 · 插件升级", FontSize = 23, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(32, 255, 164, 48)), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = new TextBlock
        {
            Text = "升级可能损坏插件、配置或其他文件。启动器会先暂停插件，自动备份并校验全部插件安装文件、配置、数据和启用状态。备份不涵盖游戏存档或任意外部文件。\n\n旧二进制迁移只更新清单与签名，不保证接口自动兼容。新版包使用本机 ECDSA 自签名，仅用于完整性检查，不冒充原作者签名。\n\n导入源码后将在临时副本中运行 dotnet build。MSBuild 目标可以执行代码，依赖恢复可能联网；仅导入可信源码。源码不会被上传给远程 AI。", TextWrapping = TextWrapping.Wrap
        } });
        panel.Children.Add(_sourceButton); panel.Children.Add(_sourceLabel);
        _sourceButton.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "源码项目 / ZIP|*.csproj;*.zip", Title = "选择插件源码（ZIP 中须只有一个入口 csproj）" }; if (picker.ShowDialog() == true) { _source = picker.FileName; _sourceLabel.Text = _source; _result = null; _install.IsEnabled = false; } };
        panel.Children.Add(_consent);
        var actions = new WrapPanel(); actions.Children.Add(_prepare); actions.Children.Add(_install);
        var close = new Button { Content = "关闭 / 停止", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        close.Click += (_, _) => { if (_busy) { _stop.Cancel(); _status.Text = "已请求停止，请等待编译进程退出。"; } else CloseWith(null); };
        actions.Children.Add(close); panel.Children.Add(actions); panel.Children.Add(_status); panel.Children.Add(_report);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _prepare.Click += async (_, _) => await PrepareAsync(); _install.Click += (_, _) => Install();
        Unloaded += (_, _) => _stop.Cancel();
    }
    private async Task PrepareAsync()
    {
        if (_busy || _stop.IsCancellationRequested) return;
        if (_consent.IsChecked != true) { _status.Text = "请先阅读并勾选升级说明。"; return; }
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "XCL2 插件包|*.xclplugin", FileName = Path.GetFileNameWithoutExtension(_plugin.FilePath) + "-api2.xclplugin", OverwritePrompt = true };
        if (picker.ShowDialog() != true) return;
        if (File.Exists(picker.FileName)) { _status.Text = "请选择尚不存在的新文件名，避免覆盖之前的升级包。"; return; }
        _busy = true; _prepare.IsEnabled = _sourceButton.IsEnabled = _install.IsEnabled = false; _result = null;
        try
        {
            _status.Text = "正在暂停插件并创建备份…";
            var backup = await _manager.BackupBeforeUpgradeAsync(_stop.Token);
            _report.Text = "备份已校验：" + backup;
            _result = await Task.Run(() => PluginUpgradeService.PrepareAsync(_plugin, _source, picker.FileName,
                new Progress<string>(message => Dispatcher.BeginInvoke(new Action(() => _status.Text = message))), _stop.Token));
            _report.Text = "备份：" + backup + "\n\n" + _result.Report + "\n签名公钥 SHA-256：" + _result.Fingerprint + "\n升级包：" + _result.Package;
            _install.IsEnabled = true;
        }
        catch (OperationCanceledException) { _status.Text = "已停止。已完成的备份保留；没有安装新插件。"; }
        catch (Exception ex) { _status.Text = "升级准备失败，原插件未替换：" + ex.Message; }
        finally
        {
            _busy = false; _prepare.IsEnabled = _sourceButton.IsEnabled = !_stop.IsCancellationRequested;
            try { _manager.ScanAndLoad(); } catch (Exception ex) { _status.Text += "\n恢复插件运行失败：" + ex.Message; }
        }
    }
    private void Install()
    {
        if (_result == null || _busy) return;
        if (!MessageBoxDialog.ShowConfirm("请核对上方迁移报告。\n\n安装会替换当前选择的插件，并再次备份当前插件文件。新插件保持禁用，检查权限后再手动启用。升级仍可能有兼容性问题。\n\n包：" + _result.Package, "🪖 确认升级")) return;
        try
        {
            var installed = _manager.InstallUpgrade(_result.Package, _plugin.FilePath);
            _manager.SetEnabled(installed, false); _install.IsEnabled = false;
            _status.Text = "升级包已安装，当前保持禁用。备份：" + _manager.LastBackupPath;
        }
        catch (Exception ex) { _status.Text = "安装失败：" + ex.Message; }
        finally { try { _manager.ScanAndLoad(); } catch (Exception ex) { _status.Text += "\n扫描失败：" + ex.Message; } }
    }
}
