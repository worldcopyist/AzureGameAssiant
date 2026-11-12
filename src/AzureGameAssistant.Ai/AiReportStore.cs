using System.IO;
using System.Text;
using System.Text.Json;

namespace AzureGameAssistant.Ai;

/// <summary>
/// The AI report for a session lives in a sibling file. Missing, truncated or
/// foreign files only mean "this session has no AI section"; they never break
/// replay or export.
/// </summary>
public static class AiReportStore
{
    public static string PathFor(string sessionPath) => sessionPath + ".ai.json";

    public static AiReport? Read(string sessionPath)
    {
        string path = PathFor(sessionPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), AiReportJsonContext.Default.AiReport);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static bool Write(string sessionPath, AiReport report)
    {
        string path = PathFor(sessionPath);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(report, AiReportJsonContext.Default.AiReport),
                new UTF8Encoding(false));
            File.Move(temp, path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }
}
