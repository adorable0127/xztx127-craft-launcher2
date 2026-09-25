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

/// <summary>
/// 跟 <see cref="AccountTypeToOfflineVisibilityConverter"/> 刚好相反：离线账户(Offline)隐藏，
/// 微软/皮肤站账户显示。用于 LoginPage 账户列表里的"账户页面"按钮——这个按钮点击后跳转到
/// 微软官网 msaprofile 页面或者皮肤站的 API Root 地址，离线账户两者都没有，点了按钮除了在
/// 状态栏提示一句"离线账户没有账户页面"之外什么都不会发生，是一个纯摆设、还会让人误以为
/// 点了应该有反应的死按钮，所以离线账户这一行直接不显示它，而不是显示出来点了才告诉你没用。</summary>
public class AccountTypeToNonOfflineVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AccountType.Offline ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
