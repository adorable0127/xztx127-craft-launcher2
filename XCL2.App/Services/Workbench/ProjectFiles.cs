using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XCL2.App.Services.Workbench;

public sealed record ProjectEntry(string Path, long Bytes, DateTime ModifiedUtc);
public sealed record ProjectMatch(string Path, int Line, int Column, string Text);
public sealed record ProjectSearchResult(IReadOnlyList<ProjectMatch> Matches, int FilesScanned, bool Truncated, IReadOnlyList<string> Warnings);
public sealed record ProjectTextPage(string Path, string Sha256, int StartLine, int? NextLine, string Text);

/// <summary>Bounded, cancellable project I/O. No junctions, symlinks, or traversal outside a root.</summary>
public static class ProjectFiles
{
    public const long MaxFileBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", ".xcl2" };

    public static string Resolve(string root, string relative, bool allowRoot = false)
    {
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!(allowRoot && path.Equals(root, comparison)) && !path.StartsWith((Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar), comparison))
            throw new UnauthorizedAccessException("路径不在项目范围内。");
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("项目工具不跟随符号链接或目录联接。");
        return path;
    }

    public static IEnumerable<ProjectEntry> Enumerate(string root, string pattern = "*", int limit = 20000, CancellationToken ct = default, Func<string, bool>? allowed = null)
    {
        root = Resolve(root, "", true);
        if (limit is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(limit));
        var pending = new Stack<string>(); pending.Push(root); int count = 0, directories = 0;
        while (pending.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            if (++directories > 20000) throw new IOException("目录数量超过扫描上限，请缩小搜索范围。");
            var dir = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                if (allowed != null && !allowed(entry)) continue;
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                if ((attrs & FileAttributes.Directory) != 0)
                { if (!Ignored.Contains(Path.GetFileName(entry))) pending.Push(entry); continue; }
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, relative, true) &&
                    !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(entry), true)) continue;
                var info = new FileInfo(entry);
                yield return new(relative, info.Length, info.LastWriteTimeUtc);
                if (++count >= limit) yield break;
            }
        }
    }

    public static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new IOException("文本文件超过 16 MiB 上限，请缩小范围。");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static (string Text, Encoding Encoding, bool Bom) Decode(byte[] bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true); int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new InvalidDataException("二进制文件不能作为文本编辑。");
        return (text, encoding, skip != 0);
    }
    public static byte[] EncodeLike(string text, byte[]? original)
    {
        var (_, encoding, bom) = original == null ? ("", (Encoding)new UTF8Encoding(false, true), false) : Decode(original);
        var payload = encoding.GetBytes(text);
        if (!bom) return payload;
        var preamble = encoding is UTF8Encoding ? new byte[] { 0xEF, 0xBB, 0xBF } : encoding.GetPreamble();
        return preamble.Concat(payload).ToArray();
    }
    public static ProjectTextPage ReadPage(string root, string path, int startLine = 1, int lineCount = 200, CancellationToken ct = default)
    {
        if (startLine < 1 || lineCount is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(startLine));
        var bytes = ReadBytes(Resolve(root, path)); var text = Decode(bytes).Text;
        using var reader = new StringReader(text); var output = new StringBuilder(); string? line; int number = 0, emitted = 0;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested(); number++;
            if (number < startLine) continue;
            if (emitted == lineCount || output.Length + line.Length > 80000)
            {
                if (emitted == 0) throw new IOException("单行超过 80000 字符，请使用字节读取接口。");
                return new(path, Hash(bytes), startLine, number, output.ToString());
            }
            output.Append(number).Append(": ").AppendLine(line); emitted++;
        }
        return new(path, Hash(bytes), startLine, null, output.ToString());
    }
    public static ProjectSearchResult Search(string root, string query, string pattern = "*", bool regex = false, bool caseSensitive = false, int maxResults = 200, CancellationToken ct = default, Func<string, bool>? allowed = null)
    {
        if (string.IsNullOrEmpty(query) || query.Length > 2000 || maxResults is < 1 or > 1000) throw new ArgumentException("查询或结果上限无效。");
        var expression = new Regex(regex ? query : Regex.Escape(query), caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        var found = new List<ProjectMatch>(); var warnings = new List<string>(); int scanned = 0, outputChars = 0; long bytes = 0;
        foreach (var file in Enumerate(root, pattern, 20001, ct, allowed))
        {
            if (++scanned > 20000 || (bytes += file.Bytes) > 128 * 1024 * 1024) return new(found, scanned - 1, true, warnings);
            if (file.Bytes > MaxFileBytes) { if (warnings.Count < 20) warnings.Add(file.Path + ": 超过大小上限"); continue; }
            try
            {
                using var reader = new StringReader(Decode(ReadBytes(Resolve(root, file.Path))).Text);
                string? line; int number = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    ct.ThrowIfCancellationRequested(); number++;
                    var match = expression.Match(line);
                    if (!match.Success) continue;
                    var snippet = line.Length > 800 ? line.Substring(Math.Max(0, match.Index - 80), Math.Min(800, line.Length - Math.Max(0, match.Index - 80))) : line;
                    if ((outputChars += snippet.Length + file.Path.Length + 80) > 80000) return new(found, scanned, true, warnings);
                    found.Add(new(file.Path, number, match.Index + 1, snippet));
                    if (found.Count >= maxResults) return new(found, scanned, true, warnings);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or DecoderFallbackException or UnauthorizedAccessException or RegexMatchTimeoutException)
            { if (warnings.Count < 20) warnings.Add(file.Path + ": " + ex.Message); }
        }
        return new(found, scanned, false, warnings);
    }
    public static string Patch(string original, string oldText, string newText)
    {
        if (string.IsNullOrEmpty(oldText)) throw new ArgumentException("局部补丁 old_text 不能为空。");
        int index = original.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0 || original.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("补丁必须精确匹配唯一位置；请重新读取并增加上下文。");
        return original[..index] + newText + original[(index + oldText.Length)..];
    }
    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".xcl-" + Guid.NewGuid().ToString("N");
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
