using System.Text;
using System.Text.Json;
using System.IO.Compression;
using XCL2.App.Services;
using XCL2.App.Services.Worlds;
using XCL2.App.Services.Plugins;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; }
void Reject(Action action, string name) { try { action(); } catch (Exception) { checks++; return; } throw new Exception("FAIL: " + name); }
async Task RejectAsync(Func<Task> action, string name) { try { await action(); } catch (Exception) { checks++; return; } throw new Exception("FAIL: " + name); }
NbtTag Compound(params (string, NbtTag)[] fields) => new(10, fields.ToDictionary(p => p.Item1, p => p.Item2));
NbtTag List(byte type, params NbtTag[] values) => new(9, new NbtList(type, values.ToList()));
NbtTag I(int value) => new(3, value);
string Frame(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finish } } }) + "\n\n";
async Task<(string Answer, List<string> Updates, string Reasoning)> Stream(string data)
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data)); var updates = new List<string>(); string thought = "";
    var answer = await AiStreamingCompletion.ReadAsync(stream, updates.Add, (reason, _) => thought = reason, default); return (answer, updates, thought);
}
var streamed = await Stream(": heartbeat\n\n" + Frame(new { content = "第一句。" }) + Frame(new { reasoning_content = "review " }) + Frame(new { reasoning_content = "carefully" }) + Frame(new { content = "第二句。" }, "stop"));
Check(streamed.Answer == "第一句。第二句。" && streamed.Updates[0] == "第一句。", "first content callback precedes the completed response");
Check(streamed.Reasoning == "review carefully", "reasoning whitespace and independent stream");
var longReasoning = new string('r', 20000) + "终点";
var fullReasoningStream = await Stream(Frame(new { reasoning_content = longReasoning[..12000] })
    + Frame(new { reasoning_content = longReasoning[12000..], content = "答案" }, "stop"));
Check(fullReasoningStream.Reasoning == longReasoning && fullReasoningStream.Answer == "答案",
    "long reasoning survives all streamed chunks without becoming answer text");
bool sawProviderError = false;
int outputStarts = 0;
using (var streamError = new MemoryStream(Encoding.UTF8.GetBytes(Frame(new { content = "<<XCL2_" })
    + "data: {\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"later error\"}}\n\n")))
{
    await RejectAsync(() => AiStreamingCompletion.ReadAsync(streamError, null, null, default,
        providerError: data => sawProviderError = data.GetProperty("error").GetProperty("code").GetString() == "rate_limit_exceeded",
        responseStarted: () => outputStarts++), "provider error ends a partially received stream");
}
Check(sawProviderError && outputStarts == 1, "raw provider error and hidden control output are signalled before replay decisions");
outputStarts = 0;
string interruptedThought = "";
using (var partialReasoning = new MemoryStream(Encoding.UTF8.GetBytes(Frame(new { reasoning_content = "思考" })
    + Frame(new { reasoning_content = longReasoning }))))
{
    await RejectAsync(() => AiStreamingCompletion.ReadAsync(partialReasoning, null, (thought, _) => interruptedThought = thought,
        default, responseStarted: () => outputStarts++), "reasoning-only premature EOF is a failed stream");
}
Check(outputStarts == 1 && interruptedThought == "思考" + longReasoning,
    "reasoning-only output is never replayed and final throttled text remains complete on error");
Check(AiStreamingCompletion.VisibleText("回答<<XCL") == "回答", "partial marker prefix does not flash");
Check(AiStreamingCompletion.VisibleText("回答<<XCL2_ACCESS:{\"action\":") == "回答", "unfinished command hidden from answer");
await RejectAsync(() => Stream(Frame(new { content = "已收到" })), "EOF without finish is a failed stream");
await RejectAsync(() => Stream(Frame(new { content = "<<XCL2_ACCESS:" }, "length")), "truncated controls never become completed replies");
await RejectAsync(() => Stream("data: {bad-json}\n\n"), "malformed SSE is rejected");
var cancellation = new CancellationTokenSource(); cancellation.Cancel();
using (var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(new { content = "a" }, "stop"))))
    await RejectAsync(() => AiStreamingCompletion.ReadAsync(input, null, null, cancellation.Token), "stream cancellation");
var firstDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var gated = new GatedStream(Encoding.UTF8.GetBytes(Frame(new { content = "即时" })), Encoding.UTF8.GetBytes("data: [DONE]\n\n"), release.Task))
{
    var pending = AiStreamingCompletion.ReadAsync(gated, _ => firstDelivered.TrySetResult(), null, default);
    await firstDelivered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check(!pending.IsCompleted, "content visible before stream completion"); release.SetResult(); await pending;
}
var root = Path.Combine(Path.GetTempPath(), "XCL2-feature-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var world = Path.Combine(root, "world"); Directory.CreateDirectory(world);
    var level = Path.Combine(world, "level.dat");
    var document = new NbtDocument("", Compound(("Data", Compound(("LevelName", new(8, "世界\0😀")), ("GameType", I(0)), ("Difficulty", new(1, (sbyte)2)), ("RandomSeed", new(4, -123L)), ("DataVersion", I(3700)), ("unknown", new(12, new long[] { long.MinValue, long.MaxValue })), ("Version", Compound(("Name", new(8, "test"))))))));
    document.Save(level); var before = File.ReadAllBytes(level);
    Check(WorldArchiveService.Read(world).Settings.Name == "世界\0😀", "modified UTF8 survives read");
    var backup = WorldArchiveService.Update(world, new("新世界", 1, 3, true, false));
    Check(File.ReadAllBytes(backup).SequenceEqual(before), "world backup preserves original bytes");
    Check(WorldArchiveService.Read(world).Settings is { Name: "新世界", GameMode: 1, Difficulty: 3, AllowCommands: true, Seed: -123 }, "world settings saved");
    Check(((long[])NbtDocument.Load(level).Root.Get("Data")!.Get("unknown")!.Value)[1] == long.MaxValue, "unknown tags preserved");
    // Exercise actual NBT read/write for optional edits, including legacy/modern seed keys and opaque plugin data.
    var extended = NbtDocument.Load(level); var data = extended.Root.Get("Data")!.Compound;
    data["WorldGenSettings"] = Compound(("seed", new(4, -123L)), ("generate_features", new(1, (sbyte)1)), ("custom", new(8, "keep")));
    data["GameRules"] = Compound(("keepInventory", new(8, "false")), ("randomTickSpeed", new(8, "3")), ("doMobSpawning", new(1, (sbyte)1)), ("mod:opaque", new(8, "vendor-value")));
    data["Player"] = Compound(("Health", new(5, 20f)), ("foodLevel", I(20)), ("Pos", List(6, new(6, 1.5d), new(6, 64d), new(6, -2d))), ("Dimension", new(8, "minecraft:overworld")), ("Inventory", List(10, Compound(("id", new(8, "minecraft:diamond")), ("Count", new(1, (sbyte)7))))));
    data["BorderSizeLerpTime"] = new(4, 10000L); data["BorderSizeLerpTarget"] = new(6, 1000d);
    var staged = level + ".test"; extended.Save(staged); File.Move(staged, level, true); var extendedBefore = File.ReadAllBytes(level);
    var extendedBackup = WorldArchiveService.Update(world, new("新世界", 1, 3, true, false)
    {
        Seed = long.MinValue, Time = 12000, DayTime = 6000, SpawnX = -128, SpawnY = 72, SpawnZ = 256, SpawnAngle = 90,
        Raining = true, Thundering = false, RainTime = int.MaxValue, ThunderTime = 0, ClearWeatherTime = 0,
        DifficultyLocked = true, MapFeatures = false, BorderCenterX = 12.5, BorderCenterZ = -42.25, BorderSize = 5000,
        GameRules = new Dictionary<string, string> { ["keepInventory"] = "true", ["randomTickSpeed"] = "9", ["doMobSpawning"] = "false", ["doDaylightCycle"] = "false" },
        Player = new() { Health = 12.5f, FoodLevel = 17, FoodSaturation = 4.5f, XpLevel = 42, XpTotal = 2920, XpProgress = .25f, X = -20.75, GameMode = 2 }
    });
    var saved = WorldArchiveService.Read(world).Settings; var persisted = NbtDocument.Load(level).Root.Get("Data")!;
    Check(File.ReadAllBytes(extendedBackup).SequenceEqual(extendedBefore), "advanced edit backup is exact previous save");
    Check(saved is { Seed: long.MinValue, Time: 12000, DayTime: 6000, SpawnX: -128, SpawnY: 72, SpawnZ: 256, SpawnAngle: 90, Raining: true, Thundering: false, RainTime: int.MaxValue, DifficultyLocked: true, MapFeatures: false }, "advanced world settings survive NBT round trip");
    Check(persisted.Get("RandomSeed")!.Number == long.MinValue && persisted.Get("WorldGenSettings")!.Get("seed")!.Number == long.MinValue && persisted.Get("WorldGenSettings")!.Get("custom")!.Text == "keep", "modern and legacy seed agree while generator metadata survives");
    Check(saved is { BorderCenterX: 12.5, BorderCenterZ: -42.25, BorderSize: 5000 } && persisted.Get("BorderSizeLerpTarget")!.Value.Equals(5000d) && persisted.Get("BorderSizeLerpTime")!.Number == 0, "world border does not revert via pending transition");
    Check(saved.GameRules["keepInventory"] == "true" && saved.GameRules["doMobSpawning"] == "false" && persisted.Get("GameRules")!.Get("doMobSpawning")!.Type == 1 && saved.GameRules["mod:opaque"] == "vendor-value", "rule merge preserves omitted rules and original tag types");
    Check(saved.Player is { Health: 12.5f, FoodLevel: 17, FoodSaturation: 4.5f, XpLevel: 42, XpTotal: 2920, XpProgress: .25f, X: -20.75, Y: 64, Z: -2, GameMode: 2, Dimension: "minecraft:overworld" } && persisted.Get("Player")!.Get("Inventory")!.Items[0].Get("Count")!.Number == 7, "partial player edit keeps other position axes and inventory");
    WorldArchiveService.Update(world, new("新世界", 1, 3, true, false));
    Check(WorldArchiveService.Read(world).Settings is { Seed: long.MinValue, DayTime: 6000, Raining: true, Player.XpLevel: 42 }, "old five-argument edit preserves all optional fields");
    var beforeInvalid = File.ReadAllBytes(level); var backupsBeforeInvalid = Directory.GetFiles(Path.Combine(world, "xcl2-backups")).Length;
    Reject(() => WorldArchiveService.Update(world, new("invalid", 0, 2, false, false) { BorderSize = double.NaN }), "non-finite border rejected");
    Reject(() => WorldArchiveService.Update(world, new("invalid", 0, 2, false, false) { Player = new() { FoodLevel = 21 } }), "out-of-range player state rejected");
    Reject(() => WorldArchiveService.Update(world, new("invalid", 0, 2, false, false) { DayTime = -1 }), "negative time rejected");
    Reject(() => WorldArchiveService.Update(world, new("invalid", 0, 2, false, false) { GameRules = new Dictionary<string, string> { ["keepInventory"] = "7" } }), "boolean rule rejects integer replacement");
    Check(File.ReadAllBytes(level).SequenceEqual(beforeInvalid) && Directory.GetFiles(Path.Combine(world, "xcl2-backups")).Length == backupsBeforeInvalid, "invalid changes leave original and backups untouched");
    var noPlayer = Path.Combine(root, "server-world"); Directory.CreateDirectory(noPlayer);
    new NbtDocument("", Compound(("Data", Compound(("LevelName", new(8, "server")), ("GameType", I(0)))))).Save(Path.Combine(noPlayer, "level.dat"));
    var noPlayerBefore = File.ReadAllBytes(Path.Combine(noPlayer, "level.dat"));
    Reject(() => WorldArchiveService.Update(noPlayer, new("server", 0, 2, false, false) { Player = new() { Health = 20 } }), "player edits cannot create single-player data in multiplayer saves");
    Check(File.ReadAllBytes(Path.Combine(noPlayer, "level.dat")).SequenceEqual(noPlayerBefore), "missing player rejection is non-destructive");
    var copy = Path.Combine(root, "copy"); WorldArchiveService.GenerateCopy(world, copy, new("副本", 0, 1, false, false));
    Check(WorldArchiveService.Read(copy).Settings.Name == "副本" && WorldArchiveService.Read(world).Settings.Name == "新世界", "copy leaves original unchanged");
    Reject(() => WorldArchiveService.GenerateCopy(world, world + Path.DirectorySeparatorChar + "nested", new("x", 0, 0, false, false)), "nested destination rejected");
    Reject(() => WorldArchiveService.GenerateCopy(world, copy, new("x", 0, 0, false, false)), "existing destination rejected");
    using (var held = new FileStream(Path.Combine(world, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Reject(() => WorldArchiveService.Update(world, new("x", 0, 0, false, false)), "busy world rejected");
    var structure = Path.Combine(root, "structure.nbt");
    new NbtDocument("", Compound(("size", List(3, I(2), I(2), I(2))), ("palette", List(10, Compound(("Name", new(8, "minecraft:stone"))))), ("blocks", List(10, Compound(("pos", List(3, I(1), I(1), I(0))), ("state", I(0))))))).Save(structure);
    var projection = StructureInspector.Read(structure);
    Check(projection.Width == 2 && projection.Height == 2 && projection.TopView.Single() is { X: 1, Y: 1, Z: 0, Block: "minecraft:stone" }, "structure uses actual block coordinates");
    var litematic = Path.Combine(root, "test.litematic");
    new NbtDocument("", Compound(("Regions", Compound(("negative", Compound(("Size", Compound(("x", I(-2)), ("y", I(1)), ("z", I(1)))), ("Position", Compound(("x", I(0)), ("y", I(0)), ("z", I(0)))), ("BlockStatePalette", List(10, Compound(("Name", new(8, "minecraft:air"))), Compound(("Name", new(8, "minecraft:stone"))))), ("BlockStates", new(12, new long[] { 4 })))))))).Save(litematic);
    Check(StructureInspector.Read(litematic) is { Width: 2, Blocks: 1 }, "negative litematic dimensions and palette decoding");
    var bad = Path.Combine(root, "bad.nbt"); File.WriteAllBytes(bad, new byte[] { 10, 0, 0, 7, 0, 1, 120, 127, 255, 255, 255 });
    Reject(() => NbtDocument.Load(bad), "oversized NBT array rejected before allocation");
    var payload = Path.Combine(root, "package"); Directory.CreateDirectory(payload); File.WriteAllText(Path.Combine(payload, "plugin.json"), "{}"); File.WriteAllText(Path.Combine(payload, "test.dll"), "test fixture");
    var fingerprint = PluginPackageSignature.SignLocal(payload); Check(PluginPackageSignature.Verify(payload) == fingerprint, "signature verified");
    File.AppendAllText(Path.Combine(payload, "test.dll"), "tamper"); Reject(() => PluginPackageSignature.Verify(payload), "changed signed file rejected");
    PluginPackageSignature.SignLocal(payload); File.WriteAllText(Path.Combine(payload, "extra.dll"), "extra"); Reject(() => PluginPackageSignature.Verify(payload), "unsigned extra file rejected");
    var plugins = Path.Combine(root, "Plugins"); Directory.CreateDirectory(Path.Combine(plugins, "installed")); Directory.CreateDirectory(Path.Combine(plugins, "config"));
    File.WriteAllText(Path.Combine(plugins, "installed", "old.dll"), "old"); File.WriteAllText(Path.Combine(plugins, "config", "config.json"), "before");
    var snapshot = PluginUpgradeBackup.Create(plugins); File.WriteAllText(Path.Combine(plugins, "installed", "old.dll"), "changed"); File.WriteAllText(Path.Combine(plugins, "config", "config.json"), "after");
    PluginUpgradeBackup.Restore(snapshot, plugins);
    Check(File.ReadAllText(Path.Combine(plugins, "installed", "old.dll")) == "old" && File.ReadAllText(Path.Combine(plugins, "config", "config.json")) == "before", "backup restores plugin and config");
    var corrupt = Path.Combine(root, "bad-backup.zip"); File.Copy(snapshot, corrupt);
    using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update)) { var entry = zip.GetEntry("installed/old.dll")!; entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("installed/old.dll").Open()); writer.Write("corrupt"); }
    Reject(() => PluginUpgradeBackup.Restore(corrupt, plugins), "corrupt backup rejected");
    Check(File.ReadAllText(Path.Combine(plugins, "installed", "old.dll")) == "old", "failed restore leaves installation untouched");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"PASS: {checks} feature regression checks");

sealed class GatedStream(byte[] first, byte[] rest, Task release) : Stream
{
    private bool _first, _rest;
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (!_first) { first.AsMemory().CopyTo(buffer); _first = true; return first.Length; }
        if (_rest) return 0; await release.WaitAsync(ct); rest.AsMemory().CopyTo(buffer); _rest = true; return rest.Length;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
