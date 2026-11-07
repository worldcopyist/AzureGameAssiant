using AzureGameAssistant.Core.Frames;

namespace AzureGameAssistant.Tests;

public sealed class PresentMonBinaryTests
{
    private const string AbcSha256 = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

    [Fact]
    public void Verify_ComputesHashAndRejectsUnknownBinary()
    {
        string path = Path.Combine(Path.GetTempPath(), "aga-presentmon-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(path, "abc");
        try
        {
            Assert.False(PresentMonBinary.Verify(path, out string actual));
            Assert.Equal(AbcSha256, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Verify_MissingFile_ReturnsFalse()
    {
        string path = Path.Combine(Path.GetTempPath(), "aga-missing-" + Guid.NewGuid().ToString("N") + ".exe");

        Assert.False(PresentMonBinary.Verify(path, out string actual));
        Assert.Equal(string.Empty, actual);
    }
}
