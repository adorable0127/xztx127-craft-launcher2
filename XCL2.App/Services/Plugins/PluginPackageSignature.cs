using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Services.Plugins;

/// <summary>ECDSA integrity signature. A locally generated key is NOT a trusted publisher certificate.</summary>
public static class PluginPackageSignature
{
    public const string FileName = "plugin.signature.json";
    private sealed record Envelope(string Algorithm, string Origin, string PublicKey, string Payload, string Signature);
    private static IEnumerable<string> Files(string directory)
    {
        WorldArchiveService.RejectLinks(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        { WorldArchiveService.RejectLinks(file); yield return file; }
        foreach (var sub in Directory.EnumerateDirectories(directory)) foreach (var file in Files(sub)) yield return file;
    }
    private static SortedDictionary<string, string> Hashes(string directory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in Files(directory))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (relative is FileName or ".xcl-plugin.json") continue;
            total += new FileInfo(file).Length;
            if (total > 512L * 1024 * 1024 || result.Count >= 10000) throw new InvalidDataException("插件包超过校验上限。");
            using var stream = File.OpenRead(file); result.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
        }
        return result;
    }
    public static string SignLocal(string directory)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(Hashes(directory));
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); var key = signer.ExportSubjectPublicKeyInfo();
        var envelope = new Envelope("ECDSA-P256-SHA256", "local-migration-untrusted", Convert.ToBase64String(key), Convert.ToBase64String(payload), Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256)));
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
        return Convert.ToHexString(SHA256.HashData(key));
    }
    public static string Verify(string directory)
    {
        var path = Path.Combine(directory, FileName); WorldArchiveService.RejectLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("插件签名文件过大。");
        var signature = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path)) ?? throw new InvalidDataException("签名为空。");
        if (signature.Algorithm != "ECDSA-P256-SHA256") throw new InvalidDataException("不支持此签名算法。");
        var key = Convert.FromBase64String(signature.PublicKey); var payload = Convert.FromBase64String(signature.Payload);
        using var verifier = ECDsa.Create(); verifier.ImportSubjectPublicKeyInfo(key, out int read);
        if (read != key.Length || verifier.KeySize != 256 || !verifier.VerifyData(payload, Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256)) throw new InvalidDataException("插件包签名校验失败。");
        var expected = JsonSerializer.Deserialize<SortedDictionary<string, string>>(payload) ?? throw new InvalidDataException("签名清单为空。");
        var actual = Hashes(directory);
        if (expected.Count != actual.Count || actual.Any(p => !expected.TryGetValue(p.Key, out var hash) || hash != p.Value)) throw new InvalidDataException("插件包内容已改变，签名与文件不一致。");
        return Convert.ToHexString(SHA256.HashData(key));
    }
}
