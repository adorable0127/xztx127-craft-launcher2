using System.IO;
using System.IO.Compression;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 整合包「从零安装成一个全新实例」。
///
/// ===== 跟旧流程的区别 =====
/// 旧的 ModpackService.Import / ImportMrpackAsync 只做一件事：**把整合包里的文件解到某个目录**。
/// 它不管这个目录里有没有游戏本体、有没有对应的加载器——所以旧的"导入整合包"实际上是
/// "把 mods/config 覆盖进你当前那个实例"。后果：
///   - 整合包要 Fabric 1.20.1，你当前实例是原版 1.21 → 装完直接崩，用户完全不知道为什么；
///   - 整合包的 mod 和你原来装的 mod 混在一起互相打架；
///   - 想卸载整合包时根本分不清哪些文件是它带来的。
///
/// 这个服务补上缺的那一半：
///   1. 读整合包清单，拿到它声明的 **MC 版本 + 加载器 + 加载器版本**
///      （mrpack 读 modrinth.index.json 的 dependencies；.xclpack 读 manifest.json；
///      CurseForge 包读 manifest.json 的 minecraft.modLoaders）
///   2. 用用户指定的实例名新建一个**全新的、跟任何已有实例无关的**版本目录
///   3. 下载对应的原版本体（DownloadService）
///   4. 安装对应的加载器（ClientLoaderInstallService）
///   5. 最后才把整合包内容解进去
///
/// 任何一步失败都会把已经创建的目录清理掉（见 InstallToNewInstanceAsync 的 catch），
/// 不留一个半成品实例在版本列表里让用户以为能玩。
/// </summary>
public class ModpackInstallService
{
    private readonly AppConfig _config;
    private readonly ModpackService _modpackService = new();

    public ModpackInstallService(AppConfig config) => _config = config;

    /// <summary>从整合包清单里读出来的环境要求。</summary>
    public sealed record ModpackRequirements(
        string? Name,
        string? McVersion,
        string? Loader,          // Fabric / Quilt / Forge / NeoForge / null
        string? LoaderVersion);

    /// <summary>
    /// 只读清单、不做任何安装，供界面在弹"起个实例名"的框之前先告诉用户
    /// "这个包需要 Fabric 1.20.1"，让用户心里有数。
    /// </summary>
    public ModpackRequirements ReadRequirements(string modpackPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(modpackPath);

            // --- Modrinth .mrpack ---
            var mrIndex = archive.GetEntry("modrinth.index.json");
            if (mrIndex != null)
            {
                using var stream = mrIndex.Open();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? mc = null, loader = null, loaderVer = null;

                if (root.TryGetProperty("dependencies", out var deps))
                {
                    if (deps.TryGetProperty("minecraft", out var m)) mc = m.GetString();
                    if (deps.TryGetProperty("fabric-loader", out var f)) { loader = "Fabric"; loaderVer = f.GetString(); }
                    else if (deps.TryGetProperty("quilt-loader", out var q)) { loader = "Quilt"; loaderVer = q.GetString(); }
                    else if (deps.TryGetProperty("neoforge", out var nf)) { loader = "NeoForge"; loaderVer = nf.GetString(); }
                    else if (deps.TryGetProperty("forge", out var fg)) { loader = "Forge"; loaderVer = fg.GetString(); }
                }
                return new ModpackRequirements(name, mc, loader, loaderVer);
            }

            // --- XCL2 .xclpack ---
            var xclManifest = archive.GetEntry("xcl2-modpack.json");
            if (xclManifest != null)
            {
                using var stream = xclManifest.Open();
                var m = JsonSerializer.Deserialize<ModpackManifest>(stream);
                if (m != null)
                    return new ModpackRequirements(m.Name, m.McVersion, m.ModLoader, m.ModLoaderVersion);
            }

            // --- CurseForge manifest.json ---
            var cfManifest = archive.GetEntry("manifest.json");
            if (cfManifest != null)
            {
                using var stream = cfManifest.Open();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                string? name = root.TryGetProperty("name", out var n2) ? n2.GetString() : null;
                string? mc = null, loader = null, loaderVer = null;

                if (root.TryGetProperty("minecraft", out var mcNode))
                {
                    if (mcNode.TryGetProperty("version", out var v)) mc = v.GetString();
                    if (mcNode.TryGetProperty("modLoaders", out var loaders) &&
                        loaders.ValueKind == JsonValueKind.Array && loaders.GetArrayLength() > 0)
                    {
                        // CurseForge 的 id 形如 "forge-47.2.0" / "fabric-0.15.11" / "neoforge-21.1.66"
                        var id = loaders[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (!string.IsNullOrEmpty(id))
                        {
                            var dash = id.IndexOf('-');
                            var kind = dash > 0 ? id[..dash] : id;
                            loaderVer = dash > 0 ? id[(dash + 1)..] : null;
                            loader = kind.ToLowerInvariant() switch
                            {
                                "fabric" => "Fabric",
                                "quilt" => "Quilt",
                                "neoforge" => "NeoForge",
                                "forge" => "Forge",
                                _ => null,
                            };
                        }
                    }
                }
                return new ModpackRequirements(name, mc, loader, loaderVer);
            }
        }
        catch
        {
            // 读不出来不算错误——退回"什么都不知道"，让调用方走"请用户手选版本"的路径。
        }

        return new ModpackRequirements(Path.GetFileNameWithoutExtension(modpackPath), null, null, null);
    }

