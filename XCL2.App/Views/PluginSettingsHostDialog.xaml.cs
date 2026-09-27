using System;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

/// <summary>把 IPlugin.CreateSettingsPanel 返回的 UserControl（或者插件没有提供图形界面时
/// 生成的通用只读 JSON 预览）装进一个统一样式的弹窗里展示，插件作者不需要自己实现
/// "怎么弹出来、怎么关掉"这部分外壳。</summary>
public partial class PluginSettingsHostDialog : OverlayDialogControl
{
    private readonly UserControl _content;

    public PluginSettingsHostDialog(string title, UserControl content)
    {
        InitializeComponent();
        TitleText.Text = title;
        HostContent.Content = content;
        _content = content;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_content is IPluginSettingsPanel savable)
        {
            try
            {
                savable.Save();
            }
            catch (Exception ex)
            {
                MessageBoxDialog.ShowWarning($"保存配置失败：{ex.Message}", "保存配置");
                return;
            }
        }

        ToastService.ShowSuccess("配置已保存。");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(null);
}
