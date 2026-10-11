namespace XCL2.App.Models;

public enum AiActivityKind { SubAgent, CodeChange, FileEdit, Command, ReasoningSummary, Reasoning }
public enum AiActivityStatus { Pending, Running, Completed, Failed, Denied, Cancelled }

/// <summary>一次真实操作或供应商公开返回内容的快照；不将普通回答推测为内部执行记录。</summary>
public sealed class AiAssistantActivity
{
    public string? ChangeId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AiActivityKind Kind { get; set; }
    public AiActivityStatus Status { get; set; } = AiActivityStatus.Running;
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public string? FilePath { get; set; }
    public string? BeforeCode { get; set; }
    public string? AfterCode { get; set; }
    public AiFileDiff? FileDiff { get; set; }
    public string? Command { get; set; }
    public string? OriginalCommand { get; set; }
    public string? CommandConversion { get; set; }
    public string? Model { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }

    public AiAssistantActivity Clone()
    {
        var clone = (AiAssistantActivity)MemberwiseClone();
        clone.FileDiff = FileDiff?.Clone();
        return clone;
    }

    public bool IsVisible(AiAssistantConfig config) => Kind switch
    {
        AiActivityKind.SubAgent => config.ShowSubAgents,
        AiActivityKind.CodeChange => config.ShowCodeChanges,
        AiActivityKind.FileEdit => config.ShowEditingFiles,
        AiActivityKind.Command => config.ShowCommands,
        AiActivityKind.ReasoningSummary => config.ShowReasoningSummary,
        AiActivityKind.Reasoning => config.ShowReasoning,
        _ => false
    };
}
