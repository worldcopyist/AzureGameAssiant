using AzureGameAssistant.Ai;

namespace AzureGameAssistant.Tests;

public sealed class AiEndpointTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("https://example.com", "https://example.com/v1/chat/completions")]
    [InlineData("https://example.com/gateway/v1/chat/completions", "https://example.com/gateway/v1/chat/completions")]
    public void NormalizesChatCompletionsEndpoint(string input, string expected)
    {
        Assert.True(AiEndpoint.TryNormalize(input, out Uri endpoint, out string? error), error);
        Assert.Equal(expected, endpoint.ToString());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/models")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/models")]
    [InlineData("https://example.com", "https://example.com/v1/models")]
    [InlineData("https://example.com/gateway/v1/chat/completions", "https://example.com/gateway/v1/models")]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434/v1/models")]
    public void NormalizesModelsEndpoint(string input, string expected)
    {
        Assert.True(AiEndpoint.TryNormalizeModels(input, out Uri endpoint, out string? error), error);
        Assert.Equal(expected, endpoint.ToString());
    }

    [Fact]
    public void RejectsUnsafeModelBaseUrl()
    {
        Assert.False(AiEndpoint.TryNormalizeModels("http://api.example.com/v1", out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("https://user:pass@example.com/v1")]
    [InlineData("https://example.com/v1?key=secret")]
    [InlineData("https://example.com/v1#fragment")]
    [InlineData("")]
    public void RejectsUnsafeBaseUrls(string input)
    {
        Assert.False(AiEndpoint.TryNormalize(input, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://[::1]:8080/v1")]
    public void AllowsPlainHttpOnlyForLoopback(string input)
    {
        Assert.True(AiEndpoint.TryNormalize(input, out Uri endpoint, out string? error), error);
        Assert.Equal(Uri.UriSchemeHttp, endpoint.Scheme);
    }
}
