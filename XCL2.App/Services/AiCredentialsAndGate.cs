using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace XCL2.App.Services;

/// <summary>
/// 内置公共 AI 接口（OpenRouter）默认凭据。
///
/// 缓存策略：不再是"用到时才拉、拉一次存一次"，而是在用户**打开 AI 助手页面**时由
/// 调用方主动调一次 <see cref="PrefetchAsync"/>，现拉一份密钥落盘到
/// %APPDATA%\XCL2\ai\1.txt；此后同一进程内 <see cref="GetApiKey"/> 一律直接读这个文件，
/// 不再重新发请求。这样一次会话里无论发多少条消息，最多只在打开页面那一刻打一次
/// 转发服务器（123-393.pages.dev），大幅降低服务器压力。
/// 如果密钥中途失效（收到 401），调用方会调 <see cref="InvalidateCache"/> 清空缓存，
/// 下次 GetApiKey 找不到缓存时会退回去现拉一份，避免卡死在坏密钥上。
/// </summary>
public static class BuiltInAiDefaults
{
    public const string BaseUrl = "https://openrouter.ai/api/v1";
    public const string ApiKeyUrl = "https://123-393.pages.dev/1.txt";

    private static readonly HttpClient HttpClient = new();
    private static readonly object CacheLock = new();
    private static string? _memoryCache;

    /// <summary>本地缓存文件路径：%APPDATA%\XCL2\ai\1.txt。</summary>
    private static string CacheFilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2", "ai");
            return Path.Combine(dir, "1.txt");
        }
    }

    /// <summary>在打开 AI 助手页面时调用：主动现拉一份密钥并落盘缓存，供本次会话期间
    /// 后续所有 GetApiKey 调用直接读文件复用。已经有有效缓存（内存或磁盘）时直接跳过，
    /// 不重复请求。拉取失败时静默忽略——GetApiKey 到时候找不到缓存自然会再现拉一次。</summary>
    public static async Task PrefetchAsync()
    {
        lock (CacheLock)
        {
            if (!string.IsNullOrWhiteSpace(_memoryCache)) return;
        }
        if (!string.IsNullOrWhiteSpace(TryReadCacheFile())) return;

        try
        {
            var fetched = (await HttpClient.GetStringAsync(ApiKeyUrl).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(fetched))
            {
                lock (CacheLock) { _memoryCache = fetched; }
                TryWriteCacheFile(fetched);
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("预拉取内置 AI 密钥失败", ex);
        }
    }

    /// <summary>取内置公共密钥：优先内存缓存 → 本地缓存文件（即打开页面时 PrefetchAsync
    /// 写入的那份）→ 都没有（比如 Prefetch 还没跑完/失败了）才现拉一份兜底，避免直接报错。</summary>
    public static string GetApiKey()
    {
        lock (CacheLock)
        {
            if (!string.IsNullOrWhiteSpace(_memoryCache))
                return _memoryCache!;
        }

        var fromDisk = TryReadCacheFile();
        if (!string.IsNullOrWhiteSpace(fromDisk))
        {
            lock (CacheLock) { _memoryCache = fromDisk; }
            return fromDisk!;
        }

        var fetched = HttpClient
            .GetStringAsync(ApiKeyUrl)
            .GetAwaiter()
            .GetResult()
            .Trim();

        if (!string.IsNullOrWhiteSpace(fetched))
        {
            lock (CacheLock) { _memoryCache = fetched; }
            TryWriteCacheFile(fetched);
        }

        return fetched;
    }

    /// <summary>本地缓存看起来坏掉/密钥失效时，调用方（比如收到 401/403）可以调这个强制清空
    /// 缓存，下一次 GetApiKey 就会重新访问网站现拉一份，而不是一直卡在一个失效的本地缓存上。</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock) { _memoryCache = null; }
        try
        {
            var path = CacheFilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("清理本地 AI 密钥缓存失败", ex);
        }
    }

    private static string? TryReadCacheFile()
    {
        try
        {
            var path = CacheFilePath;
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("读取本地 AI 密钥缓存失败，将回退到在线获取", ex);
            return null;
        }
    }

    private static void TryWriteCacheFile(string key)
    {
        try
        {
            var path = CacheFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, key);
        }
        catch (Exception ex)
        {
            // 缓存写入失败不影响本次正常使用（内存里已经有了），只是下次启动又要重新爬一次。
            ErrorPresenter.LogFallback("写入本地 AI 密钥缓存失败", ex);
        }
    }
}

