using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using AzureGameAssistant.Core.Ipc;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Collector.Ipc;

internal readonly record struct CollectorStatus(
    int SamplingIntervalMs,
    bool GameMode,
    bool Paused,
    double UptimeSeconds,
    long SampleCount,
    string? LastError,
    string FrameState,
    int? FrameTargetPid,
    string? FrameError);

internal interface ICollectorControl
{
    string AppVersion { get; }
    int CapabilityFlags { get; }
    CollectorStatus GetStatus();
    bool SetPaused(bool paused);
    bool StartFrames(int pid);
    bool StopFrames();
    void RequestQuit();
}

internal sealed class ControlPipeServer
{
    private readonly FileLogger _logger;
    private readonly ICollectorControl _control;

    public ControlPipeServer(FileLogger logger, ICollectorControl control)
    {
        _logger = logger;
        _control = control;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.Info($"控制管道已启动：{ControlProtocol.PipePath}");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    ControlProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _logger.Warn($"控制管道连接中断：{ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warn($"控制管道访问被拒绝：{ex.Message}");
                break;
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];

        while (!cancellationToken.IsCancellationRequested && server.IsConnected)
        {
            bool hasFrame = await ReadExactAsync(server, lengthBuffer, cancellationToken);
            if (!hasFrame)
            {
                return;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length <= 0 || length > ControlProtocol.MaxFrameBytes)
            {
                return;
            }

            byte[] payload = new byte[length];
            if (!await ReadExactAsync(server, payload, cancellationToken))
            {
                return;
            }

            await HandleRequestAsync(server, payload, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(
        NamedPipeServerStream server,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        int id = 0;
        string command = string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt32(out int parsedId))
            {
                id = parsedId;
            }

            if (root.TryGetProperty("cmd", out JsonElement commandElement))
            {
                command = commandElement.GetString() ?? string.Empty;
            }

            switch (command)
            {
                case "hello":
                    await WriteResponseAsync(server, id, true, WriteHello, null, cancellationToken);
                    break;

                case "status":
                    await WriteResponseAsync(server, id, true, WriteStatus, null, cancellationToken);
                    break;

                case "capabilities":
                    await WriteResponseAsync(server, id, true, WriteCapabilities, null, cancellationToken);
                    break;

                case "pause":
                {
                    _control.SetPaused(true);
                    await WriteResponseAsync(server, id, true, writer => writer.WriteBoolean("paused", true), null, cancellationToken);
                    break;
                }

                case "resume":
                {
                    _control.SetPaused(false);
                    await WriteResponseAsync(server, id, true, writer => writer.WriteBoolean("paused", false), null, cancellationToken);
                    break;
                }

                case "frames.start":
                {
                    int pid = root.TryGetProperty("pid", out JsonElement pidElement) &&
                              pidElement.ValueKind == JsonValueKind.Number &&
                              pidElement.TryGetInt32(out int parsedPid)
                        ? parsedPid
                        : 0;

                    bool started = _control.StartFrames(pid);
                    await WriteResponseAsync(
                        server,
                        id,
                        started,
                        started ? WriteFrames : null,
                        started ? null : "无法启动帧采集：目标无效或 PresentMon 不可用",
                        cancellationToken);
                    break;
                }

                case "frames.stop":
                {
                    _control.StopFrames();
                    await WriteResponseAsync(server, id, true, WriteFrames, null, cancellationToken);
                    break;
                }

                case "quit":
                    await WriteResponseAsync(server, id, true, writer => writer.WriteBoolean("quitting", true), null, cancellationToken);
                    _control.RequestQuit();
                    break;

                default:
                    await WriteResponseAsync(
                        server,
                        id,
                        false,
                        null,
                        $"命令在 M1 中不可用：{command}",
                        cancellationToken);
                    break;
            }
        }
        catch (JsonException)
        {
            await WriteResponseAsync(server, id, false, null, "请求不是有效 JSON", cancellationToken);
        }
        catch (IOException)
        {
        }
    }

    private void WriteHello(Utf8JsonWriter writer)
    {
        writer.WriteNumber("protocolVersion", ControlProtocol.ProtocolVersion);
        writer.WriteString("appVersion", _control.AppVersion);
        writer.WriteNumber("collectorPid", Environment.ProcessId);
        writer.WriteString("mode", "shallow");
        writer.WriteStartObject("capabilities");
        writer.WriteBoolean("frames", (_control.CapabilityFlags & CapabilityFlags.Frames) != 0);
        writer.WriteBoolean("vendorSensors", (_control.CapabilityFlags & CapabilityFlags.VendorSensors) != 0);
        writer.WriteBoolean("processAccess", (_control.CapabilityFlags & CapabilityFlags.ProcessAccess) != 0);
        writer.WriteEndObject();
    }

    private void WriteStatus(Utf8JsonWriter writer)
    {
        CollectorStatus status = _control.GetStatus();
        writer.WriteNumber("samplingIntervalMs", status.SamplingIntervalMs);
        writer.WriteBoolean("gameMode", status.GameMode);
        writer.WriteBoolean("paused", status.Paused);
        writer.WriteNumber("uptimeSeconds", status.UptimeSeconds);
        writer.WriteNumber("sampleCount", status.SampleCount);

        if (status.LastError is null)
        {
            writer.WriteNull("lastError");
        }
        else
        {
            writer.WriteString("lastError", status.LastError);
        }

        writer.WriteStartObject("frames");
        WriteFrames(writer);
        writer.WriteEndObject();
    }

    private void WriteFrames(Utf8JsonWriter writer)
    {
        CollectorStatus status = _control.GetStatus();
        writer.WriteString("state", status.FrameState);
        writer.WriteBoolean("capturing", status.FrameState == "capturing");

        if (status.FrameTargetPid is int pid)
        {
            writer.WriteNumber("targetPid", pid);
        }
        else
        {
            writer.WriteNull("targetPid");
        }

        if (status.FrameError is null)
        {
            writer.WriteNull("error");
        }
        else
        {
            writer.WriteString("error", status.FrameError);
        }
    }

    private void WriteCapabilities(Utf8JsonWriter writer)
    {
        writer.WriteBoolean("frames", (_control.CapabilityFlags & CapabilityFlags.Frames) != 0);
        writer.WriteBoolean("vendorSensors", (_control.CapabilityFlags & CapabilityFlags.VendorSensors) != 0);
        writer.WriteBoolean("processAccess", (_control.CapabilityFlags & CapabilityFlags.ProcessAccess) != 0);
        writer.WriteString("mode", "shallow");
    }

    private static async Task WriteResponseAsync(
        NamedPipeServerStream server,
        int id,
        bool ok,
        Action<Utf8JsonWriter>? writeData,
        string? error,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteBoolean("ok", ok);

            if (writeData is not null)
            {
                writer.WritePropertyName("data");
                writer.WriteStartObject();
                writeData(writer);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("data");
            }

            if (error is null)
            {
                writer.WriteNull("error");
            }
            else
            {
                writer.WriteString("error", error);
            }

            writer.WriteEndObject();
        }

        ReadOnlyMemory<byte> payload = buffer.WrittenMemory;
        byte[] lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);

        await server.WriteAsync(lengthBuffer, cancellationToken);
        await server.WriteAsync(payload, cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
