using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 账户列表（「账户」导航页 LoginPage、启动前的「选择账户」弹窗 AccountPickerDialog）里
/// 用户名左边那个静态头像的取图逻辑。三档优先级，从高到低：
///   1) 用户自己导入的头像照片（Account.AvatarPhotoPath，任意图片，跟 Minecraft 皮肤无关）；
///   2) 绑定了自定义 Minecraft 皮肤（Account.CustomSkinPath）时，从皮肤贴图截出正脸；
///   3) 两者都没有——不管是"离线账户还没设置皮肤"还是"选了史蒂夫/艾利克斯默认骨架"，
///      统一显示内置的史蒂夫（Steve）默认头像，不需要下载/依赖任何外部资源。
/// 任何一步读取/解码失败都静默退回下一档，最终兜底一定是史蒂夫头像，不会出现空白头像。
/// </summary>
public sealed class AccountAvatarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Account account) return SteveDefaultAvatar;

        // 第一档：用户导入的头像照片。
        if (!string.IsNullOrWhiteSpace(account.AvatarPhotoPath) && File.Exists(account.AvatarPhotoPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读完并释放文件句柄，避免占用用户原图文件
                bmp.UriSource = new Uri(Path.GetFullPath(account.AvatarPhotoPath));
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { /* 照片损坏/被移动了，落到下一档而不是显示空白 */ }
        }

        // 第二档：自定义 Minecraft 皮肤正脸渐染。
        if (!string.IsNullOrWhiteSpace(account.CustomSkinPath) && File.Exists(account.CustomSkinPath))
        {
            try
            {
                byte[] png = SkinAvatarRenderService.RenderFaceAvatar(File.ReadAllBytes(account.CustomSkinPath), 64);
                using var stream = new MemoryStream(png);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = stream; bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { /* 皮肤文件损坏，落到史蒂夫默认头像 */ }
        }

        // 第三档：没绑皮肤、纯离线账户、或者显式选了史蒂夫/艾利克斯——统一用史蒂夫默认头像。
        return SteveDefaultAvatar;
    }

    /// <summary>内置的史蒂夫(Steve)风格像素头像，8x8，按 Minecraft 原版 Steve 皮肤的配色
    /// （深棕短发、暖棕肤色、深蓝眼睛、棕红嘴唇）手绘近似，不依赖任何外部图片资源，
    /// 交给 XAML 里的 NearestNeighbor 缩放放大后正是 Minecraft 一贯的像素头像质感。</summary>
    private static ImageSource SteveDefaultAvatar => CreateSteveDefault();

    private static ImageSource CreateSteveDefault()
    {
        // 8x8 逐像素配色：0-1 行发际线，两侧一列描边，4 行含眼睛，6 行含嘴，
        // 其余脸部区域用暖棕肤色填充，整体轮廓接近 Minecraft Steve 正脸的经典配色。
        byte hairR = 54, hairG = 38, hairB = 26;     // 深棕色头发
        byte skinR = 221, skinG = 172, skinB = 133;  // 暖棕肤色
        byte eyeR = 56, eyeG = 77, eyeB = 117;       // 深蓝眼睛
        byte mouthR = 139, mouthG = 90, mouthB = 67; // 棕红嘴唇

        var pixels = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var i = (y * 8 + x) * 4;
                byte r, g, b;
                if (y <= 1 || x == 0 || x == 7)
                {
                    r = hairR; g = hairG; b = hairB;          // 顶部头发 + 两侧发际线描边
                }
                else if (y == 4 && (x == 2 || x == 5))
                {
                    r = eyeR; g = eyeG; b = eyeB;              // 眼睛
                }
                else if (y == 6 && x >= 3 && x <= 4)
                {
                    r = mouthR; g = mouthG; b = mouthB;        // 嘴
                }
                else
                {
                    r = skinR; g = skinG; b = skinB;           // 脸部肤色
                }
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 32);
        bmp.Freeze();
        return bmp;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
