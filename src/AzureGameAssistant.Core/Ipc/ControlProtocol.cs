namespace AzureGameAssistant.Core.Ipc;

/// <summary>
/// Constants shared by the collector's control pipe server and the UI client.
/// </summary>
public static class ControlProtocol
{
    public const string PipeName = "AzureGameAssistant.Control.v2";
    public const string PipePath = @"\\.\pipe\" + PipeName;
    public const int ProtocolVersion = 2;
    public const int MaxFrameBytes = 1024 * 1024;
}
