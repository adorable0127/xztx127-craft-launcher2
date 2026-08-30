namespace XCL2.App.Services;

/// <summary>
/// 启动器内置的默认 AI 接口信息。BaseUrl/ApiKey 已经填好，用户不勾选"使用自定义 Key"时
/// 都会走这里，不需要用户自己填。
/// </summary>
public static class BuiltInAiDefaults
{
    public const string BaseUrl = "https://opencode.ai/zen/v1";
    public const string ApiKey = "sk-QkYNbOnuQtOG0U3Qmym7feYhbbXXlhwWQCGyThJM9Fw4hbB7Xvx6oJOxehdx2QS1"; // 在这里填你自己的 OpenCode Zen API Key
}

/// <summary>
/// AI 助手请求实际使用的接口信息：根据 UseCustomApiKey 在"用户自填"和"内置默认"之间二选一。
/// </summary>
public static class AiCredentialResolver
{
    public static (string baseUrl, string apiKey) Resolve(Models.AiAssistantConfig config)
    {
        if (config.UseCustomApiKey)
            return (config.BaseUrl, config.ApiKey);
        return (BuiltInAiDefaults.BaseUrl, BuiltInAiDefaults.ApiKey);
    }
}

/// <summary>
/// 统一的功能可用性判断：基本模式（RestrictedMode）下不允许使用 AI 助手。
/// 调用方式：AiFeatureGate.IsAvailable(config.Ai.Enabled, config.RestrictedMode)
/// </summary>
public static class AiFeatureGate
{
    public static bool IsAvailable(bool aiEnabled, bool restrictedMode) => aiEnabled && !restrictedMode;
}