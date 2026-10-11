using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Media.Animation;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>原生 WPF 3D：模型网格各面按 Minecraft 64x64 皮肤图集进行 UV 映射。</summary>
public partial class SkinModelViewerDialog : OverlayDialogControl
{
    private readonly Stopwatch _motionClock = new();
    private readonly Dictionary<string, AxisAngleRotation3D> _bones = new();
    // 红色只叠加在模型材质上，不会把背景、按钮和说明文字染红。
    private readonly SolidColorBrush _hurtTint = new(Colors.Red) { Opacity = 0 };
    private readonly ImageBrush _textureBrush;
    private readonly string _fallbackSkinDescription;
    // 包围球覆盖身体和四肢绕关节运动的范围，并给取景留出少量边距。
    private const double CameraTargetY = 1.9;
    private const double ModelFramingRadius = 2.65;
    private const double HurtCycleSeconds = 1.1;
    private const double HurtFlashSeconds = .08;
    private double _yaw, _pitch = 7, _zoom = 1, _lastFrameSeconds;
    private string _activeMotion = "idle";
    private double _motionIntensity;
    private double _motionSpeed = 1;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private int _lastHurtCycle = -1;
    private bool _animationReady, _rendering, _settingsSubscribed, _closing;
    private Window? _ownerWindow;
    private System.Windows.Point? _dragFrom;

    public SkinModelViewerDialog(Account account)
    {
        InitializeComponent();
        // 未设置皮肤/选 Steve 的离线账户使用完整内嵌 PNG，并忽略残留的自定义皮肤路径。
        bool useOfflineDefault = OfflineDefaultSkin.AppliesTo(account);
        bool isAlex = account.Type == AccountType.Offline && account.SkinType == OfflineSkinType.Alex;
        BitmapSource texture = account.Type == AccountType.Offline && !isAlex
            ? OfflineDefaultSkin.LoadBitmap() : BuildDefaultSkin();
        var fallbackDescription = isAlex ? "艾利克斯默认模型" : "史蒂夫默认模型";
        if (!useOfflineDefault && !string.IsNullOrWhiteSpace(account.CustomSkinPath) && File.Exists(account.CustomSkinPath))
        {
            try
            {
                var bmp = LoadBitmapFromFile(account.CustomSkinPath);
                if (bmp.PixelWidth >= 64 && bmp.PixelHeight >= 64)
                {
                    texture = bmp;
                    fallbackDescription = $"本地自定义皮肤（{bmp.PixelWidth}×{bmp.PixelHeight}）";
                }
                else fallbackDescription = $"{(isAlex ? "艾利克斯" : "史蒂夫")}默认模型（本地皮肤尺寸不足 64×64）";
            }
            catch (Exception ex)
            {
                fallbackDescription = $"{(isAlex ? "艾利克斯" : "史蒂夫")}默认模型（本地皮肤读取失败：" + ex.Message + "）";
            }
        }
        _fallbackSkinDescription = fallbackDescription;

        if (account.Type == AccountType.Microsoft)
            SkinSourceText.Text = $"当前账户：{account.Username} · 正在读取微软账户当前皮肤；失败时回退到{_fallbackSkinDescription}。";
        else if (account.Type == AccountType.AuthServer)
            SkinSourceText.Text = $"当前账户：{account.Username} · 正在读取皮肤站当前皮肤；失败时回退到{_fallbackSkinDescription}。";
        else
            SkinSourceText.Text = $"当前账户：{account.Username} · 当前使用{_fallbackSkinDescription}。";

        var group = new Model3DGroup();
        // 每个部件只使用皮肤图集的一部分 UV。默认 RelativeToBoundingBox 会把
        // 整张皮肤重新压进该部件的 UV 边界，导致其它部位和透明留白被贴到身体上。
        // 固定图集在 0..1 的绝对 UV 空间；更换 ImageSource 时也保留这个映射。
        _textureBrush = new ImageBrush(PreparePixelTexture(texture))
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            TileMode = TileMode.None
        };
        RenderOptions.SetBitmapScalingMode(_textureBrush, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(ModelViewport, BitmapScalingMode.NearestNeighbor);
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(_textureBrush));
        material.Children.Add(new EmissiveMaterial(_hurtTint));
        // 坐标：x 左右、y 上下、z 前后；像素 UV 使用原版 Minecraft 标准 64x64 图集。
        AddPart(group, material, "head", 0, 3.14, 0, 1, 1, 1,
            (8, 8, 8, 8), (24, 8, 8, 8), (0, 8, 8, 8), (16, 8, 8, 8), (8, 0, 8, 8), (16, 0, 8, 8), 0, 2.75);
        AddPart(group, material, "body", 0, 1.89, 0, 1, 1.5, .50,
            (20, 20, 8, 12), (32, 20, 8, 12), (16, 20, 4, 12), (28, 20, 4, 12), (20, 16, 8, 4), (28, 16, 8, 4), 0, 1.89);
        AddPart(group, material, "leftArm", -.75, 1.89, 0, .5, 1.5, .5,
            (36, 52, 4, 12), (44, 52, 4, 12), (32, 52, 4, 12), (40, 52, 4, 12), (36, 48, 4, 4), (40, 48, 4, 4), -.75, 2.65);
        AddPart(group, material, "rightArm", .75, 1.89, 0, .5, 1.5, .5,
            (44, 20, 4, 12), (52, 20, 4, 12), (40, 20, 4, 12), (48, 20, 4, 12), (44, 16, 4, 4), (48, 16, 4, 4), .75, 2.65);
        AddPart(group, material, "leftLeg", -.25, .37, 0, .5, 1.5, .5,
            (20, 52, 4, 12), (28, 52, 4, 12), (16, 52, 4, 12), (24, 52, 4, 12), (20, 48, 4, 4), (24, 48, 4, 4), -.25, 1.14);
        AddPart(group, material, "rightLeg", .25, .37, 0, .5, 1.5, .5,
            (4, 20, 4, 12), (12, 20, 4, 12), (0, 20, 4, 12), (8, 20, 4, 12), (4, 16, 4, 4), (8, 16, 4, 4), .25, 1.14);
        ModelViewport.Children.Add(new ModelVisual3D { Content = group });
        ModelRenderHost.Configure(texture, isAlex, account.Username);
        ModelViewport.SizeChanged += (_, _) => UpdateCamera();
        UpdateCamera();

