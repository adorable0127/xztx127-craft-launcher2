namespace XCL2.App.Models;

public enum AiFileChangeKind { Created, Modified, Deleted }
public enum AiDiffLineKind { Context, Added, Deleted, Modified, Omitted }

/// <summary>实际文件操作的有限文本差异；与活动一起保存，重新打开会话仍可展开。</summary>
public sealed class AiFileDiff
{
    public AiFileChangeKind ChangeKind { get; set; }
    public List<AiDiffLine> Lines { get; set; } = new();
    public int AddedLines { get; set; }
    public int DeletedLines { get; set; }
    public int ModifiedLines { get; set; }
    public bool IsTruncated { get; set; }
    public string Notice { get; set; } = "";

    public AiFileDiff Clone() => new()
    {
        ChangeKind = ChangeKind, AddedLines = AddedLines, DeletedLines = DeletedLines,
        ModifiedLines = ModifiedLines, IsTruncated = IsTruncated, Notice = Notice,
        Lines = Lines.Select(line => line.Clone()).ToList()
    };
}

public sealed class AiDiffLine
{
    public AiDiffLineKind Kind { get; set; }
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
    public string BeforeText { get; set; } = "";
    public string AfterText { get; set; } = "";
    public AiDiffLine Clone() => (AiDiffLine)MemberwiseClone();
}
