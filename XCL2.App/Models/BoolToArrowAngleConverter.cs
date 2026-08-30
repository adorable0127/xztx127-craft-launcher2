using System.Globalization;
using System.Windows.Data;

namespace XCL2.App.Models;

/// <summary>
/// App.xaml 里 Expander 自绘 Header 模板用的箭头旋转角度转换器：IsChecked=false 时箭头
/// 指向右（-90°），IsChecked=true（展开）时指向下（0°）。
///
/// 之前尝试过用 ControlTemplate.Triggers 里的 Setter/TargetName 去改嵌套在
/// Path.RenderTransform 里的 RotateTransform.Angle：
///   1) 给 RotateTransform 起名(x:Name="arrowRotate") 直接 TargetName 过去——报 MC4111，
///      Freezable 类型的命名元素在模板里对 Setter/Trigger 的名字解析不可靠；
///   2) 改成 TargetName="arrow" Property="RenderTransform.Angle"——报 MC4106，因为
///      RenderTransform 属性声明类型是抽象的 Transform，没有 Angle；
///   3) 加括号 Property="RenderTransform.(RotateTransform.Angle)" 试图下钻到具体子类型——
///      编译能过，但运行时解析这条属性路径直接抛 XamlParseException，且发生在
///      App.xaml 资源合并阶段（早于 OnStartup 里任何异常处理器注册之前），表现就是
///      exe 打开后直接静默退出，一个报错、一行 crash.log 都没有。
/// 说明"Setter 跨抽象属性下钻到具体子类型的嵌套属性"这条路子在这个 WPF/.NET8 环境下
/// 从编译期到运行期都不稳定，彻底放弃，改用最朴素的 Binding+Converter：直接把
/// RotateTransform.Angle 绑定到 ToggleButton 自己的 IsChecked，没有 TargetName、
/// 没有嵌套属性路径，行为等价且不依赖任何模板内部机制。
/// </summary>
public class BoolToArrowAngleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0d : -90d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