        // 微软账户/皮肤站账户都不再因为"没有本地 CustomSkinPath"就永久显示占位模型。
        // 先立即用本地/默认贴图把窗口画出来，再异步按账户 UUID 获取对应服务器当前公开皮肤
        // 并热替换材质：微软/正版账户走 Mojang 官方 sessionserver；皮肤站(AuthServer)账户
        // 走该账户登录时记录下来的 AuthServerApiRoot，同一套 Yggdrasil 响应格式解析。
        if (account.Type == AccountType.Microsoft)
            _ = LoadMicrosoftSkinAsync(account);
        else if (account.Type == AccountType.AuthServer && !string.IsNullOrWhiteSpace(account.AuthServerApiRoot) && !string.IsNullOrWhiteSpace(account.Uuid))
            _ = LoadAuthServerSkinAsync(account);

        _animationReady = true;
        Loaded += Viewer_Loaded;
        IsVisibleChanged += (_, _) => ApplyAnimationSettings();
        RequestClose += (_, _) =>
        {
            _closing = true;
            StopAnimation();
            ModelRenderHost.SetPluginRenderingEnabled(false);
        };
        Unloaded += Viewer_Unloaded;
        ApplyAnimationSettings();
    }

    private async Task LoadMicrosoftSkinAsync(Account account)
    {
        try
        {
            using var service = new OfficialSkinFetchService();
            OfficialSkinFetchService.OfficialSkinInfo info;
            if (!string.IsNullOrWhiteSpace(account.Uuid))
                info = await service.LookupByUuidAsync(account.Uuid, account.Username);
            else
                info = await service.LookupAsync(account.Username);

            var bytes = await service.DownloadSkinBytesAsync(info);
            var bitmap = LoadBitmapFromBytes(bytes);
            if (bitmap.PixelWidth < 64 || bitmap.PixelHeight < 64)
                throw new InvalidDataException($"服务器返回的皮肤尺寸为 {bitmap.PixelWidth}×{bitmap.PixelHeight}，不足 64×64。");

            var displayTexture = await Task.Run(() => PreparePixelTexture(bitmap));
            await Dispatcher.InvokeAsync(() =>
            {
                _textureBrush.ImageSource = displayTexture;
                ModelRenderHost.SetSkin(bitmap, info.IsSlimModel);
                SkinSourceText.Text = $"当前账户：{account.Username} · 已加载微软账户当前皮肤" +
                                      (info.IsSlimModel ? "（Alex/纤细手臂）" : "（Steve/经典手臂）") + "。";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SkinSourceText.Text = $"当前账户：{account.Username} · 在线皮肤读取失败，已回退到{_fallbackSkinDescription}。{ex.Message}";
            });
        }
    }

    private async Task LoadAuthServerSkinAsync(Account account)
    {
        try
        {
            using var service = new OfficialSkinFetchService();
            var info = await service.LookupByUuidFromYggdrasilAsync(account.AuthServerApiRoot!, account.Uuid, account.Username);
            var bytes = await service.DownloadSkinBytesAsync(info);
            var bitmap = LoadBitmapFromBytes(bytes);
            if (bitmap.PixelWidth < 64 || bitmap.PixelHeight < 64)
                throw new InvalidDataException($"皮肤站返回的皮肤尺寸为 {bitmap.PixelWidth}×{bitmap.PixelHeight}，不足 64×64。");

            var displayTexture = await Task.Run(() => PreparePixelTexture(bitmap));
            await Dispatcher.InvokeAsync(() =>
            {
                _textureBrush.ImageSource = displayTexture;
                ModelRenderHost.SetSkin(bitmap, info.IsSlimModel);
                SkinSourceText.Text = $"当前账户：{account.Username} · 已加载皮肤站当前皮肤" +
                                      (info.IsSlimModel ? "（Alex/纤细手臂）" : "（Steve/经典手臂）") + "。";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SkinSourceText.Text = $"当前账户：{account.Username} · 皮肤站皮肤读取失败，已回退到{_fallbackSkinDescription}。{ex.Message}";
            });
        }
    }

    private static BitmapSource LoadBitmapFromFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(Path.GetFullPath(path));
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource LoadBitmapFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// 用整数倍复制像素，生成供 3D 使用的清晰贴图。只设置 ImageBrush 的
    /// BitmapScalingMode 不足以消除 3D 纹理采样的平滑；先把每个源像素扩成
    /// 同色块，可将采样过渡限制在很窄的边缘。保留原色、透明度和整张图集的 UV。
    /// </summary>
    private static BitmapSource PreparePixelTexture(BitmapSource source)
    {
        // 64x64 -> 2048x2048（32 倍）；128x128 -> 2048x2048（16 倍）。
        // 不缩小高清皮肤；扩展后的单张 BGRA 贴图最多占 16 MiB。
        const int maxExpandedSize = 2048;
        int scale = maxExpandedSize / Math.Max(source.PixelWidth, source.PixelHeight);
        if (scale <= 1) return source;

        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int sourceStride = source.PixelWidth * 4;
        byte[] sourcePixels = new byte[sourceStride * source.PixelHeight];
        bgra.CopyPixels(sourcePixels, sourceStride, 0);

        int width = source.PixelWidth * scale, height = source.PixelHeight * scale;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < source.PixelHeight; y++)
        {
            int sourceRow = y * sourceStride;
            int targetRow = y * scale * stride;
            for (int x = 0; x < source.PixelWidth; x++)
            {
                int src = sourceRow + x * 4;
                int dst = targetRow + x * scale * 4;
                for (int repeat = 0; repeat < scale; repeat++, dst += 4)
                {
                    pixels[dst] = sourcePixels[src];
                    pixels[dst + 1] = sourcePixels[src + 1];
                    pixels[dst + 2] = sourcePixels[src + 2];
                    pixels[dst + 3] = sourcePixels[src + 3];
                }
            }
            // 横向放大一行后直接复制整行，避免逐个重复计算纵向像素。
            for (int row = 1; row < scale; row++)
                Buffer.BlockCopy(pixels, targetRow, pixels, targetRow + row * stride, stride);
        }

        var texture = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        texture.Freeze();
        return texture;
    }

    private void AddPart(Model3DGroup group, Material mat, string bone, double cx, double cy, double cz,
        double width, double height, double depth,
        (int x,int y,int w,int h) front, (int x,int y,int w,int h) back,
        (int x,int y,int w,int h) left, (int x,int y,int w,int h) right,
        (int x,int y,int w,int h) top, (int x,int y,int w,int h) bottom,
        double pivotX, double pivotY)
    {
        var mesh = new MeshGeometry3D();
        double x = cx - width / 2, X = cx + width / 2;
        double y = cy - height / 2, Y = cy + height / 2;
        double z = cz - depth / 2, Z = cz + depth / 2;
        AddFace(mesh, new(x, y, Z), new(X, y, Z), new(X, Y, Z), new(x, Y, Z), front);
        AddFace(mesh, new(X, y, z), new(x, y, z), new(x, Y, z), new(X, Y, z), back);
        AddFace(mesh, new(x, y, z), new(x, y, Z), new(x, Y, Z), new(x, Y, z), left);
        AddFace(mesh, new(X, y, Z), new(X, y, z), new(X, Y, z), new(X, Y, Z), right);
        AddFace(mesh, new(x, Y, Z), new(X, Y, Z), new(X, Y, z), new(x, Y, z), top);
        AddFace(mesh, new(x, y, z), new(X, y, z), new(X, y, Z), new(x, y, Z), bottom);
        var rotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        _bones[bone] = rotation;
        group.Children.Add(new GeometryModel3D(mesh, mat)
        {
            BackMaterial = mat,
            Transform = new RotateTransform3D(rotation, pivotX, pivotY, 0)
        });
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d,
                                (int x,int y,int w,int h) uv)
    {
        int start = mesh.Positions.Count;
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
        double u0 = uv.x / 64.0, v0 = uv.y / 64.0, u1 = (uv.x + uv.w) / 64.0, v1 = (uv.y + uv.h) / 64.0;
        mesh.TextureCoordinates.Add(new Point(u0, v1));
        mesh.TextureCoordinates.Add(new Point(u1, v1));
        mesh.TextureCoordinates.Add(new Point(u1, v0));
        mesh.TextureCoordinates.Add(new Point(u0, v0));
        foreach (int k in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(start + k);
    }

    /// <summary>其他账户原有的简化回退贴图；默认 Steve 离线账户改用 OfflineDefaultSkin
    /// 读取完整的内嵌 Steve PNG，不再使用这里绘制的纯色部件。</summary>
    private static BitmapSource BuildDefaultSkin()
    {
        byte[] pixels = new byte[64 * 64 * 4];
        // 底色先铺一层肤色，避免任何遗漏区域出现全透明/全黑的贴图空洞。
        FillRegion(pixels, 0, 0, 64, 64, 221, 172, 133);

        const byte hairR = 54, hairG = 38, hairB = 26;     // 深棕色头发（跟 2D 头像同一组色值）
        const byte skinR = 221, skinG = 172, skinB = 133;  // 暖棕肤色
        const byte shirtR = 22, shirtG = 137, shirtB = 138; // 史蒂夫经典青色短袖上衣
        const byte pantsR = 45, pantsG = 60, pantsB = 110;  // 深蓝色长裤

        // 头部六个面：整体肤色打底，顶部整面 + 前/后/左/右每个面最上面两行画成头发，
        // 近似史蒂夫的深棕短发轮廓。
        (int x, int y, int w, int h)[] headFaces =
        {
            (8, 8, 8, 8), (24, 8, 8, 8), (0, 8, 8, 8), (16, 8, 8, 8) // front/back/left/right
        };
        foreach (var f in headFaces)
        {
            FillRegion(pixels, f.x, f.y, f.w, f.h, skinR, skinG, skinB);
            FillRegion(pixels, f.x, f.y, f.w, 2, hairR, hairG, hairB); // 发际线
        }
        FillRegion(pixels, 8, 0, 8, 8, hairR, hairG, hairB);   // 头顶（top）整面头发
        FillRegion(pixels, 16, 0, 8, 8, skinR, skinG, skinB);  // 下巴（bottom）保持肤色

        // 身体六个面：整体青色短袖上衣。
        (int x, int y, int w, int h)[] bodyFaces =
        {
            (20, 20, 8, 12), (32, 20, 8, 12), (16, 20, 4, 12), (28, 20, 4, 12), (20, 16, 8, 4), (28, 16, 8, 4)
        };
        foreach (var f in bodyFaces) FillRegion(pixels, f.x, f.y, f.w, f.h, shirtR, shirtG, shirtB);

        // 双臂：整体肤色（史蒂夫是短袖，露出的小臂部分是肤色），顶部两行画出袖口。
        (int x, int y, int w, int h)[] armFaces =
        {
            (36, 52, 4, 12), (44, 52, 4, 12), (32, 52, 4, 12), (40, 52, 4, 12), // leftArm front/back/left/right
            (44, 20, 4, 12), (52, 20, 4, 12), (40, 20, 4, 12), (48, 20, 4, 12), // rightArm front/back/left/right
        };
        foreach (var f in armFaces)
        {
            FillRegion(pixels, f.x, f.y, f.w, f.h, skinR, skinG, skinB);
            FillRegion(pixels, f.x, f.y, f.w, 3, shirtR, shirtG, shirtB); // 短袖袖口
        }
        FillRegion(pixels, 36, 48, 4, 4, shirtR, shirtG, shirtB); // leftArm top（肩部，衣服覆盖）
        FillRegion(pixels, 40, 48, 4, 4, skinR, skinG, skinB);    // leftArm bottom（手掌，肤色）
        FillRegion(pixels, 44, 16, 4, 4, shirtR, shirtG, shirtB); // rightArm top
        FillRegion(pixels, 48, 16, 4, 4, skinR, skinG, skinB);    // rightArm bottom

        // 双腿：整体深蓝色长裤。
        (int x, int y, int w, int h)[] legFaces =
        {
            (20, 52, 4, 12), (28, 52, 4, 12), (16, 52, 4, 12), (24, 52, 4, 12), (20, 48, 4, 4), (24, 48, 4, 4),
            (4, 20, 4, 12), (12, 20, 4, 12), (0, 20, 4, 12), (8, 20, 4, 12), (4, 16, 4, 4), (8, 16, 4, 4)
        };
        foreach (var f in legFaces) FillRegion(pixels, f.x, f.y, f.w, f.h, pantsR, pantsG, pantsB);

        var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        bitmap.Freeze(); return bitmap;
    }

    /// <summary>把 pixels（64×64 Bgra32 平铺数组）里 [x, x+w) × [y, y+h) 这块矩形整体填成
    /// 给定颜色，越界坐标自动裁掉，避免任何一处笔误导致数组越界。</summary>
    private static void FillRegion(byte[] pixels, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(64, x + w), y1 = Math.Min(64, y + h);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int i = (yy * 64 + xx) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
    }

    private void UpdateCamera()
    {
        if (ModelViewport.ActualWidth <= 0 || ModelViewport.ActualHeight <= 0) return;

        // WPF 的 FieldOfView 是水平视角。按视口宽高比求垂直视角，使用较小的
        // 半视角容纳包围球，避免宽弹窗里默认距离过近、脚部被截掉。
        double aspect = ModelViewport.ActualWidth / ModelViewport.ActualHeight;
        double horizontalHalfFov = ModelCamera.FieldOfView * Math.PI / 360;
        double verticalHalfFov = Math.Atan(Math.Tan(horizontalHalfFov) / aspect);
        double fitDistance = ModelFramingRadius / Math.Sin(Math.Min(horizontalHalfFov, verticalHalfFov));
        double distance = fitDistance * _zoom;
        double rad = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        var position = new Point3D(Math.Sin(rad) * distance * Math.Cos(pitch),
                                   CameraTargetY + Math.Sin(pitch) * distance, Math.Cos(rad) * distance * Math.Cos(pitch));
        ModelCamera.Position = position;
        ModelCamera.LookDirection = new Vector3D(-position.X, CameraTargetY-position.Y, -position.Z);
        ModelCamera.UpDirection = new Vector3D(0, 1, 0);
    }
    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(ModelViewport);
        ModelViewport.CaptureMouse();
    }
    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null; ModelViewport.ReleaseMouseCapture();
    }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(ModelViewport);
        _yaw += (p.X - start.X) * .65;
        _pitch = Math.Clamp(_pitch + (start.Y - p.Y) * .45, -65, 65);
        _dragFrom = p;
        UpdateCamera();
    }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom - e.Delta / 120.0 * .075, .5, 1.75);
        UpdateCamera();
    }
    private string CurrentMotion => (MotionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "idle";

    private static double MotionIntensity(string motion) => AnimationSettingsService.Intensity(motion switch
    {
        "attack" => AnimationKind.SkinAttack,
        "hurt" => AnimationKind.SkinHurt,
        _ => AnimationKind.SkinMotion
    });

    private void Viewer_Loaded(object sender, RoutedEventArgs e)
    {
        _closing = false;
        if (!_settingsSubscribed)
        {
            AnimationSettingsService.Changed += AnimationSettings_Changed;
            _settingsSubscribed = true;
        }
        if (_ownerWindow != null) _ownerWindow.StateChanged -= OwnerWindow_StateChanged;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow != null) _ownerWindow.StateChanged += OwnerWindow_StateChanged;
        ApplyAnimationSettings();
    }

    private void Viewer_Unloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
        if (_settingsSubscribed) AnimationSettingsService.Changed -= AnimationSettings_Changed;
        _settingsSubscribed = false;
        if (_ownerWindow != null) _ownerWindow.StateChanged -= OwnerWindow_StateChanged;
        _ownerWindow = null;
    }

    private void OwnerWindow_StateChanged(object? sender, EventArgs e) => ApplyAnimationSettings();

    private void AnimationSettings_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(ApplyAnimationSettings));
            return;
        }
        ApplyAnimationSettings();
    }

    private void MotionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyAnimationSettings();

    private void ApplyAnimationSettings()
    {
        if (!_animationReady) return; // InitializeComponent 会先触发 ComboBox 的初次选择。
        StopAnimation();
        _motionClock.Reset();
        _lastFrameSeconds = 0;
        _lastRenderingTime = TimeSpan.MinValue;
        _lastHurtCycle = -1;
        string motion = _activeMotion = CurrentMotion;
        double intensity = _motionIntensity = MotionIntensity(motion);
        _motionSpeed = AnimationSettingsService.SpeedRatio / .75;
        bool visible = !_closing && IsLoaded && IsVisible && _ownerWindow?.WindowState != WindowState.Minimized;

        // 既有插件接口没有动作幅度/受击滤镜参数。受控动作与降低强度时使用内置
        // 模型，防止第三方自有渲染循环绕过“关闭动画”；正常默认展示恢复插件。
        ModelRenderHost.SetPluginRenderingEnabled(visible && intensity >= 1 && motion != "attack" && motion != "hurt");
        RenderBuiltInFrame(motion, 0, visible ? intensity : 0);
        if (!visible || intensity <= 0) return;
        _motionClock.Start();
        CompositionTarget.Rendering += Animation_Rendering;
        _rendering = true;
    }

    private void StopAnimation()
    {
        if (_rendering) CompositionTarget.Rendering -= Animation_Rendering;
        _rendering = false;
        _motionClock.Stop();
        _hurtTint.BeginAnimation(Brush.OpacityProperty, null);
        _hurtTint.Opacity = 0;
    }

    private void Animation_Rendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = frame.RenderingTime;
        }
        double seconds = _motionClock.Elapsed.TotalSeconds;
        double elapsed = Math.Max(0, seconds - _lastFrameSeconds);
        _lastFrameSeconds = seconds;
        string motion = _activeMotion;
        // 75% 保持原有节奏，100% 播放快 1/3；幅度由独立的单类强度控制。
        // 受击始终用实际秒计时，80 ms 红色滤镜不随速度设置被拉长。
        double speed = motion == "hurt" ? 1 : _motionSpeed;
        if (ModelRenderHost.RenderFrame(motion, TimeSpan.FromSeconds(elapsed * speed))) return;
        RenderBuiltInFrame(motion, seconds * speed, _motionIntensity);
    }

    private void RenderBuiltInFrame(string motion, double seconds, double intensity)
    {
        double gait = Math.Sin(seconds * (motion == "sprint" ? 7.5 : 4.5));
        double amplitude = (motion == "sprint" ? 50 : motion == "walk" ? 30 : 0) * intensity;
        _bones["leftArm"].Angle = amplitude * gait;
        _bones["rightArm"].Angle = -amplitude * gait;
        _bones["leftLeg"].Angle = -amplitude * gait;
        _bones["rightLeg"].Angle = amplitude * gait;
        _bones["body"].Angle = 0;
        _bones["head"].Angle = Math.Sin(seconds * 1.35) * intensity;

        if (motion == "attack")
        {
            // 24° 的平滑挥臂替代原来的 -90° ± 75°；每次挥击后自然回到站姿。
            double phase = seconds % 1.05;
            double swing = phase < .48 ? Math.Pow(Math.Sin(Math.PI * phase / .48), 2) * intensity : 0;
            _bones["rightArm"].Angle = -24 * swing;
            _bones["leftArm"].Angle = -3 * swing;
            _bones["body"].Angle = 2 * swing;
        }
        else if (motion == "hurt")
        {
            double phase = seconds % HurtCycleSeconds;
            double recoil = phase < .28 ? Math.Pow(Math.Sin(Math.PI * phase / .28), 2) * intensity : 0;
            _bones["leftArm"].Angle = -8 * recoil;
            _bones["rightArm"].Angle = -8 * recoil;
            _bones["head"].Angle = -4 * recoil;
            _bones["body"].Angle = 3 * recoil;
            int cycle = (int)(seconds / HurtCycleSeconds);
            if (cycle != _lastHurtCycle)
            {
                _lastHurtCycle = cycle;
                _hurtTint.BeginAnimation(Brush.OpacityProperty, null);
                _hurtTint.Opacity = 0;
                // 用独立动画时钟归零：即使 UI 帧很晚，也不会把红色留到下一次帧回调。
                // 迟到超过 80 ms 的帧直接略过闪光，绝不补播/延长受击滤镜。
                if (phase < HurtFlashSeconds && intensity > 0)
                    _hurtTint.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(
                        .10 * intensity * (1 - phase / HurtFlashSeconds), 0,
                        TimeSpan.FromSeconds(HurtFlashSeconds - phase)) { FillBehavior = FillBehavior.Stop });
            }
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