    /// <summary>
    /// 把版本目录名规整成合法的文件夹名。用户输入的实例名可能带 \ / : * ? " &lt; &gt; |
    /// 这些 Windows 不允许的字符，直接拿去建目录会抛异常。
    /// </summary>
    public static string SanitizeInstanceName(string raw)
    {
        var name = (raw ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim(' ', '.');   // Windows 目录名不能以空格或点结尾
        if (string.IsNullOrEmpty(name)) name = "modpack";
        return name;
    }

    /// <summary>给一个不跟现有目录冲突的实例名（重名时自动加 (2)(3)…）。</summary>
    public static string MakeUniqueInstanceName(string minecraftDir, string desired)
    {
        var versionsDir = Path.Combine(minecraftDir, "versions");
        var name = SanitizeInstanceName(desired);
        if (!Directory.Exists(Path.Combine(versionsDir, name))) return name;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{name} ({i})";
            if (!Directory.Exists(Path.Combine(versionsDir, candidate))) return candidate;
        }
        return $"{name}_{DateTime.Now:yyyyMMddHHmmss}";
    }

    /// <summary>安装结果。</summary>
    public sealed record InstallResult(
        string InstanceId,
        string InstanceDir,
        string? McVersion,
        string? Loader,
        List<string> FailedFiles);

