using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MirrorSettingsDialog : OverlayDialogControl
{
    private readonly ObservableCollection<DownloadMirrorRule> _rules;
    public DownloadMirrorConfig Result { get; private set; } = new();

    public MirrorSettingsDialog(DownloadMirrorConfig? config)
    {
        InitializeComponent();
        config ??= new();
        CommunityCheck.IsChecked = config.EnableCommunityMirrors;
        GithubCheck.IsChecked = config.EnableGithubMirrors;
        _rules = new((config.CustomRules ?? new()).Where(rule => rule != null).Select(rule => new DownloadMirrorRule
        {
            Name = rule.Name, SourcePrefix = rule.SourcePrefix, MirrorPrefix = rule.MirrorPrefix, Enabled = rule.Enabled
        }));
        RulesGrid.ItemsSource = _rules;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_rules.Count >= 32) { ValidationText.Text = "最多添加 32 条镜像映射。"; return; }
        var rule = new DownloadMirrorRule { Name = "自定义镜像", Enabled = true };
        _rules.Add(rule);
        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is DownloadMirrorRule rule) _rules.Remove(rule);
    }

    private void ResetHealth_Click(object sender, RoutedEventArgs e)
    {
        DownloadEndpoints.ResetHealth();
        ValidationText.Text = "健康记录已重置，下次下载将重新按偏好尝试。";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var rule in _rules)
        {
            rule.Name = (rule.Name ?? "").Trim();
            rule.SourcePrefix = (rule.SourcePrefix ?? "").Trim();
            rule.MirrorPrefix = (rule.MirrorPrefix ?? "").Trim();
            if (DownloadMirrorPolicy.ValidateRule(rule) is { } error)
            {
                RulesGrid.SelectedItem = rule;
                ValidationText.Text = error;
                return;
            }
        }
        Result = new DownloadMirrorConfig
        {
            EnableCommunityMirrors = CommunityCheck.IsChecked == true,
            EnableGithubMirrors = GithubCheck.IsChecked == true,
            CustomRules = _rules.ToList()
        };
        CloseWith(true);
    }
}
