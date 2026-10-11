using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XCL2.App.Services;
using XCL2.App.Services.Workbench;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
void Throws(Action action, string name) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, name); }
var root = Path.Combine(Path.GetTempPath(), "xcl-workbench-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    foreach (var (id, expected) in new (string, bool)[] { ("1.8.9", false), ("1.21.5", false), ("24w14a", false), ("26.2", false), ("26.3-snapshot-1", false), ("26.3-snapshot-3", false), ("26.3-snapshot-4", true), ("26.3-snapshot-5", true), ("26.3 T3", false), ("26.3 T4", true), ("26.3.4", true), ("26.3", true), ("26.3-rc-1", true), ("26.4-snapshot-1", true), ("27.1", true) }) Check(TouchVersionPolicy.FromVersion(id) == expected, "version: " + id);
    Check(TouchVersionPolicy.FromVersion("my unknown instance") == null, "unknown version");
    var versionDir = Path.Combine(root, "versions", "26.3"); Directory.CreateDirectory(versionDir);
    File.WriteAllText(Path.Combine(versionDir, "26.3.json"), "{\"id\":\"26.3\"}");
    using (var jar = ZipFile.Open(Path.Combine(versionDir, "26.3.jar"), ZipArchiveMode.Create))
    using (var writer = new StreamWriter(jar.CreateEntry("version.json").Open())) writer.Write("{\"id\":\"26.3-snapshot-3\",\"release_target\":\"26.3\"}");
    Check(TouchVersionPolicy.Resolve(root, "26.3") == false, "real jar identity overrides folder name");
    Check(TouchInputModePolicy.Classify(true, false, true, true, false, true) == false, "capture wins over overlay cursor");
    Check(TouchInputModePolicy.Classify(false, true, true, false, true, true) == null, "suppressed is ambiguous");
    Check(TouchInputModePolicy.Classify(false, true, true, true, false, true) == true, "menu cursor");
    var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
    var file = Path.Combine(project, "source.cs"); var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\n")).ToArray(); File.WriteAllBytes(file, original);
    var page = ProjectFiles.ReadPage(project, "source.cs", 2, 1); Check(page.Text.Contains("2: beta") && page.NextLine == 3, "paged lines");
    Check(page.Sha256 == ProjectFiles.Hash(original), "whole-file revision in page");
    Throws(() => ProjectFiles.Resolve(project, "../outside"), "traversal");
    var journal = new ChangeJournal(Path.Combine(root, "journal"));
    var patched = ProjectFiles.Patch(ProjectFiles.Decode(original).Text, "beta", "BETA");
    var change = journal.Prepare(file, ProjectFiles.EncodeLike(patched, original), page.Sha256);
    Check(File.ReadAllBytes(file).SequenceEqual(original), "prepare is read-only");
    journal.Apply(change.Id); Check(File.ReadAllText(file).Contains("BETA"), "apply");
    File.AppendAllText(file, "external edit"); Throws(() => journal.Rollback(change.Id), "rollback does not overwrite later changes");
    File.WriteAllBytes(file, journal.After(change.Id)!);
    new ChangeJournal(Path.Combine(root, "journal")).Rollback(change.Id); Check(File.ReadAllBytes(file).SequenceEqual(original), "rollback survives restart and preserves BOM/CRLF");
    Throws(() => journal.Apply(change.Id), "no replay");
    var stale = journal.Prepare(file, Encoding.UTF8.GetBytes("new")); File.AppendAllText(file, "changed"); Throws(() => journal.Apply(stale.Id), "preview conflict"); File.WriteAllBytes(file, original);
    var deleted = journal.Prepare(file, null); journal.Apply(deleted.Id); Check(!File.Exists(file), "delete"); journal.Rollback(deleted.Id); Check(File.ReadAllBytes(file).SequenceEqual(original), "undo delete");
    var newFile = Path.Combine(project, "created.txt"); var created = journal.Prepare(newFile, Encoding.UTF8.GetBytes("created")); journal.Apply(created.Id); journal.Rollback(created.Id); Check(!File.Exists(newFile), "undo create");
    Throws(() => ProjectFiles.Patch("dup dup", "dup", "new"), "ambiguous patch");
    Throws(() => journal.Prepare(file, Encoding.UTF8.GetBytes("bad"), "outdated"), "stale patch");
    Directory.CreateDirectory(Path.Combine(project, "node_modules")); File.WriteAllText(Path.Combine(project, "node_modules", "ignore.cs"), "alpha");
    File.WriteAllBytes(Path.Combine(project, "binary.bin"), new byte[] { 0, 1, 2 });
    var matches = ProjectFiles.Search(project, "alpha"); Check(matches.Matches.Count == 1 && matches.Matches[0].Line == 1, "search ignores binary and dependencies");
    using var stop = new CancellationTokenSource(); stop.Cancel(); Throws(() => ProjectFiles.Search(project, "alpha", ct: stop.Token), "search cancellation");
    var store = new TaskStore(Path.Combine(root, "tasks")); var task = store.Create("plan", "finish project", project);
    task.Steps.Add(new WorkStep { Title = "read", Status = "completed", Evidence = "read source" }); task.Steps.Add(new WorkStep { Title = "patch", Status = "in_progress" }); task.NextAction = "validate"; store.Save(task);
    var restored = new TaskStore(Path.Combine(root, "tasks")).Get(task.Id); Check(restored.Steps[0].Status == "completed" && restored.NextAction == "validate", "persistent steps");
    store.Checkpoint(task.Id, "applied " + change.Id); Throws(() => store.Save(task), "concurrent task revision");
    store.Update(task.Id, t => t.Mode = "Build"); Check(store.Get(task.Id).Steps[0].Status == "completed", "Plan/Build transition retains progress");
    Throws(() => store.Update(task.Id, t => t.Steps.Add(new WorkStep { Title = "bad", Status = "in_progress" })), "one active step");
    Check(store.Get(task.Id).Steps.Count == 2, "failed update leaves disk unchanged");
    Console.WriteLine($"PASS {checks} workbench checks");
}
finally { Directory.Delete(root, true); }
