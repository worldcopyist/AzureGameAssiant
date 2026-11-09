using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Tests;

public sealed class LogMaintenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ags-logs-" + Guid.NewGuid().ToString("N"));

    public LogMaintenanceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RemovesOnlyExpiredLogFiles()
    {
        DateTime now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        string expired = Write("old.log", now.AddDays(-5));
        string recent = Write("new.log", now.AddDays(-1));
        string foreign = Write("keep.txt", now.AddDays(-30));

        int removed = LogMaintenance.Prune(_directory, 3, now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void MissingDirectoryIsIgnored()
    {
        Assert.Equal(0, LogMaintenance.Prune(Path.Combine(_directory, "missing"), 3));
    }

    private string Write(string name, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
