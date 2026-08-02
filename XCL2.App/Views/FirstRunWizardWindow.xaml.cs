using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 首次启动向导：引导新手完成 Java 安装、游戏文件夹/语言设置、账户创建这三件"不做就完全没法开始游戏"
/// 的必要配置。默认在第一次打开启动器时自动弹出(见 MainWindow 构造函数)，跑完/跳过后不会再自动弹出，
/// 但可以在设置页里手动重新打开。
///
/// 支持两种走法：
/// 1) 手动模式：一步步"下一步"，每步都能自己调整(选文件夹路径、语言、离线用户名等)。
/// 2) 一键完成：点一下，后台按推荐配置自动跑完全部步骤(自动装 Java、用默认文件夹、
///    简体中文、自动生成一个离线账户)，全程不需要用户再做任何选择——服务小白用户。
/// </summary>
public partial class FirstRunWizardWindow : Window
{
    private readonly MainWindow _owner;
    private readonly JavaService _javaService = new();

    private int _step = 1;
    private const int TotalSteps = 4;

    /// <summary>本次向导里自动/手动安装好的 Java 路径，供最终写回配置用。</summary>
    private string? _resolvedJavaPath;
    private bool _javaStepDone;
    private bool _accountStepDone;
    private bool _suppressModeEvent;
    /// <summary>是否已经执行过一次 Complete()。配合 Closing 事件兜底逻辑使用，
    /// 避免"用户点了 Skip/完成按钮触发 Complete() -> Close() -> 又触发 Closing"
    /// 时重复保存一次配置。</summary>
    private bool _completed;

    public FirstRunWizardWindow(MainWindow owner)
    {
        _owner = owner;

        // 重要：必须在 InitializeComponent() 之前把 _suppressModeEvent 置为 true。
        // 原因：XAML 里 WizardSimpleModeRadio 写了 IsChecked="True"，这个值会在
        // InitializeComponent() 解析/连接控件的过程中被应用，从而立即触发它绑定的
        // Checked="WizardMode_Changed" 事件——但这时 WPF 的 IStyleConnector 还没走到
        // 给 WizardAdvancedModeRadio 这个字段赋值那一步（它在 XAML 里排在后面），
        // 导致 WizardMode_Changed 里访问 WizardAdvancedModeRadio.IsChecked 时该字段
        // 仍是 null，抛出 NullReferenceException（构造函数还没执行到任何一行就崩溃，
        // 表现为"打开向导窗口立即报错"）。
        // 把抑制标志提前到这里，InitializeComponent() 内部触发的这次事件会被直接
        // return 掉，等真正走到下面的回填逻辑时再按需要手动设置一次。
        _suppressModeEvent = true;
        InitializeComponent();

        // 修复：之前只有点「跳过」或「完成/一键完成」按钮才会把 FirstRunWizardCompleted
        // 写成 true。但窗口默认带系统标题栏，用户完全可以直接点右上角的「×」把向导关掉，
        // 这种关闭方式不会走 Skip_Click / Complete()，导致该字段永远是 false——
        // 于是下次启动 MainWindow 又判断"没完成过"，向导又弹出来，成了怎么关都关不掉的
        // 遗留 bug。这里用 Closing 事件兜底：不管用户是点 Skip、点完成，还是直接点 ×
        // 关闭，只要窗口关闭这一步就必然会执行到这里，统一保证标记为已完成
        // （_completed 幂等标志避免重复保存；已经手动走完 Complete() 的路径会在此处跳过）。
        Closing += (_, _) =>
        {
            if (!_completed)
            {
                // 注意：这里不能调用 Complete()，因为 Complete() 内部会调用 Close()——
                // 而当前正处于 Closing 事件回调中，窗口本身已经在关闭流程里，此时再次
                // 调用 Close()/Show()/ShowDialog() 或修改 Visibility 会被 WPF 直接拒绝，
                // 抛出 InvalidOperationException("在窗口关闭期间，无法...")。
                // 所以这里只做「标记完成 + 保存配置」，不再触发第二次 Close()。
                _completed = true;
                ApplyFolderAndLanguage();
                _owner.ConfigService.Config.FirstRunWizardCompleted = true;
                _owner.ConfigService.Save();
                _owner.RefreshSidebar();
            }
        };

        // 默认游戏文件夹：沿用已有配置里的默认文件夹（如果用户是"重新打开向导"），
        // 否则给一个新装机场景下最自然的默认位置——启动器所在目录下的 .minecraft。
        var cfg = _owner.ConfigService.Config;
        var existingDefault = cfg.Folders.FirstOrDefault(f => f.IsDefault) ?? cfg.Folders.FirstOrDefault();
        FolderPathBox.Text = existingDefault?.Path
            ?? Path.Combine(AppContext.BaseDirectory, ".minecraft");

        SelectComboByTag(LanguageCombo, cfg.GameLanguage);

        // 模式选择：回填现有配置（重新打开向导时应该看到上次选的状态，而不是每次都强制回到
        // "普通模式"这个 XAML 默认值）。此时仍处于 _suppressModeEvent = true 状态，
        // 回填不会触发 WizardMode_Changed 里"立即写回配置"的逻辑，构造函数阶段没必要写一次
        // 没有变化的值。
        if (cfg.AdvancedMode) WizardAdvancedModeRadio.IsChecked = true;
        else WizardSimpleModeRadio.IsChecked = true;
        _suppressModeEvent = false;

        UpdateAccountStatusText();
        _ = DetectJavaAsync();
    }

