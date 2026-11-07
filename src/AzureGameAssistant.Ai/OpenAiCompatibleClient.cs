using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AzureGameAssistant.Ai;

public enum AiFailureKind
{
    None,
    Unauthorized,
    RateLimited,
    ServerError,
    Timeout,
    Offline,
    InvalidResponse,
    Cancelled,
    Budget,
}

public sealed record AiCompletion(
    bool Success,
    string Content,
    int? PromptTokens,
    int? CompletionTokens,
    AiFailureKind Failure,
    string? Error,
    int Attempts,
    bool Streamed);

public sealed record AiRequest(
    Uri Endpoint,
    string Model,
    string? ApiKey,
    int MaxOutputTokens,
    int TimeoutSeconds = 60,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null);

/// <summary>
/// Minimal OpenAI-compatible client: chat/completions only, SSE streaming with
/// cancellation, one retry for 429/5xx/timeout, no retry for 401/403 or a
/// broken network. The base URL is validated before this class is reached.
/// </summary>
public sealed class OpenAiCompatibleClient : IDisposable
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxStreamChars = 1_000_000;

    private readonly HttpClient _http;
    private readonly TimeSpan _retryDelay;

    public OpenAiCompatibleClient(HttpMessageHandler? handler = null, TimeSpan? retryDelay = null)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
        };

        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    public async Task<AiCompletion> CompleteAsync(
        AiRequest request,
        AiPrompt prompt,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        int attempts = 0;
        AiFailureKind lastFailure = AiFailureKind.InvalidResponse;
        string lastError = string.Empty;

        while (attempts < 2)
        {
            attempts++;
            cancellationToken.ThrowIfCancellationRequested();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 5, 300)));

            try
            {
                using HttpRequestMessage message = BuildMessage(request, prompt);
                using HttpResponseMessage response = await _http
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string body = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                    (lastFailure, lastError) = Classify(response.StatusCode, body);

                    if (ShouldRetry(lastFailure) && attempts < 2)
                    {
                        await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return new AiCompletion(false, string.Empty, null, null, lastFailure, lastError, attempts, false);
                }

                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null &&
                    mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    return await ReadStreamAsync(response, attempts, progress, timeout.Token)
                        .ConfigureAwait(false);
                }

                string payload = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                AiCompletion parsed = ParseEnvelope(payload, attempts);
                if (!parsed.Success && ShouldRetry(parsed.Failure) && attempts < 2)
                {
                    lastFailure = parsed.Failure;
                    lastError = parsed.Error ?? string.Empty;
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return parsed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new AiCompletion(false, string.Empty, null, null, AiFailureKind.Cancelled, "已取消", attempts, false);
            }
            catch (OperationCanceledException)
            {
                lastFailure = AiFailureKind.Timeout;
                lastError = "请求超时";
                if (attempts < 2)
                {
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (HttpRequestException ex)
            {
                return new AiCompletion(
                    false,
                    string.Empty,
                    null,
                    null,
                    AiFailureKind.Offline,
                    "无法连接模型服务：" + Redactor.SanitizeError(ex.Message),
                    attempts,
                    false);
            }
            catch (IOException ex)
            {
                lastFailure = AiFailureKind.InvalidResponse;
                lastError = "读取响应失败：" + Redactor.SanitizeError(ex.Message);
            }
        }

        return new AiCompletion(false, string.Empty, null, null, lastFailure, lastError, attempts, false);
    }

    private static HttpRequestMessage BuildMessage(AiRequest request, AiPrompt prompt)
    {
        var buffer = new ArrayBufferWriter<byte>(prompt.System.Length + prompt.User.Length + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", prompt.System);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", prompt.User);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteNumber("temperature", 0.2);
            writer.WriteNumber("max_tokens", Math.Clamp(request.MaxOutputTokens, 128, 32_000));
            writer.WriteBoolean("stream", true);
            writer.WriteEndObject();
        }

        var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
        {
            Content = new ByteArrayContent(buffer.WrittenSpan.ToArray()),
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
        }

        if (request.ExtraHeaders is not null)
        {
            foreach ((string name, string value) in request.ExtraHeaders)
            {
                if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return message;
    }

    private static async Task<AiCompletion> ReadStreamAsync(
        HttpResponseMessage response,
        int attempts,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        int? promptTokens = null;
        int? completionTokens = null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(data);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("error", out JsonElement error) &&
                    error.ValueKind == JsonValueKind.Object)
                {
                    string message = error.TryGetProperty("message", out JsonElement text)
                        ? text.GetString() ?? "模型返回错误"
                        : "模型返回错误";
                    return new AiCompletion(false, string.Empty, null, null, AiFailureKind.InvalidResponse,
                        Redactor.SanitizeError(message), attempts, true);
                }

                ReadUsage(root, ref promptTokens, ref completionTokens);

                if (root.TryGetProperty("choices", out JsonElement choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    JsonElement choice = choices[0];
                    if (choice.TryGetProperty("delta", out JsonElement delta) &&
                        delta.ValueKind == JsonValueKind.Object &&
                        delta.TryGetProperty("content", out JsonElement deltaContent) &&
                        deltaContent.ValueKind == JsonValueKind.String)
                    {
                        string chunk = deltaContent.GetString() ?? string.Empty;
                        if (content.Length + chunk.Length > MaxStreamChars)
                        {
                            return new AiCompletion(false, string.Empty, promptTokens, completionTokens,
                                AiFailureKind.InvalidResponse, "模型响应超过大小上限", attempts, true);
                        }

                        content.Append(chunk);
                        progress?.Report(content.Length);
                    }
                    else if (choice.TryGetProperty("message", out JsonElement finalMessage) &&
                             finalMessage.ValueKind == JsonValueKind.Object &&
                             finalMessage.TryGetProperty("content", out JsonElement finalContent) &&
                             finalContent.ValueKind == JsonValueKind.String)
                    {
                        string text = finalContent.GetString() ?? string.Empty;
                        if (text.Length > MaxStreamChars)
                        {
                            return new AiCompletion(false, string.Empty, promptTokens, completionTokens,
                                AiFailureKind.InvalidResponse, "模型响应超过大小上限", attempts, true);
                        }

                        content.Clear();
                        content.Append(text);
                        progress?.Report(content.Length);
                    }
                }
            }
            catch (JsonException)
            {
                // A malformed keep-alive chunk must not abort the whole stream.
            }
        }

        if (content.Length == 0)
        {
            return new AiCompletion(false, string.Empty, promptTokens, completionTokens,
                AiFailureKind.InvalidResponse, "模型返回了空响应", attempts, true);
        }

        return new AiCompletion(true, content.ToString(), promptTokens, completionTokens,
            AiFailureKind.None, null, attempts, true);
    }

    private static AiCompletion ParseEnvelope(string payload, int attempts)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                string message = error.TryGetProperty("message", out JsonElement text)
                    ? text.GetString() ?? "模型返回错误"
                    : "模型返回错误";
                return new AiCompletion(false, string.Empty, null, null, AiFailureKind.InvalidResponse,
                    Redactor.SanitizeError(message), attempts, false);
            }

            int? promptTokens = null;
            int? completionTokens = null;
            ReadUsage(root, ref promptTokens, ref completionTokens);

            if (root.TryGetProperty("choices", out JsonElement choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                JsonElement choice = choices[0];
                if (choice.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out JsonElement content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    string text = content.GetString() ?? string.Empty;
                    return text.Length > 0
                        ? new AiCompletion(true, text, promptTokens, completionTokens, AiFailureKind.None, null, attempts, false)
                        : new AiCompletion(false, string.Empty, promptTokens, completionTokens,
                            AiFailureKind.InvalidResponse, "模型返回了空响应", attempts, false);
                }

                if (choice.TryGetProperty("text", out JsonElement legacy) &&
                    legacy.ValueKind == JsonValueKind.String)
                {
                    string text = legacy.GetString() ?? string.Empty;
                    return text.Length > 0
                        ? new AiCompletion(true, text, promptTokens, completionTokens, AiFailureKind.None, null, attempts, false)
                        : new AiCompletion(false, string.Empty, promptTokens, completionTokens,
                            AiFailureKind.InvalidResponse, "模型返回了空响应", attempts, false);
                }
            }

            return new AiCompletion(false, string.Empty, promptTokens, completionTokens,
                AiFailureKind.InvalidResponse, "响应缺少 choices[0].message.content", attempts, false);
        }
        catch (JsonException)
        {
            return new AiCompletion(false, string.Empty, null, null,
                AiFailureKind.InvalidResponse, "响应不是有效 JSON", attempts, false);
        }
    }

    private static void ReadUsage(JsonElement root, ref int? promptTokens, ref int? completionTokens)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (usage.TryGetProperty("prompt_tokens", out JsonElement prompt) && prompt.TryGetInt32(out int input))
        {
            promptTokens = input;
        }

        if (usage.TryGetProperty("completion_tokens", out JsonElement completion) && completion.TryGetInt32(out int output))
        {
            completionTokens = output;
        }
    }

    private static (AiFailureKind Kind, string Error) Classify(HttpStatusCode status, string body)
    {
        int code = (int)status;
        string sanitized = Redactor.SanitizeError(body);
        if (sanitized.Length > 300)
        {
            sanitized = sanitized[..300];
        }

        return code switch
        {
            401 or 403 => (AiFailureKind.Unauthorized, "认证失败，请检查 API Key 与权限"),
            429 => (AiFailureKind.RateLimited, "请求过于频繁，请稍后重试"),
            408 => (AiFailureKind.Timeout, "模型服务超时"),
            >= 500 => (AiFailureKind.ServerError, $"模型服务不可用 (HTTP {code})" + Detail(sanitized)),
            300 or 301 or 302 or 303 or 307 or 308 => (AiFailureKind.InvalidResponse, "端点返回重定向，请填写最终 Base URL"),
            _ => (AiFailureKind.InvalidResponse, $"请求失败 (HTTP {code})" + Detail(sanitized)),
        };
    }

    private static string Detail(string body) => string.IsNullOrWhiteSpace(body) ? string.Empty : "：" + body;

    private static bool ShouldRetry(AiFailureKind kind) =>
        kind is AiFailureKind.RateLimited or AiFailureKind.ServerError or AiFailureKind.Timeout;

    private async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        if (_retryDelay > TimeSpan.Zero)
        {
            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[16 * 1024];
        using var memory = new MemoryStream();
        int total = 0;
        int read;
        while (total < MaxResponseBytes &&
               (read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            int allowed = Math.Min(read, MaxResponseBytes - total);
            memory.Write(buffer, 0, allowed);
            total += allowed;
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
