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
/// 2.3.0 起加了一层本地缓存：密钥原本每次都要向 <see cref="ApiKeyUrl"/> 发一次请求现拉，
/// 这个密钥是作者自己的、免费提供给所有用户共用的 OpenRouter 密钥，量一大对作者自己那台
/// 转发服务器（123-393.pages.dev）压力就很大。现在改成“先看本地有没有缓存，没有才去爬”：
/// 第一次成功拉到密钥后原样存一份到本地缓存文件，之后的请求直接读本地文件，不再打网站；
/// 只有本地缓存文件不存在/读取失败/内容为空时，才会退回去访问网站现拉一份并重新写入缓存。
/// 这样能把大部分重复请求挡在本地，明显降低服务器访问压力。
/// </summary>
public static class BuiltInAiDefaults
{
    public const string BaseUrl = "https://openrouter.ai/api/v1";
    public const string ApiKeyUrl = "https://123-393.pages.dev/1.txt";

    private static readonly HttpClient HttpClient = new();
    private static readonly object CacheLock = new();
    private static string? _memoryCache;

    /// <summary>本地缓存文件路径：%APPDATA%\XCL2\ai_key_cache.txt。跟随全局配置目录，
    /// 不需要额外单独的目录规则。</summary>
    private static string CacheFilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCL2");
            return Path.Combine(dir, "ai_key_cache.txt");
        }
    }

    /// <summary>取内置公共密钥：优先内存缓存 → 本地缓存文件 → 都没有才访问网站现拉一份，
    /// 拉到之后立即落盘缓存，供本进程后续调用和下次启动直接复用。</summary>
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