    private static void SelectComboByTag(ComboBox combo, string tagValue)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && Equals(item.Tag?.ToString(), tagValue))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void UpdateAccountStatusText()
    {
        var existing = _owner.ConfigService.GetSelectedAccount();
        AccountStatusText.Text = existing == null
            ? "当前还没有任何账户。"
            : $"当前已有账户：{existing.DisplayLabel}，可以直接点「下一步」跳过这一步，或者创建一个新的离线账户替换它。";
        _accountStepDone = existing != null;
    }

    // ---------- 步骤 1：模式选择 ----------

    /// <summary>
    /// 用户在向导第一页切换普通/高手模式时立即写回配置并保存——不等向导走完，理由：
    /// 1) 用户中途点"跳过引导"也应该让这次选择生效，不能因为跳过就丢掉。
    /// 2) 高手模式下第一步 Java 检测文案不需要因为模式而改变，但以后如果这一页/后续步骤要
    ///    根据模式动态显示不同内容（比如高手模式显示更详细的 Java 探测细节），这里是唯一
    ///    需要改的地方。
    /// </summary>
    private void WizardMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvent) return;
        // 防御性判空：即使以后有改动导致这个事件在 XAML 解析阶段被意外触发
        // （此时部分具名控件字段可能还没连接完），也不要让整个窗口崩溃。
        if (WizardAdvancedModeRadio == null) return;
        _owner.ConfigService.Config.AdvancedMode = WizardAdvancedModeRadio.IsChecked == true;
        _owner.ConfigService.Save();
    }

    // ---------- 步骤 1：检测/安装 Java ----------

    private async System.Threading.Tasks.Task DetectJavaAsync()
    {
        JavaStatusText.Text = "正在检测本机 Java...";
        AutoInstallJavaBtn.IsEnabled = false;
        try
        {
            // 只是探测，不下载；找不到就提示用户点"一键安装"，这一步不阻塞用户往下走，
            // 因为高级用户可能想跳过、之后自己在设置页指定路径。
            var found = await System.Threading.Tasks.Task.Run(() =>
                _javaService.FindJava(_owner.ConfigService.Config.JavaPath, configService: _owner.ConfigService));

            if (found != null)
            {
                _resolvedJavaPath = found;
                _javaStepDone = true;
                JavaStatusText.Text = $"已检测到可用的 Java：{found}\n可以直接点「下一步」继续。";
            }
            else
            {
                JavaStatusText.Text = "本机没有检测到可用的 Java，建议点击下面的按钮一键安装（约 200MB，需要联网）。";
            }
        }
        catch (Exception ex)
        {
            JavaStatusText.Text = "检测 Java 时出错，可以直接点「一键安装」重新安装一份：" + ex.Message;
        }
        finally
        {
            AutoInstallJavaBtn.IsEnabled = true;
        }
    }

    private async void AutoInstallJava_Click(object sender, RoutedEventArgs e)
    {
        AutoInstallJavaBtn.IsEnabled = false;
        var progressWin = new ProgressDialog("正在下载 Java 运行时...");
        progressWin.Show();
        try
        {
            var path = await _javaService.DownloadRecommendedJavaAsync(progressWin.Progress);
            _resolvedJavaPath = path;
            _javaStepDone = true;
            JavaStatusText.Text = $"Java 安装完成：{path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Java 安装失败：\n" + ex.Message + "\n\n可以先跳过这一步，之后在「设置」页重试。",
                "安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            progressWin.Close();
            AutoInstallJavaBtn.IsEnabled = true;
        }
    }

    // ---------- 步骤 2：文件夹 + 语言 ----------

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        // WPF 自带对话框没有"选文件夹"，用保存对话框变通选目录是常见做法之一，
        // 但为了避免用户误解，这里改用 OpenFolderDialog 风格的 FolderBrowserDialog 替代方案：
        // 直接允许用户手动输入/粘贴路径，同时也提供一个基于"新建文件"曲线选择目录的兜底。
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏文件夹",
            InitialDirectory = Directory.Exists(FolderPathBox.Text) ? FolderPathBox.Text : AppContext.BaseDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            FolderPathBox.Text = dialog.FolderName;
        }
    }

    // ---------- 步骤 3：账户 ----------

    private void CreateOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(OfflineNameBox.Text) ? "Player" : OfflineNameBox.Text.Trim();
        var account = OfflineAuthService.CreateOfflineAccount(name);
        _owner.ConfigService.AddOrUpdateAccount(account);
        _owner.ConfigService.SelectAccount(account.Id);
        _accountStepDone = true;
        UpdateAccountStatusText();
        MessageBox.Show($"离线账户「{name}」创建成功，已自动选中。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void GotoMicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        // 微软登录流程本身较长（设备码/网页跳转），不适合塞进向导弹窗里，
        // 直接关闭向导、跳转到主窗口的账户管理页，让用户在那边完整走完登录。
        ApplyFolderAndLanguage();
        Complete(markCompleted: false); // 先不标记"已完成"，等用户登录完自己回来重开向导或者直接开玩都行
        _owner.NavigateToAccounts();
    }

    // ---------- 通用：步骤切换 ----------

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step >= TotalSteps)
        {
            FinishManualFlow();
            return;
        }
        GoToStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        GoToStep(_step - 1);
    }

    private void GoToStep(int step)
    {
        _step = Math.Clamp(step, 1, TotalSteps);

        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        StepDot1.Background = _step >= 1 ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.LightGray;
        StepDot2.Background = _step >= 2 ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.LightGray;
        StepDot3.Background = _step >= 3 ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.LightGray;
        StepDot4.Background = _step >= 4 ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.LightGray;

        BackBtn.IsEnabled = _step > 1;
        NextBtn.Content = _step == TotalSteps ? "完成" : "下一步";

        if (_step == 3) UpdateAccountStatusText();
        if (_step == 4) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var cfg = _owner.ConfigService.Config;
        var acc = _owner.ConfigService.GetSelectedAccount();
        var mode = _owner.ConfigService.Config.AdvancedMode ? "高手模式" : "普通模式";
        SummaryText.Text =
            $"使用模式：{mode}\n" +
            $"Java：{(_javaStepDone ? (_resolvedJavaPath ?? "已就绪") : "未安装，可稍后在设置页补装")}\n" +
            $"游戏文件夹：{FolderPathBox.Text}\n" +
            $"语言：{(LanguageCombo.SelectedItem as ComboBoxItem)?.Content}\n" +
            $"账户：{(acc?.DisplayLabel ?? "未设置，可稍后在账户管理页添加")}";
    }

    private void ApplyFolderAndLanguage()
    {
        var cfg = _owner.ConfigService.Config;
        var path = string.IsNullOrWhiteSpace(FolderPathBox.Text)
            ? Path.Combine(AppContext.BaseDirectory, ".minecraft")
            : FolderPathBox.Text.Trim();

        try { Directory.CreateDirectory(path); }
        catch { /* 目录创建失败不阻塞向导，用户后续在版本选择页仍可以重新指定文件夹 */ }

        var existing = cfg.Folders.FirstOrDefault(f => f.Path == path);
        if (existing == null)
        {
            existing = new GameFolder { Name = "默认文件夹", Path = path, IsDefault = cfg.Folders.Count == 0 };
            cfg.Folders.Add(existing);
        }
        cfg.SelectedFolderPath = existing.Path;

        if ((LanguageCombo.SelectedItem as ComboBoxItem)?.Tag is string lang) cfg.GameLanguage = lang;

        if (_javaStepDone && !string.IsNullOrEmpty(_resolvedJavaPath) && string.IsNullOrEmpty(cfg.JavaPath))
        {
            // 只有用户之前完全没手动指定过 Java 路径时才写回，避免覆盖用户已有的手动设置。
            cfg.JavaPath = _resolvedJavaPath;
        }

        _owner.ConfigService.Save();
    }

    private void FinishManualFlow()
    {
        ApplyFolderAndLanguage();
        Complete(markCompleted: true);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        // 跳过也要保存已经填好的部分（比如用户可能已经点过"一键安装 Java"），
        // 不能因为点了跳过就把已经做的工作丢掉。
        ApplyFolderAndLanguage();
        Complete(markCompleted: true);
    }

    // ---------- 一键完成 ----------

    private async void OneClickComplete_Click(object sender, RoutedEventArgs e)
    {
        OneClickBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;
        BackBtn.IsEnabled = false;
        AutoRunOverlay.Visibility = Visibility.Visible;

        try
        {
            // 1) Java：已经就绪就跳过下载，否则自动装推荐版本。
            AutoRunText.Text = "正在检查 Java 环境...";
            AutoRunBar.Value = 5;
            if (!_javaStepDone)
            {
                var progress = new Progress<ProgressInfo>(info =>
                {
                    var pct = info.Total > 0 ? (double)info.Done / info.Total : 0;
                    AutoRunBar.Value = 5 + pct * 55; // Java 步骤占 5~60%
                    AutoRunText.Text = $"正在安装 Java：{info.CurrentFile}";
                });
                try
                {
                    _resolvedJavaPath = await _javaService.DownloadRecommendedJavaAsync(progress);
                    _javaStepDone = true;
                }
                catch (Exception ex)
                {
                    // Java 安装失败不阻断整个一键流程：文件夹/语言/账户依然可以配好，
                    // 用户只是之后需要在设置页手动重试一次 Java 安装。
                    File.AppendAllText(Path.Combine(App.DataDir, "logs", "crash.log"),
                        $"[{DateTime.Now}] 新手引导一键安装 Java 失败: {ex}\n\n");
                }
            }

            // 2) 文件夹 + 语言：用当前表单里的值（默认值已经在构造函数里填好）。
            AutoRunText.Text = "正在设置游戏文件夹与语言...";
            AutoRunBar.Value = 65;
            ApplyFolderAndLanguage();

            // 3) 账户：如果还没有任何账户，自动创建一个离线账户，用户随时可以后续换成微软账户。
            AutoRunText.Text = "正在创建默认账户...";
            AutoRunBar.Value = 85;
            if (!_accountStepDone)
            {
                var name = string.IsNullOrWhiteSpace(OfflineNameBox.Text) ? "Player" : OfflineNameBox.Text.Trim();
                var account = OfflineAuthService.CreateOfflineAccount(name);
                _owner.ConfigService.AddOrUpdateAccount(account);
                _owner.ConfigService.SelectAccount(account.Id);
                _accountStepDone = true;
            }

            AutoRunBar.Value = 100;
            AutoRunText.Text = "全部完成！";
            await System.Threading.Tasks.Task.Delay(500);

            GoToStep(TotalSteps);
            AutoRunOverlay.Visibility = Visibility.Collapsed;
            Complete(markCompleted: true);
        }
        finally
        {
            OneClickBtn.IsEnabled = true;
            NextBtn.IsEnabled = true;
            BackBtn.IsEnabled = _step > 1;
        }
    }

    private void Complete(bool markCompleted)
    {
        // 幂等：Skip/一键完成里调用一次 Complete() -> Close() -> 触发上面订阅的 Closing
        // 事件 -> 检查 _completed 为 true 就直接跳过，不会重复保存/重复 Close()。
        if (_completed)
        {
            return;
        }
        _completed = true;

        if (markCompleted)
        {
            _owner.ConfigService.Config.FirstRunWizardCompleted = true;
            _owner.ConfigService.Save();
        }
        _owner.RefreshSidebar();
        Close();
    }
}
