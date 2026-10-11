using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    internal Task<string> PrepareCrashAiPromptAsync(GameProcessInfo? process, DateTime incidentObservedAt,
        string summary, CancellationToken ct)
    {
        // 在 UI 线程截取配置密钥，日志文件读取与脱敏在后台进行，不阻塞崩溃弹窗。
        var config = AiAssistantConfig;
        var secrets = new[] { config.ApiKey }
            .Concat(config.ApiKeys ?? new())
            .Concat((config.ApiEndpoints ?? new()).Where(endpoint => endpoint != null).SelectMany(endpoint => endpoint.ApiKeys ?? new()))
            .Concat((config.CustomModels ?? new()).Where(model => model != null).Select(model => model.ProviderApiKey))
            .Concat(ConfigService.Accounts.SelectMany(account => new[]
                { account.MsRefreshToken, account.MinecraftAccessToken, account.AuthServerClientToken }))
            .Where(secret => !string.IsNullOrWhiteSpace(secret)).Select(secret => secret!).ToArray();
        return Task.Run(() => CrashAiLogService.BuildPrompt(process, incidentObservedAt, summary, secrets, ct), ct);
    }

    /// <summary>崩溃快捷入口专用。MainContent 与当前导航完全不变，关闭浮层即可继续原页面。</summary>
    internal async Task ShowCrashAnalysisOverlayAsync(string prompt, string sessionTitle)
    {
        if (!EnsureNotRestrictedModeForAi() || !EnsureAiTermsAccepted()) return;
        var dialog = new AiCrashAnalysisDialog(this, prompt, sessionTitle);
        PreviewFluentTheme.ApplyTo(dialog);
        try { await OverlayDialogService.ShowModalAsync(dialog); }
        finally
        {
            await dialog.DisposeAsync();
            _aiAssistantPanel?.ApplyConfig(AiAssistantConfig);
            UpdateAiFloatingButtonVisibility(MainContent.Content is not AiAssistantPanel);
        }
    }
}
