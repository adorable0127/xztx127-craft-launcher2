using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Services.Plugins;

public sealed record PluginUpgradeResult(string Package, string Report, string Fingerprint);

/// <summary>Builds in a copy and exports a reviewable v2 package. It never sends source to a remote AI.</summary>
internal static class PluginUpgradeService
{
    internal static async Task<PluginUpgradeResult> PrepareAsync(LoadedPluginInfo plugin, string? source,
        string destination, IProgress<string>? progress, CancellationToken ct)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "XCL2-upgrade-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
        try
        {
            var payload = Path.Combine(workspace, "payload"); Directory.CreateDirectory(payload);
            var original = Path.GetDirectoryName(plugin.FilePath)!;
            var manifestPath = Path.Combine(original, "plugin.json");
            JsonObject manifest = File.Exists(manifestPath) ? JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, ct))!.AsObject() : new JsonObject();
            string entry = Path.GetFileName(plugin.FilePath);
            var previousManifest = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (source != null)
            {
                progress?.Report("复制源码并准备本地编译…"); var working = Path.Combine(workspace, "source");
                string project;
                if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                { ExtractSource(source, working); var projects = Directory.GetFiles(working, "*.csproj", SearchOption.AllDirectories); if (projects.Length != 1) throw new IOException("源码 ZIP 必须包含一个 .csproj；多项目请直接选择入口 .csproj。"); project = projects[0]; }
                else
                { CopySource(Path.GetDirectoryName(source)!, working, ct); project = Path.Combine(working, Path.GetFileName(source)); }
                // Project evaluation/MSBuild targets may execute code; the dialog explicitly discloses this before entry.
                var output = Path.Combine(workspace, "build");
                var hostAssemblyPath = ResolveHostAssemblyPath();
                var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(project)!, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "build", project, "-c", "Release", "-o", output, "-p:Xcl2HostPath=" + hostAssemblyPath, "-p:NuGetAudit=false" }) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi) ?? throw new IOException("无法运行 dotnet SDK。");
                using var compilation = CancellationTokenSource.CreateLinkedTokenSource(ct); compilation.CancelAfter(TimeSpan.FromMinutes(5));
                var log = new StringBuilder(); var gate = new object();
                async Task Drain(StreamReader reader)
                { while (await reader.ReadLineAsync(compilation.Token) is { } line) { lock (gate) { if (log.Length < 200000) log.AppendLine(line); } progress?.Report(line.Length > 1000 ? line[..1000] : line); } }
                var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
                try { await process.WaitForExitAsync(compilation.Token); compilation.CancelAfter(TimeSpan.FromSeconds(5)); await Task.WhenAll(stdout, stderr); }
                catch { compilation.Cancel(); try { if (!process.HasExited) process.Kill(true); } catch { } try { await Task.WhenAll(stdout, stderr); } catch { } throw; }
                if (process.ExitCode != 0) throw new IOException("源码编译失败，原插件未修改：\n" + log.ToString()[..Math.Min(log.Length, 8000)]);
                if (!File.Exists(Path.Combine(output, entry))) throw new IOException($"编译输出没有 {entry}。请在项目中保留入口 AssemblyName，或使用正确的插件入口项目。原插件未修改。");
                CopySource(output, payload, ct, skipBuild: false);
                // The host contract is shared with XCL2 and must not be shipped as a private duplicate.
                var hostName = Path.GetFileName(ResolveHostAssemblyPath()); var copiedHost = Path.Combine(payload, hostName);
                if (!hostName.Equals(entry, StringComparison.OrdinalIgnoreCase) && File.Exists(copiedHost)) File.Delete(copiedHost);
            }
            else
            {
                progress?.Report("复制已安装插件，迁移清单…");
                if (File.Exists(Path.Combine(original, ".xcl-plugin.json"))) CopySource(original, payload, ct, skipBuild: false);
                else File.Copy(plugin.FilePath, Path.Combine(payload, entry));
            }
            foreach (var name in new[] { ".xcl-plugin.json", PluginPackageSignature.FileName }) { var file = Path.Combine(payload, name); if (File.Exists(file)) File.Delete(file); }
            var id = plugin.Metadata?.DeclaredId ?? plugin.Plugin?.Id ?? throw new IOException("无法确定插件 ID，不能安全迁移。"); PluginManifestReader.ValidateId(id);
            manifest["schemaVersion"] = 2; manifest["id"] = id; manifest["name"] = plugin.Metadata?.Name ?? plugin.Plugin?.DisplayName ?? id;
            var version = plugin.Metadata?.Version ?? plugin.Plugin?.Version ?? "1.0.0";
            try { _ = PluginSemanticVersion.Parse(version); } catch { version = "1.0.0"; }
            manifest["version"] = version; manifest["entry"] = entry; manifest["kind"] = Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "exe" : "dll";
            manifest["minApiVersion"] = "2.0"; manifest.Remove("maxApiVersion");
            if (manifest["permissions"] == null) manifest["permissions"] = new JsonArray("ui.basic", "ui.components");
            if (manifest["dependencies"] == null) manifest["dependencies"] = new JsonArray();
            if (manifest["conflicts"] == null) manifest["conflicts"] = new JsonArray();
            var report = $"XCL2 插件迁移报告\n插件：{id}\n操作：{(source == null ? "复制现有二进制；迁移清单和签名" : "在源码副本中本地编译；迁移清单和签名")}\n清单：schemaVersion 2；API 最低版本 2.0\n权限：{manifest["permissions"]}\n签名：本机临时 ECDSA 密钥，仅校验包完整性，不代表原作者或可信发布者身份。\n状态：未执行插件功能测试，接口或运行时依赖可能仍需开发者修改。\n源码没有上传到远程服务；dotnet restore 可能访问项目指定的软件源。\n安装后保持禁用，用户检查权限后自行启用。\n\n迁移前清单：\n{previousManifest}\n\n迁移后清单：\n{manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}\n";
            await File.WriteAllTextAsync(Path.Combine(payload, "migration-report.txt"), report, ct);
            await File.WriteAllTextAsync(Path.Combine(payload, "plugin.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested(); var fingerprint = PluginPackageSignature.SignLocal(payload); _ = PluginPackageSignature.Verify(payload);
            _ = PluginManifestReader.ReadEntry(payload);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { ZipFile.CreateFromDirectory(payload, temporary); ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, false); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            progress?.Report("已生成并校验升级包，等待安装确认。"); return new(destination, report, fingerprint);
        }
        finally { try { Directory.Delete(workspace, true); } catch { } }
    }
    private static string ResolveHostAssemblyPath()
    {
        var assembly = typeof(IPlugin).Assembly;
        // Assembly.Location is empty for assemblies bundled into a single-file application.
        if (!string.IsNullOrWhiteSpace(assembly.Location))
            return assembly.Location;

        var assemblyName = assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new IOException("无法确定 XCL2 插件 API 程序集名称。");

        // Source-plugin compilation needs a real file path to reference the host contract.
        var bundledHostPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (File.Exists(bundledHostPath))
            return bundledHostPath;

        throw new IOException(
            $"当前启动器以单文件方式运行，且目录中找不到插件 API 程序集：{bundledHostPath}。无法编译插件源码；请在应用目录提供该 API DLL。");
    }

    private static void CopySource(string source, string destination, CancellationToken ct, bool skipBuild = true)
    {
        long bytes = 0; int count = 0;
        void Walk(string input, string output)
        {
            WorldArchiveService.RejectLinks(input); Directory.CreateDirectory(output);
            foreach (var file in Directory.EnumerateFiles(input))
            {
                ct.ThrowIfCancellationRequested(); WorldArchiveService.RejectLinks(file);
                bytes += new FileInfo(file).Length;
                if (bytes > 512L * 1024 * 1024 || ++count > 10000) throw new IOException("源码或插件副本超过 512 MiB / 1 万文件上限。");
                File.Copy(file, Path.Combine(output, Path.GetFileName(file)), true);
            }
            foreach (var dir in Directory.EnumerateDirectories(input))
            {
                if (Path.GetFileName(dir) is ".git" or ".vs" || skipBuild && Path.GetFileName(dir) is "bin" or "obj") continue;
                Walk(dir, Path.Combine(output, Path.GetFileName(dir)));
            }
        }
        Walk(source, destination);
    }

    private static void ExtractSource(string file, string destination)
    {
        Directory.CreateDirectory(destination); string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(file); long total = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count > 10000) throw new IOException("源码包文件过多。");
        foreach (var entry in zip.Entries)
        {
            total += entry.Length; var name = entry.FullName.Replace('\\', '/');
            if (total > 256L * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "." or "..")) throw new IOException("源码包过大或包含不安全路径。");
            var target = Path.GetFullPath(Path.Combine(destination, name)); if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(target)) throw new IOException("源码包路径重复或越界。");
            if (name.EndsWith('/')) Directory.CreateDirectory(target); else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, false); }
        }
    }
}
