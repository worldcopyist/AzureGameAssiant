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
    string? LastError);

public sealed class CollectorClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

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

    private async Task<T?> SendAsync<T>(
        string command,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken)
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

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            byte[] payload = BuildRequest(id, command);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(true);
            await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(true);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(true);

            await ReadExactAsync(pipe, header, timeout.Token).ConfigureAwait(true);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > ControlProtocol.MaxFrameBytes)
            {
                ReleasePipe();
                return default;
            }

            byte[] response = new byte[length];
            await ReadExactAsync(pipe, response, timeout.Token).ConfigureAwait(true);

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

    private static byte[] BuildRequest(int id, string command)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("cmd", command);
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

        return new CollectorStatusSnapshot(
            interval.GetInt32(),
            data.TryGetProperty("gameMode", out JsonElement game) && game.GetBoolean(),
            data.TryGetProperty("paused", out JsonElement paused) && paused.GetBoolean(),
            data.TryGetProperty("uptimeSeconds", out JsonElement uptime) ? uptime.GetDouble() : 0,
            data.TryGetProperty("sampleCount", out JsonElement samples) ? samples.GetInt64() : 0,
            data.TryGetProperty("lastError", out JsonElement error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null);
    }

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
