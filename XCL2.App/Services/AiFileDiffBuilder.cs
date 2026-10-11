using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>有界逐行差异：小区间使用 LCS；极大区间只显示明确的删除/新增，不猜测相同行。</summary>
public static class AiFileDiffBuilder
{
    public const int MaxSnapshotChars = 16000;
    private const int MaxLines = 1200;
    private const int MaxCells = 600000;
    private const int MaxVisibleLines = 500;

    public static AiFileDiff Build(string? before, string? after, AiFileChangeKind kind,
        bool sourceTruncated = false, string? unavailableReason = null)
    {
        var result = new AiFileDiff { ChangeKind = kind, IsTruncated = sourceTruncated };
        if (unavailableReason != null)
        {
            result.Notice = unavailableReason;
            return result;
        }
        var oldLines = Split(before, result);
        var newLines = Split(after, result);
        var raw = new List<AiDiffLine>();
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
        {
            raw.Add(Line(AiDiffLineKind.Context, prefix, prefix, oldLines[prefix], newLines[prefix]));
            prefix++;
        }
        var oldEnd = oldLines.Length;
        var newEnd = newLines.Length;
        while (oldEnd > prefix && newEnd > prefix && oldLines[oldEnd - 1] == newLines[newEnd - 1])
        { oldEnd--; newEnd--; }

        int oldCount = oldEnd - prefix, newCount = newEnd - prefix;
        if ((long)(oldCount + 1) * (newCount + 1) > MaxCells)
        {
            for (int i = prefix; i < oldEnd; i++) raw.Add(Line(AiDiffLineKind.Deleted, i, null, oldLines[i], ""));
            for (int i = prefix; i < newEnd; i++) raw.Add(Line(AiDiffLineKind.Added, null, i, "", newLines[i]));
            result.Notice = "差异区间较大：此区间按删除与新增显示，未进行逐行替换匹配。";
        }
        else
        {
            var lcs = new int[oldCount + 1, newCount + 1];
            for (int i = oldCount - 1; i >= 0; i--)
                for (int j = newCount - 1; j >= 0; j--)
                    lcs[i, j] = oldLines[prefix + i] == newLines[prefix + j]
                        ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < oldCount || y < newCount)
            {
                if (x < oldCount && y < newCount && oldLines[prefix + x] == newLines[prefix + y])
                { raw.Add(Line(AiDiffLineKind.Context, prefix + x, prefix + y, oldLines[prefix + x], newLines[prefix + y])); x++; y++; }
                else
                {
                    var removed = new List<(int Number, string Text)>();
                    var added = new List<(int Number, string Text)>();
                    while ((x < oldCount || y < newCount)
                        && !(x < oldCount && y < newCount && oldLines[prefix + x] == newLines[prefix + y]))
                    {
                        if (x < oldCount && (y == newCount || lcs[x + 1, y] >= lcs[x, y + 1]))
                        { removed.Add((prefix + x, oldLines[prefix + x])); x++; }
                        else { added.Add((prefix + y, newLines[prefix + y])); y++; }
                    }
                    var paired = Math.Min(removed.Count, added.Count);
                    for (int i = 0; i < paired; i++) raw.Add(Line(AiDiffLineKind.Modified, removed[i].Number, added[i].Number, removed[i].Text, added[i].Text));
                    for (int i = paired; i < removed.Count; i++) raw.Add(Line(AiDiffLineKind.Deleted, removed[i].Number, null, removed[i].Text, ""));
                    for (int i = paired; i < added.Count; i++) raw.Add(Line(AiDiffLineKind.Added, null, added[i].Number, "", added[i].Text));
                }
            }
        }
        for (int i = oldEnd, j = newEnd; i < oldLines.Length && j < newLines.Length; i++, j++)
            raw.Add(Line(AiDiffLineKind.Context, i, j, oldLines[i], newLines[j]));

        result.AddedLines = raw.Count(line => line.Kind == AiDiffLineKind.Added);
        result.DeletedLines = raw.Count(line => line.Kind == AiDiffLineKind.Deleted);
        result.ModifiedLines = raw.Count(line => line.Kind == AiDiffLineKind.Modified);
        var folded = FoldContext(raw);
        if (folded.Count > MaxVisibleLines)
        {
            // 同时保留首尾，避免整块替换时仅看到前面的删除而完全看不到后面的新增。
            result.IsTruncated = true;
            result.Lines = folded.Take(MaxVisibleLines / 2 - 1).ToList();
            result.Lines.Add(new AiDiffLine { Kind = AiDiffLineKind.Omitted,
                AfterText = $"⋯ 中间 {folded.Count - MaxVisibleLines + 1} 行未展示 ⋯" });
            result.Lines.AddRange(folded.TakeLast(MaxVisibleLines / 2));
        }
        else result.Lines = folded;
        if (result.IsTruncated) result.Notice = (result.Notice + "\n内容较长，差异与计数仅覆盖已捕获/显示的部分（每侧最多 16000 字符、1200 行，最多展示 500 行）。").Trim();
        if (result.AddedLines + result.DeletedLines + result.ModifiedLines == 0 && !result.IsTruncated)
            result.Notice = kind == AiFileChangeKind.Created ? "已创建空文件。" : kind == AiFileChangeKind.Deleted ? "已删除空文件。" : "文本内容未改变。";
        return result;
    }

    private static string[] Split(string? text, AiFileDiff result)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        if (text.Length > MaxSnapshotChars) { text = text[..MaxSnapshotChars]; result.IsTruncated = true; }
        // 保留尾部空行，便于识别文件末尾换行的新增/删除；统一 CRLF 与 LF。
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > MaxLines) { result.IsTruncated = true; return lines.Take(MaxLines).ToArray(); }
        return lines;
    }

    private static AiDiffLine Line(AiDiffLineKind kind, int? oldLine, int? newLine, string before, string after)
        => new() { Kind = kind, OldLineNumber = oldLine + 1, NewLineNumber = newLine + 1, BeforeText = before, AfterText = after };

    private static List<AiDiffLine> FoldContext(List<AiDiffLine> lines)
    {
        var folded = new List<AiDiffLine>();
        for (int i = 0; i < lines.Count;)
        {
            if (lines[i].Kind != AiDiffLineKind.Context) { folded.Add(lines[i++]); continue; }
            int start = i;
            while (i < lines.Count && lines[i].Kind == AiDiffLineKind.Context) i++;
            int count = i - start;
            if (count <= 6) { folded.AddRange(lines.GetRange(start, count)); continue; }
            folded.AddRange(lines.GetRange(start, 3));
            folded.Add(new AiDiffLine { Kind = AiDiffLineKind.Omitted, AfterText = $"⋯ {count - 6} 行未改变 ⋯" });
            folded.AddRange(lines.GetRange(i - 3, 3));
        }
        return folded;
    }
}
