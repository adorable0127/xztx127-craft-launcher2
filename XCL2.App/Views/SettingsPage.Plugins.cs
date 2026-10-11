using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    /// <summary>
    /// 插件设置区同时承担“插件管理入口”和“插件贡献项宿主”。
    /// 这部分不能只显示插件自己注册的设置，否则 PluginManagerDialog 虽然存在，
    /// 用户却没有一个不受实验功能门控影响的稳定入口。
    /// </summary>
    private void InitializePluginSettings()
    {
        var area = new StackPanel { Margin = new Thickness(0) };
        PluginSettingsHost.Children.Add(area);

        area.Children.Add(new TextBlock
        {
            Text = "第三方插件",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        area.Children.Add(new TextBlock
        {
            Text = "管理 DLL / EXE 插件、启动策略、回调超时，以及插件注册到设置页的扩展项。插件管理属于基础功能，不受实验功能或简洁模式限制。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var openManager = new Button { Content = "插件管理…", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8) };
        var rescan = new Button { Content = "重新扫描 / 重新加载", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8) };
        var openDirectory = new Button { Content = "打开插件目录", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8) };
        var apiCenter = new Button { Content = "API 开发中心 / 样例", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8) };
        apiCenter.Click += (_, _) => _owner.NavigateToApiDocumentation();
        toolbar.Children.Add(openManager);
        toolbar.Children.Add(rescan);
        toolbar.Children.Add(openDirectory);
        toolbar.Children.Add(apiCenter);
        area.Children.Add(toolbar);

        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        area.Children.Add(status);

        var policyRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        policyRow.Children.Add(new TextBlock { Text = "插件启动策略", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var policy = new ComboBox { Width = 250, HorizontalAlignment = HorizontalAlignment.Left };
        policy.Items.Add(new ComboBoxItem { Content = "每次启动器启动都运行已启用插件", Tag = PluginLaunchMode.EachLaunch });
        policy.Items.Add(new ComboBoxItem { Content = "仅导入后成功启动一次", Tag = PluginLaunchMode.OnceOnImport });
        policy.SelectedItem = policy.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is PluginLaunchMode mode && mode == _owner.ConfigService.Config.PluginLaunchMode) ?? policy.Items[0];
        policy.SelectionChanged += (_, _) =>
        {
            if ((policy.SelectedItem as ComboBoxItem)?.Tag is not PluginLaunchMode mode) return;
            _owner.ConfigService.Config.PluginLaunchMode = mode;
            _owner.Plugins.StartPolicy = mode;
            _owner.ConfigService.Save();
            UpdatePluginStatus();
        };
        policyRow.Children.Add(policy);
        area.Children.Add(policyRow);

        var timeoutRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        timeoutRow.Children.Add(new TextBlock { Text = "插件回调超时（秒）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var timeout = new TextBox
        {
            Width = 80,
            Padding = new Thickness(6),
            Text = _owner.ConfigService.Config.PluginCallbackTimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture)
        };
        var applyTimeout = new Button { Content = "应用", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
        var timeoutHint = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        applyTimeout.Click += (_, _) =>
        {
            if (!double.TryParse(timeout.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0.1 || seconds > 3600)
            {
                timeoutHint.Text = "请输入 0.1～3600 秒（最长 1 小时）。";
                return;
            }
            _owner.ConfigService.Config.PluginCallbackTimeoutSeconds = seconds;
            _owner.ConfigService.Save();
            timeout.Text = seconds.ToString("0.###", CultureInfo.InvariantCulture);
            timeoutHint.Text = "已保存";
        };
        timeoutRow.Children.Add(timeout);
        timeoutRow.Children.Add(applyTimeout);
        timeoutRow.Children.Add(timeoutHint);
        area.Children.Add(timeoutRow);
        area.Children.Add(new TextBlock { Text = "可为耗时任务设置最长 1 小时的回调等待；默认 3 秒。停止插件或取消任务仍会取消等待。此设置不改变 EXE 握手与网络请求的超时。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        area.Children.Add(new TextBlock
        {
            Text = Loc.T("Str_Plugin_CustomSettings", "插件设置与背景"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 4, 0, 4)
        });
        var contributionArea = new StackPanel();
        area.Children.Add(contributionArea);

        void UpdatePluginStatus()
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(UpdatePluginStatus)); return; }
            var all = _owner.Plugins.Plugins;
            var running = all.Count(p => p.Context != null || p.RunningProcess is { HasExited: false });
            var failed = all.Count(p => p.LoadError != null);
            var enabled = all.Count(p => p.Enabled);
            status.Text = $"已发现 {all.Count} 个插件，{enabled} 个启用，{running} 个正在运行" +
                          (failed > 0 ? $"，{failed} 个加载失败" : "") +
                          $"。目录：{_owner.Plugins.InstalledDirectory}";
        }

        void RefreshContributions()
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(RefreshContributions)); return; }
            contributionArea.Children.Clear();

            var backgrounds = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 12) };
            backgrounds.Items.Add(new ComboBoxItem { Content = Loc.T("Str_Plugin_BuiltInBackground", "使用启动器原背景"), Tag = "" });
            foreach (var entry in PluginUiContributionRegistry.Snapshot("background"))
                backgrounds.Items.Add(new ComboBoxItem { Content = entry.Context.Host.Safe(((PluginBackgroundDefinition)entry.Definition).Name), Tag = entry.Id });
            backgrounds.SelectedItem = backgrounds.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == _owner.ConfigService.Config.PluginBackgroundId) ?? backgrounds.Items[0];
            backgrounds.SelectionChanged += (_, _) =>
            {
                _owner.ConfigService.Config.PluginBackgroundId = (backgrounds.SelectedItem as ComboBoxItem)?.Tag as string;
                _owner.ConfigService.Save();
            };
            contributionArea.Children.Add(backgrounds);

            foreach (var entry in PluginUiContributionRegistry.Snapshot("setting").OrderBy(e => ((PluginOptionDefinition)e.Definition).Order).ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                var option = (PluginOptionDefinition)entry.Definition;
                var ui = entry.Context.Ui;
                var row = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
                row.Children.Add(new TextBlock { Text = entry.Context.Host.Safe(entry.Context.Metadata.Name + " · " + option.Title), FontWeight = FontWeights.SemiBold });
                var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
                void Save(object? value) { error.Text = ui.SaveOption(option, value, PluginPermissionNames.UiSettings) ?? ""; }
                var current = ui.ReadOption(option);
                switch (option.Kind)
                {
                    case PluginOptionKind.Toggle:
                        var toggle = new CheckBox { IsChecked = (bool)current };
                        toggle.Click += (_, _) => Save(toggle.IsChecked == true);
                        row.Children.Add(toggle);
                        break;
                    case PluginOptionKind.Choice:
                        var combo = new ComboBox { ItemsSource = option.Choices, DisplayMemberPath = nameof(PluginChoice.Label), SelectedValuePath = nameof(PluginChoice.Value), SelectedValue = current, MinWidth = 250, HorizontalAlignment = HorizontalAlignment.Left };
                        combo.SelectionChanged += (_, _) => Save(combo.SelectedValue as string);
                        row.Children.Add(combo);
                        break;
                    default:
                        var text = new TextBox { Text = Convert.ToString(current, CultureInfo.InvariantCulture) ?? "", MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
                        text.LostFocus += (_, _) =>
                        {
                            if (option.Kind == PluginOptionKind.Number)
                            {
                                if (double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) Save(number);
                                else error.Text = Loc.T("Str_Plugin_InvalidNumber", "请输入有效数字。");
                            }
                            else Save(text.Text);
                        };
                        row.Children.Add(text);
                        break;
                }
                row.Children.Add(error);
                contributionArea.Children.Add(row);
            }
        }

        openManager.Click += (_, _) =>
        {
            OverlayDialogService.ShowModal(new PluginManagerDialog(_owner));
            UpdatePluginStatus();
            RefreshContributions();
        };
        rescan.Click += (_, _) =>
        {
            try
            {
                _owner.Plugins.StartPolicy = _owner.ConfigService.Config.PluginLaunchMode;
                _owner.Plugins.ScanAndLoad();
                ToastService.ShowSuccess("插件已重新扫描并重新加载。");
            }
            catch (Exception ex)
            {
                MessageBoxDialog.ShowWarning("重新扫描插件失败：" + ex.Message, "插件管理");
            }
            UpdatePluginStatus();
            RefreshContributions();
        };
        openDirectory.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(_owner.Plugins.InstalledDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_owner.Plugins.InstalledDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBoxDialog.ShowWarning("打开插件目录失败：" + ex.Message, "插件管理"); }
        };

        Loaded += (_, _) =>
        {
            _owner.Plugins.Changed -= UpdatePluginStatus;
            _owner.Plugins.Changed += UpdatePluginStatus;
            PluginUiContributionRegistry.Changed -= RefreshContributions;
            PluginUiContributionRegistry.Changed += RefreshContributions;
            UpdatePluginStatus();
            RefreshContributions();
        };
        Unloaded += (_, _) =>
        {
            _owner.Plugins.Changed -= UpdatePluginStatus;
            PluginUiContributionRegistry.Changed -= RefreshContributions;
        };

        UpdatePluginStatus();
        RefreshContributions();
    }
}
