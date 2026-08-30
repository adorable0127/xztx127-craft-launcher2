using System.IO;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;   // Loc.T（代码内文案本地化，见 Services/Loc.cs）

namespace XCL2.App.Views;

/// <summary>
/// 整合包下载安装前，选择"新建一个独立版本目录"还是"装进已有版本目录"。
///
/// 背景：之前整合包分类走的是跟材质包/光影包一样的通用下载函数，只是把 .mrpack 文件本身
/// 下载到某个目录，既没有调用 ModpackService.ImportMrpackAsync 正确展开清单/下载依赖，
/// 也没有跟"整合包=一整套独立配置"这个直觉对齐——用户很容易在不知情的情况下把整合包内容
/// 装进了当前正在用的版本目录，跟已经装好的 mod 混在一起、互相覆盖。
///
/// 这个弹窗提供两种目标：
/// 1. 新建（默认）：产出一个全新的、带时间戳的版本目录名（同 QuickStartWizardWindow 里
///    "modpack-yyyyMMdd-HHmmss"的命名规则），保证跟任何已有版本目录都不冲突。
/// 2. 已有：从当前选中 GameFolder 下已扫描到的版本列表里选一个，直接合并安装进去——
///    调用方需要自行决定是否要在真正开始下载前再弹一次"会覆盖现有内容，确定继续吗"的二次确认
///    （参照 ModManagerPage.ImportModpack_Click 的做法），本弹窗只负责"选目标"这一步。
/// </summary>
public partial class ModpackTargetVersionDialog : OverlayDialogControl
{
    private readonly List<GameVersion> _existingVersions;

    /// <summary>用户确认后的目标版本目录名（不是完整路径，只是 versions/ 下的那一级目录名）。</summary>
    public string TargetVersionId { get; private set; } = "";

    /// <summary>true = 新建目录，false = 装进已选中的已有目录。仅供调用方在成功后决定
    /// 提示文案/是否需要额外注册到 Folders 配置里的版本列表，不影响 TargetVersionId 本身。</summary>
    public bool IsNewVersion { get; private set; } = true;

    /// <param name="suggestedName">预填到"新版本目录名称"输入框的默认名（一般用整合包标题
    /// 做初始建议，用户可自行修改），内部会做一次文件名合法化处理。</param>
    /// <param name="existingVersions">当前选中 GameFolder 下已扫描到的版本列表，用于"已有"
    /// 选项的下拉框；为空时"安装到已有版本目录"这个单选项会被禁用，不让用户选一个空列表。</param>
    public ModpackTargetVersionDialog(string suggestedName, IEnumerable<GameVersion> existingVersions)
    {
        InitializeComponent();
        _existingVersions = existingVersions.ToList();

        NewNameBox.Text = SanitizeForFolderName(suggestedName);

        ExistingVersionCombo.ItemsSource = _existingVersions;
        if (_existingVersions.Count > 0) ExistingVersionCombo.SelectedIndex = 0;

        // 没有任何已有版本目录时，"安装到已有版本目录"这个选项直接禁用并附加说明，
        // 避免用户选中后却发现下拉框里空空如也、不知道该怎么继续。
        if (_existingVersions.Count == 0)
        {
            OptExisting.IsEnabled = false;
            ExistingVersionCombo.IsEnabled = false;
        }

        UpdatePanelState();
        Loaded += (_, _) => NewNameBox.Focus();
    }

    /// <summary>去掉文件系统路径不允许出现的字符，避免用户直接拿整合包标题
    /// （可能带有 / \ : * ? " &lt; &gt; | 等符号，或者纯 emoji）当目录名时创建目录失败。</summary>
    private static string SanitizeForFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned)
            ? "modpack-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
            : cleaned;
    }

    private void OptionChanged(object sender, RoutedEventArgs e) => UpdatePanelState();

    /// <summary>根据当前选中的是"新建"还是"已有"，切换对应输入区域的可用状态——
    /// 两个面板始终占位显示（不用 Collapsed 整体隐藏），只是灰掉不相关的那一个，
    /// 这样用户能看到两条路径完整存在，不会因为切换单选按钮导致弹窗高度跳动。</summary>
    private void UpdatePanelState()
    {
        if (NewNameBox == null || ExistingVersionCombo == null) return; // 构造函数早期 Checked 事件触发时控件还没就绪

        var creatingNew = OptCreateNew.IsChecked == true;
        NewNameBox.IsEnabled = creatingNew;
        ExistingVersionCombo.IsEnabled = !creatingNew && _existingVersions.Count > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (OptCreateNew.IsChecked == true)
        {
            var name = SanitizeForFolderName(NewNameBox.Text);
            if (string.IsNullOrWhiteSpace(name))
            {
                NewNameErrorText.Text = Loc.T("Str_Cs_Please_Enter_A_Version_Folder_Name", "请输入版本目录名称。");
                NewNameErrorText.Visibility = Visibility.Visible;
                return;
            }
            if (_existingVersions.Any(v => string.Equals(v.Id, name, StringComparison.OrdinalIgnoreCase)))
            {
                NewNameErrorText.Text = Loc.T("Str_Cs_A_Version_Folder_With_That_Name_Already_", "已经有一个同名的版本目录了，请换一个名称。");
                NewNameErrorText.Visibility = Visibility.Visible;
                return;
            }

            TargetVersionId = name;
            IsNewVersion = true;
        }
        else
        {
            if (ExistingVersionCombo.SelectedItem is not GameVersion selected)
            {
                NewNameErrorText.Visibility = Visibility.Collapsed;
                MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Choose_An_Existing_Version_Folder", "请先选择一个已有的版本目录。"));
                return;
            }

            TargetVersionId = selected.Id;
            IsNewVersion = false;
        }

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);
}
