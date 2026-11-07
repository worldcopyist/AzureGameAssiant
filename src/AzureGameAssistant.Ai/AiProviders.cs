namespace AzureGameAssistant.Ai;

/// <summary>
/// Built-in OpenAI-compatible providers. Presets only supply defaults; model
/// names stay editable so the app never hardcodes a model that may be retired.
/// </summary>
public sealed record AiProviderPreset(
    string Id,
    string DisplayName,
    string BaseUrl,
    bool RequiresKey,
    string SuggestedModel);

public static class AiProviderPresets
{
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", true, "gpt-4o-mini"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", true, "deepseek-chat"),
        new("qwen", "通义千问 (兼容模式)", "https://dashscope.aliyuncs.com/compatible-mode/v1", true, "qwen-plus"),
        new("zhipu", "智谱", "https://open.bigmodel.cn/api/paas/v4", true, "glm-4-flash"),
        new("moonshot", "Moonshot", "https://api.moonshot.cn/v1", true, "moonshot-v1-8k"),
        new("siliconflow", "SiliconFlow", "https://api.siliconflow.cn/v1", true, "Qwen/Qwen2.5-7B-Instruct"),
        new("ollama", "Ollama (本地)", "http://127.0.0.1:11434/v1", false, "qwen2.5:7b"),
        new("lmstudio", "LM Studio (本地)", "http://127.0.0.1:1234/v1", false, "local-model"),
        new("custom", "自定义", "", true, ""),
    ];

    public static AiProviderPreset Resolve(string? id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    public static bool IsKnown(string? id) =>
        All.Any(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));
}

public static class AiEndpoint
{
    /// <summary>
    /// Normalizes a user-supplied base URL into a chat-completions endpoint.
    /// Plain HTTP is rejected unless the host is a loopback address, and
    /// credentials, queries and fragments are never accepted.
    /// </summary>
    public static bool TryNormalize(string? baseUrl, out Uri endpoint, out string? error)
    {
        endpoint = null!;
        error = null;

        string text = (baseUrl ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Base URL 不能为空";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            error = "Base URL 格式无效";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "仅支持 http/https";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            error = "远程地址必须使用 HTTPS";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Base URL 不能包含账号、查询参数或片段";
            return false;
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            path = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/v4", StringComparison.OrdinalIgnoreCase)
                ? path + "/chat/completions"
                : path + "/v1/chat/completions";
        }

        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        endpoint = builder.Uri;
        return true;
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback ||
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(uri.Host, "::1", StringComparison.Ordinal) ||
        string.Equals(uri.Host, "[::1]", StringComparison.Ordinal);
}