    /// <summary>
    /// 从零安装：新建实例目录 → 装原版 → 装加载器 → 解整合包内容。
    /// </summary>
    /// <param name="modpackPath">.mrpack / .xclpack / CurseForge .zip</param>
    /// <param name="minecraftDir">目标 .minecraft 根目录</param>
    /// <param name="instanceName">用户自定义的实例名（会被 SanitizeInstanceName 规整并去重）</param>
    /// <param name="javaExeForForge">Forge/NeoForge 安装器需要本地 Java 来跑；
    /// Fabric/Quilt 不需要，传 null 即可。没有 Java 又遇到 Forge 包时会抛出带说明的异常。</param>
    public async Task<InstallResult> InstallToNewInstanceAsync(
        string modpackPath,
        string minecraftDir,
        string instanceName,
        string? javaExeForForge,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct = default)
    {
        var req = ReadRequirements(modpackPath);

        var instanceId = MakeUniqueInstanceName(minecraftDir, instanceName);
        var instanceDir = Path.Combine(minecraftDir, "versions", instanceId);

        var createdDir = false;
        try
        {
            Directory.CreateDirectory(instanceDir);
            createdDir = true;

            // ---------- 1. 原版本体 ----------
            // 整合包没写 MC 版本就没法往下走：装不了本体，装了加载器也没意义。
            // 这里明确报错让用户知道是包本身的问题，而不是默默装成一个跑不起来的空实例。
            if (string.IsNullOrWhiteSpace(req.McVersion))
                throw new InvalidOperationException(
                    "这个整合包的清单里没有写它需要的 Minecraft 版本，无法自动从零安装。\n" +
                    "可以改用「安装到已有实例」的方式，自己先建好对应版本再导入。");

            progress?.Report(new ProgressInfo("正在下载游戏本体", 0, 1, req.McVersion!));

            using var downloader = DownloadService.CreateFromConfig(_config);
            var manifest = await downloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == req.McVersion)
                ?? throw new InvalidOperationException(
                    $"在 Mojang 版本清单里找不到 Minecraft {req.McVersion}，无法安装这个整合包。\n" +
                    "可能是整合包针对的是快照/预览版，或者版本号写得不规范。");

            await downloader.InstallVersionAsync(minecraftDir, entry, progress, ct);

            // ---------- 2. 加载器 ----------
            // 注意：加载器安装出来的是它自己的版本目录（fabric-loader-x-y 之类），
            // 跟我们这个自定义名字的实例目录不是一个。装完之后要把加载器产出的
            // version json / jar 搬进我们的实例目录，并把 id 改成实例名，
            // 这样版本列表里显示的就是用户起的名字，而不是 fabric-loader-0.15.11-1.20.1。
            string? producedVersionId = null;
            if (!string.IsNullOrWhiteSpace(req.Loader))
            {
                progress?.Report(new ProgressInfo($"正在安装 {req.Loader}", 0, 1, req.LoaderVersion ?? ""));

                using var loaderService = new ClientLoaderInstallService(_config);

                switch (req.Loader)
                {
                    case "Fabric":
                    {
                        var lv = req.LoaderVersion;
                        if (string.IsNullOrWhiteSpace(lv))
                        {
                            var builds = await loaderService.GetFabricLoaderVersionsAsync(req.McVersion, ct);
                            lv = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion
                                 ?? builds.FirstOrDefault()?.DisplayVersion;
                        }
                        producedVersionId = await loaderService.InstallFabricClientAsync(
                            minecraftDir, req.McVersion!, lv!, progress, ct);
                        break;
                    }
                    case "Quilt":
                    {
                        var lv = req.LoaderVersion;
                        if (string.IsNullOrWhiteSpace(lv))
                        {
                            var builds = await loaderService.GetQuiltLoaderVersionsAsync(req.McVersion, ct);
                            lv = builds.FirstOrDefault(b => b.IsRecommended)?.DisplayVersion
                                 ?? builds.FirstOrDefault()?.DisplayVersion;
                        }
                        producedVersionId = await loaderService.InstallQuiltClientAsync(
                            minecraftDir, req.McVersion!, lv!, progress, ct);
                        break;
                    }
                    case "Forge":
                    case "NeoForge":
                    {
                        if (string.IsNullOrWhiteSpace(javaExeForForge) || !File.Exists(javaExeForForge))
                            throw new InvalidOperationException(
                                $"这个整合包需要 {req.Loader}，而 {req.Loader} 的安装器必须用本地 Java 运行。\n" +
                                "启动器没有找到可用的 Java，请先到「设置 - Java」里配置一个，或让启动器自动下载一份便携版 Java 后再试。");

                        var full = req.Loader == "Forge"
                            ? $"{req.McVersion}-{req.LoaderVersion}"
                            : req.LoaderVersion!;
                        var coreType = req.Loader == "Forge" ? ServerCoreType.Forge : ServerCoreType.NeoForge;

                        producedVersionId = await loaderService.InstallForgeOrNeoForgeClientAsync(
                            minecraftDir, coreType, full, javaExeForForge!, progress, ct);
                        break;
                    }
                }
            }

            // ---------- 3. 把加载器产出的实例改名/搬进我们这个自定义实例目录 ----------
            if (!string.IsNullOrEmpty(producedVersionId) &&
                !string.Equals(producedVersionId, instanceId, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new ProgressInfo("正在整理实例文件", 0, 1, instanceId));
                MergeProducedVersionInto(minecraftDir, producedVersionId!, instanceId, instanceDir);
            }
            else if (string.IsNullOrEmpty(producedVersionId))
            {
                // 纯原版整合包（没有加载器）：把原版目录的 json/jar 复制成实例自己的一份，
                // 让这个实例独立可启动，而不是空目录。
                MergeProducedVersionInto(minecraftDir, req.McVersion!, instanceId, instanceDir, copyOnly: true);
            }

            // ---------- 4. 解整合包内容 ----------
            progress?.Report(new ProgressInfo("正在导入整合包内容", 0, 1, Path.GetFileName(modpackPath)));

            var failed = new List<string>();
            if (ModpackService.IsMrpack(modpackPath))
            {
                var r = await _modpackService.ImportMrpackAsync(modpackPath, instanceDir,
                    new Progress<string>(msg => progress?.Report(new ProgressInfo(msg, 0, 1, ""))), ct);
                failed.AddRange(r.FailedFiles);
            }
            else
            {
                _modpackService.Import(modpackPath, instanceDir,
                    new Progress<string>(msg => progress?.Report(new ProgressInfo(msg, 0, 1, ""))));
            }

            return new InstallResult(instanceId, instanceDir, req.McVersion, req.Loader, failed);
        }
        catch
        {
            // 失败就把半成品目录清掉：留着只会让用户在版本列表里看到一个点了就崩的实例。
            if (createdDir)
            {
                try { if (Directory.Exists(instanceDir)) Directory.Delete(instanceDir, recursive: true); }
                catch { /* 清理失败不掩盖原始异常 */ }
            }
            throw;
        }
    }

    /// <summary>
    /// 把加载器/原版安装产出的版本目录，搬（或复制）成我们这个自定义名字的实例。
    ///
    /// 做法：把源目录里的 .json / .jar 拷进目标目录并改成目标名，同时改写 json 内部的 "id" 字段
    /// （version json 的 id 必须跟文件名/目录名一致，否则启动时找不到主类和库）。
    /// 其余文件（natives 等）原样复制。
    /// </summary>
    private static void MergeProducedVersionInto(string minecraftDir, string sourceVersionId,
        string targetVersionId, string targetDir, bool copyOnly = false)
    {
        var sourceDir = Path.Combine(minecraftDir, "versions", sourceVersionId);
        if (!Directory.Exists(sourceDir)) return;

        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var ext = Path.GetExtension(file);
            var isJson = ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
            var isJar = ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);

            var destName = (isJson || isJar) ? targetVersionId + ext : Path.GetFileName(file);
            var dest = Path.Combine(targetDir, destName);

            if (isJson)
            {
                // 改写 id 字段：WPF 侧启动逻辑按 id 找 jar，id 跟目录名对不上就启动不了。
                try
                {
                    var text = File.ReadAllText(file);
                    var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                    if (node != null)
                    {
                        var rebuilt = new Dictionary<string, object?>();
                        foreach (var kv in node)
                            rebuilt[kv.Key] = kv.Key == "id" ? targetVersionId : (object)kv.Value;
                        if (!rebuilt.ContainsKey("id")) rebuilt["id"] = targetVersionId;

                        File.WriteAllText(dest, JsonSerializer.Serialize(rebuilt,
                            new JsonSerializerOptions { WriteIndented = true }));
                        continue;
                    }
                }
                catch { /* 解析失败就退回原样复制 */ }
            }

            File.Copy(file, dest, overwrite: true);
        }

        // 子目录（natives 之类）原样复制
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirRecursive(dir, destSub);
        }

        // 搬完之后把加载器自己产出的那个目录删掉，避免版本列表里同时出现
        // "我的整合包" 和 "fabric-loader-0.15.11-1.20.1" 两个看起来一样的实例。
        if (!copyOnly)
        {
            try { Directory.Delete(sourceDir, recursive: true); }
            catch { /* 删不掉不影响使用，只是列表里多一项 */ }
        }
    }

    private static void CopyDirRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(source))
            CopyDirRecursive(d, Path.Combine(dest, Path.GetFileName(d)));
    }
}
