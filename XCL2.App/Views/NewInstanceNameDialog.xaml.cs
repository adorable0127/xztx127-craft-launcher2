using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 从零安装整合包前，让用户给新实例起个名字，并把"这个包需要什么环境"先摆出来。
///
/// 为什么要先告诉用户环境要求：整合包安装是个几分钟的下载过程，
/// 如果用户到最后才发现"这个包要 Forge，而我没装 Java"，那几分钟就白等了。
/// 这里在点"开始安装"之前就把 MC 版本 / 加载器写清楚。
/// </summary>
public partial class NewInstanceNameDialog : OverlayDialogControl
{
    /// <summary>用户确认的实例名（已做文件名合法化）。仅在返回 true 时有意义。</summary>
    public string InstanceName { get; private set; } = "";

    public NewInstanceNameDialog(string suggestedName, string? mcVersion, string? loader, string? loaderVersion)
    {
        InitializeComponent();

        NameBox.Text = suggestedName;
        NameBox.SelectAll();
        NameBox.Focus();

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            RequirementText.Text = Loc.T("Str_Cs_The_Manifest_Doesn_T_Specify_A_Game_Vers", "清单里没写游戏版本（可能装不起来）");
        }
        else
        {
            var loaderPart = string.IsNullOrWhiteSpace(loader)
                ? "原版（无加载器）"
                : $"{loader} {loaderVersion}".Trim();
            RequirementText.Text = $"Minecraft {mcVersion} · {loaderPart}";
        }

        UpdateHint();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateHint();

    /// <summary>实时告诉用户"实际会建成什么名字"——文件名里的非法字符会被替换成下划线，
    /// 与其让用户点了确定之后才发现名字变了，不如现在就显示出来。</summary>
    private void UpdateHint()
    {
        var raw = NameBox.Text ?? "";
        var sanitized = ModpackInstallService.SanitizeInstanceName(raw);

        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(raw);

        NameHint.Text = string.Equals(raw.Trim(), sanitized, System.StringComparison.Ordinal)
            ? "会在 versions 文件夹下建一个同名目录"
            : $"名称里有文件夹不允许的字符，实际会建成：{sanitized}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        InstanceName = ModpackInstallService.SanitizeInstanceName(NameBox.Text ?? "");
        if (string.IsNullOrWhiteSpace(InstanceName)) return;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);
}
