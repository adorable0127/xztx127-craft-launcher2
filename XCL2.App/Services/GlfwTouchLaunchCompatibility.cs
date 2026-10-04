using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>仅撤销旧包留下的配置修改；不再为触屏关闭 Raw Input，不修改鼠标速度或加速度。</summary>
internal static class GlfwTouchLaunchCompatibility
{
    public static void RestorePreviousOverride(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, "options.txt");
            var backup = Path.Combine(gameDir, "xcl", "touch-backup", "options.before-glfw-touch.txt");
            if (!File.Exists(backup) || !File.Exists(path)) return;
            var current = File.ReadAllText(path);
            var original = File.ReadAllText(backup);
            var key = new Regex(@"(?m)^rawMouseInput:[^\r\n]*");
            var old = key.Match(original);
            var entries = key.Matches(current).Cast<Match>().ToArray();
            // 只处理旧包强制写入的 false；不覆盖玩家后来改回的值或其他配置。
            if (entries.Length > 0 && entries.All(m => m.Value == "rawMouseInput:false"))
            {
                var updated = old.Success ? key.Replace(current, old.Value)
                    : Regex.Replace(current, @"(?m)^rawMouseInput:[^\r\n]*(?:\r?\n|$)", "");
                if (updated != current)
                {
                    var temp = path + ".restore-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllText(temp, updated, new UTF8Encoding(false));
                        File.Move(temp, path, overwrite: true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    LauncherLogService.AppendLine("[触屏模式] 已依据备份撤销旧版 rawMouseInput 覆盖，其余鼠标设置未改动。");
                }
            }
            // 保留备份，但消费一次恢复记录，避免以后启动再次覆盖玩家自己的选择。
            File.Move(backup, backup + ".restored-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("恢复旧版触屏鼠标设置失败，请在游戏鼠标设置中恢复原始输入选项。", ex);
        }
    }
}
