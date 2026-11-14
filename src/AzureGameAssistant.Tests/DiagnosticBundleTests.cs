using System.IO.Compression;
using AzureGameAssistant.Core.Diagnostics;

namespace AzureGameAssistant.Tests;

public sealed class DiagnosticBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ags-bundle-" + Guid.NewGuid().ToString("N"));

    public DiagnosticBundleTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "diagnostics", "diag-20261115-010101"));
        Directory.CreateDirectory(Path.Combine(_root, "issues"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(@"C:\Users\alice\Saved Games\x.txt", "%USERPROFILE%")]
    [InlineData(@"D:\Games\Client\game.exe", "<path>")]
    [InlineData(@"\\NAS\share\dump.log", "<unc-path>")]
    public void RedactsPaths(string input, string marker)
    {
        string redacted = DiagnosticRedactor.Redact(input);

        Assert.Contains(marker, redacted);
        Assert.DoesNotContain("alice", redacted);
    }

    [Fact]
    public void RedactsSecretsContactsAndAddresses()
    {
        string input = "Authorization: Bearer abcDEF123== key=sk-abcdefgh12345678 mail=user@example.com from=192.168.1.20";

        string redacted = DiagnosticRedactor.Redact(input);

        Assert.Contains("Bearer <redacted>", redacted);
        Assert.Contains("<redacted-key>", redacted);
        Assert.Contains("<email>", redacted);
        Assert.Contains("<ip>", redacted);
        Assert.DoesNotContain("abcdefgh12345678", redacted);
        Assert.DoesNotContain("user@example.com", redacted);
        Assert.DoesNotContain("192.168.1.20", redacted);
    }

    [Fact]
    public void LeavesOrdinaryTextUntouched()
    {
        const string input = "平均 FPS: 60.0 · CPU 42% · 无缺口";

        Assert.Equal(input, DiagnosticRedactor.Redact(input));
    }

    [Fact]
    public void WritesExpectedEntriesAndRedactsContent()
    {
        string diag = Path.Combine(_root, "diagnostics", "diag-20261115-010101");
        File.WriteAllText(Path.Combine(diag, "samples.csv"), "time,cpu\n" + string.Join('\n',
        [
            "1,10",
            @"2,20,C:\Users\alice\game\a.exe",
            "3,30,key=sk-abcdefgh12345678",
            "4,40,user@example.com",
            "5,50,192.168.1.20",
        ]));
        File.WriteAllText(Path.Combine(diag, "summary.txt"), @"summary at C:\Users\alice");
        File.WriteAllText(Path.Combine(_root, "issues", "2026-11.log"), @"issue at C:\Users\alice");
        File.WriteAllText(
            Path.Combine(_root, "settings.json"),
            """{"schemaVersion":4,"ai":{"activeProvider":"ollama","providers":[{"id":"ollama","baseUrl":"http://192.168.1.5:11434/v1","model":"local"}]}}""");

        string output = Path.Combine(_root, "bundle.zip");
        DiagnosticBundleResult result = DiagnosticBundle.Create(new DiagnosticBundleOptions(
            Path.Combine(_root, "diagnostics"),
            Path.Combine(_root, "issues"),
            Path.Combine(_root, "settings.json"),
            output));

        Assert.True(File.Exists(output));
        Assert.True(result.Bytes > 0);
        Assert.True(result.RedactedFiles >= 3);
        Assert.Contains("diagnostics/samples.csv", result.Entries);
        Assert.Contains("settings-summary.json", result.Entries);
        Assert.Contains("hardware.json", result.Entries);
        Assert.Contains("manifest.json", result.Entries);

        using ZipArchive archive = ZipFile.OpenRead(output);
        string samples = ReadEntry(archive, "diagnostics/samples.csv");
        Assert.DoesNotContain("alice", samples);
        Assert.DoesNotContain("sk-abcdefgh12345678", samples);
        Assert.DoesNotContain("user@example.com", samples);
        Assert.DoesNotContain("192.168.1.20", samples);

        string settings = ReadEntry(archive, "settings-summary.json");
        Assert.Contains("\"provider\": \"ollama\"", settings);
        Assert.DoesNotContain("192.168.1.5", settings);
        Assert.DoesNotContain("baseUrl", settings);
        Assert.DoesNotContain("apiKey", settings);
    }

    [Fact]
    public void KeepsOnlyTheNewestSampleLines()
    {
        string diag = Path.Combine(_root, "diagnostics", "diag-20261115-010101");
        File.WriteAllText(
            Path.Combine(diag, "samples.csv"),
            string.Join('\n', Enumerable.Range(1, 10).Select(i => "row" + i)));

        string output = Path.Combine(_root, "bundle.zip");
        DiagnosticBundle.Create(new DiagnosticBundleOptions(
            Path.Combine(_root, "diagnostics"),
            Path.Combine(_root, "issues"),
            Path.Combine(_root, "settings.json"),
            output,
            MaxSampleLines: 3));

        using ZipArchive archive = ZipFile.OpenRead(output);
        string samples = ReadEntry(archive, "diagnostics/samples.csv");
        Assert.Contains("row8", samples);
        Assert.Contains("row9", samples);
        Assert.Contains("row10", samples);
        Assert.DoesNotContain("row1\n", samples.Replace("\r\n", "\n"));
        Assert.DoesNotContain("row5", samples);
    }

    [Fact]
    public void WorksWithoutDiagnosticsOrSettings()
    {
        Directory.Delete(Path.Combine(_root, "diagnostics"), true);
        string output = Path.Combine(_root, "bundle.zip");

        DiagnosticBundleResult result = DiagnosticBundle.Create(new DiagnosticBundleOptions(
            Path.Combine(_root, "diagnostics"),
            Path.Combine(_root, "issues"),
            Path.Combine(_root, "settings.json"),
            output));

        Assert.True(File.Exists(output));
        Assert.Contains("hardware.json", result.Entries);
        using ZipArchive archive = ZipFile.OpenRead(output);
        string settings = ReadEntry(archive, "settings-summary.json");
        Assert.Contains("\"available\": false", settings);
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException("missing " + name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
