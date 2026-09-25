using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>定位启动前期的 GL 上下文创建失败；日志只是证据，不能仅凭退出码推断驱动过旧。</summary>
public static class OpenGlTroubleshooter
{
    private static readonly Regex ErrorPattern = new(
        @"GLFW error 6554[23]|WGL:\s*The driver does not appear to support OpenGL|" +
        @"OpenGL(?:\s+version)?\s+[\d.]+\s+(?:or\s+higher\s+)?(?:is\s+)?(?:required|not supported)|" +
        @"OpenGL\s+\d+(?:\.\d+)?\s+is\s+required|" +
        @"Failed to create (?:OpenGL|GL|GLFW) context|" +
        @"Unable to create (?:OpenGL|GL) context|" +
        @"The requested OpenGL version is not available|" +
        @"Pixel format not accelerated|" +
        @"Bad video card drivers|" +
        @"does not support OpenGL|OpenGL.*(?:minimum|required).*version|" +
        @"OpenGL.*(?:version|版本).*?(?:too low|过低|不支持)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string RepairSteps =
        "检测到游戏在创建 OpenGL 图形环境时失败，可能是显卡驱动缺失/过旧、游戏需求超出显卡能力，" +
        "也可能是远程桌面或虚拟机未提供硬件加速。\n\n" +
        "1. 关闭游戏并重新连接本机桌面（远程桌面/虚拟机请检查 3D 加速）；台式机确认显示器连接在独立显卡上。\n" +
        "2. 使用下方‘一键尝试安装显卡驱动’从 Windows Update 安装适配本机硬件的显示驱动；" +
        "也可以通过 NVIDIA / AMD / Intel 官方工具安装后重启 Windows。\n" +
        "3. 若安装驱动后依旧报错，查看本机显卡支持的 OpenGL 版本与当前 Minecraft / 光影 / Mod 的最低要求，" +
        "旧显卡的硬件上限无法通过复制 opengl32.dll 来提升。请勿从不明站点下载驱动 DLL 覆盖系统文件。\n" +
        "4. 没有可用的官方硬件驱动时，可考虑升级显卡或选择支持当前硬件的游戏版本。";

    public static bool IsOpenGlFailure(string? text) => !string.IsNullOrWhiteSpace(text) && ErrorPattern.IsMatch(text);

    public static string ReadCurrentIncidentEvidence(GameProcessInfo info)
    {
        var sb = new StringBuilder();
        lock (info.OutputBuffer) sb.AppendLine(info.OutputBuffer.ToString());
        foreach (var path in new[] { Path.Combine(info.GameDir, "logs", "latest.log"),
                     Path.Combine(info.GameDir, "logs", "debug.log") })
        {
            try
            {
                // 避免上次崩溃的 latest.log 让本次无关的启动失败也被判为 GL 错误。
                if (File.Exists(path) && File.GetLastWriteTime(path) >= info.StartedAt.AddSeconds(-10))
                    sb.AppendLine(File.ReadAllText(path));
            }
            catch (Exception ex) { LauncherLogService.AppendLine("[OpenGL诊断] 读取游戏日志失败：" + ex.Message); }
        }
        return sb.ToString();
    }

    /// <summary>按时间戳单独保存命中 OpenGL 报错的原始日志片段，避免只剩一句退出码。</summary>
    public static string? CaptureIncident(GameProcessInfo info, string text)
    {
        if (!IsOpenGlFailure(text)) return null;
        try
        {
            string[] lines = text.Split('\n');
            var selected = new SortedSet<int>();
            for (int i = 0; i < lines.Length; i++)
                if (ErrorPattern.IsMatch(lines[i]))
                    for (int j = Math.Max(0, i - 4); j <= Math.Min(lines.Length - 1, i + 8); j++) selected.Add(j);
            string dir = Path.Combine(App.DataDir, "logs", "opengl");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"opengl-{DateTime.Now:yyyyMMdd-HHmmss}-{info.Pid}.log");
            File.WriteAllText(path, $"Minecraft：{info.VersionId}；启动时间：{info.StartedAt:O}；进程：{info.Pid}\n" +
                                   "以下为本次进程命中 OpenGL 错误附近的原始日志：\n" +
                                   string.Join("\n", selected.Select(i => lines[i])) + "\n\n" + RepairSteps);
            LauncherLogService.AppendLine($"[OpenGL诊断] 本次启动的图形上下文错误，证据已保存在：{path}");
            return path;
        }
        catch (Exception ex) { LauncherLogService.AppendLine("[OpenGL诊断] 保存证据失败：" + ex.Message); return null; }
    }
}
