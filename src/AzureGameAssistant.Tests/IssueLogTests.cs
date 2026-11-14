using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Tests;

public sealed class IssueLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ags-issues-" + Guid.NewGuid().ToString("N"));

    public IssueLogTests()
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
    public void Warn_WritesMonthlyFileWithComponentAndPid()
    {
        IssueLog.Write(_directory, "ui", LogLevel.Warn, "帧采集不可用", null, new DateTime(2026, 11, 10, 1, 2, 3));

        string path = Path.Combine(_directory, "issues-2026-11.log");
        Assert.True(File.Exists(path));
        string text = File.ReadAllText(path);
        Assert.StartsWith("# AzureGameAssistant issue log", text);
        Assert.Contains("2026-11-10 01:02:03.000 [WRN] [ui:", text);
        Assert.Contains(Environment.ProcessId.ToString(), text);
        Assert.Contains("帧采集不可用", text);
    }

    [Fact]
    public void Info_IsNotArchived()
    {
        IssueLog.Write(_directory, "ui", LogLevel.Info, "例行状态", null, new DateTime(2026, 11, 10));

        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void MonthRollover_CreatesSeparateFile()
    {
        IssueLog.Write(_directory, "ui", LogLevel.Warn, "十一月", null, new DateTime(2026, 11, 30, 23, 59, 0));
        IssueLog.Write(_directory, "ui", LogLevel.Error, "十二月", null, new DateTime(2026, 12, 1, 0, 0, 1));

        string november = Path.Combine(_directory, "issues-2026-11.log");
        string december = Path.Combine(_directory, "issues-2026-12.log");
        Assert.True(File.Exists(november));
        Assert.True(File.Exists(december));
        Assert.DoesNotContain("十二月", File.ReadAllText(november));
        Assert.Contains("十一月", File.ReadAllText(november));
        Assert.Contains("十二月", File.ReadAllText(december));
    }

    [Fact]
    public void Header_IsWrittenOncePerMonth()
    {
        DateTime day = new(2026, 11, 10, 8, 0, 0);
        IssueLog.Write(_directory, "ui", LogLevel.Warn, "一", null, day);
        IssueLog.Write(_directory, "collector", LogLevel.Error, "二", null, day.AddMinutes(1));

        string text = File.ReadAllText(Path.Combine(_directory, "issues-2026-11.log"));
        Assert.Single(text.Split('\n'), line => line.StartsWith("# AzureGameAssistant issue log"));
        Assert.Contains("[collector:", text);
    }

    [Fact]
    public void Exception_IsIncluded()
    {
        IssueLog.Write(_directory, "ui", LogLevel.Error, "启动失败", new InvalidOperationException("boom"),
            new DateTime(2026, 11, 10));

        string text = File.ReadAllText(Path.Combine(_directory, "issues-2026-11.log"));
        Assert.Contains("System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void FileLogger_ArchivesWarningsButNotInfo()
    {
        using (var logger = new FileLogger("test", _directory))
        {
            logger.Info("routine");
            logger.Warn("problem");
        }

        string issueDirectory = Path.Combine(_directory, "issues");
        string[] files = Directory.GetFiles(issueDirectory, "issues-*.log");
        string path = Assert.Single(files);
        Assert.Matches(@"issues-\d{4}-\d{2}\.log$", path);

        string text = File.ReadAllText(path);
        Assert.Contains("[WRN] [test:", text);
        Assert.Contains("problem", text);
        Assert.DoesNotContain("routine", text);
    }

    [Fact]
    public void FileLogger_RelocatesRunAndIssueLogs()
    {
        string movedLogs = Path.Combine(_directory, "moved");
        string movedIssues = Path.Combine(_directory, "moved-issues");

        using (var logger = new FileLogger("test", _directory))
        {
            logger.Info("before");
            logger.Relocate(movedLogs, movedIssues);
            logger.Info("after");
            logger.Warn("problem");
        }

        Assert.True(Directory.Exists(movedLogs));
        string runLog = Assert.Single(Directory.GetFiles(movedLogs, "test-*.log"));
        Assert.Contains("after", File.ReadAllText(runLog));
        Assert.DoesNotContain("before", File.ReadAllText(runLog));

        string issueLog = Assert.Single(Directory.GetFiles(movedIssues, "issues-*.log"));
        Assert.Contains("problem", File.ReadAllText(issueLog));
    }
}
