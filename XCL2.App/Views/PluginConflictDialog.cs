using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>关闭或 Esc 均等同取消，不改变已有安装。</summary>
internal sealed class PluginConflictDialog : OverlayDialogControl
{
    private PluginInstallConflictAction _choice = PluginInstallConflictAction.Cancel;
    private PluginConflictDialog(string name, string existing)
    {
        Width = 520;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "发现同名插件", FontSize = 18, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = $"{name}\n已安装：{existing}\n\n复制为副本：保留原版，使用独立配置与数据。\n🪖 替换可能损坏插件或文件。启动器将暂停插件，先自动备份全部插件文件、配置、数据与启用状态（不含游戏存档），校验成功后再更新。备份可在插件管理中恢复。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, action) in new[] { ("复制为副本", PluginInstallConflictAction.Copy),
            ("替换", PluginInstallConflictAction.Replace), ("取消", PluginInstallConflictAction.Cancel) })
        {
            var button = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4) };
            button.Click += (_, _) => { _choice = action; CloseWith(action != PluginInstallConflictAction.Cancel); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        Content = panel;
    }
    internal static PluginInstallConflictAction Choose(string name, string existing)
    {
        var dialog = new PluginConflictDialog(name, existing);
        dialog.ShowDialog();
        return dialog._choice;
    }
}
