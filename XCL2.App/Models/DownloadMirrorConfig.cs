namespace XCL2.App.Models;

/// <summary>下载镜像只承接公开资源；原始带凭据/签名的链接始终直连。</summary>
public sealed class DownloadMirrorConfig
{
    public bool EnableCommunityMirrors { get; set; } = true;
    public bool EnableGithubMirrors { get; set; } = true;
    public List<DownloadMirrorRule> CustomRules { get; set; } = new();
}

public sealed class DownloadMirrorRule
{
    public string Name { get; set; } = "";
    public string SourcePrefix { get; set; } = "";
    public string MirrorPrefix { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
