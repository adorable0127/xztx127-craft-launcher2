using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Views.Controls;

namespace XCL2.App.Services;

/// <summary>主页面与二级页面共用的轻量加载占位。不要在页面构造函数中复制解码 GIF。</summary>
public static class LoadingPlaceholderFactory
{
    public static UIElement Create(string? detail = null)
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24)
        };
        content.Children.Add(new LoadingIndicator
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var title = new TextBlock
        {
            Text = Loc.T("Str_Ui_LoadingTitle", "正在加载"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        content.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = detail ?? Loc.T("Str_Ui_LoadingDetail", "正在准备界面与数据"),
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        content.Children.Add(subtitle);

        var placeholder = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10),
            Child = content,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        placeholder.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        placeholder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
        return placeholder;
    }
}
