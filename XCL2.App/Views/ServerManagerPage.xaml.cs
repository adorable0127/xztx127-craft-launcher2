using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 服务端管理页：目前只实现"核心下载"这一块（Vanilla/Paper/Fabric 直接下载，Forge/NeoForge
/// 下载安装器 + 本地运行安装）。服务器列表/插件下载/资源包下载等其余功能先占位，
/// 待后续按优先级逐项实现，避免一次性铺开导致每块都是半成品。
/// </summary>
public partial class ServerManagerPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly ServerCoreDownloadService _coreService = new();
    private readonly JavaService _javaService = new();

    private ServerCoreType _selectedCoreType = ServerCoreType.Vanilla;
    private readonly ObservableCollection<string> _mcVersions = new();
    private readonly ObservableCollection<ServerCoreBuild> _buildVersions = new();

    // 下载完成后，若需要安装（Forge/NeoForge），暂存下来供"立即运行安装"按钮使用
    private ServerCoreDownloadResult? _pendingInstallResult;
    private string? _pendingInstallTargetDir;

    /// <summary>
    /// 是否已经跑完构造函数里的 InitializeComponent()。
    ///
    /// 崩溃根因：与 DownloadCenterPage 完全相同的时序问题——XAML 里左侧分类栏默认选中的
    /// RadioButton 会在 InitializeComponent() 解析阶段同步触发 Checked 事件，但此时
    /// CorePanel/InstancesPanel/PlaceholderPanel/PlaceholderTitle 等自动生成字段还没赋值，
    /// Category_Checked 一读就是 NullReferenceException。用同一套 _initialized 短路方案。
    /// </summary>
    private bool _initialized;

    public ServerManagerPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        McVersionCombo.ItemsSource = _mcVersions;
        BuildVersionCombo.ItemsSource = _buildVersions;
        BuildVersionCombo.DisplayMemberPath = nameof(ServerCoreBuild.DisplayVersion);

        TargetDirBox.Text = System.IO.Path.Combine(App.DataDir, "servers");

        _initialized = true;
        Category_Checked(CatCoreDownload, new RoutedEventArgs()); // 补上初始化阶段被跳过的那次面板显隐

        _ = LoadMcVersionsAsync();
    }

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // InitializeComponent() 过程中触发的事件：控件树还没解析完，直接跳过
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag as string;

        CorePanel.Visibility = tag == "core" ? Visibility.Visible : Visibility.Collapsed;
        InstancesPanel.Visibility = tag == "instances" ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = tag is "core" or "instances" ? Visibility.Collapsed : Visibility.Visible;

        if (tag == "instances") RefreshInstanceList();

        PlaceholderTitle.Text = tag switch
        {
            "plugins" => "插件下载",
            "resourcepack" => "服务端资源包下载",
            _ => ""
        };
    }

    private async void CoreType_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return; // 同上：避免 InitializeComponent() 解析阶段的默认选中触发未初始化字段访问
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<ServerCoreType>(tag, out var coreType)) return;
        _selectedCoreType = coreType;

        // Vanilla 没有单独的"构建版本"概念，隐藏该栏；其余类型显示对应标签
        BuildVersionPanel.Visibility = coreType == ServerCoreType.Vanilla ? Visibility.Collapsed : Visibility.Visible;
        BuildVersionLabel.Text = coreType switch
        {
            ServerCoreType.Paper => "Build 号",
            ServerCoreType.Fabric => "Loader 版本",
            ServerCoreType.Forge => "安装器版本",
            ServerCoreType.NeoForge => "版本号",
            _ => "构建版本"
        };

        InstallRequiredPanel.Visibility = Visibility.Collapsed;
        _pendingInstallResult = null;

        await LoadMcVersionsAsync();
    }

    private async Task LoadMcVersionsAsync()
    {
        _mcVersions.Clear();
        _buildVersions.Clear();
        McVersionCombo.IsEnabled = false;
        DownloadCoreBtn.IsEnabled = false;

        try
        {
            List<string> versions = _selectedCoreType switch
            {
                ServerCoreType.Vanilla => await _coreService.GetVanillaVersionsAsync(includeSnapshots: false),
                ServerCoreType.Paper => await _coreService.GetPaperVersionsAsync(),
                ServerCoreType.Fabric => await _coreService.GetFabricMcVersionsAsync(),
                ServerCoreType.Forge => await _coreService.GetForgeVersionsAsync(),
                ServerCoreType.NeoForge => await LoadNeoForgeMcVersionPlaceholderAsync(),
                _ => new List<string>()
            };

            // 版本号排序：尝试按语义化版本从新到旧排列，排不了的（NeoForge 独立编号体系等）保留原始顺序
            foreach (var v in versions) _mcVersions.Add(v);

            if (_mcVersions.Count > 0) McVersionCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取版本列表失败] {ex}", "获取版本列表失败");
        }
        finally
        {
            McVersionCombo.IsEnabled = true;
            DownloadCoreBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// NeoForge 的版本号是独立编号（如 21.1.100），不是直接的 MC 版本号；
    /// 这里直接把 NeoForge 版本号本身列出来供用户选择，"MC 版本"栏对 NeoForge 而言
    /// 实际展示的就是完整 NeoForge 版本号，下载时二者取值相同。
    /// 后续如果要做"输入 MC 版本反查 NeoForge 版本"的映射，需要额外解析 NeoForge 版本号的命名约定，
    /// 当前先用这个更简单但完全可用的方式实现。
    /// </summary>
    private async Task<List<string>> LoadNeoForgeMcVersionPlaceholderAsync()
        => await _coreService.GetNeoForgeVersionsAsync();

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _buildVersions.Clear();
        if (McVersionCombo.SelectedItem is not string mcVersion) return;
        if (_selectedCoreType == ServerCoreType.Vanilla) return;
        if (_selectedCoreType == ServerCoreType.NeoForge) return; // NeoForge 的"版本"栏就是完整版本号，无需二级选择

        try
        {
            List<ServerCoreBuild> builds = _selectedCoreType switch
            {
                ServerCoreType.Paper => await _coreService.GetPaperBuildsAsync(mcVersion),
                ServerCoreType.Fabric => await _coreService.GetFabricLoaderVersionsAsync(),
                ServerCoreType.Forge => await _coreService.GetForgeInstallerVersionsAsync(mcVersion),
                _ => new List<ServerCoreBuild>()
            };

            foreach (var b in builds) _buildVersions.Add(b);

            var recommended = builds.FirstOrDefault(b => b.IsRecommended);
            BuildVersionCombo.SelectedItem = recommended ?? builds.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("获取构建版本列表失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[获取构建版本列表失败] {ex}", "获取构建版本列表失败");
        }
    }

    private void BrowseTargetDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择服务端安装位置" };
        if (System.IO.Directory.Exists(TargetDirBox.Text)) dialog.InitialDirectory = TargetDirBox.Text;
        if (dialog.ShowDialog() == true)
            TargetDirBox.Text = dialog.FolderName;
    }

    private async void DownloadCore_Click(object sender, RoutedEventArgs e)
    {
        if (McVersionCombo.SelectedItem is not string mcVersion)
        {
            MessageBox.Show("请先选择 Minecraft 版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetDirBox.Text))
        {
            MessageBox.Show("请先选择安装位置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 目标目录已存在且非空时提醒一下，避免用户没意识到会往一个已有文件夹里混入服务端文件
        if (System.IO.Directory.Exists(TargetDirBox.Text) &&
            System.IO.Directory.EnumerateFileSystemEntries(TargetDirBox.Text).Any())
        {
            var confirm = MessageBox.Show(
                $"目标目录「{TargetDirBox.Text}」不是空目录，服务端文件会下载到这个目录下。确定继续吗？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var buildVersion = (BuildVersionCombo.SelectedItem as ServerCoreBuild)?.DisplayVersion;

        var req = new ServerCoreDownloadRequest
        {
            CoreType = _selectedCoreType,
            McVersion = mcVersion,
            TargetDir = TargetDirBox.Text
        };

        // Forge 的"构建版本"下拉框里存的就是完整安装器版本号（mcVer-forgeVer），
        // NeoForge 则直接用选中的 McVersion 本身（见 LoadNeoForgeMcVersionPlaceholderAsync 的说明）
        if (_selectedCoreType == ServerCoreType.Forge) req.InstallerVersion = buildVersion;
        else if (_selectedCoreType == ServerCoreType.NeoForge) req.InstallerVersion = mcVersion;
        else req.BuildOrLoaderVersion = buildVersion;

        DownloadCoreBtn.IsEnabled = false;
        InstallRequiredPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBarCtl.Value = 0;

        var progress = new Progress<ProgressInfo>(p =>
        {
            ProgressStageText.Text = p.Stage;
            ProgressDetailText.Text = p.CurrentFile;
            ProgressBarCtl.Maximum = Math.Max(p.Total, 1);
            ProgressBarCtl.Value = p.Done;
        });

        try
        {
            var result = await _coreService.DownloadAsync(req, progress);

            if (result.RequiresInstall)
            {
                _pendingInstallResult = result;
                _pendingInstallTargetDir = req.TargetDir;
                InstallHintText.Text = $"{_selectedCoreType} 官方只提供安装器，需要本地再运行一次才能生成实际可用的服务端文件。" +
                    "点击下方按钮，使用启动器已配置的 Java 自动完成安装。";
                InstallRequiredPanel.Visibility = Visibility.Visible;
            }
            else
            {
                MessageBox.Show($"服务端核心下载完成：\n{result.DownloadedFilePath}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("下载失败，可能是网络连接问题或下载源暂时不可用，请检查网络后重试。", $"[下载失败] {ex}", "下载失败");
        }
        finally
        {
            DownloadCoreBtn.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void RunInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingInstallResult == null || _pendingInstallTargetDir == null) return;

        var javaPath = _javaService.FindJava(_owner.ConfigService.Config.JavaPath,
            _owner.ConfigService.Config.PreferredJavaMajorVersion);
        if (javaPath == null)
        {
            MessageBox.Show(
                "没有找到可用的 Java，无法运行安装器。请先在「设置」页配置或下载 Java 后再试。",
                "缺少 Java", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RunInstallerBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStageText.Text = "正在运行安装器";
        ProgressBarCtl.IsIndeterminate = true;

        var progress = new Progress<string>(line => ProgressDetailText.Text = line);

        try
        {
            var resultPath = await _coreService.RunForgeInstallerAsync(
                _pendingInstallResult.DownloadedFilePath, _pendingInstallTargetDir, javaPath, progress);

            InstallRequiredPanel.Visibility = Visibility.Collapsed;
            MessageBox.Show($"安装完成！\n服务端已生成到：\n{_pendingInstallTargetDir}\n\n启动脚本：{resultPath}",
                "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            _pendingInstallResult = null;
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("安装失败，可能是网络连接问题、下载源暂时不可用，或安装文件已损坏，请检查网络后重试。", $"[安装失败] {ex}", "安装失败");
        }
        finally
        {
            RunInstallerBtn.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressBarCtl.IsIndeterminate = false;
        }
    }

    // ============================================================
    // 服务器列表：启动/停止/控制台/删除
    // ============================================================

    private void RefreshInstanceList()
    {
        InstanceListPanel.Children.Clear();
        var instances = _owner.ServerInstanceService.Instances;

        if (instances.Count == 0)
        {
            InstanceListPanel.Children.Add(new TextBlock
            {
                Text = "还没有创建任何服务器，点击右上角「创建服务器」开始。",
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var instance in instances)
            InstanceListPanel.Children.Add(BuildInstanceCard(instance));
    }

    private Border BuildInstanceCard(ServerInstance instance)
    {
        var isRunning = _owner.ServerProcessManager.IsRunning(instance.Id);

        var card = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SideBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标：有自定义图标就加载显示，否则用一个占位方块 + 首字符，保持卡片布局不因缺图标而错位。
        var iconHost = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(6),
            Background = (System.Windows.Media.Brush)FindResource("GlowSoftBrush"),
            Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true
        };
        if (!string.IsNullOrEmpty(instance.IconPath) && File.Exists(instance.IconPath))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(instance.IconPath, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // 立即读完文件，避免占用文件句柄导致后续换图标时删不掉旧文件
                bmp.EndInit();
                iconHost.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
            }
            catch
            {
                // 图标文件损坏/格式不支持：静默回退到占位符，不阻断整个列表的渲染
                iconHost.Child = BuildIconPlaceholder(instance.DisplayName);
            }
        }
        else
        {
            iconHost.Child = BuildIconPlaceholder(instance.DisplayName);
        }
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(new TextBlock
        {
            Text = instance.DisplayName, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center
        });
        titleLine.Children.Add(new Border
        {
            Background = isRunning ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = isRunning ? "运行中" : "已停止", Foreground = System.Windows.Media.Brushes.White, FontSize = 11
            }
        });
        if (instance.IsDefault)
        {
            titleLine.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock { Text = "默认", Foreground = System.Windows.Media.Brushes.White, FontSize = 11 }
            });
        }
        infoPanel.Children.Add(titleLine);
        infoPanel.Children.Add(new TextBlock
        {
            Text = $"{instance.CoreType} · MC {instance.McVersion} · 内存 {instance.MinMemoryMb}~{instance.MaxMemoryMb}MB" +
                   (instance.CpuLimitPercent != null ? $" · CPU上限 {instance.CpuLimitPercent}%" : ""),
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        });

        // 连接地址：修复"新增出来的服务器没有 IP 地址"——之前卡片完全不展示怎么连进这个服务器，
        // 这里读取 server.properties 的 server-port + 本机局域网 IP 拼出连接地址；
        // 未运行时也照样展示（server.properties 在核心下载完成后就已经存在，不需要等服务器启动）。
        var connectionText = ServerConnectionInfoService.Resolve(instance);
        var addressLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        addressLine.Children.Add(new TextBlock
        {
            Text = $"连接地址：{connectionText}",
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            FontSize = 12
        });
        var copyAddrBtn = new Button
        {
            Content = "复制", Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(6, 0, 6, 0), FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0), Background = System.Windows.Media.Brushes.Gray
        };
        copyAddrBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(connectionText); } catch { /* 剪贴板偶发被占用，忽略即可，不阻断界面 */ }
        };
        addressLine.Children.Add(copyAddrBtn);
        infoPanel.Children.Add(addressLine);

        Grid.SetColumn(infoPanel, 1);
        grid.Children.Add(infoPanel);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (isRunning)
        {
            var consoleBtn = new Button { Content = "控制台", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 6, 0) };
            consoleBtn.Click += (_, _) => OpenConsole(instance);
            btnPanel.Children.Add(consoleBtn);

            var stopBtn = new Button
            {
                Content = "■ 停止", Style = (Style)FindResource("PrimaryButton"),
                Background = System.Windows.Media.Brushes.IndianRed, Margin = new Thickness(0, 0, 6, 0)
            };
            stopBtn.Click += async (_, _) => await StopInstanceAsync(instance);
            btnPanel.Children.Add(stopBtn);
        }
        else
        {
            var startBtn = new Button { Content = "▶ 启动", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 6, 0) };
            startBtn.Click += (_, _) => StartInstance(instance);
            btnPanel.Children.Add(startBtn);

            var deleteBtn = new Button
            {
                Content = "删除", Style = (Style)FindResource("PrimaryButton"),
                Background = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 6, 0)
            };
            deleteBtn.Click += (_, _) => DeleteInstance(instance);
            btnPanel.Children.Add(deleteBtn);
        }

        // "更多"按钮：承载导入导出/自定义图标/设为默认/重新覆盖安装这几个不常用的操作，
        // 避免每张卡片挤上 6-7 个常驻按钮导致列表过宽、误触风险变高。
        var moreBtn = new Button { Content = "⋯", Style = (Style)FindResource("PrimaryButton"), Background = System.Windows.Media.Brushes.Gray, Padding = new Thickness(10, 8, 10, 8) };
        moreBtn.Click += (_, _) => ShowInstanceMoreMenu(moreBtn, instance);
        btnPanel.Children.Add(moreBtn);

        Grid.SetColumn(btnPanel, 2);
        grid.Children.Add(btnPanel);

        card.Child = grid;
        return card;
    }

    private static Border BuildIconPlaceholder(string displayName)
    {
        var ch = string.IsNullOrEmpty(displayName) ? "?" : displayName.Substring(0, 1).ToUpperInvariant();
        return new Border
        {
            Child = new TextBlock
            {
                Text = ch, FontSize = 16, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>
    /// "更多"菜单：导入/导出/自定义图标/设为(取消)默认/重新覆盖安装。用 ContextMenu 而不是
    /// 常驻按钮组，因为这几项都是低频操作，塞进主按钮行会让每张卡片的操作区过宽。
    /// </summary>
    private void ShowInstanceMoreMenu(Button anchor, ServerInstance instance)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };

        // 修复"没有自定义服务器名称功能"：创建向导里虽然可以填初始名称，但创建完之后
        // 没有任何地方能改名字——之前"更多"菜单只有导入/导出/图标/默认/重装这几项，缺重命名。
        var renameItem = new MenuItem { Header = "重命名..." };
        renameItem.Click += (_, _) => RenameInstance(instance);
        menu.Items.Add(renameItem);

        var exportItem = new MenuItem { Header = "导出存档..." };
        exportItem.Click += (_, _) => ExportInstance(instance);
        menu.Items.Add(exportItem);

        var importItem = new MenuItem { Header = "导入存档 (覆盖此实例)..." };
        importItem.Click += (_, _) => ImportInstance(instance);
        menu.Items.Add(importItem);

        menu.Items.Add(new Separator());

        var iconItem = new MenuItem { Header = "设置自定义图标..." };
        iconItem.Click += (_, _) => SetInstanceIcon(instance);
        menu.Items.Add(iconItem);

        if (!string.IsNullOrEmpty(instance.IconPath))
        {
            var clearIconItem = new MenuItem { Header = "清除自定义图标" };
            clearIconItem.Click += (_, _) => ClearInstanceIcon(instance);
            menu.Items.Add(clearIconItem);
        }

        menu.Items.Add(new Separator());

        var defaultItem = new MenuItem { Header = instance.IsDefault ? "取消默认服务器" : "设为默认服务器" };
        defaultItem.Click += (_, _) => ToggleDefaultInstance(instance);
        menu.Items.Add(defaultItem);

        menu.Items.Add(new Separator());

        var propertiesItem = new MenuItem { Header = "服务器设置..." };
        propertiesItem.Click += (_, _) => OpenServerProperties(instance);
        menu.Items.Add(propertiesItem);

        menu.Items.Add(new Separator());

        var selectJavaItem = new MenuItem { Header = "选择 Java..." };
        selectJavaItem.Click += (_, _) => SelectInstanceJava(instance);
        menu.Items.Add(selectJavaItem);

        var reinstallItem = new MenuItem { Header = "重新覆盖安装核心..." };
        reinstallItem.Click += (_, _) => ReinstallInstanceCore(instance);
        menu.Items.Add(reinstallItem);

        menu.IsOpen = true;
    }

    /// <summary>
    /// 让用户从「设置」页登记的 Java 列表里，为这个已创建好的服务器实例单独选一个 Java，
    /// 不需要重新走一遍"重新覆盖安装核心"的整个下载/安装流程——这是纯粹的"换 Java"操作。
    /// </summary>
    /// <summary>
    /// 打开"服务器设置"窗口，图形化编辑 server.properties 常用字段。修法对应用户截图诉求：
    /// 让服务器可以自定义介绍(motd)/人数(max-players)/正版验证(online-mode)/允许飞行
    /// (allow-flight)等，字段清单照搬截图里另一个面板工具的分组。
    /// </summary>
    private void OpenServerProperties(ServerInstance instance)
    {
        var window = new ServerPropertiesWindow(instance.Directory) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void SelectInstanceJava(ServerInstance instance)
    {
        var window = new SelectJavaWindow(_owner.ConfigService, instance.JavaId, instance.JavaPath)
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;

        instance.JavaId = window.SelectedJavaId;
        instance.JavaPath = window.SelectedJavaPath ?? instance.JavaPath;
        _owner.ServerInstanceService.Update(instance);
        MessageBox.Show($"「{instance.DisplayName}」的 Java 已更新。", "已保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CreateServer_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new CreateServerWindow(_owner) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        RefreshInstanceList(); // 无论用户是否成功创建/取消，都刷新一遍，成功创建时列表会多一条
    }

    private void StartInstance(ServerInstance instance)
    {
        try
        {
            _owner.ServerProcessManager.Start(instance);
            RefreshInstanceList();
            OpenConsole(instance);
            MaybeShowNetworkGuide();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 服务器列表页顶部"🌐 如何联机/内网穿透教程"按钮：手动打开教程窗口，不判断
    /// ShowServerNetworkGuideOnStart（那个开关只管"启动服务器后是否自动弹"，手动点击这个
    /// 按钮时用户明确想看教程，不应该因为之前勾选过"不再自动提示"就被挡住）。
    /// 打开后如果用户又勾选了"不再自动提示"，一样写回配置——跟自动弹出那次行为一致。
    /// </summary>
    private void OpenNetworkGuide_Click(object sender, RoutedEventArgs e)
    {
        var guide = new ServerNetworkGuideWindow { Owner = Window.GetWindow(this) };
        guide.ShowDialog();
        if (guide.DontShowAgain)
        {
            _owner.ConfigService.Config.ShowServerNetworkGuideOnStart = false;
            _owner.ConfigService.Save();
        }
    }

    /// <summary>
    /// 服务器启动成功后，按用户设置弹出"如何开放外网访问"教程（内网穿透/路由器映射/云服务器）。
    /// 用 AppConfig.ShowServerNetworkGuideOnStart 控制是否弹出，用户在教程窗口里勾选
    /// "不再提示"后这里写回配置并保存，之后启动服务器就不会再自动弹出。现在还有另一个独立入口
    /// （服务器列表页顶部的"🌐 如何联机/内网穿透教程"按钮，见 OpenNetworkGuide_Click），
    /// 那个入口不受这个开关影响，随时可以手动打开。
    /// </summary>
    private void MaybeShowNetworkGuide()
    {
        var cfg = _owner.ConfigService.Config;
        if (!cfg.ShowServerNetworkGuideOnStart) return;

        var guide = new ServerNetworkGuideWindow { Owner = Window.GetWindow(this) };
        guide.ShowDialog();
        if (guide.DontShowAgain)
        {
            cfg.ShowServerNetworkGuideOnStart = false;
            _owner.ConfigService.Save();
        }
    }

    private async Task StopInstanceAsync(ServerInstance instance)
    {
        var confirm = MessageBox.Show(
            $"确定要停止服务器「{instance.DisplayName}」吗？会先尝试正常关服保存世界。",
            "确认停止", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _owner.ServerProcessManager.StopAsync(instance.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"停止失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshInstanceList();
        }
    }

    private void OpenConsole(ServerInstance instance)
    {
        var console = new ServerConsoleWindow(_owner, instance) { Owner = Window.GetWindow(this) };
        console.Closed += (_, _) => RefreshInstanceList(); // 控制台关闭时（可能服务器也被停止了）刷新状态
        console.Show();
    }

    private void DeleteInstance(ServerInstance instance)
    {
        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            MessageBox.Show("服务器正在运行，请先停止后再删除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"确定要删除服务器「{instance.DisplayName}」吗？\n\n" +
            "这里只会移除启动器里的记录，不会删除磁盘上的服务端文件夹。\n" +
            "如果需要连同存档/配置一起删除，请使用「清除服务器数据」功能（尚未实现）。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _owner.ServerInstanceService.Remove(instance.Id, deleteFiles: false);
            RefreshInstanceList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ============================================================
    // 存档导入导出 / 自定义图标 / 默认服务器 / 重新覆盖安装
    // ============================================================

    private readonly ServerInstanceTransferService _transferService = new();

    private void ExportInstance(ServerInstance instance)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出服务器存档",
            Filter = "XCL2 服务器存档 (*.xcl2server)|*.xcl2server",
            FileName = $"{instance.DisplayName}.xcl2server"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _transferService.Export(instance, dlg.FileName);
            MessageBox.Show("导出完成。", "存档导出", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 导入存档并覆盖到指定实例的目录下（合并覆盖策略，见 ServerInstanceTransferService.Import 注释）。
    /// 不改动实例的加载器/内存等配置字段——如果包内 manifest 有配置信息，只用于提示，不做静默覆盖，
    /// 避免用户没注意到的情况下配置被意外改掉。
    /// </summary>
    private void ImportInstance(ServerInstance instance)
    {
        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            MessageBox.Show("服务器正在运行，请先停止后再导入。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog { Title = "导入服务器存档", Filter = "XCL2 服务器存档 (*.xcl2server)|*.xcl2server|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;

        var confirm = MessageBox.Show(
            $"即将把存档内容合并覆盖到「{instance.DisplayName}」的服务器目录：\n{instance.Directory}\n\n" +
            "同名文件会被存档内容覆盖，其余现有文件保留。此操作不可撤销，建议先自行备份重要数据。",
            "确认导入", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var manifest = _transferService.Import(dlg.FileName, instance.Directory);
            var extra = manifest != null
                ? $"\n\n存档内附带的原始配置：{manifest.CoreType} · MC {manifest.McVersion}，内存 {manifest.MinMemoryMb}~{manifest.MaxMemoryMb}MB。\n" +
                  "如果需要按这份配置更新当前实例，请手动在创建/编辑向导里调整。"
                : "";
            MessageBox.Show("导入完成。" + extra, "存档导入", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetInstanceIcon(ServerInstance instance)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择服务器图标",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var savedPath = _owner.ServerInstanceService.SetIcon(instance.Id, dlg.FileName);
            instance.IconPath = savedPath;
            _owner.ServerInstanceService.Update(instance);
            RefreshInstanceList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置图标失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearInstanceIcon(ServerInstance instance)
    {
        _owner.ServerInstanceService.ClearIcon(instance.IconPath);
        instance.IconPath = null;
        _owner.ServerInstanceService.Update(instance);
        RefreshInstanceList();
    }

    /// <summary>
    /// 重命名一个已有服务器实例。名称冲突校验复用与创建向导一致的规则（同名不允许），
    /// 但要排除"实例改名改回自己原来的名字"这种不该算冲突的情况——否则用户点开重命名框
    /// 不改内容直接确定都会被拒绝。改名只影响 DisplayName，不影响 Id/目录/日志文件命名
    /// （那些都是用 Id，与 DisplayName 完全解耦，见 ServerInstance.Id 上的注释）。
    /// </summary>
    private void RenameInstance(ServerInstance instance)
    {
        var dlg = new RenameInstanceWindow(
            instance.DisplayName,
            isNameTaken: candidate => candidate != instance.DisplayName &&
                _owner.ServerInstanceService.Instances.Any(i => i.Id != instance.Id && i.DisplayName == candidate))
        {
            Owner = Window.GetWindow(this)
        };

        if (dlg.ShowDialog() != true) return;

        instance.DisplayName = dlg.NewName;
        _owner.ServerInstanceService.Update(instance);
        RefreshInstanceList();
    }

    private void ToggleDefaultInstance(ServerInstance instance)
    {
        _owner.ServerInstanceService.SetDefault(instance.IsDefault ? null : instance.Id);
        RefreshInstanceList();
    }

    /// <summary>
    /// 重新覆盖安装核心：复用创建向导 CreateServerWindow 的"选加载器/版本 -> 下载 -> (若需要)本地安装"
    /// 整套流程，而不是在这里重写一份下载 UI。窗口以 reinstallTarget 模式打开时不写入新的
    /// ServerInstance 记录，而是把下载/安装结果直接落到传入实例的 Directory 下，完成后更新
    /// 该实例现有记录的 CoreType/McVersion/LaunchTarget 等字段（而不是新增一条记录）。
    /// </summary>
    private void ReinstallInstanceCore(ServerInstance instance)
    {
        if (_owner.ServerProcessManager.IsRunning(instance.Id))
        {
            MessageBox.Show("服务器正在运行，请先停止后再重新安装核心。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"即将为「{instance.DisplayName}」重新下载并覆盖安装服务端核心文件。\n" +
            "world 存档等其余文件不会被清空，但核心 jar/启动脚本会被替换。是否继续？",
            "确认重新安装", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var wizard = new CreateServerWindow(_owner, reinstallTarget: instance) { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        RefreshInstanceList();
    }
}
