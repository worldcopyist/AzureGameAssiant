using System.Net;
using System.Text;
using AzureGameAssistant.Ai;

namespace AzureGameAssistant.Tests;

public sealed class OpenAiCompatibleClientTests
{
    private static readonly Uri Endpoint = new("https://example.com/v1/chat/completions");
    private static readonly AiPrompt Prompt = new("system", "user");

    [Fact]
    public async Task ParsesJsonCompletionAndUsage()
    {
        using var handler = new StubHandler((_, _) => Json(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"summary\":\"ok\"}"}}],"usage":{"prompt_tokens":12,"completion_tokens":7}}"""));

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiCompletion completion = await client.CompleteAsync(Request(), Prompt, null, CancellationToken.None);

        Assert.True(completion.Success);
        Assert.Equal("{\"summary\":\"ok\"}", completion.Content);
        Assert.Equal(12, completion.PromptTokens);
        Assert.Equal(7, completion.CompletionTokens);
        Assert.Equal(1, completion.Attempts);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DoesNotRetryUnauthorized()
    {
        using var handler = new StubHandler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}"""));

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiCompletion completion = await client.CompleteAsync(Request(), Prompt, null, CancellationToken.None);

        Assert.False(completion.Success);
        Assert.Equal(AiFailureKind.Unauthorized, completion.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RetriesRateLimitOnce()
    {
        using var handler = new StubHandler((_, call) => call == 1
            ? Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""")
            : Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}"""));

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiCompletion completion = await client.CompleteAsync(Request(), Prompt, null, CancellationToken.None);

        Assert.True(completion.Success);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, completion.Attempts);
    }

    [Fact]
    public async Task ReportsInvalidJsonWithoutRetry()
    {
        using var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, "not-json"));

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiCompletion completion = await client.CompleteAsync(Request(), Prompt, null, CancellationToken.None);

        Assert.False(completion.Success);
        Assert.Equal(AiFailureKind.InvalidResponse, completion.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ReadsServerSentEvents()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"summary\\\":\"}}]}\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"\\\"ok\\\"}\"}}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":3}}\n" +
            "data: [DONE]\n";

        using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiCompletion completion = await client.CompleteAsync(Request(), Prompt, null, CancellationToken.None);

        Assert.True(completion.Success);
        Assert.True(completion.Streamed);
        Assert.Equal("{\"summary\":\"ok\"}", completion.Content);
        Assert.Equal(9, completion.PromptTokens);
        Assert.Equal(3, completion.CompletionTokens);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}"""));
        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteAsync(Request(), Prompt, null, cancellation.Token));
    }

    [Fact]
    public async Task ListModelsParsesAndFiltersNonChatIds()
    {
        using var handler = new StubHandler((_, _) => Json(
            HttpStatusCode.OK,
            """{"data":[{"id":"gpt-4o-mini"},{"id":"text-embedding-3-small"},{"id":"gpt-4o"},{"id":"tts-1"},{"id":"whisper-1"},{"id":"gpt-4o"}]}"""));

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiModelList result = await client.ListModelsAsync(Models(), "sk-test", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(AiFailureKind.None, result.Failure);
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], result.Models);
    }

    [Fact]
    public async Task ListModelsSendsGetWithBearerToken()
    {
        HttpRequestMessage? seen = null;
        using var handler = new StubHandler((request, _) =>
        {
            seen = request;
            return Json(HttpStatusCode.OK, """{"data":[{"id":"m1"}]}""");
        });

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiModelList result = await client.ListModelsAsync(Models(), " sk-test ", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal("https://example.com/v1/models", seen.RequestUri!.ToString());
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", seen.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ListModelsReportsUnauthorized()
    {
        using var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}"""));

        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);
        AiModelList result = await client.ListModelsAsync(Models(), "sk-test", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AiFailureKind.Unauthorized, result.Failure);
        Assert.Contains("API Key", result.Error);
    }

    [Fact]
    public async Task ListModelsRejectsMissingData()
    {
        using var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"models":[]}"""));
        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);

        AiModelList result = await client.ListModelsAsync(Models(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AiFailureKind.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task ListModelsKeepsNonChatIdsWhenNothingElseIsOffered()
    {
        using var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, """{"data":[{"id":"text-embedding-3-small"}]}"""));
        using var client = new OpenAiCompatibleClient(handler, TimeSpan.Zero);

        AiModelList result = await client.ListModelsAsync(Models(), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["text-embedding-3-small"], result.Models);
    }

    private static Uri Models() => new("https://example.com/v1/models");

    private static AiRequest Request() => new(Endpoint, "test-model", "sk-test", 512);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(responder(request, Calls));
        }
    }
}
