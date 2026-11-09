using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using AzureGameAssistant.Core.Ipc;
using System.IO;

namespace AzureGameAssistant.UI.Services;

public sealed record CollectorHello(
    string AppVersion,
    int CollectorPid,
    bool Frames,
    bool VendorSensors,
    bool ProcessAccess);

public sealed record CollectorStatusSnapshot(
    int SamplingIntervalMs,
    bool GameMode,
    bool Paused,
    double UptimeSeconds,
    long SampleCount,
    string? LastError,
    string FrameState,
    int? FrameTargetPid,
    string? FrameError);

public sealed class CollectorClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    // The collector tears PresentMon down on a background task, but a busy
    // machine can still queue the command behind others; give it more room.
    private static readonly TimeSpan StopFramesTimeout = TimeSpan.FromSeconds(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private int _nextId;

    public bool IsConnected => _pipe?.IsConnected == true;

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return true;
        }

        ReleasePipe();

        var pipe = new NamedPipeClientStream(
            ".",
            ControlProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(true);
            _pipe = pipe;
            return true;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(true);
            return false;
        }
        catch (IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(true);
            return false;
        }
    }

    public Task<CollectorHello?> HelloAsync(CancellationToken cancellationToken) =>
        SendAsync("hello", ParseHello, cancellationToken);

    public Task<CollectorStatusSnapshot?> StatusAsync(CancellationToken cancellationToken) =>
        SendAsync("status", ParseStatus, cancellationToken);

    public async Task<bool> SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        bool? result = await SendAsync(
            paused ? "pause" : "resume",
            data => data.TryGetProperty("paused", out JsonElement value) && value.GetBoolean(),
            cancellationToken).ConfigureAwait(true);

        return result == true;
    }

    public async Task<bool> QuitAsync(CancellationToken cancellationToken)
    {
        bool? result = await SendAsync(
            "quit",
            data => data.TryGetProperty("quitting", out JsonElement value) && value.GetBoolean(),
            cancellationToken).ConfigureAwait(true);

        return result == true;
    }

    public async Task<bool> SetSamplingIntervalAsync(
        int intervalMs,
        bool gameMode,
        CancellationToken cancellationToken)
    {
        int? applied = await SendAsync(
            "sampling",
            data => data.TryGetProperty("samplingIntervalMs", out JsonElement interval) &&
                    interval.TryGetInt32(out int value)
                ? value
                : 0,
            cancellationToken,
            writer =>
            {
                writer.WriteNumber("intervalMs", intervalMs);
                writer.WriteBoolean("gameMode", gameMode);
            }).ConfigureAwait(true);

        return applied is int appliedMs && Math.Abs(appliedMs - intervalMs) <= 1;
    }

    public Task<string?> StartFramesAsync(int pid, CancellationToken cancellationToken) =>
        SendAsync(
            "frames.start",
            ParseFrameState,
            cancellationToken,
            writer => writer.WriteNumber("pid", pid));

    public Task<string?> StopFramesAsync(CancellationToken cancellationToken) =>
        SendAsync("frames.stop", ParseFrameState, cancellationToken, timeout: StopFramesTimeout);

    private async Task<T?> SendAsync<T>(
        string command,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken,
        Action<Utf8JsonWriter>? writeBody = null,
        TimeSpan? timeout = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!await TryConnectAsync(cancellationToken).ConfigureAwait(true))
            {
                return default;
            }

            NamedPipeClientStream pipe = _pipe!;
            int id = Interlocked.Increment(ref _nextId);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout ?? RequestTimeout);

            byte[] payload = BuildRequest(id, command, writeBody);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

            await pipe.WriteAsync(header, linked.Token).ConfigureAwait(true);
            await pipe.WriteAsync(payload, linked.Token).ConfigureAwait(true);
            await pipe.FlushAsync(linked.Token).ConfigureAwait(true);

            await ReadExactAsync(pipe, header, linked.Token).ConfigureAwait(true);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > ControlProtocol.MaxFrameBytes)
            {
                ReleasePipe();
                return default;
            }

            byte[] response = new byte[length];
            await ReadExactAsync(pipe, response, linked.Token).ConfigureAwait(true);

            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;

            bool ok = root.TryGetProperty("ok", out JsonElement okElement) && okElement.GetBoolean();
            if (!ok || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return parse(data);
        }
        catch (OperationCanceledException)
        {
            ReleasePipe();
            return default;
        }
        catch (IOException)
        {
            ReleasePipe();
            return default;
        }
        catch (JsonException)
        {
            ReleasePipe();
            return default;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] BuildRequest(int id, string command, Action<Utf8JsonWriter>? writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("cmd", command);
            writeBody?.Invoke(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static CollectorHello? ParseHello(JsonElement data)
    {
        if (!data.TryGetProperty("appVersion", out JsonElement version) ||
            !data.TryGetProperty("collectorPid", out JsonElement pid))
        {
            return null;
        }

        bool frames = false;
        bool vendor = false;
        bool processAccess = false;

        if (data.TryGetProperty("capabilities", out JsonElement capabilities) &&
            capabilities.ValueKind == JsonValueKind.Object)
        {
            frames = capabilities.TryGetProperty("frames", out JsonElement f) && f.GetBoolean();
            vendor = capabilities.TryGetProperty("vendorSensors", out JsonElement v) && v.GetBoolean();
            processAccess = capabilities.TryGetProperty("processAccess", out JsonElement p) && p.GetBoolean();
        }

        return new CollectorHello(version.GetString() ?? string.Empty, pid.GetInt32(), frames, vendor, processAccess);
    }

    private static CollectorStatusSnapshot? ParseStatus(JsonElement data)
    {
        if (!data.TryGetProperty("samplingIntervalMs", out JsonElement interval))
        {
            return null;
        }

        string frameState = "stopped";
        int? frameTargetPid = null;
        string? frameError = null;

        if (data.TryGetProperty("frames", out JsonElement frames) && frames.ValueKind == JsonValueKind.Object)
        {
            if (frames.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String)
            {
                frameState = state.GetString() ?? "stopped";
            }

            if (frames.TryGetProperty("targetPid", out JsonElement target) &&
                target.ValueKind == JsonValueKind.Number &&
                target.TryGetInt32(out int targetPid))
            {
                frameTargetPid = targetPid;
            }

            if (frames.TryGetProperty("error", out JsonElement frameErr) && frameErr.ValueKind == JsonValueKind.String)
            {
                frameError = frameErr.GetString();
            }
        }

        return new CollectorStatusSnapshot(
            interval.GetInt32(),
            data.TryGetProperty("gameMode", out JsonElement game) && game.GetBoolean(),
            data.TryGetProperty("paused", out JsonElement paused) && paused.GetBoolean(),
            data.TryGetProperty("uptimeSeconds", out JsonElement uptime) ? uptime.GetDouble() : 0,
            data.TryGetProperty("sampleCount", out JsonElement samples) ? samples.GetInt64() : 0,
            data.TryGetProperty("lastError", out JsonElement error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null,
            frameState,
            frameTargetPid,
            frameError);
    }

    private static string? ParseFrameState(JsonElement data) =>
        data.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String
            ? state.GetString()
            : null;

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(true);
            if (read == 0)
            {
                throw new IOException("控制管道已关闭");
            }

            offset += read;
        }
    }

    private void ReleasePipe()
    {
        NamedPipeClientStream? pipe = _pipe;
        _pipe = null;

        if (pipe is null)
        {
            return;
        }

        try
        {
            pipe.Dispose();
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        ReleasePipe();
        _gate.Dispose();
    }
}
