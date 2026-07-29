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
    /// 确保 authlib-injector.jar 已经下载到本地，返回其路径。已存在则直接复用，不重复下载。
    /// 使用官方发布的最新版下载地址（authlib-injector 项目本身的固定入口，会自动跳转到最新版本）。
    /// </summary>
    public async Task<string> EnsureAuthlibInjectorAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(AuthlibInjectorPath) && new FileInfo(AuthlibInjectorPath).Length > 0)
            return AuthlibInjectorPath;

        Directory.CreateDirectory(App.DataDir);
        progress?.Report(new ProgressInfo("下载万能皮肤补丁", 0, 1, "正在获取 authlib-injector 最新版本信息..."));

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

        progress?.Report(new ProgressInfo("下载万能皮肤补丁", 0, 1, "正在下载 authlib-injector.jar..."));
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

        progress?.Report(new ProgressInfo("下载万能皮肤补丁", 1, 1, "完成"));
        return AuthlibInjectorPath;
    }

    /// <summary>
    /// 为需要自定义皮肤的离线账户构造额外的 JVM 参数（-javaagent 挂载 authlib-injector，
    /// 并指定皮肤服务 API Root）。只有 SkinType=Custom 的离线账户才需要这个；史蒂夫/艾利克斯
    /// 是原版内置骨架，不需要任何额外参数。调用方应该在启动前调用
    /// <see cref="EnsureAuthlibInjectorAsync"/> 确保 jar 已存在，再调用这个方法拼参数。
    /// </summary>
    public List<string> BuildSkinJvmArgs(Account account, string apiRoot)
    {
        if (account.Type != AccountType.Offline || account.SkinType != OfflineSkinType.Custom)
            return new List<string>();
        if (!File.Exists(AuthlibInjectorPath))
            return new List<string>();

        return new List<string>
        {
            $"-javaagent:{AuthlibInjectorPath}={apiRoot}",
            "-Dauthlibinjector.side=client"
        };
    }
}
