using System.Security.Cryptography;

namespace AzureGameAssistant.Core.Frames;

public static class PresentMonBinary
{
    public const string ExpectedSha256 = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF";

    public static bool Verify(string path, out string actualHash)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actualHash, ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            actualHash = string.Empty;
            return false;
        }
    }
}
