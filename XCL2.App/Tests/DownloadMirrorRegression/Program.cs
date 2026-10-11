using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
var github = "https://github.com/example/repository/releases/download/v1.0/test.zip";
Expect(DownloadMirrorPolicy.Mirrors(github).Count == 2, "Both documented GitHub endpoints must be available.");
Expect(DownloadMirrorPolicy.Mirrors("https://cdn.modrinth.com/data/project/versions/v/test.jar").Contains("https://mod.mcimirror.top/data/project/versions/v/test.jar"), "MCIM Modrinth files");
Expect(DownloadMirrorPolicy.Mirrors("https://mediafilez.forgecdn.net/files/1/2/test.jar").Contains("https://mod.mcimirror.top/files/1/2/test.jar"), "MCIM CurseForge files");
foreach (var privateUrl in new[]
{
    github + "?token=secret", github + "?X-Amz-Signature=secret", github + "#access_token=secret",
    github.Replace("github.com", "user:secret@github.com"), github.Replace("github.com", "github.com.evil.invalid"),
    github.Replace("https:", "http:"), "https://api.modrinth.com/v2/search?query=test&access_token=secret",
    "https://api.modrinth.com/v2/search?query=test&%74oken=secret"
}) Expect(DownloadMirrorPolicy.Mirrors(privateUrl).Count == 0, "Private/spoofed URL must not cross to mirror: " + privateUrl);
Expect(DownloadMirrorPolicy.Mirrors("https://maven.neoforged.net/releases-extra/test.jar").Count == 0, "Path prefix requires a segment boundary.");
Expect(DownloadMirrorPolicy.Mirrors("https://api.modrinth.com/v2/search?query=sodium&limit=20").Single().StartsWith("https://mod.mcimirror.top/modrinth/v2/search?"), "Public search query preserved.");
var disabled = new DownloadMirrorConfig { EnableCommunityMirrors = false, EnableGithubMirrors = false };
Expect(DownloadMirrorPolicy.Mirrors(github, disabled).Count == 0, "GitHub option disabled.");
Expect(DownloadMirrorPolicy.Mirrors("https://cdn.modrinth.com/data/a.jar", disabled).Count == 0, "Community option disabled.");
var rule = new DownloadMirrorRule { Name = "Private mirror", SourcePrefix = "https://download.example.org/files", MirrorPrefix = "https://mirror.example.org/cache" };
disabled.CustomRules.Add(rule);
Expect(DownloadMirrorPolicy.ValidateRule(rule) == null, "Valid user mapping.");
Expect(DownloadMirrorPolicy.Mirrors("https://download.example.org/files/Case.jar", disabled).Single() == "https://mirror.example.org/cache/Case.jar", "Preserve case-sensitive file path.");
Expect(DownloadMirrorPolicy.Mirrors("https://download.example.org/files-evil/test.jar", disabled).Count == 0, "Custom prefix boundary.");
Expect(DownloadMirrorPolicy.Mirrors("https://download.example.org/files/test.jar?signature=abc", disabled).Count == 0, "User mirror cannot receive signature.");
Expect(DownloadMirrorPolicy.ValidateRule(new DownloadMirrorRule { Name = "Bad", SourcePrefix = rule.SourcePrefix, MirrorPrefix = "https://user:secret@mirror.example.org/" }) != null, "Mirror credential rejected.");
DownloadEndpoints.ResetHealth();
DownloadEndpoints.ReportFailure("https://gh-proxy.org/test");
Expect(new Uri(DownloadEndpoints.Candidates(github, true)[0]).Host == "gh-proxy.com", "All mirrors participate in health ranking.");
DownloadEndpoints.ResetHealth();
Expect(DownloadEndpoints.Candidates(github, false)[0] == github, "Official preference.");
Expect(DownloadEndpoints.Candidates(github, true).Last() == github, "Mirror preference preserves official fallback.");

