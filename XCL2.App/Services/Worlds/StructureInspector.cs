using System.IO;

namespace XCL2.App.Services.Worlds;

public sealed record ProjectionCell(int X, int Y, int Z, string Block);
public sealed record StructurePreview(string Format, int Width, int Height, int Depth, long Blocks,
    IReadOnlyDictionary<string, long> Materials, IReadOnlyList<ProjectionCell> TopView, string NbtText);

/// <summary>Actual block projection, with bounded allocation. Colors are symbolic, not game textures.</summary>
public static class StructureInspector
{
    private const long MaxBlocks = 4_000_000;
    public static StructurePreview Read(string path)
    {
        WorldArchiveService.RejectLinks(path);
        var doc = NbtDocument.Load(path); var root = doc.Root;
        var materials = new Dictionary<string, long>(StringComparer.Ordinal);
        var top = new Dictionary<(int, int), ProjectionCell>();
        long visited = 0, solid = 0;
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        void Bound(int x, int y, int z)
        { minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z); }
        void Add(int x, int y, int z, string block)
        {
            if (++visited > MaxBlocks) throw new InvalidDataException("预览超过 400 万方块上限。");
            Bound(x, y, z);
            if (block is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air" or "legacy:0:0") return;
            solid++; materials[block] = materials.GetValueOrDefault(block) + 1;
            if (!top.TryGetValue((x, z), out var previous) || y > previous.Y) top[(x, z)] = new(x, y, z, block);
            if (top.Count > 500_000) throw new InvalidDataException("俯视图超过 50 万列上限。");
        }
        static int Int(NbtTag? tag) => checked((int)(tag?.Number ?? throw new InvalidDataException("缺少结构尺寸或坐标。")));
        static (int x, int y, int z) Vector(NbtTag? tag)
        {
            if (tag == null) throw new InvalidDataException("缺少坐标。");
            if (tag.Items.Count == 3) return (Int(tag.Items[0]), Int(tag.Items[1]), Int(tag.Items[2]));
            return (Int(tag.Get("x")), Int(tag.Get("y")), Int(tag.Get("z")));
        }
        static string Name(NbtTag item) => item.Get("Name")?.Text is { Length: > 0 } name ? name : throw new InvalidDataException("缺少方块名称。");
        static long Volume(int x, int y, int z)
        { long volume = checked((long)x * y * z); if (x <= 0 || y <= 0 || z <= 0 || volume > MaxBlocks) throw new InvalidDataException("结构尺寸无效或过大。"); return volume; }
        string format;
        if (root.Get("Regions") is { } regions)
        {
            format = "Litematica 投影";
            foreach (var region in regions.Compound.Values)
            {
                var size = Vector(region.Get("Size")); var pos = Vector(region.Get("Position"));
                int sx = checked(Math.Abs(size.x)), sy = checked(Math.Abs(size.y)), sz = checked(Math.Abs(size.z));
                long count = Volume(sx, sy, sz);
                if (visited + count > MaxBlocks) throw new InvalidDataException("投影总方块数过大。");
                var palette = region.Get("BlockStatePalette")?.Items.Select(Name).ToArray() ?? throw new InvalidDataException("缺少调色板。");
                if (palette.Length == 0) throw new InvalidDataException("空调色板。");
                var packed = region.Get("BlockStates")?.Value as long[] ?? throw new InvalidDataException("缺少方块数据。");
                int bits = Math.Max(2, (int)Math.Ceiling(Math.Log2(palette.Length))); ulong mask = (1UL << bits) - 1;
                if ((long)packed.Length * 64 < count * bits) throw new InvalidDataException("投影方块数据不完整。");
                int ox = checked(pos.x + Math.Min(0, size.x + 1)), oy = checked(pos.y + Math.Min(0, size.y + 1)), oz = checked(pos.z + Math.Min(0, size.z + 1));
                for (long i = 0; i < count; i++)
                {
                    long bit = i * bits; int word = (int)(bit / 64), shift = (int)(bit % 64);
                    ulong id = unchecked((ulong)packed[word]) >> shift;
                    if (shift + bits > 64) id |= unchecked((ulong)packed[word + 1]) << (64 - shift);
                    id &= mask; if (id >= (ulong)palette.Length) throw new InvalidDataException("投影调色板索引越界。");
                    Add(checked(ox + (int)(i % sx)), checked(oy + (int)(i / (sx * (long)sz))), checked(oz + (int)(i / sx % sz)), palette[(int)id]);
                }
            }
        }
        else if (root.Get("palette") is { } paletteTag && root.Get("blocks") is { } blocks)
        {
            format = "Java 结构方块";
            var size = Vector(root.Get("size")); _ = Volume(size.x, size.y, size.z);
            Bound(0, 0, 0); Bound(size.x - 1, size.y - 1, size.z - 1);
            var palette = paletteTag.Items.Select(Name).ToArray();
            foreach (var block in blocks.Items)
            {
                int id = Int(block.Get("state")); var p = Vector(block.Get("pos"));
                if (id < 0 || id >= palette.Length || p.x < 0 || p.x >= size.x || p.y < 0 || p.y >= size.y || p.z < 0 || p.z >= size.z) throw new InvalidDataException("结构坐标或调色板索引越界。");
                Add(p.x, p.y, p.z, palette[id]);
            }
        }
        else if (root.Get("Blocks")?.Value is byte[] legacy)
        {
            format = "MCEdit Schematic（旧版数字方块 ID）";
            int sx = Int(root.Get("Width")), sy = Int(root.Get("Height")), sz = Int(root.Get("Length")); long count = Volume(sx, sy, sz);
            var data = root.Get("Data")?.Value as byte[]; var high = root.Get("AddBlocks")?.Value as byte[];
            if (legacy.Length != count || data != null && data.Length != count || high != null && high.Length < (count + 1) / 2) throw new InvalidDataException("Schematic 数组长度不匹配。");
            for (int i = 0; i < count; i++)
            {
                int id = legacy[i] | (high == null ? 0 : ((high[i / 2] >> ((i % 2) * 4)) & 15) << 8);
                Add(i % sx, i / (sx * sz), i / sx % sz, $"legacy:{id}:{data?[i] ?? 0}");
            }
        }
        else
        {
            var schematic = root.Get("Schematic") ?? root;
            if (schematic.Get("Palette") == null && schematic.Get("Blocks")?.Get("Palette") == null)
                throw new InvalidDataException("支持 .litematic、Java 结构 .nbt、MCEdit .schematic 和 Sponge .schem v2/v3。");
            format = "Sponge Schematic";
            int sx = Int(schematic.Get("Width")), sy = Int(schematic.Get("Height")), sz = Int(schematic.Get("Length")); long count = Volume(sx, sy, sz);
            var container = schematic.Get("Blocks") ?? schematic;
            var palette = container.Get("Palette")!.Compound.ToDictionary(p => Int(p.Value), p => p.Key);
            var data = (container.Get("Data") ?? container.Get("BlockData"))?.Value as byte[] ?? throw new InvalidDataException("缺少 Sponge 方块数据。");
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                int id = 0, shift = 0; byte b;
                do { if (offset >= data.Length || shift > 28) throw new InvalidDataException("损坏的 VarInt。"); b = data[offset++]; id |= (b & 127) << shift; shift += 7; } while ((b & 128) != 0);
                if (!palette.TryGetValue(id, out var name)) throw new InvalidDataException("Sponge 调色板索引越界。");
                Add(i % sx, i / (sx * sz), i / sx % sz, name.Split('[')[0]);
            }
        }
        if (minX == int.MaxValue) throw new InvalidDataException("结构没有区域数据。");
        return new(format, checked(maxX - minX + 1), checked(maxY - minY + 1), checked(maxZ - minZ + 1), solid,
            materials, top.Values.Select(p => p with { X = p.X - minX, Y = p.Y - minY, Z = p.Z - minZ }).ToArray(), doc.Describe());
    }
    public static (byte R, byte G, byte B) Color(string block)
    {
        uint hash = 2166136261; foreach (char c in block) hash = (hash ^ c) * 16777619;
        return ((byte)(65 + hash % 160), (byte)(65 + (hash >> 8) % 160), (byte)(65 + (hash >> 16) % 160));
    }
}
