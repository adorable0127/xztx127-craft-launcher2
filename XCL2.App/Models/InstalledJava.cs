namespace XCL2.App.Models;

/// <summary>
/// "Java 列表"里的一条记录：一个用户手动登记(或由下载/扫描自动登记)的 Java 运行时。
/// 客户端每个版本、每个服务器实例都可以从这个列表里单独选择要用哪一个，
/// 从而支持多个 Java 版本在本机同时共存、按需使用，而不是全局只能配置一份。
///
/// 存放位置：AppConfig.InstalledJavas，随 xcl2/config.json 一起持久化。
/// </summary>
public class InstalledJava
{
    /// <summary>唯一 ID，版本/服务器实例的选择通过这个 ID 引用，不直接存路径——
    /// 这样用户以后修正/移动这条记录的路径时，所有引用它的地方自动跟着更新。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户可自定义的显示名称，例如 "Java 21 (推荐)"、"Java 8 (老版本兼容)"。
    /// 新增时默认根据探测到的版本自动生成一个名字，用户可以改成自己好记的名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>javaw.exe 的完整路径。</summary>
    public string JavawPath { get; set; } = "";

    /// <summary>探测到的主版本号(如 8/17/21/25)，添加时通过 "java -version" 实测得到；
    /// 探测失败时为 null，列表里会展示成"版本未知"，仍然可以被手动选用。</summary>
    public int? MajorVersion { get; set; }

    /// <summary>添加这条记录的方式，仅用于展示来源，不影响功能：Manual=手动浏览选择，
    /// Downloaded=启动器下载安装，Scanned=全盘扫描找到后添加。</summary>
    public string Source { get; set; } = "Manual";
}
