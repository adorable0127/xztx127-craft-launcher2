using System.Globalization;
using System.IO;

namespace XCL2.App.Services.Worlds;

public sealed record WorldPlayerSettings(float? Health, int? FoodLevel, float? FoodSaturation, int? XpLevel,
    int? XpTotal, float? XpProgress, double? X, double? Y, double? Z, int? GameMode, string? Dimension);
public sealed record WorldPlayerEdit
{
    public float? Health { get; init; }
    public int? FoodLevel { get; init; }
    public float? FoodSaturation { get; init; }
    public int? XpLevel { get; init; }
    public int? XpTotal { get; init; }
    public float? XpProgress { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public int? GameMode { get; init; }
}
public sealed record WorldSettings(string Name, int GameMode, int Difficulty, bool AllowCommands, bool Hardcore,
    long? Seed, string Version, int DataVersion)
{
    public long? Time { get; init; }
    public long? DayTime { get; init; }
    public int? SpawnX { get; init; }
    public int? SpawnY { get; init; }
    public int? SpawnZ { get; init; }
    public float? SpawnAngle { get; init; }
    public bool? Raining { get; init; }
    public bool? Thundering { get; init; }
    public int? RainTime { get; init; }
    public int? ThunderTime { get; init; }
    public int? ClearWeatherTime { get; init; }
    public bool? DifficultyLocked { get; init; }
    public bool? MapFeatures { get; init; }
    public double? BorderCenterX { get; init; }
    public double? BorderCenterZ { get; init; }
    public double? BorderSize { get; init; }
    public IReadOnlyDictionary<string, string> GameRules { get; init; } = new Dictionary<string, string>();
    public WorldPlayerSettings? Player { get; init; }
}
public sealed record WorldArchive(string Path, WorldSettings Settings, DateTime ModifiedUtc);
/// <summary>Optional fields preserve the stored value when omitted/null. Player edits target Data.Player only.</summary>
public sealed record WorldEdit(string Name, int GameMode, int Difficulty, bool AllowCommands, bool Hardcore)
{
    public long? Seed { get; init; }
    public long? Time { get; init; }
    public long? DayTime { get; init; }
    public int? SpawnX { get; init; }
    public int? SpawnY { get; init; }
    public int? SpawnZ { get; init; }
    public float? SpawnAngle { get; init; }
    public bool? Raining { get; init; }
    public bool? Thundering { get; init; }
    public int? RainTime { get; init; }
    public int? ThunderTime { get; init; }
    public int? ClearWeatherTime { get; init; }
    public bool? DifficultyLocked { get; init; }
    public bool? MapFeatures { get; init; }
    public double? BorderCenterX { get; init; }
    public double? BorderCenterZ { get; init; }
    public double? BorderSize { get; init; }
    public IReadOnlyDictionary<string, string>? GameRules { get; init; }
    public WorldPlayerEdit? Player { get; init; }
}
public sealed record WorldEditField(string Key, string Label, string Type, double? Minimum = null, double? Maximum = null, string? Note = null);

public static class WorldArchiveService
{
    private static readonly object Gate = new();
    public static IReadOnlyList<WorldEditField> EditableFields { get; } = Array.AsReadOnly(new WorldEditField[]
    {
        new("name", "存档名称", "string", Note: "1–128 个字符"),
        new("gameMode", "游戏模式", "int", 0, 3), new("difficulty", "难度", "int", 0, 3),
        new("allowCommands", "允许作弊", "bool"), new("hardcore", "极限模式", "bool"),
        new("seed", "世界种子", "long", Note: "只影响以后生成的区块，不会重建已有地形"),
        new("time", "世界总时长（tick）", "long", 0), new("dayTime", "昼夜时间（tick）", "long", 0),
        new("spawnX", "出生点 X", "int", -30000000, 30000000), new("spawnY", "出生点 Y", "int", -2048, 2048),
        new("spawnZ", "出生点 Z", "int", -30000000, 30000000), new("spawnAngle", "出生朝向", "float", -360, 360),
        new("raining", "下雨", "bool"), new("thundering", "雷暴", "bool"),
        new("rainTime", "降雨计时（tick）", "int", 0, int.MaxValue), new("thunderTime", "雷暴计时（tick）", "int", 0, int.MaxValue),
        new("clearWeatherTime", "晴天计时（tick）", "int", 0, int.MaxValue),
        new("difficultyLocked", "锁定难度", "bool"), new("mapFeatures", "生成结构", "bool", Note: "已有区块不变"),
        new("borderCenterX", "世界边界中心 X", "double", -30000000, 30000000),
        new("borderCenterZ", "世界边界中心 Z", "double", -30000000, 30000000),
        new("borderSize", "世界边界直径", "double", 1, 59999968),
        new("gameRules", "游戏规则", "dictionary", Note: "规则名称区分大小写，值为 true/false 或非负 32 位整数；未提交的规则保留"),
        new("player.health", "玩家生命", "float", 0, 1024), new("player.foodLevel", "饥饿值", "int", 0, 20),
        new("player.foodSaturation", "饱和度", "float", 0, 20), new("player.xpLevel", "经验等级", "int", 0, int.MaxValue),
        new("player.xpTotal", "总经验", "int", 0, int.MaxValue), new("player.xpProgress", "当前等级经验进度", "float", 0, 1),
        new("player.x", "玩家 X", "double", -30000000, 30000000), new("player.y", "玩家 Y", "double", -2048, 2048),
        new("player.z", "玩家 Z", "double", -30000000, 30000000), new("player.gameMode", "玩家游戏模式", "int", 0, 3)
    });
    public static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(System.IO.Path.GetFullPath(path)); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接或重解析目录。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接文件。");
    }
    public static IReadOnlyList<WorldArchive> Discover(IEnumerable<string> gameRoots, Action<string>? warning = null)
    {
        var result = new List<WorldArchive>();
        foreach (string root in gameRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var saves = System.IO.Path.Combine(root, "saves");
            if (!Directory.Exists(saves)) continue;
            try
            {
                RejectLinks(saves);
                foreach (var dir in Directory.EnumerateDirectories(saves))
                    try { if (File.Exists(System.IO.Path.Combine(dir, "level.dat"))) result.Add(Read(dir)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { warning?.Invoke(dir + "：" + ex.Message); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning?.Invoke(saves + "：" + ex.Message); }
        }
        return result.OrderByDescending(w => w.ModifiedUtc).ToArray();
    }
    public static WorldArchive Read(string directory)
    {
        var path = System.IO.Path.GetFullPath(directory); RejectLinks(path);
        var level = System.IO.Path.Combine(path, "level.dat"); RejectLinks(level);
        var data = NbtDocument.Load(level).Root.Get("Data") ?? throw new InvalidDataException("不是 Java 版存档 level.dat。");
        long Number(string name, long fallback = 0) => data.Get(name)?.Number ?? fallback;
        var seed = data.Get("WorldGenSettings")?.Get("seed") ?? data.Get("RandomSeed");
        long? OptionalLong(string name) => data.Get(name)?.Number;
        int? OptionalInt(string name) => data.Get(name) is { } tag ? checked((int)tag.Number) : null;
        bool? OptionalBool(string name) => data.Get(name) is { } tag ? tag.Number != 0 : null;
        var player = data.Get("Player");
        double? Position(int index) => player?.Get("Pos")?.Items is { Count: 3 } pos ? Real(pos[index]) : null;
        var rules = data.Get("GameRules")?.Compound.ToDictionary(pair => pair.Key,
            pair => pair.Value.Type == 1 ? (pair.Value.Number != 0 ? "true" : "false") : Convert.ToString(pair.Value.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
        return new(path, new WorldSettings(data.Get("LevelName")?.Text ?? System.IO.Path.GetFileName(path), (int)Number("GameType"),
            (int)Number("Difficulty", 2), Number("allowCommands") != 0, Number("hardcore") != 0, seed?.Number,
            data.Get("Version")?.Get("Name")?.Text ?? "未知版本", (int)Number("DataVersion"))
        {
            Time = OptionalLong("Time"), DayTime = OptionalLong("DayTime"), SpawnX = OptionalInt("SpawnX"),
            SpawnY = OptionalInt("SpawnY"), SpawnZ = OptionalInt("SpawnZ"), SpawnAngle = (float?)Real(data.Get("SpawnAngle")),
            Raining = OptionalBool("raining"), Thundering = OptionalBool("thundering"), RainTime = OptionalInt("rainTime"),
            ThunderTime = OptionalInt("thunderTime"), ClearWeatherTime = OptionalInt("clearWeatherTime"),
            DifficultyLocked = OptionalBool("DifficultyLocked"),
            MapFeatures = data.Get("WorldGenSettings")?.Get("generate_features") is { } features ? features.Number != 0 : OptionalBool("MapFeatures"),
            BorderCenterX = Real(data.Get("BorderCenterX")), BorderCenterZ = Real(data.Get("BorderCenterZ")), BorderSize = Real(data.Get("BorderSize")),
            GameRules = rules,
            Player = player == null ? null : new((float?)Real(player.Get("Health")), (int?)player.Get("foodLevel")?.Number,
                (float?)Real(player.Get("foodSaturationLevel")), (int?)player.Get("XpLevel")?.Number, (int?)player.Get("XpTotal")?.Number,
                (float?)Real(player.Get("XpP")), Position(0), Position(1), Position(2), (int?)player.Get("playerGameType")?.Number,
                player.Get("Dimension")?.Value.ToString())
        }, File.GetLastWriteTimeUtc(level));
    }
    private static double? Real(NbtTag? tag) => tag?.Value switch
    { null => null, float n => n, double n => n, sbyte n => n, short n => n, int n => n, long n => n, _ => throw new InvalidDataException("需要 NBT 数字。") };

    public static void Validate(WorldEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 128 || edit.GameMode is < 0 or > 3 || edit.Difficulty is < 0 or > 3)
            throw new ArgumentException("请检查名称、游戏模式和难度。");
        static void Range(double? value, double min, double max, string name)
        { if (value.HasValue && (!double.IsFinite(value.Value) || value < min || value > max)) throw new ArgumentException($"{name} 应在 {min}–{max} 范围内。"); }
        if (edit.Time < 0 || edit.DayTime < 0) throw new ArgumentException("世界时间不能为负数。");
        Range(edit.SpawnX, -30000000, 30000000, "出生点 X"); Range(edit.SpawnY, -2048, 2048, "出生点 Y"); Range(edit.SpawnZ, -30000000, 30000000, "出生点 Z");
        Range(edit.SpawnAngle, -360, 360, "出生朝向"); Range(edit.RainTime, 0, int.MaxValue, "降雨计时");
        Range(edit.ThunderTime, 0, int.MaxValue, "雷暴计时"); Range(edit.ClearWeatherTime, 0, int.MaxValue, "晴天计时");
        Range(edit.BorderCenterX, -30000000, 30000000, "边界中心 X"); Range(edit.BorderCenterZ, -30000000, 30000000, "边界中心 Z");
        Range(edit.BorderSize, 1, 59999968, "边界直径");
        if (edit.GameRules is { Count: > 1024 }) throw new ArgumentException("一次最多修改 1024 条游戏规则。");
        foreach (var rule in edit.GameRules ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(rule.Key) || rule.Key.Length > 128 || rule.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not ':' and not '.' and not '-' and not '/'))
                throw new ArgumentException("游戏规则名称无效。");
            if (rule.Value is not "true" and not "false" && (!int.TryParse(rule.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0))
                throw new ArgumentException($"游戏规则 {rule.Key} 只能填写 true、false 或非负整数。");
        }
        if (edit.Player is { } player)
        {
            Range(player.Health, 0, 1024, "玩家生命"); Range(player.FoodLevel, 0, 20, "饥饿值"); Range(player.FoodSaturation, 0, 20, "饱和度");
            Range(player.XpLevel, 0, int.MaxValue, "经验等级"); Range(player.XpTotal, 0, int.MaxValue, "总经验"); Range(player.XpProgress, 0, 1, "经验进度");
            Range(player.X, -30000000, 30000000, "玩家 X"); Range(player.Y, -2048, 2048, "玩家 Y"); Range(player.Z, -30000000, 30000000, "玩家 Z");
            Range(player.GameMode, 0, 3, "玩家模式");
        }
    }

    /// <summary>Human-readable request summary for UI/plugin approval. Does not change the save.</summary>
    public static string DescribeEdit(WorldEdit edit)
    {
        var lines = new List<string> { $"名称：{edit.Name}；模式：{edit.GameMode}；难度：{edit.Difficulty}", $"作弊：{edit.AllowCommands}；极限：{edit.Hardcore}" };
        foreach (var property in typeof(WorldEdit).GetProperties())
        {
            if (property.Name is nameof(WorldEdit.Name) or nameof(WorldEdit.GameMode) or nameof(WorldEdit.Difficulty) or nameof(WorldEdit.AllowCommands) or nameof(WorldEdit.Hardcore)) continue;
            if (property.GetValue(edit) is not { } value) continue;
            if (value is IReadOnlyDictionary<string, string> rules) lines.Add("规则：" + string.Join("，", rules.Select(p => p.Key + "=" + p.Value)));
            else if (value is WorldPlayerEdit player) lines.Add("玩家：" + string.Join("，", typeof(WorldPlayerEdit).GetProperties().Where(p => p.GetValue(player) != null).Select(p => p.Name + "=" + p.GetValue(player))));
            else lines.Add(property.Name + "：" + Convert.ToString(value, CultureInfo.InvariantCulture));
        }
        return string.Join("\n", lines);
    }
    private static FileStream LockWorld(string path)
    {
        var file = System.IO.Path.Combine(path, "session.lock"); RejectLinks(file);
        var handle = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) handle.Lock(0, 1); return handle; }
        catch { handle.Dispose(); throw new IOException("存档正在使用，请先退出游戏或服务器。"); }
    }
    public static string Update(string directory, WorldEdit edit)
    {
        Validate(edit);
        lock (Gate)
        {
            RejectLinks(directory); using var worldLock = LockWorld(directory);
            return UpdateUnlocked(directory, edit);
        }
    }
    private static string UpdateUnlocked(string directory, WorldEdit edit)
    {
        Validate(edit);
        var level = System.IO.Path.Combine(directory, "level.dat"); RejectLinks(level);
        var doc = NbtDocument.Load(level);
        var data = doc.Root.Get("Data")?.Compound ?? throw new InvalidDataException("缺少 Data 标签。");
        data["LevelName"] = new(8, edit.Name); data["GameType"] = new(3, edit.GameMode);
        data["Difficulty"] = new(1, (sbyte)edit.Difficulty); data["allowCommands"] = new(1, (sbyte)(edit.AllowCommands ? 1 : 0));
        data["hardcore"] = new(1, (sbyte)(edit.Hardcore ? 1 : 0));
        Set(data, "Time", edit.Time, 4); Set(data, "DayTime", edit.DayTime, 4);
        Set(data, "SpawnX", edit.SpawnX, 3); Set(data, "SpawnY", edit.SpawnY, 3); Set(data, "SpawnZ", edit.SpawnZ, 3); Set(data, "SpawnAngle", edit.SpawnAngle, 5);
        Set(data, "raining", edit.Raining, 1); Set(data, "thundering", edit.Thundering, 1); Set(data, "DifficultyLocked", edit.DifficultyLocked, 1);
        Set(data, "rainTime", edit.RainTime, 3); Set(data, "thunderTime", edit.ThunderTime, 3); Set(data, "clearWeatherTime", edit.ClearWeatherTime, 3);
        Set(data, "BorderCenterX", edit.BorderCenterX, 6); Set(data, "BorderCenterZ", edit.BorderCenterZ, 6); Set(data, "BorderSize", edit.BorderSize, 6);
        // A pending border transition would immediately override the newly chosen size in-game.
        if (edit.BorderSize.HasValue) { Set(data, "BorderSizeLerpTarget", edit.BorderSize, 6); Set(data, "BorderSizeLerpTime", 0L, 4); }
        if (data.TryGetValue("WorldGenSettings", out var worldgen))
        {
            Set(worldgen.Compound, "seed", edit.Seed, 4); Set(worldgen.Compound, "generate_features", edit.MapFeatures, 1);
            if (data.ContainsKey("RandomSeed")) Set(data, "RandomSeed", edit.Seed, 4);
            if (data.ContainsKey("MapFeatures")) Set(data, "MapFeatures", edit.MapFeatures, 1);
        }
        else { Set(data, "RandomSeed", edit.Seed, 4); Set(data, "MapFeatures", edit.MapFeatures, 1); }
        if (edit.GameRules is { Count: > 0 } rules)
        {
            if (!data.TryGetValue("GameRules", out var rulesTag)) data["GameRules"] = rulesTag = new(10, new Dictionary<string, NbtTag>(StringComparer.Ordinal));
            foreach (var rule in rules)
            {
                // Some formats use primitive tags; retain those types rather than replacing their schema.
                var existing = rulesTag.Get(rule.Key); var type = existing?.Type ?? 8;
                if (existing?.Type == 8 && ((existing.Text is "true" or "false" && rule.Value is not "true" and not "false")
                    || (int.TryParse(existing.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _) && !int.TryParse(rule.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))))
                    throw new ArgumentException($"游戏规则 {rule.Key} 必须与原值保持相同的布尔/整数类型。");
                if (type == 8) rulesTag.Compound[rule.Key] = new(8, rule.Value);
                else if (type == 1 && rule.Value is "true" or "false") rulesTag.Compound[rule.Key] = new(1, (sbyte)(rule.Value == "true" ? 1 : 0));
                else if (type is 2 or 3 or 4 && int.TryParse(rule.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                { Set(rulesTag.Compound, rule.Key, n, type); }
                else throw new ArgumentException($"游戏规则 {rule.Key} 的值与原有 NBT 类型不一致。");
            }
        }
        if (edit.Player is { } player)
        {
            if (!data.TryGetValue("Player", out var playerTag)) throw new InvalidDataException("此存档没有 level.dat 内的单人玩家数据，不能创建或修改外部 playerdata 玩家。");
            var p = playerTag.Compound;
            Set(p, "Health", player.Health, 5); Set(p, "foodLevel", player.FoodLevel, 3); Set(p, "foodSaturationLevel", player.FoodSaturation, 5);
            Set(p, "XpLevel", player.XpLevel, 3); Set(p, "XpTotal", player.XpTotal, 3); Set(p, "XpP", player.XpProgress, 5); Set(p, "playerGameType", player.GameMode, 3);
            if (player.X.HasValue || player.Y.HasValue || player.Z.HasValue)
            {
                var current = playerTag.Get("Pos");
                if (current?.Value is not NbtList { ElementType: 6, Items.Count: 3 } pos) throw new InvalidDataException("玩家 Pos 不是包含三个 Double 的坐标，拒绝覆盖。");
                p["Pos"] = new(9, new NbtList(6, new List<NbtTag> { new(6, player.X ?? Real(pos.Items[0])!.Value), new(6, player.Y ?? Real(pos.Items[1])!.Value), new(6, player.Z ?? Real(pos.Items[2])!.Value) }));
            }
        }
        var backupDirectory = System.IO.Path.Combine(directory, "xcl2-backups"); RejectLinks(backupDirectory); Directory.CreateDirectory(backupDirectory);
        var backup = System.IO.Path.Combine(backupDirectory, $"level-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.dat");
        File.Copy(level, backup, false);
        var temporary = level + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { doc.Save(temporary); _ = NbtDocument.Load(temporary); File.Move(temporary, level, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return backup;
    }
    public static string GenerateCopy(string source, string destination, WorldEdit edit, CancellationToken ct = default)
    {
        Validate(edit);
        lock (Gate)
        {
            source = System.IO.Path.GetFullPath(source); destination = System.IO.Path.GetFullPath(destination);
            if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("新存档不能位于原存档内。");
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("目标已存在，不会覆盖。");
            RejectLinks(source); RejectLinks(destination); _ = Read(source);
            using var worldLock = LockWorld(source);
            var staging = destination + ".xcl2-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyTree(source, staging, ct); ct.ThrowIfCancellationRequested();
                UpdateUnlocked(staging, edit); Directory.Move(staging, destination); return destination;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
    }
    private static void Set(Dictionary<string, NbtTag> data, string key, object? value, byte type)
    {
        if (value == null) return;
        object typed = type switch
        {
            1 => (object)(value is bool boolean ? (sbyte)(boolean ? 1 : 0) : Convert.ToSByte(value, CultureInfo.InvariantCulture)),
            2 => (object)Convert.ToInt16(value, CultureInfo.InvariantCulture), 3 => (object)Convert.ToInt32(value, CultureInfo.InvariantCulture),
            4 => (object)Convert.ToInt64(value, CultureInfo.InvariantCulture), 5 => (object)Convert.ToSingle(value, CultureInfo.InvariantCulture),
            6 => (object)Convert.ToDouble(value, CultureInfo.InvariantCulture), _ => throw new ArgumentException("不支持的字段类型。")
        };
        data[key] = new(type, typed);
    }
    internal static void CopyTree(string source, string destination, CancellationToken ct)
    {
        RejectLinks(source); Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested(); RejectLinks(file);
            if (System.IO.Path.GetFileName(file).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), false);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (System.IO.Path.GetFileName(directory).Equals("xcl2-backups", StringComparison.OrdinalIgnoreCase)) continue;
            CopyTree(directory, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(directory)), ct);
        }
    }
}
