using System.IO;
using System.Net.Http;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 离线皮肤管理：
/// - 史蒂夫(Steve)/艾利克斯(Alex) 是内置默认骨架，不需要下载任何东西——原版客户端本来就自带
///   这两套皮肤资源，选中它们只是把账户的"默认外观"标记清楚，方便未来切换/展示，不需要额外文件。
/// - 自定义(Custom) 皮肤需要用户上传一张符合 Minecraft 皮肤规范的 PNG，启动器把它复制进
///   xcl2/skins/&lt;accountId&gt;.png 保存；但离线模式下光有本地文件不够——原版客户端的皮肤是从
///   Mojang/Microsoft 会话服务器按 UUID 查询的，离线账户的 UUID 根本查不到任何皮肤，必须借助
///   "万能皮肤补丁"(authlib-injector，社区事实标准方案，PCL/HMCL 等主流启动器都用这个方案)
///   把 Minecraft 认证服务的地址替换成一个可以返回自定义皮肤的第三方/自建服务，客户端才能真正
///   显示出自定义皮肤。这里选用的是公开的 ely.by 皮肤服务作为默认皮肤源（不需要用户自己搭服务器），
///   用户如果有自己的皮肤站也可以在设置里替换成别的 authlib-injector API Root。
/// </summary>
public class SkinService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public string SkinsDir { get; } = Path.Combine(App.DataDir, "skins");

    /// <summary>用户导入的"头像照片"存放目录，跟 Minecraft 皮肤（SkinsDir）分开存放——
    /// 头像照片是任意图片格式（jpg/png/bmp/webp 都行），不需要满足皮肤的尺寸规范，
    /// 混在一起容易让人以为它们是同一种东西。</summary>
    public string AvatarsDir { get; } = Path.Combine(App.DataDir, "avatars");

    /// <summary>authlib-injector jar 的本地缓存路径（下载一次后长期复用，不用每次启动都重新下载）。</summary>
    public string AuthlibInjectorPath { get; } = Path.Combine(App.DataDir, "authlib-injector.jar");

    /// <summary>
    /// 默认皮肤服务 API Root：ely.by 是一个面向离线/自建账户的公开皮肤托管服务，
    /// 主流第三方启动器(HMCL 等)也内置了它作为"万能皮肤"的默认选项，不需要用户自己搭建服务器。
    /// 如果用户有自己的皮肤站（比如自建的 authlib-injector 兼容服务），可以在设置里手动替换这个地址。
    /// </summary>
    public const string DefaultSkinApiRoot = "https://authlib-injector.yushi.moe/authlib-injector/api";

    /// <summary>
    /// 保存一张用户上传的自定义皮肤图片，复制进 xcl2/skins/&lt;accountId&gt;.png。
    /// 不在这里做图片格式/尺寸校验（皮肤规范允许 64x32 旧格式和 64x64 新格式两种，
    /// 简单粗暴地全盘接受用户提供的 PNG，交给游戏本体在渲染时自己容错）。
    /// </summary>
    public string SaveCustomSkin(string accountId, string sourcePngPath)
    {
        Directory.CreateDirectory(SkinsDir);
        var destPath = Path.Combine(SkinsDir, $"{accountId}.png");
        File.Copy(sourcePngPath, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>删除一个账户已保存的自定义皮肤文件（账户被移除，或用户改选史蒂夫/艾利克斯时清理）。</summary>
    public void RemoveCustomSkin(string accountId)
    {
        var path = Path.Combine(SkinsDir, $"{accountId}.png");
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 删除失败不影响主流程，残留一个文件不会造成功能性问题 */ }
    }

    /// <summary>
    /// 保存一张用户导入的头像照片，复制进 xcl2/avatars/&lt;accountId&gt;.&lt;原始扩展名&gt;。
    /// 跟 SaveCustomSkin 同理，不做格式/尺寸校验——WPF 的 BitmapImage 能直接解码常见格式，
    /// 显示时按控件尺寸拉伸铺满即可，不需要启动器自己裁剪/缩放。换一张新照片会先删掉这个
    /// 账户名下所有旧扩展名的文件，避免用户来回换 png/jpg 时旧文件一直堆在目录里。
    /// </summary>
    public string SaveAvatarPhoto(string accountId, string sourceImagePath)
    {
        Directory.CreateDirectory(AvatarsDir);
        RemoveAvatarPhoto(accountId);
        var ext = Path.GetExtension(sourceImagePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        var destPath = Path.Combine(AvatarsDir, $"{accountId}{ext}");
        File.Copy(sourceImagePath, destPath, overwrite: true);
        return destPath;
    }

    /// <summary>删除一个账户已保存的头像照片（不管之前保存的是什么扩展名都一并清掉）。</summary>
    public void RemoveAvatarPhoto(string accountId)
    {
        try
        {
            if (!Directory.Exists(AvatarsDir)) return;
            foreach (var file in Directory.GetFiles(AvatarsDir, $"{accountId}.*"))
                File.Delete(file);
        }
        catch { /* 删除失败不影响主流程，残留文件不会造成功能性问题 */ }
    }

    /// <summary>
    /// 确保 authlib-injector.jar 已经下载到本地，返回其路径。已存在则直接复用，不重复下载。
    /// 使用官方发布的最新版下载地址（authlib-injector 项目本身的固定入口，会自动跳转到最新版本）。
    /// </summary>
    public async Task<string> EnsureAuthlibInjectorAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(AuthlibInjectorPath) && new FileInfo(AuthlibInjectorPath).Length > 0)
            return AuthlibInjectorPath;

        Directory.CreateDirectory(App.DataDir);
        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Customskinloader", "下载万能皮肤补丁"), 0, 1, "正在获取 authlib-injector 最新版本信息..."));

        // 官方 API：GET https://authlib-injector.yushi.moe/artifacts.json 返回最新构建信息
        var infoJson = await _http.GetStringAsync("https://authlib-injector.yushi.moe/artifacts.json", ct);
        using var doc = JsonDocument.Parse(infoJson);
        var latest = doc.RootElement.GetProperty("latest");
        var buildNumber = latest.GetProperty("build_number").GetInt32();
        var downloadUrl = doc.RootElement.GetProperty("artifacts")
            .EnumerateArray()
            .First(a => a.GetProperty("build_number").GetInt32() == buildNumber)
            .GetProperty("download_url").GetString();

        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException("未能获取 authlib-injector 的下载地址。");

        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Customskinloader", "下载万能皮肤补丁"), 0, 1, "正在下载 authlib-injector.jar..."));
        using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载 authlib-injector 失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var tempPath = AuthlibInjectorPath + ".tmp";
        await using (var fs = File.Create(tempPath))
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        {
            await stream.CopyToAsync(fs, ct);
        }
        File.Move(tempPath, AuthlibInjectorPath, overwrite: true);

        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Customskinloader", "下载万能皮肤补丁"), 1, 1, Loc.T("Str_Common_Finish", "完成")));
        return AuthlibInjectorPath;
    }

    /// <summary>
    /// 为需要自定义皮肤的离线账户构造额外的 JVM 参数（-javaagent 挂载 authlib-injector，
    /// 并指定皮肤服务 API Root）。史蒂夫/艾利克斯这种内置骨架也统一挂上（见下面 BuildSkinJvmArgs
    /// 的说明——不只是为了皮肤，也是为了修复离线账户在部分版本里"多人游戏被禁用"的问题）。
    /// 调用方应该在启动前调用 <see cref="EnsureAuthlibInjectorAsync"/> 确保 jar 已存在，
    /// 再调用这个方法拼参数。
    /// </summary>
    /// <summary>
    /// 为需要挂载 authlib-injector 的账户构造额外的 JVM 参数：
    /// - 离线账户（不分是否自定义皮肤）：用全局默认/用户配置的皮肤源 apiRoot。
    ///   之前这里只处理 SkinType=Custom 的账户，理由是"史蒂夫/艾利克斯不需要皮肤补丁"——
    ///   这个理由本身没错，但漏算了一件事：不挂 authlib-injector 时，离线账户的
    ///   --accessToken 是假的占位值("0")，Minecraft 客户端在部分版本（实测 1.16.5 复现，
    ///   1.20.1 不复现）会拿这个假 token 去请求 Mojang/Xbox 的多人游戏资格校验接口，
    ///   请求失败后客户端直接把"多人游戏"按钮禁用，提示"请检查你的 Microsoft 账户设置"——
    ///   这不是 XCL2 传的启动参数有问题，是 Minecraft 自己发起的在线校验请求失败了，
    ///   而且不同版本对"校验失败"这件事的处理不一致（有的版本失败后放行，有的直接禁用）。
    ///   authlib-injector 会把 Yggdrasil 相关的网络请求（登录/会话/多人游戏资格）都改指向
    ///   配置的皮肤站 API Root，只要皮肤站给出"这是个正常账户"的合法响应，游戏就不会再去问
    ///   真正的 Mojang/Xbox 接口，多人游戏资格校验自然就通过了——这也是 PCL2/HMCL 等主流
    ///   第三方启动器的通行做法：离线账户统一挂 authlib-injector，不只是为了皮肤。
    /// - 认证服务器(AuthServer)账户：用这个账户登录时使用的 AuthServerApiRoot（忽略传入的 apiRoot 参数，
    ///   因为认证服务器账户的皮肤/会话校验必须对应它登录的那个服务器，不能用全局默认源）。
    /// 调用方应该在启动前调用 <see cref="EnsureAuthlibInjectorAsync"/> 确保 jar 已存在，再调用这个方法拼参数。
    /// </summary>
    public List<string> BuildSkinJvmArgs(Account account, string apiRoot)
    {
        if (!File.Exists(AuthlibInjectorPath))
            return new List<string>();

        if (account.Type == AccountType.AuthServer && !string.IsNullOrWhiteSpace(account.AuthServerApiRoot))
        {
            return new List<string>
            {
                $"-javaagent:{AuthlibInjectorPath}={account.AuthServerApiRoot}",
                "-Dauthlibinjector.side=client"
            };
        }

        if (account.Type != AccountType.Offline)
            return new List<string>();

        return new List<string>
        {
            $"-javaagent:{AuthlibInjectorPath}={apiRoot}",
            "-Dauthlibinjector.side=client"
        };
    }
}
