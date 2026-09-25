using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>原生 WPF 3D：模型网格各面按 Minecraft 64x64 皮肤图集进行 UV 映射。</summary>
public partial class SkinModelViewerDialog : OverlayDialogControl
{
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Dictionary<string, AxisAngleRotation3D> _bones = new();
    private readonly ImageBrush _textureBrush;
    private readonly string _fallbackSkinDescription;
    private double _yaw, _pitch = 7, _distance = 8, _tick;
    private System.Windows.Point? _dragFrom;

    public SkinModelViewerDialog(Account account)
    {
        InitializeComponent();
        // 默认贴图：离线账户没有设置任何皮肤（SkinType.None，包括还没来得及选过的全新离线
        // 账户），或者显式选了"史蒂夫"，都应该看到内置的史蒂夫模型，而不是一个跟史蒂夫/
        // 艾利克斯都不像的抽象配色示意方块——2D 头像（AccountAvatarConverter）早就是这个
        // 优先级和这个兜底了，这里的 3D 纸娃娃之前没跟上，各自维护了一套不一致的"默认皮肤"。
        bool isAlex = account.Type == AccountType.Offline && account.SkinType == OfflineSkinType.Alex;
        BitmapSource texture = BuildDefaultSkin();
        var fallbackDescription = isAlex ? "艾利克斯默认模型" : "史蒂夫默认模型";
        if (!string.IsNullOrWhiteSpace(account.CustomSkinPath) && File.Exists(account.CustomSkinPath))
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
        _textureBrush = new ImageBrush(texture) { Stretch = Stretch.Fill };
        var material = new DiffuseMaterial(_textureBrush);
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
        UpdateCamera();

        // 微软账户/皮肤站账户都不再因为"没有本地 CustomSkinPath"就永久显示占位模型。
        // 先立即用本地/默认贴图把窗口画出来，再异步按账户 UUID 获取对应服务器当前公开皮肤
        // 并热替换材质：微软/正版账户走 Mojang 官方 sessionserver；皮肤站(AuthServer)账户
        // 走该账户登录时记录下来的 AuthServerApiRoot，同一套 Yggdrasil 响应格式解析。
        if (account.Type == AccountType.Microsoft)
            _ = LoadMicrosoftSkinAsync(account);
        else if (account.Type == AccountType.AuthServer && !string.IsNullOrWhiteSpace(account.AuthServerApiRoot) && !string.IsNullOrWhiteSpace(account.Uuid))
            _ = LoadAuthServerSkinAsync(account);

        _animation.Tick += Animation_Tick;
        _animation.Start();
        RequestClose += (_, _) => _animation.Stop();
        Unloaded += (_, _) => _animation.Stop();
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

            await Dispatcher.InvokeAsync(() =>
            {
                _textureBrush.ImageSource = bitmap;
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

            await Dispatcher.InvokeAsync(() =>
            {
                _textureBrush.ImageSource = bitmap;
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

    /// <summary>没有任何本地/在线皮肤可用时的内置默认贴图：画的是真正的"史蒂夫"配色
    /// （暖棕肤色 + 深棕短发 + 青色短袖 + 深蓝裤子），而不是一堆跟史蒂夫/艾利克斯都对不上号
    /// 的抽象色块——只把 AddPart 里实际会用到的那几块 UV 矩形（头/身体/双臂/双腿的六个面）
    /// 按部位刷成对应颜色即可，其余没被任何面采样到的贴图区域不影响渲染结果。
    /// 颜色跟 AccountAvatarConverter.CreateSteveDefault 里 2D 头像用的史蒂夫肤色/发色保持
    /// 完全一致，确保"账户"页头像小图标和这里的 3D 纸娃娃是同一个"史蒂夫"，不是各画各的。</summary>
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
        double rad = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        var position = new Point3D(Math.Sin(rad) * _distance * Math.Cos(pitch),
                                   1.9 + Math.Sin(pitch) * _distance, Math.Cos(rad) * _distance * Math.Cos(pitch));
        ModelCamera.Position = position;
        ModelCamera.LookDirection = new Vector3D(-position.X, 1.9-position.Y, -position.Z);
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
        _distance = Math.Clamp(_distance - e.Delta / 120.0 * .6, 4, 14);
        UpdateCamera();
    }
    private void MotionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => _tick = 0;
    private void Animation_Tick(object? sender, EventArgs e)
    {
        _tick += .09;
        string mode = (MotionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "idle";
        double gait = Math.Sin(_tick * (mode == "sprint" ? 3.4 : 2.0));
        double amplitude = mode == "sprint" ? 75 : mode == "walk" ? 38 : 0;
        _bones["leftArm"].Angle = amplitude * gait;
        _bones["rightArm"].Angle = -amplitude * gait;
        _bones["leftLeg"].Angle = -amplitude * gait;
        _bones["rightLeg"].Angle = amplitude * gait;
        _bones["head"].Angle = Math.Sin(_tick * .6) * 2;
        if (mode == "attack") _bones["rightArm"].Angle = -90 + 75 * Math.Sin(_tick * 2);
        if (mode == "hurt")
        {
            _bones["leftArm"].Angle = -40 + 10 * Math.Sin(_tick * 3);
            _bones["rightArm"].Angle = -40 + 10 * Math.Sin(_tick * 3);
            _bones["head"].Angle = -14 + 10 * Math.Sin(_tick * 3);
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
