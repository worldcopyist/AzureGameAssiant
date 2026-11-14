using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Tests;

public sealed class LogPathsTests
{
    [Fact]
    public void Empty_UsesDefaultDirectories()
    {
        Assert.Equal(AppPaths.LogsDirectory, LogPaths.ResolveDirectory(null));
        Assert.Equal(AppPaths.LogsDirectory, LogPaths.ResolveDirectory("  "));
        Assert.Equal(AppPaths.IssueLogsDirectory, LogPaths.ResolveIssueDirectory(string.Empty, string.Empty));
    }

    [Fact]
    public void CustomLogs_MoveDefaultIssueDirectoryWithThem()
    {
        string logs = Path.Combine(Path.GetTempPath(), "ags-logs-custom");

        Assert.Equal(
            Path.Combine(logs, "issues"),
            LogPaths.ResolveIssueDirectory(string.Empty, logs));
    }

    [Fact]
    public void CustomIssueDirectory_Wins()
    {
        string issues = Path.Combine(Path.GetTempPath(), "ags-issues-custom");

        Assert.Equal(issues, LogPaths.ResolveIssueDirectory(issues, @"D:\logs"));
    }

    [Fact]
    public void EnvironmentVariable_IsExpanded()
    {
        string expected = Path.Combine(Path.GetTempPath(), "ags-env-logs");
        string input = Path.Combine("%TEMP%", "ags-env-logs");

        Assert.Equal(expected, LogPaths.ResolveDirectory(input));
    }

    [Fact]
    public void RelativePath_IsRejected()
    {
        Assert.False(LogPaths.TryNormalize(@"logs\custom", out _));
        Assert.False(LogPaths.TryNormalize("logs", out _));
        Assert.Equal(AppPaths.LogsDirectory, LogPaths.ResolveDirectory(@"logs\custom"));
    }

    [Fact]
    public void InvalidCharacters_AreRejected()
    {
        Assert.False(LogPaths.TryNormalize("C:\\bad\0path", out _));
    }

    [Fact]
    public void TrailingSeparator_IsTrimmedButRootIsKept()
    {
        string path = Path.Combine(Path.GetTempPath(), "ags-trailing");

        Assert.Equal(path, LogPaths.ResolveDirectory(path + Path.DirectorySeparatorChar));

        string root = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.True(LogPaths.TryNormalize(root, out string normalized));
        Assert.Equal(root, normalized);
    }
}
