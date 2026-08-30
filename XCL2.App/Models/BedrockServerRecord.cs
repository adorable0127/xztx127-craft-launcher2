namespace XCL2.App.Models;

/// <summary>基岩版服务端下载渠道。正式版(Stable)和预览版(Preview)是官方两条独立的分发线，
/// 版本号也不互通——这是目前公开分发方式下唯一能选择的"版本"维度（BDS 没有公开的历史版本
/// 归档，官方接口只给"当前最新的正式版"和"当前最新的预览版"这两个，见 BedrockContentService
/// 类头注释）。</summary>
public enum BdsChannel
{
    Stable,
    Preview,
}

/// <summary>一个已下载安装的基岩版服务端实例。</summary>
public class BedrockServerRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>用户可自定义的显示名，默认用"版本号 (渠道)"拼出来，用户可在列表里改。</summary>
    public string DisplayName { get; set; } = "";

    public string Version { get; set; } = "";

    public BdsChannel Channel { get; set; } = BdsChannel.Stable;

    /// <summary>安装目录，里面直接是 bedrock_server.exe。这是用户选的目录（或者
    /// AppConfig.BedrockServerDefaultDownloadDir 默认目录），不是启动器写死的固定路径。</summary>
    public string Directory { get; set; } = "";

    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
}