public static class AiCredentialResolver
{
    public static (string baseUrl, string apiKey) Resolve(
        Models.AiAssistantConfig config,
        string? modelId = null)
    {
        var modelDef = Models.AiModelIds.FindInCatalog(config, modelId);

        if (modelDef != null && modelDef.HasOwnProvider)
        {
            return (
                modelDef.ProviderBaseUrl!.Trim(),
                modelDef.ProviderApiKey!
            );
        }

        if (config.UseCustomApiKey)
        {
            return (
                config.BaseUrl,
                config.ApiKey
            );
        }

        var apiKey = BuiltInAiDefaults.GetApiKey();

        return (
            BuiltInAiDefaults.BaseUrl,
            apiKey
        );
    }
}

public static class AiFeatureGate
{
    public static bool IsAvailable(bool aiEnabled, bool restrictedMode)
        => aiEnabled && !restrictedMode;
}

/// <summary>
/// 用户在设置里填了自己的 OpenRouter API Key（自定义 API，且 Base URL 指向 openrouter.ai）时，
/// 自动去 OpenRouter 的 /models 接口拉一份当前可用模型列表，同步进自定义模型表，
/// 免得用户还要自己一个个去网站抄模型 ID。只在“Base URL 明确是 openrouter.ai”时触发，
/// 换了别的供应商不会误触发；失败（网络问题/密钥无效）只是静默跳过，不阻塞正常保存设置。
/// </summary>
public static class OpenRouterModelSyncService
{
    private static readonly HttpClient HttpClient = new();

    /// <summary>粗略判断某个 Base URL 是不是 OpenRouter 自己的接口地址。</summary>
    public static bool IsOpenRouterBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        return baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>拉取并返回 OpenRouter 当前可用模型（id + 展示名）。失败返回空列表，
    /// 调用方据此判断是否要提示用户“同步失败，请检查密钥/网络”。</summary>
    public static async Task<List<Models.AiModelDefinition>> FetchModelsAsync(string apiKey)
    {
        var result = new List<Models.AiModelDefinition>();
        if (string.IsNullOrWhiteSpace(apiKey)) return result;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return result;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

                result.Add(new Models.AiModelDefinition
                {
                    Id = id.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim(),
                    Tier = Models.AiModelTier.Normal
                });
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("同步 OpenRouter 模型列表失败", ex);
        }

        return result;
    }

    /// <summary>如果 baseUrl 是 OpenRouter，就用 apiKey 拉取模型表并合并进 existingModels
    /// （按 Id 去重，新拉到的覆盖同 Id 旧条目、保留用户手填的其它条目），返回是否发生了同步。</summary>
    public static async Task<bool> TrySyncIntoAsync(
        string baseUrl, string apiKey, ICollection<Models.AiModelDefinition> existingModels)
    {
        if (!IsOpenRouterBaseUrl(baseUrl)) return false;

        var fetched = await FetchModelsAsync(apiKey).ConfigureAwait(false);
        if (fetched.Count == 0) return false;

        var byId = existingModels
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
            .ToDictionary(m => m.Id.Trim(), m => m, StringComparer.OrdinalIgnoreCase);

        foreach (var model in fetched)
            byId[model.Id] = model;

        existingModels.Clear();
        foreach (var model in byId.Values)
            existingModels.Add(model);
        return true;
    }
}
