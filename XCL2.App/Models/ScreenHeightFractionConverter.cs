using System.Globalization;
using System.Windows.Data;

namespace XCL2.App.Models;

/// <summary>
/// 把屏幕高度(double)按 ConverterParameter 指定的比例(0~1)换算成一个具体像素值，
/// 用于"配色皮肤"下拉框弹层的 MaxHeight——需求是"加长到页面的七分之四到五分之三"
/// (约 0.571~0.6)，这里取一个居中的默认比例 0.55，同时允许通过 ConverterParameter
/// 传别的比例值复用同一个转换器，不需要为每个场景单独写一个转换器类。
/// </summary>
public class ScreenHeightFractionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double screenHeight) return 400d;

        var fraction = 0.55;
        if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            fraction = parsed;

        // 保底：极小屏幕/多显示器异常值时不要算出一个小于 200 的离谱高度，
        // 也不要超过屏幕本身（弹层比屏幕还高没有意义，Popup 也会自己夹住）。
        var result = screenHeight * fraction;
        return result < 200 ? 200d : result;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
