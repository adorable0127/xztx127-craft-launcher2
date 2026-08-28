namespace XCL2.App.Models;

/// <summary>基岩版客户端渠道。跟服务端不同，客户端的正式版/预览版
/// 在 Microsoft Store 中是两个独立的应用包，但版本列表共用同一套来源。""</summary>
public enum BedrockClientChannel
{
    Stable,
    Preview,
}

/// <summary>一个已下载的基岩版客户端实例。</summary>
public class BedrockClientRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户可自定义的显示名，默认用"版本号"拼出来。"</summary>
    public string DisplayName { get; set; } = "";

    public string Version { get; set; } = "";

    public BedrockClientChannel Channel { get; set; } = BedrockClientChannel.Stable;

    /// <summary>安装/解压目录，里面应该能找到 Minecraft.Windows.exe。"</summary>
    public string Directory { get; set; } = "";

    /// <summary>下载的原始文件名（.appx/.msix/.zip），用于识别下载来源。"</summary>
    public string? OriginalFileName { get; set; }

    /// <summary>是否是用户手动选择已有文件夹/exe 导入的（而不是本启动器下载安装的）。
    /// 这类实例只保证能通过"直接跑 exe"这条降级路径启动——正版登录能否完整生效取决于
    /// 这个目录本身有没有正确的 AppxManifest.xml 并成功注册进系统（LaunchClientAsync
    /// 会尝试，失败则回退成直接跑 exe，回退之后账号登录只能停留在离线/Demo 模式，
    /// 见 BedrockPage.xaml.cs 里 BedrockImportExisting_Click 的说明）。</summary>
    public bool IsManuallyImported { get; set; }

    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
}
