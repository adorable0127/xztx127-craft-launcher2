using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}

var created = AiFileDiffBuilder.Build("", "first\nsecond", AiFileChangeKind.Created);
Check(created.AddedLines == 2 && created.DeletedLines == 0 && created.ModifiedLines == 0, "creation is entirely added");
var deleted = AiFileDiffBuilder.Build("first\nsecond", "", AiFileChangeKind.Deleted);
Check(deleted.DeletedLines == 2 && deleted.AddedLines == 0, "deletion is entirely removed");
var modified = AiFileDiffBuilder.Build("old\nkeep", "new\nkeep", AiFileChangeKind.Modified);
Check(modified.ModifiedLines == 1 && modified.Lines[0].BeforeText == "old"
    && modified.Lines[0].AfterText == "new" && modified.Lines[0].OldLineNumber == 1, "replacement retains both texts and line numbers");
var added = AiFileDiffBuilder.Build("a\nc", "a\nb\nc", AiFileChangeKind.Modified);
Check(added.AddedLines == 1 && added.ModifiedLines == 0 && added.Lines[1].NewLineNumber == 2, "insertion does not shift into false replacements");
var removed = AiFileDiffBuilder.Build("a\nb\nc", "a\nc", AiFileChangeKind.Modified);
Check(removed.DeletedLines == 1 && removed.ModifiedLines == 0 && removed.Lines[1].OldLineNumber == 2, "removal retains old line number");
var empty = AiFileDiffBuilder.Build("", "", AiFileChangeKind.Created);
Check(empty.Lines.Count == 0 && empty.Notice.Contains("空文件"), "empty creation labelled");
var newLine = AiFileDiffBuilder.Build("a", "a\n", AiFileChangeKind.Modified);
Check(newLine.AddedLines == 1, "terminal newline detected");
Check(AiFileDiffBuilder.Build("a\r\nb", "a\nb", AiFileChangeKind.Modified).ModifiedLines == 0, "line ending normalization");
var unavailable = AiFileDiffBuilder.Build(null, "new", AiFileChangeKind.Modified, unavailableReason: "binary");
Check(unavailable.Lines.Count == 0 && unavailable.Notice == "binary", "unavailable original must not look like a new file");
var longLine = AiFileDiffBuilder.Build("", new string('x', 40000), AiFileChangeKind.Created);
Check(longLine.IsTruncated && longLine.Lines[0].AfterText.Length == AiFileDiffBuilder.MaxSnapshotChars, "character limit labelled and enforced");
var manyLines = AiFileDiffBuilder.Build("", string.Join('\n', Enumerable.Range(0, 4000)), AiFileChangeKind.Created);
Check(manyLines.IsTruncated && manyLines.Lines.Count <= 500 && manyLines.Notice.Contains("部分"), "line count and UI bounds labelled");
var context = AiFileDiffBuilder.Build(string.Join('\n', Enumerable.Range(0, 100)), string.Join('\n', Enumerable.Range(0, 100)), AiFileChangeKind.Modified);
Check(context.Lines.Count == 7 && context.Lines[3].Kind == AiDiffLineKind.Omitted, "unchanged context collapsed");
var oldHuge = string.Join('\n', Enumerable.Range(0, 1000).Select(i => "old" + i));
var newHuge = string.Join('\n', Enumerable.Range(0, 1000).Select(i => "new" + i));
var huge = AiFileDiffBuilder.Build(oldHuge, newHuge, AiFileChangeKind.Modified);
Check(huge.IsTruncated && huge.ModifiedLines == 0 && huge.Notice.Contains("未进行"), "expensive LCS uses truthful coarse fallback");
var clone = modified.Clone();
clone.Lines[0].AfterText = "changed";
Check(modified.Lines[0].AfterText == "new", "history snapshots are deep copies");
var restored = JsonSerializer.Deserialize<AiFileDiff>(JsonSerializer.Serialize(modified))!;
Check(restored.Lines[0].Kind == AiDiffLineKind.Modified && restored.Lines[0].BeforeText == "old", "history diff round trip");

// Every non-folded diff must reconstruct both original inputs exactly, including repeated lines.
var random = new Random(12839);
for (int iteration = 0; iteration < 500; iteration++)
{
    string MakeText() => string.Join('\n', Enumerable.Range(0, random.Next(1, 7)).Select(_ => ((char)('a' + random.Next(4))).ToString()));
    var before = MakeText();
    var after = MakeText();
    var diff = AiFileDiffBuilder.Build(before, after, AiFileChangeKind.Modified);
    var reconstructedBefore = string.Join('\n', diff.Lines.Where(line => line.OldLineNumber.HasValue).Select(line => line.BeforeText));
    var reconstructedAfter = string.Join('\n', diff.Lines.Where(line => line.NewLineNumber.HasValue).Select(line => line.AfterText));
    Check(reconstructedBefore == before && reconstructedAfter == after, $"reconstruction {iteration}");
}
Console.WriteLine($"PASS: {checks} file diff regression checks");
