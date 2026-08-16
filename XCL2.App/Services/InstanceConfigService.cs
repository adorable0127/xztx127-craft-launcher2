using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 每个版本（实例）的单独设置 + 启动脚本，统一保存在该实例目录下的 <c>xcl/</c> 子目录：
/// <c>&lt;.minecraft&gt;/versions/&lt;版本id&gt;/xcl/settings.json</c>（单独设置）
/// <c>&lt;.minecraft&gt;/versions/&lt;版本id&gt;/xcl/launch.bat</c>（该实例的启动脚本）
///
/// 跟全局 xcl2/config.json 是两套完全独立的存储：xcl2/config.json 放"跟哪个具体版本都无关"
/// 的启动器全局设置，这里放"只对这一个版本生效"的设置——两者已有的
/// VersionIsolationOverrides/VersionJavaOverrides 等字典式覆盖（key=版本id，存在全局
/// config.json 里）不受这次改动影响，继续按原样工作；<see cref="InstanceSettings"/> 是
/// 在此基础上新增的、真正物理落在实例文件夹自己目录里的设置文件，两者可以并存
/// （字典式覆盖负责"全局配置里对某个版本的覆盖项"，这里负责"版本自己带着走、
/// 复制/打包/分享这个版本文件夹时也会一起带走"的设置）。
///
/// 每次读取为主：正常情况下只读，不主动写入——除非 (a) 用户在界面上手动修改了某个实例设置，
/// 或 (b) 首次打开这个实例且 xcl/settings.json 不存在（此时创建目录并写入默认配置）。
/// 见 <see cref="LoadOrCreateDefault"/>。
/// </summary>
public static class InstanceConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>实例的 xcl/ 子目录路径。</summary>
    public static string GetXclDir(string versionDir) => Path.Combine(versionDir, "xcl");

    /// <summary>实例单独设置文件路径：&lt;versionDir&gt;/xcl/settings.json。</summary>
    public static string GetSettingsPath(string versionDir) => Path.Combine(GetXclDir(versionDir), "settings.json");

    /// <summary>该实例导出的启动脚本路径：&lt;versionDir&gt;/xcl/launch.bat。</summary>
    public static string GetLaunchScriptPath(string versionDir) => Path.Combine(GetXclDir(versionDir), "launch.bat");

    /// <summary>
    /// 读取一个实例的单独设置。文件不存在（首次打开这个实例）时，创建 xcl/ 目录并写入一份
    /// 默认配置后返回；文件存在但读取/反序列化失败（内容损坏）时，记录日志并同样返回一份
    /// 默认配置，但**不会**覆盖磁盘上损坏的文件——保留原始内容以便用户或以后的工具排查，
    /// 只在内存里用默认值兜底，避免因为一个实例的设置文件损坏导致整个启动器崩溃。
    /// </summary>
    public static InstanceSettings LoadOrCreateDefault(string versionDir)
    {
        var path = GetSettingsPath(versionDir);
        if (!File.Exists(path))
        {
            var fresh = new InstanceSettings();
            try
            {
                Directory.CreateDirectory(GetXclDir(versionDir));
                File.WriteAllText(path, JsonSerializer.Serialize(fresh, JsonOpts));
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogFallback($"创建实例默认设置失败：{path}", ex);
            }
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<InstanceSettings>(json) ?? new InstanceSettings();
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"读取实例设置失败，本次运行使用默认值（不覆盖磁盘原文件）：{path}", ex);
            return new InstanceSettings();
        }
    }

    /// <summary>只读探测：文件存在就读，不存在就返回 null，不创建任何文件/目录。
    /// 给"扫描全部实例做批量导出"这类场景用，避免仅仅因为遍历就意外把默认配置
    /// 写进每一个从未被用户真正打开过的版本文件夹。</summary>
    public static InstanceSettings? TryLoad(string versionDir)
    {
        var path = GetSettingsPath(versionDir);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用户手动修改了某个实例设置时调用：写回 xcl/settings.json。
    /// 这是"每次读取为主，除非用户手动修改设置"里"手动修改"这条分支唯一的写入入口。</summary>
    public static void Save(string versionDir, InstanceSettings settings)
    {
        Directory.CreateDirectory(GetXclDir(versionDir));
        File.WriteAllText(GetSettingsPath(versionDir), JsonSerializer.Serialize(settings, JsonOpts));
    }
}

/// <summary>
/// 单个版本（实例）的独立设置。字段全部可为 null/未设置，表示"这一项跟随全局
/// xcl2/config.json 里的默认值"，只有显式赋值过的字段才代表用户对这个实例做过单独覆盖。
/// 这跟全局 AppConfig 里 VersionXxxOverrides 字典（key=版本id）承载的是同一类"覆盖"语义，
/// 只是这里改为物理存放在实例自己的文件夹里，具体取舍见本文件类头注释。
/// </summary>
public class InstanceSettings
{
    /// <summary>是否启用版本隔离；null=跟随全局默认。</summary>
    public bool? IsolateVersion { get; set; }

    /// <summary>这个实例使用的 Java 列表条目 Id；null=跟随全局/自动探测。</summary>
    public string? JavaId { get; set; }

    /// <summary>这个实例的最小内存(MB)；null=跟随全局设置。</summary>
    public int? MinMemoryMb { get; set; }

    /// <summary>这个实例的最大内存(MB)；null=跟随全局设置。</summary>
    public int? MaxMemoryMb { get; set; }

    /// <summary>这个实例单独的 JVM 自定义参数；null=跟随全局 CustomJvmArgs（不覆盖，
    /// 而是在全局参数基础上追加——具体拼接顺序见 LauncherService）。</summary>
    public string? CustomJvmArgs { get; set; }

    /// <summary>开启后自动加入的服务器地址；null/空=不自动进服务器。</summary>
    public string? AutoJoinServerAddress { get; set; }

    /// <summary>这个实例最后一次成功启动的时间（UTC），仅用于展示，不参与任何启动逻辑判断。</summary>
    public DateTime? LastLaunchedAtUtc { get; set; }
}
