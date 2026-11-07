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
