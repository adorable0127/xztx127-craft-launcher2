using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 启动器界面语言选择弹窗。点击某一行立即切换并保存，立即关闭——不需要额外的"确定"
/// 按钮二次确认，参照系统语言选择器的交互习惯。
///
/// 打开入口：首页顶部按钮条最左边的"🌐 语言"按钮（见 HomePage.xaml 的
/// LanguageEntryButton），以及「设置」页"启动器界面语言"区块里的按钮，两处打开的是
/// 同一个弹窗、共享同一份切换逻辑，不是各自独立实现。
///
/// 迁移记录：原来是独立 Window（LanguageSelectWindow），现在改成挂在 MainWindow
/// Overlay 层里的 UserControl（继承 OverlayDialogControl，见 IOverlayDialog.cs）。
/// 原来"DialogResult = true; Close();"两行，现在统一改成调用基类的
/// CloseWith(true/false)。
/// </summary>
public partial class LanguageSelectDialog : OverlayDialogControl
{
    /// <summary>列表项的展示模型：NativeName 用于显示，IsCurrentVisibility 控制勾选图标的显隐。</summary>
    public sealed class LanguageItemViewModel
    {
        public required string Code { get; init; }
        public required string NativeName { get; init; }
        public Visibility IsCurrentVisibility { get; init; }
    }

    private readonly ConfigService _configService;

    public LanguageSelectDialog(ConfigService configService)
    {
        _configService = configService;
        InitializeComponent();

        var current = _configService.Config.LauncherLanguage;
        LanguageList.ItemsSource = Array.ConvertAll(LocalizationService.SupportedLanguages, l =>
            new LanguageItemViewModel
            {
                Code = l.Code,
                NativeName = l.NativeName,
                IsCurrentVisibility = l.Code == current ? Visibility.Visible : Visibility.Collapsed
            });
    }

    /// <summary>
    /// 点击某一行：写回配置、保存、立即应用（复用 LocalizationService.ApplyForCurrentState，
    /// 跟启动时读取配置应用语言是同一个入口，保证行为一致），然后关闭弹窗。
    /// 不判断"点的是不是当前已经选中的那一行"——即使重复点同一个语言，重新走一遍应用流程
    /// 也没有副作用，没必要额外加一层判断。
    /// </summary>
    private void LanguageItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string code }) return;

        _configService.Config.LauncherLanguage = code;
        _configService.Save();
        LocalizationService.ApplyForCurrentState(code);

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
