using System.Windows;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>兼容旧调用方的独立窗口包装；实际设置 UI 与主窗口 Overlay 共用 AiAssistantSettingsPanel。</summary>
public partial class AiAssistantSettingsDialog : Window
{
    public AiAssistantConfig Result { get; private set; }

    public AiAssistantSettingsDialog(AiAssistantConfig current)
    {
        Result = current;
        InitializeComponent();

        var panel = new AiAssistantSettingsPanel(current);
        PanelHost.Content = panel;
        panel.Saved += (_, config) => Result = config;
        panel.RequestClose += (_, accepted) => DialogResult = accepted == true;
    }
}
