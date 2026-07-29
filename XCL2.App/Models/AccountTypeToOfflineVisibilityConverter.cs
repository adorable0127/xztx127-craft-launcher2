using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace XCL2.App.Models;

/// <summary>
/// 把 Account.Type 转换成 Visibility：离线账户(Offline)显示，微软账户(Microsoft)隐藏。
/// 用于 LoginPage 账户列表里的"皮肤"按钮——微软账户的皮肤由 Mojang 服务器托管，
/// 启动器不需要也不应该提供皮肤设置入口。
/// </summary>
public class AccountTypeToOfflineVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AccountType.Offline ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
