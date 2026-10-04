using System.IO;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal static class ModInstallConfirmation
{
    public static async Task<Dictionary<string, ModInstallPlan>?> PrepareAsync(IEnumerable<string> sources, string modsDir)
    {
        var files = sources.ToArray();
        var plans = await Task.Run(() => ModInstallService.PrepareBatch(files, modsDir));
        if (plans.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        var details = plans.Select(p => Path.GetFileName(p.Source) + (p.ReplacedFiles.Count == 0 ? "（新增）" :
            " → 替换 " + string.Join("、", p.ReplacedFiles.Select(Path.GetFileName))));
        var message = $"将安装 {plans.Count} 个模组到：\n{modsDir}\n\n" + string.Join("\n", details) +
            "\n\n同名或同模组 ID 的旧包将被替换，不创建编号副本；已禁用模组保持禁用。";
        if (plans.Count < files.Length) message += "\n本批同一模组的多个版本仅安装最后一个。";
        if (await MessageBoxDialog.ShowConfirmAsync(message, "确认安装模组", "安装并替换") != true)
            return null;
        return plans.ToDictionary(p => p.Source, StringComparer.OrdinalIgnoreCase);
    }
}
