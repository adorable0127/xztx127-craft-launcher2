using System.IO;
using System.Windows.Media.Imaging;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>离线默认 Steve 的完整皮肤与头像共用同一内嵌 PNG，不查询同名正版角色。</summary>
internal static class OfflineDefaultSkin
{
    private const string ResourceName = "XCL2.App.Resources.Skins.steve.png";
    private static BitmapSource? _bitmap;
    private static BitmapSource? _avatar;

    internal static bool AppliesTo(Account account) => account.Type == AccountType.Offline
        && account.SkinType is OfflineSkinType.None or OfflineSkinType.Steve;

    internal static BitmapSource LoadBitmap()
    {
        if (_bitmap != null) return _bitmap;
        using var stream = OpenResource();
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames[0];
        bitmap.Freeze();
        _bitmap = bitmap;
        return bitmap;
    }

    internal static BitmapSource LoadAvatar()
    {
        if (_avatar != null) return _avatar;
        using var resource = OpenResource();
        using var skin = new MemoryStream();
        resource.CopyTo(skin);
        var png = SkinAvatarRenderService.RenderFaceAvatar(skin.ToArray(), 64);
        using var avatar = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(avatar, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames[0];
        bitmap.Freeze();
        _avatar = bitmap;
        return bitmap;
    }

    private static Stream OpenResource() => typeof(OfflineDefaultSkin).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new FileNotFoundException("找不到内嵌的离线默认 Steve 皮肤资源。", ResourceName);
}
