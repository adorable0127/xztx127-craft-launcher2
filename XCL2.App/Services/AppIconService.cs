using System.Windows;
using System.Windows.Media.Imaging;

namespace XCL2.App.Services;

/// <summary>
/// 深色/浅色模式下自动切换窗口图标。
///
/// ===== 先分清三种"图标"，它们不是一回事 =====
///
/// 1) **exe 文件图标**（资源管理器里那个 .exe 的图标）
///    来源是 csproj 的 &lt;ApplicationIcon&gt;，**编译时烧进二进制**，一个可执行文件只能有一个，
///    运行时无法更改，Windows 也不会因为系统切了深色模式就换一张。
///    → 这一项永远用 Resources/app.ico（浅色那张），不参与切换。
///
/// 2) **窗口图标**（标题栏左上角 + 任务栏 + Alt-Tab）
///    对应 Window.Icon 属性，**运行时可以随时改**。这才是这个服务负责的东西。
///    深色模式下 Win11 任务栏背景是深色，深色描边的图标会糊成一团看不见，
///    换成浅色版本才看得清——这正是要做自动切换的实际理由。
///
/// 3) **应用内 logo**（侧边栏顶部 / 关于页里显示的那张图）
///    这一项**不应该用 .ico**：ico 是多尺寸位图容器，WPF 的 Image 控件引用 ico 时
///    会自己挑一个尺寸再拉伸，非整数倍缩放下必糊。应用内 logo 请另存 PNG（512px）
///    或直接画成 XAML Path 矢量，用 {DynamicResource} 跟着主题切换。
///
/// ===== 文件该放哪 =====
///     Resources/app.ico        ← 浅色模式（保持不动，同时也是 exe 图标来源）
///     Resources/app-dark.ico   ← 深色模式（把你做的 1.ico 改名放这里）
/// 两者都要在 csproj 里注册成 &lt;Resource Include="..."/&gt;，否则运行时 pack URI 取不到。
///
/// ===== 为什么不用 App.xaml 里的隐式 Style Setter =====
/// 原来 App.xaml 里有一条 &lt;Style TargetType="Window"&gt;&lt;Setter Property="Icon" .../&gt;，
/// 给所有窗口统一设图标。但 Setter 设的是"样式值"，优先级低于本地值：
/// 一旦这个服务给 window.Icon 赋了本地值，Setter 就永远不再生效；
/// 而如果保留 Setter，新窗口会先按 Setter 显示浅色图标、再被这里改掉，肉眼能看到闪一下。
/// 两套机制并存只会互相打架，所以 App.xaml 那条 Setter 已经移除，
/// 图标统一由这个服务这一个地方负责。
/// </summary>
public static class AppIconService
{
    private const string LightIconUri = "pack://application:,,,/Resources/app.ico";
    private const string DarkIconUri = "pack://application:,,,/Resources/app-dark.ico";

    // BitmapFrame 创建有磁盘 IO + 解码开销，而主题切换会遍历所有已打开窗口逐个赋值，
    // 缓存一份避免每次切换都重复解码。图标文件在程序生命周期内不会变，缓存是安全的。
    private static BitmapFrame? _lightCache;
    private static BitmapFrame? _darkCache;

    /// <summary>
    /// 取当前主题对应的图标。深色图标文件缺失时**自动回退到浅色图标**，
    /// 而不是抛异常——图标只是观感问题，绝不该因为少一个文件就让程序起不来。
    /// （比如用户只放了 app.ico、还没来得及放 app-dark.ico 就先编译了一次。）
    /// </summary>
    public static BitmapFrame? GetCurrentIcon()
    {
        var wantDark = ThemeService.CurrentIsDarkMode;

        if (wantDark)
        {
            _darkCache ??= TryLoad(DarkIconUri);
            if (_darkCache != null) return _darkCache;
            // 深色图标没放/损坏：回退浅色，不报错。
        }

        _lightCache ??= TryLoad(LightIconUri);
        return _lightCache;
    }

    /// <summary>给单个窗口应用当前主题对应的图标。窗口创建后调用一次即可。</summary>
    public static void ApplyTo(Window window)
    {
        try
        {
            var icon = GetCurrentIcon();
            if (icon != null) window.Icon = icon;
        }
        catch
        {
            // 设置图标失败不影响窗口本身能不能用，静默跳过。
        }
    }

    /// <summary>
    /// 给当前所有已打开的窗口刷新图标。由 ThemeService.RefreshOpenWindows 在每次
    /// 深浅色切换后调用，这样已经开着的窗口不用关掉重开就能看到新图标。
    /// 同时顺带刷新 MainWindow 自绘标题栏左上角那个 Image（如果当前打开的窗口
    /// 就是 MainWindow 的话），道理跟 Window.Icon 一样——深浅色模式要用两张不同的图。
    /// </summary>
    public static void ApplyToAllOpenWindows()
    {
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows)
        {
            ApplyTo(window);
            if (window is Views.MainWindow main) ApplyToTitleBarImage(main, main.TitleBarIconImage);
        }
    }

    /// <summary>给自绘标题栏里那个 Image 控件赋当前主题对应的图标（跟 Window.Icon 是
    /// 同一张位图，共用同一份缓存，不会重复解码）。只有 MainWindow 用得到自绘标题栏，
    /// 其它 20 多个弹窗仍然是系统标题栏，不需要调用这个方法。</summary>
    public static void ApplyToTitleBarImage(Window window, System.Windows.Controls.Image image)
    {
        try
        {
            var icon = GetCurrentIcon();
            if (icon != null) image.Source = icon;
        }
        catch
        {
            // 同 ApplyTo：图标只是观感问题，失败了静默跳过，不影响窗口本身。
        }
    }

    private static BitmapFrame? TryLoad(string uri)
    {
        try
        {
            return BitmapFrame.Create(
                new Uri(uri, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad); // OnLoad：立刻读完并释放文件流，避免占用资源句柄
        }
        catch
        {
            return null;
        }
    }
}
