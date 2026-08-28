using System.IO;
using System.Threading;
using XCL2.App.Services;

namespace XCL2.App.BedrockDecode;

/// <summary>
/// 基岩版 MSIXVC/XVD（GDK 通道安装包）检测与解压入口。
///
/// 1.26.x 起微软官方把基岩版 Windows 客户端切到 GDK 通道，微软 CDN 给的全是
/// .msixvc（XVD 容器，Xbox/Game Pass 云安装格式，不是 zip）。本类用移植自
/// BedrockLauncher.Core（MIT License，BedrockBoot 同款，见本目录各文件头注释）
/// 的解码器把这种格式解开：
///   1. 魔数识别：XVD 头固定偏移 0x200 处是 8 字节 ASCII "msft-xvd"；
///   2. 完整解析校验（Parse）确认容器结构合法，用于下载完成后的完整性验证；
///   3. 用内置 CIK 密钥（正式版 rel / 预览版 pre）做 XTS-AES 解密并逐段落盘。
///
/// 保留 BedrockBoot 同款能力的同时不引入任何外部依赖：解码器源码直接内置于
/// Services/BedrockDecode/，目标框架仍是 net8.0-windows，用户运行环境要求不变。
/// </summary>
public static class GdkPackageExtractor
{
    /// <summary>XVD 头魔数：8 字节 ASCII "msft-xvd"，位于文件偏移 0x200（签名区之后）。</summary>
    private const string XvdMagic = "msft-xvd";

    /// <summary>只做便宜的魔数检查，用于下载完成后的格式分派（zip / msixvc）。</summary>
    public static bool IsMsixvcPackage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 0x208) return false;
            fs.Position = 0x200;
            Span<byte> buf = stackalloc byte[8];
            return fs.Read(buf) == 8 && System.Text.Encoding.ASCII.GetString(buf) == XvdMagic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 完整解析校验 MSIXVC 包。刚下载完的大文件可能被杀软短暂占用读句柄，
    /// 对瞬时 IO 异常做几次重试，跟 <see cref="BedrockClientDownloadService"/> 里
    /// 的 zip 校验同一套思路。
    /// </summary>
    public static bool ValidateMsixvc(string path, out string reason)
    {
        if (!File.Exists(path)) { reason = "文件不存在"; return false; }

        var fileLen = new FileInfo(path).Length;
        if (fileLen == 0) { reason = "文件大小为 0"; return false; }

        const int maxAttempts = 5;
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = new MsiXVDStream(path);
                stream.Parse();
                reason = $"有效 MSIXVC/XVD 包（驱动大小 {stream.Header.DriveSize / 1048576} MB，" +
                         (stream.IsEncrypted ? "加密" : "未加密") + "）";
                return true;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                Thread.Sleep(400);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                Thread.Sleep(400);
            }
            catch (Exception ex)
            {
                reason = $"MSIXVC/XVD 头解析失败：{ex.GetType().Name}: {ex.Message}（文件大小 {fileLen} 字节）";
                return false;
            }
        }
        reason = $"多次重试后仍无法打开（可能一直被占用）：{lastEx?.GetType().Name}: {lastEx?.Message}（文件大小 {fileLen} 字节）";
        return false;
    }

    /// <summary>按渠道选解密密钥：正式版 rel，预览版 pre（与 BedrockLauncher.Core 一致）。</summary>
    public static byte[] GetCikKeyBytes(BedrockClientDownloadService.BedrockClientChannel channel)
        => channel == BedrockClientDownloadService.BedrockClientChannel.Preview
            ? _DEFINE_REF2.pre
            : _DEFINE_REF2.rel;

    /// <summary>
    /// 解压 MSIXVC 包到目标目录。用软件 AES（useHardware: false）——
    /// 对 x86/x64 全兼容（硬件 AES-NI 路径在无 AES-NI 的 CPU 上会直接抛
    /// PlatformNotSupportedException），而 .NET 的 AES 实现本身在有 AES-NI 的
    /// 机器上会自动走硬件加速，速度不差。
    /// </summary>
    public static async Task ExtractAsync(string packagePath, string extractDir,
        BedrockClientDownloadService.BedrockClientChannel channel,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(extractDir);

        var cik = new CikKey(GetCikKeyBytes(channel));
        using var decoder = new MsiXVDDecoder(cik, useHardware: false);
        using var stream = new MsiXVDStream(packagePath);
        stream.Parse();

        LauncherLogService.AppendLine(
            $"[Bedrock下载] 开始解压 MSIXVC 包：{packagePath} -> {extractDir}" +
            $"（{(stream.IsEncrypted ? "加密" : "未加密")}，{stream.Header.DriveSize / 1048576} MB）");

        var decodeProgress = new Progress<DecompressProgress>(dp =>
            progress?.Report(new ProgressInfo("正在解压 MSIXVC 安装包",
                (int)dp.CurrentCount, (int)dp.TotalCount, dp.FileName)));

        await stream.ExtractTaskAsync(Path.GetFullPath(extractDir), decoder, decodeProgress, ct);

        LauncherLogService.AppendLine($"[Bedrock下载] MSIXVC 解压完成：{extractDir}");
    }
}