var root = Path.Combine(Path.GetTempPath(), "xcl-mirror-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "payload.zip");
var good = Encoding.UTF8.GetBytes("a complete, verified release file");
var sha256 = Convert.ToHexString(SHA256.HashData(good));
HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
try
{
    await File.WriteAllTextAsync(path, "old version");
    var calls = new List<string>();
    using (var client = new HttpClient(new Handler((request, ct) =>
    {
        calls.Add(request.RequestUri!.Host);
        Expect(File.ReadAllText(path) == "old version", "Existing file remains intact until verification succeeds.");
        return Task.FromResult(Bytes(request.RequestUri.Host == "github.com" ? good : Encoding.UTF8.GetBytes("corrupt")));
    })))
    {
        await DownloadEndpoints.DownloadFileWithFallbackAsync(client, github, true, path, "failed", expectedSha256: sha256);
        Expect(calls.SequenceEqual(new[] { "gh-proxy.org", "gh-proxy.com", "github.com" }), "Bad hash switches endpoint before retrying same mirror.");
        Expect(File.ReadAllBytes(path).SequenceEqual(good), "Verified candidate committed.");
        Expect(!Directory.EnumerateFiles(root, "*.fbpart-*").Any(), "All partial files cleaned.");
    }

    DownloadEndpoints.ResetHealth();
    calls.Clear();
    using (var client = new HttpClient(new Handler((request, ct) =>
    {
        calls.Add(request.RequestUri!.Host);
        return Task.FromResult(Bytes(good));
    })))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-only");
        await DownloadEndpoints.DownloadFileWithFallbackAsync(client, github, true, path, "failed");
        Expect(calls.SequenceEqual(new[] { "github.com" }), "Authorization never forwarded to third-party mirror.");
    }

    calls.Clear();
    using (var client = new HttpClient(new Handler((request, ct) =>
    {
        calls.Add(request.RequestUri!.Host);
        return Task.FromResult(Bytes(good));
    })))
    {
        await DownloadEndpoints.DownloadFileWithFallbackAsync(client, github + "?signature=private", true, path, "failed");
        Expect(calls.SequenceEqual(new[] { "github.com" }), "Signed URL stays direct.");
    }

    calls.Clear();
    DownloadEndpoints.ResetHealth();
    using (var client = new HttpClient(new Handler((request, ct) =>
    {
        calls.Add(request.RequestUri!.Host);
        return request.RequestUri.Host == "gh-proxy.org"
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated network timeout"))
            : Task.FromResult(Bytes(good));
    })))
    {
        await DownloadEndpoints.DownloadFileWithFallbackAsync(client, github, true, path, "failed");
        Expect(calls.SequenceEqual(new[] { "gh-proxy.org", "gh-proxy.com" }), "Network timeout switches source; user cancellation is separate.");
    }

    calls.Clear();
    using (var cancel = new CancellationTokenSource())
    using (var client = new HttpClient(new Handler((request, ct) =>
    {
        calls.Add(request.RequestUri!.Host);
        cancel.Cancel();
        return Task.FromCanceled<HttpResponseMessage>(ct);
    })))
    {
        try { await DownloadEndpoints.DownloadFileWithFallbackAsync(client, github, true, path, "failed", ct: cancel.Token); throw new Exception("Cancellation was swallowed."); }
        catch (OperationCanceledException) { checks++; }
        Expect(calls.Count == 1, "Cancelled download cannot start next candidate.");
        Expect(File.ReadAllBytes(path).SequenceEqual(good), "Cancellation preserves destination.");
    }

    calls.Clear();
    using (var client = new HttpClient(new Handler((request, ct) =>
    {
        calls.Add(request.RequestUri!.Host);
        return Task.FromResult(request.RequestUri.Host == "first.example.org"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Bytes(good));
    })))
    {
        await DownloadEndpoints.DownloadFilesWithFallbackAsync(client,
            new[] { "https://first.example.org/file.jar", "https://second.example.org/file.jar" }, false, path, "failed",
            expectedSha512: Convert.ToHexString(SHA512.HashData(good)), expectedSize: good.Length);
        Expect(calls.SequenceEqual(new[] { "first.example.org", "second.example.org" }), "Manifest alternate URLs must be tried.");
    }

    using (var client = new HttpClient(new Handler((request, ct) => Task.FromResult(Bytes(Encoding.UTF8.GetBytes("bad"))))))
    {
        try { await DownloadEndpoints.DownloadFileWithFallbackAsync(client, "https://download.example.org/test", false, path, "failed", expectedSha256: sha256); throw new Exception("Bad hash accepted."); }
        catch (IOException) { checks++; }
        Expect(File.ReadAllBytes(path).SequenceEqual(good), "All candidate failures preserve existing destination.");
        Expect(!Directory.EnumerateFiles(root, "*.fbpart-*").Any(), "Failure/cancellation clean staging files.");
    }
    Console.WriteLine($"Download mirror regression checks passed: {checks}");
}
finally { Directory.Delete(root, recursive: true); }

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
