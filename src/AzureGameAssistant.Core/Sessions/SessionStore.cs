using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Sessions;

/// <summary>
/// Owns the on-disk session format: atomic gzip JSONL writes, streaming reads,
/// retention and cleanup of files left behind by a crash. Only the UI process
/// touches this type.
/// </summary>
public static class SessionStore
{
    private const long Mb = 1024L * 1024L;
    private const int MaxSamplesOnRead = 100_000;
    private const int MaxFramesOnRead = 500_000;

    public static string DirectoryPath => AppPaths.SessionsDirectory;

    public static string BuildFileName(DateTime eventStartUtc, long eventId) =>
        $"{eventStartUtc.ToLocalTime():yyyyMMdd-HHmmss}-{eventId}{SessionSchema.Extension}";

    public static SessionInfo? Write(
        string directory,
        string fileName,
        SessionHeaderLine header,
        SessionEventLine eventLine,
        IReadOnlyList<SessionSampleLine> samples,
        IReadOnlyList<SessionFrameBatchLine> frameBatches,
        SessionReportLine? report,
        SessionFooterLine footer,
        out string? error)
    {
        error = null;
        string finalPath = Path.Combine(directory, fileName);
        string tempPath = finalPath + ".tmp";

        try
        {
            Directory.CreateDirectory(directory);

            using (var file = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    WriteLine(gzip, hash, header, SessionJsonContext.Default.SessionHeaderLine);
                    WriteLine(gzip, hash, eventLine, SessionJsonContext.Default.SessionEventLine);
                    foreach (SessionSampleLine sample in samples)
                    {
                        WriteLine(gzip, hash, sample, SessionJsonContext.Default.SessionSampleLine);
                    }

                    foreach (SessionFrameBatchLine batch in frameBatches)
                    {
                        WriteLine(gzip, hash, batch, SessionJsonContext.Default.SessionFrameBatchLine);
                    }

                    if (report is not null)
                    {
                        WriteLine(gzip, hash, report, SessionJsonContext.Default.SessionReportLine);
                    }

                    footer.Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    WriteLine(gzip, hash: null, footer, SessionJsonContext.Default.SessionFooterLine);
                    gzip.Flush();
                }

                // FlushFileBuffers before the atomic rename so a crash can never
                // leave a truncated file under the final name.
                file.Flush(true);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            var info = new FileInfo(finalPath);
            var start = new DateTime(eventLine.StartUtc, DateTimeKind.Utc);
            var end = new DateTime(eventLine.EndUtc, DateTimeKind.Utc);
            return new SessionInfo(
                finalPath,
                fileName,
                start,
                start,
                end,
                footer.Complete,
                eventLine.Id,
                eventLine.Score,
                eventLine.Cause,
                eventLine.ProcessName,
                header.SamplingMs,
                header.Mode,
                info.Exists ? info.Length : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or JsonException or ArgumentException or InvalidOperationException)
        {
            error = ex.GetType().Name;
            TryDeleteFile(tempPath);
            return null;
        }
    }

    private static void WriteLine<T>(
        Stream stream,
        IncrementalHash? hash,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        stream.Write(payload);
        stream.WriteByte((byte)'\n');
        hash?.AppendData(payload);
        if (hash is not null)
        {
            hash.AppendData("\n"u8);
        }
    }

    public static List<SessionInfo> Enumerate(string directory)
    {
        var result = new List<SessionInfo>();
        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*" + SessionSchema.Extension))
        {
            SessionHeaderLine? header = ReadHeader(path);
            if (header?.Event is null)
            {
                continue;
            }

            var start = new DateTime(header.Event.StartUtc, DateTimeKind.Utc);
            var end = new DateTime(header.Event.EndUtc, DateTimeKind.Utc);
            var file = new FileInfo(path);
            result.Add(new SessionInfo(
                path,
                file.Name,
                start,
                start,
                end,
                header.State == SessionSchema.CompleteState,
                header.Event.Id,
                header.Event.Score,
                header.Event.Cause,
                header.Event.ProcessName,
                header.SamplingMs,
                header.Mode,
                file.Exists ? file.Length : 0));
        }

        return result
            .OrderByDescending(info => info.StartUtc)
            .ThenByDescending(info => info.EventId)
            .ToList();
    }

    public static SessionHeaderLine? ReadHeader(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            string? line = reader.ReadLine();
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize(line, SessionJsonContext.Default.SessionHeaderLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    public static SessionDocument Read(string path)
    {
        SessionHeaderLine? header = null;
        SessionEventLine? eventLine = null;
        SessionReportLine? report = null;
        SessionFooterLine? footer = null;
        var samples = new List<SampleRecord>(512);
        var frames = new List<FrameTimePoint>(8192);
        var names = new Dictionary<uint, string>();

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            switch (type)
            {
                case "header":
                    header = root.Deserialize(SessionJsonContext.Default.SessionHeaderLine);
                    break;

                case "event":
                    eventLine = root.Deserialize(SessionJsonContext.Default.SessionEventLine);
                    break;

                case "sample" when samples.Count < MaxSamplesOnRead:
                    SessionSampleLine? sample = root.Deserialize(SessionJsonContext.Default.SessionSampleLine);
                    if (sample is not null)
                    {
                        samples.Add(ToSampleRecord(sample, names));
                    }

                    break;

                case "frames":
                    SessionFrameBatchLine? batch = root.Deserialize(SessionJsonContext.Default.SessionFrameBatchLine);
                    if (batch is not null)
                    {
                        AppendFrames(batch, frames);
                    }

                    break;

                case "report":
                    report = root.Deserialize(SessionJsonContext.Default.SessionReportLine);
                    break;

                case "footer":
                    footer = root.Deserialize(SessionJsonContext.Default.SessionFooterLine);
                    break;
            }
        }

        if (header is null || eventLine is null)
        {
            throw new InvalidDataException("会话文件缺少头部或事件行。");
        }

        return new SessionDocument
        {
            Header = header,
            Event = eventLine,
            Samples = samples.ToArray(),
            Frames = frames.ToArray(),
            Report = report,
            Footer = footer,
            Names = names,
        };
    }

    private static void AppendFrames(SessionFrameBatchLine batch, List<FrameTimePoint> frames)
    {
        int count = Math.Min(batch.N, Math.Min(batch.U.Length, batch.Ft.Length));
        for (int i = 0; i < count && frames.Count < MaxFramesOnRead; i++)
        {
            float cpuWait = i < batch.Cw.Length ? (float)Finite(batch.Cw[i]) : 0;
            float gpuWait = i < batch.Gw.Length ? (float)Finite(batch.Gw[i]) : 0;
            float display = i < batch.Dl.Length ? (float)Finite(batch.Dl[i]) : 0;
            bool spike = i < batch.Sp.Length && batch.Sp[i] != 0;
            frames.Add(new FrameTimePoint(batch.U[i], (float)Finite(batch.Ft[i]), spike, cpuWait, gpuWait, display));
        }
    }

    private static SampleRecord ToSampleRecord(SessionSampleLine line, Dictionary<uint, string> names)
    {
        var record = new SampleRecord
        {
            QpcTicks = line.Qpc,
            UtcTicks = line.Utc,
            CpuUtilityPct = (float)Finite(line.Cpu),
            CpuFrequencyPct = (float)Finite(line.Freq),
            MemAvailableMb = ToUInt(line.MemAvailMb),
            MemCommitMb = ToUInt(line.CommitMb),
            MemCommitLimitMb = ToUInt(line.CommitLimitMb),
            PageFilePct = (float)Finite(line.PageFilePct),
            DiskBusyPct = (float)Finite(line.DiskBusy),
            DiskLatencyMs = (float)Finite(line.DiskMs),
            GpuUtilPct = (float)Finite(line.Gpu),
            GpuDedicatedMb = ToUInt(line.GpuDedicatedMb),
            GpuSharedMb = ToUInt(line.GpuSharedMb),
            TimerJitterMs = (float)Finite(line.JitterMs),
            ForegroundPid = line.FgPid,
            ProcessorQueueLength = (ushort)Math.Clamp((int)line.QueueLength, 0, ushort.MaxValue),
            Flags = (ushort)line.Flags,
        };

        if (line.Procs is { Length: > 0 })
        {
            ProcessSlot[] slots = [record.P0, record.P1, record.P2, record.P3, record.P4];
            int count = Math.Min(line.Procs.Length, slots.Length);
            for (int i = 0; i < count; i++)
            {
                SessionProcessLine process = line.Procs[i];
                slots[i] = new ProcessSlot
                {
                    Pid = process.Pid,
                    StartTimeLow = process.StartTimeLow,
                    CpuPct = (float)Finite(process.Cpu),
                    GpuPct = (float)Finite(process.Gpu),
                    WorkingSetMb = ToUInt(process.WorkingSetMb),
                    PrivateMb = ToUInt(process.PrivateMb),
                    Flags = process.Foreground ? ProcessSlotFlags.Foreground : (ushort)0,
                };

                if (process.Pid != 0 && !string.IsNullOrEmpty(process.Name))
                {
                    names[process.Pid] = process.Name;
                }
            }

            record.P0 = slots[0];
            record.P1 = slots[1];
            record.P2 = slots[2];
            record.P3 = slots[3];
            record.P4 = slots[4];
        }

        if (line.Vendor is { } vendor)
        {
            record.V0 = new VendorSlot
            {
                TemperatureC = (float)Finite(vendor.TempC),
                HotSpotC = (float)Finite(vendor.HotspotC),
                PowerW = (float)Finite(vendor.PowerW),
                CoreClockMhz = (float)Finite(vendor.CoreMhz),
                MemoryClockMhz = (float)Finite(vendor.MemMhz),
                FanPct = (float)Finite(vendor.FanPct),
                VramUsedMb = (float)Finite(vendor.VramUsedMb),
                VramTotalMb = (float)Finite(vendor.VramTotalMb),
            };
        }

        if (line.CpuSensor is { } cpu)
        {
            record.C0 = new CpuSensorSlot
            {
                TemperatureC = (float)Finite(cpu.TempC),
                PackagePowerW = (float)Finite(cpu.PowerW),
            };
        }

        return record;
    }

    public static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string ai = path + ".ai.json";
            if (File.Exists(ai))
            {
                File.Delete(ai);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes temp files and any session whose header still says "recording":
    /// a crash during a capture must not leave a file that looks valid.
    /// </summary>
    public static int CleanupIncomplete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string temp in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            if (TryDeleteFile(temp))
            {
                removed++;
            }
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*" + SessionSchema.Extension))
        {
            SessionHeaderLine? header = ReadHeader(path);
            if (header is null || header.State != SessionSchema.CompleteState)
            {
                if (Delete(path))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    public static void Prune(string directory, int maxCount, int maxTotalMb)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        int countLimit = Math.Max(1, maxCount);
        long sizeLimit = Math.Max(1, maxTotalMb) * Mb;

        var files = new DirectoryInfo(directory)
            .GetFiles("*" + SessionSchema.Extension)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        long total = files.Sum(file => file.Length);
        for (int i = 0; i < files.Count; i++)
        {
            var ai = new FileInfo(files[i].FullName + ".ai.json");
            if (ai.Exists)
            {
                total += ai.Length;
            }
        }

        // Oldest first: keep the newest files while both limits are respected.
        for (int i = files.Count - 1; i >= 0 && (files.Count > countLimit || total > sizeLimit); i--)
        {
            FileInfo file = files[i];
            var ai = new FileInfo(file.FullName + ".ai.json");
            long bytes = file.Length + (ai.Exists ? ai.Length : 0);
            if (Delete(file.FullName) && ai.Exists)
            {
                TryDeleteFile(ai.FullName);
            }

            if (!File.Exists(file.FullName))
            {
                files.RemoveAt(i);
                total -= bytes;
            }
        }
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static uint ToUInt(double value) =>
        (uint)Math.Clamp(Finite(value), 0, uint.MaxValue);

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }
}
