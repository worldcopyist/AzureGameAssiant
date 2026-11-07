using AzureGameAssistant.Ai;

namespace AzureGameAssistant.Tests;

public sealed class AiRedactorTests
{
    [Fact]
    public void ReplacesPathsAddressesAndIdentities()
    {
        string json = @"{""note"":""C:\Users\tester\Games\save.dat"",
            ""unc"":""\\server\share\x"",""ip"":""192.168.1.20"",
            ""mac"":""AA:BB:CC:DD:EE:FF"",""mail"":""player@example.com"",
            ""user"":""" + Environment.UserName + @""",
            ""host"":""" + Environment.MachineName + @"""}";

        RedactionResult result = Redactor.Apply(json, enabled: true);

        Assert.DoesNotContain(@"C:\Users\tester", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\server\share", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.20", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("player@example.com", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, result.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, result.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted-path]", result.Json, StringComparison.Ordinal);
        Assert.Contains("[redacted-ip]", result.Json, StringComparison.Ordinal);
        Assert.Contains("path", result.Categories);
        Assert.True(result.Hits >= 6);
    }

    [Fact]
    public void DisabledLeavesPayloadUntouched()
    {
        const string json = """{"note":"C:\\Games\\x"}""";

        RedactionResult result = Redactor.Apply(json, enabled: false);

        Assert.Equal(json, result.Json);
        Assert.Equal(0, result.Hits);
        Assert.Empty(result.Categories);
    }

    [Fact]
    public void SanitizeErrorRemovesCredentialShapes()
    {
        string sanitized = Redactor.SanitizeError(
            "Authorization: Bearer abcdef123456 apiKey=sk-1234567890 {\"api_key\":\"secret\"}");

        Assert.DoesNotContain("abcdef123456", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1234567890", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", sanitized, StringComparison.Ordinal);
    }
}
