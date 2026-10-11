using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XCL2.App.Services.Worlds;

public sealed record NbtList(byte ElementType, List<NbtTag> Items);
public sealed record NbtTag(byte Type, object Value)
{
    public Dictionary<string, NbtTag> Compound => Value as Dictionary<string, NbtTag> ?? throw new InvalidDataException("需要 NBT Compound。");
    public NbtTag? Get(string name) => Value is Dictionary<string, NbtTag> map && map.TryGetValue(name, out var value) ? value : null;
    public long Number => Value switch { sbyte n => n, short n => n, int n => n, long n => n, _ => throw new InvalidDataException("需要 NBT 整数。") };
    public string Text => Value as string ?? "";
    public IReadOnlyList<NbtTag> Items => Value is NbtList list ? list.Items : Array.Empty<NbtTag>();
}

/// <summary>Java Edition NBT, gzip/zlib/raw; unknown fields and all twelve tag types survive edits.</summary>
public sealed class NbtDocument
{
    public string Name { get; }
    public NbtTag Root { get; }
    private readonly int _compression;
    private const int Limit = 64 * 1024 * 1024;
    public NbtDocument(string name, NbtTag root, int compression = 1) { Name = name; Root = root; _compression = compression; }
    public static NbtDocument Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > Limit) throw new InvalidDataException("NBT 文件超过 64 MiB 上限。");
        int a = input.ReadByte(), b = input.ReadByte(); input.Position = 0;
        int compression = a == 0x1f && b == 0x8b ? 1 : a == 0x78 ? 2 : 0;
        using Stream decoded = compression == 1 ? new GZipStream(input, CompressionMode.Decompress) :
            compression == 2 ? new ZLibStream(input, CompressionMode.Decompress) : input;
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = decoded.Read(buffer)) > 0)
        { if (bytes.Length + count > Limit) throw new InvalidDataException("NBT 解压后超过上限。"); bytes.Write(buffer, 0, count); }
        bytes.Position = 0;
        var reader = new Reader(bytes);
        var type = reader.Byte();
        if (type != 10) throw new InvalidDataException("仅支持 Java 版 Compound NBT；基岩版 level.dat / mcstructure 暂不支持。");
        var name = reader.String();
        var root = reader.Tag(type, 0);
        if (bytes.Position != bytes.Length) throw new InvalidDataException("NBT 尾部有无效数据。");
        return new(name, root, compression);
    }
    public void Save(string path)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Stream encoded = _compression == 1 ? new GZipStream(output, CompressionLevel.Optimal, true) :
            _compression == 2 ? new ZLibStream(output, CompressionLevel.Optimal, true) : output;
        encoded.WriteByte(Root.Type); WriteString(encoded, Name); WriteTag(encoded, Root);
    }
    public string Describe(int maxChars = 24000)
    {
        var text = new StringBuilder();
        void Walk(string name, NbtTag tag, int depth)
        {
            if (text.Length >= maxChars || depth > 16) return;
            text.Append(' ', depth * 2).Append(name).Append(" : ");
            if (tag.Value is Dictionary<string, NbtTag> map)
            { text.AppendLine("Compound"); foreach (var pair in map) Walk(pair.Key, pair.Value, depth + 1); }
            else if (tag.Value is NbtList list)
            { text.AppendLine($"List<{list.ElementType}> [{list.Items.Count}]"); for (int i = 0; i < Math.Min(32, list.Items.Count); i++) Walk($"[{i}]", list.Items[i], depth + 1); }
            else if (tag.Value is Array array) text.AppendLine($"{array.GetType().Name} [{array.Length}]");
            else text.AppendLine(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        Walk(Name, Root, 0);
        return text.Length <= maxChars ? text.ToString() : text.ToString(0, maxChars) + "\n…显示已截断";
    }
    private sealed class Reader(Stream input)
    {
        private int _nodes;
        public byte Byte() { int value = input.ReadByte(); return value < 0 ? throw new EndOfStreamException("NBT 数据不完整。") : (byte)value; }
        private byte[] Bytes(int count) { if (count < 0 || count > Limit || count > input.Length - input.Position) throw new InvalidDataException("无效 NBT 长度。"); var bytes = new byte[count]; input.ReadExactly(bytes); return bytes; }
        private int Length(int width) { int n = BinaryPrimitives.ReadInt32BigEndian(Bytes(4)); if (n < 0 || (long)n * width > input.Length - input.Position) throw new InvalidDataException("无效 NBT 数组长度。"); return n; }
        public string String()
        {
            var bytes = Bytes(BinaryPrimitives.ReadUInt16BigEndian(Bytes(2)));
            // Java modified UTF-8 uses UTF-16 code units, including surrogate pairs and encoded NUL.
            var result = new StringBuilder();
            for (int i = 0; i < bytes.Length;)
            {
                int c = bytes[i++];
                if (c is > 0 and < 128) result.Append((char)c);
                else if ((c & 0xe0) == 0xc0 && i < bytes.Length && (bytes[i] & 0xc0) == 0x80) result.Append((char)(((c & 31) << 6) | (bytes[i++] & 63)));
                else if ((c & 0xf0) == 0xe0 && i + 1 < bytes.Length && (bytes[i] & 0xc0) == 0x80 && (bytes[i + 1] & 0xc0) == 0x80)
                { result.Append((char)(((c & 15) << 12) | ((bytes[i] & 63) << 6) | (bytes[i + 1] & 63))); i += 2; }
                else throw new InvalidDataException("NBT Modified UTF-8 无效。");
            }
            return result.ToString();
        }
        public NbtTag Tag(byte type, int depth)
        {
            if (depth > 64 || ++_nodes > 1_000_000) throw new InvalidDataException("NBT 嵌套或节点超过上限。");
            object value;
            switch (type)
            {
                case 1: value = unchecked((sbyte)Byte()); break;
                case 2: value = BinaryPrimitives.ReadInt16BigEndian(Bytes(2)); break;
                case 3: value = BinaryPrimitives.ReadInt32BigEndian(Bytes(4)); break;
                case 4: value = BinaryPrimitives.ReadInt64BigEndian(Bytes(8)); break;
                case 5: value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(Bytes(4))); break;
                case 6: value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(Bytes(8))); break;
                case 7: value = Bytes(Length(1)); break;
                case 8: value = String(); break;
                case 9:
                    byte element = Byte(); int size = Length(1);
                    if (element > 12 || element == 0 && size != 0) throw new InvalidDataException("无效 NBT List 类型。");
                    var items = new List<NbtTag>(); for (int i = 0; i < size; i++) items.Add(Tag(element, depth + 1));
                    value = new NbtList(element, items); break;
                case 10:
                    var map = new Dictionary<string, NbtTag>(StringComparer.Ordinal); byte child;
                    while ((child = Byte()) != 0) { var name = String(); if (!map.TryAdd(name, Tag(child, depth + 1))) throw new InvalidDataException("重复 NBT 字段。"); }
                    value = map; break;
                case 11:
                    int ints = Length(4); var ia = new int[ints]; for (int i = 0; i < ints; i++) ia[i] = BinaryPrimitives.ReadInt32BigEndian(Bytes(4)); value = ia; break;
                case 12:
                    int longs = Length(8); var la = new long[longs]; for (int i = 0; i < longs; i++) la[i] = BinaryPrimitives.ReadInt64BigEndian(Bytes(8)); value = la; break;
                default: throw new InvalidDataException("未知 NBT Tag 类型。");
            }
            return new(type, value);
        }
    }
    private static void Integer(Stream stream, long value, int width)
    { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); stream.Write(bytes[(8 - width)..]); }
    private static void WriteString(Stream stream, string value)
    {
        using var bytes = new MemoryStream();
        foreach (int c in value)
        {
            if (c is > 0 and < 128) bytes.WriteByte((byte)c);
            else if (c < 2048) { bytes.WriteByte((byte)(0xc0 | (c >> 6))); bytes.WriteByte((byte)(0x80 | (c & 63))); }
            else { bytes.WriteByte((byte)(0xe0 | (c >> 12))); bytes.WriteByte((byte)(0x80 | ((c >> 6) & 63))); bytes.WriteByte((byte)(0x80 | (c & 63))); }
        }
        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("NBT 字符串过长。");
        Integer(stream, bytes.Length, 2); bytes.Position = 0; bytes.CopyTo(stream);
    }
    private static void WriteTag(Stream stream, NbtTag tag)
    {
        switch (tag.Type)
        {
            case 1: Integer(stream, tag.Number, 1); break;
            case 2: Integer(stream, tag.Number, 2); break;
            case 3: Integer(stream, tag.Number, 4); break;
            case 4: Integer(stream, tag.Number, 8); break;
            case 5: Integer(stream, BitConverter.SingleToInt32Bits((float)tag.Value), 4); break;
            case 6: Integer(stream, BitConverter.DoubleToInt64Bits((double)tag.Value), 8); break;
            case 7: var ba = (byte[])tag.Value; Integer(stream, ba.Length, 4); stream.Write(ba); break;
            case 8: WriteString(stream, tag.Text); break;
            case 9: var list = (NbtList)tag.Value; stream.WriteByte(list.ElementType); Integer(stream, list.Items.Count, 4); foreach (var item in list.Items) WriteTag(stream, item); break;
            case 10: foreach (var pair in tag.Compound) { stream.WriteByte(pair.Value.Type); WriteString(stream, pair.Key); WriteTag(stream, pair.Value); } stream.WriteByte(0); break;
            case 11: var ia = (int[])tag.Value; Integer(stream, ia.Length, 4); foreach (int n in ia) Integer(stream, n, 4); break;
            case 12: var la = (long[])tag.Value; Integer(stream, la.Length, 4); foreach (long n in la) Integer(stream, n, 8); break;
            default: throw new InvalidDataException("未知 NBT Tag 类型。");
        }
    }
}
